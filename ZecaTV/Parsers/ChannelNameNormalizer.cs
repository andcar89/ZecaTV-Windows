using System.Text.RegularExpressions;

namespace ZecaTV.Parsers
{
    /// <summary>
    /// Reduces a channel name to just its "identity" — shared by EpgStore's fuzzy guide matching and
    /// ContentClassifier's live-bouquet detection, both of which need to recognize that "PT: TvCine
    /// Action", "TvCine Action HD" and "TVCINE ACTION FHD" are all the same underlying thing.
    /// </summary>
    public static class ChannelNameNormalizer
    {
        private static readonly Regex PrefixRegex = new(@"^[a-z]{2,4}\s*[:\-]\s*", RegexOptions.Compiled);
        private static readonly Regex QualityRegex = new(@"\b(hd|fhd|full ?hd|uhd|4k|sd|hevc)\b", RegexOptions.Compiled);
        private static readonly Regex QualitySuffixRegex = new(@"(hd|fhd|fullhd|uhd|4k|sd|hevc)$", RegexOptions.Compiled);
        private static readonly Regex NonAlnumRegex = new(@"[^a-z0-9]", RegexOptions.Compiled);

        /// <summary>
        /// Lowercase, strip a leading country/provider prefix like "PT: " or "PT - ", strip common
        /// quality/resolution tokens (HD, Full HD, 4K, etc.), then strip everything but letters and
        /// digits. Turns both "PT - RTP 1" and "RTP1.pt"'s display name "RTP 1" into the same "rtp1",
        /// regardless of which provider's formatting convention produced either string.
        /// </summary>
        public static string Normalize(string name)
        {
            var n = name.ToLowerInvariant();
            n = PrefixRegex.Replace(n, "");
            n = QualityRegex.Replace(n, "");
            // QualityRegex needs a word boundary before the tag, which a digit right next to it (no
            // space) doesn't provide — "SPT1HD" has no boundary before "hd". This second, narrower
            // pass only looks at the very end of the string, so it catches that glued-on case too.
            n = QualitySuffixRegex.Replace(n, "");
            n = NonAlnumRegex.Replace(n, "");
            return n;
        }
    }
}
