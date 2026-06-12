using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace KspPrPicker
{
    // Reads CKAN's on-disk metadata to enumerate the providers of a CKAN abstract identifier
    // (a "provides" slot like Kerbalism-Config) and the one currently installed in the user's KSP.
    //
    // Two sources are merged:
    //   * <LOCALAPPDATA>\CKAN\repos\*.json -- per-repo (default + Sol etc.) caches of "available_modules";
    //     each module has a module_version dict keyed by version, each version carries a provides[] list.
    //   * <KSP>\CKAN\registry.json -- installed_modules[<ident>].source_module.provides, plus version.
    //
    // Both are lazily loaded; results are cached for the process lifetime so opening Settings repeatedly
    // doesn't re-parse 30+ MB of JSON.
    internal static class CkanRegistry
    {
        public sealed class Provider
        {
            public string Identifier;          // CKAN module identifier (e.g. "Kerbalism-Config-RO")
            public string Name;                // display name (e.g. "Kerbalism - RealismOverhaul Config")
            public List<string> Versions = new List<string>();   // newest-first (CKAN's source order is reverse-chronological)
            public string DownloadFor(string version) => _downloads.TryGetValue(version ?? "", out var url) ? url : null;
            internal Dictionary<string, string> _downloads = new Dictionary<string, string>();
        }

        public sealed class Installed
        {
            public string Identifier;
            public string Version;
        }

        static Dictionary<string, List<Provider>> _providersByAbstractId;
        static Dictionary<string, Installed> _installedByAbstractId;
        // Each installed module identifier -> list of GameData top-level subdirs (e.g. "KerbalismConfig",
        // "RP-1") it placed files into. Lets the deploy pipeline map "this repo wants to overlay
        // GameData/X/" back to "X is owned by CKAN module Y -- whose abstract slots are Z, W."
        static Dictionary<string, HashSet<string>> _installedDirsByModule;
        // Each installed module identifier -> its provides list + the identifier itself, so the
        // pipeline can ask "what abstract slots does this module fill?" without re-walking the registry.
        static Dictionary<string, List<string>> _slotsByInstalledModule;
        static readonly object _lock = new object();

        // The CKAN client's per-repo metadata cache. Layout:
        //   %LOCALAPPDATA%\CKAN\repos\<hash>-<reponame>.json
        // each file has { "available_modules": { <ident>: { "module_version": { <ver>: { ... } } } } }
        public static string ReposCacheDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CKAN", "repos");

        // The installed-state registry for THIS KSP instance. Layout:
        //   <KSP install>\CKAN\registry.json with { "installed_modules": { <ident>: { "source_module": {...} } } }
        public static string InstalledRegistryFile =>
            Path.Combine(AppConfig.KspRootDir, "CKAN", "registry.json");

        public static List<Provider> GetProviders(string abstractIdentifier)
        {
            EnsureLoaded();
            return _providersByAbstractId.TryGetValue(abstractIdentifier, out var list) ? list : new List<Provider>();
        }

        public static Installed GetInstalledProvider(string abstractIdentifier)
        {
            EnsureLoaded();
            return _installedByAbstractId.TryGetValue(abstractIdentifier, out var inst) ? inst : null;
        }

        // All abstract identifiers with more than one known provider in the local CKAN repo caches.
        // These are the slots worth surfacing in a "pick a provider" UI -- a single-provider slot
        // offers no actual choice. Sorted alphabetically.
        public static List<string> GetMultiProviderSlots()
        {
            EnsureLoaded();
            return _providersByAbstractId
                .Where(kv => kv.Value.Count > 1)
                .Select(kv => kv.Key)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // The CKAN module identifier that owns the live GameData/<topLevelDir>/, or null if no
        // installed module placed files there (manual install, picker overlay, fresh KSP, ...).
        // Comparison is case-insensitive to match the Windows filesystem.
        public static string GetOwnerOfGameDataDir(string topLevelDir)
        {
            if (string.IsNullOrEmpty(topLevelDir)) return null;
            EnsureLoaded();
            foreach (var kv in _installedDirsByModule)
                if (kv.Value.Contains(topLevelDir.Trim().TrimEnd('\\', '/')))
                    return kv.Key;
            return null;
        }

        // Abstract slots this installed module fills (its CKAN "provides" list, plus its own
        // identifier -- every module implicitly provides its own identifier per the CKAN spec).
        public static List<string> GetSlotsOfInstalledModule(string moduleIdentifier)
        {
            EnsureLoaded();
            return _slotsByInstalledModule.TryGetValue(moduleIdentifier, out var s) ? s : new List<string>();
        }

        public static void InvalidateCache()
        {
            lock (_lock)
            {
                _providersByAbstractId = null;
                _installedByAbstractId = null;
                _installedDirsByModule = null;
                _slotsByInstalledModule = null;
            }
        }

        static void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_providersByAbstractId != null) return;
                _providersByAbstractId = LoadAvailable();
                LoadInstalled(out _installedByAbstractId, out _installedDirsByModule, out _slotsByInstalledModule);
            }
        }

        static JavaScriptSerializer NewSerializer()
        {
            // CKAN repo caches are ~30+ MB; the default 2MB cap throws InvalidOperationException on them.
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };
        }

        // Walks every repos\*.json under LOCALAPPDATA\CKAN, indexing modules by every abstract identifier
        // they declare via "provides". A module that provides multiple identifiers appears under each.
        static Dictionary<string, List<Provider>> LoadAvailable()
        {
            var byAbstract = new Dictionary<string, List<Provider>>(StringComparer.Ordinal);
            if (!Directory.Exists(ReposCacheDir)) return byAbstract;

            var ser = NewSerializer();
            foreach (var repoFile in Directory.GetFiles(ReposCacheDir, "*.json"))
            {
                Dictionary<string, object> root;
                try { root = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(repoFile)); }
                catch { continue; }   // malformed cache: skip, don't abort the whole load
                if (root == null || !(root.TryGetValue("available_modules", out var availObj) && availObj is Dictionary<string, object> avail))
                    continue;

                foreach (var kv in avail)
                {
                    var identifier = kv.Key;
                    if (!(kv.Value is Dictionary<string, object> modBlob) ||
                        !modBlob.TryGetValue("module_version", out var mvObj) ||
                        !(mvObj is Dictionary<string, object> mvDict))
                        continue;

                    Provider provider = null;
                    foreach (var verKv in mvDict)
                    {
                        if (!(verKv.Value is Dictionary<string, object> ver)) continue;
                        var provides = AsStringList(ver.TryGetValue("provides", out var p) ? p : null);
                        if (provides.Count == 0) continue;
                        if (provider == null)
                        {
                            provider = new Provider
                            {
                                Identifier = identifier,
                                Name = ver.TryGetValue("name", out var n) ? n as string ?? identifier : identifier,
                            };
                        }
                        provider.Versions.Add(verKv.Key);
                        if (ver.TryGetValue("download", out var dl) && dl is string dlUrl)
                            provider._downloads[verKv.Key] = dlUrl;
                        foreach (var slot in provides)
                        {
                            if (!byAbstract.TryGetValue(slot, out var list))
                                byAbstract[slot] = list = new List<Provider>();
                            // The same provider can appear via several repos (default + Sol); merge on identifier.
                            var existing = list.FirstOrDefault(x => x.Identifier == identifier);
                            if (existing == null) list.Add(provider);
                            else MergeInto(existing, provider);
                        }
                    }
                }
            }

            // CKAN keys versions newest-last in module_version; reverse so newest is first.
            foreach (var list in byAbstract.Values)
                foreach (var prov in list)
                    prov.Versions = prov.Versions.AsEnumerable().Reverse().Distinct().ToList();

            return byAbstract;
        }

        static void MergeInto(Provider dest, Provider src)
        {
            foreach (var v in src.Versions)
                if (!dest.Versions.Contains(v)) dest.Versions.Add(v);
            foreach (var kv in src._downloads)
                if (!dest._downloads.ContainsKey(kv.Key)) dest._downloads[kv.Key] = kv.Value;
        }

        // Walks the KSP-instance registry and builds three indexes simultaneously:
        //   abstract slot -> installed module that fills it
        //   installed module identifier -> set of GameData top-level dirs it owns
        //   installed module identifier -> slots it provides (with its own identifier appended)
        // Done in one pass so we open the multi-MB file once.
        static void LoadInstalled(
            out Dictionary<string, Installed> byAbstract,
            out Dictionary<string, HashSet<string>> dirsByModule,
            out Dictionary<string, List<string>> slotsByModule)
        {
            byAbstract = new Dictionary<string, Installed>(StringComparer.Ordinal);
            dirsByModule = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            slotsByModule = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            var path = InstalledRegistryFile;
            if (!File.Exists(path)) return;

            Dictionary<string, object> root;
            try { root = NewSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path)); }
            catch { return; }
            if (root == null) return;

            if (root.TryGetValue("installed_modules", out var imObj) && imObj is Dictionary<string, object> im)
            {
                foreach (var kv in im)
                {
                    if (!(kv.Value is Dictionary<string, object> entry) ||
                        !entry.TryGetValue("source_module", out var smObj) ||
                        !(smObj is Dictionary<string, object> sm))
                        continue;
                    var identifier = sm.TryGetValue("identifier", out var iObj) ? iObj as string ?? kv.Key : kv.Key;
                    var version = sm.TryGetValue("version", out var vObj) ? vObj as string : null;
                    var provides = AsStringList(sm.TryGetValue("provides", out var pObj) ? pObj : null);
                    // A module always implicitly provides its own identifier (CKAN spec); record both so
                    // a UI can ask either "what provides Kerbalism-Config" or "what provides Kerbalism-Config-RO".
                    provides.Add(identifier);
                    var distinctSlots = provides.Distinct().ToList();
                    slotsByModule[identifier] = distinctSlots;
                    foreach (var slot in distinctSlots)
                        if (!byAbstract.ContainsKey(slot))
                            byAbstract[slot] = new Installed { Identifier = identifier, Version = version };
                }
            }

            // installed_files maps "GameData/<top>/<sub>/..." -> owner identifier; we just need the
            // distinct <top>-level dirs per owner so the pipeline can ask "who owns GameData/X/?".
            if (root.TryGetValue("installed_files", out var ifObj) && ifObj is Dictionary<string, object> instFiles)
            {
                foreach (var kv in instFiles)
                {
                    if (!(kv.Value is string owner) || string.IsNullOrEmpty(owner)) continue;
                    var path2 = kv.Key.Replace('\\', '/');
                    var parts = path2.Split('/');
                    if (parts.Length < 2) continue;
                    if (!parts[0].Equals("GameData", StringComparison.OrdinalIgnoreCase)) continue;
                    var top = parts[1];
                    if (string.IsNullOrEmpty(top)) continue;
                    if (!dirsByModule.TryGetValue(owner, out var set))
                        dirsByModule[owner] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(top);
                }
            }
        }

        static List<string> AsStringList(object o)
        {
            if (o is object[] arr) return arr.Select(x => x as string).Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (o is System.Collections.ArrayList al) return al.Cast<object>().Select(x => x as string).Where(s => !string.IsNullOrEmpty(s)).ToList();
            return new List<string>();
        }
    }
}
