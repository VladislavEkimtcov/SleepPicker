using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SleepPicker
{
    /// <summary>
    /// A window that is only ever looked at: never activated, never clicked (clicks fall
    /// through to whatever is underneath), never in the taskbar or Alt+Tab.
    /// </summary>
    internal class OverlayForm : Form
    {
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        public OverlayForm(Rectangle bounds, double opacity)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Opacity = opacity;
            Bounds = bounds;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOPMOST;
                return cp;
            }
        }
    }

    /// <summary>
    /// The trigger zones drawn for one second after the sensitivity or distance is changed.
    /// Each zone is drawn at its true size, and fills from the bottom up over the rest time,
    /// so both settings are visible at once: how wide the band is, and how long the pointer
    /// has to stay in it -- the moment a bar is full is the moment a resting pointer would
    /// switch.
    /// </summary>
    internal sealed class EdgePreview : IDisposable
    {
        private const int VisibleMilliseconds = 1000;
        private const int FrameMilliseconds = 15;

        private readonly Timer _frame = new Timer();
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly List<ZoneForm> _forms = new List<ZoneForm>();
        private int _dwellMilliseconds;

        public EdgePreview()
        {
            _frame.Interval = FrameMilliseconds;
            _frame.Tick += OnFrame;
        }

        public void Show(int dwellMilliseconds, int distancePixels, int topDistancePixels)
        {
            Close();
            _dwellMilliseconds = Math.Max(1, dwellMilliseconds);
            foreach (Rectangle zone in EdgeSwitch.Zones(distancePixels, topDistancePixels))
            {
                ZoneForm form = new ZoneForm(zone);
                _forms.Add(form);
                form.Show();
            }
            _clock.Restart();
            _frame.Start();
        }

        private void OnFrame(object sender, EventArgs e)
        {
            long elapsed = _clock.ElapsedMilliseconds;
            if (elapsed >= VisibleMilliseconds)
            {
                Close();
                return;
            }
            float fill = Math.Min(1f, elapsed / (float)_dwellMilliseconds);
            foreach (ZoneForm form in _forms)
            {
                form.SetFill(fill);
            }
        }

        private void Close()
        {
            _frame.Stop();
            foreach (ZoneForm form in _forms)
            {
                form.Close();
                form.Dispose();
            }
            _forms.Clear();
        }

        public void Dispose()
        {
            Close();
            _frame.Dispose();
        }

        private sealed class ZoneForm : OverlayForm
        {
            private static readonly Color Empty = Color.FromArgb(40, 60, 90);
            private static readonly Color Full = Color.FromArgb(0, 120, 215);  // Windows' default accent
            private static readonly Color Done = Color.FromArgb(90, 180, 255);
            private float _fill;

            public ZoneForm(Rectangle bounds)
                : base(bounds, 0.6)
            {
                BackColor = Empty;
            }

            public void SetFill(float fill)
            {
                if (fill == _fill)
                {
                    return;
                }
                _fill = fill;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                int filled = (int)Math.Round(ClientSize.Height * _fill);
                using (SolidBrush brush = new SolidBrush(_fill >= 1f ? Done : Full))
                {
                    e.Graphics.FillRectangle(brush, 0, ClientSize.Height - filled, ClientSize.Width, filled);
                }
            }
        }
    }

    /// <summary>
    /// The developer readout (<see cref="Settings.DevReadout"/>): one line of text near the
    /// bottom-left of the primary screen, redrawn a few times a second. It is made afresh
    /// whenever the desktop has changed under it, because a window stays on the desktop it
    /// was first shown on.
    /// </summary>
    internal sealed class DevReadout : IDisposable
    {
        private ReadoutForm _form;
        private Guid _desktop;
        private readonly VirtualDesktops.Shell _shell = new VirtualDesktops.Shell();

        public void Show(string text)
        {
            Guid desktop;
            _shell.TryGetCurrent(out desktop);
            if (_form == null || desktop != _desktop)
            {
                Dispose();
                _desktop = desktop;
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                _form = new ReadoutForm(new Rectangle(work.Left + 8, work.Bottom - 30, 760, 22));
                _form.Show();
            }
            _form.SetText(text);
        }

        public void Dispose()
        {
            if (_form != null)
            {
                _form.Close();
                _form.Dispose();
                _form = null;
            }
        }

        private sealed class ReadoutForm : OverlayForm
        {
            private string _text = "";

            public ReadoutForm(Rectangle bounds)
                : base(bounds, 0.85)
            {
                BackColor = Color.Black;
            }

            public void SetText(string text)
            {
                if (text == _text)
                {
                    return;
                }
                _text = text;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                using (Font font = new Font(FontFamily.GenericMonospace, 9f))
                {
                    e.Graphics.DrawString(_text, font, Brushes.Lime, 4, 3);
                }
            }
        }
    }

    /// <summary>
    /// A row of dots, one per desktop, shown for a moment after each switch. Windows 10
    /// shows nothing at all once animations are off, and over VNC an unchanged screen is
    /// otherwise indistinguishable from "already on the last desktop".
    /// </summary>
    internal sealed class DesktopDots : IDisposable
    {
        /// <summary>Explorer records the new current desktop shortly after switching.</summary>
        private const int SettleMilliseconds = 120;
        private const int VisibleMilliseconds = 650;

        private readonly Timer _settle = new Timer();
        private readonly Timer _hide = new Timer();
        private DotsForm _form;

        public DesktopDots()
        {
            _settle.Interval = SettleMilliseconds;
            _settle.Tick += OnSettled;
            _hide.Interval = VisibleMilliseconds;
            _hide.Tick += OnHide;
        }

        public void ShowSoon()
        {
            _settle.Stop();
            _settle.Start();
        }

        private void OnSettled(object sender, EventArgs e)
        {
            _settle.Stop();
            int index;
            int count;
            if (!VirtualDesktops.TryGetPosition(out index, out count))
            {
                return;
            }

            // A fresh window every time: a window belongs to the desktop it was first shown
            // on, so a reused one would be left behind on the desktop just left.
            Close();
            EdgeSwitch.NativeMethods.POINT pt;
            EdgeSwitch.NativeMethods.GetCursorPos(out pt);
            _form = new DotsForm(index, count, pt);
            _form.Show();
            _hide.Stop();
            _hide.Start();
        }

        private void OnHide(object sender, EventArgs e)
        {
            _hide.Stop();
            Close();
        }

        private void Close()
        {
            if (_form == null)
            {
                return;
            }
            _form.Close();
            _form.Dispose();
            _form = null;
        }

        public void Dispose()
        {
            _settle.Dispose();
            _hide.Dispose();
            Close();
        }

        private sealed class DotsForm : OverlayForm
        {
            private readonly int _index;
            private readonly int _count;
            private readonly int _dot;
            private readonly int _gap;
            private readonly int _pad;

            public DotsForm(int index, int count, EdgeSwitch.NativeMethods.POINT at)
                : base(PlaceNear(count, at), 0.88)
            {
                _index = index;
                _count = count;
                float scale = ScaleAt(at);
                _dot = Scaled(10, scale);
                _gap = Scaled(12, scale);
                _pad = Scaled(14, scale);
                BackColor = Color.FromArgb(28, 28, 30);
                using (GraphicsPath path = Pill(new Rectangle(Point.Empty, Size)))
                {
                    Region = new Region(path);
                }
            }

            /// <summary>Centred near the bottom of the work area of the pointer's monitor.</summary>
            private static Rectangle PlaceNear(int count, EdgeSwitch.NativeMethods.POINT at)
            {
                float scale = ScaleAt(at);
                int dot = Scaled(10, scale);
                int gap = Scaled(12, scale);
                int pad = Scaled(14, scale);
                int width = pad * 2 + count * dot + (count - 1) * gap;
                int height = pad * 2 + dot;
                Rectangle work = Screen.FromPoint(new Point(at.X, at.Y)).WorkingArea;
                return new Rectangle(work.Left + (work.Width - width) / 2, work.Bottom - height - Scaled(48, scale), width, height);
            }

            private static float ScaleAt(EdgeSwitch.NativeMethods.POINT at)
            {
                IntPtr monitor = EdgeSwitch.NativeMethods.MonitorFromPoint(at, EdgeSwitch.NativeMethods.MONITOR_DEFAULTTONEAREST);
                uint dpiX;
                uint dpiY;
                return EdgeSwitch.NativeMethods.GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) == 0 ? dpiX / 96f : 1f;
            }

            private static int Scaled(int value, float scale)
            {
                return (int)Math.Round(value * scale);
            }

            private static GraphicsPath Pill(Rectangle r)
            {
                int d = r.Height;
                GraphicsPath path = new GraphicsPath();
                path.AddArc(r.Left, r.Top, d, d, 90, 180);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
                path.CloseFigure();
                return path;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush on = new SolidBrush(Color.White))
                using (SolidBrush off = new SolidBrush(Color.FromArgb(90, 90, 96)))
                {
                    for (int i = 0; i < _count; i++)
                    {
                        e.Graphics.FillEllipse(i == _index ? on : off, _pad + i * (_dot + _gap), _pad, _dot, _dot);
                    }
                }
            }
        }
    }
}
