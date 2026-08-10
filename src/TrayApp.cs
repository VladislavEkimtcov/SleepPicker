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
        private readonly ToolStripMenuItem _autoStartItem;
        private readonly ToolStripMenuItem _dynamicIconItem;
        private readonly ToolStripMenuItem _hideBatteryMeterItem;
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

            _autoStartItem = new ToolStripMenuItem("Start with Windows");
            _autoStartItem.Click += OnAutoStartClick;
            _menu.Items.Add(_autoStartItem);

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

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
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
            bool onMains;
            if (!Settings.DynamicTrayIcon || !PowerSettings.TryGetBatteryStatus(out percent, out onMains))
            {
                ShowStaticIcon();
                return;
            }

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

            string text = "SleepPicker — " + percent.ToString() + "% battery" +
                (onMains ? ", charging" : "");
            if (_notifyIcon.Text != text)
            {
                _notifyIcon.Text = text;
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

        private void ShowStaticIcon()
        {
            _shownPercent = -1;
            _shownSize = -1;
            if (!ReferenceEquals(_notifyIcon.Icon, _staticIcon))
            {
                _notifyIcon.Icon = _staticIcon;
                _notifyIcon.Text = "SleepPicker";
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

            _autoStartItem.Checked = AutoStart.IsEnabled();

            // A moon that tracks the charge means nothing on a desktop, so the row is
            // hidden there alongside the two "on battery" rows above it.
            _dynamicIconItem.Visible = hasBattery;
            _dynamicIconItem.Checked = Settings.DynamicTrayIcon;

            // Likewise: there is no meter to hide on a machine with no battery. Read from
            // the registry rather than remembered, because a policy or another tool can
            // set the same value.
            _hideBatteryMeterItem.Visible = hasBattery;
            _hideBatteryMeterItem.Checked = BatteryMeter.IsHidden();

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

        private void OnHideBatteryMeterClick(object sender, EventArgs e)
        {
            bool hide = !BatteryMeter.IsHidden();

            // Asked before anything is written, so backing out leaves the registry exactly
            // as it was rather than half-changed until the next sign-in.
            if (!ConfirmExplorerRestart(hide))
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
        /// written at all.
        /// </summary>
        private static bool ConfirmExplorerRestart(bool hide)
        {
            string message =
                (hide ? "Hiding" : "Showing") + " the Windows battery icon takes two things " +
                "Windows asks for: an administrator's approval, because this setting is one " +
                "of the ones users are not allowed to change for themselves, and a restart " +
                "of Windows Explorer, because the taskbar only reads it when it starts." +
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
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _singleInstance.ShowMenuRequested -= OnShowMenuRequested;
                // SystemEvents holds its handlers in a static list, so leaving this
                // attached would keep the whole application context alive.
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
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
