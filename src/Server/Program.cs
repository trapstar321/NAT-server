using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using NatTest.Common;

namespace NatTest.Server
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            NetManager manager = null;

            try
            {
                var options = new Options(args, new Dictionary<string, string>
                {
                    { "introducer", "127.0.0.1" },
                    { "port", "50010" },
                    { "session", "demo-room" },
                    { "local-port", "50020" },
                    { "timeout", "30" }
                });

                int introducerPort = options.Int("port", 1, 65535);
                int localPort = options.Int("local-port", 1, 65535);
                int timeout = options.Int("timeout", 5, 600);
                string session = NatProtocol.ValidateSession(options.Get("session"));
                IPEndPoint introducer = Options.Resolve(options.Get("introducer"), introducerPort);

                var listener = new EventBasedNetListener();
                manager = new NetManager(listener);
                manager.NatPunchEnabled = true;
                manager.DisconnectTimeout = timeout * 1000;
                var pending = new HashSet<string>(StringComparer.Ordinal);

                listener.ConnectionRequestEvent += request =>
                {
                    string endpoint = request.RemoteEndPoint.ToString();
                    if (manager.ConnectedPeersCount + pending.Count < 16 || pending.Contains(endpoint))
                    {
                        pending.Add(endpoint);
                        Console.WriteLine("Accepting LiteNetLib protocol request");
                        request.AcceptIfKey(NatProtocol.ConnectionKey);
                    }
                    else
                    {
                        Console.WriteLine("Peer limit reached");
                        request.Reject();
                    }
                };

                listener.PeerConnectedEvent += peer =>
                {
                    pending.Remove(new IPEndPoint(peer.Address, peer.Port).ToString());
                    Console.WriteLine("Peer connected: " + peer.Address + ":" + peer.Port);
                };

                listener.PeerDisconnectedEvent += (peer, info) =>
                {
                    pending.Remove(new IPEndPoint(peer.Address, peer.Port).ToString());
                    Console.WriteLine("Peer disconnected: " + peer.Address + ":" + peer.Port + " " + info.Reason);
                };

                listener.NetworkReceiveEvent += (peer, reader, method, channel) =>
                {
                    try
                    {
                        string message = reader.GetString(2048);
                        Console.WriteLine("Received from " + peer.Address + ":" + peer.Port + ": " + message);

                        if (message == NatProtocol.Hello)
                        {
                            var writer = new NetDataWriter();
                            writer.Put(NatProtocol.Ready);
                            peer.Send(writer, DeliveryMethod.ReliableOrdered);
                        }
                        else if (message.StartsWith(NatProtocol.MessagePrefix, StringComparison.Ordinal))
                        {
                            var writer = new NetDataWriter();
                            writer.Put(NatProtocol.ReplyPrefix + message.Substring(NatProtocol.MessagePrefix.Length));
                            peer.Send(writer, DeliveryMethod.ReliableOrdered);
                        }
                        else
                        {
                            peer.Disconnect();
                        }
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine("Invalid message: " + exception.Message);
                        peer.Disconnect();
                    }
                    finally
                    {
                        reader.Recycle();
                    }
                };

                manager.NatPunchModule.Init(new Punch(manager));
                if (!manager.Start(localPort))
                {
                    throw new InvalidOperationException("Cannot bind local UDP port " + localPort);
                }

                Console.WriteLine("Host UDP port " + localPort + "; registering session " + session + " via " + introducer + ".");
                Console.CancelKeyPress += (sender, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    manager.Stop();
                };

                DateTime next = DateTime.MinValue;
                while (manager.IsRunning)
                {
                    manager.PollEvents();
                    manager.NatPunchModule.PollEvents();

                    if (DateTime.UtcNow >= next)
                    {
                        manager.NatPunchModule.SendNatIntroduceRequest(
                            introducer.Address.ToString(),
                            introducer.Port,
                            NatProtocol.HostToken(session));
                        next = DateTime.UtcNow.AddSeconds(5);
                    }

                    Thread.Sleep(15);
                }

                Console.WriteLine("Server stopped.");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Server error: " + exception.Message);
                Environment.ExitCode = 1;
            }
            finally
            {
                if (manager != null)
                {
                    manager.Stop();
                }
            }
        }

        private sealed class Punch : INatPunchListener
        {
            private readonly NetManager manager;
            private readonly HashSet<string> attempted = new HashSet<string>(StringComparer.Ordinal);

            public Punch(NetManager manager)
            {
                this.manager = manager;
            }

            public void OnNatIntroductionRequest(IPEndPoint local, IPEndPoint remote, string token)
            {
                Console.WriteLine("Received NAT introduction request from " + remote + " token=" + token);                
            }

            public void OnNatIntroductionFailed(IPEndPoint endpoint, string token)
            {
                Console.WriteLine("NAT introduction failed for " + endpoint + " token=" + token);
            }

            public void OnNatIntroductionSuccess(IPEndPoint endpoint, NatAddressType type, string token)
            {
                Console.WriteLine("Introduced endpoint " + endpoint + " (" + type + ") token=" + token);

                if (type == NatAddressType.External && attempted.Add(endpoint.ToString()) && manager.ConnectedPeersCount < 16)
                {
                    manager.Connect(endpoint, NatProtocol.ConnectionKey);
                }
            }            
        }
    }
}
