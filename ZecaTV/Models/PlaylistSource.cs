namespace ZecaTV.Models
{
    public enum SourceType
    {
        M3uUrl,
        M3uFile,
        Xtream
    }

    /// <summary>
    /// Where the channel list comes from. One flat record (rather than the Android app's sealed
    /// class hierarchy) so it round-trips through settings.json without custom converters; only the
    /// fields relevant to <see cref="Type"/> are filled in.
    /// </summary>
    public sealed record PlaylistSource
    {
        public SourceType Type { get; init; }

        /// <summary>M3uUrl: playlist URL.</summary>
        public string? Url { get; init; }

        /// <summary>M3uFile: full path to a local .m3u/.m3u8 file.</summary>
        public string? FilePath { get; init; }

        /// <summary>M3U only: optional EPG URL typed by the user (wins over the playlist's own url-tvg).</summary>
        public string? EpgUrl { get; init; }

        /// <summary>Xtream: server address, with or without http(s):// and port.</summary>
        public string? Server { get; init; }

        public string? Username { get; init; }

        public string? Password { get; init; }

        public static PlaylistSource FromM3uUrl(string url, string? epgUrl) =>
            new() { Type = SourceType.M3uUrl, Url = url, EpgUrl = epgUrl };

        public static PlaylistSource FromM3uFile(string filePath, string? epgUrl) =>
            new() { Type = SourceType.M3uFile, FilePath = filePath, EpgUrl = epgUrl };

        public static PlaylistSource FromXtream(string server, string username, string password) =>
            new() { Type = SourceType.Xtream, Server = server, Username = username, Password = password };
    }
}
