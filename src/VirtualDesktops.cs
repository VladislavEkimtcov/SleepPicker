using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SleepPicker
{
    /// <summary>
    /// Moves to the previous or next virtual desktop, and reports which one is current.
    ///
    /// The switch is normally a Win+Ctrl+arrow typed on this side. Over VNC from a Mac that
    /// chord never arrives (macOS keeps Ctrl+arrows for its own Spaces), but input injected
    /// here reaches the shell exactly as a local keypress would, so Windows performs its
    /// own switch -- focus handoff to the window in front on the new desktop included.
    ///
    /// A typed chord can be lost, though, and then nothing says so:
    ///   - an elevated window in front: SleepPicker runs un-elevated, and Windows silently
    ///     discards input injected by a lower-integrity process while a higher-integrity
    ///     window has the focus (UIPI);
    ///   - Task View in front, which takes the arrows for its own navigation;
    ///   - a modifier held down on the Mac side, which turns the chord into another one.
    /// For those the switch goes straight through Explorer's own virtual desktop service
    /// (<see cref="Shell"/>), and every typed chord is checked against it afterwards and
    /// completed through it if the desktop did not move. That interface is undocumented
    /// and its identifiers change between Windows releases, which is why it backs the
    /// chord up rather than replacing it: the ones below are those of builds 19041-19045,
    /// the range Windows 10 LTSC 2021 is pinned to. Anywhere else the lookup fails and the
    /// chord is all there is.
    /// </summary>
    internal static class VirtualDesktops
    {
        private const string DesktopsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
        private const string SessionInfoKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\";

        /// <summary>Whether a typed chord would be dropped or misread right now.</summary>
        public static bool ChordWouldBeLost()
        {
            if (IsForegroundAboveOurIntegrity())
            {
                return true;
            }
            int[] modifiers = new int[]
            {
                NativeMethods.VK_SHIFT, NativeMethods.VK_CONTROL, NativeMethods.VK_MENU,
                NativeMethods.VK_LWIN, NativeMethods.VK_RWIN,
            };
            for (int i = 0; i < modifiers.Length; i++)
            {
                if ((NativeMethods.GetAsyncKeyState(modifiers[i]) & 0x8000) != 0)
                {
                    return true;
                }
            }
            return IsTaskViewInFront();
        }

        /// <summary>Types Win+Ctrl+Left (-1) or Win+Ctrl+Right (+1).</summary>
        public static void TypeChord(int direction)
        {
            ushort arrow = direction < 0 ? NativeMethods.VK_LEFT : NativeMethods.VK_RIGHT;
            NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[]
            {
                Key(NativeMethods.VK_LCONTROL, false, false),
                Key(NativeMethods.VK_LWIN, false, true),
                Key(arrow, false, true),
                Key(arrow, true, true),
                Key(NativeMethods.VK_LWIN, true, true),
                Key(NativeMethods.VK_LCONTROL, true, false),
            };
            NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
        }

        /// <summary>Types Win+Tab, which opens Task View, or closes it when it is open.</summary>
        public static void TypeTaskViewChord()
        {
            NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[]
            {
                Key(NativeMethods.VK_LWIN, false, true),
                Key(NativeMethods.VK_TAB, false, false),
                Key(NativeMethods.VK_TAB, true, false),
                Key(NativeMethods.VK_LWIN, true, true),
            };
            NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
        }

        /// <summary>
        /// Opens Task View without typing anything -- the shell folder behind Windows 10's own
        /// Task View button -- for when Win+Tab would not be heard. Slower than the chord
        /// (it starts a process), so it is the fallback.
        /// </summary>
        public static void LaunchTaskView()
        {
            try
            {
                using (System.Diagnostics.Process.Start("explorer.exe", "shell:::{3080F90E-D7AD-11D9-BD98-0000947B0257}"))
                {
                }
            }
            catch (Exception)
            {
                // Nothing else to try; the next swipe tries again.
            }
        }

        /// <summary>
        /// Whether Task View, or Alt+Tab, is in front. Measured on build 19044: Task View is
        /// Explorer's CoreWindow titled "Task View" -- a CoreWindow alone could equally be
        /// Start or the Action Center -- and Alt+Tab is the MultitaskingViewFrame.
        /// </summary>
        public static bool IsTaskViewInFront()
        {
            IntPtr window = NativeMethods.GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return false;
            }
            window = NativeMethods.GetAncestor(window, NativeMethods.GA_ROOT);
            System.Text.StringBuilder text = new System.Text.StringBuilder(64);
            NativeMethods.GetClassName(window, text, text.Capacity);
            string className = text.ToString();
            if (className == "MultitaskingViewFrame")
            {
                return true;
            }
            if (className != "Windows.UI.Core.CoreWindow")
            {
                return false;
            }
            text.Length = 0;
            NativeMethods.GetWindowText(window, text, text.Capacity);
            return text.ToString() == "Task View";
        }

        private static NativeMethods.INPUT Key(ushort vk, bool up, bool extended)
        {
            NativeMethods.INPUT input = new NativeMethods.INPUT();
            input.type = NativeMethods.INPUT_KEYBOARD;
            input.ki.wVk = vk;
            input.ki.wScan = (ushort)NativeMethods.MapVirtualKey(vk, 0);
            input.ki.dwFlags = (up ? NativeMethods.KEYEVENTF_KEYUP : 0u)
                             | (extended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0u);
            return input;
        }

        /// <summary>
        /// Explorer's virtual desktop service. One per thread that uses it: the connection is
        /// kept between calls, and dropped and made afresh once if a call fails -- which is
        /// what every call does after Explorer restarts. Every method answers false rather
        /// than throwing when the service cannot be reached.
        /// </summary>
        public sealed class Shell
        {
            private const int AdjacentLeft = 3;
            private const int AdjacentRight = 4;

            private IVirtualDesktopManagerInternal _manager;

            /// <summary>The identity of the current desktop.</summary>
            public bool TryGetCurrent(out Guid id)
            {
                object result;
                bool ok = TryCall(delegate(IVirtualDesktopManagerInternal m)
                {
                    return (object)((IVirtualDesktop)m.GetCurrentDesktop()).GetId();
                }, out result);
                id = ok ? (Guid)result : Guid.Empty;
                return ok;
            }

            /// <summary>The desktop to the left (-1) or right (+1) of the current one; false
            /// on the first or last desktop, where Win+Ctrl+arrow does nothing either.</summary>
            public bool TryGetAdjacent(int direction, out object desktop)
            {
                object result;
                bool ok = TryCall(delegate(IVirtualDesktopManagerInternal m)
                {
                    object found;
                    int hr = m.GetAdjacentDesktop(m.GetCurrentDesktop(),
                        direction < 0 ? AdjacentLeft : AdjacentRight, out found);
                    return hr == 0 ? found : null;
                }, out result);
                desktop = result;
                return ok && result != null;
            }

            public bool TrySwitch(object desktop)
            {
                object unused;
                return TryCall(delegate(IVirtualDesktopManagerInternal m)
                {
                    m.SwitchDesktop(desktop);
                    return null;
                }, out unused);
            }

            private bool TryCall(Func<IVirtualDesktopManagerInternal, object> call, out object result)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        if (_manager == null)
                        {
                            _manager = Connect();
                        }
                        result = call(_manager);
                        return true;
                    }
                    catch (Exception)
                    {
                        // A stale connection (Explorer restarted) or a build whose interface
                        // we do not know. Reconnect once; the second failure is the answer.
                        _manager = null;
                    }
                }
                result = null;
                return false;
            }

            private static IVirtualDesktopManagerInternal Connect()
            {
                object shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239")));
                try
                {
                    Guid service = new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
                    Guid iid = typeof(IVirtualDesktopManagerInternal).GUID;
                    return (IVirtualDesktopManagerInternal)((IServiceProvider10)shell).QueryService(ref service, ref iid);
                }
                finally
                {
                    Marshal.ReleaseComObject(shell);
                }
            }
        }

        /// <summary>
        /// Whether the window in front belongs to a process with a higher integrity level
        /// than ours, so injected keys would be dropped. A process whose token cannot even
        /// be opened -- the usual answer for an elevated one -- counts as higher.
        /// </summary>
        private static bool IsForegroundAboveOurIntegrity()
        {
            int ours = IntegrityOf(NativeMethods.GetCurrentProcess());
            if (ours < 0 || ours >= NativeMethods.SECURITY_MANDATORY_HIGH_RID)
            {
                return false;
            }

            IntPtr window = NativeMethods.GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return false;
            }
            uint pid;
            NativeMethods.GetWindowThreadProcessId(window, out pid);
            IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero)
            {
                return true;
            }
            try
            {
                int theirs = IntegrityOf(process);
                return theirs < 0 || theirs > ours;
            }
            finally
            {
                NativeMethods.CloseHandle(process);
            }
        }

        /// <summary>The mandatory integrity RID of a process, or -1 if it cannot be read.</summary>
        private static int IntegrityOf(IntPtr process)
        {
            IntPtr token;
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out token))
            {
                return -1;
            }
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int length;
                NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, IntPtr.Zero, 0, out length);
                if (length <= 0)
                {
                    return -1;
                }
                buffer = Marshal.AllocHGlobal(length);
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buffer, length, out length))
                {
                    return -1;
                }
                // TOKEN_MANDATORY_LABEL starts with the SID pointer; its last sub-authority is the level.
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                int count = Marshal.ReadByte(NativeMethods.GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
                NativeMethods.CloseHandle(token);
            }
        }

        /// <summary>
        /// Which desktop is current, out of how many, from the registry values Explorer keeps
        /// for itself. False when they cannot be read -- for instance when only one desktop
        /// has ever existed, in which case Explorer writes no list at all.
        /// </summary>
        public static bool TryGetPosition(out int index, out int count)
        {
            index = 0;
            count = 1;
            try
            {
                byte[] ids;
                byte[] current;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(DesktopsKeyPath, false))
                {
                    if (key == null)
                    {
                        return false;
                    }
                    ids = key.GetValue("VirtualDesktopIDs") as byte[];
                    current = key.GetValue("CurrentVirtualDesktop") as byte[];
                }

                // The per-session copy is the live one where it exists.
                uint session;
                if (NativeMethods.ProcessIdToSessionId(NativeMethods.GetCurrentProcessId(), out session))
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                        SessionInfoKeyPath + session.ToString() + @"\VirtualDesktops", false))
                    {
                        if (key != null)
                        {
                            byte[] live = key.GetValue("CurrentVirtualDesktop") as byte[];
                            if (live != null)
                            {
                                current = live;
                            }
                        }
                    }
                }

                if (ids == null || ids.Length < 16 || current == null || current.Length != 16)
                {
                    return false;
                }

                count = ids.Length / 16;
                for (int i = 0; i < count; i++)
                {
                    bool match = true;
                    for (int b = 0; b < 16 && match; b++)
                    {
                        match = ids[i * 16 + b] == current[b];
                    }
                    if (match)
                    {
                        index = i;
                        return true;
                    }
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider10
        {
            [return: MarshalAs(UnmanagedType.IUnknown)]
            object QueryService(ref Guid service, ref Guid riid);
        }

        /// <summary>One desktop, builds 19041-19045.</summary>
        [ComImport, Guid("FF72FFDD-BE7E-43FC-9C03-AD81681E88E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktop
        {
            bool IsViewVisible(IntPtr view);
            Guid GetId();
        }

        /// <summary>Builds 19041-19045. Only the methods up to SwitchDesktop are declared;
        /// the vtable order before them is what matters.</summary>
        [ComImport, Guid("F31574D6-B682-4CDC-BD56-1827860ABEC6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IVirtualDesktopManagerInternal
        {
            int GetCount();
            void MoveViewToDesktop(IntPtr view, IntPtr desktop);
            bool CanViewMoveDesktops(IntPtr view);
            [return: MarshalAs(UnmanagedType.IUnknown)]
            object GetCurrentDesktop();
            void GetDesktops(out IntPtr desktops);
            [PreserveSig]
            int GetAdjacentDesktop([MarshalAs(UnmanagedType.IUnknown)] object from, int direction,
                [MarshalAs(UnmanagedType.IUnknown)] out object desktop);
            void SwitchDesktop([MarshalAs(UnmanagedType.IUnknown)] object desktop);
        }

        private static class NativeMethods
        {
            public const uint INPUT_KEYBOARD = 1;
            public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
            public const uint KEYEVENTF_KEYUP = 0x0002;
            public const ushort VK_LCONTROL = 0xA2;
            public const ushort VK_LWIN = 0x5B;
            public const ushort VK_LEFT = 0x25;
            public const ushort VK_RIGHT = 0x27;
            public const ushort VK_TAB = 0x09;

            public const int VK_SHIFT = 0x10;
            public const int VK_CONTROL = 0x11;
            public const int VK_MENU = 0x12;
            public const int VK_RWIN = 0x5C;
            public const uint GA_ROOT = 2;

            public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            public const uint TOKEN_QUERY = 0x0008;
            public const int TokenIntegrityLevel = 25;
            public const int SECURITY_MANDATORY_HIGH_RID = 0x3000;

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

            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

            [DllImport("user32.dll")]
            public static extern uint MapVirtualKey(uint uCode, uint uMapType);

            [DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")]
            public static extern short GetAsyncKeyState(int vKey);

            [DllImport("user32.dll")]
            public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

            [DllImport("user32.dll")]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

            [DllImport("kernel32.dll")]
            public static extern IntPtr GetCurrentProcess();

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr handle);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

            [DllImport("advapi32.dll")]
            public static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

            [DllImport("advapi32.dll")]
            public static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

            [DllImport("kernel32.dll")]
            public static extern uint GetCurrentProcessId();
        }
    }
}
