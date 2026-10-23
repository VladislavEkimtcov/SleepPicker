using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SleepPicker
{
    /// <summary>
    /// The system-wide Light/Dark app theme -- Settings -> Personalization -> Colors ->
    /// "Choose your mode" -- read and written through the two per-user registry values
    /// that page keeps in sync.
    ///
    /// Both AppsUseLightTheme and SystemUsesLightTheme are written together so the result
    /// matches picking "Light" or "Dark" outright, rather than landing on "Custom", which
    /// is what the two values disagreeing produces.
    /// </summary>
    internal static class DarkMode
    {
        private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string AppsValueName = "AppsUseLightTheme";
        private const string SystemValueName = "SystemUsesLightTheme";
        private const string AccentOnTaskbarValueName = "ColorPrevalence";

        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);

        /// <summary>
        /// True when dark mode is on. Missing counts as light, matching Windows' own
        /// default before this key has ever been written.
        /// </summary>
        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegPath, false))
            {
                if (key == null)
                {
                    return false;
                }
                object value = key.GetValue(AppsValueName);
                if (value == null)
                {
                    return false;
                }
                try
                {
                    return Convert.ToInt32(value) == 0;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static void SetEnabled(bool enabled)
        {
            int newValue = enabled ? 0 : 1;
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegPath))
            {
                key.SetValue(AppsValueName, newValue, RegistryValueKind.DWord);
                key.SetValue(SystemValueName, newValue, RegistryValueKind.DWord);
                ApplyAccentOnTaskbar(key, enabled);
            }

            // Broadcast so Explorer and the taskbar refresh immediately instead of
            // waiting for the next sign-in.
            UIntPtr result;
            NativeMethods.SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero,
                "ImmersiveColorSet", SMTO_ABORTIFHUNG, 100, out result);
        }

        /// <summary>
        /// "Show accent color on Start and taskbar" only means something in Dark mode:
        /// Settings greys it out in Light, yet the value is still honoured, so a Light
        /// taskbar comes out the accent colour with dark text on it -- unreadable on a dark
        /// accent, and no restart of Explorer clears it. The option is therefore switched
        /// off on the way to Light, and back on on the way to Dark, but only if it was
        /// SleepPicker that switched it off.
        /// </summary>
        private static void ApplyAccentOnTaskbar(RegistryKey key, bool darkEnabled)
        {
            if (darkEnabled)
            {
                if (Settings.AccentOnTaskbarSuspended)
                {
                    key.SetValue(AccentOnTaskbarValueName, 1, RegistryValueKind.DWord);
                    Settings.AccentOnTaskbarSuspended = false;
                }
                return;
            }

            object current = key.GetValue(AccentOnTaskbarValueName);
            if (current == null)
            {
                return;
            }
            try
            {
                if (Convert.ToInt32(current) == 0)
                {
                    return;
                }
            }
            catch (Exception)
            {
                // Something else wrote a string or a blob here; leave it alone.
                return;
            }

            // The note goes in first: if it were written second, a failure in between
            // would leave the option off with nothing to say it should come back.
            Settings.AccentOnTaskbarSuspended = true;
            key.SetValue(AccentOnTaskbarValueName, 0, RegistryValueKind.DWord);
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam,
                string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
        }
    }
}
