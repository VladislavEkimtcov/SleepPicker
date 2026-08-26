using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SleepPicker
{
    /// <summary>
    /// "Enhance pointer precision" -- Settings -> Devices -> Mouse -> "Additional mouse options"
    /// (or Control Panel -> Mouse -> Pointer Options) -- Windows' mouse acceleration setting.
    ///
    /// Controls whether Windows dynamically accelerates the pointer based on the velocity of
    /// the physical mouse movement. Read and written through SystemParametersInfo using
    /// SPI_GETMOUSE and SPI_SETMOUSE.
    /// </summary>
    internal static class MouseAcceleration
    {
        private const uint SPI_GETMOUSE = 0x0003;
        private const uint SPI_SETMOUSE = 0x0004;

        private const uint SPIF_UPDATEINIFILE = 0x0001;
        private const uint SPIF_SENDCHANGE = 0x0002;

        private const string MouseRegPath = @"Control Panel\Mouse";
        private const string MouseSpeedValue = "MouseSpeed";

        /// <summary>
        /// True when "Enhance pointer precision" (mouse acceleration) is on.
        /// SPI_GETMOUSE returns an array of 3 integers: [Threshold1, Threshold2, Acceleration].
        /// The third element is 0 when disabled, and non-zero (1 or 2) when enabled.
        /// </summary>
        public static bool IsEnabled()
        {
            int[] mouseParams = new int[3];
            if (NativeMethods.SystemParametersInfo(SPI_GETMOUSE, 0, mouseParams, 0))
            {
                return mouseParams[2] != 0;
            }

            // Fallback to registry if SystemParametersInfo fails.
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(MouseRegPath, false))
            {
                if (key == null)
                {
                    return false;
                }
                object value = key.GetValue(MouseSpeedValue);
                if (value == null)
                {
                    return false;
                }
                string strVal = value.ToString();
                return strVal != "0";
            }
        }

        /// <summary>
        /// Enables or disables "Enhance pointer precision" (mouse acceleration).
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            int[] mouseParams = new int[3];
            // Retrieve current thresholds to preserve the user's custom thresholds if present.
            if (!NativeMethods.SystemParametersInfo(SPI_GETMOUSE, 0, mouseParams, 0))
            {
                mouseParams[0] = enabled ? 6 : 0;
                mouseParams[1] = enabled ? 10 : 0;
                mouseParams[2] = enabled ? 1 : 0;
            }
            else
            {
                if (enabled)
                {
                    mouseParams[2] = 1;
                    // If thresholds were 0, assign standard Windows defaults (6, 10).
                    if (mouseParams[0] == 0 && mouseParams[1] == 0)
                    {
                        mouseParams[0] = 6;
                        mouseParams[1] = 10;
                    }
                }
                else
                {
                    mouseParams[2] = 0;
                }
            }

            uint flags = SPIF_UPDATEINIFILE | SPIF_SENDCHANGE;
            if (!NativeMethods.SystemParametersInfo(SPI_SETMOUSE, 0, mouseParams, flags))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SystemParametersInfo(
                uint uiAction,
                uint uiParam,
                [In, Out] int[] pvParam,
                uint fWinIni);
        }
    }
}
