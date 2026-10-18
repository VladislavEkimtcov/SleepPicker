using System;
using Microsoft.Win32;

namespace SleepPicker
{
    /// <summary>
    /// The thumbnails the taskbar pops up over a button the pointer rests on. With the
    /// peek (<see cref="TaskbarPeek"/>) showing the real window instead, they are only in
    /// the way.
    ///
    /// Hidden by stretching the hover delay -- ExtendedUIHoverTime, a per-user value the
    /// user may write, no elevation -- to an hour, rather than through the "Turn off
    /// taskbar thumbnails" policy, which needs an administrator and also replaces the list
    /// a click on a grouped button shows with plain text. The delay leaves that list alone:
    /// it is opened by the click, not the hover.
    ///
    /// The taskbar reads the value only when it starts (traced: a settings-change broadcast
    /// makes it re-read its other options but not this one), so the change shows after an
    /// Explorer restart or the next sign-in.
    /// </summary>
    internal static class TaskbarPreviews
    {
        private const string AdvancedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string HoverTimeValue = "ExtendedUIHoverTime";

        /// <summary>An hour: a rest no pointer makes.</summary>
        private const int HiddenHoverMilliseconds = 60 * 60 * 1000;

        /// <summary>Anything longer than this counts as hidden, whoever set it.</summary>
        private const int HiddenThresholdMilliseconds = 60 * 1000;

        public static bool IsHidden()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AdvancedKeyPath, false))
            {
                object value = key == null ? null : key.GetValue(HoverTimeValue);
                return value is int && (int)value >= HiddenThresholdMilliseconds;
            }
        }

        /// <summary>Showing them deletes the value rather than writing Windows' default back,
        /// leaving the key as a machine that never touched this has it.</summary>
        public static void SetHidden(bool hidden)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AdvancedKeyPath))
            {
                if (hidden)
                {
                    key.SetValue(HoverTimeValue, HiddenHoverMilliseconds, RegistryValueKind.DWord);
                    return;
                }
                key.DeleteValue(HoverTimeValue, false);
            }
        }
    }
}
