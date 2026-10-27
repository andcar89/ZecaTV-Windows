using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZecaTV.Localization;
using ZecaTV.Models;

namespace ZecaTV.Data
{
    /// <summary>Everything ZecaTV remembers between runs. Grows as later milestones add favorites, EPG sources, etc.</summary>
    public sealed class AppSettings
    {
        /// <summary>
        /// Legacy single-playlist fields. Read only by <see cref="SettingsStore.Load"/> to migrate an
        /// older settings.json into <see cref="Playlists"/> the first time it's opened after
        /// upgrading; never written to again afterward (both are nulled out once migrated).
        /// </summary>
        public PlaylistSource? Source { get; set; }
        public string? LastCategoryId { get; set; }

        /// <summary>Every saved playlist. Use SettingsStore.ActivePlaylist(), not this list directly, to find the current one.</summary>
        public List<SavedPlaylist> Playlists { get; set; } = new();

        /// <summary>Id of whichever entry in Playlists is currently loaded/browsable. Null only when Playlists is empty.</summary>
        public string? ActivePlaylistId { get; set; }

        /// <summary>0–100.</summary>
        public int Volume { get; set; } = 100;

        /// <summary>Favorited channels by Channel.Key (stream URL). Use FavoritesManager, not this list directly.</summary>
        public List<string> Favorites { get; set; } = new();

        /// <summary>User-added extra XMLTV feeds, beyond whatever the playlist itself bundles. Use EpgStore, not this list directly.</summary>
        public List<EpgSource> EpgSources { get; set; } = new();

        /// <summary>Per-channel EPG overrides, keyed by Channel.Key. Use EpgStore, not this dictionary directly.</summary>
        public Dictionary<string, EpgChannelMapping> EpgMappings { get; set; } = new();

        /// <summary>Channel.Key of whatever last played, updated on every channel change regardless of ResumeLastChannelOnStartup — so turning that setting on later still has something to resume.</summary>
        public string? LastChannelKey { get; set; }

        /// <summary>If true, ZecaTV starts playing LastChannelKey (in preview, not fullscreen) as soon as the playlist loads.</summary>
        public bool ResumeLastChannelOnStartup { get; set; } = false;

        /// <summary>The main window's normal (non-maximized) position and size when ZecaTV was last closed,
        /// in WPF device-independent units; null until the first close. Applied via UI.WindowBounds.Fit,
        /// which re-fits it to the screens as they are now. Written by MainWindow.Window_Closing.</summary>
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }

        /// <summary>Whether the window was maximized when closed (or, if closed while fullscreen, whether it
        /// was maximized before going fullscreen).</summary>
        public bool WindowMaximized { get; set; }

        /// <summary>Whether ZecaTV was closed while playing fullscreen. Fullscreen needs something playing,
        /// so it's only brought back on startup when "resume last channel" is on and starts a channel.</summary>
        public bool WindowFullscreen { get; set; }

        /// <summary>UI language. Changing it takes effect immediately in any window opened afterward;
        /// already-open windows (including the main one) need a restart to fully re-render.</summary>
        public AppLanguage Language { get; set; } = AppLanguage.English;

        /// <summary>When set, automatic EPG matching (see EpgStore.Resolve) considers ONLY this one
        /// source instead of trying every loaded source in order — "Activate" in the EPG sources
        /// screen. Null means the default behavior (try every source). Per-channel mappings still
        /// override this regardless. Use EpgStore.SetActiveSourceOverride, not this directly.</summary>
        public string? ActiveEpgSourceId { get; set; }

        /// <summary>How the video is fitted into its display area. See VideoRatioMode; applied by MainWindow.</summary>
        public VideoRatioMode VideoRatioMode { get; set; } = VideoRatioMode.Fit;

        /// <summary>Periodically re-fetch the active playlist and its EPG while ZecaTV stays open, so a
        /// days-long session doesn't go stale. On by default — see MainWindow.BackgroundRefreshTick.</summary>
        public bool BackgroundRefreshEnabled { get; set; } = true;

        /// <summary>Minutes between automatic background refreshes.</summary>
        public int BackgroundRefreshIntervalMinutes { get; set; } = 180;

        /// <summary>Minimizing the window sends it to the system tray (hidden, no taskbar button)
        /// instead of just minimizing to the taskbar. Does not affect closing the window (Alt+F4 / the
        /// × button), which always fully exits — see MainWindow.MainWindow_StateChanged.</summary>
        public bool MinimizeToTray { get; set; } = true;

        /// <summary>Manual per-category-name corrections for ContentClassifier's M3U Live/Movie/Series
        /// guess, keyed by the raw group-title exactly as the playlist writes it — set from the sidebar
        /// category's right-click menu ("Treat category as…") when the guess is wrong for a whole
        /// category (e.g. a live channel bouquet whose category is named for the genre it shows, not
        /// for being on-demand). Shared across every M3U playlist using this category name, the same
        /// way Favorites/EpgMappings are shared by matching value rather than kept per playlist — a
        /// provider's naming quirk isn't specific to one playlist. Use PlaylistRepository's
        /// CategoryOverride/SetCategoryOverride, not this dictionary directly.</summary>
        public Dictionary<string, ContentType> CategoryContentOverrides { get; set; } = new();
    }

    /// <summary>
    /// Persists <see cref="AppSettings"/> as %AppData%\ZecaTV\settings.json — the Windows stand-in for
    /// the Android app's SharedPreferences. Written via a temp file + move so a crash mid-save can't
    /// leave a half-written file behind.
    /// </summary>
    public static class SettingsStore
    {
        public static string AppDataDir { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZecaTV");

        private static string SettingsPath => Path.Combine(AppDataDir, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        private static AppSettings? _current;

        public static AppSettings Current => _current ??= Load();

        private static AppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new AppSettings();
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                MigrateSingleSource(settings);
                return settings;
            }
            catch (Exception ex)
            {
                // Keep the unreadable file around for inspection instead of silently overwriting it.
                Debug.WriteLine($"settings.json unreadable: {ex}");
                try { File.Copy(SettingsPath, SettingsPath + ".bad", overwrite: true); } catch { /* best effort */ }
                return new AppSettings();
            }
        }

        /// <summary>
        /// A settings.json from before multi-playlist support has a single Source/LastCategoryId and
        /// an empty Playlists list. Wraps that source as the first (and active) saved playlist, once,
        /// so upgrading never loses the playlist someone already has configured.
        /// </summary>
        private static void MigrateSingleSource(AppSettings settings)
        {
            if (settings.Playlists.Count > 0 || settings.Source == null) return;

            var playlist = new SavedPlaylist
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = PlaylistNaming.Suggest(settings.Source),
                Source = settings.Source,
                LastCategoryId = settings.LastCategoryId
            };
            settings.Playlists.Add(playlist);
            settings.ActivePlaylistId = playlist.Id;
            settings.Source = null;
            settings.LastCategoryId = null;
        }

        /// <summary>The currently active playlist, or null if none is configured yet (or ActivePlaylistId is stale — falls back to the first saved playlist rather than showing Welcome unnecessarily).</summary>
        public static SavedPlaylist? ActivePlaylist() =>
            Current.Playlists.FirstOrDefault(p => p.Id == Current.ActivePlaylistId) ?? Current.Playlists.FirstOrDefault();

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                var tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
                File.Move(tmp, SettingsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Saving settings failed: {ex}");
            }
        }

        /// <summary>
        /// Writes everything ZecaTV remembers (playlists, favorites, EPG sources/mappings, and the
        /// rest of <see cref="AppSettings"/>) to an arbitrary file the user picked. Throws on failure
        /// (a full disk, a path the user doesn't have permission to write) — the caller shows it.
        /// </summary>
        public static void ExportTo(string path)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(Current, JsonOptions));
        }

        /// <summary>
        /// Restores from a file written by <see cref="ExportTo"/>. Only overwrites the real
        /// settings.json once <paramref name="path"/> is confirmed to actually deserialize as
        /// <see cref="AppSettings"/> — a bad or unrelated file changes nothing. Takes effect the next
        /// time ZecaTV starts: this deliberately doesn't try to hot-swap every subsystem's own already-
        /// loaded copy of the old settings (the playlist currently on screen, EpgStore's loaded guide
        /// data, FavoritesManager's cached set) — same trade-off the app already makes for a language
        /// change, and far less likely to leave something half-updated than reaching into all of those
        /// live. The caller is expected to tell the user to restart.
        /// </summary>
        /// <exception cref="InvalidDataException">The file isn't a valid ZecaTV backup.</exception>
        public static void ImportFrom(string path)
        {
            var json = File.ReadAllText(path);
            AppSettings? restored;
            try
            {
                restored = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("That file isn't a valid ZecaTV backup.", ex);
            }
            if (restored == null)
            {
                throw new InvalidDataException("That file isn't a valid ZecaTV backup.");
            }

            Directory.CreateDirectory(AppDataDir);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(restored, JsonOptions));
            File.Move(tmp, SettingsPath, overwrite: true);
            _current = null; // the next read of Current (after restart) picks up what we just wrote
        }
    }
}
