using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace NatTest.Tests.EndToEnd
{
    internal enum PeerRuntime
    {
        NetFramework,
        // Unity's Mono runtime with its unityjit class libraries, i.e. what a Unity standalone (Mono backend) player runs on.
        UnityMono
    }

    internal static class TestEnvironment
    {
        internal static readonly string RepositoryRoot = FindRepositoryRoot();

        // Test output is bin/<Configuration>/net472, and the programs are built with the same configuration.
        internal static readonly string Configuration = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')).Parent.Name;

        internal static readonly string IntroducerExe = ProgramPath("Introducer", "introducer");
        internal static readonly string ServerExe = ProgramPath("Server", "server");
        internal static readonly string ClientExe = ProgramPath("Client", "client");

        // Override with NATTEST_MATCHMAKING_PROJECT; defaults to the sibling MatchmakingService repository.
        internal static readonly string MatchmakingProject = Environment.GetEnvironmentVariable("NATTEST_MATCHMAKING_PROJECT") ??
            Path.GetFullPath(Path.Combine(RepositoryRoot, "..", "MatchmakingService", "MatchmakingService.csproj"));

        // Override with NATTEST_UNITY_MONO (path to mono.exe); defaults to the newest Unity Hub editor install. Null if none.
        internal static readonly string UnityMono = FindUnityMono();

        internal static string UnityMonoProfile
        {
            get { return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(UnityMono), "..", "lib", "mono", "unityjit-win32")); }
        }

        internal static int FreeUdpPort()
        {
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                return ((IPEndPoint)socket.Client.LocalEndPoint).Port;
        }

        internal static int FreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string ProgramPath(string project, string assembly)
        {
            return Path.Combine(RepositoryRoot, "src", project, "bin", Configuration, "net472", assembly + ".exe");
        }

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "NatTest.sln"))) return directory.FullName;
            throw new InvalidOperationException("Cannot find NatTest.sln above " + AppDomain.CurrentDomain.BaseDirectory);
        }

        private static string FindUnityMono()
        {
            string configured = Environment.GetEnvironmentVariable("NATTEST_UNITY_MONO");
            if (!String.IsNullOrEmpty(configured)) return File.Exists(configured) ? configured : null;

            string editors = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor");
            if (!Directory.Exists(editors)) return null;
            return Directory.GetDirectories(editors)
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Select(path => Path.Combine(path, "Editor", "Data", "MonoBleedingEdge", "bin", "mono.exe"))
                .FirstOrDefault(File.Exists);
        }
    }

    // Runs only when a Unity editor's Mono runtime is available.
    internal sealed class UnityMonoFactAttribute : FactAttribute
    {
        public UnityMonoFactAttribute()
        {
            if (TestEnvironment.UnityMono == null) Skip = "Unity's Mono runtime was not found; set NATTEST_UNITY_MONO to its mono.exe.";
        }
    }
}
