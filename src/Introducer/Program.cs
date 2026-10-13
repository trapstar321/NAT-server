using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using LiteNetLib;
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
        private readonly NatPunchModule nat;
        private readonly int timeout;

        public PunchListener(NatPunchModule nat, int timeout)
        {
            this.nat = nat;
            this.timeout = timeout;
        }

        public void OnNatIntroductionRequest(IPEndPoint local, IPEndPoint remote, string token)
        {
            try
            {
                char role;
                string session;
                if (!NatProtocol.TryParseToken(token, out role, out session))
                {
                    Console.WriteLine("Rejected malformed NAT registration/request");
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
                        return;
                    }

                    if (!hosts.ContainsKey(session) && hosts.Count >= 256)
                    {
                        Console.WriteLine("Session capacity reached");
                        return;
                    }

                    hosts[session] = new Host { Local = local, Remote = remote, Seen = DateTime.UtcNow };
                    Console.WriteLine("Host registered " + session + " at " + remote);
                    return;
                }

                Host host;
                if (!hosts.TryGetValue(session, out host) ||
                    DateTime.UtcNow - host.Seen > TimeSpan.FromSeconds(timeout))
                {
                    Console.WriteLine("No live host yet for " + session);
                    return;
                }

                if (host.Remote.Equals(remote))
                {
                    Console.WriteLine("Ignored self-introduction for " + session);
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
                {
                    dead.Add(pair.Key);
                }
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
                    { "timeout", "120" }
                });

                IPAddress bindAddress = IPAddress.Parse(options.Get("bind"));
                int port = options.Int("port", 1, 65535);
                int timeout = options.Int("timeout", 10, 86400);

                var listener = new EventBasedNetListener();
                manager = new NetManager(listener);
                manager.NatPunchEnabled = true;
                var punchListener = new PunchListener(manager.NatPunchModule, timeout);
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
