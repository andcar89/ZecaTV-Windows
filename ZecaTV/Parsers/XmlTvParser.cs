using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using ZecaTV.Models;

namespace ZecaTV.Parsers
{
    /// <summary>Result of parsing an XMLTV document: programmes grouped by channel id, plus id -> display-name.</summary>
    public sealed record EpgParseResult(
        IReadOnlyDictionary<string, List<EpgProgramme>> ProgrammesByChannelId,
        IReadOnlyDictionary<string, string> ChannelNames);

    /// <summary>
    /// Streams an XMLTV EPG document and groups programmes by the "channel" attribute on
    /// &lt;programme&gt; (matched against Channel.Id — tvg-id / epg_channel_id — or a user mapping;
    /// see EpgStore). Also collects each &lt;channel id="..."&gt;&lt;display-name&gt; so the mapping UI
    /// can show human-readable names. Line-for-line port of the Android app's EpgXmlParser.kt.
    /// </summary>
    public static class XmlTvParser
    {
        // XMLTV timestamps look like "20240115120000 +0000" — a fixed-width digit block, optionally
        // followed by a UTC offset. Parsed manually rather than via DateTime.Parse/TryParseExact,
        // whose offset handling can vary by format string and locale settings and, on a parse
        // failure, either throws or silently uses an unintended timezone — either way risking the
        // same "next works, now doesn't" symptom the Android version's SimpleDateFormat had. See
        // ParseDate() below, ported exactly.

        public static EpgParseResult Parse(Stream rawStream, bool isGzip)
        {
            using Stream input = isGzip ? new GZipStream(rawStream, CompressionMode.Decompress) : rawStream;
            var programmes = new Dictionary<string, List<EpgProgramme>>(StringComparer.Ordinal);
            var channelNames = new Dictionary<string, string>(StringComparer.Ordinal);

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore, // some feeds reference a DTD that isn't fetchable
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                CheckCharacters = false // tolerate the odd invalid XML char rather than aborting the whole feed
            };
            using var reader = XmlReader.Create(input, settings);

            string? currentChannelId = null;
            string? currentTitle = null;
            string? currentDesc = null;
            long currentStart = 0;
            long currentStop = 0;
            var inTitle = false;
            var inDesc = false;

            var inChannelTag = false;
            string? currentChannelTagId = null;
            var inDisplayName = false;
            string? currentDisplayName = null;

            try
            {
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        switch (reader.LocalName)
                        {
                            case "channel":
                                inChannelTag = true;
                                currentChannelTagId = reader.GetAttribute("id");
                                currentDisplayName = null;
                                break;
                            case "display-name":
                                // A channel can carry several <display-name> tags (a number, a
                                // localized name, ...); only the first is wanted, but "first" means
                                // the first with actual text, so an empty one doesn't block a good
                                // name in a later tag. Capturing text ONLY while currentDisplayName
                                // is still unset (rather than resetting per tag and keeping the LAST
                                // one) is what makes that "first" — and, just as importantly, what
                                // stops every subsequent tag's text from being appended onto it.
                                if (inChannelTag) inDisplayName = currentDisplayName == null;
                                break;
                            case "programme":
                                currentChannelId = reader.GetAttribute("channel");
                                currentStart = ParseDate(reader.GetAttribute("start"));
                                currentStop = ParseDate(reader.GetAttribute("stop"));
                                currentTitle = null;
                                currentDesc = null;
                                break;
                            case "title":
                                inTitle = true;
                                break;
                            case "desc":
                                inDesc = true;
                                break;
                        }

                        // <title/> etc: XmlReader gives ONE Element node for a self-closing tag and
                        // never raises a matching EndElement, so a flag opened above would otherwise
                        // stay stuck on for the rest of the document. Close it right back down.
                        if (reader.IsEmptyElement)
                        {
                            switch (reader.LocalName)
                            {
                                case "title": inTitle = false; break;
                                case "desc": inDesc = false; break;
                                case "display-name": inDisplayName = false; break;
                                case "channel": inChannelTag = false; break;
                            }
                        }
                    }
                    else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                    {
                        if (inTitle) currentTitle = (currentTitle ?? "") + reader.Value;
                        if (inDesc) currentDesc = (currentDesc ?? "") + reader.Value;
                        if (inDisplayName) currentDisplayName = (currentDisplayName ?? "") + reader.Value;
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement)
                    {
                        switch (reader.LocalName)
                        {
                            case "title": inTitle = false; break;
                            case "desc": inDesc = false; break;
                            case "display-name": inDisplayName = false; break;
                            case "channel":
                                if (currentChannelTagId != null && currentDisplayName != null)
                                {
                                    // Only the first display-name we see per channel id.
                                    channelNames.TryAdd(currentChannelTagId, currentDisplayName);
                                }
                                inChannelTag = false;
                                break;
                            case "programme":
                                if (currentChannelId != null && currentTitle != null)
                                {
                                    if (!programmes.TryGetValue(currentChannelId, out var list))
                                    {
                                        list = new List<EpgProgramme>();
                                        programmes[currentChannelId] = list;
                                    }
                                    list.Add(new EpgProgramme(currentChannelId, currentTitle, currentDesc, currentStart, currentStop));
                                }
                                break;
                        }
                    }
                }
            }
            catch (XmlException) when (programmes.Count > 0 || channelNames.Count > 0)
            {
                // A feed that's truncated (connection cut short) or has one malformed tag near the
                // end: keep whatever was successfully parsed before the error rather than discarding
                // an otherwise-good guide. A feed broken from the start still throws (both maps empty
                // means there's nothing worth keeping, and the caller should see the failure).
            }

            // Some providers omit "stop" (technically required by the XMLTV spec, but not everyone
            // complies) — a programme with no stop time defaults to StopMillis=0, which means it can
            // never be "now" (nothing real falls between a valid start and epoch zero) even though
            // its start time is perfectly valid. It still shows up fine as "next" since that only
            // checks the start time — exactly the "next works, now doesn't" symptom this fixes. Infer
            // the missing stop from the next programme's start time per channel, or a default 2h
            // duration for a channel's last known programme. Sorting here is also what lets EpgStore's
            // binary searches assume chronological order.
            var fixedProgrammes = new Dictionary<string, List<EpgProgramme>>(StringComparer.Ordinal);
            foreach (var (channelId, list) in programmes)
            {
                var sorted = list.OrderBy(p => p.StartMillis).ToList();
                for (var i = 0; i < sorted.Count; i++)
                {
                    var p = sorted[i];
                    if (p.StopMillis <= p.StartMillis)
                    {
                        var inferredStop = i + 1 < sorted.Count ? sorted[i + 1].StartMillis : p.StartMillis + 2 * 60 * 60 * 1000L;
                        sorted[i] = p with { StopMillis = inferredStop };
                    }
                }
                fixedProgrammes[channelId] = sorted;
            }

            return new EpgParseResult(fixedProgrammes, channelNames);
        }

        /// <summary>
        /// Parses an XMLTV timestamp like "20240115120000 +0000" (or "-0500", or with no offset at
        /// all, in which case UTC is assumed) into Unix epoch milliseconds. Plain substring/int
        /// parsing, deliberately not DateTime.Parse, so the result never depends on the OS's local
        /// timezone or culture settings.
        /// </summary>
        private static long ParseDate(string? value)
        {
            if (value == null || value.Length < 14) return 0;
            try
            {
                var year = int.Parse(value.AsSpan(0, 4));
                var month = int.Parse(value.AsSpan(4, 2));
                var day = int.Parse(value.AsSpan(6, 2));
                var hour = int.Parse(value.AsSpan(8, 2));
                var minute = int.Parse(value.AsSpan(10, 2));
                var second = int.Parse(value.AsSpan(12, 2));

                var utc = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
                var offsetPart = value.Length > 14 ? value[14..].Trim() : "";
                var epochMillis = new DateTimeOffset(utc).ToUnixTimeMilliseconds();
                return epochMillis - ParseOffsetMillis(offsetPart);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException)
            {
                return 0;
            }
        }

        /// <summary>Parses a "+HHMM" / "-HHMM" UTC offset into millis; returns 0 (UTC) if absent/malformed.</summary>
        private static long ParseOffsetMillis(string offsetPart)
        {
            if (offsetPart.Length < 5) return 0;
            var sign = offsetPart[0] == '-' ? -1 : 1;
            if (!int.TryParse(offsetPart.AsSpan(1, 2), out var hours)) return 0;
            _ = int.TryParse(offsetPart.AsSpan(3, 2), out var minutes); // malformed minutes -> :00, like Kotlin's toIntOrNull() ?: 0
            return sign * (hours * 3_600_000L + minutes * 60_000L);
        }
    }
}
