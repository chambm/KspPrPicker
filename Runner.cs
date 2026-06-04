using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Rp1PrPicker
{
    internal sealed class RunResult
    {
        public int ExitCode;
        public string Stdout = "";
        public string Stderr = "";
        public bool Ok => ExitCode == 0;
    }

    // Synchronous shell-out wrapper. Streams output through onLine so the UI log fills in real time
    // rather than dumping everything at the end of a long command (msbuild, git fetch, etc.).
    internal static class Runner
    {
        public static RunResult Run(string exe, string args, string cwd, Action<string> onLine = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = cwd ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var outBuf = new StringBuilder();
            var errBuf = new StringBuilder();
            try
            {
                using (var p = new Process { StartInfo = psi, EnableRaisingEvents = true })
                {
                    p.OutputDataReceived += (_, e) => { if (e.Data != null) { outBuf.AppendLine(e.Data); onLine?.Invoke(e.Data); } };
                    p.ErrorDataReceived  += (_, e) => { if (e.Data != null) { errBuf.AppendLine(e.Data); onLine?.Invoke(e.Data); } };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    return new RunResult { ExitCode = p.ExitCode, Stdout = outBuf.ToString(), Stderr = errBuf.ToString() };
                }
            }
            catch (Exception ex)
            {
                // Most commonly: the executable isn't installed / not on PATH.
                var msg = $"Failed to run '{exe}': {ex.Message}";
                onLine?.Invoke(msg);
                return new RunResult { ExitCode = -1, Stdout = "", Stderr = msg };
            }
        }

        public const string GhMissingHelp =
            "GitHub CLI (gh) was not found on PATH. Install it (winget install --id GitHub.cli) " +
            "and run 'gh auth login', then retry.";

        // Whether an executable can be found on PATH (or as an absolute path).
        public static bool Exists(string exe)
        {
            if (File.Exists(exe)) return true;
            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
            var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';');
            foreach (var dir in pathDirs)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (var ext in exts)
                    try { if (File.Exists(Path.Combine(dir, exe + ext))) return true; } catch { }
            }
            return false;
        }

        public static RunResult Git(string args, Action<string> onLine = null)
            => Run("git", args, AppConfig.LocalRepoPath, onLine);

        // gh commands use --repo and query the remote, so they don't need a local clone as cwd.
        public static RunResult Gh(string args, Action<string> onLine = null)
            => Run("gh", args, null, onLine);
    }
}
