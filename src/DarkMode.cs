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
            }

            // Broadcast so Explorer and the taskbar refresh immediately instead of
            // waiting for the next sign-in.
            UIntPtr result;
            NativeMethods.SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero,
                "ImmersiveColorSet", SMTO_ABORTIFHUNG, 100, out result);
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam,
                string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
        }
    }
}
