using System;
using Microsoft.Win32;

namespace SleepPicker
{
    /// <summary>
    /// The one preference SleepPicker has, kept under HKCU.
    ///
    /// There is no configuration file: this program is meant to be a single executable you
    /// can drop anywhere and delete again, so it writes nothing beside itself. The value is
    /// only stored when it differs from the default -- turning the dynamic icon back on
    /// removes the value, and the key with it, leaving no trace.
    /// </summary>
    internal static class Settings
    {
        private const string KeyPath = @"Software\SleepPicker";
        private const string DynamicTrayIconValue = "DynamicTrayIcon";

        /// <summary>
        /// Whether the tray icon shows the battery charge as the moon's phase. On by
        /// default: it is the point of the feature, and a machine without a battery never
        /// sees the option at all.
        /// </summary>
        public static bool DynamicTrayIcon
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    if (key == null)
                    {
                        return true;
                    }
                    object value = key.GetValue(DynamicTrayIconValue);
                    if (value == null)
                    {
                        return true;
                    }
                    try
                    {
                        return Convert.ToInt32(value) != 0;
                    }
                    catch (Exception)
                    {
                        // Something else wrote a string or a blob here; treat it as unset.
                        return true;
                    }
                }
            }

            set
            {
                if (!value)
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                    {
                        if (key != null)
                        {
                            key.SetValue(DynamicTrayIconValue, 0, RegistryValueKind.DWord);
                        }
                    }
                    return;
                }

                bool keyIsNowEmpty;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (key == null)
                    {
                        return;
                    }
                    key.DeleteValue(DynamicTrayIconValue, false);
                    keyIsNowEmpty = key.ValueCount == 0 && key.SubKeyCount == 0;
                }

                // Deleted outside the using block: an open handle blocks the delete.
                if (keyIsNowEmpty)
                {
                    try
                    {
                        Registry.CurrentUser.DeleteSubKey(KeyPath, false);
                    }
                    catch (Exception)
                    {
                        // Another process opened the key in between. Harmless -- it is
                        // empty, and the setting reads as the default either way.
                    }
                }
            }
        }
    }
}
