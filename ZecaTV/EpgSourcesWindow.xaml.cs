using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZecaTV.Data;
using ZecaTV.Localization;
using ZecaTV.UI;

namespace ZecaTV
{
    /// <summary>
    /// Add/remove extra XMLTV feeds and see each source's load status (port of the EPG-source part
    /// of Android's Settings screen). The bundled ("primary") source, if any, is listed read-only —
    /// there's nothing to remove, since it isn't a user-added row.
    /// </summary>
    public partial class EpgSourcesWindow : Window
    {
        /// <summary>One row of the list — a snapshot, rebuilt on every refresh rather than kept live.</summary>
        private sealed class SourceRow
        {
            public required string Id { get; init; }
            public required string Name { get; init; }
            public required string Url { get; init; }
            public required Visibility CanRemove { get; init; }
            public required string StatusText { get; init; }
            public required Brush StatusBrush { get; init; }
            public required bool IsActive { get; init; }
            public required string ToggleActiveText { get; init; }
            public required Visibility ActiveBadgeVisibility { get; init; }
        }

        private readonly EpgStore _epg = EpgStore.Instance;
        private readonly HashSet<string> _loadingIds = new(StringComparer.Ordinal);

        public EpgSourcesWindow()
        {
            InitializeComponent();
            Refresh();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DarkTitleBar.Apply(this);
        }

        private void Refresh()
        {
            var rows = new List<SourceRow>();
            var activeId = _epg.ActiveSourceOverride;
            foreach (var (id, name) in _epg.AvailableSources())
            {
                var url = _epg.UrlForSource(id) ?? "";
                var isActive = id == activeId;
                rows.Add(new SourceRow
                {
                    Id = id,
                    Name = name,
                    Url = url,
                    CanRemove = EpgStore.IsPrimarySourceId(id) ? Visibility.Collapsed : Visibility.Visible,
                    StatusText = DescribeStatus(id),
                    StatusBrush = StatusBrushFor(id),
                    IsActive = isActive,
                    ToggleActiveText = isActive ? Strings.DeactivateButton : Strings.ActivateButton,
                    ActiveBadgeVisibility = isActive ? Visibility.Visible : Visibility.Collapsed
                });
            }
            SourcesList.ItemsSource = rows;
        }

        private void ToggleActive_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is not string id) return;
            _epg.SetActiveSourceOverride(_epg.ActiveSourceOverride == id ? null : id);
            Refresh();
        }

        private string DescribeStatus(string id)
        {
            if (_loadingIds.Contains(id)) return Strings.SourceLoadingStatus;
            var status = _epg.GetSourceStatus(id);
            if (status.ErrorMessage != null)
            {
                return status.IsLoaded
                    ? string.Format(Strings.SourceFailedReloadFormat, status.ChannelCount, status.ErrorMessage)
                    : string.Format(Strings.SourceFailedToLoadFormat, status.ErrorMessage);
            }
            return status.IsLoaded
                ? string.Format(Strings.SourceLoadedFormat, status.ChannelCount, status.ProgrammeCount)
                : Strings.SourceNotLoadedYet;
        }

        private Brush StatusBrushFor(string id)
        {
            if (_loadingIds.Contains(id)) return (Brush)FindResource("SubTextBrush");
            var status = _epg.GetSourceStatus(id);
            return status.ErrorMessage != null ? (Brush)FindResource("ErrorBrush") : (Brush)FindResource("SubTextBrush");
        }

        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text.Trim();
            var url = UrlBox.Text.Trim();
            if (name.Length == 0 || url.Length == 0)
            {
                ShowAddError(Strings.EnterNameAndUrl);
                return;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                ShowAddError(Strings.InvalidUrlMessage);
                return;
            }

            AddButton.IsEnabled = false;
            HideAddError();
            var source = _epg.AddSource(name, url);
            Refresh();

            await ReloadAsync(source.Id, source.Url);
            AddButton.IsEnabled = true;
            NameBox.Clear();
            UrlBox.Clear();
        }

        private async void ReloadSource_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is not string id) return;
            var url = _epg.UrlForSource(id);
            if (url == null) return;
            await ReloadAsync(id, url);
        }

        private async Task ReloadAsync(string id, string url)
        {
            _loadingIds.Add(id);
            Refresh();
            await _epg.ReloadSourceAsync(id, url);
            _loadingIds.Remove(id);
            Refresh();
        }

        private void RemoveSource_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is not string id) return;
            _epg.RemoveSource(id);
            Refresh();
        }

        private void ShowAddError(string text)
        {
            AddMessageText.Text = text;
            AddMessageText.Visibility = Visibility.Visible;
        }

        private void HideAddError() => AddMessageText.Visibility = Visibility.Collapsed;
    }
}
