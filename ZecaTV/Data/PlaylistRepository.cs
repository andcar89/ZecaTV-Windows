using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZecaTV.Models;
using ZecaTV.Network;
using ZecaTV.Parsers;

namespace ZecaTV.Data
{
    /// <summary>A freshly fetched playlist that hasn't been made current yet.</summary>
    /// <param name="Channels">Live TV only.</param>
    /// <param name="Movies">Movie entries — from Xtream's own get_vod_streams, or M3U entries ContentClassifier guessed were movies.</param>
    /// <param name="Shows">TV show tiles — from Xtream's own get_series (no episodes yet, fetched lazily), or shows synthesized
    /// by grouping M3U Series-type entries by their guessed SeriesTitle.</param>
    /// <param name="SeriesEpisodes">M3U only: the raw Series-type channels backing <paramref name="Shows"/>, so
    /// PlaylistRepository.EpisodesForLocalShow can filter them by SeriesTitle. Always empty for Xtream, whose
    /// episodes aren't fetched until a show is actually opened (see XtreamClient.FetchSeriesEpisodesAsync).</param>
    public sealed record PlaylistData(
        IReadOnlyList<Channel> Channels,
        IReadOnlyList<Channel> Movies,
        IReadOnlyList<ShowSummary> Shows,
        IReadOnlyList<Channel> SeriesEpisodes,
        IReadOnlyList<string> EpgUrls,
        int XtreamServerTimeOffsetSeconds = 0,
        /// <summary>M3U only: every channel exactly as ContentClassifier guessed it, before any manual
        /// category override (see PlaylistRepository.SetCategoryOverride) is applied. Kept around so an
        /// override change — or one being removed again — always re-derives Channels/Movies/Shows/
        /// SeriesEpisodes from the original guess rather than layering a correction on top of a
        /// previous one. Null for Xtream, which is never guessed and so never has overrides applied.</summary>
        IReadOnlyList<Channel>? RawM3uChannels = null);

    /// <summary>
    /// In-memory holder for the current channel list (port of the Android PlaylistRepository, minus
    /// the EPG half, which arrives in Milestone 3).
    ///
    /// Loading is split in two, unlike Android: <see cref="FetchAsync"/> downloads and parses WITHOUT
    /// touching current state, and <see cref="Apply"/> makes the result current. That way a failed or
    /// empty load from the playlist dialog never leaves the main window showing channels the
    /// repository no longer knows about.
    /// </summary>
    public sealed class PlaylistRepository
    {
        public static PlaylistRepository Instance { get; } = new();

        public const string CategoryAll = "__all__";

        public const string CategoryFavorites = "__favorites__";

        private Dictionary<string, List<Channel>> _byGroup = new(StringComparer.Ordinal);
        private Dictionary<string, List<Channel>> _movieByGroup = new(StringComparer.Ordinal);
        private Dictionary<string, List<ShowSummary>> _showByGroup = new(StringComparer.Ordinal);

        /// <summary>Group names in the order they first appear in the playlist file — NOT alphabetical.
        /// Tools like the M3U Editor let someone drag categories into a deliberate order before
        /// exporting; this is what makes the sidebar honor that order instead of re-sorting it.</summary>
        private List<string> _groupOrder = new();
        private List<string> _movieGroupOrder = new();
        private List<string> _showGroupOrder = new();

        /// <summary>M3U only: the raw Series-type channels backing <see cref="Shows"/> — see PlaylistData.SeriesEpisodes.</summary>
        private IReadOnlyList<Channel> _seriesEpisodes = Array.Empty<Channel>();

        /// <summary>M3U only: every channel exactly as ContentClassifier guessed it — see PlaylistData.RawM3uChannels.</summary>
        private IReadOnlyList<Channel> _rawM3uChannels = Array.Empty<Channel>();

        public IReadOnlyList<Channel> Channels { get; private set; } = Array.Empty<Channel>();

        public IReadOnlyList<Channel> Movies { get; private set; } = Array.Empty<Channel>();

        public IReadOnlyList<ShowSummary> Shows { get; private set; } = Array.Empty<ShowSummary>();

        /// <summary>EPG URL(s) bundled with the playlist source (M3U url-tvg / typed EPG URL override / Xtream
        /// xmltv.php) — usually one, but an M3U can declare several as a comma-separated list.</summary>
        public IReadOnlyList<string> PrimaryEpgUrls { get; private set; } = Array.Empty<string>();

        /// <summary>Xtream only: server-local-minus-UTC offset used to build catch-up (timeshift) URLs. 0 for M3U playlists.</summary>
        public int XtreamServerTimeOffsetSeconds { get; private set; }

        /// <summary>Downloads (or reads) and parses <paramref name="source"/>. Does not change current state.</summary>
        public async Task<PlaylistData> FetchAsync(PlaylistSource source, CancellationToken ct = default)
        {
            switch (source.Type)
            {
                case SourceType.M3uUrl:
                {
                    using var response = await Http.GetAsync(source.Url ?? "", ct).ConfigureAwait(false);
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    var result = await Task.Run(() => M3uParser.Parse(stream), ct).ConfigureAwait(false);
                    // URL(s) the user typed in win entirely; otherwise whatever the playlist itself declares
                    // (which may itself be more than one, comma-separated — see M3uParser.SplitEpgUrls).
                    var manualUrls = M3uParser.SplitEpgUrls(source.EpgUrl);
                    return BuildFromM3u(result, manualUrls.Count > 0 ? manualUrls : result.EpgUrls);
                }

                case SourceType.M3uFile:
                {
                    var path = source.FilePath ?? "";
                    if (!File.Exists(path))
                    {
                        throw new FileNotFoundException($"Couldn't open the playlist file: {path}");
                    }
                    var result = await Task.Run(() =>
                    {
                        using var stream = File.OpenRead(path);
                        return M3uParser.Parse(stream);
                    }, ct).ConfigureAwait(false);
                    var manualUrls = M3uParser.SplitEpgUrls(source.EpgUrl);
                    return BuildFromM3u(result, manualUrls.Count > 0 ? manualUrls : result.EpgUrls);
                }

                case SourceType.Xtream:
                {
                    var client = new XtreamClient(source.Server ?? "", source.Username ?? "", source.Password ?? "");
                    var channels = await client.FetchLiveChannelsAsync(ct).ConfigureAwait(false);
                    // Only worth the extra request when at least one channel actually offers catch-up.
                    var offset = channels.Any(c => c.HasCatchup)
                        ? await client.FetchServerTimeOffsetSecondsAsync(ct).ConfigureAwait(false)
                        : 0;

                    // Movies/Shows are optional extras (see XtreamClient's best-effort handling) fetched
                    // alongside Live, not blocking on it — an account with no VOD/series entitlement just
                    // gets empty tabs rather than a failed load.
                    var moviesTask = client.FetchVodAsync(ct);
                    var showsTask = client.FetchSeriesShowsAsync(ct);
                    await Task.WhenAll(moviesTask, showsTask).ConfigureAwait(false);

                    return new PlaylistData(channels, moviesTask.Result, showsTask.Result, Array.Empty<Channel>(),
                        new[] { client.EpgUrl() }, offset);
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(source), source.Type, "Unknown source type");
            }
        }

        /// <summary>Splits an M3U's flat, already-classified channel list (see ContentClassifier) into the
        /// three pools, applying any manual category overrides first (see <see cref="SetCategoryOverride"/>),
        /// and groups Series-type entries into show tiles by their guessed SeriesTitle — M3U has no native
        /// "show" concept the way Xtream's get_series does, so this is the M3U equivalent of that API
        /// call, built from what's already been parsed rather than a second request.</summary>
        private static PlaylistData BuildFromM3u(M3uParser.Result result, IReadOnlyList<string> epgUrls)
        {
            var (live, movies, episodes) = SplitByContentType(result.Channels);
            var shows = BuildShows(episodes);
            return new PlaylistData(live, movies, shows, episodes, epgUrls, RawM3uChannels: result.Channels);
        }

        private static (List<Channel> Live, List<Channel> Movies, List<Channel> Episodes) SplitByContentType(IReadOnlyList<Channel> channels)
        {
            var live = new List<Channel>();
            var movies = new List<Channel>();
            var episodes = new List<Channel>();
            foreach (var raw in channels)
            {
                var channel = ApplyCategoryOverride(raw);
                switch (channel.ContentType)
                {
                    case ContentType.Movie: movies.Add(channel); break;
                    case ContentType.Series: episodes.Add(channel); break;
                    default: live.Add(channel); break;
                }
            }
            return (live, movies, episodes);
        }

        private static List<ShowSummary> BuildShows(List<Channel> episodes)
        {
            var shows = new List<ShowSummary>();
            var seenTitles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var episode in episodes)
            {
                var title = episode.SeriesTitle ?? episode.Name;
                if (!seenTitles.Add(title)) continue;
                shows.Add(new ShowSummary(title, title, episode.LogoUrl, episode.GroupTitle, IsXtreamSeries: false));
            }
            return shows;
        }

        /// <summary>Manual per-category-name correction for ContentClassifier's M3U guess — see the
        /// sidebar category's right-click "Treat category as…" menu. Null means no override (use the
        /// automatic guess as-is).</summary>
        public static ContentType? CategoryOverride(string groupTitle) =>
            SettingsStore.Current.CategoryContentOverrides.TryGetValue(groupTitle, out var type) ? type : null;

        /// <summary>Sets (or, with null, clears) the override for one category name, persists it, and —
        /// for an M3U playlist — immediately re-derives Channels/Movies/Shows/SeriesEpisodes so the
        /// change is visible without a playlist reload. A no-op on the underlying data for an Xtream
        /// playlist (nothing there is ever guessed, so <see cref="_rawM3uChannels"/> is empty), though
        /// the setting itself is still saved for whenever an M3U playlist using this category name is
        /// loaded, current or future.</summary>
        public void SetCategoryOverride(string groupTitle, ContentType? type)
        {
            if (type == null) SettingsStore.Current.CategoryContentOverrides.Remove(groupTitle);
            else SettingsStore.Current.CategoryContentOverrides[groupTitle] = type.Value;
            SettingsStore.Save();

            if (_rawM3uChannels.Count == 0) return;
            var (live, movies, episodes) = SplitByContentType(_rawM3uChannels);
            var shows = BuildShows(episodes);
            Apply(new PlaylistData(live, movies, shows, episodes, PrimaryEpgUrls, XtreamServerTimeOffsetSeconds, _rawM3uChannels));
        }

        private static Channel ApplyCategoryOverride(Channel channel)
        {
            var forced = CategoryOverride(channel.GroupTitle);
            if (forced == null || forced == channel.ContentType) return channel;

            return forced.Value == ContentType.Series
                ? channel with { ContentType = ContentType.Series, SeriesTitle = channel.SeriesTitle ?? channel.Name }
                : channel with { ContentType = forced.Value, SeriesTitle = null, SeasonNumber = null, EpisodeNumber = null };
        }

        /// <summary>A snapshot of the repository's current state, for MainWindow.ApplyPlaylist to re-render
        /// the UI from after <see cref="SetCategoryOverride"/> has already updated the repository itself
        /// — no playlist refetch, just the UI catching up to data that's already current.</summary>
        public PlaylistData Snapshot() =>
            new(Channels, Movies, Shows, _seriesEpisodes, PrimaryEpgUrls, XtreamServerTimeOffsetSeconds, _rawM3uChannels);

        /// <summary>Makes <paramref name="data"/> the current playlist. Call on the UI thread.</summary>
        public void Apply(PlaylistData data)
        {
            Channels = data.Channels;
            Movies = data.Movies;
            Shows = data.Shows;
            _seriesEpisodes = data.SeriesEpisodes;
            _rawM3uChannels = data.RawM3uChannels ?? Array.Empty<Channel>();
            PrimaryEpgUrls = data.EpgUrls;
            XtreamServerTimeOffsetSeconds = data.XtreamServerTimeOffsetSeconds;

            (_byGroup, _groupOrder) = GroupByTitle(data.Channels, c => c.GroupTitle);
            (_movieByGroup, _movieGroupOrder) = GroupByTitle(data.Movies, c => c.GroupTitle);
            (_showByGroup, _showGroupOrder) = GroupByTitle(data.Shows, s => s.GroupTitle);
        }

        /// <summary>Groups items by their group-title, preserving first-seen order (not alphabetical) —
        /// shared by Live/Movies/Shows so all three sidebar sections follow the same playlist-order rule.</summary>
        private static (Dictionary<string, List<T>> ByGroup, List<string> Order) GroupByTitle<T>(
            IEnumerable<T> items, Func<T, string> groupTitleOf)
        {
            var byGroup = new Dictionary<string, List<T>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var item in items)
            {
                var group = groupTitleOf(item);
                if (!byGroup.TryGetValue(group, out var list))
                {
                    list = new List<T>();
                    byGroup[group] = list;
                    order.Add(group);
                }
                list.Add(item);
            }
            return (byGroup, order);
        }

        /// <summary>
        /// Sidebar categories: Favorites (only while at least one channel of this playlist is a
        /// favorite), All channels, then every group in the order it first appears in the playlist
        /// file — not alphabetical, unlike the Android app's sorted() — with the ungrouped sentinel
        /// shown as <paramref name="uncategorizedLabel"/>.
        /// </summary>
        public IReadOnlyList<Category> Categories(string favoritesLabel, string allLabel, string uncategorizedLabel)
        {
            var list = new List<Category>();
            var favoriteCount = FavoriteChannels().Count;
            if (favoriteCount > 0) list.Add(new Category(CategoryFavorites, favoritesLabel, favoriteCount));
            list.Add(new Category(CategoryAll, allLabel, Channels.Count));
            foreach (var group in _groupOrder)
            {
                var label = group == Channel.UngroupedGroupKey ? uncategorizedLabel : group;
                list.Add(new Category(group, label, _byGroup[group].Count));
            }
            return list;
        }

        /// <summary>Resolves a category id (as returned by <see cref="Categories"/>) to its channels, in playlist order.</summary>
        public IReadOnlyList<Channel> ChannelsForCategory(string id)
        {
            if (id == CategoryAll) return Channels;
            if (id == CategoryFavorites) return FavoriteChannels();
            return _byGroup.TryGetValue(id, out var list) ? list : Array.Empty<Channel>();
        }

        /// <summary>Movies tab categories: All movies, then one per genre group. No Favorites row here —
        /// favoriting is Live-channel-only for now.</summary>
        public IReadOnlyList<Category> MovieCategories(string allLabel, string uncategorizedLabel) =>
            GenreCategories(Movies.Count, _movieGroupOrder, _movieByGroup, allLabel, uncategorizedLabel);

        /// <summary>Resolves a category id (as returned by <see cref="MovieCategories"/>) to its movies, in playlist/API order.</summary>
        public IReadOnlyList<Channel> MoviesForCategory(string id) =>
            id == CategoryAll ? Movies : (_movieByGroup.TryGetValue(id, out var list) ? list : Array.Empty<Channel>());

        /// <summary>TV Shows tab categories: All shows, then one per genre group.</summary>
        public IReadOnlyList<Category> ShowCategories(string allLabel, string uncategorizedLabel) =>
            GenreCategories(Shows.Count, _showGroupOrder, _showByGroup, allLabel, uncategorizedLabel);

        /// <summary>Resolves a category id (as returned by <see cref="ShowCategories"/>) to its shows, in playlist/API order.</summary>
        public IReadOnlyList<ShowSummary> ShowsForCategory(string id) =>
            id == CategoryAll ? Shows : (_showByGroup.TryGetValue(id, out var list) ? list : Array.Empty<ShowSummary>());

        private static IReadOnlyList<Category> GenreCategories<T>(
            int allCount, List<string> groupOrder, Dictionary<string, List<T>> byGroup, string allLabel, string uncategorizedLabel)
        {
            var list = new List<Category> { new(CategoryAll, allLabel, allCount) };
            foreach (var group in groupOrder)
            {
                var label = group == Channel.UngroupedGroupKey ? uncategorizedLabel : group;
                list.Add(new Category(group, label, byGroup[group].Count));
            }
            return list;
        }

        /// <summary>Episodes of one M3U-synthesized show (<see cref="ShowSummary.IsXtreamSeries"/> false),
        /// sorted by season then episode number — a local filter of already-loaded channels, since M3U
        /// has nothing left to fetch. For a real Xtream series, fetch with
        /// XtreamClient.FetchSeriesEpisodesAsync(show.Id) instead and pass the result through <see cref="SortEpisodes"/>.</summary>
        public IReadOnlyList<Channel> EpisodesForLocalShow(ShowSummary show) =>
            SortEpisodes(_seriesEpisodes.Where(c => (c.SeriesTitle ?? c.Name) == show.Id));

        /// <summary>Season-then-episode order, unknown numbers sorted last (in their original order).</summary>
        public static IReadOnlyList<Channel> SortEpisodes(IEnumerable<Channel> episodes) =>
            episodes.OrderBy(c => c.SeasonNumber ?? int.MaxValue).ThenBy(c => c.EpisodeNumber ?? int.MaxValue).ToList();

        /// <summary>Favorited channels of the current playlist, in playlist order (as Android).</summary>
        private List<Channel> FavoriteChannels()
        {
            var result = new List<Channel>();
            if (FavoritesManager.Count == 0) return result;
            foreach (var channel in Channels)
            {
                if (FavoritesManager.IsFavorite(channel.Key)) result.Add(channel);
            }
            return result;
        }

        /// <summary>
        /// Builds a catch-up (timeshift) playback URL for <paramref name="programme"/> on <paramref name="channel"/>,
        /// or null if catch-up isn't applicable — the channel/panel doesn't offer it, the active playlist
        /// isn't an Xtream source, or the programme aired further back than the panel's retention window.
        /// Never throws: any of those is just "no catch-up available", not an error.
        /// </summary>
        public string? BuildCatchupUrl(Channel channel, EpgProgramme programme, PlaylistSource? activeSource)
        {
            if (!channel.HasCatchup || channel.StreamId == null) return null;
            if (activeSource == null || activeSource.Type != SourceType.Xtream) return null;

            var start = DateTimeOffset.FromUnixTimeMilliseconds(programme.StartMillis);
            var retentionCutoff = DateTimeOffset.UtcNow.AddDays(-channel.CatchupDurationDays);
            if (start < retentionCutoff) return null;

            var stop = DateTimeOffset.FromUnixTimeMilliseconds(programme.StopMillis);
            var durationMinutes = Math.Max(1, (int)Math.Ceiling((stop - start).TotalMinutes));

            var client = new XtreamClient(activeSource.Server ?? "", activeSource.Username ?? "", activeSource.Password ?? "");
            return client.BuildCatchupUrl(channel.StreamId, start, durationMinutes, XtreamServerTimeOffsetSeconds);
        }
    }
}
