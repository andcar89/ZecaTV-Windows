namespace ZecaTV.Models
{
    /// <summary>
    /// What kind of thing a <see cref="Channel"/> record actually is — most playlists are pure live TV,
    /// but some providers bundle movies and TV series episodes into the same list (or, for Xtream, expose
    /// them as separate but equally "channel-shaped" API endpoints). A TV-series episode is still just one
    /// playable stream, so it's represented as a Channel too (see Channel.SeriesTitle/SeasonNumber/
    /// EpisodeNumber) rather than as a whole separate model.
    /// </summary>
    public enum ContentType
    {
        Live,
        Movie,
        Series
    }
}
