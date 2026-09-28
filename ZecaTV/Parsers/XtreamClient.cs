using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZecaTV.Models;
using ZecaTV.Network;

namespace ZecaTV.Parsers
{
    /// <summary>
    /// Talks to a standard Xtream Codes panel via its player_api.php endpoint.
    /// Port of the Android app's XtreamClient.kt.
    ///
    /// JSON is read field-by-field rather than deserialized into classes: panels are inconsistent
    /// about types (stream_id and category_id arrive as numbers on some panels, strings on others).
    /// Gson on Android silently coerced both ways; System.Text.Json would throw instead.
    /// </summary>
    public sealed class XtreamClient
    {
        private readonly string _server;
        private readonly string _username;
        private readonly string _password;

        public XtreamClient(string server, string username, string password)
        {
            _server = server;
            _username = username;
            _password = password;
        }

        private string BaseUrl()
        {
            var s = _server.Trim();
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                s = "http://" + s;
            }
            return s.TrimEnd('/');
        }

        private string ApiUrl(string? action) =>
            $"{BaseUrl()}/player_api.php?username={Uri.EscapeDataString(_username)}" +
            $"&password={Uri.EscapeDataString(_password)}" +
            (action != null ? $"&action={action}" : "");

        /// <summary>URL of the full XMLTV EPG feed for this account (the server may gzip the body).</summary>
        public string EpgUrl() =>
            $"{BaseUrl()}/xmltv.php?username={Uri.EscapeDataString(_username)}&password={Uri.EscapeDataString(_password)}";

        /// <summary>
        /// Xtream's timeshift URLs are widely understood to expect the start time in the SERVER's own
        /// local time (not UTC) — this is the difference between the two, in seconds, found from a
        /// bare login call to player_api.php. Best-effort: 0 (i.e. assume the server is on UTC) if the
        /// panel doesn't report its clock or can't be reached, since a wrong catch-up time is much less
        /// disruptive than failing to build a URL at all.
        /// </summary>
        public async Task<int> FetchServerTimeOffsetSecondsAsync(CancellationToken ct = default)
        {
            try
            {
                var json = await Http.GetStringAsync(ApiUrl(null), ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("server_info", out var serverInfo) || serverInfo.ValueKind != JsonValueKind.Object)
                {
                    return 0;
                }

                var timestampNow = ReadString(serverInfo, "timestamp_now");
                if (timestampNow != null && long.TryParse(timestampNow, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochSeconds))
                {
                    return (int)(epochSeconds - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }

                var timeNow = ReadString(serverInfo, "time_now");
                if (timeNow != null && DateTime.TryParseExact(timeNow, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var localNow))
                {
                    var asUtc = DateTime.SpecifyKind(localNow, DateTimeKind.Utc);
                    return (int)(((DateTimeOffset)asUtc).ToUnixTimeSeconds() - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }

                return 0;
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or TaskCanceledException)
            {
                return 0;
            }
        }

        /// <summary>
        /// Builds a timeshift (catch-up) playback URL for one programme, per the widely-used Xtream
        /// convention: /timeshift/{user}/{pass}/{durationMinutes}/{yyyy-MM-dd:HH-mm}/{streamId}.ts,
        /// with the start time expressed in the SERVER's local time (see
        /// <see cref="FetchServerTimeOffsetSecondsAsync"/>).
        /// </summary>
        public string BuildCatchupUrl(string streamId, DateTimeOffset startUtc, int durationMinutes, int serverTimeOffsetSeconds)
        {
            var serverLocalStart = startUtc.AddSeconds(serverTimeOffsetSeconds);
            var startFormatted = serverLocalStart.ToString("yyyy-MM-dd:HH-mm", CultureInfo.InvariantCulture);
            return $"{BaseUrl()}/timeshift/{_username}/{_password}/{durationMinutes}/{startFormatted}/{streamId}.ts";
        }

        /// <summary>Fetches live categories + streams and builds a flat channel list with playable stream URLs.</summary>
        public async Task<IReadOnlyList<Channel>> FetchLiveChannelsAsync(CancellationToken ct = default)
        {
            var baseUrl = BaseUrl();

            // Categories are best-effort, same as Android: without them every channel is just "uncategorized".
            var categories = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var categoriesJson = await Http.GetStringAsync(ApiUrl("get_live_categories"), ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(categoriesJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in doc.RootElement.EnumerateArray())
                    {
                        var id = ReadString(c, "category_id");
                        var name = ReadString(c, "category_name");
                        if (id != null && name != null) categories.TryAdd(id, name);
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed categories — carry on without them.
            }

            var streamsJson = await Http.GetStringAsync(ApiUrl("get_live_streams"), ct).ConfigureAwait(false);

            JsonDocument streamsDoc;
            try
            {
                streamsDoc = JsonDocument.Parse(streamsJson);
            }
            catch (JsonException)
            {
                throw new XtreamException("The server didn't return a channel list. Check the server address, username and password.");
            }

            using (streamsDoc)
            {
                // A wrong login typically comes back as HTTP 200 with a JSON *object*
                // ({"user_info":{"auth":0}}) instead of the stream array.
                if (streamsDoc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new XtreamException("Login failed. Check the server address, username and password.");
                }

                var channels = new List<Channel>();
                foreach (var s in streamsDoc.RootElement.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;

                    var streamId = ReadString(s, "stream_id");
                    if (string.IsNullOrWhiteSpace(streamId)) continue;

                    var name = ReadString(s, "name");
                    var categoryId = ReadString(s, "category_id");
                    var epgChannelId = ReadString(s, "epg_channel_id");

                    var group = categoryId != null && categories.TryGetValue(categoryId, out var catName)
                        ? catName
                        : Channel.UngroupedGroupKey;
                    var streamUrl = $"{baseUrl}/live/{_username}/{_password}/{streamId}.ts";

                    // tv_archive is "1"/"0" (sometimes 1/0 as numbers); a flag with no usable duration
                    // is treated as no catch-up at all rather than an unbounded one.
                    var hasCatchupFlag = ReadString(s, "tv_archive") == "1";
                    var archiveDays = hasCatchupFlag && int.TryParse(ReadString(s, "tv_archive_duration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
                        ? days
                        : 0;

                    channels.Add(new Channel(
                        Id: string.IsNullOrWhiteSpace(epgChannelId) ? $"xt_{streamId}" : epgChannelId,
                        Key: streamUrl,
                        Name: string.IsNullOrWhiteSpace(name) ? $"Channel {streamId}" : name,
                        StreamUrl: streamUrl,
                        LogoUrl: ReadString(s, "stream_icon"),
                        GroupTitle: group,
                        StreamId: streamId,
                        HasCatchup: archiveDays > 0,
                        CatchupDurationDays: archiveDays));
                }
                return channels;
            }
        }

        /// <summary>
        /// Fetches VOD (movies) categories + streams as Channels (ContentType.Movie), same shape and
        /// naming convention as get_live_streams but for the panel's separate movie catalogue. Best-effort
        /// end to end: an account with no VOD entitlement, or an older panel that doesn't support these
        /// actions at all, just yields an empty list rather than failing the whole playlist load — unlike
        /// live channels, Movies is an optional extra tab, not the thing the user is waiting to watch.
        /// </summary>
        public async Task<IReadOnlyList<Channel>> FetchVodAsync(CancellationToken ct = default)
        {
            try
            {
                var baseUrl = BaseUrl();
                var categories = await FetchExtraCategoriesAsync("get_vod_categories", ct).ConfigureAwait(false);

                var streamsJson = await Http.GetStringAsync(ApiUrl("get_vod_streams"), ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(streamsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<Channel>();

                var movies = new List<Channel>();
                foreach (var s in doc.RootElement.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    var streamId = ReadString(s, "stream_id");
                    if (string.IsNullOrWhiteSpace(streamId)) continue;

                    var categoryId = ReadString(s, "category_id");
                    var group = categoryId != null && categories.TryGetValue(categoryId, out var catName)
                        ? catName
                        : Channel.UngroupedGroupKey;
                    var ext = ReadString(s, "container_extension");
                    if (string.IsNullOrWhiteSpace(ext)) ext = "mp4";
                    var streamUrl = $"{baseUrl}/movie/{_username}/{_password}/{streamId}.{ext}";

                    movies.Add(new Channel(
                        Id: $"xt_vod_{streamId}",
                        Key: streamUrl,
                        Name: ReadString(s, "name") ?? $"Movie {streamId}",
                        StreamUrl: streamUrl,
                        LogoUrl: ReadString(s, "stream_icon"),
                        GroupTitle: group,
                        StreamId: streamId,
                        ContentType: ContentType.Movie));
                }
                return movies;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                Debug.WriteLine($"Fetching Xtream VOD failed (treated as \"no movies\"): {ex}");
                return Array.Empty<Channel>();
            }
        }

        /// <summary>
        /// Fetches the panel's series catalogue as show tiles only — NOT their episodes, which are fetched
        /// separately and lazily (see <see cref="FetchSeriesEpisodesAsync"/>) only once a show is actually
        /// opened. Same best-effort philosophy as <see cref="FetchVodAsync"/>: no series entitlement or an
        /// unsupported panel just means an empty TV Shows tab, not a failed playlist load.
        /// </summary>
        public async Task<IReadOnlyList<ShowSummary>> FetchSeriesShowsAsync(CancellationToken ct = default)
        {
            try
            {
                var categories = await FetchExtraCategoriesAsync("get_series_categories", ct).ConfigureAwait(false);

                var json = await Http.GetStringAsync(ApiUrl("get_series"), ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<ShowSummary>();

                var shows = new List<ShowSummary>();
                foreach (var s in doc.RootElement.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    var seriesId = ReadString(s, "series_id");
                    if (string.IsNullOrWhiteSpace(seriesId)) continue;

                    var categoryId = ReadString(s, "category_id");
                    var group = categoryId != null && categories.TryGetValue(categoryId, out var catName)
                        ? catName
                        : Channel.UngroupedGroupKey;

                    shows.Add(new ShowSummary(
                        Id: seriesId,
                        Name: ReadString(s, "name") ?? $"Series {seriesId}",
                        CoverUrl: ReadString(s, "cover"),
                        GroupTitle: group,
                        IsXtreamSeries: true));
                }
                return shows;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                Debug.WriteLine($"Fetching Xtream series list failed (treated as \"no shows\"): {ex}");
                return Array.Empty<ShowSummary>();
            }
        }

        /// <summary>
        /// Fetches one series' full episode list (every season flattened into one list, sortable by
        /// SeasonNumber/EpisodeNumber) via get_series_info. Unlike <see cref="FetchVodAsync"/>/
        /// <see cref="FetchSeriesShowsAsync"/>, failures here are NOT swallowed — this runs on demand when
        /// the user opens a specific show, so the show window can tell them it failed and let them retry,
        /// rather than silently looking like a show with zero episodes.
        /// </summary>
        public async Task<IReadOnlyList<Channel>> FetchSeriesEpisodesAsync(string seriesId, CancellationToken ct = default)
        {
            var baseUrl = BaseUrl();
            var json = await Http.GetStringAsync($"{ApiUrl("get_series_info")}&series_id={Uri.EscapeDataString(seriesId)}", ct)
                .ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("episodes", out var episodesBySeasonNum) ||
                episodesBySeasonNum.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<Channel>();
            }

            var seriesName = doc.RootElement.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                ? ReadString(info, "name")
                : null;

            var episodes = new List<Channel>();
            foreach (var seasonProperty in episodesBySeasonNum.EnumerateObject())
            {
                if (seasonProperty.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in seasonProperty.Value.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var episodeId = ReadString(e, "id");
                    if (string.IsNullOrWhiteSpace(episodeId)) continue;

                    var ext = ReadString(e, "container_extension");
                    if (string.IsNullOrWhiteSpace(ext)) ext = "mp4";
                    var streamUrl = $"{baseUrl}/series/{_username}/{_password}/{episodeId}.{ext}";

                    // "season" on the episode itself is the documented field, but the object's own key
                    // (episodesBySeasonNum is keyed by season number) is a reliable fallback if a panel omits it.
                    if (!int.TryParse(ReadString(e, "season"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var season) || season <= 0)
                    {
                        int.TryParse(seasonProperty.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out season);
                    }
                    int.TryParse(ReadString(e, "episode_num"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var episodeNum);

                    episodes.Add(new Channel(
                        Id: $"xt_ep_{episodeId}",
                        Key: streamUrl,
                        Name: ReadString(e, "title") ?? $"Episode {episodeId}",
                        StreamUrl: streamUrl,
                        LogoUrl: null,
                        GroupTitle: Channel.UngroupedGroupKey,
                        StreamId: episodeId,
                        ContentType: ContentType.Series,
                        SeriesTitle: seriesName,
                        SeasonNumber: season > 0 ? season : null,
                        EpisodeNumber: episodeNum > 0 ? episodeNum : null));
                }
            }
            return episodes;
        }

        /// <summary>Shared by FetchVodAsync/FetchSeriesShowsAsync: category_id -> category_name, or empty
        /// on any failure (network, malformed JSON, unsupported action) — same "best-effort" contract as
        /// get_live_categories already has in FetchLiveChannelsAsync, just reused instead of duplicated.</summary>
        private async Task<Dictionary<string, string>> FetchExtraCategoriesAsync(string action, CancellationToken ct)
        {
            var categories = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var json = await Http.GetStringAsync(ApiUrl(action), ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in doc.RootElement.EnumerateArray())
                    {
                        var id = ReadString(c, "category_id");
                        var name = ReadString(c, "category_name");
                        if (id != null && name != null) categories.TryAdd(id, name);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                // Best-effort, same as live categories — without them everything just falls into "uncategorized".
            }
            return categories;
        }

        /// <summary>A property as a string whether the panel sent it as a JSON string or a number; null otherwise.</summary>
        private static string? ReadString(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
        }
    }

    public sealed class XtreamException : Exception
    {
        public XtreamException(string message) : base(message) { }
    }
}
