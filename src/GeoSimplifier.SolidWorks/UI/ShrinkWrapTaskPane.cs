using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GeoSimplifier.Core.ShrinkWrap;

namespace GeoSimplifier.SolidWorks.UI
{
    [ComVisible(false)]
    public sealed class ShrinkWrapTaskPane : UserControl
    {
        private readonly ShrinkWrapService _service;
        private readonly NumericUpDown _resolution;
        private readonly NumericUpDown _gap;
        private readonly Button _run;
        private readonly Label _status;

        public ShrinkWrapTaskPane(ShrinkWrapService service)
        {
            _service = service;

            var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var title = new Label { Text = "Shrink Wrap", Font = new Font(Font, FontStyle.Bold), AutoSize = true };
            layout.Controls.Add(title, 0, 0);
            layout.SetColumnSpan(title, 2);

            _resolution = new NumericUpDown { Minimum = 8, Maximum = 512, Value = 96, Dock = DockStyle.Fill };
            AddRow(layout, "Resolution (voxels on longest side)", _resolution);

            _gap = new NumericUpDown { Minimum = 0, Maximum = 32, Value = 2, Dock = DockStyle.Fill };
            AddRow(layout, "Gap closing (voxels)", _gap);

            _run = new Button { Text = "Wrap active model", Dock = DockStyle.Fill, Height = 28 };
            _run.Click += (s, e) => RunFromCommand();
            layout.Controls.Add(_run);
            layout.SetColumnSpan(_run, 2);

            _status = new Label { AutoSize = true, MaximumSize = new Size(260, 0), Text = "Open a part or assembly." };
            layout.Controls.Add(_status);
            layout.SetColumnSpan(_status, 2);

            Controls.Add(layout);
        }

        public void RunFromCommand()
        {
            var options = new ShrinkWrapOptions
            {
                Resolution = (int)_resolution.Value,
                GapClosingVoxels = (int)_gap.Value,
            };

            _run.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                SetStatus(_service.Run(options, SetStatus));
            }
            catch (Exception ex)
            {
                SetStatus("Failed: " + ex.Message);
            }
            finally
            {
                Cursor = Cursors.Default;
                _run.Enabled = true;
            }
        }

        private void SetStatus(string text)
        {
            _status.Text = text;
            _status.Refresh();
        }

        private static void AddRow(TableLayoutPanel layout, string label, Control control)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
            layout.Controls.Add(control);
        }
    }
}
