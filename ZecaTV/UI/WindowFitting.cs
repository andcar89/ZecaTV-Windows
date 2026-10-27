using System;
using System.Windows;

namespace ZecaTV.UI
{
    /// <summary>
    /// Keeps the secondary windows (Settings, TV Guide, the dialogs...) inside the screen. Their sizes are
    /// fixed in XAML for a roomy desktop — fine at 100% display scaling, but on a laptop at 150% a
    /// 1920x1080 screen is only 1280x720 in WPF's units (less the taskbar), so a 700- or 720-tall window
    /// overflowed and its bottom (the buttons) fell off the screen.
    /// </summary>
    public static class WindowFitting
    {
        /// <summary>Call right after InitializeComponent(): shrinks the window's size, and its minimum
        /// size if that would no longer fit, so it's never larger than the usable screen area.</summary>
        public static void ShrinkToWorkArea(Window window)
        {
            var area = SystemParameters.WorkArea;
            var maxWidth = Math.Max(300, area.Width - 24);
            var maxHeight = Math.Max(300, area.Height - 24);

            window.MaxWidth = Math.Min(window.MaxWidth, maxWidth);
            window.MaxHeight = Math.Min(window.MaxHeight, maxHeight);
            window.MinWidth = Math.Min(window.MinWidth, window.MaxWidth);
            window.MinHeight = Math.Min(window.MinHeight, window.MaxHeight);

            // NaN means "size to content" (SizeToContent windows) — leave that to MaxWidth/MaxHeight above.
            if (!double.IsNaN(window.Width)) window.Width = Math.Min(window.Width, window.MaxWidth);
            if (!double.IsNaN(window.Height)) window.Height = Math.Min(window.Height, window.MaxHeight);
        }
    }
}
