namespace ZecaTV.Models
{
    /// <summary>One programme entry from an XMLTV feed, already resolved to a guide channel id.</summary>
    public sealed record EpgProgramme(
        string ChannelId,
        string Title,
        string? Description,
        long StartMillis,
        long StopMillis);

    /// <summary>
    /// A named XMLTV feed the user added beyond whatever came bundled with the playlist itself.
    /// "primary" (see EpgStore.PrimarySourceId) is that bundled feed and isn't stored here.
    /// </summary>
    public sealed record EpgSource(string Id, string Name, string Url);

    /// <summary>
    /// Overrides which EPG feed + which channel id within it to use for now/next and the guide,
    /// for one specific playlist channel. Without a mapping, a channel defaults to its own id
    /// looked up across every loaded source (see EpgStore.Resolve).
    /// </summary>
    public sealed record EpgChannelMapping(
        /// <summary>A Channel's Key (its stream URL) — NOT Id, which quality variants often share.</summary>
        string ChannelKey,
        string EpgSourceId,
        string EpgChannelId);
}
