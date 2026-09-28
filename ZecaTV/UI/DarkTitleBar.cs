using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ZecaTV.UI
{
    /// <summary>
    /// Asks Windows to draw a window's title bar dark, so a dark app doesn't sit under a white bar.
    /// Windows 10 20H1+ and Windows 11; silently does nothing on older builds.
    /// Call from OnSourceInitialized (the window handle doesn't exist before that).
    /// </summary>
    internal static class DarkTitleBar
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void Apply(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                var enabled = 1;
                if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
                }
            }
            catch
            {
                // Cosmetic only.
            }
        }
    }
}
