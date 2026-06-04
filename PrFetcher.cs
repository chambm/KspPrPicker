using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Rp1PrPicker
{
    // Wraps `gh pr list` to fetch open PRs and their file lists. We use jq inside gh to flatten
    // results to tab-separated lines so we don't need a JSON library on net48.
    internal static class PrFetcher
    {
        // jq emits one record per PR with fields joined by \t, files joined by ;
        // Format: number\ttitle\theadRefName\tauthorLogin\tmergeable\tfile1;file2;file3
        // mergeable is GitHub's own merge-against-base verdict: MERGEABLE | CONFLICTING | UNKNOWN.
        const string Jq = ".[] | [(.number|tostring), .title, .headRefName, .author.login, .mergeable, ([.files[]?.path] | join(\";\"))] | @tsv";

        public static List<PrInfo> ListOpen(Action<string> log)
        {
            var prs = new List<PrInfo>();
            foreach (var slug in AppConfig.SelectedRepos)
            {
                log?.Invoke($"Fetching open PRs from {slug}…");
                // The jq expression itself contains double quotes (join(";"), @tsv etc). We wrap the whole
                // expression in double quotes for the command line, so any embedded quote must be escaped as
                // \" — otherwise CommandLineToArgvW (used by gh.exe) treats the inner quote as closing the
                // argument and the bare ; leaks out, yielding `unexpected token ";"`.
                var jqArg = Jq.Replace("\"", "\\\"");
                var args = $"pr list --repo {slug} --state open --limit 200 --json number,title,headRefName,author,mergeable,files --jq \"{jqArg}\"";
                var r = Runner.Gh(args);
                if (!r.Ok)
                {
                    log?.Invoke($"`gh pr list` failed for {slug}:");
                    log?.Invoke(r.Stderr);
                    continue;
                }
                var repoName = AppConfig.RepoNameOf(slug);
                int count = 0;
                foreach (var line in r.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 6) continue;
                    int num;
                    if (!int.TryParse(parts[0], out num)) continue;
                    prs.Add(new PrInfo
                    {
                        Number = num,
                        Title = parts[1],
                        HeadRef = parts[2],
                        Author = parts[3],
                        Mergeable = parts[4],
                        Repo = repoName,
                        RepoSlug = slug,
                        Files = parts[5].Length == 0
                            ? new List<string>()
                            : parts[5].Split(';').Where(s => s.Length > 0).ToList(),
                    });
                    count++;
                }
                log?.Invoke($"  {repoName}: {count} open PR(s).");
            }
            log?.Invoke($"Found {prs.Count} open PR(s) across {AppConfig.SelectedRepos.Count} repo(s).");
            return prs.OrderBy(p => p.Repo).ThenBy(p => p.Number).ToList();
        }
    }
}
