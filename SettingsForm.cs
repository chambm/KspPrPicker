using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Rp1PrPicker
{
    // Edits the two machine-specific executable paths and persists them to config.txt via AppConfig.Save().
    internal sealed class SettingsForm : Form
    {
        readonly TextBox _reposFolder = new TextBox();
        readonly TextBox _msbuild = new TextBox();
        readonly TextBox _ksp = new TextBox();
        readonly TextBox _mergeTool = new TextBox();

        public SettingsForm()
        {
            Text = "Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            Width = 720;
            Height = 300;

            AddFolderRow("Repos folder:", _reposFolder, AppConfig.ReposFolder, 20);
            AddRow("MSBuild.exe path:", _msbuild, AppConfig.MsBuildExe, "MSBuild.exe|MSBuild.exe|All files|*.*", 70);
            AddRow("KSP_x64.exe path:", _ksp, AppConfig.KspExe, "KSP_x64.exe|KSP_x64.exe|All files|*.*", 120);

            // Merge tool: a `git mergetool --tool=` name (e.g. tortoisemerge, vscode, kdiff3). Blank = auto-detect.
            Controls.Add(new Label { Text = "Merge tool:", Top = 173, Left = 12, Width = 120 });
            _mergeTool.Top = 170; _mergeTool.Left = 135; _mergeTool.Width = 250; _mergeTool.Text = AppConfig.MergeTool;
            Controls.Add(_mergeTool);
            Controls.Add(new Label { Top = 173, Left = 395, Width = 300, Text = "git mergetool --tool name; blank = auto" });

            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 90, Top = 220, Left = 510 };
            ok.Click += (s, e) =>
            {
                AppConfig.ReposFolder = _reposFolder.Text.Trim();
                AppConfig.MsBuildExe = _msbuild.Text.Trim();
                AppConfig.KspExe = _ksp.Text.Trim();
                AppConfig.MergeTool = _mergeTool.Text.Trim();
                AppConfig.Save();
            };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Top = 220, Left = 610 };

            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        void AddFolderRow(string label, TextBox box, string value, int top)
        {
            Controls.Add(new Label { Text = label, Top = top + 3, Left = 12, Width = 120 });
            box.Top = top; box.Left = 135; box.Width = 470; box.Text = value;
            box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(box);
            var browse = new Button { Text = "Browse…", Top = top - 1, Left = 610, Width = 90 };
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
            var browse = new Button { Text = "Browse…", Top = top - 1, Left = 610, Width = 90 };
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
