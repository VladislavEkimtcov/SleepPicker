using System;
using Microsoft.Win32;

namespace SleepPicker
{
    /// <summary>
    /// What SleepPicker has to remember between runs, kept under HKCU.
    ///
    /// There is no configuration file: this program is meant to be a single executable you
    /// can drop anywhere and delete again, so it writes nothing beside itself. Each value is
    /// only stored while it has something to say -- turning the dynamic icon back on, or
    /// leaving battery saver, removes the value, and the key with it, leaving no trace.
    /// </summary>
    internal static class Settings
    {
        private const string KeyPath = @"Software\SleepPicker";
        private const string DynamicTrayIconValue = "DynamicTrayIcon";
        private const string EnergySaverThresholdValue = "EnergySaverThreshold";

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
                    Store(DynamicTrayIconValue, 0);
                    return;
                }
                Forget(DynamicTrayIconValue);
            }
        }

        /// <summary>
        /// The battery saver charge level as it was before SleepPicker raised it to force
        /// battery saver on, or false when SleepPicker has not touched it.
        ///
        /// The value being here at all is the record that battery saver was switched on
        /// from our menu, which is why it is written rather than merely held in memory: a
        /// threshold of 100 left behind by a crash would keep battery saver on for good,
        /// with nothing left to say why or what it had been.
        /// </summary>
        public static bool TryGetForcedEnergySaverThreshold(out uint previous)
        {
            previous = 0;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
            {
                if (key == null)
                {
                    return false;
                }
                object value = key.GetValue(EnergySaverThresholdValue);
                if (value == null)
                {
                    return false;
                }
                try
                {
                    int stored = Convert.ToInt32(value);
                    if (stored < 0 || stored > 100)
                    {
                        // Not a charge level. Treat it as unset rather than writing
                        // nonsense back into the power scheme.
                        return false;
                    }
                    previous = (uint)stored;
                    return true;
                }
                catch (Exception)
                {
                    // Something else wrote a string or a blob here; treat it as unset.
                    return false;
                }
            }
        }

        public static void RememberForcedEnergySaverThreshold(uint previous)
        {
            Store(EnergySaverThresholdValue, (int)previous);
        }

        public static void ForgetForcedEnergySaverThreshold()
        {
            Forget(EnergySaverThresholdValue);
        }

        private static void Store(string valueName, int value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (key != null)
                {
                    key.SetValue(valueName, value, RegistryValueKind.DWord);
                }
            }
        }

        /// <summary>
        /// Removes a value, and the key with it once the last one has gone.
        /// </summary>
        private static void Forget(string valueName)
        {
            bool keyIsNowEmpty;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, true))
            {
                if (key == null)
                {
                    return;
                }
                key.DeleteValue(valueName, false);
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
