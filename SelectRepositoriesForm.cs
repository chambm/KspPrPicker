using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace KspPrPicker
{
    // Checklist of the org's repositories plus any custom github repos added by URL. The checked ones are
    // the repos whose PRs we list. Custom (non-org) repos can be removed; org repos cannot.
    internal sealed class SelectRepositoriesForm : Form
    {
        readonly CheckedListBox _list = new CheckedListBox();
        readonly HashSet<string> _orgRepos;
        readonly Button _remove = new Button();

        public List<string> SelectedSlugs =>
            _list.CheckedItems.Cast<object>().Select(o => o.ToString()).ToList();

        public SelectRepositoriesForm(List<string> allRepos, IEnumerable<string> preChecked)
        {
            Text = $"Select repositories — {AppConfig.RepoOrg}";
            Width = 560;
            Height = 600;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(420, 400);

            _orgRepos = new HashSet<string>(allRepos, StringComparer.OrdinalIgnoreCase);
            var checkedSet = new HashSet<string>(preChecked, StringComparer.OrdinalIgnoreCase);

            _list.Dock = DockStyle.Fill;
            _list.CheckOnClick = true;
            _list.IntegralHeight = false;
            foreach (var slug in allRepos)
                _list.Items.Add(slug, checkedSet.Contains(slug));
            // Remembered custom repos that aren't in the org list — keep them checked and removable.
            foreach (var slug in checkedSet)
                if (!_orgRepos.Contains(slug))
                    _list.Items.Add(slug, true);
            _list.SelectedIndexChanged += (s, e) => UpdateRemoveEnabled();

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 44 };
            var all = new Button { Text = "All", Width = 50, Top = 8, Left = 8 };
            var none = new Button { Text = "None", Width = 55, Top = 8, Left = 62 };
            var add = new Button { Text = "Add URL…", Width = 80, Top = 8, Left = 121 };
            _remove.Text = "Remove"; _remove.Width = 70; _remove.Top = 8; _remove.Left = 205; _remove.Enabled = false;
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Top = 8 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Top = 8 };
            all.Click += (s, e) => SetAll(true);
            none.Click += (s, e) => SetAll(false);
            add.Click += (s, e) => AddCustom();
            _remove.Click += (s, e) => RemoveSelected();
            // Position OK/Cancel against the panel's real width (default 200 until docked/laid out).
            buttons.Layout += (s, e) =>
            {
                cancel.Left = buttons.ClientSize.Width - cancel.Width - 8;
                ok.Left = cancel.Left - ok.Width - 8;
            };
            buttons.Controls.AddRange(new Control[] { all, none, add, _remove, ok, cancel });

            Controls.Add(_list);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        void UpdateRemoveEnabled()
        {
            // Remove is only for custom (non-org) repos.
            _remove.Enabled = _list.SelectedItem is string slug && !_orgRepos.Contains(slug);
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
            if (existing >= 0) { _list.SetItemChecked(existing, true); _list.SelectedIndex = existing; return; }
            int idx = _list.Items.Add(slug, true);
            _list.SelectedIndex = idx;
        }

        void RemoveSelected()
        {
            if (_list.SelectedItem is string slug && !_orgRepos.Contains(slug))
                _list.Items.Remove(slug);
        }

        int IndexOfSlug(string slug)
        {
            for (int i = 0; i < _list.Items.Count; i++)
                if (string.Equals(_list.Items[i].ToString(), slug, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        void SetAll(bool value)
        {
            for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, value);
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
