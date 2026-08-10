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
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem[] _targetItems;
        private readonly ToolStripMenuItem _autoStartItem;
        private readonly SingleInstance _singleInstance;
        private readonly SynchronizationContext _uiContext;

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

            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += OnExitClick;
            _menu.Items.Add(exitItem);

            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = LoadTrayIcon();
            _notifyIcon.Text = "SleepPicker";
            _notifyIcon.ContextMenuStrip = _menu;
            // Left-click opens the same menu, so either button works.
            _notifyIcon.MouseUp += OnIconMouseUp;
            _notifyIcon.Visible = true;
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
                    return SystemIcons.Application;
                }
                return new Icon(stream, SystemInformation.SmallIconSize);
            }
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
            _notifyIcon.Visible = false;
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _singleInstance.ShowMenuRequested -= OnShowMenuRequested;
                _notifyIcon.Dispose();
                _menu.Dispose();
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
