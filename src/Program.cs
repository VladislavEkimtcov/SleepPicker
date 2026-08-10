using System;
using System.Threading;
using System.Windows.Forms;

namespace SleepPicker
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Hiding Windows' own battery meter means writing a policy value that Windows
            // keeps read-only for the user, so the running copy starts an elevated one of
            // these to do it. That copy is not the tray application: it writes one value
            // and leaves, before the single-instance guard would send it away.
            if (BatteryMeter.IsElevatedWriteRequest(args))
            {
                return BatteryMeter.RunElevatedWrite(args);
            }

            using (SingleInstance instance = new SingleInstance())
            {
                if (!instance.TryAcquire())
                {
                    // Already running: ask that instance to show its menu, then step aside.
                    SingleInstance.SignalRunningInstance();
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // WindowsFormsSynchronizationContext.Current is what the single-instance
                // listener thread posts the "show menu" request to, so install it before
                // the tray context is constructed.
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

                using (TrayApp trayApp = new TrayApp(instance))
                {
                    Application.Run(trayApp);
                }
            }
            return 0;
        }
    }
}
