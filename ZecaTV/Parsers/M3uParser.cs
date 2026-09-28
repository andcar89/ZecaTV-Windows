using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ZecaTV.Models;

namespace ZecaTV.Parsers
{
    /// <summary>
    /// Parses standard extended M3U playlists as used by IPTV services, e.g.:
    ///
    ///   #EXTM3U url-tvg="http://server/epg.xml"
    ///   #EXTINF:-1 tvg-id="bbc1" tvg-logo="http://.../bbc1.png" group-title="UK",BBC One
    ///   http://server/live/user/pass/12345.ts
    ///
    /// Line-for-line port of the Android app's M3uParser.kt, so both apps produce the same channel
    /// list (same names, groups, ids and keys) from the same playlist.
    /// </summary>
    public static class M3uParser
    {
        private static readonly Regex AttrRegex = new("([a-zA-Z0-9_-]+)=\"([^\"]*)\"", RegexOptions.Compiled);

        /// <param name="Channels">Parsed channels in playlist order.</param>
        /// <param name="EpgUrls">
        /// Companion EPG URL(s) the playlist declares on its #EXTM3U header line (checking
        /// "url-tvg", "x-tvg-url" and "tvg-url" — different generators use different names), or
        /// empty if none. Some generators (e.g. the user's own M3U Editor) pack several feeds into
        /// one attribute as a comma-separated list — "url1,url2,url3" — which is split back out here,
        /// so each URL ends up as its own independently-loaded EPG source; see EpgStore.
        /// </param>
        public sealed record Result(IReadOnlyList<Channel> Channels, IReadOnlyList<string> EpgUrls);

        /// <summary>Splits a comma-separated list of URLs (as used by both the #EXTM3U header attribute and
        /// the manual "EPG URL" override field) into trimmed, non-empty entries. Also accepts a single URL.</summary>
        public static IReadOnlyList<string> SplitEpgUrls(string? raw) =>
            string.IsNullOrWhiteSpace(raw)
                ? Array.Empty<string>()
                : raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        public static Result Parse(Stream input)
        {
            using var reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return Parse(reader);
        }

        /// <summary>Everything about one playlist entry except its classification — gathered in a first
        /// pass so <see cref="ContentClassifier.ClassifyAll"/> can look at a whole category's entries
        /// together (see its doc comment) before any Channel is built.</summary>
        private readonly record struct RawEntry(string Id, string Name, string StreamUrl, string? Logo, string GroupTitle, string? TvgType);

        public static Result Parse(TextReader reader)
        {
            var raw = new List<RawEntry>();
            IReadOnlyList<string> headerEpgUrls = Array.Empty<string>();

            var pendingName = "";
            string? pendingLogo = null;
            var pendingGroup = Channel.UngroupedGroupKey;
            var pendingTvgId = "";
            string? pendingTvgType = null;
            var index = 0;

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                {
                    var attrs = ParseAttributes(trimmed);
                    var url = Get(attrs, "url-tvg") ?? Get(attrs, "x-tvg-url") ?? Get(attrs, "tvg-url");
                    headerEpgUrls = SplitEpgUrls(url);
                }
                else if (trimmed.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                {
                    var attrs = ParseAttributes(trimmed);
                    pendingLogo = Get(attrs, "tvg-logo");
                    var group = Get(attrs, "group-title");
                    pendingGroup = string.IsNullOrWhiteSpace(group) ? Channel.UngroupedGroupKey : group;
                    pendingTvgId = Get(attrs, "tvg-id") ?? "";
                    pendingTvgType = Get(attrs, "tvg-type");

                    var commaIdx = trimmed.LastIndexOf(',');
                    pendingName = commaIdx != -1 && commaIdx < trimmed.Length - 1
                        ? trimmed.Substring(commaIdx + 1).Trim()
                        : Get(attrs, "tvg-name") ?? $"Channel {index + 1}";
                }
                else if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                {
                    // Stream URL line — this closes out the channel we were building.
                    var id = string.IsNullOrWhiteSpace(pendingTvgId)
                        ? $"gen_{index}_{JavaStringHash(pendingName)}"
                        : pendingTvgId;
                    var name = string.IsNullOrWhiteSpace(pendingName) ? $"Channel {index + 1}" : pendingName;
                    raw.Add(new RawEntry(id, name, trimmed, pendingLogo, pendingGroup, pendingTvgType));

                    index++;
                    pendingName = "";
                    pendingLogo = null;
                    pendingGroup = Channel.UngroupedGroupKey;
                    pendingTvgId = "";
                    pendingTvgType = null;
                }
            }

            var classifications = ContentClassifier.ClassifyAll(
                raw.Select(r => new ContentClassifier.Entry(r.Name, r.GroupTitle, r.StreamUrl, r.TvgType)).ToList());

            var channels = new List<Channel>(raw.Count);
            for (var i = 0; i < raw.Count; i++)
            {
                var r = raw[i];
                var classified = classifications[i];
                // For a series episode, show just its own title ("Pilot") in the episode list rather
                // than repeating the show's name on every row ("Breaking Bad - S01E01 - Pilot") — but
                // only when there actually was something left after stripping the show/episode-number
                // part out; otherwise the full raw name is the best label there is.
                var displayName = classified is { Type: ContentType.Series, EpisodeTitle: { Length: > 0 } episodeTitle }
                    ? episodeTitle
                    : r.Name;

                channels.Add(new Channel(
                    Id: r.Id,
                    Key: r.StreamUrl,
                    Name: displayName,
                    StreamUrl: r.StreamUrl,
                    LogoUrl: r.Logo,
                    GroupTitle: r.GroupTitle,
                    ContentType: classified.Type,
                    SeriesTitle: classified.SeriesTitle,
                    SeasonNumber: classified.Season,
                    EpisodeNumber: classified.Episode));
            }

            return new Result(channels, headerEpgUrls);
        }

        /// <summary>All name="value" pairs on a line. Later duplicates win, like Kotlin's associate().</summary>
        private static Dictionary<string, string> ParseAttributes(string line)
        {
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in AttrRegex.Matches(line))
            {
                attrs[m.Groups[1].Value] = m.Groups[2].Value;
            }
            return attrs;
        }

        private static string? Get(Dictionary<string, string> attrs, string key) =>
            attrs.TryGetValue(key, out var value) ? value : null;

        /// <summary>
        /// Java's String.hashCode(). .NET's string.GetHashCode() is randomized per process, which would
        /// make generated ids differ on every launch; this keeps them stable (and identical to Android's).
        /// </summary>
        private static int JavaStringHash(string s)
        {
            var h = 0;
            unchecked
            {
                foreach (var c in s) h = 31 * h + c;
            }
            return h;
        }
    }
}
