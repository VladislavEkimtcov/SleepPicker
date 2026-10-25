using System;
using Microsoft.Win32;

namespace SleepPicker
{
    /// <summary>What the taskbar pops up over a button the pointer rests on.</summary>
    internal enum TaskbarPreviewMode
    {
        /// <summary>Windows' own default: a thumbnail of each window.</summary>
        Thumbnails,

        /// <summary>The list Windows otherwise keeps for crowded groups: a row per window,
        /// its title and a close button, and no thumbnails.</summary>
        List,

        /// <summary>Nothing at all; the peek (<see cref="TaskbarPeek"/>) shows the real window instead.</summary>
        Hidden,
    }

    /// <summary>
    /// The popups the taskbar shows over a button the pointer rests on. With the peek
    /// (<see cref="TaskbarPeek"/>) showing the real window, thumbnails are only in the way;
    /// either nothing or a plain list of titles, each with its close button, is offered
    /// instead. Both are per-user values the user may write, no elevation.
    ///
    /// Hidden by stretching the hover delay -- ExtendedUIHoverTime -- to an hour, rather
    /// than through the "Turn off taskbar thumbnails" policy, which needs an administrator
    /// and also replaces the list a click on a grouped button shows with plain text. The
    /// delay leaves that list alone: it is opened by the click, not the hover.
    ///
    /// The list is the one Windows switches to when a group has more windows than
    /// NumThumbnails: lowered to nothing, every button gets it. The close button on each
    /// row is Windows' own, so it closes a window without bringing it forward.
    ///
    /// The taskbar reads both values only when it starts (traced for the delay: a
    /// settings-change broadcast makes it re-read its other options but not this one), so
    /// a change shows after an Explorer restart or the next sign-in.
    /// </summary>
    internal static class TaskbarPreviews
    {
        private const string AdvancedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string HoverTimeValue = "ExtendedUIHoverTime";
        private const string TaskbandKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";
        private const string NumThumbnailsValue = "NumThumbnails";

        /// <summary>An hour: a rest no pointer makes.</summary>
        private const int HiddenHoverMilliseconds = 60 * 60 * 1000;

        /// <summary>Anything longer than this counts as hidden, whoever set it.</summary>
        private const int HiddenThresholdMilliseconds = 60 * 1000;

        /// <summary>No thumbnails before the list: every button lists its windows.</summary>
        private const int ListThumbnails = 0;

        /// <summary>A threshold this low counts as the list, whoever set it: at one, only a
        /// lone window still gets a thumbnail.</summary>
        private const int ListThresholdThumbnails = 1;

        public static TaskbarPreviewMode GetMode()
        {
            if (ReadInt(AdvancedKeyPath, HoverTimeValue, 0) >= HiddenThresholdMilliseconds)
            {
                return TaskbarPreviewMode.Hidden;
            }
            if (ReadInt(TaskbandKeyPath, NumThumbnailsValue, int.MaxValue) <= ListThresholdThumbnails)
            {
                return TaskbarPreviewMode.List;
            }
            return TaskbarPreviewMode.Thumbnails;
        }

        /// <summary>Writes only the value the mode needs and deletes the other, rather than
        /// writing Windows' defaults back: the two exclude each other anyway (a hover that never
        /// comes shows no list either), and thumbnails leave both keys as a machine that never
        /// touched this has them.</summary>
        public static void SetMode(TaskbarPreviewMode mode)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AdvancedKeyPath))
            {
                if (mode == TaskbarPreviewMode.Hidden)
                {
                    key.SetValue(HoverTimeValue, HiddenHoverMilliseconds, RegistryValueKind.DWord);
                }
                else
                {
                    key.DeleteValue(HoverTimeValue, false);
                }
            }
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(TaskbandKeyPath))
            {
                if (mode == TaskbarPreviewMode.List)
                {
                    key.SetValue(NumThumbnailsValue, ListThumbnails, RegistryValueKind.DWord);
                }
                else
                {
                    key.DeleteValue(NumThumbnailsValue, false);
                }
            }
        }

        private static int ReadInt(string keyPath, string name, int missing)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, false))
            {
                object value = key == null ? null : key.GetValue(name);
                return value is int ? (int)value : missing;
            }
        }
    }
}
