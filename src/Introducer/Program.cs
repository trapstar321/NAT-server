using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using NatTest.Common;

namespace NatTest.Introducer
{
    internal sealed class PunchListener : INatPunchListener
    {
        private sealed class Host
        {
            public IPEndPoint Local;
            public IPEndPoint Remote;
            public DateTime Seen;
        }

        private readonly Dictionary<string, Host> hosts = new Dictionary<string, Host>(StringComparer.Ordinal);
        private readonly NetManager manager;
        private readonly NatPunchModule nat;
        private readonly string signingKey;
        private readonly int timeout;

        public PunchListener(NetManager manager, string signingKey, int timeout)
        {
            this.manager = manager;
            this.nat = manager.NatPunchModule;
            this.signingKey = signingKey;
            this.timeout = timeout;
        }

        public void OnNatIntroductionRequest(IPEndPoint local, IPEndPoint remote, string token)
        {
            try
            {
                char role;
                string session;
                string tokenError;

                if (!TokenUtils.TryParseToken(token, signingKey, out role, out session, out tokenError))
                {
                    Console.WriteLine("Rejected NAT registration/request: " + tokenError);
                    SendError(remote, tokenError);
                    return;
                }

                if (role == 'H')
                {
                    Host previous;
                    if (hosts.TryGetValue(session, out previous) &&
                        DateTime.UtcNow - previous.Seen <= TimeSpan.FromSeconds(timeout) &&
                        !previous.Remote.Equals(remote))
                    {
                        Console.WriteLine("Rejected duplicate host registration for active session " + session);
                        SendError(remote, "duplicate-host-registration");
                        return;
                    }

                    if (!hosts.ContainsKey(session) && hosts.Count >= 256)
                    {
                        Console.WriteLine("Session capacity reached");
                        SendError(remote, "session-capacity-reached");
                        return;
                    }

                    hosts[session] = new Host { Local = local, Remote = remote, Seen = DateTime.UtcNow };
                    SendAcknowledgment(remote);
                    Console.WriteLine("Host registered " + session + " at " + remote);
                    return;
                }

                SendAcknowledgment(remote);

                Host host;
                if (!hosts.TryGetValue(session, out host) ||
                    DateTime.UtcNow - host.Seen > TimeSpan.FromSeconds(timeout))
                {
                    Console.WriteLine("No live host yet for " + session);
                    SendError(remote, "no-live-host");
                    return;
                }

                if (host.Remote.Equals(remote))
                {
                    Console.WriteLine("Ignored self-introduction for " + session);
                    SendError(remote, "self-introduction");
                    return;
                }

                nat.NatIntroduce(host.Local, host.Remote, local, remote, session);
                
                Console.WriteLine(
                    "NatIntroduce called:\n" +
                    "  Session:      " + session + "\n" +
                    "  Host Local:   " + host.Local + "\n" +
                    "  Host Remote:  " + host.Remote + "\n" +
                    "  Client Local: " + local + "\n" +
                    "  Client Remote:" + remote
                );
            }
            catch (Exception exception)
            {
                Console.WriteLine("Malformed request: " + exception.Message);
            }
        }

        private void SendAcknowledgment(IPEndPoint remote)
        {
            var acknowledgment = new NetDataWriter();
            acknowledgment.Put("NATTEST/1|INTRODUCER_ACK");
            if (!manager.SendUnconnectedMessage(acknowledgment, remote))
                Console.WriteLine("Failed to acknowledge NAT request from " + remote);
        }

        private void SendError(IPEndPoint remote, string reason)
        {
            var error = new NetDataWriter();
            error.Put("NATTEST/1|INTRODUCER_ERR|" + reason);
            if (!manager.SendUnconnectedMessage(error, remote))
                Console.WriteLine("Failed to send token validation error to " + remote);
        }

        public void OnNatIntroductionSuccess(IPEndPoint endpoint, NatAddressType type, string token)
        {
            Console.WriteLine("NAT introduction succeeded for " + endpoint + " (" + type + ") token=" + token);
        }

        public void OnNatIntroductionFailed(IPEndPoint endpoint, string token)
        {
            Console.WriteLine("NAT introduction failed for " + endpoint + " token=" + token);
        }

        public void Expire()
        {
            var dead = new List<string>();
            foreach (var pair in hosts)
            {
                if (DateTime.UtcNow - pair.Value.Seen > TimeSpan.FromSeconds(timeout))
                    dead.Add(pair.Key);
            }

            foreach (string session in dead)
            {
                hosts.Remove(session);
                Console.WriteLine("Expired " + session);
            }
        }

    }

    internal static class Program
    {
        private static void Main(string[] args)
        {
            NetManager manager = null;

            try
            {
                var options = new Options(args, new Dictionary<string, string>
                {
                    { "bind", "0.0.0.0" },
                    { "port", "50010" },
                    { "timeout", "30" },
                    { "token-key", Environment.GetEnvironmentVariable("MATCHMAKING__TOKENSIGNINGKEY") ?? "" }
                });

                IPAddress bindAddress = IPAddress.Parse(options.Get("bind"));
                int port = options.Int("port", 1, 65535);
                int timeout = options.Int("timeout", 10, 86400);
                string signingKey = options.Get("token-key");
                if (String.IsNullOrWhiteSpace(signingKey) || System.Text.Encoding.UTF8.GetByteCount(signingKey) < 32)
                    throw new ArgumentException("A token signing key of at least 32 UTF-8 bytes is required via --token-key or MATCHMAKING__TOKENSIGNINGKEY.");

                var listener = new EventBasedNetListener();
                manager = new NetManager(listener);
                manager.NatPunchEnabled = true;
                manager.UnconnectedMessagesEnabled = true;
                var punchListener = new PunchListener(manager, signingKey, timeout);
                manager.NatPunchModule.Init(punchListener);

                if (!manager.Start(bindAddress, IPAddress.IPv6Any, port))
                {
                    throw new InvalidOperationException("UDP bind failed: " + bindAddress + ":" + port);
                }

                Console.WriteLine("Listening on " + bindAddress + ":" + port + " UDP. Ctrl+C exits.");
                Console.CancelKeyPress += (sender, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    manager.Stop();
                };

                while (manager.IsRunning)
                {
                    manager.PollEvents();
                    manager.NatPunchModule.PollEvents();
                    punchListener.Expire();
                    Thread.Sleep(15);
                }

                Console.WriteLine("Introducer stopped.");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Introducer error: " + exception.Message);
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
    }
}
