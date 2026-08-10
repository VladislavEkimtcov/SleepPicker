using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SleepPicker
{
    /// <summary>
    /// The whole user interface: one notification-area icon whose menu mirrors the four
    /// dropdowns of Settings -> System -> Power &amp; sleep. There is deliberately no
    /// window, no taskbar button and no settings dialog.
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
        private readonly ToolStripMenuItem _autoStartItem;
        private readonly ToolStripMenuItem _dynamicIconItem;
        private readonly SingleInstance _singleInstance;
        private readonly SynchronizationContext _uiContext;

        /// <summary>The fixed artwork, shown whenever the moon is not.</summary>
        private readonly Icon _staticIcon;
        // Qualified: System.Threading also has a Timer, and this one has to tick on the UI
        // thread, where the icon can be swapped without marshalling.
        private readonly System.Windows.Forms.Timer _refreshTimer;

        /// <summary>The moon currently in the notification area, owned here, or null.</summary>
        private Icon _moonIcon;

        // What the moon on screen was drawn for, so an unchanged charge costs nothing.
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

            _autoStartItem = new ToolStripMenuItem("Start with Windows");
            _autoStartItem.Click += OnAutoStartClick;
            _menu.Items.Add(_autoStartItem);

            _dynamicIconItem = new ToolStripMenuItem("Dynamic tray icon");
            _dynamicIconItem.ToolTipText = "Show the battery charge as the moon's phase.";
            _dynamicIconItem.Click += OnDynamicIconClick;
            _menu.Items.Add(_dynamicIconItem);

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
            _refreshTimer.Enabled = IsMoonWanted();
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
        /// Whether the moon should be drawn at all: only when it is switched on and there
        /// is a battery whose charge it could be showing.
        /// </summary>
        private static bool IsMoonWanted()
        {
            return Settings.DynamicTrayIcon && PowerSettings.HasBattery();
        }

        /// <summary>
        /// Puts the right picture in the notification area. Redrawing is skipped when
        /// neither the charge nor the icon size has moved, which is the usual case: the
        /// timer fires sixty times for every percent the battery actually loses.
        /// </summary>
        private void RefreshIcon()
        {
            int percent;
            if (!Settings.DynamicTrayIcon || !PowerSettings.TryGetBatteryPercent(out percent))
            {
                ShowStaticIcon();
                return;
            }

            // Re-read rather than cached: the notification area asks for a different size
            // when the display's scaling changes, and that can happen while we run.
            int size = SystemInformation.SmallIconSize.Width;
            if (_moonIcon != null && percent == _shownPercent && size == _shownSize)
            {
                return;
            }

            Icon moon;
            try
            {
                moon = MoonIcon.Create(percent, size);
            }
            catch (Exception)
            {
                // Drawing needs a GDI+ bitmap, which can fail when the session is starved
                // of handles. The fixed artwork always works, so fall back to it.
                ShowStaticIcon();
                return;
            }

            // The old icon is released only once the new one is on screen: the shell is
            // holding that handle until it has been handed a replacement.
            Icon previous = _moonIcon;
            _moonIcon = moon;
            _notifyIcon.Icon = moon;
            _notifyIcon.Text = "SleepPicker — " + percent.ToString() + "% battery";
            _shownPercent = percent;
            _shownSize = size;

            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private void ShowStaticIcon()
        {
            _shownPercent = -1;
            _shownSize = -1;
            if (!ReferenceEquals(_notifyIcon.Icon, _staticIcon))
            {
                _notifyIcon.Icon = _staticIcon;
                _notifyIcon.Text = "SleepPicker";
            }
            if (_moonIcon != null)
            {
                _moonIcon.Dispose();
                _moonIcon = null;
            }
        }

        private void OnRefreshTick(object sender, EventArgs e)
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

            _autoStartItem.Checked = AutoStart.IsEnabled();

            // A moon that tracks the charge means nothing on a desktop, so the row is
            // hidden there alongside the two "on battery" rows above it.
            _dynamicIconItem.Visible = hasBattery;
            _dynamicIconItem.Checked = Settings.DynamicTrayIcon;

            // Opening the menu is also when a battery that appeared since the last look --
            // a tablet back in its dock -- gets noticed.
            _refreshTimer.Enabled = Settings.DynamicTrayIcon && hasBattery;
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
            _refreshTimer.Enabled = IsMoonWanted();
            RefreshIcon();
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
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _singleInstance.ShowMenuRequested -= OnShowMenuRequested;
                _refreshTimer.Dispose();
                _notifyIcon.Dispose();
                _menu.Dispose();
                if (_moonIcon != null)
                {
                    _moonIcon.Dispose();
                    _moonIcon = null;
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
