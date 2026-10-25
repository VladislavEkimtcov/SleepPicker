using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SleepPicker
{
    /// <summary>
    /// The whole user interface: one notification-area icon whose menu mirrors the four
    /// dropdowns of Settings -> System -> Power &amp; sleep. There is deliberately no
    /// window, no taskbar button and no settings dialog -- the one dialog in the program
    /// is the confirmation before restarting Explorer, which is not a setting but a
    /// warning the user has to be able to decline.
    /// </summary>
    internal sealed class TrayApp : ApplicationContext
    {
        /// <summary>
        /// How often the moon is redrawn. A battery moves by one percent every few
        /// minutes at best, so anything faster would be redrawing the same picture; a
        /// minute is also short enough that the icon is never visibly stale.
        /// </summary>
        private const int RefreshIntervalMilliseconds = 60 * 1000;

        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem[] _targetItems;
        private readonly ToolStripMenuItem _powerModeItem;
        private readonly ToolStripMenuItem _darkModeItem;
        private readonly ToolStripMenuItem _autoBrightnessItem;
        private readonly ToolStripMenuItem _mouseAccelerationItem;
        private readonly ToolStripMenuItem _autoStartItem;
        private readonly ToolStripMenuItem _edgeSwitchItem;
        private readonly ToolStripMenuItem _edgeSensitivityItem;
        private readonly ToolStripMenuItem _edgeDistanceItem;
        private readonly ToolStripMenuItem _edgeTopDistanceItem;
        private readonly ToolStripMenuItem _taskbarItem;
        private readonly ToolStripMenuItem _taskbarPeekItem;
        private readonly ToolStripMenuItem _hoverFocusItem;

        /// <summary>One row per <see cref="TaskbarPreviewMode"/>; each Tag is its mode.</summary>
        private readonly ToolStripMenuItem[] _previewModeItems;
        private readonly EdgePreview _edgePreview = new EdgePreview();

        /// <summary>
        /// The mouse hook behind the edge switching, the taskbar peek and the hover focus:
        /// running while any is on, null while all are off.
        /// </summary>
        private EdgeSwitch _edgeSwitch;

        /// <summary>
        /// Rebuilds <see cref="_edgeSwitch"/> once a minute while it is quiet. Turning the
        /// feature off and on is what brought a dead one back, and a fresh one costs a few
        /// milliseconds -- so it is simply done regularly, whatever the cause turns out to be.
        /// </summary>
        private readonly System.Windows.Forms.Timer _edgeRefreshTimer = new System.Windows.Forms.Timer();

        /// <summary>Rest times offered, shortest (most sensitive) first, with their names.</summary>
        private static readonly int[] EdgeDwellPresets = new int[] { 100, 200, 300, 500, 800 };
        private static readonly string[] EdgeDwellNames = new string[] { "Highest", "High", "Medium", "Low", "Lowest" };

        /// <summary>Zone widths offered, in screen pixels.</summary>
        private static readonly int[] EdgeDistancePresets = new int[] { 2, 8, 16, 24, 32, 48, 64, 96, 128 };

        /// <summary>Top zone thicknesses offered: thicker, for the viewer's title bar and toolbar.</summary>
        private static readonly int[] EdgeTopDistancePresets = new int[] { 16, 32, 48, 64, 96, 128, 160 };
        private readonly ToolStripMenuItem _dynamicIconItem;
        private readonly ToolStripMenuItem _hideBatteryMeterItem;
#if ENABLE_AUTO_UPDATE
        private readonly ToolStripMenuItem _updateStatusItem;
#endif
        private readonly SingleInstance _singleInstance;
        private readonly SynchronizationContext _uiContext;

        /// <summary>The fixed artwork, shown whenever the moon is not.</summary>
        private readonly Icon _staticIcon;
        // Qualified: System.Threading also has a Timer, and this one has to tick on the UI
        // thread, where the icon can be swapped without marshalling.
        private readonly System.Windows.Forms.Timer _refreshTimer;

        /// <summary>
        /// The same charge drawn both ways -- waning for a battery going down, waxing for
        /// one on mains -- owned here, or null. Both are drawn together and kept, so that
        /// plugging the cable in swaps a reference rather than starting a redraw: a moon
        /// that turned round half a second after the cable went in would read as a
        /// coincidence rather than as an answer.
        /// </summary>
        private Icon _waningMoon;
        private Icon _waxingMoon;

        // What the pair was drawn for, so an unchanged charge costs nothing.
        private int _shownPercent = -1;
        private int _shownSize = -1;

        public TrayApp(SingleInstance singleInstance)
        {
            _uiContext = SynchronizationContext.Current;
            _singleInstance = singleInstance;
            _singleInstance.ShowMenuRequested += OnShowMenuRequested;

            _menu = new ContextMenuStrip();
            _menu.Opening += OnMenuOpening;

            _targetItems = new ToolStripMenuItem[PowerSettings.Targets.Length];
            for (int i = 0; i < PowerSettings.Targets.Length; i++)
            {
                PowerTarget target = PowerSettings.Targets[i];
                ToolStripMenuItem item = new ToolStripMenuItem(target.Label);
                item.Tag = target;
                // Populated on open so the ticks always reflect the live scheme.
                item.DropDownItems.Add(new ToolStripMenuItem("(reading...)"));
                _targetItems[i] = item;
                _menu.Items.Add(item);
            }

            _menu.Items.Add(new ToolStripSeparator());

            // Between the sleep rows and the application's own settings, because it belongs
            // with the former: it is a Windows power setting, not a preference of ours.
            _powerModeItem = new ToolStripMenuItem("Power Mode");
            _powerModeItem.ToolTipText =
                "The power slider from Windows' battery flyout — shown here because hiding " +
                "the battery icon hides the only place Windows offers it.";
            // Populated on open, like the timeout rows above.
            _powerModeItem.DropDownItems.Add(new ToolStripMenuItem("(reading...)"));
            _menu.Items.Add(_powerModeItem);

            // Alongside Power Mode: another system-level setting Windows keeps off its own
            // Settings pages in any convenient form, offered here as a plain on/off tick.
            _darkModeItem = new ToolStripMenuItem("Dark Mode");
            _darkModeItem.Click += OnDarkModeClick;
            _menu.Items.Add(_darkModeItem);

            _autoBrightnessItem = new ToolStripMenuItem("Auto Brightness");
            _autoBrightnessItem.ToolTipText =
                "Change brightness automatically when lighting changes.";
            _autoBrightnessItem.Click += OnAutoBrightnessClick;
            _menu.Items.Add(_autoBrightnessItem);

            _menu.Items.Add(new ToolStripSeparator());

            _mouseAccelerationItem = new ToolStripMenuItem("Mouse acceleration");
            _mouseAccelerationItem.ToolTipText =
                "Enhance pointer precision — dynamically adjust pointer speed based on mouse movement.";
            _mouseAccelerationItem.Click += OnMouseAccelerationClick;
            _menu.Items.Add(_mouseAccelerationItem);

            _menu.Items.Add(new ToolStripSeparator());

            _autoStartItem = new ToolStripMenuItem("Start with Windows");
            _autoStartItem.Click += OnAutoStartClick;
            _menu.Items.Add(_autoStartItem);

            // Clicking the row itself turns the feature on and off; its submenu tunes it.
            // A row with a submenu stays open when clicked, so the tick changes in place.
            _edgeSwitchItem = new ToolStripMenuItem("Switch desktops at screen edges");
            _edgeSwitchItem.ToolTipText =
                "Rest the pointer near the left or right edge of the screen, or scroll over " +
                "the taskbar, to move to the previous or next desktop. Fling it up to the top " +
                "for Task View.";
            _edgeSwitchItem.Click += OnEdgeSwitchClick;
            _edgeSensitivityItem = new ToolStripMenuItem("Sensitivity");
            _edgeSensitivityItem.ToolTipText = "How long the pointer has to rest near the edge.";
            _edgeSensitivityItem.DropDownItems.Add(new ToolStripMenuItem("(reading...)"));
            _edgeDistanceItem = new ToolStripMenuItem("Distance");
            _edgeDistanceItem.ToolTipText =
                "How far in from the edge still counts. Widen it when the pointer leaves a " +
                "small VNC viewer window before it reaches the edge.";
            _edgeDistanceItem.DropDownItems.Add(new ToolStripMenuItem("(reading...)"));
            _edgeTopDistanceItem = new ToolStripMenuItem("Top distance");
            _edgeTopDistanceItem.ToolTipText =
                "How far down from the top a fling still opens Task View. Thick, because the " +
                "viewer's title bar and toolbar take the pointer before it reaches the top.";
            _edgeTopDistanceItem.DropDownItems.Add(new ToolStripMenuItem("(reading...)"));
            _edgeSwitchItem.DropDownItems.Add(_edgeSensitivityItem);
            _edgeSwitchItem.DropDownItems.Add(_edgeDistanceItem);
            _edgeSwitchItem.DropDownItems.Add(_edgeTopDistanceItem);
            _menu.Items.Add(_edgeSwitchItem);

            // A plain row, like the timeout rows: what it holds is several settings, not one.
            _taskbarItem = new ToolStripMenuItem("Taskbar modification");

            _taskbarPeekItem = new ToolStripMenuItem("Peek at windows from taskbar buttons");
            _taskbarPeekItem.ToolTipText =
                "Rest the pointer on a taskbar button and every other window turns to glass, " +
                "as hovering its thumbnail does -- without waiting for the thumbnail.";
            _taskbarPeekItem.Click += OnTaskbarPeekClick;
            _taskbarItem.DropDownItems.Add(_taskbarPeekItem);

            _hoverFocusItem = new ToolStripMenuItem("Focus windows on hover");
            _hoverFocusItem.ToolTipText =
                "Rest the pointer on a taskbar button and its window comes to the front, as a click " +
                "would. Not for the window already in front, which a click would minimize. Taps Alt " +
                "when Windows refuses, which lifts its lock on the foreground.";
            _hoverFocusItem.Click += OnHoverFocusClick;
            _taskbarItem.DropDownItems.Add(_hoverFocusItem);

            _taskbarItem.DropDownItems.Add(new ToolStripSeparator());

            // Two settings in Windows, one choice here: they exclude each other (a hover that
            // never comes shows no list either), so they are offered as one.
            _previewModeItems = new ToolStripMenuItem[]
            {
                PreviewModeItem(TaskbarPreviewMode.Thumbnails, "Thumbnail previews",
                    "Windows' own default: a thumbnail of each window over a resting pointer."),
                PreviewModeItem(TaskbarPreviewMode.List, "Title list with close buttons",
                    "A row per window -- its title and a close button -- instead of thumbnails. " +
                    "The button closes a window without bringing it forward."),
                PreviewModeItem(TaskbarPreviewMode.Hidden, "No previews",
                    "Nothing over a resting pointer; the peek shows the real window instead. " +
                    "A click on a grouped button still lists its windows."),
            };
            for (int i = 0; i < _previewModeItems.Length; i++)
            {
                _previewModeItems[i].ToolTipText += " Restarts Explorer; you are asked first.";
                _taskbarItem.DropDownItems.Add(_previewModeItems[i]);
            }
            _menu.Items.Add(_taskbarItem);

            _dynamicIconItem = new ToolStripMenuItem("Dynamic tray icon");
            _dynamicIconItem.ToolTipText =
                "Show the battery charge as the moon's phase — waning on battery, waxing on mains.";
            _dynamicIconItem.Click += OnDynamicIconClick;
            _menu.Items.Add(_dynamicIconItem);

            _hideBatteryMeterItem = new ToolStripMenuItem("Hide the Windows battery icon");
            _hideBatteryMeterItem.ToolTipText =
                "Switch off Windows' own battery meter, leaving the moon as the only one. " +
                "Needs an administrator's approval and restarts Explorer; you are asked first.";
            _hideBatteryMeterItem.Click += OnHideBatteryMeterClick;
            _menu.Items.Add(_hideBatteryMeterItem);

#if ENABLE_AUTO_UPDATE
            _updateStatusItem = new ToolStripMenuItem(AutoUpdater.StatusText);
            _updateStatusItem.Enabled = false;
            _menu.Items.Add(_updateStatusItem);

            AutoUpdater.StatusChanged += OnAutoUpdaterStatusChanged;
#endif

            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += OnExitClick;
            _menu.Items.Add(exitItem);

            _staticIcon = LoadTrayIcon();

            _notifyIcon = new NotifyIcon();
            _notifyIcon.Text = "SleepPicker";
            _notifyIcon.ContextMenuStrip = _menu;
            // Left-click opens the same menu, so either button works.
            _notifyIcon.MouseUp += OnIconMouseUp;
            RefreshIcon();
            _notifyIcon.Visible = true;

            _refreshTimer = new System.Windows.Forms.Timer();
            _refreshTimer.Interval = RefreshIntervalMilliseconds;
            _refreshTimer.Tick += OnRefreshTick;
            _refreshTimer.Enabled = IsRefreshWanted();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            ApplyPointerFeatures();

            _edgeRefreshTimer.Interval = 60 * 1000;
            _edgeRefreshTimer.Tick += OnEdgeRefreshTick;
            _edgeRefreshTimer.Start();
        }

        private void OnEdgeRefreshTick(object sender, EventArgs e)
        {
            if (_edgeSwitch != null && _edgeSwitch.IsQuiet)
            {
                StopEdgeSwitch();
                ApplyPointerFeatures();
            }
        }

        /// <summary>Starts, retunes or stops the mouse hook to match the settings that use it.</summary>
        private void ApplyPointerFeatures()
        {
            bool edges = Settings.EdgeSwitchEnabled;
            bool peek = Settings.TaskbarPeek;
            bool focus = Settings.TaskbarHoverFocus;
            if (!edges && !peek && !focus)
            {
                StopEdgeSwitch();
                return;
            }
            if (_edgeSwitch == null)
            {
                _edgeSwitch = new EdgeSwitch(Settings.EdgeSwitchDwellMilliseconds, Settings.EdgeSwitchDistancePixels,
                    Settings.EdgeSwitchTopDistancePixels, edges, peek, focus);
                return;
            }
            _edgeSwitch.EdgesEnabled = edges;
            _edgeSwitch.PeekEnabled = peek;
            _edgeSwitch.FocusEnabled = focus;
        }

        private void StopEdgeSwitch()
        {
            if (_edgeSwitch != null)
            {
                _edgeSwitch.Dispose();
                _edgeSwitch = null;
            }
        }

        /// <summary>
        /// Loads the icon embedded in this executable, at the size the notification area
        /// actually wants, so it stays crisp under any DPI scaling.
        /// </summary>
        private static Icon LoadTrayIcon()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (System.IO.Stream stream = assembly.GetManifestResourceStream("SleepPicker.ico"))
            {
                if (stream == null)
                {
                    // Cloned so the caller can dispose it like any other icon we made.
                    return (Icon)SystemIcons.Application.Clone();
                }
                return new Icon(stream, SystemInformation.SmallIconSize);
            }
        }

        /// <summary>
        /// Whether there is anything to refresh: a battery to report on. Not tied to the
        /// dynamic icon, because the tooltip reports the charge and the time remaining
        /// whether the moon is drawn or the fixed artwork is -- that setting is about the
        /// picture, and a hover is not a picture.
        /// </summary>
        private static bool IsRefreshWanted()
        {
            return PowerSettings.HasBattery();
        }

        /// <summary>
        /// Puts the right picture and the right words in the notification area, both from
        /// one reading of the battery: an icon and a tooltip that disagreed would be worse
        /// than either being a minute stale.
        /// </summary>
        private void RefreshIcon()
        {
            // Null for a desktop, or a charge the firmware will not name: the two halves
            // below fall back to the fixed artwork and the bare name between them.
            BatteryReading reading;
            if (!PowerSettings.TryRead(out reading))
            {
                reading = null;
            }

            UpdateIcon(reading);
            UpdateTooltip(reading);
        }

        /// <summary>
        /// Draws the moon for the charge just read, or falls back to the fixed artwork.
        /// Redrawing is skipped when neither the charge nor the icon size has moved, which
        /// is the usual case: the timer fires sixty times for every percent the battery
        /// actually loses.
        /// </summary>
        private void UpdateIcon(BatteryReading reading)
        {
            if (!Settings.DynamicTrayIcon || reading == null)
            {
                ShowStaticIcon();
                return;
            }

            int percent = reading.Percent;
            bool onMains = reading.OnMains;

            // Re-read rather than cached: the notification area asks for a different size
            // when the display's scaling changes, and that can happen while we run.
            int size = SystemInformation.SmallIconSize.Width;

            Icon staleWaning = null;
            Icon staleWaxing = null;
            if (_waningMoon == null || percent != _shownPercent || size != _shownSize)
            {
                Icon waning;
                Icon waxing;
                try
                {
                    waning = MoonIcon.Create(percent, size, false);
                    try
                    {
                        waxing = MoonIcon.Create(percent, size, true);
                    }
                    catch (Exception)
                    {
                        waning.Dispose();
                        throw;
                    }
                }
                catch (Exception)
                {
                    // Drawing needs a GDI+ bitmap, which can fail when the session is
                    // starved of handles. The fixed artwork always works, so fall back to
                    // it rather than showing half a pair.
                    ShowStaticIcon();
                    return;
                }

                // Held, not yet disposed: the shell keeps the handle it was last given
                // until it has been handed a replacement below.
                staleWaning = _waningMoon;
                staleWaxing = _waxingMoon;
                _waningMoon = waning;
                _waxingMoon = waxing;
                _shownPercent = percent;
                _shownSize = size;
            }

            // Assigning either of these hands the shell a fresh notification, so both are
            // compared first: most ticks find nothing at all to say.
            Icon wanted = onMains ? _waxingMoon : _waningMoon;
            if (!ReferenceEquals(_notifyIcon.Icon, wanted))
            {
                _notifyIcon.Icon = wanted;
            }

            if (staleWaning != null)
            {
                staleWaning.Dispose();
            }
            if (staleWaxing != null)
            {
                staleWaxing.Dispose();
            }
        }

        /// <summary>
        /// What the tooltip says about the battery just read: the charge, and how long it
        /// has at the rate it is going.
        ///
        /// "to full" already says the machine is charging, so the word is not repeated;
        /// on mains without an estimate -- a full battery, or one a vendor is holding at
        /// 80% -- "charging" is all there is to say. Every assignment hands the shell a
        /// fresh notification, so the text is compared before it is set.
        /// </summary>
        private void UpdateTooltip(BatteryReading reading)
        {
            string text;
            if (reading == null)
            {
                text = "SleepPicker";
            }
            else
            {
                text = "SleepPicker — " + reading.Percent.ToString() + "% battery";
                if (reading.HasTime)
                {
                    text += ", " + PowerSettings.DescribeRemaining(reading.Seconds) +
                        (reading.ToFull ? " to full" : " left");
                }
                else if (reading.OnMains)
                {
                    text += ", charging";
                }
            }

            if (_notifyIcon.Text != text)
            {
                _notifyIcon.Text = text;
            }
        }

        private void ShowStaticIcon()
        {
            _shownPercent = -1;
            _shownSize = -1;
            if (!ReferenceEquals(_notifyIcon.Icon, _staticIcon))
            {
                _notifyIcon.Icon = _staticIcon;
            }
            if (_waningMoon != null)
            {
                _waningMoon.Dispose();
                _waningMoon = null;
            }
            if (_waxingMoon != null)
            {
                _waxingMoon.Dispose();
                _waxingMoon = null;
            }
        }

        private void OnRefreshTick(object sender, EventArgs e)
        {
            RefreshIcon();
        }

        /// <summary>
        /// The cable going in or out, among other things. Windows announces it, so the
        /// moon turns round as it happens rather than at some point in the next minute.
        /// </summary>
        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                AutoUpdater.CheckForUpdatesAsync();
            }

            if (e.Mode != PowerModes.StatusChange)
            {
                return;
            }
            // SystemEvents raises this on its own thread; the icon belongs to the UI one.
            _uiContext.Post(new SendOrPostCallback(RefreshIconCallback), null);
        }

        private void RefreshIconCallback(object state)
        {
            RefreshIcon();
        }

        /// <summary>
        /// Rebuilds the menu from the live power scheme every time it opens: another
        /// application, the Settings page, or a power-plan switch may have changed things
        /// since it was last shown.
        /// </summary>
        private void OnMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            bool hasBattery = PowerSettings.HasBattery();

            for (int i = 0; i < _targetItems.Length; i++)
            {
                ToolStripMenuItem item = _targetItems[i];
                PowerTarget target = (PowerTarget)item.Tag;

                // On a desktop the battery rows are meaningless, exactly as the Settings
                // page hides them there.
                if (target.RequiresBattery && !hasBattery)
                {
                    item.Visible = false;
                    continue;
                }
                item.Visible = true;

                uint current;
                try
                {
                    current = PowerSettings.Read(target);
                }
                catch (Exception)
                {
                    item.Text = target.Label + " — unavailable";
                    item.Enabled = false;
                    continue;
                }

                item.Enabled = true;
                item.Text = target.Label + " — " + PowerSettings.Describe(current);
                FillChoices(item, target, current);
            }

            // One reading of the power source for the whole row, so its label and its ticks
            // cannot disagree about which way the machine is being powered.
            int percent;
            bool onMains;
            bool statusKnown = PowerSettings.TryGetBatteryStatus(out percent, out onMains);

            // Likewise one reading of the meter, so the Power Mode row and the tick box that
            // governs it cannot contradict each other.
            bool meterHidden = BatteryMeter.IsHidden();

            // The row exists precisely when Windows' own does not: hiding the battery icon
            // hides the flyout, and the flyout is the only place Windows 10 offers the
            // slider. Read live, since the icon can be hidden from this very menu.
            bool showPowerMode = hasBattery && meterHidden && PowerModeSettings.IsAvailable();
            _powerModeItem.Visible = showPowerMode;
            if (showPowerMode)
            {
                UpdatePowerMode(statusKnown, onMains);
            }

            _darkModeItem.Checked = DarkMode.IsEnabled();

            _autoBrightnessItem.Checked = AutoBrightness.IsEnabled();

            _mouseAccelerationItem.Checked = MouseAcceleration.IsEnabled();

            _autoStartItem.Checked = AutoStart.IsEnabled();

            UpdateEdgeSwitch();

            // A moon that tracks the charge means nothing on a desktop, so the row is
            // hidden there alongside the two "on battery" rows above it.
            _dynamicIconItem.Visible = hasBattery;
            _dynamicIconItem.Checked = Settings.DynamicTrayIcon;

            // Likewise: there is no meter to hide on a machine with no battery. Read from
            // the registry rather than remembered, because a policy or another tool can
            // set the same value.
            _hideBatteryMeterItem.Visible = hasBattery;
            _hideBatteryMeterItem.Checked = meterHidden;

#if ENABLE_AUTO_UPDATE
            _updateStatusItem.Text = AutoUpdater.StatusText;
#endif

            // Opening the menu is also when a battery that appeared since the last look --
            // a tablet back in its dock -- gets noticed.
            _refreshTimer.Enabled = hasBattery;
            RefreshIcon();
        }

        private void FillChoices(ToolStripMenuItem parent, PowerTarget target, uint current)
        {
            // Dispose the previous generation of items; Clear() only detaches them.
            ToolStripItem[] stale = new ToolStripItem[parent.DropDownItems.Count];
            parent.DropDownItems.CopyTo(stale, 0);
            parent.DropDownItems.Clear();
            for (int i = 0; i < stale.Length; i++)
            {
                stale[i].Dispose();
            }

            bool currentIsPreset = false;
            for (int i = 0; i < PowerSettings.Presets.Length; i++)
            {
                if (PowerSettings.Presets[i] == current)
                {
                    currentIsPreset = true;
                    break;
                }
            }

            // A value set elsewhere need not be one of ours; show it rather than leaving
            // the submenu with nothing ticked.
            if (!currentIsPreset)
            {
                ToolStripMenuItem customItem = new ToolStripMenuItem(PowerSettings.Describe(current) + " (current)");
                customItem.Checked = true;
                customItem.Enabled = false;
                parent.DropDownItems.Add(customItem);
                parent.DropDownItems.Add(new ToolStripSeparator());
            }

            for (int i = 0; i < PowerSettings.Presets.Length; i++)
            {
                uint seconds = PowerSettings.Presets[i];

                // "Never" closes the list, matching the Settings dropdown ordering.
                if (seconds == PowerSettings.Never)
                {
                    parent.DropDownItems.Add(new ToolStripSeparator());
                }

                ToolStripMenuItem choice = new ToolStripMenuItem(PowerSettings.Describe(seconds));
                choice.Checked = seconds == current;
                choice.Tag = new Choice(target, seconds);
                choice.Click += OnChoiceClick;
                parent.DropDownItems.Add(choice);
            }
        }

        /// <summary>
        /// Labels and fills the Power Mode row from the mode actually in force. Effective
        /// rather than chosen, deliberately: Windows lowers the mode by itself as a battery
        /// runs down, and the row is meant to report what the machine is doing.
        /// </summary>
        private void UpdatePowerMode(bool statusKnown, bool onMains)
        {
            // The mode is stored per power source, so the flyout names the source it is
            // talking about and so does this. Left off when the source cannot be read,
            // rather than guessed at.
            string source = statusKnown ? (onMains ? " (plugged in)" : " (on battery)") : "";

            Guid effectiveOverlay;
            try
            {
                effectiveOverlay = PowerModeSettings.GetEffectiveOverlay();
            }
            catch (Exception)
            {
                _powerModeItem.Text = "Power Mode" + source + " — unavailable";
                _powerModeItem.Enabled = false;
                return;
            }

            _powerModeItem.Enabled = true;
            PowerMode current = PowerModeSettings.GetEffectiveMode(effectiveOverlay);
            string currentLabel = current != null
                ? current.Label
                : PowerModeSettings.DescribeOverlay(effectiveOverlay);

            _powerModeItem.Text = "Power Mode" + source + " — " + currentLabel;
            FillModes(_powerModeItem, current, currentLabel, statusKnown && onMains);
        }

        private void FillModes(ToolStripMenuItem parent, PowerMode current, string currentLabel, bool onMains)
        {
            // Dispose the previous generation of items; Clear() only detaches them.
            ToolStripItem[] stale = new ToolStripItem[parent.DropDownItems.Count];
            parent.DropDownItems.CopyTo(stale, 0);
            parent.DropDownItems.Clear();
            for (int i = 0; i < stale.Length; i++)
            {
                stale[i].Dispose();
            }

            // An overlay set by something else -- an OEM tool, or a power plan that brings
            // its own -- is none of the four; show it rather than leaving nothing ticked,
            // exactly as the timeout submenus do for a value that is not one of the presets.
            if (current == null)
            {
                ToolStripMenuItem customItem = new ToolStripMenuItem(currentLabel + " (current)");
                customItem.Checked = true;
                customItem.Enabled = false;
                parent.DropDownItems.Add(customItem);
                parent.DropDownItems.Add(new ToolStripSeparator());
            }

            for (int i = 0; i < PowerModeSettings.All.Length; i++)
            {
                PowerMode mode = PowerModeSettings.All[i];

                // Battery saver is not offered on mains, where Windows drops the notch too.
                if (mode.RequiresBattery && onMains)
                {
                    continue;
                }

                ToolStripMenuItem choice = new ToolStripMenuItem(mode.Label);
                choice.Checked = ReferenceEquals(mode, current);
                choice.Tag = mode;
                choice.Click += OnModeClick;
                parent.DropDownItems.Add(choice);
            }
        }

        private void OnModeClick(object sender, EventArgs e)
        {
            PowerMode mode = (PowerMode)((ToolStripMenuItem)sender).Tag;
            try
            {
                PowerModeSettings.Apply(mode);
            }
            catch (Exception ex)
            {
                ShowError("Could not change the power mode to \"" + mode.Label + "\": " + ex.Message);
            }
        }

        /// <summary>
        /// Battery saver throttles background work hard enough that the theme-change
        /// broadcast in <see cref="DarkMode.SetEnabled"/> can sit unapplied by Explorer
        /// for minutes -- see <see cref="PowerModeSettings.TrySuspendForImmediateEffect"/>
        /// -- so battery saver is lifted for the moment the change is made and restored
        /// straight afterward, whether the toggle itself succeeds or not.
        /// </summary>
        private void OnDarkModeClick(object sender, EventArgs e)
        {
            bool suspended = PowerModeSettings.TrySuspendForImmediateEffect();
            try
            {
                DarkMode.SetEnabled(!DarkMode.IsEnabled());
            }
            catch (Exception ex)
            {
                ShowError("Could not change the Dark Mode setting: " + ex.Message);
            }
            finally
            {
                if (suspended)
                {
                    try
                    {
                        PowerModeSettings.ResumeAfterImmediateEffect();
                    }
                    catch (Exception)
                    {
                        // Nothing more to do from here; the next launch's startup check,
                        // or the next mode picked from the Power Mode row, puts the
                        // charge level back.
                    }
                }
            }
        }

        private void OnAutoBrightnessClick(object sender, EventArgs e)
        {
            try
            {
                AutoBrightness.SetEnabled(!AutoBrightness.IsEnabled());
            }
            catch (Exception ex)
            {
                ShowError("Could not change the Auto Brightness setting: " + ex.Message);
            }
        }

        private void OnMouseAccelerationClick(object sender, EventArgs e)
        {
            try
            {
                MouseAcceleration.SetEnabled(!MouseAcceleration.IsEnabled());
            }
            catch (Exception ex)
            {
                ShowError("Could not change the Mouse acceleration setting: " + ex.Message);
            }
        }

        private void OnChoiceClick(object sender, EventArgs e)
        {
            Choice choice = (Choice)((ToolStripMenuItem)sender).Tag;
            try
            {
                PowerSettings.Write(choice.Target, choice.Seconds);
            }
            catch (Exception ex)
            {
                ShowError("Could not change \"" + choice.Target.Label + "\": " + ex.Message);
            }
        }

        private void OnAutoStartClick(object sender, EventArgs e)
        {
            try
            {
                AutoStart.SetEnabled(!AutoStart.IsEnabled());
            }
            catch (Exception ex)
            {
                ShowError("Could not change the start-with-Windows setting: " + ex.Message);
            }
        }

        private void UpdateEdgeSwitch()
        {
            _edgeSwitchItem.Checked = Settings.EdgeSwitchEnabled;
            _taskbarPeekItem.Checked = Settings.TaskbarPeek;
            _hoverFocusItem.Checked = Settings.TaskbarHoverFocus;
            TaskbarPreviewMode mode = TaskbarPreviews.GetMode();
            for (int i = 0; i < _previewModeItems.Length; i++)
            {
                _previewModeItems[i].Checked = (TaskbarPreviewMode)_previewModeItems[i].Tag == mode;
            }

            int dwell = Settings.EdgeSwitchDwellMilliseconds;
            _edgeSensitivityItem.Text = "Sensitivity — " + DescribeDwell(dwell);
            string[] dwellLabels = new string[EdgeDwellPresets.Length];
            for (int i = 0; i < EdgeDwellPresets.Length; i++)
            {
                dwellLabels[i] = EdgeDwellNames[i] + " — " + Seconds(EdgeDwellPresets[i]);
            }
            FillEdgeChoices(_edgeSensitivityItem, EdgeDwellPresets, dwellLabels, dwell,
                DescribeDwell(dwell), delegate(int v) { Settings.EdgeSwitchDwellMilliseconds = v; }, "edge sensitivity");

            int distance = Settings.EdgeSwitchDistancePixels;
            _edgeDistanceItem.Text = "Distance — " + Pixels(distance);
            string[] distanceLabels = new string[EdgeDistancePresets.Length];
            for (int i = 0; i < EdgeDistancePresets.Length; i++)
            {
                distanceLabels[i] = Pixels(EdgeDistancePresets[i]) +
                    (EdgeDistancePresets[i] <= 2 ? " — the very edge" : "");
            }
            FillEdgeChoices(_edgeDistanceItem, EdgeDistancePresets, distanceLabels, distance,
                Pixels(distance), delegate(int v) { Settings.EdgeSwitchDistancePixels = v; }, "edge distance");

            int top = Settings.EdgeSwitchTopDistancePixels;
            _edgeTopDistanceItem.Text = "Top distance — " + Pixels(top);
            string[] topLabels = new string[EdgeTopDistancePresets.Length];
            for (int i = 0; i < EdgeTopDistancePresets.Length; i++)
            {
                topLabels[i] = Pixels(EdgeTopDistancePresets[i]);
            }
            FillEdgeChoices(_edgeTopDistanceItem, EdgeTopDistancePresets, topLabels, top,
                Pixels(top), delegate(int v) { Settings.EdgeSwitchTopDistancePixels = v; }, "top distance");
        }

        private void FillEdgeChoices(ToolStripMenuItem parent, int[] presets, string[] labels,
            int current, string currentLabel, Action<int> store, string what)
        {
            // Dispose the previous generation of items; Clear() only detaches them.
            ToolStripItem[] stale = new ToolStripItem[parent.DropDownItems.Count];
            parent.DropDownItems.CopyTo(stale, 0);
            parent.DropDownItems.Clear();
            for (int i = 0; i < stale.Length; i++)
            {
                stale[i].Dispose();
            }

            // A value written by hand need not be one of ours; show it rather than leaving
            // nothing ticked, as the timeout submenus do.
            if (Array.IndexOf(presets, current) < 0)
            {
                ToolStripMenuItem customItem = new ToolStripMenuItem(currentLabel + " (current)");
                customItem.Checked = true;
                customItem.Enabled = false;
                parent.DropDownItems.Add(customItem);
                parent.DropDownItems.Add(new ToolStripSeparator());
            }

            for (int i = 0; i < presets.Length; i++)
            {
                ToolStripMenuItem choice = new ToolStripMenuItem(labels[i]);
                choice.Checked = presets[i] == current;
                int value = presets[i];
                choice.Click += delegate { OnEdgeChoice(store, value, what); };
                parent.DropDownItems.Add(choice);
            }
        }

        private static string DescribeDwell(int milliseconds)
        {
            int i = Array.IndexOf(EdgeDwellPresets, milliseconds);
            return i < 0 ? Seconds(milliseconds) : EdgeDwellNames[i] + " (" + Seconds(milliseconds) + ")";
        }

        private static string Seconds(int milliseconds)
        {
            return (milliseconds / 1000.0).ToString("0.0##", System.Globalization.CultureInfo.CurrentCulture) + " s";
        }

        private static string Pixels(int pixels)
        {
            return pixels.ToString() + " px";
        }

        private void OnEdgeSwitchClick(object sender, EventArgs e)
        {
            bool enable = !Settings.EdgeSwitchEnabled;
            try
            {
                Settings.EdgeSwitchEnabled = enable;
                ApplyPointerFeatures();
            }
            catch (Exception ex)
            {
                ShowError("Could not change the edge desktop switching setting: " + ex.Message);
            }
            // The menu stays open on a row with a submenu, so its tick is updated here.
            _edgeSwitchItem.Checked = Settings.EdgeSwitchEnabled;
            if (Settings.EdgeSwitchEnabled)
            {
                ShowEdgePreview();
            }
        }

        private void OnTaskbarPeekClick(object sender, EventArgs e)
        {
            try
            {
                Settings.TaskbarPeek = !Settings.TaskbarPeek;
                ApplyPointerFeatures();
            }
            catch (Exception ex)
            {
                ShowError("Could not change the taskbar peek setting: " + ex.Message);
            }
        }

        private void OnHoverFocusClick(object sender, EventArgs e)
        {
            try
            {
                Settings.TaskbarHoverFocus = !Settings.TaskbarHoverFocus;
                ApplyPointerFeatures();
            }
            catch (Exception ex)
            {
                ShowError("Could not change the hover focus setting: " + ex.Message);
            }
        }

        private ToolStripMenuItem PreviewModeItem(TaskbarPreviewMode mode, string text, string toolTip)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Tag = mode;
            item.ToolTipText = toolTip;
            item.Click += OnPreviewModeClick;
            return item;
        }

        private void OnPreviewModeClick(object sender, EventArgs e)
        {
            TaskbarPreviewMode mode = (TaskbarPreviewMode)((ToolStripMenuItem)sender).Tag;
            if (mode == TaskbarPreviews.GetMode())
            {
                return; // an Explorer restart that would change nothing
            }
            // Asked before anything is written, as for the battery icon. Unlike that one this
            // needs no elevation, so No could still write it for the next sign-in -- but a
            // tick that changes nothing visible until then would read as broken.
            if (!ConfirmExplorerRestart(
                "Changing the taskbar previews takes a restart of Windows Explorer, because " +
                "the taskbar only reads this setting when it starts."))
            {
                return;
            }
            try
            {
                TaskbarPreviews.SetMode(mode);
            }
            catch (Exception ex)
            {
                ShowError("Could not change the taskbar preview setting: " + ex.Message);
                return;
            }
            try
            {
                BatteryMeter.RestartExplorer();
            }
            catch (Exception ex)
            {
                ShowError("The setting was changed, but Explorer could not be restarted: " +
                    ex.Message + " It takes effect the next time you sign in.");
            }
        }

        private void ShowEdgePreview()
        {
            _edgePreview.Show(Settings.EdgeSwitchDwellMilliseconds, Settings.EdgeSwitchDistancePixels,
                Settings.EdgeSwitchTopDistancePixels);
        }

        /// <summary>One handler for all three submenus: store the value, hand all three
        /// current values to the running hook, and show the zones.</summary>
        private void OnEdgeChoice(Action<int> store, int value, string what)
        {
            try
            {
                store(value);
            }
            catch (Exception ex)
            {
                ShowError("Could not change the " + what + ": " + ex.Message);
                return;
            }
            if (_edgeSwitch != null)
            {
                _edgeSwitch.DwellMilliseconds = Settings.EdgeSwitchDwellMilliseconds;
                _edgeSwitch.DistancePixels = Settings.EdgeSwitchDistancePixels;
                _edgeSwitch.TopDistancePixels = Settings.EdgeSwitchTopDistancePixels;
            }
            ShowEdgePreview();
        }

        private void OnDynamicIconClick(object sender, EventArgs e)
        {
            try
            {
                Settings.DynamicTrayIcon = !Settings.DynamicTrayIcon;
            }
            catch (Exception ex)
            {
                ShowError("Could not change the dynamic-tray-icon setting: " + ex.Message);
                return;
            }

            // Swapped straight away rather than at the next tick, so the tick box and the
            // icon never disagree.
            _refreshTimer.Enabled = IsRefreshWanted();
            RefreshIcon();
        }

        private void OnHideBatteryMeterClick(object sender, EventArgs e)
        {
            bool hide = !BatteryMeter.IsHidden();

            // Asked before anything is written, so backing out leaves the registry exactly
            // as it was rather than half-changed until the next sign-in.
            if (!ConfirmExplorerRestart(
                (hide ? "Hiding" : "Showing") + " the Windows battery icon takes two things " +
                "Windows asks for: an administrator's approval, because this setting is one " +
                "of the ones users are not allowed to change for themselves, and a restart " +
                "of Windows Explorer, because the taskbar only reads it when it starts."))
            {
                return;
            }

            BatteryMeter.ChangeResult result;
            try
            {
                result = BatteryMeter.RequestHidden(hide);
            }
            catch (Exception ex)
            {
                ShowError("Could not change the Windows battery icon setting: " + ex.Message);
                return;
            }

            if (result == BatteryMeter.ChangeResult.Declined)
            {
                // The elevation prompt was refused. Nothing was written, so there is
                // nothing to say and nothing to restart.
                return;
            }
            if (result == BatteryMeter.ChangeResult.Failed)
            {
                ShowError("Windows would not let the battery icon setting be changed.");
                return;
            }

            try
            {
                BatteryMeter.RestartExplorer();
            }
            catch (Exception ex)
            {
                // The setting is written either way; only the moment it becomes visible is
                // lost, so say so rather than undoing what was asked for.
                ShowError("The setting was changed, but Explorer could not be restarted: " +
                    ex.Message + " It takes effect the next time you sign in.");
            }
        }

        /// <summary>
        /// The one dialog in the program. Restarting the shell is disruptive enough that it
        /// has to be declinable, and false here means nothing is asked for and nothing is
        /// written at all. <paramref name="why"/> says what is changing and what it takes.
        /// </summary>
        private static bool ConfirmExplorerRestart(string why)
        {
            string message =
                why +
                Environment.NewLine + Environment.NewLine +
                "The taskbar will disappear for a moment and any open File Explorer windows " +
                "will close. Nothing is changed if you choose No." +
                Environment.NewLine + Environment.NewLine +
                "Continue?";

            // A tray application has no window of its own to own the box, and an unowned
            // one can come up behind whatever the user is looking at. A top-most form
            // parked off screen gives it something to sit in front of.
            using (Form owner = new Form())
            {
                owner.ShowInTaskbar = false;
                owner.FormBorderStyle = FormBorderStyle.None;
                owner.StartPosition = FormStartPosition.Manual;
                owner.Location = new Point(-32000, -32000);
                owner.Size = new Size(1, 1);
                owner.TopMost = true;
                owner.Show();

                DialogResult answer = MessageBox.Show(owner, message, "SleepPicker",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

                owner.Hide();
                return answer == DialogResult.Yes;
            }
        }

        private void OnIconMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowMenu();
            }
        }

        private void OnShowMenuRequested(object sender, EventArgs e)
        {
            // Raised on the single-instance listener thread; the menu must be shown on
            // the UI thread.
            _uiContext.Post(new SendOrPostCallback(ShowMenuCallback), null);
        }

        private void ShowMenuCallback(object state)
        {
            ShowMenu();
        }

        /// <summary>
        /// Pops the menu at the cursor. NotifyIcon's own (non-public) ShowContextMenu is
        /// used because it also handles the foreground-window dance that stops a tray menu
        /// from sticking open when you click elsewhere.
        /// </summary>
        private void ShowMenu()
        {
            MethodInfo showContextMenu = typeof(NotifyIcon).GetMethod(
                "ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);

            if (showContextMenu != null)
            {
                showContextMenu.Invoke(_notifyIcon, null);
                return;
            }
            _menu.Show(Cursor.Position);
        }

        private void ShowError(string message)
        {
            _notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
            _notifyIcon.BalloonTipTitle = "SleepPicker";
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.ShowBalloonTip(5000);
        }

        private void OnExitClick(object sender, EventArgs e)
        {
            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            // Hide first, or the icon lingers in the notification area until hovered.
            _refreshTimer.Enabled = false;
            _notifyIcon.Visible = false;
            AutoUpdater.ApplyPendingUpdate();
            base.ExitThreadCore();
        }

#if ENABLE_AUTO_UPDATE
        private void OnAutoUpdaterStatusChanged(object sender, EventArgs e)
        {
            _uiContext.Post(new SendOrPostCallback(UpdateStatusCallback), null);
        }

        private void UpdateStatusCallback(object state)
        {
            if (_updateStatusItem != null)
            {
                _updateStatusItem.Text = AutoUpdater.StatusText;
            }
        }
#endif

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
#if ENABLE_AUTO_UPDATE
                AutoUpdater.StatusChanged -= OnAutoUpdaterStatusChanged;
#endif
                _singleInstance.ShowMenuRequested -= OnShowMenuRequested;
                // SystemEvents holds its handlers in a static list, so leaving this
                // attached would keep the whole application context alive.
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                _edgeRefreshTimer.Dispose();
                StopEdgeSwitch();
                _edgePreview.Dispose();
                _refreshTimer.Dispose();
                _notifyIcon.Dispose();
                _menu.Dispose();
                if (_waningMoon != null)
                {
                    _waningMoon.Dispose();
                    _waningMoon = null;
                }
                if (_waxingMoon != null)
                {
                    _waxingMoon.Dispose();
                    _waxingMoon = null;
                }
                _staticIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>What a submenu entry does when clicked.</summary>
        private sealed class Choice
        {
            private readonly PowerTarget _target;
            private readonly uint _seconds;

            public Choice(PowerTarget target, uint seconds)
            {
                _target = target;
                _seconds = seconds;
            }

            public PowerTarget Target { get { return _target; } }
            public uint Seconds { get { return _seconds; } }
        }
    }
}
