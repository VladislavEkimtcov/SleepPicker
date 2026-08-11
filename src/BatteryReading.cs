namespace SleepPicker
{
    /// <summary>
    /// Everything one look at the battery found: the charge, the power source, and how
    /// long the one has left at the other. Taken as a single snapshot and passed around
    /// rather than re-read by each thing that wants a part of it, so the icon and the
    /// tooltip drawn from it are always describing the same moment.
    /// </summary>
    internal sealed class BatteryReading
    {
        /// <summary>No estimate. The usual case for a minute after the cable moves.</summary>
        public const uint Unknown = 0xFFFFFFFF;

        private readonly int _percent;
        private readonly bool _onMains;
        private readonly uint _seconds;
        private readonly bool _toFull;

        public BatteryReading(int percent, bool onMains, uint seconds, bool toFull)
        {
            _percent = percent;
            _onMains = onMains;
            _seconds = seconds;
            _toFull = toFull;
        }

        /// <summary>Charge remaining, 0 to 100.</summary>
        public int Percent { get { return _percent; } }

        /// <summary>Whether the machine is running on mains power.</summary>
        public bool OnMains { get { return _onMains; } }

        /// <summary>
        /// Seconds until the battery is empty, or until it is full, according to
        /// <see cref="ToFull"/> -- or <see cref="Unknown"/>. Check <see cref="HasTime"/>
        /// first: an estimate is missing more often than it is present, and a battery
        /// being held at 80% by its vendor never has one at all.
        /// </summary>
        public uint Seconds { get { return _seconds; } }

        /// <summary>Which end <see cref="Seconds"/> counts down to: full, or empty.</summary>
        public bool ToFull { get { return _toFull; } }

        /// <summary>Whether there is an estimate to show.</summary>
        public bool HasTime { get { return _seconds != Unknown; } }
    }
}
