using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using ZecaTV.Data;
using ZecaTV.Localization;
using ZecaTV.Player;

namespace ZecaTV
{
    public partial class App : Application
    {
        private static PlayerService? _player;

        /// <summary>The app-wide player (created at startup, before any window).</summary>
        public static PlayerService Player => _player ?? throw new InvalidOperationException("Player not initialized");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += OnUnhandledException;

            // Read once at startup so every {x:Static loc:Strings.X} in the very first window's XAML
            // already resolves in the right language.
            Strings.Language = SettingsStore.Current.Language;

            // Finds and loads the native libvlc shipped by the VideoLAN.LibVLC.Windows package
            // (bin\...\libvlc\win-x64 or win-x86, matching the process).
            Core.Initialize();

            _player = new PlayerService(Dispatcher) { Volume = SettingsStore.Current.Volume };

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _player?.Dispose();
            base.OnExit(e);
        }

        /// <summary>
        /// Last-chance handler: log the crash to %AppData%\ZecaTV\error.log (attach it to bug reports)
        /// and keep the app alive rather than vanishing without a trace.
        /// </summary>
        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            var logPath = Path.Combine(SettingsStore.AppDataDir, "error.log");
            try
            {
                Directory.CreateDirectory(SettingsStore.AppDataDir);
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // Nothing more we can do.
            }

            MessageBox.Show(
                $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were saved to:\n{logPath}",
                "ZecaTV", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
