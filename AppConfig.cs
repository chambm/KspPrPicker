using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KspPrPicker
{
    // Defaults are tuned for the maintainer's machine; override in %APPDATA%\rp1-pr-picker\config.txt
    // with KEY=VALUE lines. Missing keys fall through to the constant.
    internal static class AppConfig
    {
        public static string RepoOrg = "KSP-RO";
        public static string RepoSlug = "KSP-RO/RP-1";   // RP-1 is the primary build/deploy repo
        // Repositories are cloned into ReposFolder/<name>; this replaces a hardcoded RP-1 path.
        public static string ReposFolder = @"C:\Users\Matt\Downloads\ksp-claude";
        // Repos whose PRs we list, as owner/name slugs. Remembered across launches.
        public static List<string> SelectedRepos = new List<string> { "KSP-RO/RP-1" };
        public static string KspGameDataRp1Plugins = @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program\GameData\RP-1\Plugins";
        public static string KspExe = @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program\KSP_x64.exe";
        public static string MsBuildExe = @"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe";
        public static string RP0CsProj = @"Source\RP0\RP0.csproj";
        public static string BuiltDllRelative = @"GameData\RP-1\Plugins\RP0.dll";
        public static string PickerBranch = "picker/build";
        public static string BaseBranch = "master";
        // Tool name passed to `git mergetool --tool=`. Empty = let git auto-detect an available one.
        public static string MergeTool = "";
        // When false, Build && Deploy / Restore only print the commands instead of running them.
        public static bool TrustClanker = true;

        // %APPDATA%\KspPrPicker — holds config.txt and last-prs.txt.
        public static string ConfigDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KspPrPicker");

        public static void Load()
        {
            var path = Path.Combine(ConfigDir, "config.txt");
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var s = line.Trim();
                if (s.Length == 0 || s.StartsWith("#")) continue;
                int eq = s.IndexOf('=');
                if (eq <= 0) continue;
                var k = s.Substring(0, eq).Trim();
                var v = s.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "RepoSlug":               RepoSlug = v; break;
                    case "RepoOrg":                RepoOrg = v; break;
                    case "ReposFolder":            ReposFolder = v; break;
                    case "SelectedRepos":          SelectedRepos = v.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList(); break;
                    case "LocalRepoPath":          ReposFolder = Path.GetDirectoryName(v) ?? ReposFolder; break;  // migrate old key
                    case "KspGameDataRp1Plugins":  KspGameDataRp1Plugins = v; break;
                    case "KspExe":                 KspExe = v; break;
                    case "MsBuildExe":             MsBuildExe = v; break;
                    case "RP0CsProj":              RP0CsProj = v; break;
                    case "BuiltDllRelative":       BuiltDllRelative = v; break;
                    case "PickerBranch":           PickerBranch = v; break;
                    case "BaseBranch":             BaseBranch = v; break;
                    case "MergeTool":              MergeTool = v; break;
                    case "TrustClanker":           bool.TryParse(v, out TrustClanker); break;
                }
            }
        }

        public static void Save()
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllLines(Path.Combine(ConfigDir, "config.txt"), new[]
            {
                $"RepoSlug={RepoSlug}",
                $"RepoOrg={RepoOrg}",
                $"ReposFolder={ReposFolder}",
                $"SelectedRepos={string.Join(";", SelectedRepos)}",
                $"KspGameDataRp1Plugins={KspGameDataRp1Plugins}",
                $"KspExe={KspExe}",
                $"MsBuildExe={MsBuildExe}",
                $"RP0CsProj={RP0CsProj}",
                $"BuiltDllRelative={BuiltDllRelative}",
                $"PickerBranch={PickerBranch}",
                $"BaseBranch={BaseBranch}",
                $"MergeTool={MergeTool}",
                $"TrustClanker={TrustClanker}",
            });
        }

        // Short repo name from an owner/name slug, e.g. "KSP-RO/RP-1" -> "RP-1".
        public static string RepoNameOf(string slug) => slug.Contains("/") ? slug.Substring(slug.LastIndexOf('/') + 1) : slug;
        // Local clone path for a repo slug (or short name).
        public static string RepoPath(string slug) => Path.Combine(ReposFolder, RepoNameOf(slug));
        // The RP-1 clone — still the primary build/deploy repo. Derived from ReposFolder now.
        public static string LocalRepoPath => RepoPath(RepoSlug);

        public static string DeployedDllPath => Path.Combine(KspGameDataRp1Plugins, "RP0.dll");
        public static string BuiltDllPath    => Path.Combine(LocalRepoPath, BuiltDllRelative);
        public static string RefsDir         => Path.Combine(LocalRepoPath, ".refs");

        // KspGameDataRp1Plugins is ...\GameData\RP-1\Plugins; walk up to derive the surrounding dirs.
        public static string KspGameDataRp1Dir  => Path.GetDirectoryName(KspGameDataRp1Plugins);   // ...\GameData\RP-1
        public static string KspGameDataDir      => Path.GetDirectoryName(KspGameDataRp1Dir);       // ...\GameData
        public static string KspRootDir          => Path.GetDirectoryName(KspGameDataDir);          // ...\Kerbal Space Program
        // Per-mod pristine backups live under here, one subfolder per GameData mod.
        public static string BackupRootDir       => Path.Combine(KspRootDir, "GameDataPrPickerBak");
        public static string BackupRp1Dir        => Path.Combine(BackupRootDir, "RP-1");
        // Core KSP managed assemblies, used when building repos that have no .refs of their own.
        public static string KspManagedDir       => Path.Combine(KspRootDir, "KSP_x64_Data", "Managed");
        // A flat folder aggregating every reference assembly (managed + all GameData plugins), built once.
        public static string SharedRefsDir       => Path.Combine(ReposFolder, ".shared-refs");
        // The VS IDE folder (holds TextTransform.exe), derived from the MSBuild path. Some repos' pre-build
        // events call $(DevEnvDir)\texttransform.exe, which is empty under plain MSBuild.
        public static string VsIdeDir => SafeFullPath(Path.Combine(Path.GetDirectoryName(MsBuildExe) ?? "", "..", "..", "..", "Common7", "IDE"));
        static string SafeFullPath(string p) { try { return Path.GetFullPath(p); } catch { return p; } }
        // The merged working tree's GameData/RP-1 — the source we deploy from.
        public static string RepoGameDataRp1Dir  => Path.Combine(LocalRepoPath, "GameData", "RP-1");
    }
}
