using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit.Sdk;

namespace NatTest.Tests.EndToEnd
{
    // A child process whose stdout and stderr lines are collected so tests can wait for log lines.
    internal sealed class PeerProcess : IDisposable
    {
        internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

        private readonly Process process;
        private readonly List<string> lines = new List<string>();
        private readonly string name;

        private PeerProcess(string name, ProcessStartInfo info)
        {
            this.name = name;
            process = new Process { StartInfo = info };
            process.OutputDataReceived += (sender, e) => Add(e.Data);
            process.ErrorDataReceived += (sender, e) => Add(e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        internal static PeerProcess Start(string name, string program, IEnumerable<object> args, PeerRuntime runtime = PeerRuntime.NetFramework,
            string workingDirectory = null, IDictionary<string, string> environment = null)
        {
            string arguments = String.Join(" ", args.Select(arg => Quote(Convert.ToString(arg))));
            var info = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(program)
            };

            if (runtime == PeerRuntime.UnityMono)
            {
                info.FileName = TestEnvironment.UnityMono;
                info.Arguments = Quote(program) + " " + arguments;
                info.EnvironmentVariables["MONO_PATH"] = TestEnvironment.UnityMonoProfile;
            }
            else
            {
                info.FileName = program;
                info.Arguments = arguments;
            }

            if (environment != null)
                foreach (var pair in environment) info.EnvironmentVariables[pair.Key] = pair.Value;
            var started = new PeerProcess(name, info);
            started.Add("[test] started " + Path.GetFileName(info.FileName) + " " + info.Arguments);
            return started;
        }

        // Runs a command to completion and returns its output, failing if it exits with a non-zero code.
        internal static string Run(string name, string program, IEnumerable<object> args, TimeSpan timeout, string workingDirectory = null)
        {
            using (var run = Start(name, program, args, PeerRuntime.NetFramework, workingDirectory))
            {
                if (!run.process.WaitForExit((int)timeout.TotalMilliseconds)) throw new XunitException(name + " did not finish in " + timeout + ".\n" + run.Output);
                run.process.WaitForExit();
                if (run.process.ExitCode != 0) throw new XunitException(name + " exited with " + run.process.ExitCode + ".\n" + run.Output);
                return run.Output;
            }
        }

        internal string Output
        {
            get { lock (lines) return "--- " + name + " output ---\n" + String.Join("\n", lines); }
        }

        internal bool HasExited { get { return process.HasExited; } }

        internal bool HasLine(string text)
        {
            lock (lines) return lines.Any(line => line.Contains(text));
        }

        internal Task<string> WaitForLine(string text)
        {
            return WaitForLine(text, DefaultTimeout);
        }

        internal async Task<string> WaitForLine(string text, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                lock (lines)
                {
                    string match = lines.FirstOrDefault(line => line.Contains(text));
                    if (match != null) return match;
                }
                if (DateTime.UtcNow > deadline || process.HasExited && !Flushing())
                    throw new XunitException(name + " never logged \"" + text + "\"" + (process.HasExited ? " before exiting" : " within " + timeout) + ".\n" + Output);
                await Task.Delay(50);
            }
        }

        internal async Task WaitForExit(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!process.HasExited)
            {
                if (DateTime.UtcNow > deadline) throw new XunitException(name + " did not exit within " + timeout + ".\n" + Output);
                await Task.Delay(50);
            }
            process.WaitForExit();
        }

        public void Dispose()
        {
            try
            {
                if (!process.HasExited) process.Kill();
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }

        private bool flushed;

        // Gives the asynchronous output readers one more pass after the process exits.
        private bool Flushing()
        {
            if (flushed) return false;
            process.WaitForExit();
            flushed = true;
            return true;
        }

        private void Add(string line)
        {
            if (line == null) return;
            lock (lines) lines.Add(line);
        }

        private static string Quote(string value)
        {
            return value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? value : "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
