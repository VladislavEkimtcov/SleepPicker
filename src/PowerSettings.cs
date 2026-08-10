using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SleepPicker
{
    /// <summary>
    /// Reads and writes the active power scheme's idle timeouts through powrprof.dll.
    ///
    /// The Win32 power API is used rather than parsing powercfg.exe output: powercfg's
    /// output is localised and would have to be screen-scraped, while these calls return
    /// raw values and work on any UI language. They also succeed for a standard user --
    /// the Power service performs the write on the caller's behalf, so no elevation is
    /// needed even though the schemes live under HKLM.
    /// </summary>
    internal static class PowerSettings
    {
        private const uint ErrorSuccess = 0;

        // Subgroup and setting GUIDs, as reported by "powercfg /query".
        private static readonly Guid SubVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
        private static readonly Guid VideoIdle = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
        private static readonly Guid SubSleep = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
        private static readonly Guid StandbyIdle = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

        /// <summary>No system battery -- the machine is a desktop.</summary>
        private const byte BatteryFlagNoBattery = 128;

        /// <summary>Mains power is connected. The other values are 0 (on battery) and 255 (unknown).</summary>
        private const byte AcLineOnline = 1;

        /// <summary>Timeout in seconds meaning "Never".</summary>
        public const uint Never = 0;

        /// <summary>
        /// The choices offered per setting, in seconds. These mirror the Windows
        /// Settings dropdowns exactly, so the menu is a drop-in replacement for them.
        /// </summary>
        public static readonly uint[] Presets = new uint[]
        {
            60, 120, 180, 300, 600, 900, 1200, 1500, 1800, 2700,
            3600, 7200, 10800, 14400, 18000,
            Never
        };

        /// <summary>The four dropdowns of the Power &amp; sleep page, in the same order.</summary>
        public static readonly PowerTarget[] Targets = new PowerTarget[]
        {
            new PowerTarget("Screen off on battery", SubVideo, VideoIdle, false),
            new PowerTarget("Screen off when plugged in", SubVideo, VideoIdle, true),
            new PowerTarget("Sleep on battery", SubSleep, StandbyIdle, false),
            new PowerTarget("Sleep when plugged in", SubSleep, StandbyIdle, true)
        };

        /// <summary>
        /// Whether this machine has a battery. Re-checked on every menu open rather than
        /// cached, because a battery can be removed or a docked tablet undocked at runtime.
        /// </summary>
        public static bool HasBattery()
        {
            SystemPowerStatus status;
            if (!NativeMethods.GetSystemPowerStatus(out status))
            {
                // If the status is unavailable, assume there is a battery: showing an
                // option that does nothing is a smaller failure than hiding a real one.
                return true;
            }
            return status.BatteryFlag != BatteryFlagNoBattery;
        }

        /// <summary>
        /// Charge remaining, 0 to 100, and whether the machine is running on mains power.
        /// False when there is no battery, or when the firmware does not report a level --
        /// GetSystemPowerStatus answers 255 for "unknown", which is what virtual machines
        /// and some docks give back, and a caller must not draw 255% of a moon.
        ///
        /// Both facts come out of one call so that they cannot disagree: asking twice
        /// could catch the charge from before a cable went in and the power source from
        /// after it, and draw a moon that was never true.
        ///
        /// Mains power, rather than the BatteryFlag "charging" bit: a laptop holding its
        /// battery at 80% on purpose is not charging, but it is not draining either, and
        /// the flag is the less reliable of the two across firmware.
        /// </summary>
        public static bool TryGetBatteryStatus(out int percent, out bool onMains)
        {
            percent = 0;
            onMains = false;

            SystemPowerStatus status;
            if (!NativeMethods.GetSystemPowerStatus(out status))
            {
                return false;
            }
            if (status.BatteryFlag == BatteryFlagNoBattery || status.BatteryLifePercent > 100)
            {
                return false;
            }

            percent = status.BatteryLifePercent;
            onMains = status.ACLineStatus == AcLineOnline;
            return true;
        }

        /// <summary>GUID of the power scheme currently in effect.</summary>
        public static Guid GetActiveScheme()
        {
            IntPtr buffer = IntPtr.Zero;
            uint result = NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out buffer);
            if (result != ErrorSuccess)
            {
                throw new Win32Exception((int)result, "PowerGetActiveScheme failed.");
            }
            try
            {
                return (Guid)Marshal.PtrToStructure(buffer, typeof(Guid));
            }
            finally
            {
                NativeMethods.LocalFree(buffer);
            }
        }

        /// <summary>Current timeout in seconds for a target under the active scheme.</summary>
        public static uint Read(PowerTarget target)
        {
            Guid scheme = GetActiveScheme();
            Guid subGroup = target.SubGroup;
            Guid setting = target.Setting;
            uint value;

            uint result = target.IsAc
                ? NativeMethods.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out value)
                : NativeMethods.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out value);

            if (result != ErrorSuccess)
            {
                throw new Win32Exception((int)result, "Reading the power setting failed.");
            }
            return value;
        }

        /// <summary>
        /// Sets a target's timeout, in seconds, on the active scheme. The write alone does
        /// not take effect -- the scheme has to be re-applied afterwards, which is what
        /// PowerSetActiveScheme does here.
        /// </summary>
        public static void Write(PowerTarget target, uint seconds)
        {
            Guid scheme = GetActiveScheme();
            Guid subGroup = target.SubGroup;
            Guid setting = target.Setting;

            uint result = target.IsAc
                ? NativeMethods.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, seconds)
                : NativeMethods.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, seconds);

            if (result != ErrorSuccess)
            {
                throw new Win32Exception((int)result, "Writing the power setting failed.");
            }

            result = NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            if (result != ErrorSuccess)
            {
                throw new Win32Exception((int)result, "Applying the power scheme failed.");
            }
        }

        /// <summary>Renders a timeout the way the Settings dropdowns render it.</summary>
        public static string Describe(uint seconds)
        {
            if (seconds == Never)
            {
                return "Never";
            }
            if (seconds % 3600 == 0)
            {
                uint hours = seconds / 3600;
                return hours == 1 ? "1 hour" : hours.ToString() + " hours";
            }
            if (seconds % 60 == 0)
            {
                uint minutes = seconds / 60;
                return minutes == 1 ? "1 minute" : minutes.ToString() + " minutes";
            }
            return seconds == 1 ? "1 second" : seconds.ToString() + " seconds";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        private static class NativeMethods
        {
            [DllImport("powrprof.dll", ExactSpelling = true)]
            public static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

            [DllImport("powrprof.dll", ExactSpelling = true)]
            public static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

            [DllImport("powrprof.dll", ExactSpelling = true)]
            public static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
                ref Guid subGroupGuid, ref Guid powerSettingGuid, out uint value);

            [DllImport("powrprof.dll", ExactSpelling = true)]
            public static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
                ref Guid subGroupGuid, ref Guid powerSettingGuid, out uint value);

            [DllImport("powrprof.dll", ExactSpelling = true)]
            public static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
                ref Guid subGroupGuid, ref Guid powerSettingGuid, uint value);

            [DllImport("powrprof.dll", ExactSpelling = true)]
            public static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
                ref Guid subGroupGuid, ref Guid powerSettingGuid, uint value);

            [DllImport("kernel32.dll", ExactSpelling = true)]
            public static extern IntPtr LocalFree(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
        }
    }
}
