using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace GeoSimplifier.SolidWorks.UI
{
    /// <summary>
    /// A small progress window in the middle of the SOLIDWORKS window (the status bar's progress bar went
    /// unnoticed). The wrap runs on SOLIDWORKS' own thread and keeps it busy, so the window lives on a thread of
    /// its own, where it repaints by itself; updates are posted to it. It appears only for computations that take
    /// longer than a moment, never takes the focus, and closes itself when disposed.
    /// <para>The owner window is only used to place it.</para>
    /// </summary>
    internal sealed class ProgressWindow : IDisposable
    {
        private const int ShowAfterMs = 700;

        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim();
        private Form _form;
        private Label _label;
        private ProgressBar _bar;
        private volatile bool _closed;

        public ProgressWindow(IntPtr owner, string title)
        {
            _thread = new Thread(() => Run(owner, title)) { IsBackground = true, Name = "GeoSimplifier progress" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(2000);
        }

        private void Run(IntPtr owner, string title)
        {
            try
            {
                _form = new NoActivateForm
                {
                    Text = "Shrink Wrap",
                    FormBorderStyle = FormBorderStyle.FixedToolWindow,
                    ControlBox = false,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    ClientSize = new Size(380, 70),
                    // Not owned by the SOLIDWORKS window: a window owned across threads shares that thread's input
                    // queue and would stall while SOLIDWORKS is busy. Topmost keeps it above SOLIDWORKS instead.
                    TopMost = true,
                };
                _label = new Label { Text = title, AutoSize = false, Bounds = new Rectangle(12, 10, 356, 20) };
                _bar = new ProgressBar { Minimum = 0, Maximum = 100, Bounds = new Rectangle(12, 36, 356, 20) };
                _form.Controls.Add(_label);
                _form.Controls.Add(_bar);
                if (GetWindowRect(owner, out var r))
                    _form.Location = new Point((r.Left + r.Right - _form.Width) / 2, (r.Top + r.Bottom - _form.Height) / 2);
                var show = new System.Windows.Forms.Timer { Interval = ShowAfterMs };
                show.Tick += (s, e) =>
                {
                    show.Stop();
                    if (!_closed)
                        _form.Show();
                };
                show.Start();
                // The handle is created without showing the window, so updates can be posted to it at once.
                var unused = _form.Handle;
                _ready.Set();
                Application.Run();
            }
            catch (Exception ex)
            {
                PageLog.Write("İlerleme penceresi hatası: " + ex.Message);
                _ready.Set();
            }
        }

        public void Update(string title, int percent)
        {
            Post(() =>
            {
                _label.Text = title;
                _bar.Value = Math.Max(0, Math.Min(100, percent));
            });
        }

        public void Dispose()
        {
            _closed = true;
            Post(() =>
            {
                _form.Close();
                _form.Dispose();
                Application.ExitThread();
            });
        }

        private void Post(Action action)
        {
            var form = _form;
            if (form == null || !form.IsHandleCreated)
                return;
            try { form.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        private sealed class NoActivateForm : Form
        {
            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_NOACTIVATE = 0x08000000;
                    var p = base.CreateParams;
                    p.ExStyle |= WS_EX_NOACTIVATE;
                    return p;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    }
}
