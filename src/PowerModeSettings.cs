using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SleepPicker
{
    /// <summary>
    /// The power slider from Windows' battery flyout -- "Power mode (on battery)" -- read
    /// and written through powrprof.dll.
    ///
    /// Windows 10 has no Settings page for this: the slider exists only in the flyout that
    /// hangs off the battery icon, so hiding that icon takes the setting with it. This is
    /// what puts it back, which is why the menu row appears only while the icon is hidden.
    ///
    /// Three of the four positions are an *overlay*: a set of tweaks laid over the active
    /// power scheme without changing it, chosen with PowerSetActiveOverlayScheme and stored
    /// per power source, so the machine can run one mode on battery and another on mains.
    /// These exports are undocumented but have been present and stable since Windows 10
    /// 1709; they are absent on anything older, which <see cref="IsAvailable"/> checks for
    /// rather than assuming.
    ///
    /// The reading is deliberately of the *effective* overlay rather than the one last
    /// asked for. Windows lowers the mode itself as a battery runs down, and the menu is
    /// meant to say what the machine is doing now, not what it was told an hour ago.
    ///
    /// Battery saver, the slider's leftmost notch, is the exception: Windows exposes no API
    /// for it at all. It comes on whenever the charge is at or below the "charge level"
    /// setting, so it is switched on by raising that level to 100 -- every charge is at or
    /// below 100 -- and switched off by putting the level back. What it was is remembered in
    /// <see cref="Settings"/>, because the level is the only trace and a forgotten 100 would
    /// keep battery saver on for good.
    /// </summary>
    internal static class PowerModeSettings
    {
        private const uint ErrorSuccess = 0;

        /// <summary>Insufficient buffer -- the name is longer than the one we offered.</summary>
        private const uint ErrorMoreData = 234;

        // Overlay GUIDs, as reported by PowerEnumerate with ACCESS_OVERLAY_SCHEME. Windows
        // names them "Better Battery-life Overlay" and "Max Performance Overlay"; the labels
        // below are the flyout's wording instead, which is what a user has actually seen.
        private static readonly Guid OverlayBetterBattery = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
        private static readonly Guid OverlayBestPerformance = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");

        /// <summary>
        /// The charge level that forces battery saver on: no charge is above 100, so the
        /// condition Windows watches for is always met.
        /// </summary>
        private const uint ForcedEnergySaverThreshold = 100;

        /// <summary>
        /// The four positions of the slider, in the order the slider has them -- battery
        /// life on the left, performance on the right.
        ///
        /// "Better performance" is no overlay at all rather than a GUID of its own: it is
        /// the active scheme left to speak for itself, which is what Windows treats as the
        /// recommended position.
        /// </summary>
        public static readonly PowerMode[] All = new PowerMode[]
        {
            new PowerMode("Battery saver", Guid.Empty, true),
            new PowerMode("Better battery", OverlayBetterBattery, false),
            new PowerMode("Better performance", Guid.Empty, false),
            new PowerMode("Best performance", OverlayBestPerformance, false)
        };

        /// <summary>-1 until asked, then 0 or 1. Exports do not come and go while we run.</summary>
        private static int _available = -1;

        /// <summary>
        /// Whether this Windows has the overlay API. False on anything before Windows 10
        /// 1709, where the menu row hides itself rather than throwing when clicked.
        /// </summary>
        public static bool IsAvailable()
        {
            if (_available < 0)
            {
                try
                {
                    Guid effective;
                    _available = NativeMethods.PowerGetEffectiveOverlayScheme(out effective) == ErrorSuccess ? 1 : 0;
                }
                catch (EntryPointNotFoundException)
                {
                    _available = 0;
                }
                catch (DllNotFoundException)
                {
                    _available = 0;
                }
            }
            return _available == 1;
        }

        /// <summary>
        /// The overlay actually in force, which is not always the one that was asked for --
        /// see the note on this class.
        /// </summary>
        public static Guid GetEffectiveOverlay()
        {
            Guid effective;
            uint result = NativeMethods.PowerGetEffectiveOverlayScheme(out effective);
            if (result != ErrorSuccess)
            {
                throw new Win32Exception((int)result, "Reading the power mode failed.");
            }
            return effective;
        }

        /// <summary>
        /// The mode in force: battery saver if Windows is in it, otherwise whichever of
        /// <see cref="All"/> matches the effective overlay -- or null when the overlay is
        /// one we do not offer, which an OEM tool or a power plan can leave behind.
        /// </summary>
        public static PowerMode GetEffectiveMode(Guid effectiveOverlay)
        {
            if (PowerSettings.IsBatterySaverOn())
            {
                return All[0];
            }

            for (int i = 0; i < All.Length; i++)
            {
                if (!All[i].IsBatterySaver && All[i].Overlay == effectiveOverlay)
                {
                    return All[i];
                }
            }
            return null;
        }

        /// <summary>
        /// What Windows calls an overlay, for naming one that is not among the four. These
        /// are the internal names -- "High Performance Overlay" -- rather than the flyout's,
        /// but a name Windows chose beats a bare GUID, and this is only reached when
        /// something other than SleepPicker set the mode.
        /// </summary>
        public static string DescribeOverlay(Guid overlay)
        {
            byte[] buffer = new byte[512];
            uint size = (uint)buffer.Length;
            uint result = NativeMethods.PowerReadFriendlyName(
                IntPtr.Zero, ref overlay, IntPtr.Zero, IntPtr.Zero, buffer, ref size);

            if (result == ErrorMoreData && size > buffer.Length)
            {
                buffer = new byte[size];
                result = NativeMethods.PowerReadFriendlyName(
                    IntPtr.Zero, ref overlay, IntPtr.Zero, IntPtr.Zero, buffer, ref size);
            }

            if (result != ErrorSuccess || size == 0)
            {
                return overlay.ToString("B");
            }

            // A counted buffer of Unicode, with the terminator inside the count.
            int bytes = (int)Math.Min(size, (uint)buffer.Length);
            string name = Encoding.Unicode.GetString(buffer, 0, bytes).TrimEnd('\0');
            return name.Length == 0 ? overlay.ToString("B") : name;
        }

        /// <summary>
        /// Puts the machine into a mode. The overlay is set first and battery saver released
        /// afterwards, so that the thing actually asked for lands even if the tidying up
        /// behind it fails.
        /// </summary>
        public static void Apply(PowerMode mode)
        {
            if (mode.IsBatterySaver)
            {
                ForceBatterySaver();
                return;
            }

            Guid overlay = mode.Overlay;
            uint result = NativeMethods.PowerSetActiveOverlayScheme(ref overlay);
            if (result != ErrorSuccess)
            {
                throw new Win32Exception((int)result, "Changing the power mode failed.");
            }

            // Leaving the charge level at 100 would put battery saver straight back on and
            // drag the mode down again, making the menu look as though it had ignored the
            // click.
            ReleaseBatterySaver();
        }

        /// <summary>
        /// Raises the charge level so that battery saver comes on, remembering what it was.
        /// The old value is written down before the level is changed: if the write then
        /// fails, releasing simply puts back the value that is already there.
        /// </summary>
        private static void ForceBatterySaver()
        {
            uint ignored;
            if (!Settings.TryGetForcedEnergySaverThreshold(out ignored))
            {
                Settings.RememberForcedEnergySaverThreshold(ReadThresholdToRestore());
            }
            PowerSettings.Write(PowerSettings.EnergySaverThreshold, ForcedEnergySaverThreshold);
        }

        /// <summary>
        /// The charge level to go back to. Normally the one in force, but a level already at
        /// 100 -- left by a crash of ours, or by another tool -- is no use to come back to,
        /// so Windows' own default stands in.
        /// </summary>
        private static uint ReadThresholdToRestore()
        {
            uint current;
            try
            {
                current = PowerSettings.Read(PowerSettings.EnergySaverThreshold);
            }
            catch (Exception)
            {
                return PowerSettings.DefaultEnergySaverThreshold;
            }

            if (current < ForcedEnergySaverThreshold)
            {
                return current;
            }

            try
            {
                uint fallback = PowerSettings.ReadDefault(PowerSettings.EnergySaverThreshold);
                return fallback < ForcedEnergySaverThreshold
                    ? fallback
                    : PowerSettings.DefaultEnergySaverThreshold;
            }
            catch (Exception)
            {
                return PowerSettings.DefaultEnergySaverThreshold;
            }
        }

        /// <summary>
        /// Puts the charge level back, if it was us that raised it. Does nothing at all
        /// otherwise: a level set by the user or by another tool is not ours to undo.
        /// The memory is dropped only once the write has succeeded, so a failure can be
        /// retried the next time a mode is picked.
        /// </summary>
        public static void ReleaseBatterySaver()
        {
            uint previous;
            if (!Settings.TryGetForcedEnergySaverThreshold(out previous))
            {
                return;
            }
            PowerSettings.Write(PowerSettings.EnergySaverThreshold, previous);
            Settings.ForgetForcedEnergySaverThreshold();
        }

        private static class NativeMethods
        {
            [DllImport("powrprof.dll", ExactSpelling = true)]
            public static extern uint PowerGetEffectiveOverlayScheme(out Guid overlaySchemeGuid);

            // By reference, like every other GUID in this API family: the native function
            // takes a pointer to one. Declaring it by value survives x64 by luck -- the Win64
            // ABI hands a 16-byte struct over as a pointer to a copy anyway -- and then dies
            // with an access violation in a 32-bit process, where the GUID goes onto the
            // stack whole and the callee reads its first four bytes as an address. This
            // executable is AnyCPU with Prefer32Bit, so it is a 32-bit process even here.
            [DllImport("powrprof.dll", ExactSpelling = true)]
            public static extern uint PowerSetActiveOverlayScheme(ref Guid overlaySchemeGuid);

            [DllImport("powrprof.dll", ExactSpelling = true)]
            public static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid,
                IntPtr subGroupGuid, IntPtr powerSettingGuid, byte[] buffer, ref uint bufferSize);
        }
    }
}
