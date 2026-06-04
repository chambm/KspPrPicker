using System.Collections.Generic;
using System.Linq;

namespace Rp1PrPicker
{
    internal sealed class PrInfo
    {
        public int Number;
        public string Title;
        public string Author;
        public string HeadRef;     // PR branch name, e.g. "feat/foo"
        public string Repo;        // short repo name, e.g. "RP-1"
        public string RepoSlug;    // owner/name, e.g. "KSP-RO/RP-1"
        public string Mergeable;   // GitHub's verdict vs base: MERGEABLE | CONFLICTING | UNKNOWN

        // PR numbers repeat across repos, so identify a PR by repo + number.
        public string Uid => $"{RepoSlug}#{Number}";
        public List<string> Files = new List<string>();

        // GitHub already knows this PR conflicts with master, so it'll fail the merge regardless of selection.
        public bool ConflictsWithMaster => string.Equals(Mergeable, "CONFLICTING", System.StringComparison.OrdinalIgnoreCase);

        public bool TouchesCs => Files.Any(f => f.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase));
        public string FilesSummary
        {
            get
            {
                if (Files.Count == 0) return "(no file info)";
                int cs = Files.Count(f => f.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase));
                int cfg = Files.Count(f => f.EndsWith(".cfg", System.StringComparison.OrdinalIgnoreCase));
                return $"{Files.Count} file(s){(cs > 0 ? $", {cs} .cs" : "")}{(cfg > 0 ? $", {cfg} .cfg" : "")}";
            }
        }

        public string DisplayLabel => $"#{Number,-5} [{Author,-15}] {Title}  —  {FilesSummary}";

        public bool TouchesAnyOf(IEnumerable<string> paths)
        {
            var set = new HashSet<string>(paths, System.StringComparer.OrdinalIgnoreCase);
            return Files.Any(set.Contains);
        }
    }
}
