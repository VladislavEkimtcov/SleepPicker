using System;
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
    /// The same lookup also serves the hover focus: the window the button belongs to is
    /// brought to the front, as a click on it would.
    ///
    /// Used from one thread only: the edge-switch worker.
    /// </summary>
    internal sealed class TaskbarPeek
    {
        private const uint PeekWindow = 1;

        private static readonly Regex GroupSuffix = new Regex(@"\s-\s\d+ running windows?$", RegexOptions.CultureInvariant);

        private IntPtr _peeked;
        private IntPtr _taskbar;

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
            IntPtr window = WindowOfButtonAt(pt, out name);
            string at = " at " + pt.X + "," + pt.Y + (Covering != null ? " under " + Covering : "");
            if (window == IntPtr.Zero)
            {
                End();
                return (name == null ? "no button here" : "no window found for [" + name + "]") + at;
            }
            string did = "[" + name + "] -> 0x" + window.ToInt64().ToString("X");
            if (peek)
            {
                did += ", " + Peek(window);
            }
            if (focus)
            {
                did += ", " + Focus(window);
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

        /// <summary>Brings <paramref name="window"/> to the front as a click on its button
        /// would -- restoring it if minimized -- but never the click itself: a click on the
        /// button of the window already in front minimizes it.</summary>
        private static string Focus(IntPtr window)
        {
            if (NativeMethods.GetForegroundWindow() == window)
            {
                return "already in front";
            }
            if (NativeMethods.IsIconic(window))
            {
                NativeMethods.ShowWindowAsync(window, NativeMethods.SW_RESTORE);
            }
            if (NativeMethods.SetForegroundWindow(window) && NativeMethods.GetForegroundWindow() == window)
            {
                return "focused";
            }
            // A background process is usually refused the foreground, and a hover is not the
            // input that would earn it. SwitchToThisWindow is what Alt+Tab uses and is let
            // through -- without typing a key to fool the lock.
            NativeMethods.SwitchToThisWindow(window, true);
            return NativeMethods.GetForegroundWindow() == window ? "focused by switching" : "focus refused";
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

        private IntPtr WindowOfButtonAt(EdgeSwitch.NativeMethods.POINT pt, out string name)
        {
            name = null;
            IntPtr list = TaskListAt(pt);
            if (list == IntPtr.Zero)
            {
                return IntPtr.Zero; // the Start button, the clock, an empty stretch of taskbar...
            }
            _taskbar = EdgeSwitch.NativeMethods.GetAncestor(list, EdgeSwitch.NativeMethods.GA_ROOT);

            name = ButtonNameAt(list, pt);
            if (string.IsNullOrEmpty(name))
            {
                name = null;
                return IntPtr.Zero;
            }
            name = GroupSuffix.Replace(name, "").Trim();
            return FindWindow(name);
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

        private static string ButtonNameAt(IntPtr list, EdgeSwitch.NativeMethods.POINT pt)
        {
            Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71"); // IAccessible
            object found;
            if (NativeMethods.AccessibleObjectFromWindow(list, NativeMethods.OBJID_CLIENT, ref iid, out found) != 0)
            {
                return null;
            }
            IAccessible accessible = found as IAccessible;
            if (accessible == null)
            {
                return null;
            }
            try
            {
                object hit = accessible.accHitTest(pt.X, pt.Y);
                if (hit is int && (int)hit != 0)
                {
                    return accessible.get_accName(hit);
                }
                IAccessible child = hit as IAccessible;
                return child != null ? child.get_accName(0) : null;
            }
            finally
            {
                Marshal.ReleaseComObject(accessible);
            }
        }

        /// <summary>The topmost taskbar window titled <paramref name="name"/>; else the topmost
        /// whose title ends in " - name" (a combined button names the application); else the
        /// topmost whose title starts with it (the button's name is cut off at 260 characters).</summary>
        private static IntPtr FindWindow(string name)
        {
            IntPtr exact = IntPtr.Zero;
            IntPtr suffixed = IntPtr.Zero;
            IntPtr prefixed = IntPtr.Zero;
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
                    exact = window;
                    return false; // Z order: the first match is the topmost
                }
                if (suffixed == IntPtr.Zero && text.EndsWith(suffix, StringComparison.Ordinal))
                {
                    suffixed = window;
                }
                if (prefixed == IntPtr.Zero && name.Length >= 40 && text.StartsWith(name, StringComparison.Ordinal))
                {
                    prefixed = window;
                }
                return true;
            }, IntPtr.Zero);
            return exact != IntPtr.Zero ? exact : suffixed != IntPtr.Zero ? suffixed : prefixed;
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
            public static extern void SwitchToThisWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool altTab);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool IsIconic(IntPtr hWnd);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ShowWindowAsync(IntPtr hWnd, int command);
        }
    }
}
