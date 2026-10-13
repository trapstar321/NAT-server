using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using NatTest.Common;

namespace NatTest.Client
{
    internal static class Program
    {
        private static volatile bool connected, ready, stop, introduced, probeSent;

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
                    { "local-port", "50021" },
                    { "timeout", "30" },
                    { "introduction-timeout", "60" }
                });

                int introducerPort = options.Int("port", 1, 65535);
                int localPort = options.Int("local-port", 1, 65535);
                int timeout = options.Int("timeout", 5, 600);
                int introductionTimeout = options.Int("introduction-timeout", 5, 600);
                string session = NatProtocol.ValidateSession(options.Get("session"));
                IPEndPoint introducer = Options.Resolve(options.Get("introducer"), introducerPort);

                var listener = new EventBasedNetListener();
                manager = new NetManager(listener);
                manager.NatPunchEnabled = true;
                manager.DisconnectTimeout = timeout * 1000;

                listener.ConnectionRequestEvent += request =>
                {
                    if (manager.ConnectedPeersCount < 4)
                    {
                        Console.WriteLine("Accepting LiteNetLib peer request");
                        request.AcceptIfKey();
                    }
                    else
                    {
                        request.Reject();
                    }
                };

                listener.PeerConnectedEvent += peer =>
                {
                    Console.WriteLine("Direct peer connected: " + peer.Address + ":" + peer.Port);
                    connected = true;
                    var writer = new NetDataWriter();
                    writer.Put(NatProtocol.Hello);
                    peer.Send(writer, DeliveryMethod.ReliableOrdered);
                };

                listener.PeerDisconnectedEvent += (peer, info) =>
                {
                    Console.WriteLine("Disconnected: " + info.Reason);
                    connected = false;
                    ready = false;
                };

                listener.NetworkReceiveEvent += (peer, reader, method, channel) =>
                {
                    try
                    {
                        string message = reader.GetString(2048);
                        Console.WriteLine("Received: " + message);

                        if (message == NatProtocol.Ready)
                        {
                            ready = true;
                            Console.WriteLine("Application handshake confirmed; direct path works.");
                        }
                        else if (message.StartsWith(NatProtocol.ReplyPrefix, StringComparison.Ordinal))
                        {
                            Console.WriteLine("Round trip confirmed: " + message.Substring(NatProtocol.ReplyPrefix.Length));
                            ready = true;
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

                Console.WriteLine("Client UDP port " + localPort + "; requesting session " + session + " via " + introducer + ".");
                Console.CancelKeyPress += (sender, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    stop = true;
                    manager.Stop();
                };

                DateTime next = DateTime.MinValue;
                DateTime deadline = DateTime.UtcNow.AddSeconds(introductionTimeout);

                while (manager.IsRunning && !stop)
                {
                    manager.PollEvents();
                    manager.NatPunchModule.PollEvents();

                    if (!introduced && !connected && DateTime.UtcNow >= next)
                    {
                        manager.NatPunchModule.SendNatIntroduceRequest(
                            introducer.Address.ToString(),
                            introducer.Port,
                            NatProtocol.ClientToken(session));
                        next = DateTime.UtcNow.AddSeconds(3);
                    }

                    if (!introduced && !connected && DateTime.UtcNow > deadline)
                    {
                        Console.WriteLine("Introduction timed out; check session, DNS and introducer UDP reachability.");
                        break;
                    }

                    if (introduced && !connected && DateTime.UtcNow > deadline.AddSeconds(timeout))
                    {
                        Console.WriteLine("Introduction succeeded, but direct peer connection timed out; check peer firewall and NAT behavior.");
                        break;
                    }

                    if (ready && !probeSent)
                    {
                        probeSent = true;
                        var writer = new NetDataWriter();
                        writer.Put(NatProtocol.MessagePrefix + "ping");
                        manager.FirstPeer.Send(writer, DeliveryMethod.ReliableOrdered);
                    }

                    if (ready && !Console.IsInputRedirected && Console.KeyAvailable)
                    {
                        string line = Console.ReadLine();
                        if (String.Equals(line, "/quit", StringComparison.OrdinalIgnoreCase))
                        {
                            stop = true;
                            break;
                        }

                        if (connected)
                        {
                            var writer = new NetDataWriter();
                            writer.Put(NatProtocol.MessagePrefix + line);
                            manager.FirstPeer.Send(writer, DeliveryMethod.ReliableOrdered);
                        }
                    }
                    else
                    {
                        Thread.Sleep(15);
                    }
                }

                Console.WriteLine("Client stopped.");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Client error: " + exception.Message);
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
            private bool attempted;

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
                introduced = true;
                Console.WriteLine("Introduction success: " + endpoint + " (" + type + ")");

                if (type == NatAddressType.External && !attempted && manager.ConnectedPeersCount == 0)
                {
                    attempted = true;
                    manager.Connect(endpoint, NatProtocol.ConnectionKey);
                }
            }
        }
    }
}
