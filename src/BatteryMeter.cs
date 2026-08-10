using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace SleepPicker
{
    /// <summary>
    /// Windows' own battery meter -- the system icon next to the clock -- and the two
    /// things it takes to switch it off.
    ///
    /// Windows exposes this as the user policy "Remove the battery meter": HideSCAPower,
    /// a DWORD under HKCU\...\CurrentVersion\Policies\Explorer. That subtree is where
    /// Windows keeps the restrictions placed *on* the user, so it is deliberately
    /// read-only for them -- a standard user cannot write their own policy, and neither
    /// can an administrator until they elevate. This is the one thing SleepPicker does
    /// that its own asInvoker manifest cannot: the write is handed to a second copy of the
    /// program started with the runas verb, which puts up the UAC prompt, writes the one
    /// value and leaves. Everything else in the program still runs without elevation.
    ///
    /// The elevated copy writes through HKEY_USERS and the caller's SID rather than its own
    /// HKCU, because the administrator who answers the prompt need not be the user sitting
    /// in front of the machine -- and it is that user's meter being hidden.
    ///
    /// The taskbar only reads the value when it starts, so <see cref="RestartExplorer"/> is
    /// what makes the change show without signing out. Setting it also greys out Power in
    /// Settings -> Taskbar -> "Turn system icons on or off", so while it is set our own
    /// menu row is the way back.
    /// </summary>
    internal static class BatteryMeter
    {
        private const string PolicyKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
        private const string HideValueName = "HideSCAPower";

        /// <summary>The switch the elevated copy of the program is started with.</summary>
        private const string ElevatedSwitch = "--battery-meter";
        private const string HideArgument = "hide";
        private const string ShowArgument = "show";

        // Exit codes of the elevated copy. Its only way of reporting back: the runas verb
        // needs UseShellExecute, which rules out capturing its output.
        private const int ExitWritten = 0;
        private const int ExitWriteFailed = 1;
        private const int ExitBadArguments = 2;

        /// <summary>ERROR_CANCELLED -- the UAC prompt was dismissed.</summary>
        private const int ErrorCancelled = 1223;

        /// <summary>How long to wait for the old shell to go.</summary>
        private const int ShellExitTimeoutMilliseconds = 5000;

        /// <summary>
        /// How long to give Windows to put a new shell up before starting one by hand.
        /// It normally takes a second or two.
        /// </summary>
        private const int ShellReturnTimeoutMilliseconds = 15000;

        /// <summary>How often to look for the new taskbar while waiting for it.</summary>
        private const int ShellPollIntervalMilliseconds = 250;

        /// <summary>What became of a request to hide or show the meter.</summary>
        public enum ChangeResult
        {
            /// <summary>The value was written; the shell has still to be restarted.</summary>
            Applied,

            /// <summary>The elevation prompt was declined. Nothing was written.</summary>
            Declined,

            /// <summary>Elevation was granted but the write did not succeed.</summary>
            Failed
        }

        /// <summary>
        /// Whether Windows' battery meter is currently hidden. Reading needs no elevation,
        /// and it is read live rather than cached: an administrator's policy or another
        /// tool can set the same value.
        /// </summary>
        public static bool IsHidden()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PolicyKeyPath, false))
            {
                if (key == null)
                {
                    return false;
                }
                object value = key.GetValue(HideValueName);
                if (value == null)
                {
                    return false;
                }
                try
                {
                    return Convert.ToInt32(value) != 0;
                }
                catch (Exception)
                {
                    // Something else wrote a string or a blob here; treat it as unset.
                    return false;
                }
            }
        }

        /// <summary>
        /// Asks for the meter to be hidden or shown, elevating to do it. Blocks while the
        /// UAC prompt is up -- the menu is closed by then, and the answer is the next thing
        /// that has to happen either way.
        /// </summary>
        public static ChangeResult RequestHidden(bool hidden)
        {
            ProcessStartInfo start = new ProcessStartInfo(AutoStart.ExecutablePath);
            start.Arguments = ElevatedSwitch + " " + (hidden ? HideArgument : ShowArgument) +
                " " + WindowsIdentity.GetCurrent().User.Value;
            // The runas verb is what raises the prompt, and it needs the shell to do the
            // starting -- hence UseShellExecute, and hence exit codes rather than output.
            start.UseShellExecute = true;
            start.Verb = "runas";
            start.WindowStyle = ProcessWindowStyle.Hidden;

            Process helper;
            try
            {
                helper = Process.Start(start);
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == ErrorCancelled)
                {
                    // The prompt was dismissed, or no administrator password was given.
                    // That is an answer, not a fault, so it is reported rather than thrown.
                    return ChangeResult.Declined;
                }
                throw;
            }

            if (helper == null)
            {
                return ChangeResult.Failed;
            }

            using (helper)
            {
                helper.WaitForExit();
                return helper.ExitCode == ExitWritten ? ChangeResult.Applied : ChangeResult.Failed;
            }
        }

        /// <summary>
        /// What the elevated copy of the program runs instead of showing a tray icon:
        /// one registry value, then out. Returns the process exit code.
        /// </summary>
        public static int RunElevatedWrite(string[] args)
        {
            if (args.Length != 3 || !string.Equals(args[0], ElevatedSwitch, StringComparison.Ordinal))
            {
                return ExitBadArguments;
            }

            bool hidden = string.Equals(args[1], HideArgument, StringComparison.Ordinal);
            if (!hidden && !string.Equals(args[1], ShowArgument, StringComparison.Ordinal))
            {
                return ExitBadArguments;
            }

            try
            {
                Write(hidden, args[2]);
                return ExitWritten;
            }
            catch (Exception)
            {
                // Nothing here can be shown to anyone -- this copy has no user interface --
                // so the exit code is the whole report.
                return ExitWriteFailed;
            }
        }

        /// <summary>
        /// Whether the arguments are the elevated copy's, rather than something a user
        /// typed. Kept beside the switch itself so the two cannot drift apart.
        /// </summary>
        public static bool IsElevatedWriteRequest(string[] args)
        {
            return args.Length > 0 && string.Equals(args[0], ElevatedSwitch, StringComparison.Ordinal);
        }

        /// <summary>
        /// The elevated half: sets or removes HideSCAPower in the named user's hive.
        /// Showing the meter deletes the value rather than writing a zero, so a machine
        /// that never touched this is left exactly as it was found.
        /// </summary>
        private static void Write(bool hidden, string userSid)
        {
            string keyPath = userSid + "\\" + PolicyKeyPath;

            using (RegistryKey users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default))
            {
                if (hidden)
                {
                    using (RegistryKey key = users.CreateSubKey(keyPath))
                    {
                        if (key != null)
                        {
                            key.SetValue(HideValueName, 1, RegistryValueKind.DWord);
                        }
                    }
                    return;
                }

                bool keyIsNowEmpty;
                using (RegistryKey key = users.OpenSubKey(keyPath, true))
                {
                    if (key == null)
                    {
                        return;
                    }
                    key.DeleteValue(HideValueName, false);
                    keyIsNowEmpty = key.ValueCount == 0 && key.SubKeyCount == 0;
                }

                // This key is Windows', not ours, and an administrator may keep other
                // restrictions in it -- so it goes only when nothing else is left in it.
                // Deleted outside the using block: an open handle blocks the delete.
                if (keyIsNowEmpty)
                {
                    try
                    {
                        users.DeleteSubKey(keyPath, false);
                    }
                    catch (Exception)
                    {
                        // Another process opened the key in between. Harmless -- it is
                        // empty, and the meter shows either way.
                    }
                }
            }
        }

        /// <summary>
        /// Closes the shell and starts it again, which is what makes a changed HideSCAPower
        /// take effect without signing out.
        ///
        /// Every notification-area icon goes with it, including ours; WinForms' NotifyIcon
        /// puts itself back when the shell broadcasts TaskbarCreated, so the moon returns
        /// on its own a moment later.
        /// </summary>
        public static void RestartExplorer()
        {
            Process shell = FindShellProcess();
            if (shell != null)
            {
                using (shell)
                {
                    try
                    {
                        // Killed, rather than asked to leave through the shell's own
                        // "exit Explorer" command. Windows watches the process it made the
                        // shell and puts a new one up when it dies -- AutoRestartShell,
                        // which is on by default -- and that replacement is a real shell.
                        // A shell that exits politely is replaced by nobody, and an
                        // explorer.exe started by hand in its place is handed off to
                        // whatever Explorer process is still open for an open folder
                        // instead of taking over, which leaves the session with no taskbar
                        // at all. Being killed is what Explorer is built to survive.
                        shell.Kill();
                        shell.WaitForExit(ShellExitTimeoutMilliseconds);
                    }
                    catch (InvalidOperationException)
                    {
                        // It had already gone. Nothing to kill, and the wait below still
                        // holds: either a new shell is on its way or we start one.
                    }
                }
            }

            if (WaitForShell())
            {
                return;
            }

            // Windows did not bring it back -- AutoRestartShell can be switched off, and on
            // an image like this one it may well have been. Starting it by hand is the last
            // resort, and it works when no other Explorer is left to swallow the request.
            StartExplorer();
        }

        /// <summary>
        /// Waits for a taskbar to exist again, and says whether one turned up. Polled
        /// rather than waited on: there is no handle to wait on for "the shell is back",
        /// and the window is the thing we actually care about.
        /// </summary>
        private static bool WaitForShell()
        {
            int waited = 0;
            while (waited < ShellReturnTimeoutMilliseconds)
            {
                if (NativeMethods.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero)
                {
                    return true;
                }
                Thread.Sleep(ShellPollIntervalMilliseconds);
                waited += ShellPollIntervalMilliseconds;
            }
            return NativeMethods.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;
        }

        /// <summary>
        /// The process owning the taskbar, or null when there is no taskbar. Found through
        /// the window rather than by name: "explorer" also matches every open folder
        /// window on a machine where folders run in their own processes.
        /// </summary>
        private static Process FindShellProcess()
        {
            IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero)
            {
                return null;
            }

            uint processId;
            NativeMethods.GetWindowThreadProcessId(tray, out processId);
            if (processId == 0)
            {
                return null;
            }

            try
            {
                return Process.GetProcessById((int)processId);
            }
            catch (ArgumentException)
            {
                // It exited between finding the window and asking about it.
                return null;
            }
        }

        /// <summary>
        /// Starts the shell by hand, unless one has turned up in the meantime -- a second
        /// one would give the session two taskbars.
        /// </summary>
        private static void StartExplorer()
        {
            if (NativeMethods.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero)
            {
                return;
            }

            ProcessStartInfo start = new ProcessStartInfo("explorer.exe");
            start.UseShellExecute = false;
            // Process.Start hands back a handle we have no further use for -- the shell
            // outlives us -- so it is closed straight away.
            Process started = Process.Start(start);
            if (started != null)
            {
                started.Dispose();
            }
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr FindWindow(string className, string windowName);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        }
    }
}
