using System;

namespace ZecaTV.UI
{
    /// <summary>A plain rectangle in WPF device-independent units — deliberately not System.Windows.Rect,
    /// so <see cref="WindowBounds"/> has no WPF dependency and can be regression-tested off Windows.</summary>
    public readonly record struct BoundsD(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }

    /// <summary>What the main window should open with: where, how big, and whether maximized.</summary>
    public readonly record struct WindowFit(BoundsD Bounds, bool Maximized);

    /// <summary>
    /// Works out the main window's startup size/position from what was saved last time (if anything) and
    /// the screens as they are right now. The old fixed 1280x760 window didn't fit a laptop at 150% display
    /// scaling (a 1920x1080 screen is only 1280x720 in WPF's units there, minus the taskbar), so the size
    /// is always fitted to the visible area; and a saved position from a monitor that's since been
    /// unplugged never leaves the window off-screen.
    /// </summary>
    public static class WindowBounds
    {
        /// <summary>The default size is only used if it fits inside this fraction of the work area —
        /// otherwise the window just opens maximized, which is what a small laptop screen wants anyway.</summary>
        private const double FitFraction = 0.92;

        /// <param name="saved">Last session's normal (non-maximized) bounds, or null on first run.</param>
        /// <param name="savedMaximized">Whether the window was maximized (or fullscreen from maximized) when closed.</param>
        /// <param name="virtualScreen">The whole desktop across all monitors.</param>
        /// <param name="workArea">The primary monitor's usable area (screen minus taskbar).</param>
        public static WindowFit Fit(BoundsD? saved, bool savedMaximized, BoundsD virtualScreen, BoundsD workArea,
            double defaultWidth, double defaultHeight, double minWidth, double minHeight)
        {
            if (saved is { } s && IsUsable(s, virtualScreen))
            {
                var width = Math.Clamp(s.Width, minWidth, Math.Max(minWidth, virtualScreen.Width));
                var height = Math.Clamp(s.Height, minHeight, Math.Max(minHeight, virtualScreen.Height));
                // Keep the title bar reachable even if the clamp above shrank the window.
                var left = Math.Clamp(s.Left, virtualScreen.Left - width + 120, virtualScreen.Right - 120);
                var top = Math.Clamp(s.Top, virtualScreen.Top, Math.Max(virtualScreen.Top, virtualScreen.Bottom - 40));
                return new WindowFit(new BoundsD(left, top, width, height), savedMaximized);
            }

            // No usable saved position: centered default, or maximized outright when it wouldn't fit.
            var fits = defaultWidth <= workArea.Width * FitFraction && defaultHeight <= workArea.Height * FitFraction;
            var w = Math.Min(defaultWidth, Math.Max(minWidth, workArea.Width * FitFraction));
            var h = Math.Min(defaultHeight, Math.Max(minHeight, workArea.Height * FitFraction));
            var centered = new BoundsD(
                workArea.Left + (workArea.Width - w) / 2,
                workArea.Top + (workArea.Height - h) / 2,
                w, h);
            return new WindowFit(centered, savedMaximized || !fits);
        }

        /// <summary>True if a saved rectangle is sane and its title bar would be visible and grabbable on
        /// the current desktop (it overlaps the virtual screen by a decent amount, horizontally and at the top).</summary>
        private static bool IsUsable(BoundsD b, BoundsD virtualScreen)
        {
            if (double.IsNaN(b.Left) || double.IsNaN(b.Top) || double.IsNaN(b.Width) || double.IsNaN(b.Height)) return false;
            if (b.Width < 100 || b.Height < 100) return false;

            var horizontalOverlap = Math.Min(b.Right, virtualScreen.Right) - Math.Max(b.Left, virtualScreen.Left);
            var titleBarVisible = b.Top >= virtualScreen.Top - 5 && b.Top <= virtualScreen.Bottom - 40;
            return horizontalOverlap >= 120 && titleBarVisible;
        }
    }
}
