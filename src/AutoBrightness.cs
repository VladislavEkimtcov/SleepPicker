using System;

namespace SleepPicker
{
    /// <summary>
    /// "Change brightness automatically when lighting changes" -- Settings -> System ->
    /// Display -- the adaptive-brightness power setting under the Display subgroup, read
    /// and written through the same powrprof.dll calls as <see cref="PowerSettings"/>'s
    /// timeout targets, but offered as a single tick, matching the single checkbox
    /// Settings shows for it rather than a per-power-source submenu.
    ///
    /// The setting is stored once per power source, same as the timeouts; both are written
    /// together here so the tick cannot end up on for one source and off for the other,
    /// which the Settings checkbox never lets happen either.
    /// </summary>
    internal static class AutoBrightness
    {
        private static readonly Guid SubVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
        private static readonly Guid AdaptiveBrightness = new Guid("fbd9aa66-9553-4097-ba44-ed6e9d65eab8");

        private static readonly PowerTarget Ac = new PowerTarget("Auto Brightness", SubVideo, AdaptiveBrightness, true);
        private static readonly PowerTarget Dc = new PowerTarget("Auto Brightness", SubVideo, AdaptiveBrightness, false);

        /// <summary>
        /// True when on. Read from whichever power source is active, which is what the
        /// Settings checkbox itself would show; on mains when the source cannot be told,
        /// since that is the common case on a desktop. Missing entirely -- hardware with
        /// no light sensor, or a scheme that has never had this value written -- counts
        /// as off, matching Windows' own default.
        /// </summary>
        public static bool IsEnabled()
        {
            int percent;
            bool onMains;
            bool statusKnown = PowerSettings.TryGetBatteryStatus(out percent, out onMains);
            PowerTarget target = (!statusKnown || onMains) ? Ac : Dc;

            try
            {
                return PowerSettings.Read(target) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Writes both power sources together, as the Settings checkbox does.</summary>
        public static void SetEnabled(bool enabled)
        {
            uint value = enabled ? 1u : 0u;
            PowerSettings.Write(Ac, value);
            PowerSettings.Write(Dc, value);
        }
    }
}
