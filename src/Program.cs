using System;
using System.Threading;
using System.Windows.Forms;

namespace SleepPicker
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            using (SingleInstance instance = new SingleInstance())
            {
                if (!instance.TryAcquire())
                {
                    // Already running: ask that instance to show its menu, then step aside.
                    SingleInstance.SignalRunningInstance();
                    return;
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
        }
    }
}
