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
                    { "introduce-timeout", "5" }
                });

                int introducerPort = options.Int("port", 1, 65535);
                int localPort = options.Int("local-port", 1, 65535);
                int connectTimeout = options.Int("connect-timeout", 1, 600);
                int introduceTimeout = options.Int("introduce-timeout", 1, 600);
                string token = options.Get("token");
                if (String.IsNullOrWhiteSpace(token))
                    throw new ArgumentException("A client token from MatchmakingService is required via --token.");

                IPEndPoint introducer = Options.Resolve(options.Get("introducer"), introducerPort);
                var client = new HostedGameClient(introducer, localPort, connectTimeout, introduceTimeout, token);
                
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
