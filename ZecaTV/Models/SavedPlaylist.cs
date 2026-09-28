using System;

namespace ZecaTV.Models
{
    /// <summary>
    /// One saved, named playlist source. The app can hold several; exactly one is "active" (its
    /// channels loaded and browsable) at a time — see AppSettings.ActivePlaylistId. Favorites and
    /// EPG data are shared across every saved playlist (keyed by Channel.Key / stream URL, which is
    /// already globally unique), so switching never loses a favorite or a channel-EPG mapping.
    /// </summary>
    public sealed class SavedPlaylist
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public required PlaylistSource Source { get; set; }

        /// <summary>Category shown while this playlist was last active; restored when you switch back to it.</summary>
        public string? LastCategoryId { get; set; }
    }

    /// <summary>Suggests a sensible default name for a playlist source, used when the user leaves the name field blank.</summary>
    public static class PlaylistNaming
    {
        public static string Suggest(PlaylistSource source) => source.Type switch
        {
            SourceType.M3uUrl => TryGetHost(source.Url) ?? "M3U Playlist",
            SourceType.M3uFile => TryGetFileName(source.FilePath) ?? "M3U Playlist",
            SourceType.Xtream => string.IsNullOrWhiteSpace(source.Server) ? "Xtream Playlist" : source.Server,
            _ => "Playlist"
        };

        private static string? TryGetHost(string? url) =>
            Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ? uri.Host : null;

        private static string? TryGetFileName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            // Manual split on either separator, rather than System.IO.Path (whose separator handling
            // is OS-dependent — backslash isn't a separator to Path on non-Windows), so a Windows-style
            // path is parsed the same way regardless of which OS actually runs this.
            var trimmed = path.Trim();
            var lastSeparator = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            var fileName = lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
            var dot = fileName.LastIndexOf('.');
            var nameOnly = dot > 0 ? fileName[..dot] : fileName; // dot>0, not >=0: a leading-dot name isn't emptied
            return string.IsNullOrWhiteSpace(nameOnly) ? null : nameOnly;
        }
    }
}
