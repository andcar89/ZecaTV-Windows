using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using ZecaTV.Data;
using ZecaTV.Localization;
using ZecaTV.Models;
using ZecaTV.UI;

namespace ZecaTV
{
    /// <summary>
    /// Add/edit the playlist source (port of AddSourceActivity). Like Android, the source is only
    /// accepted once it actually loads with at least one channel — a typo'd URL or wrong password
    /// is reported here, not after the dialog has closed.
    /// </summary>
    public partial class SourceWindow : Window
    {
        private CancellationTokenSource? _loadCts;
        private bool _passwordVisible;

        /// <summary>Set when the dialog closes with true: the source the user entered…</summary>
        public PlaylistSource? LoadedSource { get; private set; }

        /// <summary>…and its already-fetched playlist, ready for PlaylistRepository.Apply.</summary>
        public PlaylistData? LoadedData { get; private set; }

        /// <summary>The name entered (or defaulted), set alongside LoadedSource/LoadedData on success.</summary>
        public string? SavedName { get; private set; }

        public SourceWindow(string? existingName, PlaylistSource? existingSource)
        {
            InitializeComponent();
            NameBox.Text = existingName ?? "";
            Prefill(existingSource);
            Closing += OnClosing;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DarkTitleBar.Apply(this);
        }

        private void Prefill(PlaylistSource? source)
        {
            switch (source?.Type)
            {
                case SourceType.M3uFile:
                    M3uFileRadio.IsChecked = true;
                    M3uFilePathBox.Text = source.FilePath ?? "";
                    M3uFileEpgBox.Text = source.EpgUrl ?? "";
                    break;
                case SourceType.Xtream:
                    XtreamRadio.IsChecked = true;
                    XtreamServerBox.Text = source.Server ?? "";
                    XtreamUserBox.Text = source.Username ?? "";
                    XtreamPassBox.Password = source.Password ?? "";
                    break;
                case SourceType.M3uUrl:
                    M3uUrlRadio.IsChecked = true;
                    M3uUrlBox.Text = source.Url ?? "";
                    M3uUrlEpgBox.Text = source.EpgUrl ?? "";
                    break;
                default:
                    M3uUrlRadio.IsChecked = true;
                    break;
            }
        }

        private void SourceType_Checked(object sender, RoutedEventArgs e)
        {
            // Can fire while the XAML is still loading, before the panels below the radios exist.
            if (M3uUrlFields == null || M3uFileFields == null || XtreamFields == null) return;

            M3uUrlFields.Visibility = M3uUrlRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            M3uFileFields.Visibility = M3uFileRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            XtreamFields.Visibility = XtreamRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            HideMessage();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = Strings.ChooseM3uFileDialogTitle,
                Filter = "M3U playlists (*.m3u;*.m3u8)|*.m3u;*.m3u8|All files (*.*)|*.*"
            };
            var currentDir = string.IsNullOrWhiteSpace(M3uFilePathBox.Text) ? null : Path.GetDirectoryName(M3uFilePathBox.Text);
            if (currentDir != null && Directory.Exists(currentDir))
            {
                dialog.InitialDirectory = currentDir;
            }
            if (dialog.ShowDialog(this) == true)
            {
                M3uFilePathBox.Text = dialog.FileName;
                HideMessage();
            }
        }

        private void TogglePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            _passwordVisible = !_passwordVisible;
            if (_passwordVisible)
            {
                XtreamPassVisibleBox.Text = XtreamPassBox.Password;
                XtreamPassBox.Visibility = Visibility.Collapsed;
                XtreamPassVisibleBox.Visibility = Visibility.Visible;
                XtreamPassVisibleBox.CaretIndex = XtreamPassVisibleBox.Text.Length;
                TogglePasswordButton.Content = Strings.HideButton;
            }
            else
            {
                XtreamPassBox.Password = XtreamPassVisibleBox.Text;
                XtreamPassVisibleBox.Visibility = Visibility.Collapsed;
                XtreamPassBox.Visibility = Visibility.Visible;
                TogglePasswordButton.Content = Strings.ShowButton;
            }
        }

        private string CurrentPassword => _passwordVisible ? XtreamPassVisibleBox.Text : XtreamPassBox.Password;

        /// <summary>The source described by the form, or null (with a message shown) if something required is missing.</summary>
        private PlaylistSource? BuildSource()
        {
            if (M3uUrlRadio.IsChecked == true)
            {
                var url = M3uUrlBox.Text.Trim();
                if (url.Length == 0)
                {
                    ShowError(Strings.EnterPlaylistUrl);
                    return null;
                }
                return PlaylistSource.FromM3uUrl(url, NullIfBlank(M3uUrlEpgBox.Text));
            }

            if (M3uFileRadio.IsChecked == true)
            {
                var path = M3uFilePathBox.Text.Trim();
                if (path.Length == 0)
                {
                    ShowError(Strings.ChoosePlaylistFileFirst);
                    return null;
                }
                return PlaylistSource.FromM3uFile(path, NullIfBlank(M3uFileEpgBox.Text));
            }

            var server = XtreamServerBox.Text.Trim();
            var user = XtreamUserBox.Text.Trim();
            var pass = CurrentPassword.Trim();
            if (server.Length == 0 || user.Length == 0 || pass.Length == 0)
            {
                ShowError(Strings.FillServerUserPassword);
                return null;
            }
            return PlaylistSource.FromXtream(server, user, pass);
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var source = BuildSource();
            if (source == null) return;

            // An empty name is fine — a sensible one is derived from the source once it's known
            // (e.g. the M3U filename, or the Xtream server address).
            var name = string.IsNullOrWhiteSpace(NameBox.Text) ? PlaylistNaming.Suggest(source) : NameBox.Text.Trim();

            var cts = new CancellationTokenSource();
            _loadCts = cts;
            SetBusy(true);
            ShowInfo(Strings.LoadingPlaylist);

            try
            {
                var data = await PlaylistRepository.Instance.FetchAsync(source, cts.Token);

                // Closed (Cancel / Esc / X) while loading: the result is no longer wanted, and
                // setting DialogResult on a closed window would throw.
                if (cts.IsCancellationRequested || !IsVisible) return;

                if (data.Channels.Count == 0)
                {
                    ShowError(Strings.PlaylistEmptyMessage);
                    return;
                }

                LoadedSource = source;
                LoadedData = data;
                SavedName = name;
                DialogResult = true;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Dialog closed mid-load.
            }
            catch (Exception ex)
            {
                if (IsVisible) ShowError(Strings.CouldntLoadPlaylistPrefix + ErrorText.Describe(ex));
            }
            finally
            {
                if (_loadCts == cts) _loadCts = null;
                SetBusy(false);
            }
        }

        /// <summary>Cancel / Esc / the X button all land here (Cancel is IsCancel=True): stop any load in progress.</summary>
        private void OnClosing(object? sender, CancelEventArgs e) => _loadCts?.Cancel();

        private void SetBusy(bool busy)
        {
            SaveButton.IsEnabled = !busy;
            NameBox.IsEnabled = !busy;
            M3uUrlRadio.IsEnabled = M3uFileRadio.IsEnabled = XtreamRadio.IsEnabled = !busy;
            M3uUrlFields.IsEnabled = M3uFileFields.IsEnabled = XtreamFields.IsEnabled = !busy;
        }

        private void ShowInfo(string text)
        {
            MessageText.Text = text;
            MessageText.Foreground = (System.Windows.Media.Brush)FindResource("SubTextBrush");
            MessageText.Visibility = Visibility.Visible;
        }

        private void ShowError(string text)
        {
            MessageText.Text = text;
            MessageText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            MessageText.Visibility = Visibility.Visible;
        }

        private void HideMessage()
        {
            if (MessageText != null) MessageText.Visibility = Visibility.Collapsed;
        }

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
