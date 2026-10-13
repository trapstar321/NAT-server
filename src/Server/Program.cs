using System;
using System.Collections.Generic;
using System.Net;
using NatTest.Common;

namespace NatTest.Server
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
                    { "local-port", "50020" },
                    { "timeout", "30" },
                    { "introduce-timeout", "5" }
                });

                int introducerPort = options.Int("port", 1, 65535);
                int localPort = options.Int("local-port", 1, 65535);
                int timeout = options.Int("timeout", 5, 600);
                int introduceTimeout = options.Int("introduce-timeout", 1, 600);
                string token = options.Get("token");
                if (String.IsNullOrWhiteSpace(token))
                    throw new ArgumentException("A host token from MatchmakingService is required via --token.");

                IPEndPoint introducer = Options.Resolve(options.Get("introducer"), introducerPort);
                var server = new HostedGameServer(introducer, localPort, timeout, introduceTimeout, token);
                
                server.OnErrorOccurred += (message) =>
                {
                    Console.Error.WriteLine(message);
                };

                server.OnInfo = (message) =>
                {
                    Console.WriteLine(message);
                };
                
                server.Run();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Server error: " + exception.Message);
                Environment.ExitCode = 1;
            }
        }
    }
}
