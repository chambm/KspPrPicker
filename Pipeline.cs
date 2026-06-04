using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Rp1PrPicker
{
    // The merge/build/deploy pipeline. Run() groups the selected PRs by repository and processes each
    // repo: reset its picker branch, merge its PRs, build its C# projects, and deploy each GameData mod
    // folder (with a one-time per-mod backup).
    //
    // Plan vs execute share ONE code path. Every action goes through Git()/Msbuild()/FileStep()/Note():
    // in execute mode they run; in plan mode (Plan == true) they record the equivalent command instead.
    // The only execute-only logic is the conflict-resolution loop, which is inherently result-dependent
    // (a plan can't know whether a merge actually conflicted) — it's guarded by `if (Plan) ... continue`.
    internal sealed class Pipeline
    {
        public enum ConflictChoice { Skip, DropEarlier, ResolveManually }

        public sealed class ConflictDecision
        {
            public ConflictChoice Choice;
            public List<int> PrsToDrop = new List<int>();   // only used when Choice == DropEarlier
        }

        public sealed class ConflictContext
        {
            public PrInfo Failing;
            public List<string> ConflictingFiles;
            public List<PrInfo> MergedSoFar;
        }

        public Action<string> Log;
        public Func<ConflictContext, ConflictDecision> ResolveConflict;
        public Func<string, string, bool> AskYesNo;   // (title, message) -> Yes? — for modify/delete choices
        public bool Plan;                             // true = don't execute; record the commands instead

        public List<PrInfo> Merged = new List<PrInfo>();
        public List<PrInfo> Skipped = new List<PrInfo>();
        public List<PrInfo> DroppedDueToConflict = new List<PrInfo>();

        string _repoSlug;
        string _repoPath;
        readonly List<string> _planLines = new List<string>();

        void L(string s) => Log?.Invoke(s);

        // ---- the execute-or-record primitives -----------------------------------------------------------
        void Note(string planLine) { if (Plan) { _planLines.Add(planLine); L(planLine); } }

        RunResult Git(string args, Action<string> onLine = null)
        {
            if (Plan) { Note("git " + args); return new RunResult(); }
            return Runner.Run("git", args, _repoPath, onLine);
        }

        RunResult Msbuild(string targetAndArgs)
        {
            if (Plan) { Note($"& \"{AppConfig.MsBuildExe}\" {targetAndArgs}"); return new RunResult(); }
            return Runner.Run(AppConfig.MsBuildExe, targetAndArgs, _repoPath, L);
        }

        void FileStep(string planLine, Action execute)
        {
            if (Plan) Note(planLine); else execute();
        }
        // -------------------------------------------------------------------------------------------------

        public bool Run(IEnumerable<PrInfo> selected)
        {
            try
            {
                var groups = selected.GroupBy(p => p.RepoSlug)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();

                if (Plan)
                {
                    Note("# ============================================================");
                    Note("# RP-1 PR Picker — planned commands ('Trust the clanker' is OFF)");
                    Note("# Nothing has been executed. Review, then run these yourself.");
                    Note("# ============================================================");
                }

                bool allOk = true;
                foreach (var g in groups)
                    if (!ProcessRepo(g.Key, g.ToList())) allOk = false;

                if (Plan) WritePlanFile();
                return allOk;
            }
            catch (Exception ex)
            {
                L("\nFATAL: " + ex);
                return false;
            }
        }

        bool ProcessRepo(string slug, List<PrInfo> prs)
        {
            _repoSlug = slug;
            _repoPath = AppConfig.RepoPath(slug);
            var name = AppConfig.RepoNameOf(slug);
            L($"\n########################  {name}  ({prs.Count} PR(s))  ########################");
            Note($"# ===================== {name} =====================");

            if (!RepoManager.IsCloned(slug))
            {
                if (Plan) Note($"git clone https://github.com/{slug}.git \"{_repoPath}\"");
                else if (!RepoManager.EnsureCloned(slug, L)) { L($"Could not clone {slug}; skipping repo."); return false; }
            }
            Note($"Set-Location \"{_repoPath}\"");

            Git("config rerere.enabled true");
            Fetch();
            ResetPickerBranch();

            var merged = new List<PrInfo>();
            var skipped = new HashSet<string>();
            var dropped = new HashSet<string>();
            var queue = prs.OrderBy(p => p.Number).ToList();
            int i = 0;

            Note("# --- merge PRs (resolve conflicts yourself) ---");
            while (i < queue.Count)
            {
                var pr = queue[i];
                if (skipped.Contains(pr.Uid) || dropped.Contains(pr.Uid)) { i++; continue; }

                L($"\n=== Merging PR #{pr.Number} ({pr.HeadRef}) — {pr.Title} ===");
                if (Plan && pr.ConflictsWithMaster) Note($"# NOTE: GitHub reports PR #{pr.Number} CONFLICTING with {AppConfig.BaseBranch}");

                if (!FetchPrRef(pr) && !Plan)
                {
                    L($"  Failed to fetch PR #{pr.Number}; skipping.");
                    skipped.Add(pr.Uid); Skipped.Add(pr); i++;
                    continue;
                }

                var mergeRes = MergePr(pr);
                if (Plan) { i++; continue; }   // plan: commands emitted; conflict handling is execute-only

                if (mergeRes.Ok) { merged.Add(pr); i++; continue; }

                if (TryAutoCompleteWithRerere())
                {
                    L($"  Auto-resolved PR #{pr.Number} from a remembered resolution (git rerere).");
                    merged.Add(pr); i++;
                    continue;
                }

                var conflictingFiles = GetConflictingFiles();
                L($"  Conflict in {conflictingFiles.Count} file(s): {string.Join(", ", conflictingFiles)}");
                var ctx = new ConflictContext
                {
                    Failing = pr,
                    ConflictingFiles = conflictingFiles,
                    MergedSoFar = merged.Where(m => m.TouchesAnyOf(conflictingFiles)).ToList(),
                };
                var decision = ResolveConflict?.Invoke(ctx) ?? new ConflictDecision { Choice = ConflictChoice.Skip };

                if (decision.Choice == ConflictChoice.ResolveManually)
                {
                    if (ResolveWithMergeTool(pr)) merged.Add(pr);
                    else { L($"  Conflicts left unresolved — skipping PR #{pr.Number}."); Git("merge --abort", L); skipped.Add(pr.Uid); Skipped.Add(pr); }
                    i++;
                }
                else if (decision.Choice == ConflictChoice.Skip)
                {
                    L($"  User chose: skip PR #{pr.Number}.");
                    Git("merge --abort", L); skipped.Add(pr.Uid); Skipped.Add(pr); i++;
                }
                else
                {
                    Git("merge --abort", L);
                    L($"  User chose: drop earlier PR(s) {string.Join(", ", decision.PrsToDrop)} and retry.");
                    var drop = merged.Where(m => decision.PrsToDrop.Contains(m.Number)).ToList();
                    foreach (var d in drop) { dropped.Add(d.Uid); DroppedDueToConflict.Add(d); }
                    merged = merged.Where(m => !dropped.Contains(m.Uid)).ToList();

                    ResetPickerBranch();
                    var survivors = merged.ToList();
                    merged.Clear();
                    foreach (var s in survivors)
                    {
                        L($"  Replaying PR #{s.Number}");
                        FetchPrRef(s);
                        if (!MergePr(s).Ok)
                        {
                            L($"  ERROR: replay of PR #{s.Number} unexpectedly conflicted. Aborting {name}.");
                            Git("merge --abort", L);
                            return false;
                        }
                        merged.Add(s);
                    }
                    // do NOT advance i — retry the current PR.
                }
            }

            if (!Plan) Merged.AddRange(merged);

            // In plan mode we don't know what will merge, so plan for the whole selected set.
            var workItems = Plan ? (IEnumerable<PrInfo>)prs : merged;
            if (!workItems.Any()) { L($"\nNo PRs merged for {name} — nothing to build/deploy."); return true; }

            if (workItems.Any(p => p.TouchesCs))
            {
                if (!BuildRepo()) return false;
            }
            else { L($"\nNo .cs changes in {name}'s merged set — skipping build; deploying GameData as-is."); Note("# (no selected PR touches .cs — no build)"); }

            return DeployRepo();
        }

        void Fetch()
        {
            L("Fetching origin (refs/heads + refs/pull)…");
            // A shallow clone can be missing the merge-base commit of an older PR branch, which makes git
            // refuse the merge with "unrelated histories". Deepen once so merge bases are reachable.
            if (Plan)
                Note("# If 'git rev-parse --is-shallow-repository' prints true, first run: git fetch --unshallow origin");
            else if (Git("rev-parse --is-shallow-repository").Stdout.Trim() == "true")
            {
                L("Local clone is shallow — fetching full history (one-time --unshallow)…");
                Git("fetch --unshallow origin", L);
            }
            Git($"fetch origin {AppConfig.BaseBranch} +refs/pull/*/head:refs/remotes/origin/pr/*", L);
        }

        void ResetPickerBranch()
        {
            L($"Resetting {AppConfig.PickerBranch} to origin/{AppConfig.BaseBranch}…");
            // Discard any tracked changes a prior run left (built DLLs placed into GameData, etc.) so the
            // branch checkout below isn't blocked. (Untracked build artifacts under gitignored bin/obj are fine.)
            if (!Plan) Git("reset --hard", L);
            if (Plan)
                Git($"checkout {AppConfig.BaseBranch}");
            else
            {
                var current = Git("branch --show-current").Stdout.Trim();
                if (current == AppConfig.PickerBranch) Git($"checkout {AppConfig.BaseBranch}", L);
            }
            Git($"branch -f {AppConfig.PickerBranch} origin/{AppConfig.BaseBranch}", L);
            Git($"checkout {AppConfig.PickerBranch}", L);
            Git($"reset --hard origin/{AppConfig.BaseBranch}", L);
        }

        bool FetchPrRef(PrInfo pr) => Git($"fetch origin pull/{pr.Number}/head:pr-{pr.Number} --force", L).Ok;

        RunResult MergePr(PrInfo pr)
        {
            var msg = $"picker: merge PR #{pr.Number} - {pr.Title}".Replace("\"", "'");
            return Git($"merge --no-ff --no-edit -m \"{msg}\" pr-{pr.Number}", L);
        }

        List<string> GetConflictingFiles()
        {
            var r = Git("diff --name-only --diff-filter=U");
            return r.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        static readonly HashSet<string> ConflictCodes = new HashSet<string> { "DD", "AU", "UD", "UA", "DU", "AA", "UU" };

        sealed class Unmerged { public string Code; public string Path; }

        List<Unmerged> UnmergedEntries()
        {
            var list = new List<Unmerged>();
            foreach (var line in Git("status --porcelain").Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (line.Length >= 4 && ConflictCodes.Contains(line.Substring(0, 2)))
                    list.Add(new Unmerged { Code = line.Substring(0, 2), Path = line.Substring(3) });
            return list;
        }

        static readonly string[] ConflictMarkers = { "<<<<<<<", "|||||||", "=======", ">>>>>>>" };

        bool IsMarkerFreeText(string repoRelPath)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(Path.Combine(_repoPath, repoRelPath)); }
            catch { return false; }
            if (Array.IndexOf(bytes, (byte)0) >= 0) return false;   // NUL byte ⇒ binary ⇒ rerere can't resolve it
            foreach (var raw in System.Text.Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (ConflictMarkers.Any(m => line == m || line.StartsWith(m + " ")))
                    return false;
            }
            return true;
        }

        bool TryAutoCompleteWithRerere()
        {
            var entries = UnmergedEntries();
            if (entries.Count == 0 || entries.Any(e => e.Code != "UU")) return false;
            if (entries.Any(e => !IsMarkerFreeText(e.Path))) return false;
            Git("add -A", L);
            if (!Git("commit --no-edit", L).Ok)
            {
                Git("merge --abort", L);
                return false;
            }
            return true;
        }

        bool ResolveWithMergeTool(PrInfo pr)
        {
            foreach (var e in UnmergedEntries())
            {
                if (e.Code == "UU" || e.Code == "AA") continue;
                if (e.Code == "UD" || e.Code == "DU")
                {
                    bool keep = AskYesNo?.Invoke("Modify/delete conflict",
                        $"{e.Path}\n\nThis file was modified on one side and deleted by PR #{pr.Number} on the other.\n\n" +
                        "Yes = keep the modified file\nNo = accept the deletion") ?? false;
                    Git(keep ? $"add -- \"{e.Path}\"" : $"rm -- \"{e.Path}\"", L);
                    L($"  {e.Path}: {(keep ? "kept modified version" : "accepted deletion")}.");
                }
                else if (e.Code == "DD") Git($"rm -- \"{e.Path}\"", L);
                else Git($"add -- \"{e.Path}\"", L);
            }

            var stillConflicted = new HashSet<string>(Git("rerere remaining").Stdout
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
            foreach (var e in UnmergedEntries())
                if (!stillConflicted.Contains(e.Path))
                    Git($"add -- \"{e.Path}\"", L);

            if (UnmergedEntries().Count > 0)
            {
                var tool = string.IsNullOrWhiteSpace(AppConfig.MergeTool) ? "" : $" --tool={AppConfig.MergeTool}";
                L($"  Launching merge tool for PR #{pr.Number} — resolve, save, and close each file…");
                Git($"-c mergetool.keepBackup=false -c mergetool.writeToTemp=true -c mergetool.trustExitCode=true mergetool -y{tool}", L);
            }

            if (UnmergedEntries().Count > 0) { L("  Some files are still unresolved after the merge tool closed."); return false; }
            if (!Git("commit --no-edit", L).Ok) { L("  Commit after resolution failed."); return false; }
            L($"  Resolved and committed PR #{pr.Number}. Content resolutions are remembered (git rerere) for future runs.");
            return true;
        }

        // Restores a project's NuGet packages. SolutionDir is required for packages.config restore to find
        // the solution-level packages folder; RestorePackagesConfig handles old-style packages.config.
        void RestorePackages(string target)
        {
            if (target == null) return;
            L($"  Restoring NuGet packages ({Path.GetFileName(target)})…");
            // Trailing '/' (not '\') so the closing quote isn't escaped; packages.config restore needs SolutionDir.
            Msbuild($"\"{(Plan ? GetRel(_repoPath, target) : target)}\" -t:Restore -p:RestorePackagesConfig=true \"-p:SolutionDir={_repoPath}/\" -nologo -v:minimal");
        }

        // Builds the repo's C# projects. RP-1 keeps its known config (RP0.csproj + .refs, Release|x64) and a
        // build failure aborts. Other repos are best-effort: KSP refs via $(KSPDIR)/shared-refs, $(DevEnvDir)
        // for T4 pre-build events, machine-specific PostBuildEvents suppressed, and a Debug retry if Release
        // fails. Repos with a Kerbalism-style BuildSystem use their own recipe.
        bool BuildRepo()
        {
            bool isRp1 = _repoSlug == AppConfig.RepoSlug;

            if (!isRp1 && File.Exists(Path.Combine(_repoPath, "BuildSystem", "UserConfigDevEnv.xml.CopyMe")))
                return BuildKerbalismStyle();

            if (!Plan && !File.Exists(AppConfig.MsBuildExe))
            {
                L($"  MSBuild not found at {AppConfig.MsBuildExe}{(isRp1 ? "; aborting." : "; skipping build.")}");
                return !isRp1;
            }

            var csprojs = isRp1
                ? new List<string> { Path.Combine(_repoPath, AppConfig.RP0CsProj) }
                : RepoManager.DiscoverCsprojs(_repoPath);
            if (csprojs.Count == 0) { L("  No .csproj found — deploying committed GameData without a build."); Note("# (no .csproj discovered — deploy committed GameData)"); return true; }

            var sln = Directory.Exists(_repoPath)
                ? Directory.GetFiles(_repoPath, "*.sln", SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault()
                : null;
            RestorePackages(sln ?? csprojs.FirstOrDefault());

            var ownRefs = Path.Combine(_repoPath, ".refs");
            var refsDir = Directory.Exists(ownRefs) ? ownRefs : AppConfig.SharedRefsDir;
            if (!Plan && !Directory.Exists(ownRefs)) RepoManager.EnsureSharedRefs(L);

            // Properties KSP-RO repos commonly need but that are only set inside Visual Studio. No trailing
            // backslash on the values (it would escape the closing quote); $(DevEnvDir)\tool resolves fine.
            var common = isRp1 ? "" :
                $" \"-p:KSPDIR={AppConfig.KspRootDir}\" \"-p:DevEnvDir={AppConfig.VsIdeDir}\" -p:PostBuildEvent=";

            foreach (var csproj in csprojs)
            {
                var label = Path.GetFileName(csproj);
                L($"\n=== Building {label} ===");
                var platform = isRp1 ? " -p:Platform=x64" : "";
                var target = Plan ? GetRel(_repoPath, csproj) : csproj;
                var r = Msbuild($"\"{target}\" -p:Configuration=Release{platform}{common} \"-p:ReferencePath={refsDir}\" -nologo -v:minimal");
                if (!Plan && !r.Ok && !isRp1)
                {
                    L($"  Release build of {label} failed — retrying Debug…");
                    r = Msbuild($"\"{target}\" -p:Configuration=Debug{common} \"-p:ReferencePath={refsDir}\" -nologo -v:minimal");
                }
                if (Plan) continue;
                if (!r.Ok)
                {
                    if (isRp1) { L("MSBuild FAILED."); return false; }
                    L($"  Build of {label} FAILED — continuing. Config files still deploy, but this PR's compiled code");
                    L($"    won't take effect: the overlay keeps the repo's committed DLL if it ships one, else the");
                    L($"    DLL already installed in KSP stays in place.");
                }
                else PlaceBuiltDlls(r.Stdout);
            }
            if (!Plan && isRp1 && !File.Exists(AppConfig.BuiltDllPath))
            {
                L($"Build reported success but {AppConfig.BuiltDllPath} not found.");
                return false;
            }
            return true;
        }

        // Builds via a Kerbalism-style custom BuildSystem: generate UserConfigDevEnv.xml pointing at the KSP
        // install, restore the package-bearing project (with SolutionDir), and build the orchestrator
        // (*Build.csproj) in Debug — Release needs a contributor password. Its build system copies the DLLs
        // straight into the live GameData (so they bypass our per-mod backup; the overlay still handles cfgs).
        bool BuildKerbalismStyle()
        {
            var name = AppConfig.RepoNameOf(_repoSlug);
            L($"\n=== Building {name} (custom BuildSystem, Debug) ===");
            var orchestrator = RepoManager.DiscoverCsprojs(_repoPath)
                .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).EndsWith("Build", StringComparison.OrdinalIgnoreCase));
            var pkgProject = RepoManager.DiscoverCsprojs(_repoPath)
                .FirstOrDefault(p => File.Exists(Path.Combine(Path.GetDirectoryName(p), "packages.config")));

            if (Plan)
            {
                Note($"# {name}: create BuildSystem\\UserConfigDevEnv.xml from .CopyMe with KSPDevPath = the KSP install, then:");
                if (pkgProject != null) Note($"& \"{AppConfig.MsBuildExe}\" \"{GetRel(_repoPath, pkgProject)}\" -t:Restore -p:RestorePackagesConfig=true \"-p:SolutionDir={_repoPath}\\\"");
                if (orchestrator != null) Note($"& \"{AppConfig.MsBuildExe}\" \"{GetRel(_repoPath, orchestrator)}\" -p:Configuration=Debug");
                return true;
            }

            if (orchestrator == null) { L("  Could not find the *Build orchestrator project — skipping build."); return true; }

            var cfg = Path.Combine(_repoPath, "BuildSystem", "UserConfigDevEnv.xml");
            if (!File.Exists(cfg))
            {
                try
                {
                    var tmpl = File.ReadAllText(cfg + ".CopyMe").Replace(@"C:\MY\PATH\TO\KSP", AppConfig.KspRootDir);
                    File.WriteAllText(cfg, tmpl);
                    L($"  Wrote BuildSystem/UserConfigDevEnv.xml (KSPDevPath = {AppConfig.KspRootDir})");
                }
                catch (Exception ex) { L("  Could not write UserConfigDevEnv.xml: " + ex.Message); return true; }
            }

            if (pkgProject != null) RestorePackages(pkgProject);
            var r = Msbuild($"\"{orchestrator}\" -p:Configuration=Debug -nologo -v:minimal");
            if (!r.Ok) L($"  {name} Debug build FAILED — its committed/installed DLLs will be used.");
            else L($"  {name} built (its build system copied the DLLs into the live GameData).");
            return true;
        }

        // Repos that build into bin/ rather than GameData: take each just-built DLL (parsed from the MSBuild
        // "Project -> path.dll" output) and drop it into the repo's GameData at the same place the install
        // keeps it, so the normal overlay deploys it with a backup.
        void PlaceBuiltDlls(string buildOutput)
        {
            if (string.IsNullOrEmpty(buildOutput) || !Directory.Exists(AppConfig.KspGameDataDir)) return;
            var repoGd = Path.Combine(_repoPath, "GameData");
            // Only place a built DLL into a mod folder THIS repo actually ships (a DLL can be bundled by
            // several mods in the install, e.g. ClickThroughBlocker.dll lives in both its own mod and RealFuels).
            var repoMods = new HashSet<string>(
                Directory.Exists(repoGd) ? Directory.GetDirectories(repoGd).Select(Path.GetFileName) : Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            if (repoMods.Count == 0) return;

            foreach (var raw in buildOutput.Split('\n'))
            {
                int arrow = raw.IndexOf(" -> ");
                if (arrow < 0) continue;
                var built = raw.Substring(arrow + 4).Trim();
                if (!built.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(built)) continue;

                var dllName = Path.GetFileName(built);
                string chosenRel = null;
                foreach (var m in Directory.GetFiles(AppConfig.KspGameDataDir, dllName, SearchOption.AllDirectories))
                {
                    var rel = m.Substring(AppConfig.KspGameDataDir.Length).TrimStart('\\', '/');
                    if (repoMods.Contains(rel.Split('\\', '/')[0])) { chosenRel = rel; break; }
                }
                if (chosenRel == null) continue;   // install doesn't place it under one of this repo's mods

                var dest = Path.Combine(repoGd, chosenRel);
                if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(built), StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(built, dest, overwrite: true);
                    L($"  Placed built {dllName} → GameData\\{chosenRel}");
                }
                catch (Exception ex) { L($"  Could not place {dllName}: {ex.Message}"); }
            }
        }

        // Deploys each of the repo's GameData mod folders: a one-time pristine backup per mod, then an
        // overlay (overwrite shared files, keep install-only ones, so the live DLL survives when unbuilt).
        bool DeployRepo()
        {
            L($"\n=== Deploying {AppConfig.RepoNameOf(_repoSlug)} GameData ===");
            if (!Plan && IsKspRunning())
            {
                L("KSP is running — cannot deploy while it holds GameData files. Close KSP and retry.");
                return false;
            }
            var repoGameData = Path.Combine(_repoPath, "GameData");
            var mods = Directory.Exists(repoGameData)
                ? Directory.GetDirectories(repoGameData).Select(Path.GetFileName).ToList()
                : new List<string>();
            if (mods.Count == 0)
            {
                if (Plan) Note("#   (clone the repo to enumerate its GameData mod folders, then backup+overlay each)");
                else L("  Repo has no GameData mod folders — nothing to deploy.");
                return true;
            }

            foreach (var mod in mods)
            {
                var repoMod = Path.Combine(repoGameData, mod);
                var liveMod = Path.Combine(AppConfig.KspGameDataDir, mod);
                var backupMod = Path.Combine(AppConfig.BackupRootDir, mod);

                FileStep(
                    $"if (-not (Test-Path \"{backupMod}\")) {{ New-Item -ItemType Directory -Force \"{AppConfig.BackupRootDir}\" | Out-Null; Copy-Item -Recurse \"{liveMod}\" \"{backupMod}\" }}",
                    () =>
                    {
                        if (Directory.Exists(backupMod)) L($"  {mod}: backup exists — overlaying (not re-backing up).");
                        else if (Directory.Exists(liveMod))
                        {
                            L($"  {mod}: backing up pristine → {backupMod}");
                            Directory.CreateDirectory(AppConfig.BackupRootDir);
                            CopyDirectory(liveMod, backupMod);
                        }
                    });
                FileStep(
                    $"Copy-Item -Recurse -Force \"{repoMod}\\*\" \"{liveMod}\"",
                    () => { L($"  {mod}: overlaying merged files onto {liveMod}"); CopyDirectory(repoMod, liveMod); });
            }
            if (!Plan) L($"Deployed {mods.Count} mod folder(s).");
            return true;
        }

        void WritePlanFile()
        {
            try
            {
                var planPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "picker-plan.ps1");
                File.WriteAllLines(planPath, _planLines);
                L($"\nPlan written to {planPath}");
            }
            catch (Exception ex) { L("Could not write plan file: " + ex.Message); }
        }

        static string GetRel(string baseDir, string full)
        {
            var b = baseDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(b, StringComparison.OrdinalIgnoreCase) ? full.Substring(b.Length) : full;
        }

        // Restores every per-mod pristine backup: delete the live mod folder and move the backup back.
        // With plan == true, emits the commands instead of running them.
        public static bool RestoreBackup(Action<string> log, bool plan)
        {
            var backupRoot = AppConfig.BackupRootDir;
            var gameData = AppConfig.KspGameDataDir;
            if (plan)
            {
                log?.Invoke("# Restore plan ('Trust the clanker' is OFF) — review and run:");
                if (Directory.Exists(backupRoot))
                    foreach (var modBak in Directory.GetDirectories(backupRoot))
                    {
                        var mod = Path.GetFileName(modBak);
                        log?.Invoke($"Remove-Item -Recurse -Force \"{Path.Combine(gameData, mod)}\"");
                        log?.Invoke($"Move-Item \"{modBak}\" \"{Path.Combine(gameData, mod)}\"");
                    }
                return true;
            }
            if (!Directory.Exists(backupRoot) || Directory.GetDirectories(backupRoot).Length == 0)
            {
                log?.Invoke($"No backups to restore at {backupRoot}.");
                return false;
            }
            if (IsKspRunning())
            {
                log?.Invoke("KSP is running — close it before restoring GameData.");
                return false;
            }
            foreach (var modBak in Directory.GetDirectories(backupRoot))
            {
                var mod = Path.GetFileName(modBak);
                var liveMod = Path.Combine(gameData, mod);
                log?.Invoke($"Restoring {mod}…");
                if (Directory.Exists(liveMod)) Directory.Delete(liveMod, recursive: true);
                Directory.Move(modBak, liveMod);
            }
            try { if (Directory.GetFileSystemEntries(backupRoot).Length == 0) Directory.Delete(backupRoot); }
            catch { /* leave it if anything is in the way */ }
            log?.Invoke("Restore complete.");
            return true;
        }

        static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.GetDirectories(src))
                CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }

        static bool IsKspRunning()
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("KSP_x64"))
                {
                    string path = null;
                    try { path = p.MainModule?.FileName; } catch { /* access denied — fall through */ }
                    if (path == null) return true;  // can't tell — assume yes, safer
                    if (string.Equals(path, AppConfig.KspExe, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { /* permission issues enumerating — best-effort */ }
            return false;
        }
    }
}
