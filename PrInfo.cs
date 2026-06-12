using System.Collections.Generic;
using System.Linq;

namespace KspPrPicker
{
    internal sealed class PrInfo
    {
        public int Number;
        public string Title;
        public string Author;
        public string HeadRef;     // PR source branch, or (for IsBranch) the branch name itself
        public string Repo;        // short repo name, e.g. "RP-1"
        public string RepoSlug;    // owner/name, e.g. "KSP-RO/RP-1"
        public string Mergeable;   // GitHub's verdict vs base: MERGEABLE | CONFLICTING | UNKNOWN
        public bool IsBranch;      // true = a repo branch listed directly (not a pull request)
        // Lazy-loaded PR body (Markdown). Null = never fetched; the Description tab fills it on demand
        // via PrFetcher.FetchBody so we don't pay for every body up-front on Refresh.
        public string Body;

        // PR numbers repeat across repos, so identify by repo + number (PR) or repo + branch name.
        public string Uid => IsBranch ? $"{RepoSlug}@{HeadRef}" : $"{RepoSlug}#{Number}";
        // Human label for logs/dialogs.
        public string Label => IsBranch ? $"branch {HeadRef}" : $"PR #{Number}";
        // Local ref the picker fetches into, and the fetch refspec to get it from origin.
        public string LocalRef => IsBranch ? "kbr-" + HeadRef.Replace('/', '-') : $"pr-{Number}";
        public string FetchSpec => IsBranch ? $"{HeadRef}:{LocalRef}" : $"pull/{Number}/head:{LocalRef}";
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

        public string DisplayLabel => IsBranch
            ? $"branch {HeadRef}  —  {Title}"
            : $"#{Number,-5} [{Author,-15}] {Title}  —  {FilesSummary}";

        public bool TouchesAnyOf(IEnumerable<string> paths)
        {
            var set = new HashSet<string>(paths, System.StringComparer.OrdinalIgnoreCase);
            return Files.Any(set.Contains);
        }
    }
}
