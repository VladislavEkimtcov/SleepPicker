using System;
using System.Reflection;
using Microsoft.Win32;

namespace SleepPicker
{
    /// <summary>
    /// Start-with-Windows support via the per-user Run key.
    ///
    /// A Run entry is used rather than a service or a scheduled task because this is an
    /// interactive tray app: HKCU\...\Run needs no administrator rights, survives profile
    /// migration, and starts the app in the logged-on user's session where a tray icon
    /// can actually appear.
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SleepPicker";

        /// <summary>
        /// Full path of the running executable. Environment.ProcessPath does not exist on
        /// .NET Framework, so the entry assembly's location is used instead.
        /// </summary>
        public static string ExecutablePath
        {
            get { return Assembly.GetEntryAssembly().Location; }
        }

        /// <summary>
        /// True when the Run entry exists and points at this exact executable. An entry
        /// left behind by a copy that has since been moved counts as disabled, so ticking
        /// the menu item repairs it.
        /// </summary>
        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (key == null)
                {
                    return false;
                }
                string value = key.GetValue(ValueName) as string;
                if (string.IsNullOrEmpty(value))
                {
                    return false;
                }
                return string.Equals(Unquote(value), ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null)
                {
                    return;
                }
                if (enabled)
                {
                    // Quoted so a path containing spaces is passed as one argument.
                    key.SetValue(ValueName, "\"" + ExecutablePath + "\"", RegistryValueKind.String);
                }
                else if (key.GetValue(ValueName) != null)
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }

        private static string Unquote(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
            {
                return trimmed.Substring(1, trimmed.Length - 2);
            }
            return trimmed;
        }
    }
}
