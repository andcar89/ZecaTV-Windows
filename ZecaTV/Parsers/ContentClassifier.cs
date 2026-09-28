using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ZecaTV.Models;

namespace ZecaTV.Parsers
{
    /// <summary>
    /// Best-effort guess at whether an M3U playlist entry is live TV, a movie, or a TV series episode —
    /// M3U has no real "type" field, unlike Xtream (which exposes movies/series through their own separate,
    /// unambiguous API calls; see XtreamClient.FetchVodAsync/FetchSeriesShowsAsync). Like the EPG fuzzy
    /// name-matcher (see EpgStore.MatchesAbbreviation), this is a genuine guess with guard rails, not a
    /// rule — it can occasionally get an entry wrong. There's no manual override for this yet (unlike the
    /// EPG matcher's "Assign channel EPG…"); if a provider's playlist consistently gets misclassified,
    /// that's a sign this heuristic needs a new rule, not that every entry needs a per-item override.
    ///
    /// Checked in order, most reliable first:
    ///  1. The stream URL's own path — Xtream always encodes /movie/ or /series/ in movie/episode URLs,
    ///     even when a provider flattens everything into one exported M3U, so this is effectively certain
    ///     whenever it matches.
    ///  2. An explicit tvg-type="movie"/"vod"/"series"/"tvshow" attribute — not every generator writes
    ///     one, but it's a direct declaration when present, so it outranks any guesswork below.
    ///  3. An "S01E04"-style (or "1x04") episode number in the channel name — a strong, format-level signal
    ///     for episodic content that doesn't depend on any particular group-title convention.
    ///  4. Group-title keywords (English + Portuguese) various generators use for a "Movies" or "Series"
    ///     category. This is the weakest signal here — see ClassifyAll's live-bouquet correction below.
    ///  5. A file extension in the URL that live streams essentially never use (.mp4, .mkv, ...) — the
    ///     last resort for a movie with no season/episode numbering and no helpful group-title.
    /// Anything matching none of these stays Live, which is also everything's default — so a playlist with
    /// no movies/series mixed in behaves exactly as before.
    /// </summary>
    public static class ContentClassifier
    {
        /// <param name="EpisodeTitle">The per-episode part of the name once the show title and episode
        /// number are stripped out (e.g. "Pilot" from "Breaking Bad - S01E01 - Pilot"), so a show's episode
        /// list doesn't repeat the show's own name on every row. Null when there was nothing left after
        /// stripping (or no episode number was found at all) — callers fall back to the raw name.</param>
        public readonly record struct Result(ContentType Type, string? SeriesTitle, int? Season, int? Episode, string? EpisodeTitle = null);

        // A "/" already delimits the path segment on both sides, so this is just a literal substring
        // check — no word-boundary guard needed (or wanted: the segment before "/series/" is virtually
        // always a hostname or "user/pass" ending in an ordinary letter, e.g. ".../pass/series/123.mp4").
        private static bool HasUrlPathSegment(string url, string segment) => url.Contains(segment, StringComparison.OrdinalIgnoreCase);

        // "S01E04", "S1E4", "S01.E04", "S01 E04" — the near-universal scene/panel convention.
        private static readonly Regex SxxExxPattern = new(
            @"(?<![0-9A-Za-z])S(?<season>\d{1,2})[\s._-]*[EX](?<episode>\d{1,3})(?![0-9])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "1x04" — common alternative, without an "S" prefix. Episode held to 2-3 digits and both sides
        // guarded against an adjacent digit, so a resolution tag like "1920x1080" can never match: at
        // every candidate position inside "1920x1080" the digits immediately before the "x" (or after it)
        // continue past what the {1,2}/{2,3} group would consume, so the adjacent-digit guard rejects it.
        private static readonly Regex NxNPattern = new(
            @"(?<![0-9A-Za-z])(?<season>\d{1,2})x(?<episode>\d{2,3})(?![0-9])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex MovieGroupKeywords = new(
            @"\b(movies?|filmes?|cinema|vod)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SeriesGroupKeywords = new(
            @"\b(series|séries|shows?|temporadas?|epis[oó]dios?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] VodExtensions = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".m4v" };

        public static Result Classify(string name, string groupTitle, string streamUrl, string? tvgType = null) =>
            Classify(name, groupTitle, streamUrl, tvgType, out _);

        /// <param name="viaGroupTitleOnly">True when nothing but the group-title keyword match (rule 4)
        /// produced this result — no URL path, no tvg-type, no episode number in the name backs it up.
        /// That's the weakest signal this classifier has, and <see cref="ClassifyAll"/> uses this flag to
        /// find and correct the one case it's known to get wrong: a live bouquet of channels whose
        /// category is named for the genre they show (e.g. "Filmes/Séries"), not for being on-demand.</param>
        private static Result Classify(string name, string groupTitle, string streamUrl, string? tvgType, out bool viaGroupTitleOnly)
        {
            viaGroupTitleOnly = false;

            if (HasUrlPathSegment(streamUrl, "/series/"))
            {
                var episode = TryParseEpisode(name);
                return AsSeries(episode, name, groupTitle);
            }

            if (HasUrlPathSegment(streamUrl, "/movie/") || HasUrlPathSegment(streamUrl, "/movies/"))
            {
                return new Result(ContentType.Movie, null, null, null);
            }

            switch (tvgType?.Trim().ToLowerInvariant())
            {
                case "movie" or "vod":
                    return new Result(ContentType.Movie, null, null, null);
                case "series" or "tvshow" or "show":
                    return AsSeries(TryParseEpisode(name), name, groupTitle);
            }

            var parsed = TryParseEpisode(name);
            if (parsed != null)
            {
                return AsSeries(parsed, name, groupTitle);
            }

            if (SeriesGroupKeywords.IsMatch(groupTitle))
            {
                viaGroupTitleOnly = true;
                return new Result(ContentType.Series, FallbackTitle(name, groupTitle), null, null);
            }

            if (MovieGroupKeywords.IsMatch(groupTitle))
            {
                viaGroupTitleOnly = true;
                return new Result(ContentType.Movie, null, null, null);
            }

            if (HasVodExtension(streamUrl))
            {
                return new Result(ContentType.Movie, null, null, null);
            }

            return new Result(ContentType.Live, null, null, null);
        }

        /// <param name="Name">Raw #EXTINF display name.</param>
        /// <param name="GroupTitle">Raw group-title attribute.</param>
        /// <param name="StreamUrl">The entry's stream URL.</param>
        /// <param name="TvgType">Raw tvg-type attribute, if any.</param>
        public readonly record struct Entry(string Name, string GroupTitle, string StreamUrl, string? TvgType);

        /// <summary>
        /// Classifies a whole playlist at once (in order — results line up with <paramref name="entries"/>
        /// index for index), then corrects one known failure mode of rule 4 above: some providers give a
        /// live bouquet of movie/drama-genre channels — TVCine Action/Edition/Emotion, Fox, AMC, and
        /// similar — a category named for what they show (e.g. "Filmes/Séries", "Portugal Series") rather
        /// than for being on-demand. Classified alone, "PT: TvCine Action HD" has nothing but that category
        /// name to go on, so rule 4 calls it a show.
        ///
        /// The giveaway only shows up once you look at the category as a whole: an on-demand movie or
        /// episode is published once, but a live channel is typically offered as several playlist entries,
        /// one per resolution — "TvCine Action", "TvCine Action HD", "TvCine Action FHD", "...HEVC". So
        /// among entries a category earned only through rule 4 (no stronger signal at all), whichever base
        /// names (after stripping quality tags) repeat two or more times get corrected back to Live — but
        /// only once the category shows at least two different channels doing this, since one coincidental
        /// duplicate (say, the same movie listed twice) isn't enough to call the pattern deliberate. This
        /// is deliberately scoped to just the repeating rows: a single genuine movie or show sharing a
        /// category with a mislabeled bouquet — unique title, no quality-suffix siblings — is left alone,
        /// as is a category reached via real episode numbering (rule 3, never rule 4) however it repeats.
        /// </summary>
        public static IReadOnlyList<Result> ClassifyAll(IReadOnlyList<Entry> entries)
        {
            var results = new Result[entries.Count];
            var fallbackByGroup = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                results[i] = Classify(e.Name, e.GroupTitle, e.StreamUrl, e.TvgType, out var viaGroupTitleOnly);
                if (!viaGroupTitleOnly) continue;

                if (!fallbackByGroup.TryGetValue(e.GroupTitle, out var indices))
                {
                    fallbackByGroup[e.GroupTitle] = indices = new List<int>();
                }
                indices.Add(i);
            }

            foreach (var indices in fallbackByGroup.Values)
            {
                foreach (var i in FindLiveBouquetRows(entries, indices))
                {
                    results[i] = new Result(ContentType.Live, null, null, null);
                }
            }

            return results;
        }

        /// <summary>Within one category's group-title-only entries, groups them by base name (quality tags
        /// stripped) and returns just the rows belonging to a base name that repeats — but only once at
        /// least two distinct base names do, since a single repeated title on its own isn't a reliable
        /// enough signal to correct anything.</summary>
        private static IEnumerable<int> FindLiveBouquetRows(IReadOnlyList<Entry> entries, List<int> indices)
        {
            var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var i in indices)
            {
                var baseName = ChannelNameNormalizer.Normalize(entries[i].Name);
                if (baseName.Length == 0) continue;
                if (!groups.TryGetValue(baseName, out var list)) groups[baseName] = list = new List<int>();
                list.Add(i);
            }

            var repeated = groups.Values.Where(g => g.Count >= 2).ToList();
            return repeated.Count >= 2 ? repeated.SelectMany(g => g) : Enumerable.Empty<int>();
        }

        private static string FallbackTitle(string name, string groupTitle) =>
            string.IsNullOrWhiteSpace(name) ? groupTitle : name;

        private static Result AsSeries(EpisodeMatch? episode, string name, string groupTitle) => new(
            ContentType.Series,
            episode?.Title ?? FallbackTitle(name, groupTitle),
            episode?.Season,
            episode?.Episode,
            episode?.EpisodeTitle);

        private readonly record struct EpisodeMatch(string? Title, int Season, int Episode, string? EpisodeTitle);

        private static EpisodeMatch? TryParseEpisode(string name)
        {
            var match = SxxExxPattern.Match(name);
            if (!match.Success) match = NxNPattern.Match(name);
            if (!match.Success) return null;

            if (!int.TryParse(match.Groups["season"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var season) ||
                !int.TryParse(match.Groups["episode"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var episode))
            {
                return null;
            }

            var title = TrimTrailingSeparators(name[..match.Index]);
            var episodeTitle = TrimLeadingSeparators(name[(match.Index + match.Length)..]);
            return new EpisodeMatch(
                string.IsNullOrEmpty(title) ? null : title,
                season,
                episode,
                string.IsNullOrEmpty(episodeTitle) ? null : episodeTitle);
        }

        private static string TrimTrailingSeparators(string title) => title.Trim().TrimEnd('-', '_', '.', '(', '[').TrimEnd();

        private static string TrimLeadingSeparators(string title) => title.Trim().TrimStart('-', '_', '.', ')', ']').TrimStart();

        private static bool HasVodExtension(string url)
        {
            var withoutQuery = url;
            var queryIdx = withoutQuery.IndexOf('?');
            if (queryIdx >= 0) withoutQuery = withoutQuery[..queryIdx];

            foreach (var ext in VodExtensions)
            {
                if (withoutQuery.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
