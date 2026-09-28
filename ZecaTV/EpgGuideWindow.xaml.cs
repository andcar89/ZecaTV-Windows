using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZecaTV.Data;
using ZecaTV.Localization;
using ZecaTV.Models;
using ZecaTV.UI;

namespace ZecaTV
{
    /// <summary>
    /// A TiviMate-style EPG grid: channels down the side, a shared timeline across the top, and one
    /// scrollable area of programme cells — port of Android's EpgGridActivity. Android synced each
    /// visible row's own horizontal scroll view to a shared position (needed there because its channel
    /// list was virtualized by a RecyclerView); here all rows live in one ScrollViewer, so the header
    /// and channel column just slave their offset to that single scroll position instead — see
    /// BodyScroll_ScrollChanged.
    /// </summary>
    public partial class EpgGuideWindow : Window
    {
        private const int WindowHours = 6;
        private const double PxPerMinute = 3.5;
        private const double RowHeight = 60;

        private sealed record CategoryOption(string Id, string Label);

        private readonly PlaylistRepository _repo = PlaylistRepository.Instance;
        private readonly EpgStore _epg = EpgStore.Instance;

        private long _windowStart;
        private long _windowEnd;
        private bool _suppressCategoryChange;
        private CancellationTokenSource? _reloadCts;

        public EpgGuideWindow()
        {
            InitializeComponent();
            ResetWindowToNow();
            LoadCategories();
            Loaded += (_, _) => BuildGrid();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DarkTitleBar.Apply(this);
        }

        protected override void OnClosed(EventArgs e)
        {
            _reloadCts?.Cancel();
            base.OnClosed(e);
        }

        private void LoadCategories()
        {
            var categories = _repo.Categories(Strings.CategoryFavorites, Strings.CategoryAllChannels, Strings.CategoryUncategorized)
                .Select(c => new CategoryOption(c.Id, c.Label))
                .ToList();

            _suppressCategoryChange = true;
            CategoryCombo.ItemsSource = categories;
            CategoryCombo.SelectedItem = categories.FirstOrDefault(c => c.Id == PlaylistRepository.CategoryAll) ?? categories.FirstOrDefault();
            _suppressCategoryChange = false;
        }

        private void ResetWindowToNow()
        {
            var now = DateTimeOffset.Now;
            var roundedMinute = now.Minute < 30 ? 0 : 30;
            var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, roundedMinute, 0, now.Offset);
            _windowStart = start.ToUnixTimeMilliseconds();
            _windowEnd = _windowStart + WindowHours * 60L * 60L * 1000L;
        }

        private void ShiftWindow(int hours)
        {
            _windowStart += hours * 60L * 60L * 1000L;
            _windowEnd += hours * 60L * 60L * 1000L;
            BuildGrid();
        }

        // ---- Building the grid -------------------------------------------------------------------

        private void BuildGrid()
        {
            if (!IsLoaded) return;

            var startLocal = DateTimeOffset.FromUnixTimeMilliseconds(_windowStart).ToLocalTime();
            var endLocal = DateTimeOffset.FromUnixTimeMilliseconds(_windowEnd).ToLocalTime();
            WindowLabel.Text = startLocal.Date == endLocal.Date
                ? $"{startLocal:ddd d MMM} · {startLocal:HH:mm}–{endLocal:HH:mm}"
                : $"{startLocal:HH:mm} {startLocal:ddd d MMM} – {endLocal:HH:mm} {endLocal:ddd d MMM}";

            BuildHeader();

            var categoryId = (CategoryCombo.SelectedItem as CategoryOption)?.Id ?? PlaylistRepository.CategoryAll;
            var channels = _repo.ChannelsForCategory(categoryId);

            ChannelColumn.Children.Clear();
            BodyRows.Children.Clear();

            foreach (var channel in channels)
            {
                ChannelColumn.Children.Add(BuildChannelRow(channel));
                BodyRows.Children.Add(BuildProgrammeRow(channel));
            }

            // A fresh grid always starts scrolled to the top-left of the new window/category.
            BodyScroll.ScrollToHorizontalOffset(0);
            BodyScroll.ScrollToVerticalOffset(0);
        }

        private void BuildHeader()
        {
            HeaderTicks.Children.Clear();
            var totalMinutes = (int)((_windowEnd - _windowStart) / 60000L);
            var cursor = _windowStart;
            while (cursor < _windowEnd)
            {
                var local = DateTimeOffset.FromUnixTimeMilliseconds(cursor).ToLocalTime();
                HeaderTicks.Children.Add(new TextBlock
                {
                    Text = local.ToString("HH:mm", CultureInfo.CurrentCulture),
                    Width = 30 * PxPerMinute,
                    Height = 34,
                    Padding = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("SubTextBrush")
                });
                cursor += 30 * 60 * 1000L;
            }
            _ = totalMinutes; // header width follows its children; kept for clarity/future use
        }

        private Border BuildChannelRow(Channel channel)
        {
            var image = new Image { Width = 28, Height = 28, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 10, 0) };
            _ = LoadLogoAsync(image, channel.LogoUrl);

            var nameText = new TextBlock
            {
                Text = channel.Name,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var row = new DockPanel { LastChildFill = true };
            row.Children.Add(image);
            DockPanel.SetDock(image, Dock.Left);
            row.Children.Add(nameText);

            var border = new Border
            {
                Height = RowHeight,
                Padding = new Thickness(14, 0, 10, 0),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent, // hit-tests the whole row, not just the text glyphs
                Child = row
            };
            // The channel name is always a shortcut back to live, whatever programme cell the user is
            // looking at (including while scrolled into a catch-up-eligible past cell).
            border.MouseLeftButtonUp += (_, _) => PlayFromGuide(channel);
            return border;
        }

        private static async Task LoadLogoAsync(Image image, string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var source = await LogoCache.GetAsync(url.Trim());
            if (source != null) image.Source = source;
        }

        private Border BuildProgrammeRow(Channel channel)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var programmes = _epg.ProgrammesFor(channel, _windowStart, _windowEnd);
            var nowMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var activeSource = SettingsStore.ActivePlaylist()?.Source;

            if (programmes.Count == 0)
            {
                content.Children.Add(FillerCell((_windowEnd - _windowStart) / 60000.0));
            }
            else
            {
                var cursor = _windowStart;
                foreach (var p in programmes)
                {
                    var start = Math.Max(p.StartMillis, _windowStart);
                    var stop = Math.Min(p.StopMillis, _windowEnd);
                    if (start > cursor) content.Children.Add(FillerCell((start - cursor) / 60000.0));
                    content.Children.Add(ProgrammeCell(p, channel, start, stop, nowMillis, activeSource));
                    cursor = stop;
                }
                if (cursor < _windowEnd) content.Children.Add(FillerCell((_windowEnd - cursor) / 60000.0));
            }

            return new Border { Height = RowHeight, Child = content };
        }

        private Border FillerCell(double durationMinutes)
        {
            return new Border
            {
                Width = Math.Max(1, durationMinutes * PxPerMinute),
                Margin = new Thickness(1, 6, 1, 6),
                Background = (Brush)FindResource("PanelBrush"),
                CornerRadius = new CornerRadius(4),
                Child = durationMinutes >= 20
                    ? new TextBlock
                    {
                        Text = Strings.NoProgrammeInfo,
                        FontSize = 11,
                        Foreground = (Brush)FindResource("MutedTextBrush"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                    : null
            };
        }

        /// <summary>Catch-up-eligible past cells get this blue tint instead of the plain panel background,
        /// so they read as "still watchable" at a glance without a separate legend.</summary>
        private static readonly Brush CatchupEligibleBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x15, 0x65, 0xC0));

        private Border ProgrammeCell(EpgProgramme p, Channel channel, long start, long stop, long nowMillis, PlaylistSource? activeSource)
        {
            var isCurrent = nowMillis >= p.StartMillis && nowMillis < p.StopMillis;
            var isPast = p.StopMillis <= nowMillis;
            var catchupUrl = isPast ? _repo.BuildCatchupUrl(channel, p, activeSource) : null;
            var durationMinutes = (stop - start) / 60000.0;

            // Four states: live now (accent), past + catch-up available (blue tint, clickable), past with
            // no catch-up (dimmed, inert — nothing useful happens if you click an old, unwatchable slot),
            // future (plain panel colour, clickable to just switch to the channel live).
            Brush background;
            Brush textBrush;
            bool clickable;
            if (isCurrent)
            {
                background = (Brush)FindResource("AccentBrush");
                textBrush = (Brush)FindResource("TextBrush");
                clickable = true;
            }
            else if (isPast && catchupUrl != null)
            {
                background = CatchupEligibleBrush;
                textBrush = (Brush)FindResource("TextBrush");
                clickable = true;
            }
            else if (isPast)
            {
                background = (Brush)FindResource("PanelAltBrush");
                textBrush = (Brush)FindResource("MutedTextBrush");
                clickable = false;
            }
            else
            {
                background = (Brush)FindResource("PanelAltBrush");
                textBrush = (Brush)FindResource("TextBrush");
                clickable = true;
            }

            var border = new Border
            {
                Width = Math.Max(1, durationMinutes * PxPerMinute),
                Margin = new Thickness(1, 6, 1, 6),
                Background = background,
                CornerRadius = new CornerRadius(4),
                Cursor = clickable ? Cursors.Hand : Cursors.Arrow,
                ToolTip = BuildTooltip(p, hasCatchup: catchupUrl != null),
                Child = new TextBlock
                {
                    Text = p.Title,
                    FontSize = 12,
                    Foreground = textBrush,
                    Padding = new Thickness(10, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = RowHeight - 12
                }
            };

            if (catchupUrl != null)
            {
                border.MouseLeftButtonUp += (_, _) => PlayCatchupFromGuide(channel, p);
            }
            else if (clickable)
            {
                border.MouseLeftButtonUp += (_, _) => PlayFromGuide(channel);
            }
            return border;
        }

        private static string BuildTooltip(EpgProgramme p, bool hasCatchup)
        {
            var start = DateTimeOffset.FromUnixTimeMilliseconds(p.StartMillis).ToLocalTime();
            var stop = DateTimeOffset.FromUnixTimeMilliseconds(p.StopMillis).ToLocalTime();
            var header = $"{p.Title}  ({start:HH:mm}–{stop:HH:mm})";
            var withDescription = string.IsNullOrWhiteSpace(p.Description) ? header : header + "\n" + p.Description;
            return hasCatchup ? withDescription + "\n" + Strings.WatchCatchupTooltip : withDescription;
        }

        private void PlayFromGuide(Channel channel)
        {
            if (Owner is MainWindow mainWindow) mainWindow.PlayChannelAndFullscreen(channel);
            Close();
        }

        private void PlayCatchupFromGuide(Channel channel, EpgProgramme programme)
        {
            if (Owner is MainWindow mainWindow) mainWindow.PlayChannelCatchupAndFullscreen(channel, programme);
            Close();
        }

        // ---- Toolbar & keyboard -------------------------------------------------------------------

        private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_suppressCategoryChange) BuildGrid();
        }

        private void NowButton_Click(object sender, RoutedEventArgs e)
        {
            ResetWindowToNow();
            BuildGrid();
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e) => ShiftWindow(-WindowHours);

        private void NextButton_Click(object sender, RoutedEventArgs e) => ShiftWindow(+WindowHours);

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _reloadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _reloadCts = cts;
            RefreshButton.IsEnabled = false;
            try
            {
                await _epg.ReloadAllAsync(cts.Token);
            }
            finally
            {
                if (_reloadCts == cts)
                {
                    RefreshButton.IsEnabled = true;
                    if (!cts.IsCancellationRequested) BuildGrid();
                }
            }
        }

        private void BodyScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.HorizontalChange != 0) HeaderScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
            if (e.VerticalChange != 0) ChannelScroll.ScrollToVerticalOffset(e.VerticalOffset);
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
                case Key.PageUp:
                    ShiftWindow(-WindowHours);
                    e.Handled = true;
                    break;
                case Key.PageDown:
                    ShiftWindow(+WindowHours);
                    e.Handled = true;
                    break;
                case Key.F5:
                    RefreshButton_Click(sender, e);
                    e.Handled = true;
                    break;
            }
        }
    }
}
