using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Rp1PrPicker
{
    // Lists repositories in the org, clones the picked ones into ReposFolder, and checks each repo's
    // GameData layout so we know whether a simple per-mod backup is possible.
    internal static class RepoManager
    {
        // owner/name slugs for every non-archived repo in the org.
        public static List<string> ListOrgRepos(Action<string> log)
        {
            if (!Runner.Exists("gh")) { log?.Invoke(Runner.GhMissingHelp); return new List<string>(); }
            log?.Invoke($"Listing repositories in {AppConfig.RepoOrg}…");
            var r = Runner.Gh($"repo list {AppConfig.RepoOrg} --no-archived --limit 500 --json nameWithOwner --jq \".[].nameWithOwner\"");
            if (!r.Ok)
            {
                log?.Invoke("`gh repo list` failed:");
                log?.Invoke(r.Stderr);
                return new List<string>();
            }
            return r.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool IsCloned(string slug) => Directory.Exists(Path.Combine(AppConfig.RepoPath(slug), ".git"));

        // Clone the repo if it isn't already present. Returns true if a picker-owned clone exists afterwards.
        // Refuses to clone into a pre-existing non-empty directory that isn't already a git clone — the
        // picker owns the directories under ReposFolder and won't clobber something it didn't create.
        public static bool EnsureCloned(string slug, Action<string> log)
        {
            var path = AppConfig.RepoPath(slug);
            if (IsCloned(slug)) return true;
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            {
                log?.Invoke($"Refusing to use {path} for {slug}: it already exists and is not a picker clone.");
                log?.Invoke($"  Remove that folder or change the Repos folder in Settings, then retry.");
                return false;
            }
            Directory.CreateDirectory(AppConfig.ReposFolder);
            log?.Invoke($"Cloning {slug} → {path}…");
            var r = Runner.Run("git", $"clone https://github.com/{slug}.git \"{path}\"", AppConfig.ReposFolder, log);
            if (!r.Ok) log?.Invoke($"  Clone of {slug} failed.");
            return r.Ok;
        }

        // Reports the repo's GameData layout: which mod folders it ships, and whether any files sit loose
        // in GameData root (which would make per-mod backup ambiguous).
        public static void CheckGameDataLayout(string slug, Action<string> log)
        {
            var name = AppConfig.RepoNameOf(slug);
            var gd = Path.Combine(AppConfig.RepoPath(slug), "GameData");
            if (!Directory.Exists(gd))
            {
                log?.Invoke($"  {name}: no GameData/ folder (nothing to deploy).");
                return;
            }
            var looseFiles = Directory.GetFiles(gd).Select(Path.GetFileName).ToList();
            var modDirs = Directory.GetDirectories(gd).Select(Path.GetFileName).ToList();
            if (looseFiles.Count == 0)
                log?.Invoke($"  {name}: GameData mod dir(s) [{string.Join(", ", modDirs)}] — OK, per-mod backup is safe.");
            else
                log?.Invoke($"  {name}: ⚠ {looseFiles.Count} file(s) loose in GameData root ({string.Join(", ", looseFiles.Take(5))}) — per-mod backup won't be clean. Mod dir(s): [{string.Join(", ", modDirs)}]");
        }

        // Clone (if needed) and report layout for each selected repo.
        public static void PrepareSelected(Action<string> log)
        {
            log?.Invoke($"Preparing {AppConfig.SelectedRepos.Count} selected repo(s)…");
            foreach (var slug in AppConfig.SelectedRepos)
                if (EnsureCloned(slug, log))
                    CheckGameDataLayout(slug, log);
            log?.Invoke("Repositories ready.");
        }

        // Buildable C# projects in a repo: prefer those under Source/, skip test projects.
        public static List<string> DiscoverCsprojs(string repoPath)
        {
            if (!Directory.Exists(repoPath)) return new List<string>();
            var src = Path.Combine(repoPath, "Source");
            var root = Directory.Exists(src) ? src : repoPath;
            return Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !Path.GetFileNameWithoutExtension(p).EndsWith("Test", StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileNameWithoutExtension(p).EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Mod folders the repo ships under GameData (the per-mod deploy/backup units).
        public static List<string> GameDataModDirs(string repoPath)
        {
            var gd = Path.Combine(repoPath, "GameData");
            return Directory.Exists(gd)
                ? Directory.GetDirectories(gd).Select(Path.GetFileName).ToList()
                : new List<string>();
        }

        // A single ReferencePath dir aggregating every assembly KSP-RO csprojs reference. Built once from
        // the KSP install (core managed DLLs + every GameData plugin). Repos that ship their own .refs use
        // that instead; this is the fallback for everything else.
        public static string EnsureSharedRefs(Action<string> log)
        {
            var dir = AppConfig.SharedRefsDir;
            if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.dll").Length > 50) return dir;
            Directory.CreateDirectory(dir);
            log?.Invoke($"Building shared reference set → {dir} (one-time)…");
            int n = 0;
            bool IsAsm(string f) => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".kbin", StringComparison.OrdinalIgnoreCase);
            void LinkAll(IEnumerable<string> files)
            {
                foreach (var f in files.Where(IsAsm))
                    if (LinkOrCopy(f, Path.Combine(dir, Path.GetFileName(f)))) n++;
            }
            if (Directory.Exists(AppConfig.KspManagedDir))
                LinkAll(Directory.GetFiles(AppConfig.KspManagedDir));
            // Every assembly anywhere under GameData — some mods put DLLs outside a Plugins folder
            // (e.g. 000_Harmony/0Harmony.dll, ContractConfigurator), which a Plugins-only scan would miss.
            if (Directory.Exists(AppConfig.KspGameDataDir))
                LinkAll(Directory.GetFiles(AppConfig.KspGameDataDir, "*.*", SearchOption.AllDirectories));
            log?.Invoke($"  {n} reference assemblies linked.");
            return dir;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateHardLink(string newLink, string existingFile, IntPtr reserved);

        // Hardlink src → dst (no extra disk; same data as the install). Falls back to a copy when a hardlink
        // isn't possible (e.g. ReposFolder on a different volume than KSP). Returns true on success.
        static bool LinkOrCopy(string src, string dst)
        {
            try { if (File.Exists(dst)) File.Delete(dst); } catch { }
            if (CreateHardLink(dst, src, IntPtr.Zero)) return true;
            try { File.Copy(src, dst, true); return true; } catch { return false; }
        }
    }
}
