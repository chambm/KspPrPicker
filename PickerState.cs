using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Rp1PrPicker
{
    // Remembers the PR set from the last Build && Deploy so the checkboxes come back pre-ticked.
    // Stored next to config.txt as one PR Uid ("owner/name#number") per line.
    internal static class PickerState
    {
        static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rp1-pr-picker", "last-prs.txt");

        public static HashSet<string> LoadLastPrs()
        {
            var set = new HashSet<string>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                        if (line.Trim().Length > 0) set.Add(line.Trim());
            }
            catch { /* best-effort; absent/garbled state just means nothing is pre-checked */ }
            return set;
        }

        public static void SaveLastPrs(IEnumerable<string> uids)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, uids);
            }
            catch { /* persistence is a convenience, not load-bearing */ }
        }
    }
}
