using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace KspPrPicker
{
    // Per-repo config: "Query" lists the repo's PRs, "Branches" also lists its branches. The org's repos
    // plus any custom github repos added by URL. Custom (non-org) repos can be removed; org repos cannot.
    internal sealed class SelectRepositoriesForm : Form
    {
        readonly DataGridView _grid = new DataGridView();
        readonly HashSet<string> _orgRepos;
        readonly Button _remove = new Button();

        public List<string> SelectedSlugs => RowsWhere("Query");
        public List<string> BranchSlugs => RowsWhere("Branches");

        public SelectRepositoriesForm(List<string> allRepos, IEnumerable<string> preChecked, IEnumerable<string> preBranches)
        {
            Text = $"Select repositories — {AppConfig.RepoOrg}";
            Width = 620;
            Height = 600;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(460, 400);

            _orgRepos = new HashSet<string>(allRepos, StringComparer.OrdinalIgnoreCase);
            var query = new HashSet<string>(preChecked, StringComparer.OrdinalIgnoreCase);
            var branches = new HashSet<string>(preBranches, StringComparer.OrdinalIgnoreCase);

            _grid.Dock = DockStyle.Fill;
            _grid.AutoGenerateColumns = false;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Query", HeaderText = "Query", Width = 55 });
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Branches", HeaderText = "Branches", Width = 70 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Repo", HeaderText = "Repository", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

            // Org repos, then any remembered custom repos that aren't in the org list.
            foreach (var slug in allRepos)
                AddRow(slug, query.Contains(slug), branches.Contains(slug));
            foreach (var slug in query.Union(branches).Where(s => !_orgRepos.Contains(s)))
                AddRow(slug, query.Contains(slug), branches.Contains(slug));

            // Commit checkbox toggles immediately.
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.SelectionChanged += (s, e) => UpdateRemoveEnabled();

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 44 };
            var all = new Button { Text = "All", Width = 50, Top = 8, Left = 8 };
            var none = new Button { Text = "None", Width = 55, Top = 8, Left = 62 };
            var add = new Button { Text = "Add URL…", Width = 80, Top = 8, Left = 121 };
            _remove.Text = "Remove"; _remove.Width = 70; _remove.Top = 8; _remove.Left = 205; _remove.Enabled = false;
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Top = 8 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Top = 8 };
            all.Click += (s, e) => SetAllQuery(true);
            none.Click += (s, e) => SetAllQuery(false);
            add.Click += (s, e) => AddCustom();
            _remove.Click += (s, e) => RemoveSelected();
            buttons.Layout += (s, e) =>
            {
                cancel.Left = buttons.ClientSize.Width - cancel.Width - 8;
                ok.Left = cancel.Left - ok.Width - 8;
            };
            buttons.Controls.AddRange(new Control[] { all, none, add, _remove, ok, cancel });

            Controls.Add(_grid);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        void AddRow(string slug, bool query, bool branches)
        {
            int i = _grid.Rows.Add(query, branches, slug);
            if (!_orgRepos.Contains(slug)) _grid.Rows[i].DefaultCellStyle.ForeColor = Color.MediumBlue;   // custom
        }

        List<string> RowsWhere(string column)
        {
            var result = new List<string>();
            foreach (DataGridViewRow row in _grid.Rows)
                if (row.Cells[column].Value is bool b && b)
                    result.Add((string)row.Cells["Repo"].Value);
            return result;
        }

        void SetAllQuery(bool value)
        {
            foreach (DataGridViewRow row in _grid.Rows) row.Cells["Query"].Value = value;
        }

        void UpdateRemoveEnabled()
        {
            _remove.Enabled = _grid.CurrentRow != null
                && _grid.CurrentRow.Cells["Repo"].Value is string slug && !_orgRepos.Contains(slug);
        }

        void AddCustom()
        {
            var url = PromptForUrl();
            var slug = ParseSlug(url);
            if (slug == null)
            {
                if (!string.IsNullOrWhiteSpace(url))
                    MessageBox.Show(this, "Couldn't parse an owner/name from that. Use a github.com URL or owner/name.",
                        "Add repository", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            int existing = IndexOfSlug(slug);
            if (existing >= 0) { _grid.Rows[existing].Cells["Query"].Value = true; _grid.CurrentCell = _grid.Rows[existing].Cells["Repo"]; return; }
            AddRow(slug, true, false);
            _grid.CurrentCell = _grid.Rows[_grid.Rows.Count - 1].Cells["Repo"];
        }

        void RemoveSelected()
        {
            if (_grid.CurrentRow != null && _grid.CurrentRow.Cells["Repo"].Value is string slug && !_orgRepos.Contains(slug))
                _grid.Rows.Remove(_grid.CurrentRow);
        }

        int IndexOfSlug(string slug)
        {
            for (int i = 0; i < _grid.Rows.Count; i++)
                if (string.Equals((string)_grid.Rows[i].Cells["Repo"].Value, slug, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // Normalises a github URL (or owner/name) to an "owner/name" slug.
        static string ParseSlug(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var s = url.Trim()
                .Replace("git@github.com:", "")
                .Replace("https://github.com/", "")
                .Replace("http://github.com/", "")
                .Replace("github.com/", "");
            if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            var parts = s.Trim('/').Split('/');
            if (parts.Length < 2 || parts[parts.Length - 1].Length == 0 || parts[parts.Length - 2].Length == 0) return null;
            return parts[parts.Length - 2] + "/" + parts[parts.Length - 1];
        }

        // Minimal single-line input dialog (WinForms has no built-in InputBox).
        string PromptForUrl()
        {
            using (var dlg = new Form
            {
                Text = "Add repository by GitHub URL",
                Width = 460, Height = 140, FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
            })
            {
                var box = new TextBox { Left = 12, Top = 16, Width = 420, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
                var lbl = new Label { Left = 12, Top = 44, Width = 420, Text = "e.g. https://github.com/KSP-RO/ROLibrary or KSP-RO/ROLibrary" };
                var ok = new Button { Text = "Add", DialogResult = DialogResult.OK, Width = 80, Top = 68, Left = 268 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80, Top = 68, Left = 352 };
                dlg.Controls.AddRange(new Control[] { box, lbl, ok, cancel });
                dlg.AcceptButton = ok; dlg.CancelButton = cancel;
                return dlg.ShowDialog(this) == DialogResult.OK ? box.Text : null;
            }
        }
    }
}
