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
        private static readonly Guid SubEnergySaver = new Guid("de830923-a562-41af-a086-e3a2c6bad2da");
        private static readonly Guid EsBattThreshold = new Guid("e69653ca-cf7f-4f05-aa73-cb833fa90ad4");

        /// <summary>No system battery -- the machine is a desktop.</summary>
        private const byte BatteryFlagNoBattery = 128;

        /// <summary>Mains power is connected. The other values are 0 (on battery) and 255 (unknown).</summary>
        private const byte AcLineOnline = 1;

        /// <summary>SystemBatteryState, the one CallNtPowerInformation level used here.</summary>
        private const int SystemBatteryStateLevel = 5;

        private const uint StatusSuccess = 0;

        // What the battery driver says instead of a number it does not have.
        private const int BatteryUnknownRate = unchecked((int)0x80000000);
        private const uint BatteryUnknownCapacity = 0xFFFFFFFF;
        private const uint BatteryUnknownTime = 0xFFFFFFFF;

        /// <summary>
        /// The longest estimate worth repeating. Charging tapers off to a trickle as the
        /// battery fills, and dividing what is left to do by a rate of a few milliwatts
        /// gives days -- arithmetically sound and worthless to read.
        /// </summary>
        private const long LongestUsefulEstimate = 24 * 60 * 60;

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
        /// The charge, as a percentage, at or below which Windows switches battery saver on.
        /// Not one of the <see cref="Targets"/>: it is not a timeout and has no row of its
        /// own -- it is how <see cref="PowerMode"/> reaches battery saver, which Windows
        /// exposes no API for. Hidden from "powercfg /query" output, but it reads and writes
        /// like any other setting. DC only; battery saver never engages on mains.
        /// </summary>
        public static readonly PowerTarget EnergySaverThreshold =
            new PowerTarget("Battery saver charge level", SubEnergySaver, EsBattThreshold, false);

        /// <summary>
        /// The threshold Windows uses when nothing has changed it -- 20 on a stock install.
        /// Asked for rather than assumed, because an OEM image can ship a different one.
        /// </summary>
        public const uint DefaultEnergySaverThreshold = 20;

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

        /// <summary>
        /// One look at the battery, for the tray icon and its tooltip: the charge, the
        /// power source, and the time remaining to whichever end the battery is heading
        /// for. False in the same cases as <see cref="TryGetBatteryStatus"/> -- no
        /// battery, or a charge the firmware will not name.
        ///
        /// Taken once per refresh and handed on, rather than each caller asking again:
        /// two facts read a minute apart are two facts about two different moments.
        ///
        /// The time is the only part that needs a second call, and it is allowed to come
        /// back missing without failing the whole reading -- the charge is worth showing
        /// on its own, and an estimate is absent more often than not.
        /// </summary>
        public static bool TryRead(out BatteryReading reading)
        {
            reading = null;

            int percent;
            bool onMains;
            if (!TryGetBatteryStatus(out percent, out onMains))
            {
                return false;
            }

            // Whichever end the time counts down to is the one the power source implies:
            // ReadTimeRemaining answers nothing at all unless the battery agrees with it.
            reading = new BatteryReading(percent, onMains, ReadTimeRemaining(onMains), onMains);
            return true;
        }

        /// <summary>
        /// Seconds until the battery is empty, or until it is full when on mains, or
        /// <see cref="BatteryReading.Unknown"/>.
        ///
        /// CallNtPowerInformation rather than GetSystemPowerStatus, whose BatteryLifeTime
        /// is the same estimate with none of the workings: this one also reports the
        /// capacity and the rate, which is the only way to say anything at all about
        /// charging.
        /// </summary>
        private static uint ReadTimeRemaining(bool onMains)
        {
            SystemBatteryState state;
            uint size = (uint)Marshal.SizeOf(typeof(SystemBatteryState));
            uint result = NativeMethods.CallNtPowerInformation(
                SystemBatteryStateLevel, IntPtr.Zero, 0, out state, size);
            if (result != StatusSuccess || state.BatteryPresent == 0)
            {
                return BatteryReading.Unknown;
            }

            // The power source has now been read twice -- once by the caller for the
            // charge, once here -- and a cable moving in between would pair a time with
            // the wrong end of the journey. Disagreement drops the estimate rather than
            // showing it: the cable raises PowerModeChanged, which brings another reading
            // along a moment later.
            if ((state.AcOnLine != 0) != onMains)
            {
                return BatteryReading.Unknown;
            }

            if (!onMains)
            {
                // Windows' own estimate, and the one its battery flyout shows. Preferred
                // to the arithmetic below because it is smoothed over recent samples,
                // where an instantaneous rate swings with whatever the screen is doing.
                if (state.EstimatedTime != BatteryUnknownTime)
                {
                    return Usable(state.EstimatedTime);
                }
                // Missing for the first minute or so after the cable comes out, before
                // there are samples to smooth. Until then the rate answers on its own.
                if (state.Rate == BatteryUnknownRate || state.Rate >= 0 ||
                    state.RemainingCapacity == BatteryUnknownCapacity)
                {
                    return BatteryReading.Unknown;
                }
                return Usable((long)state.RemainingCapacity * 3600 / -(long)state.Rate);
            }

            // Charging has no estimate anywhere in Windows: not here, not in
            // GetSystemPowerStatus, whose BatteryLifeTime is -1 on mains by definition,
            // and not in WMI, whose Win32_Battery.TimeToFullCharge comes back empty on the
            // machines that matter. Windows' own flyout divides what is left to put in by
            // the rate it is going in at, so that is what this does.
            //
            // A battery reporting relative units rather than milliwatt-hours needs no
            // special case: its rate is in those same units per hour, and this is a ratio.
            if (state.Rate == BatteryUnknownRate || state.Rate <= 0 ||
                state.MaxCapacity == BatteryUnknownCapacity ||
                state.RemainingCapacity == BatteryUnknownCapacity ||
                state.RemainingCapacity >= state.MaxCapacity)
            {
                // A full battery, or one a vendor is holding at 80%, is taking nothing in
                // and will never fill. It has no time to report, only "charging".
                return BatteryReading.Unknown;
            }
            return Usable(((long)state.MaxCapacity - state.RemainingCapacity) * 3600 / state.Rate);
        }

        /// <summary>
        /// An estimate, unless it is too far off to mean anything -- see
        /// <see cref="LongestUsefulEstimate"/>.
        /// </summary>
        private static uint Usable(long seconds)
        {
            if (seconds <= 0 || seconds > LongestUsefulEstimate)
            {
                return BatteryReading.Unknown;
            }
            return (uint)seconds;
        }

        /// <summary>
        /// Whether Windows is currently in battery saver. Read from the same
        /// GetSystemPowerStatus call as everything else here rather than from a power
        /// setting: the setting says when battery saver *should* come on, and this says
        /// whether it actually is on, which is what the menu has to show.
        /// </summary>
        public static bool IsBatterySaverOn()
        {
            SystemPowerStatus status;
            if (!NativeMethods.GetSystemPowerStatus(out status))
            {
                return false;
            }
            return status.SystemStatusFlag != 0;
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
        /// The value Windows itself would use for a target, for putting one back the way it
        /// was found when nothing better is remembered.
        /// </summary>
        public static uint ReadDefault(PowerTarget target)
        {
            Guid scheme = GetActiveScheme();
            Guid subGroup = target.SubGroup;
            Guid setting = target.Setting;
            uint value;

            uint result = target.IsAc
                ? NativeMethods.PowerReadACDefaultIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out value)
                : NativeMethods.PowerReadDCDefaultIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out value);

            if (result != ErrorSuccess)
            {
                throw new Win32Exception((int)result, "Reading the power setting's default failed.");
            }
            return value;
        }

        /// <summary>
        /// Sets a target's value on the active scheme -- a timeout in seconds for the four
        /// <see cref="Targets"/>, a percentage for <see cref="EnergySaverThreshold"/>. The
        /// write alone does not take effect -- the scheme has to be re-applied afterwards,
        /// which is what PowerSetActiveScheme does here.
        /// </summary>
        public static void Write(PowerTarget target, uint value)
        {
            Guid scheme = GetActiveScheme();
            Guid subGroup = target.SubGroup;
            Guid setting = target.Setting;

            uint result = target.IsAc
                ? NativeMethods.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, value)
                : NativeMethods.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, value);

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

        /// <summary>
        /// Renders a time remaining, rounded to the minute: "2 h 15 min", "3 h", "45 min".
        /// Not <see cref="Describe"/>, which renders the exact multiples the timeout
        /// dropdowns are made of and would call an estimate of 8123 seconds "8123 seconds".
        /// </summary>
        public static string DescribeRemaining(uint seconds)
        {
            uint minutes = (seconds + 30) / 60;
            if (minutes == 0)
            {
                // Rounding has taken the last half-minute down to nothing, and "0 min"
                // reads as a battery that has already gone rather than one about to.
                minutes = 1;
            }
            if (minutes < 60)
            {
                return minutes.ToString() + " min";
            }

            uint hours = minutes / 60;
            minutes = minutes % 60;
            return minutes == 0
                ? hours.ToString() + " h"
                : hours.ToString() + " h " + minutes.ToString() + " min";
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

        /// <summary>
        /// SYSTEM_BATTERY_STATE. The three BOOLEAN spares are named rather than skipped
        /// because the fields after them are only in the right place if every byte before
        /// them is accounted for.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SystemBatteryState
        {
            public byte AcOnLine;
            public byte BatteryPresent;
            public byte Charging;
            public byte Discharging;
            public byte Spare1;
            public byte Spare2;
            public byte Spare3;
            public byte Tag;
            /// <summary>Capacity when full, in mWh -- what the battery holds now, not when new.</summary>
            public uint MaxCapacity;
            public uint RemainingCapacity;
            /// <summary>Milliwatts: positive going in, negative coming out.</summary>
            public int Rate;
            /// <summary>Seconds until empty. Unknown while on mains.</summary>
            public uint EstimatedTime;
            public uint DefaultAlert1;
            public uint DefaultAlert2;
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
            public static extern uint PowerReadACDefaultIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
                ref Guid subGroupGuid, ref Guid powerSettingGuid, out uint value);

            [DllImport("powrprof.dll", ExactSpelling = true)]
            public static extern uint PowerReadDCDefaultIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
                ref Guid subGroupGuid, ref Guid powerSettingGuid, out uint value);

            [DllImport("powrprof.dll", ExactSpelling = true)]
            public static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
                ref Guid subGroupGuid, ref Guid powerSettingGuid, uint value);

            [DllImport("powrprof.dll", ExactSpelling = true)]
            public static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
                ref Guid subGroupGuid, ref Guid powerSettingGuid, uint value);

            [DllImport("powrprof.dll", ExactSpelling = true)]
            public static extern uint CallNtPowerInformation(int informationLevel,
                IntPtr inputBuffer, uint inputBufferLength,
                out SystemBatteryState outputBuffer, uint outputBufferLength);

            [DllImport("kernel32.dll", ExactSpelling = true)]
            public static extern IntPtr LocalFree(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
        }
    }
}
