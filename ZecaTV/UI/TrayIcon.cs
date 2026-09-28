using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZecaTV.UI
{
    /// <summary>
    /// A minimal system-tray icon for "minimize to tray" — WPF has no tray-icon control of its own, and
    /// <see cref="System.Windows.Forms.NotifyIcon"/> (enabled via UseWindowsForms in ZecaTV.csproj) is
    /// the standard way to get one without an extra NuGet package. Used only for this one control; no
    /// WinForms windows/UI are created.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly Icon _ownedIcon;

        /// <summary>Raised when the user asks to bring ZecaTV back (tray icon click, or "Open ZecaTV").</summary>
        public event Action? OpenRequested;

        /// <summary>Raised from the tray context menu's "Exit" — the only way to fully quit while
        /// minimized to tray, since there's no taskbar button to right-click-close there.</summary>
        public event Action? ExitRequested;

        public TrayIcon(string tooltipText)
        {
            var menu = new ContextMenuStrip();
            var openItem = menu.Items.Add(Localization.Strings.TrayOpenMenuItem);
            openItem.Font = new Font(openItem.Font ?? SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold);
            openItem.Click += (_, _) => OpenRequested?.Invoke();
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Localization.Strings.TrayExitMenuItem).Click += (_, _) => ExitRequested?.Invoke();

            _ownedIcon = BuildIcon();
            _icon = new NotifyIcon
            {
                Icon = _ownedIcon,
                Text = Truncate(tooltipText, 63), // NotifyIcon.Text is limited to 63 characters natively
                Visible = false,
                ContextMenuStrip = menu
            };
            _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        }

        public bool Visible
        {
            get => _icon.Visible;
            set => _icon.Visible = value;
        }

        private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

        /// <summary>A simple round accent-blue badge with a white "Z" — ZecaTV doesn't have dedicated
        /// icon artwork yet, so this is drawn on the fly rather than shipping a placeholder image file.</summary>
        private static Icon BuildIcon()
        {
            using var bitmap = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var background = new SolidBrush(Color.FromArgb(255, 0x15, 0x65, 0xC0)); // matches AccentColor
                g.FillEllipse(background, 0, 0, 32, 32);
                using var font = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
                using var textBrush = new SolidBrush(Color.White);
                var size = g.MeasureString("Z", font);
                g.DrawString("Z", font, textBrush, (32 - size.Width) / 2, (32 - size.Height) / 2 - 1);
            }

            // The icon handle from GetHicon() is normally meant to be released via DestroyIcon, but this
            // one icon lives for the whole process and is reclaimed by Windows on exit either way, so the
            // extra P/Invoke isn't worth it here.
            return Icon.FromHandle(bitmap.GetHicon());
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _ownedIcon.Dispose();
        }
    }
}
