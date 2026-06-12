using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KspPrPicker
{
    internal sealed class MainForm : Form
    {
        readonly DataGridView _grid = new DataGridView();
        readonly BindingSource _bindingSource = new BindingSource();
        readonly DataTable _table = new DataTable("prs");
        readonly TextBox _log = new TextBox();
        readonly TextBox _description = new TextBox();
        readonly SplitContainer _split = new SplitContainer();
        readonly TabControl _bottomTabs = new TabControl();
        readonly TabPage _outputTab = new TabPage("Output");
        readonly TabPage _descriptionTab = new TabPage("Description");
        // The PR whose body is currently being fetched (by Uid). Used to drop stale background results
        // when the user moves on before the fetch returns.
        string _descLoadingUid;
        readonly Button _refresh = new Button();
        readonly Button _selectRepos = new Button();
        readonly Button _build = new Button();
        readonly Button _run = new Button();
        readonly Button _restore = new Button();
        readonly Button _settings = new Button();
        readonly CheckBox _launchAfter = new CheckBox();
        readonly CheckBox _trustClanker = new CheckBox();
        readonly Label _status = new Label();

        // Per-column "contains" filters keyed by the DataTable column name. Combined with AND.
        readonly Dictionary<string, string> _filters = new Dictionary<string, string>();
        // PR numbers repeat across repos, so everything is keyed by PrInfo.Uid ("owner/name#number").
        readonly Dictionary<string, PrInfo> _prById = new Dictionary<string, PrInfo>();

        // Undirected "may conflict" graph (Uid -> Uids touching a shared file in the same repo).
        readonly Dictionary<string, HashSet<string>> _conflicts = new Dictionary<string, HashSet<string>>();
        // (repoSlug \0 file path) -> every open PR Uid touching it. Backs the conflict graph and files dialog.
        readonly Dictionary<string, List<string>> _prsByFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        static readonly Color ConflictColor = Color.FromArgb(255, 205, 205);        // light: overlaps a checked PR
        static readonly Color MasterConflictColor = Color.FromArgb(229, 115, 115);  // dark: GitHub says it conflicts with master
        bool _suppressRecolor;

        public MainForm()
        {
            Text = $"KSP PR Picker — {AppConfig.RepoSlug}";
            Width = 1200;
            Height = 760;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(800, 500);

            _refresh.Text = "Refresh";
            _refresh.Top = 10; _refresh.Left = 10; _refresh.Width = 80; _refresh.Height = 28;
            _refresh.Click += async (s, e) => await RefreshAsync();

            _selectRepos.Text = "Repositories…";
            _selectRepos.Top = 10; _selectRepos.Left = 96; _selectRepos.Width = 110; _selectRepos.Height = 28;
            _selectRepos.Click += async (s, e) => await SelectReposAsync();

            _build.Text = "Build && Deploy";
            _build.Top = 10; _build.Left = 212; _build.Width = 120; _build.Height = 28;
            _build.Click += async (s, e) => await RunAsync();

            // "Run" just launches KSP. The launch-after checkbox below clicks this button.
            _run.Text = "Run";
            _run.Top = 10; _run.Left = 338; _run.Width = 50; _run.Height = 28;
            _run.Click += (s, e) => LaunchKsp();

            _restore.Text = "Restore backup";
            _restore.Top = 10; _restore.Left = 394; _restore.Width = 110; _restore.Height = 28;
            _restore.Enabled = Directory.Exists(AppConfig.BackupRootDir);
            _restore.Click += async (s, e) => await RestoreAsync();

            _settings.Text = "Settings";
            _settings.Top = 10; _settings.Left = 510; _settings.Width = 80; _settings.Height = 28;
            _settings.Click += (s, e) => OpenSettings();

            _launchAfter.Text = "Launch KSP after deploy";
            _launchAfter.Top = 14; _launchAfter.Left = 596; _launchAfter.Width = 160; _launchAfter.Height = 22;

            // When unchecked, Build && Deploy / Restore only print the commands instead of running them.
            _trustClanker.Text = "Trust the clanker";
            _trustClanker.Checked = AppConfig.TrustClanker;
            _trustClanker.Top = 14; _trustClanker.Left = 762; _trustClanker.Width = 130; _trustClanker.Height = 22;
            _trustClanker.CheckedChanged += (s, e) => { AppConfig.TrustClanker = _trustClanker.Checked; AppConfig.Save(); };

            _status.Top = 14; _status.Left = 900; _status.Width = 282; _status.Height = 22;
            _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            BuildGrid();
            BuildBottomTabs();

            // Split panel holds the grid above and the Output/Description tabs below, separated by a
            // draggable splitter so the user can resize either pane. The grid+log were previously
            // anchored at fixed offsets; now they each fill their own SplitContainer panel.
            _split.Orientation = Orientation.Horizontal;
            _split.Top = 50; _split.Left = 10;
            _split.Width = ClientSize.Width - 20; _split.Height = ClientSize.Height - 60;
            _split.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _split.SplitterDistance = 320;     // grid height; user can drag from here
            _split.Panel1MinSize = 120;
            _split.Panel2MinSize = 120;
            _grid.Dock = DockStyle.Fill;
            _bottomTabs.Dock = DockStyle.Fill;
            _split.Panel1.Controls.Add(_grid);
            _split.Panel2.Controls.Add(_bottomTabs);

            Controls.Add(_refresh);
            Controls.Add(_selectRepos);
            Controls.Add(_build);
            Controls.Add(_run);
            Controls.Add(_restore);
            Controls.Add(_settings);
            Controls.Add(_launchAfter);
            Controls.Add(_trustClanker);
            Controls.Add(_status);
            Controls.Add(_split);

            Shown += async (s, e) => await RefreshAsync();
        }

        void BuildBottomTabs()
        {
            _log.Dock = DockStyle.Fill;
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Font = new Font(FontFamily.GenericMonospace, 9);
            _log.BackColor = Color.White;
            _outputTab.Controls.Add(_log);

            _description.Dock = DockStyle.Fill;
            _description.Multiline = true;
            _description.ReadOnly = true;
            _description.ScrollBars = ScrollBars.Vertical;
            _description.WordWrap = true;
            _description.Font = new Font(FontFamily.GenericSansSerif, 9);
            _description.BackColor = Color.White;
            _description.Text = "(select a PR)";
            _descriptionTab.Controls.Add(_description);

            _bottomTabs.TabPages.Add(_outputTab);
            _bottomTabs.TabPages.Add(_descriptionTab);
            _bottomTabs.SelectedIndexChanged += (s, e) => { if (_bottomTabs.SelectedTab == _descriptionTab) RefreshDescriptionForCurrentRow(); };

            // Re-fetch when the user clicks a new row (only if the Description tab is what they're looking at).
            _grid.SelectionChanged += (s, e) => { if (_bottomTabs.SelectedTab == _descriptionTab) RefreshDescriptionForCurrentRow(); };
        }

        PrInfo CurrentSelectedPr()
        {
            if (_grid.CurrentRow == null || _grid.CurrentRow.IsNewRow) return null;
            var uid = _grid.CurrentRow.Cells["Uid"].Value as string;
            return uid != null && _prById.TryGetValue(uid, out var pr) ? pr : null;
        }

        void RefreshDescriptionForCurrentRow()
        {
            var pr = CurrentSelectedPr();
            if (pr == null) { _description.Text = "(select a PR)"; _descLoadingUid = null; return; }
            if (pr.Body != null) { _description.Text = pr.Body.Length == 0 ? "(empty description)" : pr.Body; _descLoadingUid = null; return; }

            // Mark this Uid as the in-flight fetch. The result is dropped if the user moves to a
            // different row (or away from the Description tab) before the gh call returns.
            _descLoadingUid = pr.Uid;
            _description.Text = $"Loading description for #{pr.Number}…";
            var slug = pr.RepoSlug;
            var number = pr.Number;
            var uid = pr.Uid;
            Task.Run(() => PrFetcher.FetchBody(slug, number)).ContinueWith(t =>
            {
                if (_descLoadingUid != uid) return;       // user moved on
                var body = t.IsFaulted ? null : t.Result;
                if (_prById.TryGetValue(uid, out var p)) p.Body = body ?? "";
                if (_bottomTabs.SelectedTab == _descriptionTab && CurrentSelectedPr()?.Uid == uid)
                    _description.Text = string.IsNullOrEmpty(body) ? "(empty or unavailable)" : body;
                _descLoadingUid = null;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void BuildGrid()
        {
            _table.Columns.Add("Sel", typeof(bool));
            _table.Columns.Add("Uid", typeof(string));
            _table.Columns.Add("Repo", typeof(string));
            _table.Columns.Add("Number", typeof(int));
            _table.Columns.Add("Author", typeof(string));
            _table.Columns.Add("Title", typeof(string));
            _table.Columns.Add("Files", typeof(string));
            _table.Columns.Add("CS", typeof(bool));
            _bindingSource.DataSource = _table;

            // Position/size set by SplitContainer's Panel1 (Dock = Fill) -- no Anchor needed.
            _grid.AutoGenerateColumns = false;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.ShowCellToolTips = true;

            AddColumn(new DataGridViewCheckBoxColumn(), "Sel", "✓", 32, readOnly: false);
            AddColumn(new DataGridViewTextBoxColumn(), "Uid", "Uid", 20, readOnly: true).Visible = false;
            AddColumn(new DataGridViewTextBoxColumn(), "Repo", "Repo", 90, readOnly: true);
            AddColumn(new DataGridViewTextBoxColumn(), "Number", "PR #", 60, readOnly: true);
            AddColumn(new DataGridViewTextBoxColumn(), "Author", "Author", 130, readOnly: true);
            var title = AddColumn(new DataGridViewTextBoxColumn(), "Title", "Title", 480, readOnly: true);
            title.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            AddColumn(new DataGridViewLinkColumn { TrackVisitedState = false }, "Files", "Files", 160, readOnly: true);
            AddColumn(new DataGridViewCheckBoxColumn(), "CS", ".cs?", 48, readOnly: true);

            _grid.DataSource = _bindingSource;

            // Commit checkbox toggles immediately so selection state is correct without losing cell focus.
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            // Space toggles the row's Sel checkbox from any cell (when already on Sel, let the native toggle run).
            _grid.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Space || _grid.CurrentRow == null) return;
                if (_grid.CurrentCell != null && _grid.Columns[_grid.CurrentCell.ColumnIndex].Name == "Sel") return;
                var cell = _grid.CurrentRow.Cells["Sel"];
                cell.Value = !(cell.Value is bool b && b);
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
            // Left-click headers sorts (native). Right-click opens a per-column "contains" filter.
            _grid.ColumnHeaderMouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Right && e.ColumnIndex >= 0)
                    ShowHeaderFilterMenu(e.ColumnIndex);
            };
            // Re-tint conflicts when a checkbox toggles, and after any sort/filter re-lays-out the rows.
            _grid.CellValueChanged += (s, e) =>
            {
                if (!_suppressRecolor && e.RowIndex >= 0 && e.ColumnIndex >= 0 &&
                    _grid.Columns[e.ColumnIndex].DataPropertyName == "Sel")
                    RecolorConflicts();
            };
            _grid.DataBindingComplete += (s, e) => { if (!_suppressRecolor) RecolorConflicts(); };
            // Clicking the Files link opens a per-file breakdown of cross-PR overlaps.
            _grid.CellContentClick += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                if (_grid.Columns[e.ColumnIndex].DataPropertyName != "Files") return;
                var uid = _grid.Rows[e.RowIndex].Cells["Uid"].Value as string;
                if (uid != null && _prById.TryGetValue(uid, out var pr))
                    using (var dlg = new PrFilesForm(pr, _prsByFile))
                        dlg.ShowDialog(this);
            };
        }

        // PRs that touch a file in common may conflict on merge. Cheap to compute from the file lists
        // we already fetched (a true pairwise git merge test across all open PRs would be far too slow).
        // Keys file overlaps by (repo, path) so only PRs in the same repo — which actually merge into the
        // same tree — are linked. Paths in different repos go to different GameData mods anyway.
        static string FileKey(PrInfo pr, string file) => pr.RepoSlug + "\0" + file;

        void BuildConflictGraph()
        {
            _conflicts.Clear();
            _prsByFile.Clear();
            foreach (var pr in _prById.Values)
                foreach (var f in pr.Files)
                {
                    var key = FileKey(pr, f);
                    if (!_prsByFile.TryGetValue(key, out var list)) _prsByFile[key] = list = new List<string>();
                    list.Add(pr.Uid);
                }
            foreach (var sharers in _prsByFile.Values)
            {
                if (sharers.Count < 2) continue;
                for (int i = 0; i < sharers.Count; i++)
                    for (int j = i + 1; j < sharers.Count; j++)
                    {
                        Edge(sharers[i]).Add(sharers[j]);
                        Edge(sharers[j]).Add(sharers[i]);
                    }
            }
        }

        HashSet<string> Edge(string uid)
        {
            if (!_conflicts.TryGetValue(uid, out var set)) _conflicts[uid] = set = new HashSet<string>();
            return set;
        }

        void RecolorConflicts()
        {
            var checkedUids = new HashSet<string>();
            foreach (DataRow r in _table.Rows)
                if (r["Sel"] is bool b && b) checkedUids.Add((string)r["Uid"]);

            var conflicting = new HashSet<string>();
            foreach (var uid in checkedUids)
                if (_conflicts.TryGetValue(uid, out var nbrs))
                    foreach (var m in nbrs)
                        if (!checkedUids.Contains(m)) conflicting.Add(m);

            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow || !(row.Cells["Uid"].Value is string uid)) continue;
                bool vsMaster = _prById.TryGetValue(uid, out var pr) && pr.ConflictsWithMaster;
                // Conflicts-with-master is intrinsic (dark) and wins over a selection-driven overlap (light).
                row.DefaultCellStyle.BackColor = vsMaster ? MasterConflictColor
                    : conflicting.Contains(uid) ? ConflictColor
                    : Color.Empty;
                row.Cells["Number"].ToolTipText = vsMaster ? "GitHub reports this PR conflicts with master — it needs a rebase." : "";
            }
        }

        DataGridViewColumn AddColumn(DataGridViewColumn col, string prop, string header, int width, bool readOnly)
        {
            col.Name = prop;
            col.DataPropertyName = prop;
            col.HeaderText = header;
            col.Width = width;
            col.ReadOnly = readOnly;
            col.SortMode = DataGridViewColumnSortMode.Automatic;
            col.ToolTipText = "Click to sort · right-click to filter";
            _grid.Columns.Add(col);
            return col;
        }

        void ShowHeaderFilterMenu(int columnIndex)
        {
            var col = _grid.Columns[columnIndex];
            var prop = col.DataPropertyName;
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripLabel($"Filter \"{col.HeaderText}\" contains:"));
            var box = new ToolStripTextBox { Text = _filters.TryGetValue(prop, out var cur) ? cur : "" };
            box.TextChanged += (s, e) =>
            {
                if (string.IsNullOrEmpty(box.Text)) _filters.Remove(prop);
                else _filters[prop] = box.Text;
                ApplyFilter();
            };
            menu.Items.Add(box);
            var clear = new ToolStripMenuItem("Clear this filter");
            clear.Click += (s, e) => { _filters.Remove(prop); ApplyFilter(); menu.Close(); };
            var clearAll = new ToolStripMenuItem("Clear all filters");
            clearAll.Click += (s, e) => { _filters.Clear(); ApplyFilter(); menu.Close(); };
            menu.Items.Add(clear);
            menu.Items.Add(clearAll);
            menu.Opened += (s, e) => box.Focus();

            var headerRect = _grid.GetCellDisplayRectangle(columnIndex, -1, true);
            menu.Show(_grid, new Point(headerRect.Left, headerRect.Bottom));
        }

        void ApplyFilter()
        {
            var clauses = _filters
                .Where(kv => !string.IsNullOrEmpty(kv.Value))
                .Select(kv => $"CONVERT([{kv.Key}], 'System.String') LIKE '%{EscapeLike(kv.Value)}%'")
                .ToList();
            _bindingSource.Filter = clauses.Count == 0 ? null : string.Join(" AND ", clauses);
            SetStatus($"{_bindingSource.Count} of {_table.Rows.Count} PR(s) shown" +
                      (clauses.Count > 0 ? " (filtered)." : "."));
        }

        // DataColumn LIKE treats * % [ ] as special; escape them (and the string delimiter ').
        static string EscapeLike(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\'': sb.Append("''"); break;
                    case '%': sb.Append("[%]"); break;
                    case '*': sb.Append("[*]"); break;
                    case '[': sb.Append("[[]"); break;
                    case ']': sb.Append("[]]"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        void Log(string s)
        {
            Logger.Write(s);
            if (InvokeRequired) { BeginInvoke((Action<string>)AppendLog, s); return; }
            AppendLog(s);
        }

        void AppendLog(string s) => _log.AppendText(s + Environment.NewLine);

        void SetStatus(string s)
        {
            if (InvokeRequired) { BeginInvoke((Action<string>)SetStatus, s); return; }
            _status.Text = s;
        }

        async Task RefreshAsync()
        {
            SetButtonsEnabled(false);
            SetStatus("Loading PRs…");
            try
            {
                var prs = await Task.Run(() => PrFetcher.ListOpen(Log));
                var remembered = PickerState.LoadLastPrs();
                _suppressRecolor = true;
                _prById.Clear();
                _table.Rows.Clear();
                foreach (var pr in prs)
                {
                    _prById[pr.Uid] = pr;
                    // Branches have no PR number — leave that cell blank.
                    object number = pr.IsBranch ? (object)DBNull.Value : pr.Number;
                    _table.Rows.Add(remembered.Contains(pr.Uid), pr.Uid, pr.Repo, number, pr.Author, pr.Title, pr.FilesSummary, pr.TouchesCs);
                }
                BuildConflictGraph();
                _suppressRecolor = false;
                ApplyFilter();
                RecolorConflicts();
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        List<PrInfo> SelectedPrs()
        {
            var result = new List<PrInfo>();
            foreach (DataRow row in _table.Rows)
                if (row["Sel"] is bool b && b && _prById.TryGetValue((string)row["Uid"], out var pr))
                    result.Add(pr);
            return result;
        }

        async Task RunAsync()
        {
            var selected = SelectedPrs();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Select at least one PR.", "Nothing to do", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            PickerState.SaveLastPrs(selected.Select(p => p.Uid));

            bool trust = _trustClanker.Checked;
            int repoCount = selected.Select(p => p.RepoSlug).Distinct().Count();
            SetButtonsEnabled(false);
            SetStatus(trust ? $"Running build for {selected.Count} PR(s) across {repoCount} repo(s)…" : "Generating command plan…");
            _log.Clear();

            var pipeline = new Pipeline
            {
                Log = Log,
                Plan = !trust,
                ResolveConflict = ctx => InvokeConflictDialog(ctx),
                AskYesNo = (title, msg) => InvokeYesNo(title, msg),
            };
            bool ok = false;
            try
            {
                ok = await Task.Run(() => pipeline.Run(selected));
            }
            catch (Exception ex)
            {
                Log("Unhandled exception: " + ex);
            }

            SetStatus(!trust
                ? "Plan written — review picker-plan.ps1 (nothing was run)."
                : ok
                    ? $"Done. Merged={pipeline.Merged.Count}, Skipped={pipeline.Skipped.Count}, Dropped={pipeline.DroppedDueToConflict.Count}."
                    : "Failed. See log.");

            SetButtonsEnabled(true);

            // The checkbox just clicks the Run button once a deploy succeeds. (Not in plan mode — nothing deployed.)
            if (trust && ok && _launchAfter.Checked)
                _run.PerformClick();
        }

        async Task RestoreAsync()
        {
            bool trust = _trustClanker.Checked;
            // Only the real (executing) path is destructive, so only it needs the warning.
            if (trust && MessageBox.Show(this,
                    "Delete the live mod folder(s) and restore their pristine backups in place?",
                    "Restore backup", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            SetButtonsEnabled(false);
            SetStatus(trust ? "Restoring backup…" : "Generating restore plan…");
            bool ok = false;
            try
            {
                ok = await Task.Run(() => Pipeline.RestoreBackup(Log, plan: !trust));
            }
            catch (Exception ex)
            {
                Log("Restore failed: " + ex);
            }
            SetStatus(!trust ? "Restore plan written to log (nothing was run)."
                : ok ? "Backup restored." : "Restore failed / nothing to restore. See log.");
            SetButtonsEnabled(true);
        }

        void OpenSettings()
        {
            using (var dlg = new SettingsForm())
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    SetStatus("Settings saved.");
        }

        async Task SelectReposAsync()
        {
            SetButtonsEnabled(false);
            SetStatus($"Listing repositories in {AppConfig.RepoOrg}…");
            List<string> orgRepos;
            try { orgRepos = await Task.Run(() => RepoManager.ListOrgRepos(Log)); }
            finally { SetButtonsEnabled(true); }

            using (var dlg = new SelectRepositoriesForm(orgRepos, AppConfig.SelectedRepos, AppConfig.BranchRepos))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) { SetStatus("Repository selection cancelled."); return; }
                AppConfig.SelectedRepos = dlg.SelectedSlugs;
                AppConfig.BranchRepos = dlg.BranchSlugs;
                AppConfig.Save();
            }

            SetButtonsEnabled(false);
            SetStatus("Cloning / checking selected repositories…");
            _log.Clear();
            try { await Task.Run(() => RepoManager.PrepareSelected(Log)); }
            finally { SetButtonsEnabled(true); }

            await RefreshAsync();
        }

        void LaunchKsp()
        {
            if (!File.Exists(AppConfig.KspExe))
            {
                Log($"KSP exe not found: {AppConfig.KspExe}");
                SetStatus("KSP exe not found.");
                return;
            }
            Log("\nLaunching KSP…");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppConfig.KspExe,
                    WorkingDirectory = Path.GetDirectoryName(AppConfig.KspExe),
                    UseShellExecute = false,
                });
                SetStatus("KSP launched.");
            }
            catch (Exception ex)
            {
                Log("Launch failed: " + ex.Message);
                SetStatus("Launch failed. See log.");
            }
        }

        void SetButtonsEnabled(bool enabled)
        {
            if (InvokeRequired) { BeginInvoke((Action<bool>)SetButtonsEnabled, enabled); return; }
            _refresh.Enabled = enabled;
            _selectRepos.Enabled = enabled;
            _build.Enabled = enabled;
            _run.Enabled = enabled;
            // Restore is only meaningful when a pristine backup is on disk.
            _restore.Enabled = enabled && Directory.Exists(AppConfig.BackupRootDir);
            _settings.Enabled = enabled;
        }

        bool InvokeYesNo(string title, string msg)
        {
            if (InvokeRequired) return (bool)Invoke((Func<bool>)(() => InvokeYesNo(title, msg)));
            return MessageBox.Show(this, msg, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        Pipeline.ConflictDecision InvokeConflictDialog(Pipeline.ConflictContext ctx)
        {
            Pipeline.ConflictDecision result = null;
            if (InvokeRequired)
                Invoke((Action)(() => result = ShowConflictDialog(ctx)));
            else
                result = ShowConflictDialog(ctx);
            return result;
        }

        Pipeline.ConflictDecision ShowConflictDialog(Pipeline.ConflictContext ctx)
        {
            using (var dlg = new ConflictForm(ctx))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Decision != null)
                    return dlg.Decision;
                // Cancel == treat as skip so we don't get stuck in a retry loop.
                return new Pipeline.ConflictDecision { Choice = Pipeline.ConflictChoice.Skip };
            }
        }
    }
}
