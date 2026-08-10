using System;

namespace SleepPicker
{
    /// <summary>
    /// One of the four dropdowns from Settings -> System -> Power &amp; sleep:
    /// a power setting (screen-off or sleep) on one power source (AC or DC).
    /// </summary>
    internal sealed class PowerTarget
    {
        private readonly string _label;
        private readonly Guid _subGroup;
        private readonly Guid _setting;
        private readonly bool _isAc;

        public PowerTarget(string label, Guid subGroup, Guid setting, bool isAc)
        {
            _label = label;
            _subGroup = subGroup;
            _setting = setting;
            _isAc = isAc;
        }

        public string Label { get { return _label; } }
        public Guid SubGroup { get { return _subGroup; } }
        public Guid Setting { get { return _setting; } }

        /// <summary>True for "plugged in", false for "on battery".</summary>
        public bool IsAc { get { return _isAc; } }

        /// <summary>
        /// Battery-only entries are meaningless on a desktop and are hidden there,
        /// which is what the Settings page itself does.
        /// </summary>
        public bool RequiresBattery { get { return !_isAc; } }
    }
}
