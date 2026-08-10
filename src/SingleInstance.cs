using System;
using System.Threading;

namespace SleepPicker
{
    /// <summary>
    /// Keeps one tray icon per logon session.
    ///
    /// Launching SleepPicker again while it is already running would otherwise silently
    /// add a duplicate icon, which looks broken. Instead the second process signals the
    /// first and exits, and the first pops its menu at the cursor -- so double-launching
    /// from the Start menu does something useful instead of nothing.
    /// </summary>
    internal sealed class SingleInstance : IDisposable
    {
        // "Local\" scopes both objects to the logon session, so two users on the same
        // machine each get their own tray icon.
        private const string MutexName = @"Local\SleepPicker.SingleInstance";
        private const string SignalName = @"Local\SleepPicker.ShowMenu";

        private Mutex _mutex;
        private EventWaitHandle _showMenuSignal;
        private EventWaitHandle _stopSignal;
        private Thread _listener;

        /// <summary>Raised on a background thread when another launch asks for the menu.</summary>
        public event EventHandler ShowMenuRequested;

        /// <summary>
        /// True if this process is the first instance. When false the caller should
        /// signal the running instance and quit.
        /// </summary>
        public bool TryAcquire()
        {
            bool createdNew;
            _mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                return false;
            }

            _showMenuSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
            _stopSignal = new EventWaitHandle(false, EventResetMode.ManualReset);

            _listener = new Thread(Listen);
            _listener.IsBackground = true;
            _listener.Name = "SleepPicker single-instance listener";
            _listener.Start();
            return true;
        }

        /// <summary>Asks the already-running instance to show its menu.</summary>
        public static void SignalRunningInstance()
        {
            EventWaitHandle signal;
            if (!EventWaitHandle.TryOpenExisting(SignalName, out signal))
            {
                // The other instance is shutting down; nothing to ask.
                return;
            }
            using (signal)
            {
                signal.Set();
            }
        }

        private void Listen()
        {
            WaitHandle[] handles = new WaitHandle[] { _showMenuSignal, _stopSignal };
            while (true)
            {
                int index = WaitHandle.WaitAny(handles);
                if (index != 0)
                {
                    return;
                }
                EventHandler handler = ShowMenuRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }

        public void Dispose()
        {
            if (_stopSignal != null)
            {
                _stopSignal.Set();
            }
            if (_listener != null)
            {
                _listener.Join(TimeSpan.FromSeconds(1));
                _listener = null;
            }
            if (_showMenuSignal != null)
            {
                _showMenuSignal.Close();
                _showMenuSignal = null;
            }
            if (_stopSignal != null)
            {
                _stopSignal.Close();
                _stopSignal = null;
            }
            if (_mutex != null)
            {
                _mutex.ReleaseMutex();
                _mutex.Close();
                _mutex = null;
            }
        }
    }
}
