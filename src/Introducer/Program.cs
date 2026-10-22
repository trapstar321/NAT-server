using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using NatTest.Common;
using Org.BouncyCastle.Crypto.Parameters;

namespace NatTest.Introducer
{
    internal sealed class PunchListener : INatPunchListener
    {
        private sealed class Host
        {
            public IPEndPoint Local;
            public IPEndPoint Remote;
            public DateTime Seen;
            public Guid PlayerId;
            public long TokenExpires;
        }

        private readonly Dictionary<Guid, Host> hosts = new Dictionary<Guid, Host>();
        private readonly NetManager manager;
        private readonly NatPunchModule nat;
        private readonly ECPublicKeyParameters matchmakingKey;
        private readonly ECPrivateKeyParameters introducerKey;
        private readonly int timeout;
        private readonly int introductionLifetime;

        public PunchListener(NetManager manager, ECPublicKeyParameters matchmakingKey, ECPrivateKeyParameters introducerKey, int timeout, int introductionLifetime)
        {
            this.manager = manager;
            this.nat = manager.NatPunchModule;
            this.matchmakingKey = matchmakingKey;
            this.introducerKey = introducerKey;
            this.timeout = timeout;
            this.introductionLifetime = introductionLifetime;
        }

        // LiteNetLib uses the same request for host registration and client introduction, so the signed role selects the
        // operation: H registers the token's session, C asks to be introduced to the token's session. Neither role can be
        // used for the other operation without invalidating MM's signature.
        public void OnNatIntroductionRequest(IPEndPoint local, IPEndPoint remote, string token)
        {
            try
            {
                long now = NatTokens.NowUnixSeconds();
                MatchmakingTokenClaims claims;
                string tokenError;

                if (!NatTokens.TryVerifyMatchmakingToken(token, matchmakingKey, now, out claims, out tokenError))
                {
                    Console.WriteLine("Rejected NAT registration/request from " + remote + ": " + tokenError);
                    SendError(remote, tokenError);
                    return;
                }

                // owner tokens are only for the player's own game server
                if (claims.Role != NatTokens.HostRole && claims.Role != NatTokens.ClientRole)
                {
                    Console.WriteLine("Rejected NAT registration/request from " + remote + ": owner token");
                    SendError(remote, NatTokens.InvalidToken);
                    return;
                }

                Guid session = claims.SessionId;
                if (claims.Role == NatTokens.HostRole)
                {
                    Host previous;
                    if (hosts.TryGetValue(session, out previous))
                    {
                        if (previous.PlayerId != claims.PlayerId)
                        {
                            Console.WriteLine("Rejected host registration for " + session + " by a different player " + claims.PlayerId);
                            SendError(remote, "host-player-mismatch");
                            return;
                        }

                        if (DateTime.UtcNow - previous.Seen <= TimeSpan.FromSeconds(timeout) && !previous.Remote.Equals(remote))
                        {
                            Console.WriteLine("Rejected duplicate host registration for active session " + session);
                            SendError(remote, "duplicate-host-registration");
                            return;
                        }
                    }

                    if (previous == null && hosts.Count >= 256)
                    {
                        Console.WriteLine("Session capacity reached");
                        SendError(remote, "session-capacity-reached");
                        return;
                    }

                    hosts[session] = new Host { Local = local, Remote = remote, Seen = DateTime.UtcNow, PlayerId = claims.PlayerId, TokenExpires = claims.ExpiresUnixSeconds };
                    SendAcknowledgment(remote);
                    Console.WriteLine("Host registered " + session + " player " + claims.PlayerId + " at " + remote);
                    return;
                }

                SendAcknowledgment(remote);

                Host host;
                if (!hosts.TryGetValue(session, out host) ||
                    DateTime.UtcNow - host.Seen > TimeSpan.FromSeconds(timeout) ||
                    host.TokenExpires <= now)
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

                // Both peers receive this token and relay it to each other in their punch packets, so it carries no secrets.
                string introductionToken = NatTokens.CreateIntroductionToken(introducerKey, session, host.PlayerId, claims.PlayerId, now + introductionLifetime);
                nat.NatIntroduce(host.Local, host.Remote, local, remote, introductionToken);

                Console.WriteLine(
                    "NatIntroduce called:\n" +
                    "  Session:      " + session + "\n" +
                    "  Host player:  " + host.PlayerId + "\n" +
                    "  Client player:" + claims.PlayerId + "\n" +
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
            Console.WriteLine("NAT introduction succeeded for " + endpoint + " (" + type + ")");
        }

        public void OnNatIntroductionFailed(IPEndPoint endpoint, string token)
        {
            Console.WriteLine("NAT introduction failed for " + endpoint);
        }

        public void Expire()
        {
            var dead = new List<Guid>();
            long now = NatTokens.NowUnixSeconds();
            foreach (var pair in hosts)
            {
                if (DateTime.UtcNow - pair.Value.Seen > TimeSpan.FromSeconds(timeout) || pair.Value.TokenExpires <= now)
                    dead.Add(pair.Key);
            }

            foreach (Guid session in dead)
            {
                hosts.Remove(session);
                Console.WriteLine("Expired " + session);
            }
        }

    }

    internal static class Program
    {
        internal const string KeyName = "introducer-signing";

        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "generate-key")
            {
                GenerateKey(args);
                return;
            }

            NetManager manager = null;

            try
            {
                var options = new Options(args, new Dictionary<string, string>
                {
                    { "bind", "0.0.0.0" },
                    { "port", "50010" },
                    { "timeout", "30" },
                    { "introduction-lifetime", "60" },
                    { "mm-public-key", "keys\\mm-nat-signing.pub.pem" },
                    { "key", "keys\\" + KeyName + ".pem" }
                });

                IPAddress bindAddress = IPAddress.Parse(options.Get("bind"));
                int port = options.Int("port", 1, 65535);
                int timeout = options.Int("timeout", 10, 86400);
                int introductionLifetime = options.Int("introduction-lifetime", 5, 600);
                // MM's private key never reaches the introducer: only its public key is needed to verify H and C tokens.
                var matchmakingKey = SigningKeys.LoadPublicKeyFile(options.Get("mm-public-key"), "--mm-public-key");
                var introducerKey = SigningKeys.LoadPrivateKeyFile(options.Get("key"), "--key");

                var listener = new EventBasedNetListener();
                manager = new NetManager(listener);
                manager.NatPunchEnabled = true;
                manager.UnconnectedMessagesEnabled = true;
                var punchListener = new PunchListener(manager, matchmakingKey, introducerKey, timeout, introductionLifetime);
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

        // introducer.exe generate-key <directory>: writes introducer-signing.pem (private, keep on the introducer) and
        // introducer-signing.pub.pem (give to game hosts and clients). Existing keys are never overwritten.
        private static void GenerateKey(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: introducer.exe generate-key <directory>");
                Environment.ExitCode = 2;
                return;
            }

            try
            {
                string privatePath = Path.Combine(args[1], KeyName + ".pem");
                string publicPath = Path.Combine(args[1], KeyName + ".pub.pem");
                if (File.Exists(privatePath) || File.Exists(publicPath))
                    throw new InvalidOperationException("Refusing to overwrite existing key " + privatePath + ". Outstanding introductions depend on it.");

                Directory.CreateDirectory(args[1]);
                var pair = SigningKeys.Generate();
                WriteNew(privatePath, SigningKeys.ToPem(pair.Private));
                WriteNew(publicPath, SigningKeys.ToPem(pair.Public));
                Console.WriteLine("Wrote " + privatePath + " (private, keep secret) and " + publicPath + " (public, give to hosts and clients).");
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is InvalidOperationException)
            {
                Console.Error.WriteLine(exception.Message);
                Environment.ExitCode = 1;
            }
        }

        private static void WriteNew(string path, string contents)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new System.Text.ASCIIEncoding()))
                writer.Write(contents);
        }
    }
}
