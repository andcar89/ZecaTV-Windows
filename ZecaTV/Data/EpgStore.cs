using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ZecaTV.Localization;
using ZecaTV.Models;
using ZecaTV.Network;
using ZecaTV.Parsers;

namespace ZecaTV.Data
{
    /// <summary>
    /// EPG data for the current playlist, kept per source (port of the EPG half of Android's
    /// PlaylistRepository, plus EpgSourceManager and EpgMappingStore folded in). The "primary"
    /// source is whatever XMLTV URL came bundled with the playlist/Xtream config; any number of
    /// extra feeds can be added by the user and are persisted in settings.json. Each channel
    /// resolves to a (sourceId, epgChannelId) pair via an explicit mapping override, or by default
    /// its own id looked up across every loaded source — see <see cref="Resolve"/>.
    ///
    /// All mutation happens after an await with the default (UI-thread) SynchronizationContext, so
    /// — like ChannelItem's logo loading — no locking is needed: everything here runs on the UI
    /// thread, just spread across several yields while a feed downloads and parses.
    /// </summary>
    public sealed class EpgStore
    {
        public static EpgStore Instance { get; } = new();

        /// <summary>Reserved id for the FIRST EPG URL bundled with the playlist/Xtream config (not stored in
        /// settings). An M3U can declare more than one (comma-separated in its url-tvg header); each extra
        /// one beyond the first gets its own id, "primary:1", "primary:2", ... — see <see cref="PrimarySourceIdAt"/>.
        /// Kept unchanged for index 0 so existing per-channel mappings/active-source overrides saved by
        /// earlier versions (back when there was only ever one primary source) still resolve correctly.</summary>
        public const string PrimarySourceId = "primary";

        private static string PrimarySourceIdAt(int index) => index == 0 ? PrimarySourceId : $"{PrimarySourceId}:{index}";

        private static int PrimaryIndexOf(string sourceId)
        {
            if (sourceId == PrimarySourceId) return 0;
            if (sourceId.StartsWith(PrimarySourceId + ":", StringComparison.Ordinal) &&
                int.TryParse(sourceId.AsSpan(PrimarySourceId.Length + 1), out var index) && index > 0)
            {
                return index;
            }
            return -1;
        }

        /// <summary>True if <paramref name="sourceId"/> names one of the playlist-bundled sources (as opposed
        /// to a user-added extra one) — used by the EPG sources screen to hide the "remove" action for it.</summary>
        public static bool IsPrimarySourceId(string sourceId) => PrimaryIndexOf(sourceId) >= 0;

        private sealed record SourceData(
            Dictionary<string, List<EpgProgramme>> ProgrammesByChannelId,
            Dictionary<string, string> ChannelNames,
            /// <summary>Normalized display-name -> epgChannelId, precomputed once per load so the fuzzy-match
            /// fallback in Resolve stays a cheap hashmap lookup even with hundreds of channels.</summary>
            Dictionary<string, string> NormalizedNameIndex);

        private readonly Dictionary<string, SourceData> _dataBySource = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _loadErrors = new(StringComparer.Ordinal);

        public IReadOnlyList<string> PrimaryEpgUrls { get; private set; } = Array.Empty<string>();

        /// <summary>Raised (on the UI thread) whenever any source finishes loading, fails, or a mapping changes.</summary>
        public event EventHandler? Changed;

        private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        // ---- Loading -------------------------------------------------------------------------

        /// <summary>Forgets all loaded EPG data — call when a new playlist is applied, before ReloadAllAsync.</summary>
        public void Reset(IReadOnlyList<string> primaryEpgUrls)
        {
            PrimaryEpgUrls = primaryEpgUrls;
            _dataBySource.Clear();
            _loadErrors.Clear();
        }

        /// <summary>(Re-)fetches every primary EPG (if any) plus every user-added extra source. Best-effort per source.</summary>
        public async Task ReloadAllAsync(CancellationToken ct = default)
        {
            var tasks = new List<Task>();
            for (var i = 0; i < PrimaryEpgUrls.Count; i++) tasks.Add(LoadSourceAsync(PrimarySourceIdAt(i), PrimaryEpgUrls[i], ct));
            foreach (var source in SettingsStore.Current.EpgSources) tasks.Add(LoadSourceAsync(source.Id, source.Url, ct));
            await Task.WhenAll(tasks);
        }

        /// <summary>(Re)loads a single EPG source by id — the primary id or any extra source id.</summary>
        public Task ReloadSourceAsync(string sourceId, string url, CancellationToken ct = default) =>
            LoadSourceAsync(sourceId, url, ct);

        private async Task LoadSourceAsync(string sourceId, string url, CancellationToken ct)
        {
            try
            {
                byte[] bytes;
                using (var response = await Http.GetAsync(url, ct))
                {
                    bytes = await response.Content.ReadAsByteArrayAsync(ct);
                }

                var isGzip = url.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ||
                             (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b);

                var result = await Task.Run(() =>
                {
                    using var stream = new MemoryStream(bytes);
                    return XmlTvParser.Parse(stream, isGzip);
                }, ct);

                if (result.ProgrammesByChannelId.Count == 0 && result.ChannelNames.Count == 0)
                {
                    _loadErrors[sourceId] = "The guide loaded, but it has no programmes.";
                }
                else
                {
                    var nameIndex = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (epgChannelId, displayName) in result.ChannelNames)
                    {
                        var normalized = ChannelNameNormalizer.Normalize(displayName);
                        if (normalized.Length > 0) nameIndex.TryAdd(normalized, epgChannelId);
                    }

                    var programmes = new Dictionary<string, List<EpgProgramme>>(StringComparer.Ordinal);
                    foreach (var (id, list) in result.ProgrammesByChannelId) programmes[id] = list;
                    var names = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (id, name) in result.ChannelNames) names[id] = name;

                    _dataBySource[sourceId] = new SourceData(programmes, names, nameIndex);
                    _loadErrors.Remove(sourceId);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // superseded — don't touch error state or fire Changed for a cancelled load
            }
            catch (Exception ex)
            {
                // Leave any previously loaded data for this source in place rather than wiping it,
                // but DO record what went wrong instead of failing silently.
                _loadErrors[sourceId] = DescribeLoadError(ex);
            }
            RaiseChanged();
        }

        private static string DescribeLoadError(Exception ex) => ex switch
        {
            System.Net.Http.HttpRequestException h => h.Message,
            OperationCanceledException => "The server didn't respond in time.",
            _ => ex.Message
        };

        // ---- Sources (persisted extras) -------------------------------------------------------

        /// <summary>Ids + display names of every EPG source currently known (primary one(s) first, if any).</summary>
        public IReadOnlyList<(string Id, string Name)> AvailableSources()
        {
            var result = new List<(string, string)>();
            for (var i = 0; i < PrimaryEpgUrls.Count; i++)
            {
                var name = i == 0 ? Strings.PrimarySourceName : string.Format(Strings.PrimarySourceNameNumberedFormat, i + 1);
                result.Add((PrimarySourceIdAt(i), name));
            }
            foreach (var source in SettingsStore.Current.EpgSources) result.Add((source.Id, source.Name));
            return result;
        }

        /// <summary>The feed URL for any known source id — a primary (playlist-bundled) one or a user-added
        /// extra one — or null if <paramref name="sourceId"/> isn't recognized. Lets callers (the EPG sources
        /// screen) treat both kinds uniformly instead of special-casing the primary id(s).</summary>
        public string? UrlForSource(string sourceId)
        {
            var index = PrimaryIndexOf(sourceId);
            if (index >= 0) return index < PrimaryEpgUrls.Count ? PrimaryEpgUrls[index] : null;
            return SettingsStore.Current.EpgSources.FirstOrDefault(s => s.Id == sourceId)?.Url;
        }

        public EpgSource AddSource(string name, string url)
        {
            var source = new EpgSource(Guid.NewGuid().ToString("N"), name, url);
            SettingsStore.Current.EpgSources.Add(source);
            SettingsStore.Save();
            RaiseChanged();
            return source;
        }

        public void RemoveSource(string id)
        {
            SettingsStore.Current.EpgSources.RemoveAll(s => s.Id == id);
            _dataBySource.Remove(id);
            _loadErrors.Remove(id);
            // Any mapping pointing at a now-gone source would silently resolve to nothing; drop it
            // so the channel falls back to normal resolution instead of a dead, invisible override.
            var stale = SettingsStore.Current.EpgMappings.Where(kv => kv.Value.EpgSourceId == id).Select(kv => kv.Key).ToList();
            foreach (var key in stale) SettingsStore.Current.EpgMappings.Remove(key);
            // Same idea for a dangling "active source" override — removing the forced source should
            // fall back to normal (every-source) resolution, not silently match nothing at all.
            if (SettingsStore.Current.ActiveEpgSourceId == id) SettingsStore.Current.ActiveEpgSourceId = null;
            SettingsStore.Save();
            RaiseChanged();
        }

        /// <summary>id -> display-name of every guide channel known inside one EPG source (empty if not loaded yet).</summary>
        public IReadOnlyDictionary<string, string> ChannelNamesForSource(string sourceId) =>
            _dataBySource.TryGetValue(sourceId, out var data) ? data.ChannelNames : new Dictionary<string, string>();

        /// <summary>Diagnostic snapshot of one source's load state, for the EPG sources screen.</summary>
        public readonly record struct SourceStatus(bool IsLoaded, int ChannelCount, int ProgrammeCount, string? ErrorMessage);

        public SourceStatus GetSourceStatus(string sourceId)
        {
            _loadErrors.TryGetValue(sourceId, out var error);
            if (!_dataBySource.TryGetValue(sourceId, out var data)) return new SourceStatus(false, 0, 0, error);
            var programmeCount = data.ProgrammesByChannelId.Values.Sum(list => list.Count);
            return new SourceStatus(true, data.ChannelNames.Count, programmeCount, error);
        }

        // ---- Per-channel mapping ---------------------------------------------------------------

        public EpgChannelMapping? GetMapping(string channelKey) =>
            SettingsStore.Current.EpgMappings.TryGetValue(channelKey, out var mapping) ? mapping : null;

        public void SetMapping(EpgChannelMapping mapping)
        {
            SettingsStore.Current.EpgMappings[mapping.ChannelKey] = mapping;
            SettingsStore.Save();
            RaiseChanged();
        }

        public void ClearMapping(string channelKey)
        {
            if (SettingsStore.Current.EpgMappings.Remove(channelKey))
            {
                SettingsStore.Save();
                RaiseChanged();
            }
        }

        // ---- Resolution & lookup ---------------------------------------------------------------

        private bool HasProgrammeData(string sourceId, string epgChannelId) =>
            _dataBySource.TryGetValue(sourceId, out var data) &&
            data.ProgrammesByChannelId.TryGetValue(epgChannelId, out var list) && list.Count > 0;

        /// <summary>
        /// Resolves which (sourceId, epgChannelId) a playlist channel should use, honoring any user
        /// mapping. Absent one, this checks EVERY loaded source (primary first, then extras in the
        /// order they were added) in two passes: first an exact tvg-id match, then — only if nothing
        /// matched by id anywhere — a fuzzy match on normalized channel *name*. The second pass exists
        /// because a playlist's tvg-id scheme and a third-party EPG's channel-id scheme are two
        /// unrelated providers' conventions with no reason to line up as strings, even when the
        /// channels obviously refer to the same thing to a human ("PT - RTP 1" vs "RTP1.pt").
        /// </summary>
        /// <summary>When set (see <see cref="SetActiveSourceOverride"/>), the id of the one source that
        /// automatic matching is forced to use — every other loaded source is ignored for matching
        /// purposes until this is cleared. Per-channel mappings still take priority over this.</summary>
        public string? ActiveSourceOverride => SettingsStore.Current.ActiveEpgSourceId;

        /// <summary>Forces automatic matching to use only <paramref name="sourceId"/> (or, with null,
        /// reverts to the default of trying every loaded source in priority order). "Activate"/"Deactivate"
        /// in the EPG sources screen — activating one source implicitly deactivates whichever was active.</summary>
        public void SetActiveSourceOverride(string? sourceId)
        {
            SettingsStore.Current.ActiveEpgSourceId = sourceId;
            SettingsStore.Save();
            RaiseChanged();
        }

        public (string SourceId, string EpgChannelId)? Resolve(Channel channel)
        {
            if (GetMapping(channel.Key) is { } mapping) return (mapping.EpgSourceId, mapping.EpgChannelId);

            // An active override restricts matching to just that one source — including the fallback at
            // the bottom of this method — rather than the normal primary-then-extras priority order.
            var overrideSourceId = ActiveSourceOverride;
            var sourceIds = overrideSourceId != null ? new[] { overrideSourceId } : OrderedSourceIds();

            foreach (var sourceId in sourceIds)
            {
                if (HasProgrammeData(sourceId, channel.Id)) return (sourceId, channel.Id);
            }

            var normalizedChannelName = ChannelNameNormalizer.Normalize(channel.Name);
            if (normalizedChannelName.Length > 0)
            {
                foreach (var sourceId in sourceIds)
                {
                    if (_dataBySource.TryGetValue(sourceId, out var data) &&
                        data.NormalizedNameIndex.TryGetValue(normalizedChannelName, out var epgChannelId))
                    {
                        return (sourceId, epgChannelId);
                    }
                }

                // Last resort: a heavily abbreviated guide name (e.g. "SPT1HD" for "SPORT TV 1 FHD") —
                // see MatchesAbbreviation for exactly what this does and does not accept.
                foreach (var sourceId in sourceIds)
                {
                    if (!_dataBySource.TryGetValue(sourceId, out var data)) continue;
                    foreach (var (epgChannelId, candidateNormalizedName) in data.NormalizedNameIndex)
                    {
                        if (MatchesAbbreviation(normalizedChannelName, candidateNormalizedName))
                        {
                            return (sourceId, epgChannelId);
                        }
                    }
                }
            }

            // Nothing confirmed anywhere — fall back to primary (or the active override, if one is set)
            // in case it simply hasn't finished loading yet.
            if (overrideSourceId != null) return (overrideSourceId, channel.Id);
            return PrimaryEpgUrls.Count == 0 ? null : (PrimarySourceId, channel.Id);
        }

        private IReadOnlyList<string> OrderedSourceIds()
        {
            var ids = new List<string>(PrimaryEpgUrls.Count + SettingsStore.Current.EpgSources.Count);
            for (var i = 0; i < PrimaryEpgUrls.Count; i++) ids.Add(PrimarySourceIdAt(i));
            ids.AddRange(SettingsStore.Current.EpgSources.Select(s => s.Id));
            return ids;
        }

        private static readonly Regex TrailingDigitsRegex = new(@"(\d+)$", RegexOptions.Compiled);

        /// <summary>
        /// Last-resort, deliberately narrow match for a heavily abbreviated guide name against a
        /// normalized channel name — e.g. a channel "SPORT TV 1 FHD" (normalizes to "sporttv1")
        /// against a guide entry "SPT1HD" (normalizes to "spt1"). Unlike ChannelNameNormalizer's
        /// prefix/quality stripping, collapsing whole words and vowels down to a call-sign-style
        /// abbreviation has no reliable inverse — this is a genuine guess, not a rule. The guards
        /// below (agreement on any trailing channel number, and a minimum length ratio between the
        /// two names, stricter when neither has a number to anchor on) cut the worst false-positive
        /// risk — a short, generic abbreviation coincidentally fitting inside an unrelated long name
        /// — but cannot eliminate it. When this gets a channel wrong, "Assign channel EPG" fixes it
        /// permanently for that one channel.
        /// </summary>
        private static bool MatchesAbbreviation(string a, string b)
        {
            var (lettersA, digitsA) = SplitTrailingDigits(a);
            var (lettersB, digitsB) = SplitTrailingDigits(b);
            if (lettersA.Length == 0 || lettersB.Length == 0) return false;

            // A channel number is the strongest signal available here — if both sides have one, a
            // mismatch (e.g. "...1" vs "...2") rules the pair out immediately.
            if (digitsA.Length > 0 && digitsB.Length > 0 && digitsA != digitsB) return false;
            var hasNumberAnchor = digitsA.Length > 0 && digitsB.Length > 0;

            var (shorter, longer) = lettersA.Length <= lettersB.Length ? (lettersA, lettersB) : (lettersB, lettersA);
            if (shorter.Length < 3) return false;

            // Without a shared number to anchor on, a loose subsequence match alone is too easy to
            // satisfy by coincidence, so it's held to a tighter length ratio.
            var minRatio = hasNumberAnchor ? 0.4 : 0.55;
            if ((double)shorter.Length / longer.Length < minRatio) return false;

            return IsSubsequence(shorter, longer);
        }

        private static (string Letters, string Digits) SplitTrailingDigits(string normalized)
        {
            var match = TrailingDigitsRegex.Match(normalized);
            return match.Success ? (normalized[..match.Index], match.Value) : (normalized, "");
        }

        /// <summary>True if every character of <paramref name="shorter"/> appears in <paramref name="longer"/>, in order (not necessarily contiguous).</summary>
        private static bool IsSubsequence(string shorter, string longer)
        {
            var i = 0;
            foreach (var c in longer)
            {
                if (i < shorter.Length && c == shorter[i]) i++;
            }
            return i == shorter.Length;
        }

        private IReadOnlyList<EpgProgramme> ProgrammesForResolved(string sourceId, string epgChannelId) =>
            _dataBySource.TryGetValue(sourceId, out var data) && data.ProgrammesByChannelId.TryGetValue(epgChannelId, out var list)
                ? list
                : Array.Empty<EpgProgramme>();

        /// <summary>
        /// Index of the first programme with StartMillis > <paramref name="time"/>, or
        /// programmes.Count if none. Binary search — safe ONLY because the list is guaranteed
        /// chronologically sorted by XmlTvParser (its stop-time-inference step sorts before
        /// returning) and this is the only place that ever populates ProgrammesByChannelId, from
        /// that parser's output directly. Don't break the sort without updating this.
        /// </summary>
        private static int UpperBound(IReadOnlyList<EpgProgramme> programmes, long time)
        {
            var lo = 0;
            var hi = programmes.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (programmes[mid].StartMillis <= time) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// <summary>Current and next programme for a channel, or (null, null) if there's no EPG data for it.</summary>
        public (EpgProgramme? Current, EpgProgramme? Next) NowNext(Channel channel, long nowMillis)
        {
            var resolved = Resolve(channel);
            if (resolved == null) return (null, null);
            var programmes = ProgrammesForResolved(resolved.Value.SourceId, resolved.Value.EpgChannelId);
            if (programmes.Count == 0) return (null, null);

            var idx = UpperBound(programmes, nowMillis);
            var next = idx < programmes.Count ? programmes[idx] : null;
            var candidateCurrent = idx - 1 >= 0 ? programmes[idx - 1] : null;
            var current = candidateCurrent != null && nowMillis < candidateCurrent.StopMillis ? candidateCurrent : null;
            return (current, next);
        }

        /// <summary>Up to <paramref name="count"/> consecutive programmes for a channel starting from whichever
        /// is airing at <paramref name="nowMillis"/>, or the next upcoming one if nothing is airing right now.
        /// The caller (the now-playing panel's "Playing now / next / after next" lines) tells those apart by
        /// checking whether result[0] actually spans <paramref name="nowMillis"/>.</summary>
        public IReadOnlyList<EpgProgramme> UpcomingFrom(Channel channel, long nowMillis, int count)
        {
            var resolved = Resolve(channel);
            if (resolved == null) return Array.Empty<EpgProgramme>();
            var programmes = ProgrammesForResolved(resolved.Value.SourceId, resolved.Value.EpgChannelId);
            if (programmes.Count == 0) return Array.Empty<EpgProgramme>();

            var idx = UpperBound(programmes, nowMillis);
            var startIdx = idx > 0 && programmes[idx - 1].StopMillis > nowMillis ? idx - 1 : idx;

            var result = new List<EpgProgramme>(count);
            for (var i = startIdx; i < programmes.Count && result.Count < count; i++) result.Add(programmes[i]);
            return result;
        }

        /// <summary>Programmes for a channel overlapping [windowStart, windowEnd), sorted by start time. Used by the guide grid.</summary>
        public IReadOnlyList<EpgProgramme> ProgrammesFor(Channel channel, long windowStart, long windowEnd)
        {
            var resolved = Resolve(channel);
            if (resolved == null) return Array.Empty<EpgProgramme>();
            var programmes = ProgrammesForResolved(resolved.Value.SourceId, resolved.Value.EpgChannelId);
            if (programmes.Count == 0) return Array.Empty<EpgProgramme>();

            // Binary search to the last programme starting at or before windowStart (it may still
            // overlap the window even though it started earlier), then scan forward only as far as
            // the window extends — cheap even when a channel's full guide spans many days.
            var startIdx = Math.Max(0, UpperBound(programmes, windowStart) - 1);
            var result = new List<EpgProgramme>();
            for (var i = startIdx; i < programmes.Count; i++)
            {
                var p = programmes[i];
                if (p.StartMillis >= windowEnd) break;
                if (p.StopMillis > windowStart) result.Add(p);
            }
            return result;
        }
    }
}
