# ZecaTV for Windows

WPF + LibVLCSharp port of the ZecaTV Android TV IPTV player.

## Build & run

1. Open `ZecaTV.sln` in Visual Studio 2022 (".NET desktop development" workload, .NET 8 SDK).
2. Press **F5**. The first build restores NuGet packages, including about 80 MB of native LibVLC files.

## Installer & portable build

For sharing ZecaTV as something other than "open it in Visual Studio and press F5," the `installer\`
folder builds two things, both self-contained (they bundle the .NET runtime, so nothing needs to be
installed on the machine that runs them — which does make both quite a bit bigger, around 200 MB,
mostly the runtime plus LibVLC's own native files):

- **A portable copy** — a folder (zipped up) you can copy anywhere, even a USB stick, and run
  `ZecaTV.exe` from directly. No installer, no admin rights, nothing to set up.
- **A setup.exe installer** — installs to the current user's own AppData (no admin prompt), adds a
  Start Menu entry and an uninstaller, and offers a desktop shortcut and launching ZecaTV once
  install finishes.

From a PowerShell prompt in `installer\`:

- `.\publish.ps1` builds just the portable zip (`installer\..\publish\ZecaTV-Portable-win-x64.zip`).
- `.\build-installer.ps1` builds the portable copy first and then the installer, which needs
  [Inno Setup](https://jrsoftware.org/isdl.php) (free) installed once. Without it, the script tells
  you as much — you can still open `installer\ZecaTV.iss` in the Inno Setup app afterward and click
  **Compile** by hand.
- The finished installer lands in `installer\Output\ZecaTV-Setup-<version>.exe`.

Bumping the version for a new build means updating it in two places: `ZecaTV\ZecaTV.csproj`
(`<Version>`) and `installer\ZecaTV.iss` (`MyAppVersion`) — they're not linked automatically.

Both builds are unsigned (a code-signing certificate isn't worth it for a personal app), so Windows
SmartScreen may warn the first time the installer or `ZecaTV.exe` runs on another machine — **More
info → Run anyway** gets past it. This is only ever shown once per machine.

ZecaTV doesn't have real branding yet, so both the installer and the app's own icon (taskbar,
shortcuts, tray) currently use the same placeholder — a plain accent-blue "Z" badge
(`ZecaTV\Assets\ZecaTV.ico`) — swap that file out whenever real artwork is ready.

## Controls

The same table below is also available inside the app: **Settings → Controls → View controls…**.

| Action | Keys / mouse |
|---|---|
| Preview a channel | Click it, or select it and press Enter |
| Fullscreen | Click / Enter again on the playing channel, F11, F, or double-click the video |
| Exit fullscreen | Esc (or F11 / F / double-click) |
| Change channel (fullscreen) | ↑ / ↓ or PgUp / PgDn |
| Pause / resume (Movies & TV Shows) | Space (fullscreen), or the pause button |
| Seek (Movies & TV Shows) | ← / → (fullscreen), or drag the seek bar |
| Move between categories and channels | ← / → |
| Volume | Mouse wheel over the video, + / − |
| Mute | M |
| Video ratio, audio, subtitles | Right-click the video (while playing) |
| Jump straight to a channel | Type its number, Enter to confirm |
| Add/remove favorite | Ctrl+D, or right-click a channel |
| Browse Movies & TV Shows | Tabs in the sidebar |
| Correct a miscategorized category (M3U) | Right-click it in the sidebar → Treat category as |
| Channel list (fullscreen) | ← (again for categories) · → or Esc to close |
| Back to the shows grid | Esc, or the back button |
| Search all channels | Ctrl+F, or click the search box |
| Switch playlist | The playlist picker in the top bar |
| TV Guide | G, or the TV Guide button |
| Reload playlist | F5 |

## Search

Click the search box (top bar, next to Reload) or press **Ctrl+F**, then type. It matches anywhere
in a channel's name, case-insensitively — searching "sport" finds "Sport TV 1" and "ESPN Sports HD"
alike — and always searches every channel in the active playlist, regardless of which category is
currently selected. Results update as you type; ↑/↓ move through them without leaving the box, and
Enter plays the selected one. Esc, or clearing the box, goes back to whichever category was open.

## Movies & TV Shows

Playlists that bundle movies and/or TV series alongside live channels (common with many Xtream Codes
logins, and increasingly with M3U playlists too) get two extra tabs in the sidebar — **Movies** and
**TV Shows** — next to the usual **Live TV** tab. Each tab has its own categories and remembers its
own last-selected one, entirely separately from Live TV's. A playlist with no movies or shows in it
simply doesn't show the extra tabs.

- **Xtream Codes** playlists get their movies and series straight from the panel's own VOD/series
  API, so they're already correctly separated from live channels — nothing to guess.
- **M3U** playlists have no such separation built in, so ZecaTV classifies each entry automatically —
  by its group title and name (a year in parentheses, season/episode markers such as `S01E02`, a
  "Movies"/"Series" category name, and similar signals most other IPTV players key off too).
- The **TV Shows** tab groups individual episodes into one tile per show; opening a show lists its
  episodes grouped by season, instead of one long flat list of hundreds of episodes.
- A Movie or episode gets a play/pause button and a seek bar once playing — under the video when
  windowed, along the bottom of the screen in fullscreen — since unlike a live channel, these have a
  real position and duration to show and scrub through. See the Controls table above for the
  keyboard shortcuts.
- Favorites, EPG channel mapping, and catch-up remain Live TV-only, same as everywhere else in the
  app — a Movie/episode's right-click menu only offers Play and Copy stream URL.

### When the automatic guess gets a category wrong (M3U only)

Some providers name a category for the *genre* a bouquet of live channels shows — "Filmes/Séries" for
a set of always-on movie/drama channels like TVCine or AMC — rather than for being on-demand, which
can fool the automatic guess into putting live channels in the Movies/TV Shows tabs. Right-click that
category in the sidebar and choose **Treat category as** → **Live TV** (or **Movies** / **TV Shows**,
for the same mistake in the other direction) to correct it by hand. The correction applies instantly,
is remembered for that category name across every playlist that uses it, and shows up under
**Settings → Category overrides**, where each one can be reviewed or removed. Choosing **Automatic**
goes back to ZecaTV's own guess.

## Language

Change it from **Settings**, or from the small **EN / PT** buttons under **Add playlist** on the
very first screen (before any playlist is configured). Changing it there updates that screen
immediately and everything else the moment you add your first playlist. Changing it later from
Settings still needs a restart of ZecaTV for the main window and an already-open Settings to fully
switch over — any new window opened after the change already reflects it.

## Playlists

ZecaTV can hold several playlists at once, switching between them rather than merging their
channels together. Add one from the picker in the top bar or from **Settings → Playlists**, which
also lets you rename, edit, or remove any saved playlist and switch to another with one click.

Favorites and EPG channel mappings are shared across every playlist (matched by the channel's
stream URL), so a channel that appears in more than one playlist keeps its favorite/mapping status
no matter which one is active. Each playlist does remember its own last-viewed category, though.

If you're upgrading from an earlier build with a single playlist already configured, it's carried
over automatically the first time you run this version — nothing to redo.

Categories in the sidebar follow the order they appear in the playlist file itself, not
alphabetical — so if you've arranged categories with the M3U Editor before exporting, that order
carries straight through.

## Settings

Click **Settings…** for a small hub rather than one long form: your saved playlists with buttons to
switch, edit, remove, or add one; a summary of your EPG sources with buttons to manage or reload
them; a "resume the last channel on startup" toggle; a button to view every keyboard/mouse control;
category overrides (see [Movies & TV Shows](#movies--tv-shows) above); a link to the bundled
**M3U Editor** — a standalone web-based playlist editor/manager, unrelated to ZecaTV's own code,
that opens in your default browser; your version number; and a shortcut to the `%AppData%\ZecaTV`
logs folder.

## EPG

Press **G** or click **TV Guide** to open a channel-by-channel grid of what's on, similar to
TiviMate. It uses whichever EPG (XMLTV) feed came bundled with your playlist automatically — and if
your playlist's `#EXTM3U` header declares several, comma-separated (e.g. an M3U Editor export with
multiple EPG sources baked in), each one is loaded as its own independent source, exactly like an
extra source added by hand below. A gzip-compressed feed (`.xml.gz`, or just gzip-compressed bytes
regardless of the URL's extension) is decompressed automatically either way.

The currently playing channel's next few programmes also show under the video in the main window,
and in the same spot in the fullscreen info bar, whenever guide data is available: nearest first,
as `HH:mm - HH:mm : Title`.

- **◀ / ▶** or **PgUp / PgDn** move the visible time window; **Now** jumps back to the current time.
- The category dropdown narrows the grid to one category (handy for a large playlist).
- Click a programme to play its channel in fullscreen.
- **Settings → Manage sources…** lets you add extra XMLTV feeds — useful if your playlist's own
  guide is missing some channels. Every loaded source is shown with how many channels and
  programmes it contributed, or why it failed.
- Each source has an **Activate** button. Activating one forces automatic matching to use only
  that source — every other loaded source is ignored for matching purposes until you deactivate it
  (or activate a different one, which switches automatically). Handy when testing a new source: it
  won't get a chance to match anything while an older source keeps satisfying every channel first.
  Per-channel assignments (below) always take priority over this regardless.
- Automatic matching tries, in order: the exact channel ID, then the name with country prefixes
  and quality tags (HD/FHD/4K/etc.) stripped, then — as a last resort — a looser match for heavily
  abbreviated guide names (e.g. a guide entry called "SPT1HD" matching a channel named
  "SPORT TV 1 FHD"). That last step is a genuine guess with guard rails (it won't cross a channel
  number, and it needs the abbreviation to be a reasonable fraction of the full name's length), not
  a rule — it can occasionally match the wrong channel. Assigning a channel by hand (below) always
  overrides it and is the reliable fix when it gets one wrong.
- If a channel's guide data doesn't match automatically (its name or ID doesn't line up with any
  loaded source), assign it a specific guide entry by hand: right-click the channel →
  **Assign channel EPG…**, or open the same tool from **Settings → Assign channel EPG…**, which
  lets you pick any channel (not just one you've right-clicked) and lists every mapping you've
  made so far with a Remove button on each.

## Catch-up (Xtream only)

Channels with catch-up enabled show a small clock icon in the channel list (main list and the
fullscreen side panel), next to the favorite star.

## Logo sizing

Some IPTV logos are a small mark centered on a much larger transparent (or flat-color) canvas,
which makes them look tiny once scaled to fit the channel-list tile even though the tile itself
is sized fine. ZecaTV now trims that empty padding off each logo automatically before displaying
it (a little breathing room is kept around the trimmed content), so a padded logo fills its tile
about as well as an already-tight one does. A logo that's already full-bleed, or one with no
single clear background color to trim against, is left exactly as downloaded.

Logos are cached on disk (under `%AppData%\ZecaTV\logocache`) so a restart doesn't re-download
every logo in the playlist. A local M3U file's logos are kept indefinitely, since they only change
when you re-import that file; an M3U URL or Xtream playlist's logos come from a provider that can
swap an icon at any time, so those are kept for 30 days before being re-downloaded.

If your Xtream Codes login has catch-up (archive/timeshift) enabled for a channel, past
programmes in the **TV Guide** for that channel get a blue tint and are clickable — click one to
watch it from the start via catch-up instead of live. A badge appears (top-left of the video, in
both windowed and fullscreen) showing which programme you're watching, with a **Back to Live**
button to switch back to the live stream at any time. Clicking the channel name itself (left side
of the guide) always jumps back to live, whatever programme cell you're looking at.

Past programmes older than your panel's own catch-up retention window (however many days it
reports) are shown dimmed and aren't clickable — there's nothing there to actually watch. This
only applies to Xtream Codes playlists: M3U playlists and files have no catch-up concept, so their
guide cells never offer it, matching how the provider itself works.

## Direct channel entry

Type a channel's number (shown next to it in both channel lists and in the fullscreen info bar) and
press **Enter** — or just stop typing for a moment and it jumps on its own — to tune straight to it,
from anywhere in the app, windowed or fullscreen. **Backspace** removes the last digit, **Esc**
cancels. If the number isn't in the current playlist, a brief message says so instead.

## Video ratio

Right-click the video for a **Video ratio** submenu with four choices: **Fit** (default — the
stream's own ratio, letterboxed if needed, nothing cropped or distorted), **Stretch to fill** (fills
the whole video area exactly, distorting the picture if the stream's ratio doesn't already match —
handy for a stream that reports the wrong ratio and shows unwanted black bars), **4:3**, and
**16:9** (forces that ratio regardless of what the stream reports). The choice is remembered and
reapplied automatically to whatever plays next, and to a resized/fullscreen window while
**Stretch to fill** is active.

## Audio track / subtitles

Right-click the video (not the channel list) to pick an audio track or subtitle language, when the
stream itself offers more than one — this is only ever populated once a channel is actually
playing, since that's when LibVLC learns what's in the stream. Subtitles always include an **Off**
option at the top. If a stream carries just one audio track and no subtitles, the corresponding
submenu simply says there's nothing to pick from.

## Minimize to tray

On by default (**Settings → Window**): minimizing ZecaTV sends it to the system tray, near the
clock, instead of leaving a button on the taskbar. Click the tray icon (or double-click it, or use
its right-click menu's **Open ZecaTV**) to bring it back. Playback keeps running while minimized to
the tray, same as a normal minimize. Closing the window (the **×** button, or Alt+F4) always fully
exits ZecaTV either way — the tray icon's own right-click menu has an **Exit** as well, since a
window that's minimized to the tray has no taskbar button to close from. Turn it off in Settings to
go back to a plain taskbar minimize.

## Background refresh

For a ZecaTV window left open for days, **Settings → Playback** can automatically re-fetch your
active playlist and its EPG guide on a timer (every 30 min to 24 h, 3 hours by default) — the same
thing pressing **F5** does, just on its own. It's on by default, and quietly skips a turn (trying
again at the next interval) instead of refreshing right when it would interrupt something: while a
load is already underway, while fullscreen, or while you're in the middle of a search.

## Backup / Restore

**Settings → Backup** saves everything ZecaTV remembers — every saved playlist, favorites, and EPG
sources/mappings — to a `.json` file you choose. **Restore** loads one back, after confirming
(it replaces everything currently saved) — restart ZecaTV afterward for the restored data to take
effect everywhere.

## Where things are stored

`%AppData%\ZecaTV\`
- `settings.json` holds your saved playlists (and which one is active), favorites, EPG sources and channel mappings, each playlist's last category, the volume, and whether to resume the last channel on startup.
- `error.log` records unexpected errors. Attach it to bug reports.
- `vlc.log` records playback attempts and LibVLC's own warnings and errors, rewritten on each start. Attach it to playback bug reports. Stream URLs are shortened so logins aren't written to it.

## Project layout

| Folder | Contents |
|---|---|
| `Models/` | Channel, PlaylistSource, SavedPlaylist, Category, ContentType, ShowSummary |
| `Parsers/` | M3uParser, XtreamClient (ported from the Android app), ContentClassifier (Live/Movie/Show for M3U) |
| `Network/` | Shared HttpClient: lenient TLS, manual cross-protocol redirects |
| `Data/` | PlaylistRepository (separate Live/Movies/Shows pools), SettingsStore |
| `Player/` | PlayerService: the single shared LibVLC player |
| `UI/` | Channel list items, show tiles, logo cache, tray icon, helpers |
| `Themes/` | Dark theme (colours match the Android app) |
| `Assets/` | Icon artwork (currently just the placeholder `ZecaTV.ico`) |
| `installer/` (repo root, alongside `ZecaTV.sln`) | Portable-build and setup.exe scripts — see Installer & portable build above |

EPG-related windows (`EpgGuideWindow`, `EpgSourcesWindow`, `EpgMappingWindow`) live at the project
root alongside `MainWindow`, since WPF windows aren't usually nested in subfolders.

