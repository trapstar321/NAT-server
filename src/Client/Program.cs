using System;
using System.Collections.Generic;
using System.Net;
using NatTest.Common;

namespace NatTest.Client
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            try
            {
                var options = new Options(args, new Dictionary<string, string>
                {
                    { "introducer", "127.0.0.1" },
                    { "port", "50010" },
                    { "token", "" },
                    { "local-port", "50021" },
                    { "connect-timeout", "5" },
                    { "introduce-timeout", "5" },
                    { "introducer-public-key", "keys\\introducer-signing.pub.pem" },
                    { "host-player", "" }
                });

                int introducerPort = options.Int("port", 1, 65535);
                int localPort = options.Int("local-port", 1, 65535);
                int connectTimeout = options.Int("connect-timeout", 1, 600);
                int introduceTimeout = options.Int("introduce-timeout", 1, 600);
                string token = options.Get("token");
                if (String.IsNullOrWhiteSpace(token))
                    throw new ArgumentException("A client token from MatchmakingService is required via --token.");

                // The client has no MM public key; it reads its own token only to learn its session and player ID.
                MatchmakingTokenClaims clientClaims;
                if (!NatTokens.TryReadMatchmakingToken(token, out clientClaims) || clientClaims.Role != NatTokens.ClientRole)
                    throw new ArgumentException("--token must be a client (C) token from POST /join.");

                Guid expectedHostPlayerId = Guid.Empty;
                if (options.Get("host-player").Length > 0 && !Guid.TryParse(options.Get("host-player"), out expectedHostPlayerId))
                    throw new ArgumentException("Invalid --host-player; use the hostPlayerId from the game listing.");

                var introducerKey = SigningKeys.LoadPublicKeyFile(options.Get("introducer-public-key"), "--introducer-public-key");
                IPEndPoint introducer = Options.Resolve(options.Get("introducer"), introducerPort);
                var client = new HostedGameClient(introducer, localPort, connectTimeout, introduceTimeout, token, clientClaims, introducerKey, expectedHostPlayerId);
                
                client.OnErrorOccurred+= (message) =>
                {
                    Console.Error.WriteLine(message);
                };

                client.OnInfo = (message) =>
                {
                    Console.WriteLine(message);
                };

                client.Run();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Client error: " + exception.Message);
                Environment.ExitCode = 1;
            }
        }
    }
}
