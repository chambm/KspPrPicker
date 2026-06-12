using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace KspPrPicker
{
    // Edits the machine-specific paths plus a per-CKAN-abstract-slot provider preference, persisted to
    // config.txt via AppConfig.Save(). The provider section is generated dynamically from CKAN registry
    // data -- one row per abstract identifier ("Kerbalism-Config", etc.) with more than one known
    // provider in the local CKAN repo cache. Picking a provider that matches what CKAN actually has
    // installed makes the picker skip overlaying that provider's GameData dirs from any built repo.
    internal sealed class SettingsForm : Form
    {
        readonly TextBox _reposFolder = new TextBox();
        readonly TextBox _msbuild = new TextBox();
        readonly TextBox _ksp = new TextBox();
        readonly TextBox _mergeTool = new TextBox();

        // One row per slot. Each row holds the slot string and its two ComboBoxes so Save can read them.
        readonly List<SlotRow> _slotRows = new List<SlotRow>();
        Panel _slotPanel;
        Label _slotStatus;
        Button _ok, _cancel;

        sealed class SlotRow
        {
            public string Slot;
            public ComboBox Provider;
            public ComboBox Version;
            public List<CkanRegistry.Provider> Providers;
        }

        public SettingsForm()
        {
            Text = "Settings";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(740, 460);
            Width = 740;
            Height = 540;

            AddFolderRow("Repos folder:", _reposFolder, AppConfig.ReposFolder, 20);
            AddRow("MSBuild.exe path:", _msbuild, AppConfig.MsBuildExe, "MSBuild.exe|MSBuild.exe|All files|*.*", 70);
            AddRow("KSP_x64.exe path:", _ksp, AppConfig.KspExe, "KSP_x64.exe|KSP_x64.exe|All files|*.*", 120);

            // Merge tool: a `git mergetool --tool=` name (e.g. tortoisemerge, vscode, kdiff3). Blank = auto-detect.
            Controls.Add(new Label { Text = "Merge tool:", Top = 173, Left = 12, Width = 120 });
            _mergeTool.Top = 170; _mergeTool.Left = 135; _mergeTool.Width = 250; _mergeTool.Text = AppConfig.MergeTool;
            Controls.Add(_mergeTool);
            Controls.Add(new Label { Top = 173, Left = 395, Width = 300, Text = "git mergetool --tool name; blank = auto" });

            BuildCkanSection(210);

            _ok = new Button
            {
                Text = "Save", DialogResult = DialogResult.OK, Width = 90, Height = 28,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            _ok.Click += (s, e) =>
            {
                AppConfig.ReposFolder = _reposFolder.Text.Trim();
                AppConfig.MsBuildExe = _msbuild.Text.Trim();
                AppConfig.KspExe = _ksp.Text.Trim();
                AppConfig.MergeTool = _mergeTool.Text.Trim();
                SaveSlotPrefs();
                AppConfig.Save();
            };
            _cancel = new Button
            {
                Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 28,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };

            Controls.Add(_ok);
            Controls.Add(_cancel);
            AcceptButton = _ok;
            CancelButton = _cancel;

            // ClientSize isn't final until the form is laid out (DPI/border scaling), so position the
            // bottom strip (slot panel height, status label, buttons) once it is.
            Shown += (s, e) => LayoutBottom();
        }

        void LayoutBottom()
        {
            int ch = ClientSize.Height, cw = ClientSize.Width;
            _ok.Top = _cancel.Top = ch - 36;
            _ok.Left = cw - 210;
            _cancel.Left = cw - 110;
            _slotStatus.Top = ch - 74;
            _slotStatus.Width = cw - 24;
            if (_slotPanel != null)
                _slotPanel.Height = Math.Max(60, (ch - 80) - _slotPanel.Top);
        }

        void BuildCkanSection(int top)
        {
            Controls.Add(new Label
            {
                Text = "CKAN provider preferences",
                Top = top, Left = 12, Width = 400,
                Font = new Font(Font, FontStyle.Bold),
            });
            var refresh = new Button
            {
                Text = "Refresh", Top = top - 4, Width = 90,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Left = ClientSize.Width - 110,
            };
            refresh.Click += (s, e) => { CkanRegistry.InvalidateCache(); RepopulateSlots(); };
            Controls.Add(refresh);

            // Column headers above the scroll panel, mirroring the panel's child x-offsets exactly so they line up.
            var headerTop = top + 22;
            Controls.Add(new Label { Top = headerTop, Left = 12,  Width = 200, Text = "Slot",     ForeColor = SystemColors.GrayText });
            Controls.Add(new Label { Top = headerTop, Left = 215, Width = 280, Text = "Provider", ForeColor = SystemColors.GrayText });
            Controls.Add(new Label { Top = headerTop, Left = 500, Width = 130, Text = "Version",  ForeColor = SystemColors.GrayText });
            Controls.Add(new Label { Top = headerTop, Left = 635, Width = 80,  Text = "Installed",ForeColor = SystemColors.GrayText });

            // Reserve a bottom strip for the status label (above) and the Save/Cancel buttons (below) so
            // the scroll panel stretches to fill only the space between the headers and that strip.
            int statusTop = ClientSize.Height - 74;
            _slotPanel = new Panel
            {
                Top = headerTop + 18, Left = 12,
                Width = ClientSize.Width - 24,
                Height = Math.Max(60, statusTop - 6 - (headerTop + 18)),
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            };
            Controls.Add(_slotPanel);

            _slotStatus = new Label
            {
                Top = statusTop, Left = 12,
                Width = ClientSize.Width - 24,
                Height = 34,
                ForeColor = SystemColors.GrayText,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Text = "When a provider matches what's CKAN-installed, the picker skips overlaying that provider's GameData dir(s) from any built repo.",
            };
            Controls.Add(_slotStatus);

            RepopulateSlots();
        }

        void RepopulateSlots()
        {
            _slotPanel.Controls.Clear();
            _slotRows.Clear();
            List<string> slots;
            try { slots = CkanRegistry.GetMultiProviderSlots(); }
            catch (Exception ex) { _slotStatus.Text = "CKAN data unavailable: " + ex.Message; return; }

            int y = 4;
            foreach (var slot in slots)
            {
                var providers = CkanRegistry.GetProviders(slot)
                    .OrderBy(p => p.Identifier, StringComparer.OrdinalIgnoreCase).ToList();
                var installed = CkanRegistry.GetInstalledProvider(slot);

                var row = new SlotRow { Slot = slot, Providers = providers };

                _slotPanel.Controls.Add(new Label { Top = y + 3, Left = 0, Width = 200, Text = slot });
                row.Provider = new ComboBox
                {
                    Top = y, Left = 203, Width = 280,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                };
                foreach (var prov in providers)
                    row.Provider.Items.Add(new ProviderItem { Identifier = prov.Identifier, Name = prov.Name });
                row.Provider.SelectedIndexChanged += (s, e) => RepopulateVersionsFor(row);
                _slotPanel.Controls.Add(row.Provider);

                row.Version = new ComboBox
                {
                    Top = y, Left = 488, Width = 130,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                };
                _slotPanel.Controls.Add(row.Version);

                _slotPanel.Controls.Add(new Label
                {
                    Top = y + 3, Left = 623, Width = 110,
                    Text = installed != null ? installed.Identifier : "(not installed)",
                    ForeColor = installed != null ? SystemColors.ControlText : SystemColors.GrayText,
                });

                // Default selection priority: saved pref -> installed -> first.
                var savedProvider = AppConfig.CkanProviderPrefs.TryGetValue(slot, out var sp) ? sp : null;
                var preferred = !string.IsNullOrEmpty(savedProvider) ? savedProvider : installed?.Identifier;
                int idx = -1;
                if (preferred != null)
                    for (int i = 0; i < row.Provider.Items.Count; i++)
                        if (((ProviderItem)row.Provider.Items[i]).Identifier == preferred) { idx = i; break; }
                if (idx < 0 && row.Provider.Items.Count > 0) idx = 0;
                if (idx >= 0) row.Provider.SelectedIndex = idx;   // fires RepopulateVersionsFor

                _slotRows.Add(row);
                y += 30;
            }

            if (slots.Count == 0)
                _slotStatus.Text = "No multi-provider CKAN abstract slots detected. (Refresh after a CKAN update if you expect some.)";
        }

        void RepopulateVersionsFor(SlotRow row)
        {
            row.Version.Items.Clear();
            var selected = row.Provider.SelectedItem as ProviderItem;
            var prov = selected == null ? null : row.Providers.FirstOrDefault(p => p.Identifier == selected.Identifier);
            if (prov == null) return;
            foreach (var v in prov.Versions) row.Version.Items.Add(v);

            // Prefer the saved version for this slot+provider combination; else installed version when
            // the provider is the installed one; else newest available.
            var installed = CkanRegistry.GetInstalledProvider(row.Slot);
            string wanted = null;
            if (AppConfig.CkanProviderPrefs.TryGetValue(row.Slot, out var savedProv) &&
                savedProv == selected.Identifier &&
                AppConfig.CkanVersionPrefs.TryGetValue(row.Slot, out var savedVer))
                wanted = savedVer;
            else if (installed != null && installed.Identifier == selected.Identifier)
                wanted = installed.Version;

            int idx = wanted == null ? -1 : row.Version.Items.IndexOf(wanted);
            if (idx < 0 && row.Version.Items.Count > 0) idx = 0;
            if (idx >= 0) row.Version.SelectedIndex = idx;
        }

        void SaveSlotPrefs()
        {
            AppConfig.CkanProviderPrefs.Clear();
            AppConfig.CkanVersionPrefs.Clear();
            foreach (var row in _slotRows)
            {
                if (row.Provider.SelectedItem is ProviderItem pi)
                    AppConfig.CkanProviderPrefs[row.Slot] = pi.Identifier;
                if (row.Version.SelectedItem is string v)
                    AppConfig.CkanVersionPrefs[row.Slot] = v;
            }
        }

        sealed class ProviderItem
        {
            public string Identifier;
            public string Name;
            public override string ToString() =>
                string.IsNullOrEmpty(Name) || Name == Identifier ? Identifier : $"{Name} ({Identifier})";
        }

        void AddFolderRow(string label, TextBox box, string value, int top)
        {
            Controls.Add(new Label { Text = label, Top = top + 3, Left = 12, Width = 120 });
            box.Top = top; box.Left = 135; box.Width = 470; box.Text = value;
            box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(box);
            var browse = new Button { Text = "Browse…", Top = top - 1, Left = 610, Width = 90, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            browse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    try { if (Directory.Exists(box.Text)) dlg.SelectedPath = box.Text; } catch { }
                    if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
                }
            };
            Controls.Add(browse);
        }

        void AddRow(string label, TextBox box, string value, string filter, int top)
        {
            Controls.Add(new Label { Text = label, Top = top + 3, Left = 12, Width = 120 });
            box.Top = top; box.Left = 135; box.Width = 470; box.Text = value;
            box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(box);
            var browse = new Button { Text = "Browse…", Top = top - 1, Left = 610, Width = 90, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            browse.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = filter })
                {
                    try { if (File.Exists(box.Text)) dlg.InitialDirectory = Path.GetDirectoryName(box.Text); } catch { }
                    if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
                }
            };
            Controls.Add(browse);
        }
    }
}
