using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ZecaTV.Data;
using ZecaTV.Localization;
using ZecaTV.Models;
using ZecaTV.UI;

namespace ZecaTV
{
    /// <summary>
    /// Assigns which EPG source + guide channel a playlist channel should use, overriding the
    /// automatic id/name matching in <see cref="EpgStore.Resolve"/>. Port of Android's
    /// ChannelMappingActivity: a channel picker plus every current mapping, removable — reachable
    /// either from Settings (no channel picked yet) or a channel's right-click menu (preselected).
    /// Unlike a typical "OK/Cancel" dialog, Save applies the mapping and the window stays open, so
    /// several channels can be assigned in one sitting; Close/Esc/the titlebar X just dismiss it.
    /// </summary>
    public partial class EpgMappingWindow : Window
    {
        private sealed record NamedId(string Id, string Name);
        private sealed record ChannelOption(Channel Channel) { public string Name => Channel.Name; }
        private sealed record MappingRow(string ChannelKey, string ChannelName, string Detail);

        private readonly EpgStore _epg = EpgStore.Instance;
        private readonly Channel? _preselect;
        private bool _populating;

        public EpgMappingWindow(Channel? preselect = null)
        {
            InitializeComponent();
            WindowFitting.ShrinkToWorkArea(this);
            _preselect = preselect;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DarkTitleBar.Apply(this);
        }

        public new bool? ShowDialog()
        {
            LoadChannels();
            RefreshMappingsList();
            return base.ShowDialog();
        }

        private void LoadChannels()
        {
            var channels = PlaylistRepository.Instance.Channels
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ChannelOption(c))
                .ToList();

            if (channels.Count == 0)
            {
                ShowMessage(Strings.NoPlaylistLoadedYet);
                ChannelCombo.IsEnabled = SourceCombo.IsEnabled = GuideChannelCombo.IsEnabled = SaveButton.IsEnabled = false;
                return;
            }

            ChannelCombo.ItemsSource = channels;
            ChannelCombo.SelectedItem = _preselect != null
                ? channels.FirstOrDefault(c => c.Channel.Key == _preselect.Key) ?? channels[0]
                : channels[0];
        }

        private void ChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadSourcesForChannel();

        private void LoadSourcesForChannel()
        {
            if (ChannelCombo.SelectedItem is not ChannelOption channel) return;

            var sources = _epg.AvailableSources().Select(s => new NamedId(s.Id, s.Name)).ToList();
            if (sources.Count == 0)
            {
                ShowMessage(Strings.NoEpgSourcesAvailableYet);
                SourceCombo.ItemsSource = null;
                GuideChannelCombo.ItemsSource = null;
                SaveButton.IsEnabled = false;
                ClearButton.Visibility = Visibility.Collapsed;
                return;
            }
            SaveButton.IsEnabled = true;
            HideMessage();

            var existing = _epg.GetMapping(channel.Channel.Key);
            ClearButton.Visibility = existing != null ? Visibility.Visible : Visibility.Collapsed;

            _populating = true;
            SourceCombo.ItemsSource = sources;
            SourceCombo.SelectedItem = existing != null
                ? sources.FirstOrDefault(s => s.Id == existing.EpgSourceId) ?? sources[0]
                : sources[0];
            _populating = false;

            PopulateGuideChannels(existing?.EpgChannelId);
        }

        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_populating) PopulateGuideChannels(null);
        }

        private async void PopulateGuideChannels(string? preselectEpgChannelId)
        {
            if (SourceCombo.SelectedItem is not NamedId source) return;
            HideMessage();

            var names = _epg.ChannelNamesForSource(source.Id);
            if (names.Count == 0 && source.Id != EpgStore.PrimarySourceId)
            {
                // This extra source's guide list hasn't been fetched in this session — load it now.
                var url = SettingsStore.Current.EpgSources.FirstOrDefault(s => s.Id == source.Id)?.Url;
                if (url != null)
                {
                    ShowMessage(Strings.LoadingSourceChannelList);
                    GuideChannelCombo.ItemsSource = null;
                    await _epg.ReloadSourceAsync(source.Id, url);
                    // The user may have picked a different source while this was loading.
                    if (!Equals(SourceCombo.SelectedItem, source)) return;
                    names = _epg.ChannelNamesForSource(source.Id);
                    HideMessage();
                }
            }

            var items = names
                .Select(kv => new NamedId(kv.Key, $"{kv.Value} ({kv.Key})"))
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            GuideChannelCombo.ItemsSource = items;
            if (items.Count == 0)
            {
                ShowMessage(source.Id == EpgStore.PrimarySourceId
                    ? Strings.PrimarySourceNoGuideChannelsYet
                    : Strings.SourceHasNoGuideChannels);
                return;
            }

            GuideChannelCombo.SelectedItem = preselectEpgChannelId != null
                ? items.FirstOrDefault(i => i.Id == preselectEpgChannelId) ?? items[0]
                : items[0];
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (ChannelCombo.SelectedItem is not ChannelOption channel ||
                SourceCombo.SelectedItem is not NamedId source ||
                GuideChannelCombo.SelectedItem is not NamedId guide)
            {
                ShowMessage(Strings.PickChannelSourceGuideFirst);
                return;
            }
            _epg.SetMapping(new EpgChannelMapping(channel.Channel.Key, source.Id, guide.Id));
            ShowMessage(string.Format(Strings.SavedMappingFormat, channel.Name, guide.Name));
            ClearButton.Visibility = Visibility.Visible;
            RefreshMappingsList();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (ChannelCombo.SelectedItem is not ChannelOption channel) return;
            _epg.ClearMapping(channel.Channel.Key);
            ClearButton.Visibility = Visibility.Collapsed;
            ShowMessage(string.Format(Strings.RemovedMappingFormat, channel.Name));
            RefreshMappingsList();
        }

        private void RemoveMapping_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is not string channelKey) return;
            _epg.ClearMapping(channelKey);
            RefreshMappingsList();
            if (ChannelCombo.SelectedItem is ChannelOption current && current.Channel.Key == channelKey)
            {
                ClearButton.Visibility = Visibility.Collapsed;
            }
        }

        private void RefreshMappingsList()
        {
            var channelsByKey = PlaylistRepository.Instance.Channels.ToDictionary(c => c.Key, c => c.Name, StringComparer.Ordinal);
            var sourceNames = _epg.AvailableSources().ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal);

            var rows = new List<MappingRow>();
            foreach (var mapping in SettingsStore.Current.EpgMappings.Values)
            {
                var channelName = channelsByKey.TryGetValue(mapping.ChannelKey, out var name) ? name : mapping.ChannelKey;
                var sourceName = sourceNames.TryGetValue(mapping.EpgSourceId, out var sName) ? sName : mapping.EpgSourceId;
                var guideName = _epg.ChannelNamesForSource(mapping.EpgSourceId).TryGetValue(mapping.EpgChannelId, out var gName)
                    ? gName
                    : mapping.EpgChannelId;
                rows.Add(new MappingRow(mapping.ChannelKey, channelName, $"{sourceName} → {guideName}"));
            }
            MappingsList.ItemsSource = rows.OrderBy(r => r.ChannelName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void ShowMessage(string text)
        {
            MessageText.Text = text;
            MessageText.Foreground = (System.Windows.Media.Brush)FindResource("SubTextBrush");
            MessageText.Visibility = Visibility.Visible;
        }

        private void HideMessage() => MessageText.Visibility = Visibility.Collapsed;
    }
}
