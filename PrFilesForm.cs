using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace KspPrPicker
{
    // Lists every file a PR touches and, per file, which other open PRs also touch it — i.e. where this
    // PR may collide on merge. Overlapping rows are tinted with the same red used in the main list.
    internal sealed class PrFilesForm : Form
    {
        static readonly Color ConflictColor = Color.FromArgb(255, 205, 205);

        public PrFilesForm(PrInfo pr, Dictionary<string, List<string>> prsByFile)
        {
            Text = $"{pr.Repo} PR #{pr.Number} files — {pr.Title}";
            Width = 920;
            Height = 600;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(500, 300);

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoGenerateColumns = false,
            };
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "File",
                HeaderText = "File",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.Automatic,
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Shared",
                HeaderText = "Also touched by",
                Width = 240,
                SortMode = DataGridViewColumnSortMode.Automatic,
            });

            int overlapCount = 0;
            // Conflicting files first, then alphabetical, so the interesting rows are up top.
            foreach (var file in pr.Files.OrderByDescending(f => Others(prsByFile, pr, f).Any())
                                          .ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var others = Others(prsByFile, pr, file).ToList();
                int rowIndex = grid.Rows.Add(file, others.Count == 0 ? "" : string.Join(", ", others));
                if (others.Count > 0)
                {
                    grid.Rows[rowIndex].DefaultCellStyle.BackColor = ConflictColor;
                    overlapCount++;
                }
            }

            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Padding = new Padding(8, 6, 8, 0),
                Text = $"{pr.Files.Count} file(s); {overlapCount} also touched by other open PR(s).",
            };

            Controls.Add(grid);
            Controls.Add(header);
        }

        // Other PRs in the same repo touching this file, shown as "#<number>" parsed from their Uid.
        static IEnumerable<string> Others(Dictionary<string, List<string>> prsByFile, PrInfo pr, string file)
        {
            if (prsByFile.TryGetValue(pr.RepoSlug + "\0" + file, out var list))
                return list.Where(uid => uid != pr.Uid)
                           .Select(uid => "#" + uid.Substring(uid.LastIndexOf('#') + 1))
                           .OrderBy(s => s);
            return Enumerable.Empty<string>();
        }
    }
}
