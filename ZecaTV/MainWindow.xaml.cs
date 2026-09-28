using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ZecaTV.Data;
using ZecaTV.Localization;
using ZecaTV.Models;
using ZecaTV.Network;
using ZecaTV.Parsers;
using ZecaTV.Player;
using ZecaTV.UI;

namespace ZecaTV
{
    /// <summary>
    /// Main screen: categories | channels | video. Merges the Android MainActivity (browse + mini
    /// preview) and PlayerActivity (fullscreen + overlay sidebars). On Windows, "fullscreen" is this
    /// same window with the side panels hidden and the video expanded, so the stream is never handed
    /// between views.
    /// </summary>
    public partial class MainWindow : Window
    {
        private static string FavoritesLabel => Strings.CategoryFavorites;
        private static string AllChannelsLabel => Strings.CategoryAllChannels;
        private static string UncategorizedLabel => Strings.CategoryUncategorized;

        private enum SideMode
        {
            None,
            Channels,
            Categories
        }

        /// <summary>Which sidebar tab is currently shown — see the section tabs above CategoryList.</summary>
        private enum Section
        {
            Live,
            Movies,
            Shows
        }

        private readonly PlaylistRepository _repo = PlaylistRepository.Instance;
        private readonly PlayerService _player = App.Player;
        private readonly EpgStore _epg = EpgStore.Instance;
        private readonly DispatcherTimer _infoBarTimer;
        private readonly DispatcherTimer _osdTimer;
        private readonly DispatcherTimer _cursorTimer;
        private readonly DispatcherTimer _sidePanelTimer;
        private readonly DispatcherTimer _epgTimer;
        private DispatcherTimer? _backgroundRefreshTimer;
        private readonly TrayIcon _trayIcon;

        /// <summary>One ChannelItem per channel of the current playlist (by reference — records compare by value).</summary>
        private Dictionary<Channel, ChannelItem> _items = new(ReferenceEqualityComparer.Instance);

        private IReadOnlyList<Category> _categories = Array.Empty<Category>();

        /// <summary>Category shown in the main channel list, and its items.</summary>
        private string _currentCategoryId = PlaylistRepository.CategoryAll;
        private IReadOnlyList<ChannelItem> _visibleItems = Array.Empty<ChannelItem>();

        /// <summary>The list (and its category) the playing channel was started from — what ↑/↓ zap through.</summary>
        private IReadOnlyList<ChannelItem> _playbackItems = Array.Empty<ChannelItem>();
        private string _playbackCategoryId = PlaylistRepository.CategoryAll;

        private ChannelItem? _playingItem;
        private CancellationTokenSource? _loadCts;
        private CancellationTokenSource? _epgLoadCts;
        private bool _isFirstLoad = true;
        private bool _isLoading;
        private bool _suppressCategoryChange;
        private bool _suppressPlaylistSwitcherChange;

        private bool _isFullscreen;
        private WindowState _restoreWindowState;
        private WindowStyle _restoreWindowStyle;
        private ResizeMode _restoreResizeMode;
        private Point _lastMousePosition;

        // Fullscreen side panel state.
        private SideMode _sideMode = SideMode.None;
        private string _sideCategoryId = PlaylistRepository.CategoryAll;
        private IReadOnlyList<ChannelItem> _sideItems = Array.Empty<ChannelItem>();

        private VideoRatioMode _videoRatioMode = SettingsStore.Current.VideoRatioMode;

        // ---- Movies / TV Shows tabs ----------------------------------------------------------
        // Mirrors the Live TV state above (_items/_categories/_visibleItems/_currentCategoryId) but kept
        // completely separate: Number is always 0 for these rows (see ChannelItem), so merging them into
        // _items would risk collisions in the direct-channel-entry digit feature (CommitChannelEntry).

        private Section _section = Section.Live;

        /// <summary>One ChannelItem per movie of the current playlist (Number always 0).</summary>
        private Dictionary<Channel, ChannelItem> _movieItems = new(ReferenceEqualityComparer.Instance);
        private IReadOnlyList<Category> _movieCategories = Array.Empty<Category>();
        private string _currentMovieCategoryId = PlaylistRepository.CategoryAll;
        private IReadOnlyList<ChannelItem> _visibleMovieItems = Array.Empty<ChannelItem>();

        /// <summary>One ShowItem per show tile of the current playlist.</summary>
        private Dictionary<ShowSummary, ShowItem> _showItems = new(ReferenceEqualityComparer.Instance);
        private IReadOnlyList<Category> _showCategories = Array.Empty<Category>();
        private string _currentShowCategoryId = PlaylistRepository.CategoryAll;
        private IReadOnlyList<ShowItem> _visibleShows = Array.Empty<ShowItem>();

        /// <summary>The show currently drilled into (its episode list is showing in ChannelList), or
        /// null while browsing the show grid itself.</summary>
        private ShowSummary? _openShow;
        private IReadOnlyList<ChannelItem> _openShowEpisodes = Array.Empty<ChannelItem>();
        private CancellationTokenSource? _episodesLoadCts;

        // Direct channel entry: type a channel's Number (shown next to it everywhere in the UI already),
        // then Enter — or just pause for a moment and it commits on its own.
        private string _channelEntryBuffer = "";
        private readonly DispatcherTimer _channelEntryTimer;

        // VOD (Movie/episode) transport controls (play/pause + seek bar) — see UpdateVodControls.
        private readonly DispatcherTimer _vodProgressTimer;
        private bool _seekDragging;

        public MainWindow()
        {
            InitializeComponent();

            _infoBarTimer = NewOneShotTimer(TimeSpan.FromSeconds(4), () => InfoBar.Visibility = Visibility.Collapsed);
            _osdTimer = NewOneShotTimer(TimeSpan.FromSeconds(1.5), () => OsdBadge.Visibility = Visibility.Collapsed);
            _cursorTimer = NewOneShotTimer(TimeSpan.FromSeconds(3), () =>
            {
                if (_isFullscreen) Overlay.Cursor = Cursors.None;
            });
            _sidePanelTimer = NewOneShotTimer(TimeSpan.FromSeconds(12), CloseSidePanel);
            _channelEntryTimer = NewOneShotTimer(TimeSpan.FromMilliseconds(1200), CommitChannelEntry);

            // Advances the seek bar's position while a VOD plays — LibVLC's own TimeChanged event fires
            // many times a second, far more often than the UI needs, so this polls instead.
            _vodProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _vodProgressTimer.Tick += (_, _) => UpdateVodControls();
            _vodProgressTimer.Start();

            // Now/next text ages by the minute even with no data changes, so it's re-stamped on a
            // timer rather than only in response to EpgStore.Changed.
            _epgTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _epgTimer.Tick += (_, _) => { RefreshEpgText(); UpdateEpgPanel(); };
            _epgTimer.Start();

            _player.StateChanged += (_, _) => UpdatePlayerStatus();
            // A new stream gets a fresh LibVLC video output, which can reset its own aspect-ratio
            // override back to default — re-apply ours every time playback (re)starts.
            _player.StateChanged += (_, _) => { if (_player.State == PlaybackState.Playing) ApplyVideoRatioMode(); };
            VideoFrame.SizeChanged += (_, _) => { if (_videoRatioMode == VideoRatioMode.Fill) ApplyVideoRatioMode(); };
            _epg.Changed += (_, _) => { RefreshEpgText(); UpdateEpgPanel(); };
            RestartBackgroundRefreshTimer();

            _trayIcon = new TrayIcon(Strings.TrayTooltip);
            _trayIcon.OpenRequested += RestoreFromTray;
            _trayIcon.ExitRequested += () => Close();
            StateChanged += MainWindow_StateChanged;

            Loaded += MainWindow_Loaded;
        }

        /// <summary>Minimizing sends the window to the tray instead of the taskbar, when enabled in
        /// Settings — restoring is the tray icon itself (click, double-click, or its "Open ZecaTV").
        /// Maximizing/restoring to Normal otherwise behaves exactly as a normal window.</summary>
        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState != WindowState.Minimized || !SettingsStore.Current.MinimizeToTray) return;
            ShowInTaskbar = false;
            Hide();
            _trayIcon.Visible = true;
        }

        private void RestoreFromTray()
        {
            _trayIcon.Visible = false;
            Show();
            WindowState = WindowState.Normal;
            ShowInTaskbar = true;
            Activate();
        }

        /// <summary>(Re)starts the background-refresh timer at Settings' current interval — public so
        /// SettingsWindow can call it right after the user picks a new interval, rather than waiting for
        /// a restart. Safe to call any number of times; harmless if background refresh is currently off,
        /// since <see cref="BackgroundRefreshTick"/> checks that itself on every tick.</summary>
        public void RestartBackgroundRefreshTimer()
        {
            _backgroundRefreshTimer?.Stop();
            var minutes = Math.Max(5, SettingsStore.Current.BackgroundRefreshIntervalMinutes);
            _backgroundRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
            _backgroundRefreshTimer.Tick += (_, _) => BackgroundRefreshTick();
            _backgroundRefreshTimer.Start();
        }

        /// <summary>
        /// Quietly re-fetches the active playlist (and, as part of that, its EPG) so a session left
        /// running for days doesn't go stale — same path as pressing F5. Skipped (and simply retried at
        /// the next tick) whenever it would visibly disrupt what's on screen right now: already loading,
        /// fullscreen (actively being watched), or an active search whose results it would silently
        /// replace with the plain category view.
        /// </summary>
        private void BackgroundRefreshTick()
        {
            if (!SettingsStore.Current.BackgroundRefreshEnabled) return;
            if (_isLoading || _isFullscreen) return;
            if (SearchBox.Text.Length > 0) return;
            if (SettingsStore.ActivePlaylist() is not { } active) return;
            _ = LoadSourceAsync(active.Source);
        }

        private static DispatcherTimer NewOneShotTimer(TimeSpan interval, Action onElapsed)
        {
            var timer = new DispatcherTimer { Interval = interval };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                onElapsed();
            };
            return timer;
        }

        private static void Restart(DispatcherTimer timer)
        {
            timer.Stop();
            timer.Start();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DarkTitleBar.Apply(this);
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdatePlayerStatus();
            RefreshPlaylistSwitcher();

            GuideButton.IsEnabled = false;
            var active = SettingsStore.ActivePlaylist();
            if (active == null)
            {
                ShowWelcome(Strings.WelcomeTitle, Strings.WelcomeMessage, canRetry: false);
                return;
            }

            await LoadSourceAsync(active.Source);
        }

        private void VideoView_Loaded(object sender, RoutedEventArgs e)
        {
            // Must happen once the VideoView's native window exists (i.e. here, not in the
            // constructor) — the LibVLCSharp samples do the same.
            if (VideoView.MediaPlayer == null)
            {
                VideoView.MediaPlayer = _player.MediaPlayer;
            }
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            _loadCts?.Cancel();
            _epgLoadCts?.Cancel();
            _backgroundRefreshTimer?.Stop();
            _trayIcon.Dispose();
            SettingsStore.Current.Volume = _player.Volume;
            SettingsStore.Save();
        }

        // ---- Playlist loading ------------------------------------------------------------------

        private async Task LoadSourceAsync(PlaylistSource source)
        {
            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;
            SetLoading(true, Strings.LoadingPlaylist);

            try
            {
                var data = await _repo.FetchAsync(source, cts.Token);
                if (cts.IsCancellationRequested) return;

                if (data.Channels.Count == 0 && data.Movies.Count == 0 && data.Shows.Count == 0)
                {
                    ReportLoadFailure(Strings.PlaylistEmptyMessage);
                    return;
                }

                ApplyPlaylist(data);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Superseded by another load or by the playlist dialog — nothing to report.
            }
            catch (Exception ex)
            {
                ReportLoadFailure(ErrorText.Describe(ex));
            }
            finally
            {
                if (_loadCts == cts) SetLoading(false, null);
            }
        }

        private void ReportLoadFailure(string message)
        {
            if (_repo.Channels.Count == 0 && _repo.Movies.Count == 0 && _repo.Shows.Count == 0)
            {
                ShowWelcome(Strings.CouldntLoadPlaylistTitle, message, canRetry: true);
                StatusText.Text = "";
            }
            else
            {
                // A failed *reload* keeps the channels we already have.
                StatusText.Text = Strings.ReloadFailedPrefix + message;
            }
        }

        private void SetLoading(bool loading, string? status)
        {
            _isLoading = loading;
            var hasSource = SettingsStore.ActivePlaylist() != null;
            ReloadButton.IsEnabled = !loading && hasSource;
            WelcomeRetryButton.IsEnabled = !loading;
            WelcomeSourceButton.IsEnabled = !loading;
            if (status != null) StatusText.Text = status;
        }

        /// <summary>Makes a freshly loaded playlist current and rebuilds the lists.</summary>
        private void ApplyPlaylist(PlaylistData data)
        {
            CloseSidePanel();
            _episodesLoadCts?.Cancel();
            _repo.Apply(data);

            var items = new Dictionary<Channel, ChannelItem>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < data.Channels.Count; i++)
            {
                items[data.Channels[i]] = new ChannelItem(data.Channels[i], i + 1);
            }
            _items = items;
            _playbackItems = Array.Empty<ChannelItem>();
            _playingItem = null;

            var movieItems = new Dictionary<Channel, ChannelItem>(ReferenceEqualityComparer.Instance);
            foreach (var movie in data.Movies) movieItems[movie] = new ChannelItem(movie, 0);
            _movieItems = movieItems;
            _visibleMovieItems = Array.Empty<ChannelItem>();

            var showItems = new Dictionary<ShowSummary, ShowItem>(ReferenceEqualityComparer.Instance);
            foreach (var show in data.Shows) showItems[show] = new ShowItem(show);
            _showItems = showItems;
            _visibleShows = Array.Empty<ShowItem>();
            _openShow = null;
            _openShowEpisodes = Array.Empty<ChannelItem>();

            // Stay on whichever tab was open — a periodic background refresh (see BackgroundRefreshTick)
            // re-runs this same method, and it would be jarring to get yanked back to Live TV every few
            // minutes while browsing Movies/Shows. Only fall back to Live if the new data no longer has
            // that tab's content at all (e.g. switched to a different, Live-only playlist).
            if ((_section == Section.Movies && data.Movies.Count == 0) ||
                (_section == Section.Shows && data.Shows.Count == 0))
            {
                _section = Section.Live;
            }
            SearchBox.IsEnabled = _section == Section.Live;
            MoviesSectionButton.IsEnabled = data.Movies.Count > 0;
            ShowsSectionButton.IsEnabled = data.Shows.Count > 0;
            UpdateSectionButtonStyles();

            // If the playing channel is still in the new list (same stream URL), keep it playing.
            var current = _player.CurrentChannel;
            if (current != null)
            {
                var match = data.Channels.FirstOrDefault(c => c.Key == current.Key);
                if (match == null)
                {
                    _player.Stop();
                }
                else
                {
                    _playingItem = items[match];
                    _playingItem.IsPlaying = true;
                }
            }
            UpdateNowPlaying();

            HideWelcome();
            GuideButton.IsEnabled = true;
            RefreshPlaylistSwitcher();

            // Live's own category list/counts are recomputed unconditionally (StatusText below reports
            // them regardless of which tab is active) — only the ON-SCREEN lists depend on the section.
            var selected = RebuildCategories(SettingsStore.ActivePlaylist()?.LastCategoryId ?? PlaylistRepository.CategoryAll);
            switch (_section)
            {
                case Section.Live: ShowCategory(selected); break;
                case Section.Movies: ShowMovieCategory(RebuildMovieCategories(_currentMovieCategoryId)); break;
                case Section.Shows: ShowShowCategory(RebuildShowCategories(_currentShowCategoryId)); break;
            }

            var groupCount = _categories.Count(c => c.Id != PlaylistRepository.CategoryAll && c.Id != PlaylistRepository.CategoryFavorites);
            StatusText.Text = string.Format(Strings.ChannelsCategoriesFormat, data.Channels.Count, groupCount);

            Dispatcher.InvokeAsync(() => FocusSelected(ChannelList), DispatcherPriority.Loaded);

            // Channels are on screen already; the guide fills in as each source downloads and
            // parses, same as Android's best-effort per-source loading — just non-blocking here.
            _epgLoadCts?.Cancel();
            var epgCts = new CancellationTokenSource();
            _epgLoadCts = epgCts;
            _epg.Reset(data.EpgUrls);
            RefreshEpgText();
            _ = _epg.ReloadAllAsync(epgCts.Token);

            if (_isFirstLoad && SettingsStore.Current.ResumeLastChannelOnStartup &&
                SettingsStore.Current.LastChannelKey is { } lastKey &&
                _items.Values.FirstOrDefault(i => i.Channel.Key == lastKey) is { } lastItem)
            {
                PlayItem(lastItem, _visibleItems.Contains(lastItem) ? _visibleItems : BuildItems(PlaylistRepository.CategoryAll),
                    _visibleItems.Contains(lastItem) ? _currentCategoryId : PlaylistRepository.CategoryAll);
            }
            _isFirstLoad = false;
        }

        /// <summary>Recomputes Subtitle for every item currently on screen (main list + side panel, if open).</summary>
        private void RefreshEpgText()
        {
            var now = DateTimeOffset.Now;
            foreach (var item in _visibleItems) item.UpdateEpg(_epg, now);
            if (_sideMode != SideMode.None)
            {
                foreach (var item in _sideItems) item.UpdateEpg(_epg, now);
            }
        }

        // ---- Welcome / error panel ---------------------------------------------------------------

        private void ShowWelcome(string title, string message, bool canRetry)
        {
            WelcomeTitle.Text = title;
            WelcomeMessage.Text = message;
            WelcomeRetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
            WelcomeSourceButton.Content = SettingsStore.ActivePlaylist() == null ? Strings.AddPlaylistButton : Strings.EditPlaylistButton;

            // The bare first-run screen (no playlist at all — canRetry is only ever true once one
            // exists but failed to load) hides the top bar entirely: nothing there is usable yet, and
            // its own language buttons stand in for the ones normally reached via Settings. Once a
            // playlist exists, keep the top bar (its playlist switcher may offer a working one).
            TopBar.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
            WelcomeLanguagePanel.Visibility = canRetry ? Visibility.Collapsed : Visibility.Visible;

            // Hidden, NOT Collapsed: a hidden VideoView still gets laid out, so its native window and
            // overlay window are set up correctly even when the app starts on this panel. The overlay
            // lives in its own transparent window that ignores our visibility, so its content is
            // collapsed separately — otherwise it would sit invisibly on top of the welcome buttons
            // and swallow clicks.
            ContentGrid.Visibility = Visibility.Hidden;
            Overlay.Visibility = Visibility.Collapsed;
            WelcomePanel.Visibility = Visibility.Visible;
            WelcomeSourceButton.Focus();
        }

        private void HideWelcome()
        {
            WelcomePanel.Visibility = Visibility.Collapsed;
            ContentGrid.Visibility = Visibility.Visible;
            Overlay.Visibility = Visibility.Visible;
            TopBar.Visibility = Visibility.Visible;
            // The top bar sat hidden (and its {x:Static} text resolved only once, at startup) while
            // the bare first-run screen was up — if its own language buttons changed the language in
            // the meantime, this is the one moment that needs to catch it back up before it's seen.
            RefreshChrome();
        }

        /// <summary>Re-applies Strings.* to every top-bar element that only resolves its text once (at
        /// MainWindow's construction) via {x:Static} — see HideWelcome and the welcome screen's own
        /// language buttons.</summary>
        private void RefreshChrome()
        {
            ReloadButton.Content = Strings.ReloadButton;
            ReloadButton.ToolTip = Strings.ReloadButtonTooltip;
            GuideButton.Content = Strings.TvGuideButton;
            GuideButton.ToolTip = Strings.TvGuideButtonTooltip;
            SettingsButton.Content = Strings.SettingsButton;
            PlaylistSwitcher.ToolTip = Strings.SwitchPlaylistTooltip;
            SearchPlaceholder.Text = Strings.SearchPlaceholder;
            CategoriesHeaderText.Text = Strings.CategoriesHeader;
            SideCategoriesHeaderText.Text = Strings.CategoriesHeader;
            IdleHint.Text = Strings.SelectChannelHint;
        }

        private void WelcomeLanguageEnButton_Click(object sender, RoutedEventArgs e) => ChangeWelcomeLanguage(AppLanguage.English);

        private void WelcomeLanguagePtButton_Click(object sender, RoutedEventArgs e) => ChangeWelcomeLanguage(AppLanguage.Portuguese);

        /// <summary>The only text visible right now is the welcome screen's own — re-apply just that
        /// immediately; RefreshChrome catches the (still-hidden) rest up later, in HideWelcome.</summary>
        private void ChangeWelcomeLanguage(AppLanguage language)
        {
            if (Strings.Language == language) return;
            Strings.Language = language;
            SettingsStore.Current.Language = language;
            SettingsStore.Save();

            WelcomeTitle.Text = Strings.WelcomeTitle;
            WelcomeMessage.Text = Strings.WelcomeMessage;
            WelcomeSourceButton.Content = SettingsStore.ActivePlaylist() == null ? Strings.AddPlaylistButton : Strings.EditPlaylistButton;
            WelcomeRetryButton.Content = Strings.TryAgainButton;
        }

        // ---- Categories & channels --------------------------------------------------------------

        private Category? FindCategory(string id) => _categories.FirstOrDefault(c => c.Id == id);

        private List<ChannelItem> BuildItems(string categoryId)
        {
            var channels = _repo.ChannelsForCategory(categoryId);
            var list = new List<ChannelItem>(channels.Count);
            foreach (var channel in channels) list.Add(_items[channel]);
            return list;
        }

        /// <summary>
        /// Recomputes the category list (counts, Favorites appearing/disappearing) and selects
        /// <paramref name="preferredId"/>, or All channels if that category no longer exists.
        /// Doesn't touch the channel list — returns the selected category for the caller to show.
        /// </summary>
        private Category RebuildCategories(string preferredId)
        {
            _categories = _repo.Categories(FavoritesLabel, AllChannelsLabel, UncategorizedLabel);
            var selected = FindCategory(preferredId) ?? FindCategory(PlaylistRepository.CategoryAll)!;

            _suppressCategoryChange = true;
            CategoryList.ItemsSource = _categories;
            CategoryList.SelectedItem = selected;
            _suppressCategoryChange = false;
            CategoryList.ScrollIntoView(selected);
            return selected;
        }

        private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCategoryChange) return;
            if (_section == Section.Live && SearchBox.Text.Length > 0)
            {
                SearchBox.Clear(); // its own TextChanged handler shows the newly picked category
                return;
            }
            if (CategoryList.SelectedItem is not Category category) return;
            switch (_section)
            {
                case Section.Live: ShowCategory(category); break;
                case Section.Movies: ShowMovieCategory(category); break;
                case Section.Shows: ShowShowCategory(category); break;
            }
        }

        // ---- Sections: Live TV / Movies / TV Shows ------------------------------------------------

        private void SectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag }) return;
            var section = tag switch
            {
                "Movies" => Section.Movies,
                "Shows" => Section.Shows,
                _ => Section.Live
            };
            SwitchSection(section);
        }

        private void SwitchSection(Section section)
        {
            if (_section == section) return;
            _episodesLoadCts?.Cancel(); // leaving Shows (or switching within it) drops any in-flight Xtream episode fetch
            SearchBox.Clear(); // search is Live-only (see ShowSearchResults) — don't leave it active behind another tab
            _section = section;
            SearchBox.IsEnabled = section == Section.Live;
            UpdateSectionButtonStyles();

            switch (section)
            {
                case Section.Live:
                    ChannelList.ItemTemplate = (DataTemplate)Resources["ChannelTemplate"];
                    ShowsBackButton.Visibility = Visibility.Collapsed;
                    ShowCategory(RebuildCategories(_currentCategoryId));
                    break;
                case Section.Movies:
                    ChannelList.ItemTemplate = (DataTemplate)Resources["ChannelTemplate"];
                    ShowsBackButton.Visibility = Visibility.Collapsed;
                    ShowMovieCategory(RebuildMovieCategories(_currentMovieCategoryId));
                    break;
                case Section.Shows:
                    ShowShowCategory(RebuildShowCategories(_currentShowCategoryId));
                    break;
            }
        }

        private void UpdateSectionButtonStyles()
        {
            var selected = (Style)Resources["SectionTabButtonSelected"];
            var normal = (Style)Resources["SectionTabButton"];
            LiveSectionButton.Style = _section == Section.Live ? selected : normal;
            MoviesSectionButton.Style = _section == Section.Movies ? selected : normal;
            ShowsSectionButton.Style = _section == Section.Shows ? selected : normal;
        }

        // ---- Search (always across every channel, regardless of the selected category) -----------

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var query = SearchBox.Text.Trim();
            SearchPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (query.Length == 0)
            {
                if (CategoryList.SelectedItem is Category category) ShowCategory(category);
                return;
            }
            ShowSearchResults(query);
        }

        /// <summary>Live substring search (case-insensitive, matches anywhere in the name) across every
        /// channel in the playlist — deliberately ignoring whatever category is selected in the sidebar,
        /// so a search always looks at the whole playlist.</summary>
        private void ShowSearchResults(string query)
        {
            var results = _repo.Channels
                .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(c => _items[c])
                .ToList();

            _visibleItems = results;
            _currentCategoryId = PlaylistRepository.CategoryAll; // closest existing category if the user backs out via ← etc.
            ChannelList.ItemsSource = results;
            ChannelHeader.Text = string.Format(Strings.SearchResultsFormat, query, results.Count);

            var now = DateTimeOffset.Now;
            foreach (var item in results) item.UpdateEpg(_epg, now);

            var index = _playingItem != null ? results.IndexOf(_playingItem) : -1;
            if (index < 0 && results.Count > 0) index = 0;
            if (index >= 0)
            {
                ChannelList.SelectedIndex = index;
                ChannelList.ScrollIntoView(results[index]);
            }
        }

        /// <summary>Escape clears the search; Enter plays the selected result; ↑/↓ move the selection —
        /// all without leaving the search box, so you can type, arrow to a result, and hit Enter.</summary>
        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    SearchBox.Clear();
                    FocusSelected(ChannelList);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (ChannelList.SelectedItem is ChannelItem item) ActivateChannel(item);
                    e.Handled = true;
                    break;
                case Key.Down:
                    MoveSelection(ChannelList, +1, wrap: false);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveSelection(ChannelList, -1, wrap: false);
                    e.Handled = true;
                    break;
            }
        }

        /// <param name="preferredIndex">Row to select if the playing channel isn't in this category (clamped).</param>
        private void ShowCategory(Category category, int? preferredIndex = null)
        {
            var previous = _visibleItems;
            var list = BuildItems(category.Id);

            _visibleItems = list;
            _currentCategoryId = category.Id;
            ChannelList.ItemsSource = list;
            ChannelHeader.Text = $"{category.Label.ToUpperInvariant()}  ·  {list.Count:N0}";
            if (SettingsStore.ActivePlaylist() is { } active) active.LastCategoryId = category.Id;

            // Zapping was going through the list this one replaces (e.g. Favorites after a change):
            // follow the new version, as long as the playing channel is still in it.
            if (ReferenceEquals(_playbackItems, previous) && _playbackCategoryId == category.Id &&
                _playingItem != null && list.Contains(_playingItem))
            {
                _playbackItems = list;
            }

            var index = _playingItem == null ? -1 : list.IndexOf(_playingItem);
            if (index < 0 && list.Count > 0) index = Math.Clamp(preferredIndex ?? 0, 0, list.Count - 1);
            if (index >= 0)
            {
                ChannelList.SelectedIndex = index;
                ChannelList.ScrollIntoView(list[index]);
                if (_playbackItems.Count == 0 && _playingItem != null && ReferenceEquals(list[index], _playingItem))
                {
                    _playbackItems = list;
                    _playbackCategoryId = category.Id;
                }
            }

            var now = DateTimeOffset.Now;
            foreach (var channelItem in list) channelItem.UpdateEpg(_epg, now);
        }

        // ---- Movies tab (same shape as Live TV above, minus EPG/favorites/catch-up) ---------------

        private List<ChannelItem> BuildMovieItems(string categoryId)
        {
            var movies = _repo.MoviesForCategory(categoryId);
            var list = new List<ChannelItem>(movies.Count);
            foreach (var movie in movies) list.Add(_movieItems[movie]);
            return list;
        }

        private Category? FindMovieCategory(string id) => _movieCategories.FirstOrDefault(c => c.Id == id);

        private Category RebuildMovieCategories(string preferredId)
        {
            _movieCategories = _repo.MovieCategories(AllChannelsLabel, UncategorizedLabel);
            var selected = FindMovieCategory(preferredId) ?? FindMovieCategory(PlaylistRepository.CategoryAll)!;

            _suppressCategoryChange = true;
            CategoryList.ItemsSource = _movieCategories;
            CategoryList.SelectedItem = selected;
            _suppressCategoryChange = false;
            CategoryList.ScrollIntoView(selected);
            return selected;
        }

        private void ShowMovieCategory(Category category)
        {
            var list = BuildMovieItems(category.Id);
            _visibleMovieItems = list;
            _currentMovieCategoryId = category.Id;
            ChannelList.ItemTemplate = (DataTemplate)Resources["ChannelTemplate"];
            ChannelList.ItemsSource = list;
            ChannelHeader.Text = $"{category.Label.ToUpperInvariant()}  ·  {list.Count:N0}";
            ShowsBackButton.Visibility = Visibility.Collapsed;

            var index = _playingItem != null ? list.IndexOf(_playingItem) : -1;
            if (index < 0 && list.Count > 0) index = 0;
            if (index >= 0)
            {
                ChannelList.SelectedIndex = index;
                ChannelList.ScrollIntoView(list[index]);
            }
        }

        // ---- TV Shows tab: show grid, then (once one is opened) its episode list -------------------

        private Category? FindShowCategory(string id) => _showCategories.FirstOrDefault(c => c.Id == id);

        private Category RebuildShowCategories(string preferredId)
        {
            _showCategories = _repo.ShowCategories(AllChannelsLabel, UncategorizedLabel);
            var selected = FindShowCategory(preferredId) ?? FindShowCategory(PlaylistRepository.CategoryAll)!;

            _suppressCategoryChange = true;
            CategoryList.ItemsSource = _showCategories;
            CategoryList.SelectedItem = selected;
            _suppressCategoryChange = false;
            CategoryList.ScrollIntoView(selected);
            return selected;
        }

        private List<ShowItem> BuildShowItems(string categoryId)
        {
            var shows = _repo.ShowsForCategory(categoryId);
            var list = new List<ShowItem>(shows.Count);
            foreach (var show in shows) list.Add(_showItems[show]);
            return list;
        }

        /// <summary>Shows the grid of shows for <paramref name="category"/> — the TV Shows tab's default
        /// view, and where the back button/Escape return to from an open show's episode list.</summary>
        private void ShowShowCategory(Category category)
        {
            _episodesLoadCts?.Cancel();
            _openShow = null;

            var list = BuildShowItems(category.Id);
            _visibleShows = list;
            _currentShowCategoryId = category.Id;
            ChannelList.ItemTemplate = (DataTemplate)Resources["ShowTemplate"];
            ChannelList.ItemsSource = list;
            ChannelHeader.Text = $"{category.Label.ToUpperInvariant()}  ·  {list.Count:N0}";
            ShowsBackButton.Visibility = Visibility.Collapsed;
            if (list.Count > 0) ChannelList.SelectedIndex = 0;
        }

        /// <summary>True while the TV Shows tab is showing the show grid itself (as opposed to one show's
        /// episode list) — i.e. whether ChannelList currently holds ShowItem rows rather than ChannelItem ones.</summary>
        private bool IsBrowsingShows() => _section == Section.Shows && _openShow == null;

        /// <summary>Drills into one show's episode list — fetched lazily from Xtream (get_series_info) the
        /// first time a real Xtream series is opened, or filtered instantly from already-loaded channels
        /// for an M3U-synthesized show. Reuses ChannelList/ChannelTemplate, exactly like Movies.</summary>
        private async void OpenShow(ShowSummary show)
        {
            _episodesLoadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _episodesLoadCts = cts;

            _openShow = show;
            ShowsBackButton.Visibility = Visibility.Visible;
            ChannelList.ItemTemplate = (DataTemplate)Resources["ChannelTemplate"];
            ChannelHeader.Text = show.Name.ToUpperInvariant();
            ChannelList.ItemsSource = null;
            _openShowEpisodes = Array.Empty<ChannelItem>();

            IReadOnlyList<Channel> episodes;
            if (show.IsXtreamSeries)
            {
                if (SettingsStore.ActivePlaylist()?.Source is not { Type: SourceType.Xtream } source)
                {
                    return; // shouldn't happen: an Xtream-native show implies the active playlist is Xtream
                }

                StatusText.Text = Strings.LoadingEpisodes;
                try
                {
                    var client = new XtreamClient(source.Server ?? "", source.Username ?? "", source.Password ?? "");
                    var raw = await client.FetchSeriesEpisodesAsync(show.Id, cts.Token);
                    if (cts.IsCancellationRequested) return;
                    episodes = PlaylistRepository.SortEpisodes(raw);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (cts.IsCancellationRequested) return;
                    StatusText.Text = $"{Strings.CouldntLoadEpisodes} {ErrorText.Describe(ex)}";
                    episodes = Array.Empty<Channel>();
                }
                finally
                {
                    if (_episodesLoadCts == cts && StatusText.Text == Strings.LoadingEpisodes) StatusText.Text = "";
                }
            }
            else
            {
                episodes = _repo.EpisodesForLocalShow(show);
            }

            if (cts.IsCancellationRequested) return;

            var items = new List<ChannelItem>(episodes.Count);
            foreach (var episode in episodes)
            {
                var label = episode.SeasonNumber != null && episode.EpisodeNumber != null
                    ? $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}"
                    : "";
                items.Add(new ChannelItem(episode, 0, string.IsNullOrEmpty(label) ? null : label));
            }
            _openShowEpisodes = items;
            ChannelList.ItemsSource = items;
            ChannelHeader.Text = $"{show.Name.ToUpperInvariant()}  ·  {items.Count:N0}";
            if (items.Count > 0) ChannelList.SelectedIndex = 0;
        }

        private void ShowsBackButton_Click(object sender, RoutedEventArgs e) => BackToShowsGrid();

        /// <summary>Back from one show's episode list to the show grid it was opened from.</summary>
        private void BackToShowsGrid()
        {
            if (_section != Section.Shows || _openShow == null) return;
            ShowShowCategory(FindShowCategory(_currentShowCategoryId) ?? FindShowCategory(PlaylistRepository.CategoryAll)!);
        }

        /// <summary>Where a played item and its zap/next-episode context come from, for whichever
        /// section/view is currently on screen — see ActivateChannel.</summary>
        private (IReadOnlyList<ChannelItem> Items, string CategoryId) CurrentPlaybackContext() => _section switch
        {
            Section.Movies => (_visibleMovieItems, _currentMovieCategoryId),
            Section.Shows => (_openShowEpisodes, _openShow?.Id ?? PlaylistRepository.CategoryAll),
            _ => (_visibleItems, _currentCategoryId)
        };

        /// <summary>The data item of the row an event happened on, or null (scrollbar, empty space…).</summary>
        private static T? ItemFromEvent<T>(ItemsControl list, RoutedEventArgs e) where T : class =>
            e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(list, source) is FrameworkElement { DataContext: T item }
                ? item
                : null;

        private void ChannelList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (IsBrowsingShows())
            {
                if (ItemFromEvent<ShowItem>(ChannelList, e) is { } showItem) OpenShow(showItem.Show);
                return;
            }
            if (ItemFromEvent<ChannelItem>(ChannelList, e) is { } item) ActivateChannel(item);
        }

        private void ChannelList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            if (IsBrowsingShows())
            {
                if (ChannelList.SelectedItem is ShowItem showItem) OpenShow(showItem.Show);
                e.Handled = true;
                return;
            }
            if (ChannelList.SelectedItem is ChannelItem item)
            {
                ActivateChannel(item);
                e.Handled = true;
            }
        }

        /// <summary>Same as Android: first click/Enter previews, again on the playing channel goes fullscreen.
        /// Used for Live channels, Movies, and (once a show is opened) its episodes alike — see
        /// CurrentPlaybackContext for which list/category each plays from and zaps through.</summary>
        private void ActivateChannel(ChannelItem item)
        {
            if (IsPlayingOk(item))
            {
                EnterFullscreen();
                return;
            }
            var (items, categoryId) = CurrentPlaybackContext();
            PlayItem(item, items, categoryId);
        }

        /// <summary>True if <paramref name="item"/> is the current channel and hasn't failed.</summary>
        private bool IsPlayingOk(ChannelItem item) =>
            _player.CurrentChannel?.Key == item.Channel.Key && _player.State != PlaybackState.Failed;

        /// <summary>Plays a channel from outside this window (the EPG guide) and jumps straight to fullscreen.</summary>
        public void PlayChannelAndFullscreen(Channel channel)
        {
            var item = _items.Values.FirstOrDefault(i => i.Channel.Key == channel.Key);
            if (item == null) return;

            var inCurrentCategory = _visibleItems.Contains(item);
            var context = inCurrentCategory ? _visibleItems : BuildItems(PlaylistRepository.CategoryAll);
            var categoryId = inCurrentCategory ? _currentCategoryId : PlaylistRepository.CategoryAll;

            PlayItem(item, context, categoryId);
            WindowState = WindowState.Normal; // in case this window was minimized
            Activate();
            EnterFullscreen();
        }

        private void PlayItem(ChannelItem item, IReadOnlyList<ChannelItem> context, string categoryId)
        {
            if (_playingItem != null) _playingItem.IsPlaying = false;
            _playingItem = item;
            item.IsPlaying = true;
            _playbackItems = context;
            _playbackCategoryId = categoryId;
            // "Resume on startup" only ever resumes a Live channel (see ApplyPlaylist) — don't let a
            // Movie/episode overwrite it, so it still points at the last Live channel watched.
            if (item.Channel.ContentType == ContentType.Live) SettingsStore.Current.LastChannelKey = item.Channel.Key;
            _player.Play(item.Channel);
            UpdateNowPlaying();
        }

        /// <summary>Plays an Xtream catch-up (timeshift) recording of <paramref name="programme"/> from
        /// outside this window (the TV Guide) and jumps straight to fullscreen — the catch-up counterpart
        /// of <see cref="PlayChannelAndFullscreen"/>. Silently does nothing if catch-up turns out not to be
        /// available any more (e.g. the active playlist changed between opening the guide and clicking).</summary>
        public void PlayChannelCatchupAndFullscreen(Channel channel, EpgProgramme programme)
        {
            var activeSource = SettingsStore.ActivePlaylist()?.Source;
            var url = _repo.BuildCatchupUrl(channel, programme, activeSource);
            if (url == null) return;

            var item = _items.Values.FirstOrDefault(i => i.Channel.Key == channel.Key);
            if (item == null) return;

            var inCurrentCategory = _visibleItems.Contains(item);
            var context = inCurrentCategory ? _visibleItems : BuildItems(PlaylistRepository.CategoryAll);
            var categoryId = inCurrentCategory ? _currentCategoryId : PlaylistRepository.CategoryAll;

            if (_playingItem != null) _playingItem.IsPlaying = false;
            _playingItem = item;
            item.IsPlaying = true;
            _playbackItems = context;
            _playbackCategoryId = categoryId;

            _player.PlayCatchup(channel, programme, url);
            UpdateNowPlaying();

            WindowState = WindowState.Normal; // in case this window was minimized
            Activate();
            EnterFullscreen();
        }

        /// <summary>Switches from catch-up back to the same channel's live stream. No-op if not currently watching catch-up.</summary>
        private void BackToLive()
        {
            if (_player.CatchupProgramme == null) return;
            _player.ReturnToLive();
            ShowOsd(Strings.BackToLiveButton);
            UpdateNowPlaying();
        }

        private void BackToLiveButton_Click(object sender, RoutedEventArgs e) => BackToLive();

        private static bool TryDigit(Key key, out int digit)
        {
            if (key >= Key.D0 && key <= Key.D9) { digit = key - Key.D0; return true; }
            if (key >= Key.NumPad0 && key <= Key.NumPad9) { digit = key - Key.NumPad0; return true; }
            digit = 0;
            return false;
        }

        /// <summary>Jumps to whatever channel has the typed Number (the same number shown next to every
        /// channel in both lists and the fullscreen info bar) — from the keyboard, no mouse needed, like
        /// typing a channel number on a remote. Goes straight to fullscreen if not there already; just
        /// switches channel in place if already fullscreen, same as zapping with the arrow keys.</summary>
        private void CommitChannelEntry()
        {
            if (_channelEntryBuffer.Length == 0) return;
            var number = int.Parse(_channelEntryBuffer);
            _channelEntryBuffer = "";

            var match = _items.Values.FirstOrDefault(i => i.Number == number);
            if (match == null)
            {
                ShowOsd(string.Format(Strings.ChannelNotFoundFormat, number));
                return;
            }

            if (_isFullscreen)
            {
                CloseSidePanel();
                var inCurrentCategory = _visibleItems.Contains(match);
                PlayItem(match, inCurrentCategory ? _visibleItems : BuildItems(PlaylistRepository.CategoryAll),
                    inCurrentCategory ? _currentCategoryId : PlaylistRepository.CategoryAll);
                ShowInfoBar();
            }
            else
            {
                PlayChannelAndFullscreen(match.Channel);
            }
        }

        /// <summary>Next/previous channel (wrapping) in the list the current channel was played from.</summary>
        private void Zap(int delta)
        {
            if (_playingItem == null) return;
            var list = _playbackItems.Count > 0 ? _playbackItems : _visibleItems;
            var index = IndexOf(list, _playingItem);
            if (index < 0 || list.Count < 2) return;

            var next = ((index + delta) % list.Count + list.Count) % list.Count;
            PlayItem(list[next], list, _playbackCategoryId);

            if (ReferenceEquals(list, _visibleItems))
            {
                ChannelList.SelectedIndex = next;
                ChannelList.ScrollIntoView(list[next]);
            }
            ShowInfoBar();
        }

        private static int IndexOf(IReadOnlyList<ChannelItem> list, ChannelItem item)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], item)) return i;
            }
            return -1;
        }

        private static string GroupLabel(Channel channel) =>
            channel.GroupTitle == Channel.UngroupedGroupKey ? UncategorizedLabel : channel.GroupTitle;

        private void UpdateNowPlaying()
        {
            var channel = _playingItem?.Channel;
            NowPlayingName.Text = channel?.Name ?? " ";
            NowPlayingGroup.Text = channel == null ? "" : GroupLabel(channel);

            InfoBar.DataContext = _playingItem; // logo binding
            InfoNumber.Text = _playingItem?.Number.ToString() ?? "";
            InfoName.Text = channel?.Name ?? "";
            InfoStar.Text = _playingItem?.IsFavorite == true ? "  ★" : "";
            InfoGroup.Text = channel == null ? "" : GroupLabel(channel);

            UpdateEpgPanel();
        }

        /// <summary>Fills in "Playing now / next / after next" for whatever's currently playing. The first line
        /// is only labeled "Playing now" when a programme genuinely spans the current moment — if the channel's
        /// guide has a gap right now, the lines shift up rather than mislabeling an upcoming show as playing.</summary>
        /// <summary>Fills in up to three upcoming programmes for whatever's currently playing — nearest first —
        /// in both the main window's now-playing panel and the fullscreen info bar. No "now/next" labels: each
        /// line is just its own air time and title, brightest-to-dimmest so the nearest one stands out.</summary>
        private void UpdateEpgPanel()
        {
            var mainTexts = new[] { EpgNowText, EpgNextText, EpgAfterText };
            var fullscreenTexts = new[] { InfoEpgNowText, InfoEpgNextText, InfoEpgAfterText };

            IReadOnlyList<EpgProgramme> upcoming = Array.Empty<EpgProgramme>();
            if (_playingItem != null)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                upcoming = _epg.UpcomingFrom(_playingItem.Channel, now, 3);
            }

            void Apply(TextBlock[] texts)
            {
                for (var k = 0; k < texts.Length; k++)
                {
                    if (k >= upcoming.Count)
                    {
                        texts[k].Visibility = Visibility.Collapsed;
                        continue;
                    }
                    var p = upcoming[k];
                    var start = DateTimeOffset.FromUnixTimeMilliseconds(p.StartMillis).ToLocalTime();
                    var stop = DateTimeOffset.FromUnixTimeMilliseconds(p.StopMillis).ToLocalTime();
                    texts[k].Text = $"{start:HH:mm} - {stop:HH:mm} : {p.Title}";
                    texts[k].Visibility = Visibility.Visible;
                }
            }

            Apply(mainTexts);
            Apply(fullscreenTexts);
        }

        private void UpdatePlayerStatus()
        {
            if (_player.CurrentChannel == null && _playingItem != null)
            {
                _playingItem.IsPlaying = false;
                _playingItem = null;
                UpdateNowPlaying();
            }

            IdleHint.Visibility = _player.CurrentChannel == null ? Visibility.Visible : Visibility.Collapsed;

            var retryHint = _isFullscreen ? Strings.RetryHintFullscreen : Strings.RetryHintWindowed;
            string? text = _player.State switch
            {
                PlaybackState.Connecting => Strings.Connecting,
                PlaybackState.Reconnecting => Strings.Reconnecting,
                PlaybackState.Failed => $"{_player.LastError ?? Strings.ErrorCouldntPlayChannel}\n{retryHint}",
                _ => null
            };
            StatusLabel.Text = text ?? "";
            StatusBadge.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;

            UpdateCatchupBadge();
            UpdateVodControls();
        }

        /// <summary>Shows a persistent "Watching catch-up: <i>title</i> — Back to Live" badge whenever
        /// catch-up playback is active (windowed or fullscreen — the overlay is shared between both).</summary>
        private void UpdateCatchupBadge()
        {
            var programme = _player.CatchupProgramme;
            if (programme == null)
            {
                CatchupBadge.Visibility = Visibility.Collapsed;
                return;
            }
            CatchupLabel.Text = string.Format(Strings.WatchingCatchupFormat, programme.Title);
            CatchupBadge.Visibility = Visibility.Visible;
        }

        // ---- VOD (Movie/episode) transport controls: play/pause + seek bar -----------------------
        // Live TV never shows these (a live stream has no duration/position to seek, and typically
        // can't be paused either) — see PlayerService.CanPause/CanSeek, which reflect that directly.

        private const string PlayGlyph = "";
        private const string PauseGlyph = "";

        /// <summary>Refreshes the play/pause button + seek bar (windowed and fullscreen alike) for
        /// whatever's currently playing. Called from the player's StateChanged (covers play/pause/stop
        /// instantly) and a short polling timer (advances the seek bar's position while playing).</summary>
        private void UpdateVodControls()
        {
            var isVod = _player.CurrentChannel is { ContentType: not ContentType.Live } &&
                        _player.State == PlaybackState.Playing;

            VodControlRow.Visibility = isVod && !_isFullscreen ? Visibility.Visible : Visibility.Collapsed;
            VodControlBar.Visibility = isVod && _isFullscreen ? Visibility.Visible : Visibility.Collapsed;
            if (!isVod) return;

            var canPause = _player.CanPause;
            var canSeek = _player.CanSeek;
            var duration = _player.DurationMillis;
            var position = _player.PositionMillis;
            var glyph = _player.IsPaused ? PlayGlyph : PauseGlyph;

            PlayPauseButton.Content = glyph;
            PlayPauseButton.IsEnabled = canPause;
            FullscreenPlayPauseButton.Content = glyph;
            FullscreenPlayPauseButton.IsEnabled = canPause;

            UpdateSeekBar(SeekSlider, SeekTimeText, canSeek, duration, position);
            UpdateSeekBar(FullscreenSeekSlider, FullscreenSeekTimeText, canSeek, duration, position);
        }

        private void UpdateSeekBar(Slider slider, TextBlock timeText, bool canSeek, long durationMillis, long positionMillis)
        {
            slider.IsEnabled = canSeek && durationMillis > 0;
            if (durationMillis > 0)
            {
                slider.Maximum = durationMillis;
                if (!_seekDragging) slider.Value = Math.Clamp(positionMillis, 0, durationMillis);
                timeText.Text = $"{FormatTime(positionMillis)} / {FormatTime(durationMillis)}";
            }
            else
            {
                // Duration not known yet (LibVLC hasn't parsed it, or never will for this stream) — still
                // show elapsed time by itself rather than a dead "0:00 / 0:00".
                slider.Maximum = 0;
                if (!_seekDragging) slider.Value = 0;
                timeText.Text = positionMillis > 0 ? FormatTime(positionMillis) : "";
            }
        }

        private static string FormatTime(long millis)
        {
            var span = TimeSpan.FromMilliseconds(Math.Max(0, millis));
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes}:{span.Seconds:00}";
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _player.TogglePause();

        /// <summary>Seeks by a relative amount (e.g. the Left/Right-arrow nudge in fullscreen — see
        /// Window_PreviewKeyDown). No-op if the current stream isn't seekable or its position isn't known yet.</summary>
        private void SeekRelative(long deltaMillis)
        {
            var position = _player.PositionMillis;
            if (!_player.CanSeek || position < 0) return;
            _player.SeekTo(position + deltaMillis);
        }

        private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _seekDragging = true;

        private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _seekDragging = false;
            if (sender is Slider slider) _player.SeekTo((long)slider.Value);
        }

        /// <summary>Arrow/Page/Home/End keys move a focused Slider's Value on their own — this commits that
        /// new position once the key is released, the same way SeekSlider_PreviewMouseLeftButtonUp commits
        /// a drag, rather than seeking on every intermediate step while a key is held.</summary>
        private void SeekSlider_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (sender is not Slider slider) return;
            if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            {
                _player.SeekTo((long)slider.Value);
            }
        }

        // ---- Favorites & channel menu ------------------------------------------------------------

        private void ToggleFavorite(ChannelItem item)
        {
            var nowFavorite = FavoritesManager.Toggle(item.Channel.Key);

            // Every row with this exact stream URL (duplicates in a playlist are possible).
            foreach (var other in _items.Values)
            {
                if (other.Channel.Key == item.Channel.Key) other.IsFavorite = nowFavorite;
            }

            ShowOsd(nowFavorite ? Strings.AddedToFavorites : Strings.RemovedFromFavorites);
            UpdateNowPlaying();
            RefreshAfterFavoritesChange();
        }

        /// <summary>Favorites count/category changed: refresh sidebar counts and any list showing Favorites.</summary>
        private void RefreshAfterFavoritesChange()
        {
            var listHadFocus = ChannelList.IsKeyboardFocusWithin;
            var oldIndex = ChannelList.SelectedIndex;
            var wasShowing = _currentCategoryId;

            var selected = RebuildCategories(wasShowing);
            if (wasShowing == PlaylistRepository.CategoryFavorites || selected.Id != wasShowing)
            {
                ShowCategory(selected, selected.Id == wasShowing ? oldIndex : null);
                if (listHadFocus) Dispatcher.InvokeAsync(() => FocusSelected(ChannelList), DispatcherPriority.Loaded);
            }

            if (_sideMode != SideMode.None)
            {
                if (_sideMode == SideMode.Categories) SideCategoryList.ItemsSource = _categories;
                if (_sideCategoryId == PlaylistRepository.CategoryFavorites || FindCategory(_sideCategoryId) == null)
                {
                    LoadSideChannels(selectPlaying: false, preferredIndex: SideChannelList.SelectedIndex);
                }
            }
        }

        private void ChannelList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            // No per-row menu for the show grid itself (no favorites/EPG/stream-URL to offer there);
            // only its episode list, opened via OpenShow, gets the normal channel-row menu below.
            if (IsBrowsingShows())
            {
                e.Handled = true;
                return;
            }

            var item = ItemFromEvent<ChannelItem>(ChannelList, e);
            var menu = ChannelList.ContextMenu;
            if (item == null || menu == null)
            {
                e.Handled = true; // right-click on empty space / scrollbar: no menu
                return;
            }

            ChannelList.SelectedItem = item;
            menu.Items.Clear();
            menu.Items.Add(MakeMenuItem(IsPlayingOk(item) ? Strings.WatchFullscreenMenuItem : Strings.PlayMenuItem, "Enter",
                () => ActivateChannel(item)));

            // Favorites, EPG mapping, and catch-up are Live-TV-only concepts (see PlaylistRepository's
            // own "favoriting is Live-channel-only for now" note) — a Movie/episode row only offers
            // Play and Copy stream URL.
            if (item.Channel.ContentType == ContentType.Live)
            {
                menu.Items.Add(MakeMenuItem(item.IsFavorite ? Strings.RemoveFromFavoritesMenuItem : Strings.AddToFavoritesMenuItem, "Ctrl+D",
                    () => ToggleFavorite(item)));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem(Strings.AssignChannelEpgMenuItem, null, () => OpenEpgMapping(item)));
            }
            else
            {
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(MakeMenuItem(Strings.CopyStreamUrlMenuItem, null, () => CopyStreamUrl(item)));
        }

        private void OpenEpgMapping(ChannelItem item)
        {
            new EpgMappingWindow(item.Channel) { Owner = this }.ShowDialog();
        }

        /// <summary>"Treat category as Live TV / Movies / TV Shows" — a manual correction for
        /// ContentClassifier's M3U guess when it gets a whole category wrong (see
        /// PlaylistRepository.SetCategoryOverride), e.g. a live channel bouquet whose category is named
        /// for the genre it shows rather than for being on-demand. Only offered for M3U playlists —
        /// Xtream tells Live/Movies/Shows apart itself, so overriding one of its categories would be a
        /// no-op — and never for the synthetic Favorites/All-channels rows, which aren't real categories.
        /// Shared across CategoryList regardless of which section tab is currently showing it, since the
        /// category id is the playlist's own raw group-title either way.</summary>
        private void CategoryList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var category = ItemFromEvent<Category>(CategoryList, e);
            var menu = CategoryList.ContextMenu;
            var activeSource = SettingsStore.ActivePlaylist()?.Source;
            if (category == null || menu == null || activeSource == null || activeSource.Type == SourceType.Xtream ||
                category.Id is PlaylistRepository.CategoryAll or PlaylistRepository.CategoryFavorites)
            {
                e.Handled = true;
                return;
            }

            CategoryList.SelectedItem = category;
            menu.Items.Clear();

            var current = PlaylistRepository.CategoryOverride(category.Id);
            var treatAsMenu = new MenuItem { Header = Strings.TreatCategoryAsMenuItem };
            treatAsMenu.Items.Add(MakeCheckableMenuItem(Strings.CategoryAutomaticMenuItem, current == null,
                () => SetCategoryOverride(category.Id, null)));
            treatAsMenu.Items.Add(new Separator());
            treatAsMenu.Items.Add(MakeCheckableMenuItem(Strings.SectionLive, current == ContentType.Live,
                () => SetCategoryOverride(category.Id, ContentType.Live)));
            treatAsMenu.Items.Add(MakeCheckableMenuItem(Strings.SectionMovies, current == ContentType.Movie,
                () => SetCategoryOverride(category.Id, ContentType.Movie)));
            treatAsMenu.Items.Add(MakeCheckableMenuItem(Strings.SectionShows, current == ContentType.Series,
                () => SetCategoryOverride(category.Id, ContentType.Series)));
            menu.Items.Add(treatAsMenu);
        }

        private void SetCategoryOverride(string categoryId, ContentType? type)
        {
            _repo.SetCategoryOverride(categoryId, type);
            RefreshAfterCategoryOverrideChange();
        }

        /// <summary>Re-renders the sidebar/channel list from the repository's already-current state — call
        /// after PlaylistRepository.SetCategoryOverride, whether triggered from here (the category
        /// right-click menu) or from the Settings window's category-overrides list.</summary>
        public void RefreshAfterCategoryOverrideChange() => ApplyPlaylist(_repo.Snapshot());

        private static MenuItem MakeMenuItem(string header, string? gesture, Action action)
        {
            var menuItem = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            menuItem.Click += (_, _) => action();
            return menuItem;
        }

        private static MenuItem MakeCheckableMenuItem(string header, bool isChecked, Action action)
        {
            var menuItem = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
            menuItem.Click += (_, _) => action();
            return menuItem;
        }

        /// <summary>Applies <see cref="_videoRatioMode"/> to the player right now. Fill is recomputed from
        /// the video area's current on-screen size every time (windowed size, or the full screen once
        /// fullscreen), so it always stretches to exactly what's currently visible.</summary>
        private void ApplyVideoRatioMode()
        {
            string? ratio = _videoRatioMode switch
            {
                VideoRatioMode.Fill => FillAspectRatioString(),
                VideoRatioMode.Ratio4_3 => "4:3",
                VideoRatioMode.Ratio16_9 => "16:9",
                _ => null
            };
            _player.SetAspectRatio(ratio);
        }

        /// <summary>The video area's current pixel size as a LibVLC-style "W:H" ratio string, or null if it
        /// hasn't been laid out yet (falls back to the default fit until the next size change).</summary>
        private string? FillAspectRatioString()
        {
            var width = (int)Math.Round(VideoFrame.ActualWidth);
            var height = (int)Math.Round(VideoFrame.ActualHeight);
            return width > 0 && height > 0 ? $"{width}:{height}" : null;
        }

        private void SetVideoRatioMode(VideoRatioMode mode)
        {
            _videoRatioMode = mode;
            SettingsStore.Current.VideoRatioMode = mode;
            SettingsStore.Save();
            ApplyVideoRatioMode();
        }

        /// <summary>Right-click on the bare video: video ratio, audio track, subtitles — the latter two only
        /// while something's actually playing (LibVLC only knows the track list once a stream has started).</summary>
        private void Overlay_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var menu = Overlay.ContextMenu;
            if (menu == null || _player.CurrentChannel == null || _player.State != PlaybackState.Playing)
            {
                e.Handled = true; // nothing playing — no video, no tracks, nothing to offer
                return;
            }

            menu.Items.Clear();

            var ratioMenu = new MenuItem { Header = Strings.VideoRatioMenuItem };
            ratioMenu.Items.Add(MakeCheckableMenuItem(Strings.VideoRatioFit, _videoRatioMode == VideoRatioMode.Fit,
                () => SetVideoRatioMode(VideoRatioMode.Fit)));
            ratioMenu.Items.Add(MakeCheckableMenuItem(Strings.VideoRatioFill, _videoRatioMode == VideoRatioMode.Fill,
                () => SetVideoRatioMode(VideoRatioMode.Fill)));
            ratioMenu.Items.Add(MakeCheckableMenuItem(Strings.VideoRatio4x3, _videoRatioMode == VideoRatioMode.Ratio4_3,
                () => SetVideoRatioMode(VideoRatioMode.Ratio4_3)));
            ratioMenu.Items.Add(MakeCheckableMenuItem(Strings.VideoRatio16x9, _videoRatioMode == VideoRatioMode.Ratio16_9,
                () => SetVideoRatioMode(VideoRatioMode.Ratio16_9)));
            menu.Items.Add(ratioMenu);
            menu.Items.Add(new Separator());

            var audioMenu = new MenuItem { Header = Strings.AudioTrackMenuItem };
            var audioTracks = _player.AudioTracks;
            if (audioTracks.Count == 0)
            {
                audioMenu.Items.Add(new MenuItem { Header = Strings.NoTracksAvailable, IsEnabled = false });
            }
            else
            {
                var currentAudio = _player.CurrentAudioTrack;
                foreach (var track in audioTracks)
                {
                    audioMenu.Items.Add(MakeCheckableMenuItem(track.Name, track.Id == currentAudio,
                        () => _player.SetAudioTrack(track.Id)));
                }
            }
            menu.Items.Add(audioMenu);

            var subtitlesMenu = new MenuItem { Header = Strings.SubtitlesMenuItem };
            var currentSubtitle = _player.CurrentSubtitleTrack;
            subtitlesMenu.Items.Add(MakeCheckableMenuItem(Strings.SubtitlesOffMenuItem, currentSubtitle == -1,
                () => _player.SetSubtitleTrack(-1)));
            var subtitleTracks = _player.SubtitleTracks;
            if (subtitleTracks.Count > 0)
            {
                subtitlesMenu.Items.Add(new Separator());
                foreach (var track in subtitleTracks)
                {
                    subtitlesMenu.Items.Add(MakeCheckableMenuItem(track.Name, track.Id == currentSubtitle,
                        () => _player.SetSubtitleTrack(track.Id)));
                }
            }
            menu.Items.Add(subtitlesMenu);
        }

        private void CopyStreamUrl(ChannelItem item)
        {
            try
            {
                // The address that actually plays (http:// if the playlist's https:// was mislabelled).
                Clipboard.SetText(Http.PreferredUrl(item.Channel.StreamUrl));
                ShowOsd(Strings.StreamUrlCopied);
            }
            catch (Exception)
            {
                ShowOsd(Strings.CouldntCopy);
            }
        }

        /// <summary>Which channel Ctrl+D applies to right now — favoriting is Live-TV-only (see
        /// PlaylistRepository.MovieCategories' doc comment), so this is null on the Movies/TV Shows tabs.</summary>
        private ChannelItem? FavoriteTarget()
        {
            if (_section != Section.Live) return null;
            if (_isFullscreen)
            {
                return _sideMode == SideMode.Channels && SideChannelList.SelectedItem is ChannelItem side
                    ? side
                    : _playingItem;
            }
            if (ChannelList.IsKeyboardFocusWithin && ChannelList.SelectedItem is ChannelItem selected) return selected;
            return _playingItem ?? ChannelList.SelectedItem as ChannelItem;
        }

        // ---- Fullscreen --------------------------------------------------------------------------

        private void ToggleFullscreen()
        {
            if (_isFullscreen) ExitFullscreen();
            else EnterFullscreen();
        }

        private void EnterFullscreen()
        {
            if (_isFullscreen || _playingItem == null) return;
            _isFullscreen = true;

            _restoreWindowState = WindowState;
            _restoreWindowStyle = WindowStyle;
            _restoreResizeMode = ResizeMode;

            TopBar.Visibility = Visibility.Collapsed;
            CategoryPanel.Visibility = Visibility.Collapsed;
            ChannelPanel.Visibility = Visibility.Collapsed;
            NowPlayingPanel.Visibility = Visibility.Collapsed;
            PlayerArea.Margin = new Thickness(0);

            // Borderless + maximized covers the taskbar. It only does so when the maximize happens
            // AFTER the border is removed, hence the detour through Normal if already maximized.
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            Activate();

            UpdatePlayerStatus(); // also refreshes the VOD transport controls' windowed/fullscreen visibility
            ShowInfoBar();
            Restart(_cursorTimer);
        }

        private void ExitFullscreen()
        {
            if (!_isFullscreen) return;
            CloseSidePanel();
            _isFullscreen = false;

            _infoBarTimer.Stop();
            _cursorTimer.Stop();
            InfoBar.Visibility = Visibility.Collapsed;
            Overlay.Cursor = null;

            WindowState = WindowState.Normal;
            WindowStyle = _restoreWindowStyle;
            ResizeMode = _restoreResizeMode;
            WindowState = _restoreWindowState;

            TopBar.Visibility = Visibility.Visible;
            CategoryPanel.Visibility = Visibility.Visible;
            ChannelPanel.Visibility = Visibility.Visible;
            NowPlayingPanel.Visibility = Visibility.Visible;
            PlayerArea.Margin = new Thickness(20);

            UpdatePlayerStatus();

            // Land back on the channel that's playing (it may have changed while zapping or choosing
            // from the side panel — possibly in another category). The side panel (the only thing that
            // can jump to a different CATEGORY while fullscreen) is Live-only — see OpenSidePanel — so
            // Movies/Shows never need the category-relocation step below, only the selection re-sync.
            if (_playingItem != null && _section == Section.Live)
            {
                if (IndexOf(_visibleItems, _playingItem) < 0 && FindCategory(_playbackCategoryId) is { } category)
                {
                    _suppressCategoryChange = true;
                    CategoryList.SelectedItem = category;
                    _suppressCategoryChange = false;
                    CategoryList.ScrollIntoView(category);
                    ShowCategory(category);
                }
                var index = IndexOf(_visibleItems, _playingItem);
                if (index >= 0)
                {
                    ChannelList.SelectedIndex = index;
                    ChannelList.ScrollIntoView(_visibleItems[index]);
                }
            }
            else if (_playingItem != null && _section == Section.Movies)
            {
                var index = IndexOf(_visibleMovieItems, _playingItem);
                if (index >= 0)
                {
                    ChannelList.SelectedIndex = index;
                    ChannelList.ScrollIntoView(_visibleMovieItems[index]);
                }
            }
            else if (_playingItem != null && _section == Section.Shows && !IsBrowsingShows())
            {
                var index = IndexOf(_openShowEpisodes, _playingItem);
                if (index >= 0)
                {
                    ChannelList.SelectedIndex = index;
                    ChannelList.ScrollIntoView(_openShowEpisodes[index]);
                }
            }
            Dispatcher.InvokeAsync(() => FocusSelected(ChannelList), DispatcherPriority.Loaded);
        }

        private void ShowInfoBar()
        {
            if (!_isFullscreen || _playingItem == null || _sideMode != SideMode.None) return;
            // VOD shows its own persistent transport bar instead (see UpdateVodControls) — no toast to flash.
            if (_playingItem.Channel.ContentType != ContentType.Live) return;
            InfoBar.Visibility = Visibility.Visible;
            Restart(_infoBarTimer);
        }

        // ---- Fullscreen side panel (channels, then categories) -----------------------------------

        private void OpenSidePanel()
        {
            if (!_isFullscreen || _sideMode != SideMode.None) return;
            // v1: the quick-switch side panel only knows Live channels/categories — a Movie or episode
            // currently playing just doesn't offer it (there's nothing stopping playback here, though).
            if (_playingItem != null && _playingItem.Channel.ContentType != ContentType.Live) return;

            _sideCategoryId = FindCategory(_playbackCategoryId) != null ? _playbackCategoryId : _currentCategoryId;
            LoadSideChannels(selectPlaying: true);

            _infoBarTimer.Stop();
            InfoBar.Visibility = Visibility.Collapsed;
            SideCategoryColumn.Visibility = Visibility.Collapsed;
            SidePanel.Visibility = Visibility.Visible;
            _sideMode = SideMode.Channels;
            Restart(_sidePanelTimer);
        }

        private void OpenSideCategories()
        {
            SideCategoryList.ItemsSource = _categories;
            var category = FindCategory(_sideCategoryId);
            SideCategoryList.SelectedItem = category;
            if (category != null) SideCategoryList.ScrollIntoView(category);
            SideCategoryColumn.Visibility = Visibility.Visible;
            _sideMode = SideMode.Categories;
        }

        /// <summary>Back from categories to the channel column (keeping whatever category was chosen).</summary>
        private void CloseSideCategories()
        {
            SideCategoryColumn.Visibility = Visibility.Collapsed;
            _sideMode = SideMode.Channels;
        }

        private void CloseSidePanel()
        {
            _sidePanelTimer.Stop();
            SidePanel.Visibility = Visibility.Collapsed;
            SideCategoryColumn.Visibility = Visibility.Collapsed;
            _sideMode = SideMode.None;
        }

        /// <summary>Fills the side channel column with <see cref="_sideCategoryId"/>'s channels.</summary>
        private void LoadSideChannels(bool selectPlaying, int? preferredIndex = null)
        {
            var category = FindCategory(_sideCategoryId) ?? FindCategory(PlaylistRepository.CategoryAll)!;
            _sideCategoryId = category.Id;
            _sideItems = BuildItems(category.Id);
            SideChannelList.ItemsSource = _sideItems;
            SideChannelHeader.Text = $"{category.Label.ToUpperInvariant()}  ·  {_sideItems.Count:N0}";

            var now = DateTimeOffset.Now;
            foreach (var channelItem in _sideItems) channelItem.UpdateEpg(_epg, now);

            if (_sideItems.Count == 0) return;
            var index = selectPlaying && _playingItem != null ? IndexOf(_sideItems, _playingItem) : -1;
            if (index < 0) index = Math.Clamp(preferredIndex ?? 0, 0, _sideItems.Count - 1);
            SideChannelList.SelectedIndex = index;
            SideChannelList.ScrollIntoView(_sideItems[index]);
        }

        private static void MoveSelection(ListBox list, int delta, bool wrap)
        {
            var count = list.Items.Count;
            if (count == 0) return;
            var index = list.SelectedIndex < 0 ? 0 : list.SelectedIndex + delta;
            index = wrap ? ((index % count) + count) % count : Math.Clamp(index, 0, count - 1);
            list.SelectedIndex = index;
            list.ScrollIntoView(list.Items[index]);
        }

        /// <summary>Plays a side-panel channel; Enter/click again on the one already playing closes the panel.</summary>
        private void ActivateSideChannel(ChannelItem item)
        {
            if (IsPlayingOk(item))
            {
                CloseSidePanel();
                ShowInfoBar();
                return;
            }
            PlayItem(item, _sideItems, _sideCategoryId);
        }

        private void SideChannelList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (ItemFromEvent<ChannelItem>(SideChannelList, e) is not { } item) return;
            SideChannelList.SelectedItem = item;
            if (_sideMode == SideMode.Categories) CloseSideCategories();
            ActivateSideChannel(item);
            Restart(_sidePanelTimer);
            e.Handled = true;
        }

        private void SideCategoryList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (ItemFromEvent<Category>(SideCategoryList, e) is not { } category) return;
            SideCategoryList.SelectedItem = category;
            _sideCategoryId = category.Id;
            LoadSideChannels(selectPlaying: true);
            CloseSideCategories();
            Restart(_sidePanelTimer);
            e.Handled = true;
        }

        /// <summary>Keys while the side panel is open. Returns false for keys it leaves to the global handler (volume, mute…).</summary>
        private bool HandleSidePanelKey(Key key)
        {
            Restart(_sidePanelTimer);

            if (_sideMode == SideMode.Categories)
            {
                switch (key)
                {
                    case Key.Up: MoveSideCategory(-1); return true;
                    case Key.Down: MoveSideCategory(+1); return true;
                    case Key.PageUp: MoveSideCategory(-8); return true;
                    case Key.PageDown: MoveSideCategory(+8); return true;
                    case Key.Enter:
                    case Key.Right:
                        CloseSideCategories();
                        return true;
                    case Key.Escape:
                        CloseSidePanel();
                        return true;
                    case Key.Left:
                        return true;
                }
                return false;
            }

            switch (key)
            {
                case Key.Up: MoveSelection(SideChannelList, -1, wrap: true); return true;
                case Key.Down: MoveSelection(SideChannelList, +1, wrap: true); return true;
                case Key.PageUp: MoveSelection(SideChannelList, -10, wrap: false); return true;
                case Key.PageDown: MoveSelection(SideChannelList, +10, wrap: false); return true;
                case Key.Home: MoveSelection(SideChannelList, -SideChannelList.Items.Count, wrap: false); return true;
                case Key.End: MoveSelection(SideChannelList, +SideChannelList.Items.Count, wrap: false); return true;
                case Key.Enter:
                case Key.Space:
                    if (SideChannelList.SelectedItem is ChannelItem item) ActivateSideChannel(item);
                    return true;
                case Key.Left:
                    OpenSideCategories();
                    return true;
                case Key.Right:
                case Key.Escape:
                    CloseSidePanel();
                    return true;
            }
            return false;
        }

        /// <summary>Moving through categories previews each one's channels immediately (like Android).</summary>
        private void MoveSideCategory(int delta)
        {
            MoveSelection(SideCategoryList, delta, wrap: false);
            if (SideCategoryList.SelectedItem is Category category && category.Id != _sideCategoryId)
            {
                _sideCategoryId = category.Id;
                LoadSideChannels(selectPlaying: true);
            }
        }

        // ---- Volume & on-screen messages ---------------------------------------------------------

        private void ChangeVolume(int delta)
        {
            if (delta > 0 && _player.Muted) _player.Muted = false;
            _player.Volume += delta;
            SettingsStore.Current.Volume = _player.Volume;
            ShowOsd(string.Format(Strings.VolumeFormat, _player.Volume));
        }

        private void ToggleMute()
        {
            _player.Muted = !_player.Muted;
            ShowOsd(_player.Muted ? Strings.Muted : string.Format(Strings.VolumeFormat, _player.Volume));
        }

        private void ShowOsd(string text)
        {
            OsdLabel.Text = text;
            OsdBadge.Visibility = Visibility.Visible;
            Restart(_osdTimer);
        }

        // ---- Overlay (mouse over the video) ------------------------------------------------------

        private void Overlay_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // The overlay is a separate window; clicking it takes keyboard focus away from ours.
            Activate();
        }

        private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Only reached for clicks on the bare video (the side lists handle their own).
            if (e.ClickCount == 2 && _playingItem != null)
            {
                ToggleFullscreen();
            }
            else if (_sideMode != SideMode.None)
            {
                CloseSidePanel();
            }
            e.Handled = true;
        }

        private void Overlay_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isFullscreen) return;

            // Only real movement counts: Windows also sends move messages when things change under
            // a still cursor, which would otherwise keep the cursor and info bar from ever hiding.
            var position = e.GetPosition(Overlay);
            if (Math.Abs(position.X - _lastMousePosition.X) < 3 && Math.Abs(position.Y - _lastMousePosition.Y) < 3) return;
            _lastMousePosition = position;

            Overlay.Cursor = null;
            Restart(_cursorTimer);
            if (_sideMode != SideMode.None) Restart(_sidePanelTimer);
            else ShowInfoBar();
        }

        private void Overlay_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Wheel over the side lists scrolls them (handled there); anywhere else it's volume.
            ChangeVolume(e.Delta > 0 ? 5 : -5);
            e.Handled = true;
        }

        // ---- Keyboard ----------------------------------------------------------------------------

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox or PasswordBox) return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = Keyboard.Modifiers;
            var noModifiers = modifiers == ModifierKeys.None;

            // Direct channel entry: any digit (top row or numpad) starts/continues it, from anywhere,
            // in or out of fullscreen — it commits itself after a short pause, or right away on Enter.
            if (noModifiers && TryDigit(key, out var digit))
            {
                _channelEntryBuffer = (_channelEntryBuffer.Length >= 5 ? _channelEntryBuffer[1..] : _channelEntryBuffer) + digit;
                ShowOsd(string.Format(Strings.ChannelEntryFormat, _channelEntryBuffer));
                Restart(_channelEntryTimer);
                e.Handled = true;
                return;
            }
            if (_channelEntryBuffer.Length > 0)
            {
                if (key == Key.Enter)
                {
                    _channelEntryTimer.Stop();
                    CommitChannelEntry();
                    e.Handled = true;
                    return;
                }
                if (key == Key.Back)
                {
                    _channelEntryBuffer = _channelEntryBuffer[..^1];
                    if (_channelEntryBuffer.Length == 0)
                    {
                        _channelEntryTimer.Stop();
                        OsdBadge.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        ShowOsd(string.Format(Strings.ChannelEntryFormat, _channelEntryBuffer));
                        Restart(_channelEntryTimer);
                    }
                    e.Handled = true;
                    return;
                }
                if (key == Key.Escape)
                {
                    _channelEntryTimer.Stop();
                    _channelEntryBuffer = "";
                    OsdBadge.Visibility = Visibility.Collapsed;
                    e.Handled = true;
                    return;
                }
            }

            // Side panel first: it owns arrows/Enter/Esc while open (Esc closes it, not fullscreen).
            if (_isFullscreen && _sideMode != SideMode.None && noModifiers && HandleSidePanelKey(key))
            {
                e.Handled = true;
                return;
            }

            switch (key)
            {
                case Key.F11:
                case Key.F when noModifiers:
                    ToggleFullscreen();
                    e.Handled = true;
                    return;
                case Key.Escape when _isFullscreen:
                    ExitFullscreen();
                    e.Handled = true;
                    return;
                case Key.Escape when !_isFullscreen && _section == Section.Shows && _openShow != null:
                    BackToShowsGrid();
                    e.Handled = true;
                    return;
                case Key.M when noModifiers:
                    ToggleMute();
                    e.Handled = true;
                    return;
                case Key.D when modifiers == ModifierKeys.Control:
                    if (FavoriteTarget() is { } target) ToggleFavorite(target);
                    e.Handled = true;
                    return;
                case Key.F when modifiers == ModifierKeys.Control:
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                    e.Handled = true;
                    return;
                case Key.Add:
                case Key.OemPlus:
                    ChangeVolume(+5);
                    e.Handled = true;
                    return;
                case Key.Subtract:
                case Key.OemMinus:
                    ChangeVolume(-5);
                    e.Handled = true;
                    return;
                case Key.F5:
                    if (!_isLoading && SettingsStore.ActivePlaylist() is { } source) _ = LoadSourceAsync(source.Source);
                    e.Handled = true;
                    return;
                case Key.G when noModifiers:
                    if (_repo.Channels.Count > 0) new EpgGuideWindow { Owner = this }.Show();
                    e.Handled = true;
                    return;
            }

            if (_isFullscreen)
            {
                switch (key)
                {
                    case Key.Left:
                        // Seeking back a Movie/episode takes priority over the (Live-only) side panel,
                        // which is a no-op for VOD anyway — see OpenSidePanel.
                        if (_player.CanSeek) SeekRelative(-10_000);
                        else OpenSidePanel();
                        break;
                    case Key.Right:
                        if (_player.CanSeek) SeekRelative(10_000);
                        break;
                    case Key.Up:
                    case Key.PageUp:
                        Zap(-1);
                        break;
                    case Key.Down:
                    case Key.PageDown:
                        Zap(+1);
                        break;
                    case Key.Enter:
                    case Key.Space:
                        if (_player.State == PlaybackState.Failed && _playingItem != null)
                        {
                            PlayItem(_playingItem, _playbackItems, _playbackCategoryId);
                        }
                        else if (key == Key.Space && _player.CanPause && _player.State == PlaybackState.Playing)
                        {
                            _player.TogglePause();
                        }
                        ShowInfoBar();
                        break;
                }
                // The (hidden) lists must not react to anything typed while fullscreen.
                if (e.Key != Key.System) e.Handled = true;
                return;
            }

            // Windowed: ←/→ hop between the category and channel lists, like the Android sidebar.
            if (key == Key.Right && CategoryList.IsKeyboardFocusWithin)
            {
                FocusSelected(ChannelList);
                e.Handled = true;
            }
            else if (key == Key.Left && ChannelList.IsKeyboardFocusWithin)
            {
                FocusSelected(CategoryList);
                e.Handled = true;
            }
        }

        /// <summary>Puts keyboard focus on a list's selected row (selecting the first if none), scrolling it into view.</summary>
        private static void FocusSelected(ListBox list)
        {
            if (list.Items.Count == 0 || !list.IsVisible) return;
            if (list.SelectedIndex < 0) list.SelectedIndex = 0;
            list.ScrollIntoView(list.SelectedItem);
            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) is ListBoxItem container)
            {
                container.Focus();
            }
            else
            {
                list.Focus();
            }
        }

        // ---- Buttons -----------------------------------------------------------------------------

        private void GuideButton_Click(object sender, RoutedEventArgs e)
        {
            if (_repo.Channels.Count == 0) return;
            new EpgGuideWindow { Owner = this }.Show();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            new SettingsWindow { Owner = this }.ShowDialog();
        }

        private void WelcomeSourceButton_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsStore.ActivePlaylist() is { } active) EditPlaylist(active);
            else AddPlaylist();
        }

        // ---- Multiple playlists --------------------------------------------------------------

        /// <summary>Opens the "new playlist" dialog and, on success, saves and switches to it. The dialog already
        /// fetched the channels to validate the source, so applying them here needs no second round-trip.</summary>
        public void AddPlaylist()
        {
            var dialog = new SourceWindow(null, null) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.LoadedSource == null || dialog.LoadedData == null) return;

            var playlist = new SavedPlaylist
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = dialog.SavedName ?? PlaylistNaming.Suggest(dialog.LoadedSource),
                Source = dialog.LoadedSource
            };
            SettingsStore.Current.Playlists.Add(playlist);
            SettingsStore.Current.ActivePlaylistId = playlist.Id;
            SettingsStore.Save();
            _loadCts?.Cancel(); // any load still running for the previous active playlist is moot now
            RefreshPlaylistSwitcher();
            ApplyPlaylist(dialog.LoadedData);
        }

        /// <summary>Opens the "edit playlist" dialog prefilled with <paramref name="playlist"/>. If it's the active
        /// one, applies the freshly-fetched channels immediately; otherwise just saves the new definition for
        /// next time it's switched to.</summary>
        public void EditPlaylist(SavedPlaylist playlist)
        {
            var dialog = new SourceWindow(playlist.Name, playlist.Source) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.LoadedSource == null || dialog.LoadedData == null) return;

            playlist.Name = dialog.SavedName ?? playlist.Name;
            playlist.Source = dialog.LoadedSource;
            SettingsStore.Save();
            RefreshPlaylistSwitcher();

            if (playlist.Id == SettingsStore.Current.ActivePlaylistId)
            {
                _loadCts?.Cancel();
                ApplyPlaylist(dialog.LoadedData);
            }
        }

        /// <summary>Makes <paramref name="playlist"/> active and (re-)fetches its channels.</summary>
        public async Task SwitchPlaylistAsync(SavedPlaylist playlist)
        {
            if (playlist.Id == SettingsStore.Current.ActivePlaylistId) return;
            SettingsStore.Current.ActivePlaylistId = playlist.Id;
            SettingsStore.Save();
            RefreshPlaylistSwitcher();
            await LoadSourceAsync(playlist.Source);
        }

        /// <summary>Removes a saved playlist. If it was active, switches to another saved playlist if one
        /// remains, or falls back to the Welcome screen if that was the last one.</summary>
        public async Task RemovePlaylistAsync(SavedPlaylist playlist)
        {
            var wasActive = playlist.Id == SettingsStore.Current.ActivePlaylistId;
            SettingsStore.Current.Playlists.Remove(playlist);

            if (!wasActive)
            {
                SettingsStore.Save();
                RefreshPlaylistSwitcher();
                return;
            }

            var next = SettingsStore.Current.Playlists.FirstOrDefault();
            SettingsStore.Current.ActivePlaylistId = next?.Id;
            SettingsStore.Save();
            RefreshPlaylistSwitcher();

            if (next != null)
            {
                await LoadSourceAsync(next.Source);
            }
            else
            {
                _loadCts?.Cancel();
                _player.Stop();
                _repo.Apply(new PlaylistData(Array.Empty<Channel>(), Array.Empty<Channel>(), Array.Empty<ShowSummary>(), Array.Empty<Channel>(), Array.Empty<string>()));
                _items.Clear();
                _movieItems.Clear();
                _showItems.Clear();
                _openShow = null;
                _openShowEpisodes = Array.Empty<ChannelItem>();
                _section = Section.Live;
                SearchBox.IsEnabled = true;
                MoviesSectionButton.IsEnabled = false;
                ShowsSectionButton.IsEnabled = false;
                UpdateSectionButtonStyles();
                _playingItem = null;
                GuideButton.IsEnabled = false;
                ShowWelcome(Strings.WelcomeTitle, Strings.WelcomeMessage, canRetry: false);
            }
        }

        /// <summary>Keeps the top-bar playlist picker in sync with SettingsStore — call after any add/edit/remove/switch.</summary>
        private void RefreshPlaylistSwitcher()
        {
            // A fresh List every call, not the stored one directly: SettingsStore.Current.Playlists is
            // mutated in place (Add/Remove) rather than replaced, and WPF's ItemsSource only re-reads a
            // non-observable collection when it's handed a genuinely different reference — reassigning
            // the same List object again is a no-op, so a newly added playlist would never appear.
            var playlists = SettingsStore.Current.Playlists.ToList();
            _suppressPlaylistSwitcherChange = true;
            PlaylistSwitcher.ItemsSource = playlists;
            PlaylistSwitcher.SelectedItem = playlists.FirstOrDefault(p => p.Id == SettingsStore.Current.ActivePlaylistId);
            _suppressPlaylistSwitcherChange = false;
            PlaylistSwitcher.Visibility = playlists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void PlaylistSwitcher_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPlaylistSwitcherChange) return;
            if (PlaylistSwitcher.SelectedItem is SavedPlaylist playlist) await SwitchPlaylistAsync(playlist);
        }

        private async void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || SettingsStore.ActivePlaylist() is not { } active) return;
            await LoadSourceAsync(active.Source);
        }
    }
}
