using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Rp1PrPicker
{
    internal sealed class ConflictForm : Form
    {
        readonly Pipeline.ConflictContext _ctx;
        readonly RadioButton _rbResolve;
        readonly RadioButton _rbSkip;
        readonly RadioButton _rbDrop;
        readonly CheckedListBox _dropList;

        public Pipeline.ConflictDecision Decision { get; private set; }

        public ConflictForm(Pipeline.ConflictContext ctx)
        {
            _ctx = ctx;
            Text = $"Merge conflict — PR #{ctx.Failing.Number}";
            Width = 700;
            Height = 560;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;

            var pad = 10;
            int y = pad;

            var header = new Label
            {
                Left = pad, Top = y, Width = Width - 2 * pad - 16, Height = 40,
                Text = $"PR #{ctx.Failing.Number} ({ctx.Failing.HeadRef}) could not be merged cleanly.\n{ctx.Failing.Title}",
                Font = new Font(Font, FontStyle.Bold),
            };
            Controls.Add(header);
            y += header.Height + 4;

            var filesLabel = new Label
            {
                Left = pad, Top = y, Width = Width - 2 * pad - 16, Height = 16,
                Text = $"Conflicting files ({ctx.ConflictingFiles.Count}):",
            };
            Controls.Add(filesLabel);
            y += filesLabel.Height + 2;

            var filesBox = new TextBox
            {
                Left = pad, Top = y, Width = Width - 2 * pad - 16, Height = 60,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Text = string.Join("\r\n", ctx.ConflictingFiles),
                Font = new Font(FontFamily.GenericMonospace, 9),
            };
            Controls.Add(filesBox);
            y += filesBox.Height + 8;

            var toolName = string.IsNullOrWhiteSpace(AppConfig.MergeTool) ? "git mergetool" : AppConfig.MergeTool;
            _rbResolve = new RadioButton
            {
                Left = pad, Top = y, Width = Width - 2 * pad - 16, Height = 24,
                Text = $"Resolve now in a graphical merge tool ({toolName}) — remembered for next time",
                Checked = true,
            };
            Controls.Add(_rbResolve);
            y += _rbResolve.Height + 2;

            _rbSkip = new RadioButton
            {
                Left = pad, Top = y, Width = Width - 2 * pad - 16, Height = 24,
                Text = $"Skip PR #{ctx.Failing.Number} (keep the build as-is, drop this PR)",
            };
            Controls.Add(_rbSkip);
            y += _rbSkip.Height + 2;

            _rbDrop = new RadioButton
            {
                Left = pad, Top = y, Width = Width - 2 * pad - 16, Height = 24,
                Text = $"Keep PR #{ctx.Failing.Number} and drop the earlier PR(s) it conflicts with:",
            };
            Controls.Add(_rbDrop);
            y += _rbDrop.Height + 2;

            _dropList = new CheckedListBox
            {
                Left = pad + 20, Top = y, Width = Width - 2 * pad - 40, Height = 160,
                CheckOnClick = true, Enabled = false,
            };
            foreach (var m in ctx.MergedSoFar)
                _dropList.Items.Add(new PrItem(m), true);   // default: drop all earlier conflictors
            if (ctx.MergedSoFar.Count == 0)
                _dropList.Items.Add(new BlankItem("(no earlier PRs touched these files — can't auto-identify; skip is the only sensible choice)"), false);
            Controls.Add(_dropList);
            y += _dropList.Height + 10;

            _rbSkip.CheckedChanged += (s, e) => _dropList.Enabled = _rbDrop.Checked;
            _rbDrop.CheckedChanged += (s, e) => _dropList.Enabled = _rbDrop.Checked;

            var ok = new Button { Left = Width - 200, Top = y, Width = 80, Height = 26, Text = "Apply", DialogResult = DialogResult.OK };
            var cancel = new Button { Left = Width - 110, Top = y, Width = 80, Height = 26, Text = "Cancel", DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                Decision = BuildDecision();
                if (Decision == null) { DialogResult = DialogResult.None; return; }
            };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        Pipeline.ConflictDecision BuildDecision()
        {
            if (_rbResolve.Checked) return new Pipeline.ConflictDecision { Choice = Pipeline.ConflictChoice.ResolveManually };
            if (_rbSkip.Checked) return new Pipeline.ConflictDecision { Choice = Pipeline.ConflictChoice.Skip };
            var drops = new List<int>();
            foreach (var item in _dropList.CheckedItems)
                if (item is PrItem pi) drops.Add(pi.Pr.Number);
            if (drops.Count == 0)
            {
                MessageBox.Show(this, "Pick at least one earlier PR to drop, or choose Skip.", "Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            return new Pipeline.ConflictDecision { Choice = Pipeline.ConflictChoice.DropEarlier, PrsToDrop = drops };
        }

        sealed class PrItem
        {
            public readonly PrInfo Pr;
            public PrItem(PrInfo pr) { Pr = pr; }
            public override string ToString() => Pr.DisplayLabel;
        }
        sealed class BlankItem
        {
            readonly string _t;
            public BlankItem(string t) { _t = t; }
            public override string ToString() => _t;
        }
    }
}
