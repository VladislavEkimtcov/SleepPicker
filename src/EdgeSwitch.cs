using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SleepPicker
{
    /// <summary>
    /// Desktop switching for a session driven over VNC, where neither Win+Ctrl+arrows nor
    /// trackpad gestures arrive:
    ///   - let the pointer come to rest within <see cref="DistancePixels"/> of the left or
    ///     right edge of the screen for <see cref="DwellMilliseconds"/>: previous / next
    ///     desktop, once per rest
    ///   - fling the pointer up into the zone along the top (<see cref="TopDistancePixels"/>
    ///     thick, because the viewer's own title bar and toolbar sit above it): Task View
    ///   - scroll over the taskbar: previous / next desktop, one per gesture
    ///   - rest on a taskbar button: peek at its window (<see cref="TaskbarPeek"/>)
    /// The first three are the "edges" half, the last the "peek" half; either can be off.
    ///
    /// The zone is a band rather than the last pixel because a small VNC viewer window
    /// lets the Mac's pointer fly off its side before the remote one reaches the edge:
    /// Windows is told the last position inside the viewer, which may be dozens of pixels
    /// short, and then hears nothing more. That silence is the rest the switch waits for.
    /// The clock restarts on every real movement, so passing through the zone or moving
    /// about in it never switches, and every new rest after a movement can switch again.
    ///
    /// The pointer is followed by reading its position, not by a mouse hook. Windows does
    /// not deliver low-level hook events to a non-elevated program while an elevated
    /// window is in front, so a hook goes deaf the moment a desktop switch brings an admin
    /// console forward -- and reinstalling it changes nothing. Reading the position works
    /// whatever is in front. It is read every 16 ms while the pointer is moving or near a
    /// trigger, and every 100 ms once it has been still for a second.
    ///
    /// The hook remains for the one thing a position cannot tell: the wheel. Its thread
    /// does nothing else, so it always answers in time; scrolling over the taskbar is the
    /// only feature that pauses while an elevated window is in front.
    /// </summary>
    internal sealed class EdgeSwitch : IDisposable
    {
        public const int DefaultDwellMilliseconds = 300;
        public const int DefaultDistancePixels = 24;
        public const int DefaultTopDistancePixels = 96;

        /// <summary>Top and bottom share of each side edge that never triggers, in percent.
        /// Keeps the close button of a maximized window and the taskbar corners out of it.</summary>
        public const int DeadBandPercent = 12;

        /// <summary>Share of the top edge's width, at each end, that never triggers, in
        /// percent: the close button of a maximized window and the File menu live there.</summary>
        public const int TopCornerPercent = 15;

        /// <summary>
        /// The top zone is thick, and the top of the screen is where tabs, menus and title
        /// bars are, so resting there is not enough: the pointer must have been travelling
        /// up at least this fast (pixels per second) when it stopped. A fling off the top of
        /// a VNC viewer arrives faster; aiming at a tab slows down below it before stopping.
        /// </summary>
        private const int SwipeSpeedPixelsPerSecond = 500;
        private const int SwipeWindowMilliseconds = 200;

        /// <summary>How long the pointer has to rest on a taskbar button before the peek, and
        /// on the taskbar before an open Task View is closed.</summary>
        private const int PeekHoverMilliseconds = 150;

        /// <summary>
        /// A button that has been down this long is taken as a release that never arrived,
        /// not a drag: let go of the mouse outside a VNC viewer and Windows may never hear
        /// the button come up. Without this, one lost release disarms every trigger until
        /// the next click anywhere.
        /// </summary>
        private const int StuckButtonMilliseconds = 3000;

        /// <summary>No movement, peek or pending check for this long counts as quiet:
        /// the moment it is safe for the menu thread to rebuild this object.</summary>
        private const int QuietMilliseconds = 3000;

        /// <summary>Smaller movements than this are jitter: they do not restart the rest clock.</summary>
        private const int JitterPixels = 3;

        /// <summary>Wheel events closer together than this belong to one gesture, so a
        /// trackpad swipe still coasting moves one desktop, not five.</summary>
        private const int WheelQuietMilliseconds = 250;

        private const int FastSampleMilliseconds = 16;
        private const int IdleSampleMilliseconds = 100;
        private const int IdleAfterMilliseconds = 1000;

        /// <summary>The taskbar rectangles are re-read this often; the wheel hook is replaced
        /// every tenth time, in case Windows dropped it.</summary>
        private const int HousekeepingMilliseconds = 1000;

        /// <summary>A typed chord is checked at these two moments and, if the desktop has not
        /// moved, completed through Explorer. Twice, TF2-style: repeating it is harmless,
        /// because the target was chosen once, before the chord.</summary>
        private const int VerifyMilliseconds = 350;
        private const int SecondVerifyMilliseconds = 450;

        /// <summary>The second Task View check waits longer: the fallback starts a process,
        /// and launching it again while the first is still starting would close it.</summary>
        private const int TaskViewVerifyMilliseconds = 500;
        private const int SecondTaskViewVerifyMilliseconds = 1000;

        /// <summary>The edge code of the top zone; the sides are -1 and +1.</summary>
        private const int TopEdge = 2;

        private readonly Thread _hookThread;
        private readonly Thread _workerThread;
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private Control _hookSink;
        private Control _workerSink;

        // Settings, written by the menu thread.
        private volatile int _dwellMilliseconds;
        private volatile int _distancePixels;
        private volatile int _topDistancePixels;
        private volatile bool _edgesEnabled;
        private volatile bool _peekEnabled;

        /// <summary>When the worker last had something in hand; read by <see cref="IsQuiet"/>.</summary>
        private volatile int _lastBusyTick = Environment.TickCount;

        /// <summary>Where the taskbars are: written by the worker, read by the hook, which
        /// must not ask Windows which window is under the pointer (that waits on the window).</summary>
        private volatile NativeMethods.RECT[] _taskbarRects = new NativeMethods.RECT[0];

        // Hook thread only.
        private NativeMethods.LowLevelMouseProc _proc; // held so the GC cannot collect the delegate
        private IntPtr _hook;
        private int _lastWheelTick;
        private bool _wheelGestureUsed;
        private int _wheelAccumulated;
        private Action<int> _go;

        // Worker thread only.
        private System.Windows.Forms.Timer _sample;
        private System.Windows.Forms.Timer _housekeeping;
        private System.Windows.Forms.Timer _verify;
        private System.Windows.Forms.Timer _taskViewVerify;
        private DesktopDots _dots;
        private TaskbarPeek _peek;
        private VirtualDesktops.Shell _shell;
        private NativeMethods.POINT _anchor;
        private readonly int[] _recentTicks = new int[32];
        private readonly int[] _recentYs = new int[32];
        private int _recentNext;
        private int _lastMoveTick;
        private int _moveSerial;
        private int _upSpeed;          // pixels per second, positive = upwards, at the last movement
        private bool _clickedInZone;
        private int _buttonDownSince;  // tick the current press began, 0 while no button is down
        // Each starts equal to _moveSerial, so a fresh instance never fires on a pointer that
        // was already resting somewhere when it started -- only after the next movement.
        private int _firedSerial;
        private int _peekSerial;
        private int _taskViewCloseSerial;
        private DevReadout _readout;
        private int _readoutTicks;
        private int _housekeepingTicks;
        private int _verifyPass;
        private int _taskViewVerifyPass;
        private Guid _verifyFrom;
        private object _verifyTarget;

        public EdgeSwitch(int dwellMilliseconds, int distancePixels, int topDistancePixels,
            bool edgesEnabled, bool peekEnabled)
        {
            _dwellMilliseconds = dwellMilliseconds;
            _distancePixels = distancePixels;
            _topDistancePixels = topDistancePixels;
            _edgesEnabled = edgesEnabled;
            _peekEnabled = peekEnabled;

            _workerThread = StartThread(WorkerMain, "Edge switch worker", ThreadPriority.Normal);
            _hookThread = StartThread(HookMain, "Edge switch wheel hook", ThreadPriority.Highest);
        }

        private Thread StartThread(ThreadStart main, string name, ThreadPriority priority)
        {
            Thread thread = new Thread(main);
            thread.Name = name;
            thread.IsBackground = true;
            thread.Priority = priority;
            thread.SetApartmentState(ApartmentState.STA);
            _ready.Reset();
            thread.Start();
            _ready.WaitOne();
            return thread;
        }

        public int DwellMilliseconds
        {
            get { return _dwellMilliseconds; }
            set { _dwellMilliseconds = value; }
        }

        public int DistancePixels
        {
            get { return _distancePixels; }
            set { _distancePixels = value; }
        }

        public int TopDistancePixels
        {
            get { return _topDistancePixels; }
            set { _topDistancePixels = value; }
        }

        /// <summary>The side and top zones and the taskbar scroll.</summary>
        public bool EdgesEnabled
        {
            get { return _edgesEnabled; }
            set { _edgesEnabled = value; }
        }

        /// <summary>The taskbar-button peek. Turning it off ends a peek in progress at the next sample.</summary>
        public bool PeekEnabled
        {
            get { return _peekEnabled; }
            set { _peekEnabled = value; }
        }

        /// <summary>Nothing in hand -- no movement, peek or pending check -- for a few seconds.</summary>
        public bool IsQuiet
        {
            get { return unchecked(Environment.TickCount - _lastBusyTick) > QuietMilliseconds; }
        }

        // ---------------------------------------------------------------- worker thread

        private void WorkerMain()
        {
            _workerSink = CreateSink();
            _shell = new VirtualDesktops.Shell();
            _dots = new DesktopDots();
            _peek = new TaskbarPeek();
            _sample = NewTimer(FastSampleMilliseconds, OnSample);
            _housekeeping = NewTimer(HousekeepingMilliseconds, OnHousekeeping);
            _verify = NewTimer(VerifyMilliseconds, OnVerify);
            _taskViewVerify = NewTimer(TaskViewVerifyMilliseconds, OnTaskViewVerify);
            RefreshTaskbarRects();
            NativeMethods.GetCursorPos(out _anchor);
            EdgeLog.Enabled = Settings.DevReadout;
            if (Settings.DevReadout)
            {
                _readout = new DevReadout();
            }
            EdgeLog.Write("started: edges=" + _edgesEnabled + " peek=" + _peekEnabled + " dwell=" + _dwellMilliseconds +
                " distance=" + _distancePixels + " top=" + _topDistancePixels);
            _sample.Start();
            _housekeeping.Start();
            _ready.Set();

            Application.Run();

            Guarded(_peek.End); // never leave the screen glassed over
            if (_readout != null)
            {
                _readout.Dispose();
            }
            _sample.Dispose();
            _housekeeping.Dispose();
            _verify.Dispose();
            _taskViewVerify.Dispose();
            _dots.Dispose();
            _workerSink.Dispose();
        }

        private static System.Windows.Forms.Timer NewTimer(int interval, EventHandler tick)
        {
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = interval;
            timer.Tick += tick;
            return timer;
        }

        private void OnSample(object sender, EventArgs e)
        {
            Guarded(Sample);
        }

        private void Sample()
        {
            NativeMethods.POINT pt;
            if (!NativeMethods.GetCursorPos(out pt))
            {
                return; // the secure desktop (UAC, lock screen): nothing to follow
            }
            int now = Environment.TickCount;
            _recentTicks[_recentNext] = now;
            _recentYs[_recentNext] = pt.Y;
            _recentNext = (_recentNext + 1) % _recentTicks.Length;
            if (Math.Abs(pt.X - _anchor.X) > JitterPixels || Math.Abs(pt.Y - _anchor.Y) > JitterPixels)
            {
                _anchor = pt;
                _upSpeed = UpwardSpeed(now, pt.Y);
                _lastMoveTick = now;
                _moveSerial++;
            }

            bool held = AnyButtonDown();
            bool pressed = held && _buttonDownSince == 0;
            if (pressed)
            {
                _buttonDownSince = now;
            }
            else if (!held)
            {
                _buttonDownSince = 0;
            }
            bool buttons = held && unchecked(now - _buttonDownSince) < StuckButtonMilliseconds;

            int edge = _edgesEnabled ? EdgeAt(pt, _distancePixels, _topDistancePixels) : 0;
            bool overTaskbar = _peekEnabled && IsOverTaskbar(pt, 0);
            // Task View closes from the taskbar or just above it: a fling off the bottom of
            // a VNC viewer stops short of the screen's edge, as one off its side does.
            bool nearTaskbar = _edgesEnabled && IsOverTaskbar(pt, _distancePixels);
            int rested = unchecked(now - _lastMoveTick);

            // A button pressed in the zone -- a maximized window's scrollbar, say -- is the
            // user doing something else there: no switch until the pointer has left it. Only
            // a press that starts in the zone counts, so a stuck button cannot disarm it.
            if (edge == 0)
            {
                _clickedInZone = false;
            }
            else if (pressed)
            {
                _clickedInZone = true;
            }

            // A click on a taskbar button activates its window; the peek has done its job.
            if (_peek.Active && (!overTaskbar || buttons))
            {
                EdgeLog.Write("peek ended: " + (buttons ? "button pressed" : "left the taskbar"));
                _peek.End();
            }

            if (rested < QuietMilliseconds || _peek.Active || _verifyTarget != null)
            {
                _lastBusyTick = now;
            }
            if (_readout != null && ++_readoutTicks % 6 == 0)
            {
                _readout.Show("edge=" + edge + " rest=" + Math.Min(rested, 9999) + "ms btn=" + (held ? (buttons ? "down" : "STUCK") : "up") +
                    " clicked=" + (_clickedInZone ? 1 : 0) + " armed=" + (_moveSerial != _firedSerial ? 1 : 0) +
                    " | taskbar=" + (overTaskbar || nearTaskbar ? 1 : 0) + " peek=" + (_peek.Active ? 1 : 0) +
                    " | up=" + _upSpeed + "px/s");
            }

            if (nearTaskbar && _moveSerial != _taskViewCloseSerial && rested >= PeekHoverMilliseconds && !buttons)
            {
                _taskViewCloseSerial = _moveSerial;
                if (VirtualDesktops.IsTaskViewInFront())
                {
                    EdgeLog.Write("taskbar rest with Task View open: closing it");
                    VirtualDesktops.TypeTaskViewChord();
                    return;
                }
            }

            int interval = edge != 0 || overTaskbar || rested < IdleAfterMilliseconds
                ? FastSampleMilliseconds
                : IdleSampleMilliseconds;
            if (_sample.Interval != interval)
            {
                _sample.Interval = interval;
            }

            if (overTaskbar)
            {
                if (_moveSerial != _peekSerial && rested >= PeekHoverMilliseconds && !buttons)
                {
                    _peekSerial = _moveSerial;
                    EdgeLog.Write("peek: " + _peek.Update(pt));
                }
                return;
            }

            if (edge == 0 || _moveSerial == _firedSerial || _clickedInZone || buttons || rested < _dwellMilliseconds)
            {
                return;
            }
            if (edge == TopEdge && _upSpeed < SwipeSpeedPixelsPerSecond)
            {
                return; // came to rest near the top without flinging there: a tab, a menu
            }
            _firedSerial = _moveSerial;
            EdgeLog.Write("fire: edge=" + edge + " rested=" + rested + "ms up=" + _upSpeed + "px/s");
            if (edge == TopEdge)
            {
                ToggleTaskView();
            }
            else
            {
                Go(edge);
            }
        }

        /// <summary>How fast the pointer has been rising just now, measured against each
        /// recent sample still inside <see cref="SwipeWindowMilliseconds"/>.</summary>
        private int UpwardSpeed(int now, int y)
        {
            int best = 0;
            for (int i = 0; i < _recentTicks.Length; i++)
            {
                int age = unchecked(now - _recentTicks[i]);
                if (age >= 15 && age <= SwipeWindowMilliseconds)
                {
                    best = Math.Max(best, (_recentYs[i] - y) * 1000 / age);
                }
            }
            return best;
        }

        /// <summary>
        /// Moves one desktop. The chord is typed when it will be heard and checked twice
        /// after; when it would be lost, or turns out to have been, Explorer is asked
        /// directly. On the last desktop in that direction only the dots are shown, so the
        /// lack of a switch still gets an answer.
        /// </summary>
        private void Go(int direction)
        {
            Guarded(delegate
            {
                _verify.Stop();
                Guid from;
                bool shellAvailable = _shell.TryGetCurrent(out from);
                object target = null;
                if (shellAvailable && !_shell.TryGetAdjacent(direction, out target))
                {
                    EdgeLog.Write("go " + direction + ": already on the last desktop that way");
                }
                else if (shellAvailable && VirtualDesktops.ChordWouldBeLost())
                {
                    EdgeLog.Write("go " + direction + ": through Explorer, ok=" + _shell.TrySwitch(target));
                }
                else
                {
                    EdgeLog.Write("go " + direction + ": chord");
                    VirtualDesktops.TypeChord(direction);
                    _lastBusyTick = Environment.TickCount;
                    if (shellAvailable)
                    {
                        _verifyFrom = from;
                        _verifyTarget = target;
                        _verifyPass = 0;
                        _verify.Interval = VerifyMilliseconds;
                        _verify.Start();
                    }
                }
                _dots.ShowSoon();
            });
        }

        private void OnVerify(object sender, EventArgs e)
        {
            Guarded(delegate
            {
                _verify.Stop();
                Guid now;
                if (_verifyTarget != null && _shell.TryGetCurrent(out now) && now == _verifyFrom)
                {
                    EdgeLog.Write("verify " + _verifyPass + ": chord lost, through Explorer, ok=" + _shell.TrySwitch(_verifyTarget));
                    _dots.ShowSoon();
                }
                if (_verifyPass++ == 0)
                {
                    _verify.Interval = SecondVerifyMilliseconds;
                    _verify.Start();
                    return;
                }
                _verifyTarget = null;
            });
        }

        /// <summary>
        /// Win+Tab when it will be heard -- it also closes Task View when it is already
        /// open -- checked twice after; otherwise, or when it was not heard, Task View is
        /// opened through the shell instead.
        /// </summary>
        private void ToggleTaskView()
        {
            Guarded(delegate
            {
                _taskViewVerify.Stop();
                if (VirtualDesktops.IsTaskViewInFront())
                {
                    VirtualDesktops.TypeTaskViewChord();
                    return;
                }
                if (VirtualDesktops.ChordWouldBeLost())
                {
                    VirtualDesktops.LaunchTaskView();
                    return;
                }
                VirtualDesktops.TypeTaskViewChord();
                _taskViewVerifyPass = 0;
                _taskViewVerify.Interval = TaskViewVerifyMilliseconds;
                _taskViewVerify.Start();
            });
        }

        private void OnTaskViewVerify(object sender, EventArgs e)
        {
            Guarded(delegate
            {
                _taskViewVerify.Stop();
                if (VirtualDesktops.IsTaskViewInFront())
                {
                    return;
                }
                VirtualDesktops.LaunchTaskView();
                if (_taskViewVerifyPass++ == 0)
                {
                    _taskViewVerify.Interval = SecondTaskViewVerifyMilliseconds;
                    _taskViewVerify.Start();
                }
            });
        }

        private void OnHousekeeping(object sender, EventArgs e)
        {
            Guarded(delegate
            {
                RefreshTaskbarRects();
                if (++_housekeepingTicks % 10 == 0 && _hookSink != null && _hookSink.IsHandleCreated)
                {
                    _hookSink.BeginInvoke(new MethodInvoker(Install));
                }
            });
        }

        /// <summary>Re-reads where the taskbars are: they move with auto-hide, display
        /// changes and Explorer restarts.</summary>
        private void RefreshTaskbarRects()
        {
            List<NativeMethods.RECT> rects = new List<NativeMethods.RECT>();
            foreach (string className in new string[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                IntPtr window = IntPtr.Zero;
                while ((window = NativeMethods.FindWindowEx(IntPtr.Zero, window, className, null)) != IntPtr.Zero)
                {
                    NativeMethods.RECT rc;
                    if (NativeMethods.GetWindowRect(window, out rc))
                    {
                        rects.Add(rc);
                    }
                }
            }
            _taskbarRects = rects.ToArray();
        }

        // ---------------------------------------------------------------- hook thread (the wheel)

        private void HookMain()
        {
            _hookSink = CreateSink();
            _proc = HookProc;
            _go = Go;
            Install();
            _ready.Set();

            Application.Run();

            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            _hookSink.Dispose();
        }

        /// <summary>Installs a fresh hook before removing the old one, so there is no gap.</summary>
        private void Install()
        {
            IntPtr fresh = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc,
                NativeMethods.GetModuleHandle(null), 0);
            if (fresh == IntPtr.Zero)
            {
                return;
            }
            IntPtr old = _hook;
            _hook = fresh;
            if (old != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(old);
            }
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                int message = wParam.ToInt32();
                if (nCode >= 0 && _edgesEnabled &&
                    (message == NativeMethods.WM_MOUSEWHEEL || message == NativeMethods.WM_MOUSEHWHEEL))
                {
                    NativeMethods.MSLLHOOKSTRUCT info = (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                        lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));
                    if (IsOverTaskbar(info.pt, 0))
                    {
                        OnTaskbarWheel(message == NativeMethods.WM_MOUSEHWHEEL, (short)(info.mouseData >> 16));
                        return (IntPtr)1; // swallowed: the taskbar has no use for it
                    }
                }
            }
            catch (Exception)
            {
                // Never let anything escape into the system's input path.
            }
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private void OnTaskbarWheel(bool horizontal, int delta)
        {
            int now = Environment.TickCount;
            if (unchecked(now - _lastWheelTick) > WheelQuietMilliseconds)
            {
                _wheelGestureUsed = false;
                _wheelAccumulated = 0;
            }
            _lastWheelTick = now;
            if (_wheelGestureUsed)
            {
                return;
            }
            _wheelAccumulated += delta;
            if (Math.Abs(_wheelAccumulated) < 120)
            {
                return; // less than one detent (WHEEL_DELTA) so far
            }
            // Down or right means "next": under the Mac's natural scrolling, that is the
            // same swipe that moves to the Space on the right.
            int direction = horizontal
                ? (_wheelAccumulated > 0 ? 1 : -1)
                : (_wheelAccumulated < 0 ? 1 : -1);
            _wheelGestureUsed = true;
            _workerSink.BeginInvoke(_go, direction);
        }

        // ---------------------------------------------------------------- shared

        /// <summary>Runs a step and swallows whatever it throws: an exception escaping a timer
        /// would end in WinForms' unhandled-exception dialog, and one missed switch is the
        /// better failure.</summary>
        private static void Guarded(MethodInvoker step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                // Logged: a swallowed exception once hid that the peek never ran at all.
                EdgeLog.Write("error: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>An invisible control, created only for its handle, so other threads can
        /// hand work to this thread's message loop. It is never shown.</summary>
        private static Control CreateSink()
        {
            Control sink = new Control();
            sink.CreateControl();
            IntPtr unusedHandle = sink.Handle;
            return sink;
        }

        /// <summary>Whether the point is on a taskbar, or within <paramref name="above"/> pixels
        /// above one.</summary>
        private bool IsOverTaskbar(NativeMethods.POINT pt, int above)
        {
            NativeMethods.RECT[] rects = _taskbarRects;
            for (int i = 0; i < rects.Length; i++)
            {
                if (pt.X >= rects[i].Left && pt.X < rects[i].Right && pt.Y >= rects[i].Top - above && pt.Y < rects[i].Bottom)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// -1 in the zone along an outer left edge, +1 along an outer right edge,
        /// <see cref="TopEdge"/> in the zone along an outer top edge, else 0. An edge shared
        /// with another monitor is not an edge: the pointer crosses it.
        /// </summary>
        private static int EdgeAt(NativeMethods.POINT pt, int distance, int topDistance)
        {
            IntPtr monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            NativeMethods.MONITORINFO mi = new NativeMethods.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO));
            if (!NativeMethods.GetMonitorInfo(monitor, ref mi))
            {
                return 0;
            }
            NativeMethods.RECT rc = mi.rcMonitor;

            int corner = (rc.Right - rc.Left) * TopCornerPercent / 100;
            if (pt.Y < rc.Top + topDistance && pt.X >= rc.Left + corner && pt.X < rc.Right - corner &&
                IsOuter(pt.X, rc.Top - 1))
            {
                return TopEdge;
            }

            int dead = (rc.Bottom - rc.Top) * DeadBandPercent / 100;
            if (pt.Y < rc.Top + dead || pt.Y >= rc.Bottom - dead)
            {
                return 0;
            }
            if (pt.X < rc.Left + distance && IsOuter(rc.Left - 1, pt.Y))
            {
                return -1;
            }
            if (pt.X >= rc.Right - distance && IsOuter(rc.Right, pt.Y))
            {
                return 1;
            }
            return 0;
        }

        /// <summary>The trigger zones on every monitor, for drawing the preview.</summary>
        public static List<Rectangle> Zones(int distance, int topDistance)
        {
            List<Rectangle> zones = new List<Rectangle>();
            foreach (Screen screen in Screen.AllScreens)
            {
                Rectangle b = screen.Bounds;
                int dead = b.Height * DeadBandPercent / 100;
                int top = b.Top + dead;
                int height = b.Height - 2 * dead;
                int mid = top + height / 2;
                if (IsOuter(b.Left - 1, mid))
                {
                    zones.Add(new Rectangle(b.Left, top, distance, height));
                }
                if (IsOuter(b.Right, mid))
                {
                    zones.Add(new Rectangle(b.Right - distance, top, distance, height));
                }
                int corner = b.Width * TopCornerPercent / 100;
                if (IsOuter(b.Left + b.Width / 2, b.Top - 1))
                {
                    zones.Add(new Rectangle(b.Left + corner, b.Top, b.Width - 2 * corner, topDistance));
                }
            }
            return zones;
        }

        private static bool IsOuter(int x, int y)
        {
            return NativeMethods.MonitorFromPoint(new NativeMethods.POINT(x, y), NativeMethods.MONITOR_DEFAULTTONULL) == IntPtr.Zero;
        }

        private static bool AnyButtonDown()
        {
            return (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0
                || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RBUTTON) & 0x8000) != 0
                || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MBUTTON) & 0x8000) != 0;
        }

        public void Dispose()
        {
            // The hook first, so nothing is posted to a worker that has gone.
            Stop(_hookSink, _hookThread);
            Stop(_workerSink, _workerThread);
            _ready.Close();
        }

        private static void Stop(Control sink, Thread thread)
        {
            if (sink != null && sink.IsHandleCreated)
            {
                try
                {
                    sink.BeginInvoke(new MethodInvoker(Application.ExitThread));
                }
                catch (InvalidOperationException)
                {
                    // The handle went away in between: the thread is already ending.
                }
                thread.Join(2000);
            }
        }

        /// <summary>
        /// A short diagnostic trail, %LOCALAPPDATA%\SleepPicker\edge.log, kept under 64 KB
        /// by starting over. Written from the worker thread only, and only in developer mode
        /// (<see cref="Settings.DevReadout"/>): otherwise nothing touches the disk.
        /// </summary>
        internal static class EdgeLog
        {
            private static readonly string Path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"SleepPicker\edge.log");

            /// <summary>Set by each new switcher as it starts.</summary>
            public static volatile bool Enabled;

            public static void Write(string line)
            {
                if (!Enabled)
                {
                    return;
                }
                try
                {
                    System.IO.FileInfo file = new System.IO.FileInfo(Path);
                    file.Directory.Create();
                    if (file.Exists && file.Length > 64 * 1024)
                    {
                        file.Delete();
                    }
                    System.IO.File.AppendAllText(Path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
                }
                catch (Exception)
                {
                    // Diagnostics only; never worth a failure.
                }
            }
        }

        internal static class NativeMethods
        {
            public const int WH_MOUSE_LL = 14;
            public const int WM_MOUSEWHEEL = 0x020A;
            public const int WM_MOUSEHWHEEL = 0x020E;
            public const uint MONITOR_DEFAULTTONULL = 0;
            public const uint MONITOR_DEFAULTTONEAREST = 2;
            public const uint GA_ROOT = 2;
            public const int VK_LBUTTON = 0x01;
            public const int VK_RBUTTON = 0x02;
            public const int VK_MBUTTON = 0x04;

            public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int X;
                public int Y;

                public POINT(int x, int y)
                {
                    X = x;
                    Y = y;
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct MSLLHOOKSTRUCT
            {
                public POINT pt;
                public uint mouseData;
                public uint flags;
                public uint time;
                public IntPtr dwExtraInfo;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct MONITORINFO
            {
                public int cbSize;
                public RECT rcMonitor;
                public RECT rcWork;
                public uint dwFlags;
            }

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool UnhookWindowsHookEx(IntPtr hhk);

            [DllImport("user32.dll")]
            public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            public static extern IntPtr GetModuleHandle(string lpModuleName);

            [DllImport("user32.dll")]
            public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetCursorPos(out POINT lpPoint);

            [DllImport("user32.dll")]
            public static extern short GetAsyncKeyState(int vKey);

            [DllImport("user32.dll")]
            public static extern IntPtr WindowFromPoint(POINT pt);

            [DllImport("user32.dll")]
            public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

            [DllImport("shcore.dll")]
            public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
        }
    }
}
