namespace ZecaTV.Models
{
    /// <summary>
    /// One browsable "show" tile in the TV Shows tab. Two different origins end up looking the same here:
    ///  - A real Xtream series (<see cref="IsXtreamSeries"/> true): <see cref="Id"/> is the panel's own
    ///    series_id, and its episodes aren't known yet — they're fetched on demand (XtreamClient.FetchSeriesEpisodesAsync)
    ///    only once the user actually opens this show, since eagerly fetching every series' episode list
    ///    up front would mean one extra request per show at playlist-load time.
    ///  - A show synthesized from an M3U playlist (<see cref="IsXtreamSeries"/> false): <see cref="Id"/> is
    ///    the guessed SeriesTitle text itself, and its episodes are just whichever already-loaded channels
    ///    share that SeriesTitle (see PlaylistRepository.EpisodesForLocalShow) — nothing more to fetch.
    /// </summary>
    public sealed record ShowSummary(
        string Id,
        string Name,
        string? CoverUrl,
        string GroupTitle,
        bool IsXtreamSeries);
}
