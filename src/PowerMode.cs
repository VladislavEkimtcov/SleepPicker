using System;

namespace SleepPicker
{
    /// <summary>
    /// One position of the power slider in Windows' own battery flyout -- "Better battery",
    /// "Best performance" and so on.
    ///
    /// Three of the four are an overlay laid over the active power scheme, and are set by
    /// GUID. The fourth, battery saver, is not: Windows reaches it another way, and
    /// <see cref="IsBatterySaver"/> is what tells the two apart. Its <see cref="Overlay"/>
    /// is meaningless and never read.
    /// </summary>
    internal sealed class PowerMode
    {
        private readonly string _label;
        private readonly Guid _overlay;
        private readonly bool _isBatterySaver;

        public PowerMode(string label, Guid overlay, bool isBatterySaver)
        {
            _label = label;
            _overlay = overlay;
            _isBatterySaver = isBatterySaver;
        }

        public string Label { get { return _label; } }

        /// <summary>
        /// The overlay to lay over the active scheme. Guid.Empty means no overlay at all --
        /// the scheme's own settings, which is a position in its own right.
        /// </summary>
        public Guid Overlay { get { return _overlay; } }

        /// <summary>
        /// True for battery saver alone, which is switched on through the charge level
        /// rather than through an overlay, and which Windows offers on battery only.
        /// </summary>
        public bool IsBatterySaver { get { return _isBatterySaver; } }

        /// <summary>
        /// Battery saver only shows up on battery, exactly as the flyout's slider drops its
        /// leftmost notch when the machine is plugged in.
        /// </summary>
        public bool RequiresBattery { get { return _isBatterySaver; } }
    }
}
