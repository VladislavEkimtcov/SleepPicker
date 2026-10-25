using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Accessibility;

namespace SleepPicker
{
    /// <summary>
    /// Aero Peek from the taskbar button itself. Windows only peeks -- every other window
    /// turned to glass -- once the pointer reaches the thumbnail above a button; this does
    /// it as soon as the pointer comes to rest on the button.
    ///
    /// Two steps, both through what Explorer already exposes:
    ///   - which button is under the pointer: the task list's accessibility object, whose
    ///     children are the buttons, each named with its window's title (followed, when the
    ///     button belongs to a group, by " - N running windows");
    ///   - which window that title belongs to: the topmost window the taskbar would show
    ///     with that title -- or, for a combined button that only carries the application's
    ///     name, the topmost one whose title ends in it ("Page - Google Chrome").
    /// The peek itself is DWM's own, the same call Explorer makes for a thumbnail. It is
    /// undocumented (dwmapi ordinal 113) but has kept this shape since Windows 8.
    ///
    /// The same lookup also serves the hover focus: the window is brought to the front --
    /// or, when its title is shared, the button is pressed through the same accessibility
    /// object, so the taskbar picks the window.
    ///
    /// Used from one thread only: the edge-switch worker.
    /// </summary>
    internal sealed class TaskbarPeek
    {
        private const uint PeekWindow = 1;

        /// <summary>How long after a press a window it minimized is still caught and restored.</summary>
        private const int UndoMinimizeMilliseconds = 250;

        private static readonly Regex GroupSuffix = new Regex(@"\s-\s\d+ running windows?$", RegexOptions.CultureInvariant);

        private IntPtr _peeked;
        private IntPtr _taskbar;

        // What the last lookup found, for the press that may follow it.
        private IntPtr _list;

        /// <summary>Whether the button under the pointer shows as pressed: the taskbar's own
        /// mark of the active window, which titles alone cannot give when several windows
        /// share one.</summary>
        private bool _pressed;

        public bool Active
        {
            get { return _peeked != IntPtr.Zero; }
        }

        /// <summary>For the log: what answered WindowFromPoint over the buttons, if not them.</summary>
        public string Covering { get; private set; }

        /// <summary>Peeks at the window of the button under <paramref name="pt"/>, brings it
        /// to the front, or both; ends the peek when there is no button there. One lookup
        /// serves both, so turning both on costs no more than one. Returns what it did, for
        /// the log.</summary>
        public string Update(EdgeSwitch.NativeMethods.POINT pt, bool peek, bool focus)
        {
            Covering = null;
            string name;
            List<IntPtr> windows = WindowsOfButtonAt(pt, out name);
            string at = " at " + pt.X + "," + pt.Y + (Covering != null ? " under " + Covering : "");
            if (windows.Count == 0)
            {
                End();
                if (name == null)
                {
                    return "no button here" + at;
                }
                // Not pressed: a button whose window is not on this desktop belongs to one on
                // another, and the press, like the click, would switch desktops to reach it.
                return "no window found for [" + name + "]" + at;
            }
            IntPtr window = windows[0];
            string did = "[" + name + "] -> 0x" + window.ToInt64().ToString("X") +
                (windows.Count > 1 ? " (title shared by " + windows.Count + ")" : "");
            if (peek)
            {
                did += ", " + Peek(window);
            }
            if (focus)
            {
                did += ", " + Focus(windows, pt);
            }
            return did + at;
        }

        private string Peek(IntPtr window)
        {
            if (window == _peeked)
            {
                return "already peeking";
            }
            // Peek at the new window straight away: ending the old peek first would flash
            // every window back in between.
            int hr = NativeMethods.DwmpActivateLivePreview(1, window, _taskbar, PeekWindow, IntPtr.Zero);
            if (hr >= 0)
            {
                _peeked = window;
            }
            return "dwm hr=0x" + hr.ToString("X");
        }

        /// <summary>Brings the button's window to the front, restored if minimized -- and never
        /// hides it, as a click on the button of the active window would.
        ///
        /// Asked for plainly, Windows mostly refuses once the user has typed into the window
        /// in front: it holds the foreground for the process that had the last input, and a
        /// hover is nobody's -- the button only flashes. An Alt tap is what Windows documents
        /// as lifting that lock, so one is sent then, and only then, and the request made again.
        ///
        /// Only windows sharing their title with others (<paramref name="windows"/> holds more
        /// than one) are left to the taskbar's press, since only Explorer knows which of them
        /// the button is. The press is a click, though, and the taskbar's idea of the active
        /// window can lag behind Windows': whatever it minimizes is put straight back.</summary>
        private string Focus(List<IntPtr> windows, EdgeSwitch.NativeMethods.POINT pt)
        {
            if (_pressed)
            {
                return "already in front";
            }
            string first = Activate(windows, pt);
            if (first != null)
            {
                return first;
            }
            if (!TapAlt())
            {
                return "refused; no Alt tap with a key held";
            }
            string second = Activate(windows, pt);
            return second != null ? "after Alt tap, " + second : "refused even after Alt tap";
        }

        /// <summary>One attempt: what brought the window forward, or null when Windows refused.</summary>
        private string Activate(List<IntPtr> windows, EdgeSwitch.NativeMethods.POINT pt)
        {
            if (windows.Count == 1)
            {
                IntPtr window = windows[0];
                NativeMethods.SetForegroundWindow(window);
                if (NativeMethods.GetForegroundWindow() != window)
                {
                    return null;
                }
                if (NativeMethods.IsIconic(window))
                {
                    NativeMethods.ShowWindowAsync(window, NativeMethods.SW_RESTORE);
                }
                return "focused";
            }

            bool[] wasMinimized = new bool[windows.Count];
            for (int i = 0; i < windows.Count; i++)
            {
                wasMinimized[i] = NativeMethods.IsIconic(windows[i]);
            }
            // The press is Explorer's, and the right to the foreground an Alt tap gives is
            // ours: handed on, or the taskbar is refused where we would not be.
            uint explorer;
            NativeMethods.GetWindowThreadProcessId(_taskbar, out explorer);
            NativeMethods.AllowSetForegroundWindow(explorer);
            IntPtr before = NativeMethods.GetForegroundWindow();
            string pressed = Press(pt);
            // The minimize, when it comes, is posted rather than done in the press: watched
            // for a moment, which the worker can spare once per rest.
            int start = Environment.TickCount;
            while (unchecked(Environment.TickCount - start) < UndoMinimizeMilliseconds)
            {
                for (int i = 0; i < windows.Count; i++)
                {
                    if (!wasMinimized[i] && NativeMethods.IsIconic(windows[i]))
                    {
                        NativeMethods.ShowWindowAsync(windows[i], NativeMethods.SW_RESTORE);
                        NativeMethods.SetForegroundWindow(windows[i]);
                        return pressed + ", minimized one: restored";
                    }
                }
                System.Threading.Thread.Sleep(15);
            }
            return NativeMethods.GetForegroundWindow() != before ? pressed : null;
        }

        /// <summary>Taps Alt, unless a modifier is already held: added to the user's own keys
        /// it would make a shortcut of them.</summary>
        private static bool TapAlt()
        {
            foreach (int vk in new int[] { NativeMethods.VK_SHIFT, NativeMethods.VK_CONTROL, NativeMethods.VK_MENU,
                NativeMethods.VK_LWIN, NativeMethods.VK_RWIN })
            {
                if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0)
                {
                    return false;
                }
            }
            NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[] { AltKey(false), AltKey(true) };
            return NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT))) == inputs.Length;
        }

        private static NativeMethods.INPUT AltKey(bool up)
        {
            NativeMethods.INPUT input = new NativeMethods.INPUT();
            input.type = NativeMethods.INPUT_KEYBOARD;
            input.ki.wVk = (ushort)NativeMethods.VK_MENU;
            input.ki.wScan = (ushort)NativeMethods.MapVirtualKey((uint)NativeMethods.VK_MENU, 0);
            input.ki.dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0u;
            return input;
        }

        /// <summary>The button's default action through its accessibility object: the
        /// taskbar's own click, with no input injected.</summary>
        private string Press(EdgeSwitch.NativeMethods.POINT pt)
        {
            IAccessible accessible = TaskListAccessible(_list);
            if (accessible == null)
            {
                return "press: no accessible";
            }
            try
            {
                object hit = accessible.accHitTest(pt.X, pt.Y);
                if (hit is int && (int)hit != 0)
                {
                    accessible.accDoDefaultAction(hit);
                    return "pressed";
                }
                IAccessible child = hit as IAccessible;
                if (child == null)
                {
                    return "press: no button";
                }
                try
                {
                    child.accDoDefaultAction(0);
                }
                finally
                {
                    Marshal.ReleaseComObject(child);
                }
                return "pressed";
            }
            catch (COMException ex)
            {
                return "press failed: 0x" + ex.ErrorCode.ToString("X");
            }
            finally
            {
                Marshal.ReleaseComObject(accessible);
            }
        }

        public void End()
        {
            if (_peeked == IntPtr.Zero)
            {
                return;
            }
            NativeMethods.DwmpActivateLivePreview(0, _peeked, _taskbar, PeekWindow, IntPtr.Zero);
            _peeked = IntPtr.Zero;
        }

        /// <summary>The windows the button under <paramref name="pt"/> may stand for, topmost
        /// first: one, unless its title is shared; none when there is no button there, or its
        /// window is on another desktop.</summary>
        private List<IntPtr> WindowsOfButtonAt(EdgeSwitch.NativeMethods.POINT pt, out string name)
        {
            name = null;
            _pressed = false;
            IntPtr list = TaskListAt(pt);
            _list = list;
            if (list == IntPtr.Zero)
            {
                return new List<IntPtr>(); // the Start button, the clock, an empty stretch of taskbar...
            }
            _taskbar = EdgeSwitch.NativeMethods.GetAncestor(list, EdgeSwitch.NativeMethods.GA_ROOT);

            name = ButtonAt(list, pt, out _pressed);
            if (string.IsNullOrEmpty(name))
            {
                name = null;
                return new List<IntPtr>();
            }
            name = GroupSuffix.Replace(name, "").Trim();
            return FindWindows(name);
        }

        /// <summary>The task list -- the strip of buttons -- under <paramref name="pt"/>, found
        /// by where it is rather than by what is on top: with the pointer really there,
        /// something else answers WindowFromPoint above the buttons.</summary>
        private IntPtr TaskListAt(EdgeSwitch.NativeMethods.POINT pt)
        {
            IntPtr found = IntPtr.Zero;
            foreach (string className in new string[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                IntPtr tray = IntPtr.Zero;
                while (found == IntPtr.Zero &&
                    (tray = EdgeSwitch.NativeMethods.FindWindowEx(IntPtr.Zero, tray, className, null)) != IntPtr.Zero)
                {
                    NativeMethods.EnumChildWindows(tray, delegate(IntPtr child, IntPtr unused)
                    {
                        EdgeSwitch.NativeMethods.RECT rc;
                        if (ClassOf(child) == "MSTaskListWClass" && EdgeSwitch.NativeMethods.GetWindowRect(child, out rc) &&
                            pt.X >= rc.Left && pt.X < rc.Right && pt.Y >= rc.Top && pt.Y < rc.Bottom)
                        {
                            found = child;
                            return false;
                        }
                        return true;
                    }, IntPtr.Zero);
                }
            }
            if (found != IntPtr.Zero)
            {
                IntPtr top = EdgeSwitch.NativeMethods.WindowFromPoint(pt);
                if (top != found)
                {
                    Covering = ClassOf(top) + " of " + ClassOf(EdgeSwitch.NativeMethods.GetAncestor(top, EdgeSwitch.NativeMethods.GA_ROOT));
                }
            }
            return found;
        }

        /// <summary>The task list's accessibility object, whose children are its buttons; the
        /// caller releases it.</summary>
        private static IAccessible TaskListAccessible(IntPtr list)
        {
            Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71"); // IAccessible
            object found;
            if (list == IntPtr.Zero ||
                NativeMethods.AccessibleObjectFromWindow(list, NativeMethods.OBJID_CLIENT, ref iid, out found) != 0)
            {
                return null;
            }
            return found as IAccessible;
        }

        /// <summary>The name of the button under <paramref name="pt"/>, and whether it shows
        /// as pressed.</summary>
        private static string ButtonAt(IntPtr list, EdgeSwitch.NativeMethods.POINT pt, out bool pressed)
        {
            pressed = false;
            IAccessible accessible = TaskListAccessible(list);
            if (accessible == null)
            {
                return null;
            }
            try
            {
                object hit = accessible.accHitTest(pt.X, pt.Y);
                if (hit is int && (int)hit != 0)
                {
                    pressed = IsPressed(accessible.get_accState(hit));
                    return accessible.get_accName(hit);
                }
                IAccessible child = hit as IAccessible;
                if (child == null)
                {
                    return null;
                }
                pressed = IsPressed(child.get_accState(0));
                return child.get_accName(0);
            }
            finally
            {
                Marshal.ReleaseComObject(accessible);
            }
        }

        private static bool IsPressed(object state)
        {
            return state is int && ((int)state & NativeMethods.STATE_SYSTEM_PRESSED) != 0;
        }

        /// <summary>The taskbar windows titled <paramref name="name"/>; else those whose title
        /// ends in " - name" (a combined button names the application); else those whose title
        /// starts with it (the button's name is cut off at 260 characters). Topmost first, in
        /// Z order; more than one when the title does not say which window the button is.</summary>
        private static List<IntPtr> FindWindows(string name)
        {
            List<IntPtr> exact = new List<IntPtr>();
            List<IntPtr> suffixed = new List<IntPtr>();
            List<IntPtr> prefixed = new List<IntPtr>();
            string suffix = " - " + name;
            StringBuilder title = new StringBuilder(512);
            NativeMethods.EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                if (!IsOnTaskbar(window))
                {
                    return true;
                }
                title.Length = 0;
                NativeMethods.GetWindowText(window, title, title.Capacity);
                string text = title.ToString();
                if (text == name)
                {
                    exact.Add(window);
                }
                else if (text.EndsWith(suffix, StringComparison.Ordinal))
                {
                    suffixed.Add(window);
                }
                else if (name.Length >= 40 && text.StartsWith(name, StringComparison.Ordinal))
                {
                    prefixed.Add(window);
                }
                return true;
            }, IntPtr.Zero);
            return exact.Count > 0 ? exact : suffixed.Count > 0 ? suffixed : prefixed;
        }

        /// <summary>Roughly the taskbar's own rule: visible, unowned or an app window, not a
        /// tool window, and not cloaked -- windows on other virtual desktops are cloaked.</summary>
        private static bool IsOnTaskbar(IntPtr window)
        {
            if (!NativeMethods.IsWindowVisible(window))
            {
                return false;
            }
            long exStyle = NativeMethods.GetWindowLong(window, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            {
                return false;
            }
            if (NativeMethods.GetWindow(window, NativeMethods.GW_OWNER) != IntPtr.Zero &&
                (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
            {
                return false;
            }
            int cloaked;
            if (NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 &&
                cloaked != 0)
            {
                return false;
            }
            return true;
        }

        private static string ClassOf(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                return "";
            }
            StringBuilder name = new StringBuilder(64);
            EdgeSwitch.NativeMethods.GetClassName(window, name, name.Capacity);
            return name.ToString();
        }

        private static class NativeMethods
        {
            public const uint OBJID_CLIENT = 0xFFFFFFFC;
            public const int GWL_EXSTYLE = -20;
            public const long WS_EX_TOOLWINDOW = 0x00000080;
            public const long WS_EX_APPWINDOW = 0x00040000;
            public const uint GW_OWNER = 4;
            public const int DWMWA_CLOAKED = 14;
            public const int SW_RESTORE = 9;
            public const int STATE_SYSTEM_PRESSED = 0x8;
            public const uint INPUT_KEYBOARD = 1;
            public const uint KEYEVENTF_KEYUP = 0x0002;
            public const int VK_SHIFT = 0x10;
            public const int VK_CONTROL = 0x11;
            public const int VK_MENU = 0x12;
            public const int VK_LWIN = 0x5B;
            public const int VK_RWIN = 0x5C;

            public delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);

            [DllImport("dwmapi.dll", EntryPoint = "#113")]
            public static extern int DwmpActivateLivePreview(uint peekOn, IntPtr peekWindow, IntPtr topmostWindow, uint peekType, IntPtr reserved);

            [DllImport("dwmapi.dll")]
            public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

            [DllImport("oleacc.dll")]
            public static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid,
                [MarshalAs(UnmanagedType.IUnknown)] out object accessible);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool IsWindowVisible(IntPtr hWnd);

            // Not GetWindowLongPtrW, which a 32-bit user32 does not export; the extended
            // style fits in 32 bits either way.
            [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
            public static extern int GetWindowLong(IntPtr hWnd, int index);

            [DllImport("user32.dll")]
            public static extern IntPtr GetWindow(IntPtr hWnd, uint command);

            [DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool IsIconic(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern short GetAsyncKeyState(int vKey);

            [DllImport("user32.dll")]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool AllowSetForegroundWindow(uint processId);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

            [DllImport("user32.dll")]
            public static extern uint MapVirtualKey(uint uCode, uint uMapType);

            [StructLayout(LayoutKind.Sequential)]
            public struct KEYBDINPUT
            {
                public ushort wVk;
                public ushort wScan;
                public uint dwFlags;
                public uint time;
                public IntPtr dwExtraInfo;
            }

            // INPUT's union is sized by MOUSEINPUT, its largest member; KEYBDINPUT is padded
            // out to it, or SendInput rejects the structure size.
            [StructLayout(LayoutKind.Sequential)]
            public struct INPUT
            {
                public uint type;
                public KEYBDINPUT ki;
                public uint pad1;
                public uint pad2;
            }

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ShowWindowAsync(IntPtr hWnd, int command);
        }
    }
}
