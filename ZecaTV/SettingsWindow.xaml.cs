using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ZecaTV.Data;
using ZecaTV.Localization;
using ZecaTV.Models;
using ZecaTV.UI;

namespace ZecaTV
{
    /// <summary>
    /// A light hub rather than a single long form (port of the polish half of Android's
    /// SettingsActivity — its EPG-source management, playlist editing, and channel-mapping already
    /// have their own dedicated windows here, so this just summarizes each and links out to them).
    /// </summary>
    public partial class SettingsWindow : Window
    {
        /// <summary>One row of the playlists list — a snapshot, rebuilt on every refresh rather than kept live.</summary>
        private sealed class PlaylistRow
        {
            public required SavedPlaylist Playlist { get; init; }
            public required string SourceSummary { get; init; }
            public required Visibility ActiveBadgeVisibility { get; init; }
            public required Visibility SwitchVisibility { get; init; }
        }

        /// <summary>One row of the category-overrides list — see PlaylistRepository.SetCategoryOverride.</summary>
        private sealed class CategoryOverrideRow
        {
            public required string GroupTitle { get; init; }
            public required string TypeLabel { get; init; }
        }

        private static readonly int[] RefreshIntervalMinutes = { 30, 60, 120, 180, 360, 720, 1440 };

        public SettingsWindow()
        {
            InitializeComponent();
            PopulateRefreshIntervalCombo();
            Refresh();
        }

        private void PopulateRefreshIntervalCombo()
        {
            BackgroundRefreshIntervalCombo.Items.Clear();
            foreach (var minutes in RefreshIntervalMinutes)
            {
                BackgroundRefreshIntervalCombo.Items.Add(new ComboBoxItem
                {
                    Content = minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h",
                    Tag = minutes
                });
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DarkTitleBar.Apply(this);
        }

        private void Refresh()
        {
            RefreshPlaylists();
            RefreshCategoryOverrides();
            EpgSummaryText.Text = DescribeEpg();
            ResumeChannelCheck.IsChecked = SettingsStore.Current.ResumeLastChannelOnStartup;

            BackgroundRefreshCheck.IsChecked = SettingsStore.Current.BackgroundRefreshEnabled;
            BackgroundRefreshIntervalCombo.IsEnabled = SettingsStore.Current.BackgroundRefreshEnabled;
            var intervalIndex = Array.IndexOf(RefreshIntervalMinutes, SettingsStore.Current.BackgroundRefreshIntervalMinutes);
            BackgroundRefreshIntervalCombo.SelectedIndex = intervalIndex >= 0 ? intervalIndex : Array.IndexOf(RefreshIntervalMinutes, 180);

            MinimizeToTrayCheck.IsChecked = SettingsStore.Current.MinimizeToTray;

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = version == null ? "ZecaTV" : $"ZecaTV {version.Major}.{version.Minor}.{version.Build}";
        }

        private void LanguageEnButton_Click(object sender, RoutedEventArgs e) => ChangeLanguage(AppLanguage.English);

        private void LanguagePtButton_Click(object sender, RoutedEventArgs e) => ChangeLanguage(AppLanguage.Portuguese);

        /// <summary>Changing it here doesn't re-render this already-open window's own {x:Static} text
        /// (that only resolves once, when a window is constructed) — the note explains that new windows
        /// already reflect it, and this one (plus MainWindow) needs a restart.</summary>
        private void ChangeLanguage(AppLanguage language)
        {
            if (Strings.Language == language)
            {
                LanguageNoteText.Visibility = Visibility.Collapsed;
                return;
            }
            Strings.Language = language;
            SettingsStore.Current.Language = language;
            SettingsStore.Save();
            LanguageNoteText.Visibility = Visibility.Visible;
        }

        private void RefreshPlaylists()
        {
            var activeId = SettingsStore.Current.ActivePlaylistId;
            var rows = SettingsStore.Current.Playlists.Select(p => new PlaylistRow
            {
                Playlist = p,
                SourceSummary = ShortSourceSummary(p.Source),
                ActiveBadgeVisibility = p.Id == activeId ? Visibility.Visible : Visibility.Collapsed,
                SwitchVisibility = p.Id == activeId ? Visibility.Collapsed : Visibility.Visible
            }).ToList();
            PlaylistsList.ItemsSource = rows;
        }

        private static string ShortSourceSummary(PlaylistSource source) => source.Type switch
        {
            SourceType.M3uUrl => source.Url ?? "",
            SourceType.M3uFile => source.FilePath ?? "",
            SourceType.Xtream => string.Format(Strings.XtreamSourceSummaryFormat, source.Server, source.Username),
            _ => ""
        };

        private void RefreshCategoryOverrides()
        {
            var rows = SettingsStore.Current.CategoryContentOverrides
                .Select(kv => new CategoryOverrideRow { GroupTitle = kv.Key, TypeLabel = TypeLabel(kv.Value) })
                .OrderBy(r => r.GroupTitle, StringComparer.OrdinalIgnoreCase)
                .ToList();
            CategoryOverridesList.ItemsSource = rows;
            NoCategoryOverridesText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CategoryOverridesScroll.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string TypeLabel(ContentType type) => type switch
        {
            ContentType.Movie => Strings.SectionMovies,
            ContentType.Series => Strings.SectionShows,
            _ => Strings.SectionLive
        };

        private void RemoveCategoryOverride_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is not string groupTitle) return;
            PlaylistRepository.Instance.SetCategoryOverride(groupTitle, null);
            (Owner as MainWindow)?.RefreshAfterCategoryOverrideChange();
            RefreshCategoryOverrides();
        }

        private static string DescribeEpg()
        {
            var sources = EpgStore.Instance.AvailableSources();
            if (sources.Count == 0) return Strings.NoEpgSourcesYet;

            var loaded = 0;
            var channels = 0;
            var programmes = 0;
            foreach (var (id, _) in sources)
            {
                var status = EpgStore.Instance.GetSourceStatus(id);
                if (!status.IsLoaded) continue;
                loaded++;
                channels += status.ChannelCount;
                programmes += status.ProgrammeCount;
            }

            return loaded == 0
                ? string.Format(Strings.EpgSourcesNoneLoadedFormat, sources.Count)
                : string.Format(Strings.EpgSourcesLoadedFormat, loaded, sources.Count, channels, programmes);
        }

        private void AddPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            (Owner as MainWindow)?.AddPlaylist();
            RefreshPlaylists();
        }

        private void EditPlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is not SavedPlaylist playlist) return;
            (Owner as MainWindow)?.EditPlaylist(playlist);
            RefreshPlaylists();
        }

        private async void SwitchPlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is not SavedPlaylist playlist) return;
            if (Owner is MainWindow mainWindow) await mainWindow.SwitchPlaylistAsync(playlist);
            RefreshPlaylists();
        }

        private async void RemovePlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is not SavedPlaylist playlist) return;
            if (Owner is MainWindow mainWindow) await mainWindow.RemovePlaylistAsync(playlist);
            RefreshPlaylists();
        }

        private void ManageEpgSourcesButton_Click(object sender, RoutedEventArgs e)
        {
            new EpgSourcesWindow { Owner = this }.ShowDialog();
            Refresh();
        }

        private void AssignChannelEpgButton_Click(object sender, RoutedEventArgs e)
        {
            new EpgMappingWindow { Owner = this }.ShowDialog();
        }

        private async void ReloadEpgButton_Click(object sender, RoutedEventArgs e)
        {
            ReloadEpgButton.IsEnabled = false;
            await EpgStore.Instance.ReloadAllAsync();
            ReloadEpgButton.IsEnabled = true;
            Refresh();
        }

        private void ResumeChannelCheck_Changed(object sender, RoutedEventArgs e)
        {
            SettingsStore.Current.ResumeLastChannelOnStartup = ResumeChannelCheck.IsChecked == true;
            SettingsStore.Save();
        }

        private void BackgroundRefreshCheck_Changed(object sender, RoutedEventArgs e)
        {
            var enabled = BackgroundRefreshCheck.IsChecked == true;
            SettingsStore.Current.BackgroundRefreshEnabled = enabled;
            SettingsStore.Save();
            BackgroundRefreshIntervalCombo.IsEnabled = enabled;
        }

        private void BackgroundRefreshIntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BackgroundRefreshIntervalCombo.SelectedItem is not ComboBoxItem { Tag: int minutes }) return;
            if (SettingsStore.Current.BackgroundRefreshIntervalMinutes == minutes) return;
            SettingsStore.Current.BackgroundRefreshIntervalMinutes = minutes;
            SettingsStore.Save();
            (Owner as MainWindow)?.RestartBackgroundRefreshTimer();
        }

        private void MinimizeToTrayCheck_Changed(object sender, RoutedEventArgs e)
        {
            SettingsStore.Current.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
            SettingsStore.Save();
        }

        private void ViewControlsButton_Click(object sender, RoutedEventArgs e)
        {
            new ControlsWindow { Owner = this }.ShowDialog();
        }

        private void BackupButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = Strings.BackupDialogTitle,
                Filter = "ZecaTV backup (*.json)|*.json|All files (*.*)|*.*",
                FileName = $"ZecaTV-backup-{DateTime.Now:yyyy-MM-dd}.json"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                SettingsStore.ExportTo(dialog.FileName);
                ShowBackupStatus(string.Format(Strings.BackupSavedFormat, dialog.FileName), isError: false);
            }
            catch (Exception ex)
            {
                ShowBackupStatus(string.Format(Strings.BackupFailedFormat, ex.Message), isError: true);
            }
        }

        private void RestoreButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Strings.RestoreDialogTitle,
                Filter = "ZecaTV backup (*.json)|*.json|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) != true) return;

            var confirmed = MessageBox.Show(this, Strings.RestoreConfirmMessage, Strings.RestoreConfirmTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!confirmed) return;

            try
            {
                SettingsStore.ImportFrom(dialog.FileName);
                ShowBackupStatus(Strings.RestoreDoneMessage, isError: false);
            }
            catch (InvalidDataException)
            {
                ShowBackupStatus(Strings.InvalidBackupFile, isError: true);
            }
            catch (Exception ex)
            {
                ShowBackupStatus(string.Format(Strings.RestoreFailedFormat, ex.Message), isError: true);
            }
        }

        private void ShowBackupStatus(string text, bool isError)
        {
            BackupStatusText.Text = text;
            BackupStatusText.Foreground = (Brush)FindResource(isError ? "ErrorBrush" : "SubTextBrush");
            BackupStatusText.Visibility = Visibility.Visible;
        }

        private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = SettingsStore.AppDataDir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Couldn't open logs folder: {ex}");
            }
        }

        /// <summary>Opens the bundled M3uEditor.html (a self-contained web tool, unrelated to the rest of
        /// ZecaTV's own code) in the system's default browser. UseShellExecute — not LibVLC or anything
        /// of ours — is what makes Windows treat a .html path as "open with the default browser".</summary>
        private void OpenM3uEditorButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "M3uEditor.html");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Couldn't open the M3U editor: {ex}");
            }
        }
    }
}
