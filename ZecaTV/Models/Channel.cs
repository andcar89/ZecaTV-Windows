namespace ZecaTV.Models
{
    /// <summary>
    /// A single playable channel, whether it came from an M3U playlist or an Xtream portal.
    ///
    /// <see cref="Id"/> is used to match this channel against EPG data (tvg-id in M3U, epg_channel_id
    /// in Xtream). Providers routinely give the SAME id to multiple quality variants of one channel
    /// ("RTP 1", "RTP 1 HD", "RTP 1 Full HD" all sharing one tvg-id) so they resolve to the same guide
    /// entry — correct for EPG, but it means Id must NEVER be used to identify a specific channel entry
    /// (favorites, per-channel EPG mapping overrides, "which channel is playing"...). Use
    /// <see cref="Key"/> for all of that instead: it's the stream URL, unique per entry.
    /// (Same rule as the Android app — see its favorites bug.)
    /// </summary>
    public sealed record Channel(
        string Id,
        string Key,
        string Name,
        string StreamUrl,
        string? LogoUrl,
        string GroupTitle,
        string? StreamId = null,
        /// <summary>Xtream only: true if this channel's account/stream has catch-up (timeshift) enabled
        /// with a usable retention window. M3U playlists have no catch-up concept, so this is always
        /// false for them.</summary>
        bool HasCatchup = false,
        /// <summary>Xtream only: how many days of catch-up the panel reports for this channel. 0 when
        /// <see cref="HasCatchup"/> is false.</summary>
        int CatchupDurationDays = 0,
        /// <summary>Live TV (the default — every existing playlist/channel is this), a movie, or one
        /// episode of a TV series. For M3U playlists this comes from ZecaTV.Parsers.ContentClassifier's
        /// best-effort guess; for Xtream it's known for certain, since the panel exposes movies and series
        /// through their own dedicated API calls.</summary>
        ContentType ContentType = ContentType.Live,
        /// <summary>Series only: the show's title with the episode-number part stripped out (e.g. "Breaking
        /// Bad" from "Breaking Bad S01E04"), used to group episodes into one show tile in the TV Shows tab.
        /// For Xtream, this is the series' own name and <see cref="Id"/> is its numeric series_id.</summary>
        string? SeriesTitle = null,
        /// <summary>Series only: season number, if the name/API told us one (used to sort/group episodes).</summary>
        int? SeasonNumber = null,
        /// <summary>Series only: episode number within its season, if known.</summary>
        int? EpisodeNumber = null)
    {
        /// <summary>
        /// Sentinel GroupTitle used when a channel's source doesn't specify a real category.
        /// Never shown raw — PlaylistRepository.Categories() maps it to a display label. Kept
        /// distinct from the synthetic "All channels" category so the two never appear as separate,
        /// confusingly similar sidebar entries.
        /// </summary>
        public const string UngroupedGroupKey = "__ungrouped__";
    }
}
