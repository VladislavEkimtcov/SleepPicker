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
        private const string DarkModeEnergySaverOverrideValue = "DarkModeEnergySaverOverride";
        private const string EdgeSwitchValue = "EdgeSwitch";
        private const string EdgeSwitchDwellValue = "EdgeSwitchDwellMilliseconds";
        private const string EdgeSwitchDistanceValue = "EdgeSwitchDistancePixels";
        private const string EdgeSwitchTopDistanceValue = "EdgeSwitchTopDistancePixels";
        private const string TaskbarPeekValue = "TaskbarPeek";
        private const string DevReadoutValue = "DevReadout";

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

        /// <summary>
        /// The charge level as it was before it was briefly overridden to pull the
        /// machine out of battery saver for a Dark Mode toggle -- see
        /// <see cref="PowerModeSettings.TrySuspendForImmediateEffect"/> -- or false when
        /// no such override is outstanding.
        ///
        /// Kept under a key of its own rather than reusing
        /// <see cref="EnergySaverThresholdValue"/>: that one is the Power Mode row's own
        /// note of what to restore once battery saver is no longer wanted, and the two
        /// overrides can be in the air together -- battery saver chosen deliberately from
        /// the menu, then briefly lifted again for a theme change -- so they must not
        /// share a slot or one would overwrite what the other needs to put back.
        /// </summary>
        public static bool TryGetDarkModeEnergySaverOverride(out uint previous)
        {
            previous = 0;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
            {
                if (key == null)
                {
                    return false;
                }
                object value = key.GetValue(DarkModeEnergySaverOverrideValue);
                if (value == null)
                {
                    return false;
                }
                try
                {
                    int stored = Convert.ToInt32(value);
                    if (stored < 0 || stored > 100)
                    {
                        return false;
                    }
                    previous = (uint)stored;
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static void RememberDarkModeEnergySaverOverride(uint previous)
        {
            Store(DarkModeEnergySaverOverrideValue, (int)previous);
        }

        public static void ForgetDarkModeEnergySaverOverride()
        {
            Forget(DarkModeEnergySaverOverrideValue);
        }

        /// <summary>
        /// Whether resting the pointer on a side edge, or scrolling over the taskbar,
        /// switches virtual desktops. Off by default: the hook watches every pointer
        /// movement in the session, which nobody should get without asking.
        /// </summary>
        public static bool EdgeSwitchEnabled
        {
            get { return ReadInt(EdgeSwitchValue, 0, 0, 1) != 0; }
            set
            {
                if (value)
                {
                    Store(EdgeSwitchValue, 1);
                    return;
                }
                Forget(EdgeSwitchValue);
            }
        }

        /// <summary>How long the pointer has to rest in the zone; stored only when not the default.</summary>
        public static int EdgeSwitchDwellMilliseconds
        {
            get { return ReadInt(EdgeSwitchDwellValue, EdgeSwitch.DefaultDwellMilliseconds, 10, 5000); }
            set { StoreUnlessDefault(EdgeSwitchDwellValue, value, EdgeSwitch.DefaultDwellMilliseconds); }
        }

        /// <summary>How far in from the edge the zone reaches, in screen pixels; stored only when not the default.</summary>
        public static int EdgeSwitchDistancePixels
        {
            get { return ReadInt(EdgeSwitchDistanceValue, EdgeSwitch.DefaultDistancePixels, 1, 1000); }
            set { StoreUnlessDefault(EdgeSwitchDistanceValue, value, EdgeSwitch.DefaultDistancePixels); }
        }

        /// <summary>How far down from the top the Task View zone reaches; stored only when not the default.</summary>
        public static int EdgeSwitchTopDistancePixels
        {
            get { return ReadInt(EdgeSwitchTopDistanceValue, EdgeSwitch.DefaultTopDistancePixels, 1, 1000); }
            set { StoreUnlessDefault(EdgeSwitchTopDistanceValue, value, EdgeSwitch.DefaultTopDistancePixels); }
        }

        /// <summary>
        /// Whether resting on a taskbar button peeks at its window. Off by default, for the
        /// same reason as <see cref="EdgeSwitchEnabled"/>: it needs the same mouse hook.
        /// </summary>
        public static bool TaskbarPeek
        {
            get { return ReadInt(TaskbarPeekValue, 0, 0, 1) != 0; }
            set
            {
                if (value)
                {
                    Store(TaskbarPeekValue, 1);
                    return;
                }
                Forget(TaskbarPeekValue);
            }
        }

        /// <summary>
        /// Developer switch, deliberately not in the menu: a one-line readout of what the edge
        /// switcher sees, near the bottom-left of the screen, and its trail in edge.log. Off
        /// unless DevReadout=1 is set by hand.
        /// </summary>
        public static bool DevReadout
        {
            get { return ReadInt(DevReadoutValue, 0, 0, 1) != 0; }
        }

        /// <summary>A stored DWORD, or the fallback when it is missing, malformed or out of range.</summary>
        private static int ReadInt(string valueName, int fallback, int min, int max)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
            {
                if (key == null)
                {
                    return fallback;
                }
                object value = key.GetValue(valueName);
                if (value == null)
                {
                    return fallback;
                }
                try
                {
                    int stored = Convert.ToInt32(value);
                    return stored < min || stored > max ? fallback : stored;
                }
                catch (Exception)
                {
                    // Something else wrote a string or a blob here; treat it as unset.
                    return fallback;
                }
            }
        }

        private static void StoreUnlessDefault(string valueName, int value, int defaultValue)
        {
            if (value == defaultValue)
            {
                Forget(valueName);
                return;
            }
            Store(valueName, value);
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
