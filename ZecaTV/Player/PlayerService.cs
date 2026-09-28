using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures; // TrackDescription lives here, not in LibVLCSharp.Shared itself
using ZecaTV.Localization;
using ZecaTV.Models;
using ZecaTV.Network;

namespace ZecaTV.Player
{
    public enum PlaybackState
    {
        Idle,
        Connecting,
        Playing,
        Reconnecting,
        Failed
    }

    /// <summary>
    /// Owns the single LibVLC MediaPlayer for the whole app — the Windows counterpart of the Android
    /// PlayerHolder. The same VideoView shows both the windowed preview and fullscreen (fullscreen just
    /// expands it), so going fullscreen never touches the connection at all, and <see cref="Play"/> is a
    /// no-op for the channel that's already playing (matched by <see cref="Channel.Key"/>).
    ///
    /// Threading rules this class exists to enforce:
    ///  - LibVLC's Play()/Stop() can block for seconds on a slow or dead stream, so they never run on
    ///    the UI thread. They go through a one-at-a-time background queue, and a request that's been
    ///    superseded before it starts (fast zapping A→B→C) is skipped instead of played.
    ///  - LibVLC raises events on its own threads, and calling back into LibVLC from inside one of its
    ///    event handlers can deadlock. Every handler here only posts to the UI dispatcher.
    ///
    /// Recovery (mirrors Android's 15s stall re-prepare, extended for LibVLC): a stream that errors,
    /// ends, never gets going, or stops advancing is restarted automatically, up to
    /// <see cref="MaxAutoRetries"/> times in a row before giving up and showing an error.
    /// </summary>
    public sealed class PlayerService : IDisposable
    {
        private const int MaxAutoRetries = 3;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);

        private readonly Dispatcher _dispatcher;
        private readonly object _queueLock = new();
        private readonly DispatcherTimer _watchdog;
        private readonly VlcLog _log = new();
        private Task _queue = Task.CompletedTask;

        // Incremented (UI thread) for every play/stop request. The background queue skips work whose
        // id is no longer the latest; events are tagged with the id that was active when they fired.
        private int _requestId;
        private int _activeRequestId;

        private int _retries;
        private DateTime _stateSince = DateTime.UtcNow;
        private long _lastTime = -1;
        private DateTime _lastProgressAt = DateTime.UtcNow;
        private bool _sawProgress;
        private int _volume = 100;
        private bool _muted;
        private bool _disposed;

        // First LibVLC error (log line or error dialog) of the current play attempt — usually the
        // real reason ("HTTP 403", TLS failure...) rather than the generic message that follows it.
        private string? _attemptError;

        // Set (1) when the current attempt logged a TLS handshake failure.
        private int _attemptTlsFailed;

        public LibVLC LibVLC { get; }
        public MediaPlayer MediaPlayer { get; }

        public Channel? CurrentChannel { get; private set; }
        public PlaybackState State { get; private set; } = PlaybackState.Idle;

        /// <summary>True while paused — only ever reachable via <see cref="SetPaused"/> on a stream that
        /// reports <see cref="CanPause"/>, i.e. in practice a Movie/episode, not a Live channel. Reset by
        /// <see cref="Start"/>/<see cref="Stop"/>, and updated from LibVLC's own Paused/Playing events so
        /// it always reflects what's actually happening rather than what was last requested.</summary>
        public bool IsPaused { get; private set; }

        /// <summary>Non-null while watching Xtream catch-up instead of live — the programme being watched.
        /// Cleared by <see cref="Stop"/>, <see cref="ReturnToLive"/>, or playing any channel live again.</summary>
        public EpgProgramme? CatchupProgramme { get; private set; }

        /// <summary>Set instead of channel.StreamUrl by <see cref="Start"/> while a catch-up URL is in effect.</summary>
        private string? _overrideUrl;

        /// <summary>User-facing reason when <see cref="State"/> is Failed.</summary>
        public string? LastError { get; private set; }

        /// <summary>Raised on the UI thread whenever <see cref="State"/> or <see cref="CurrentChannel"/> changes.</summary>
        public event EventHandler? StateChanged;

        public PlayerService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;

            LibVLC = new LibVLC("--no-video-title-show");
            MediaPlayer = new MediaPlayer(LibVLC)
            {
                EnableHardwareDecoding = true,
                // Let mouse/keyboard fall through to our own WPF handlers instead of VLC's video window.
                EnableKeyInput = false,
                EnableMouseInput = false
            };

            LibVLC.Log += OnVlcLog;
            LibVLC.SetDialogHandlers(OnDialogError, OnDialogLogin, OnDialogQuestion, OnDialogProgress, OnDialogProgressUpdated);

            MediaPlayer.Playing += (_, _) => Post(OnPlaying);
            MediaPlayer.Paused += (_, _) => Post(OnPaused);
            MediaPlayer.EncounteredError += (_, _) => Post(id => OnStreamProblem(id, Strings.ErrorCouldntPlayChannel));
            MediaPlayer.EndReached += (_, _) => Post(OnEndReached);

            _watchdog = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _watchdog.Tick += (_, _) => WatchdogTick();
            _watchdog.Start();
        }

        // ---- Public API (UI thread) -------------------------------------------------------------

        /// <summary>
        /// Plays <paramref name="channel"/>. No-op if it's already the current channel and not failed,
        /// preserving the existing connection/buffer (e.g. preview → fullscreen, or re-clicking).
        /// </summary>
        public void Play(Channel channel)
        {
            if (CurrentChannel?.Key == channel.Key && CatchupProgramme == null &&
                State != PlaybackState.Failed && State != PlaybackState.Idle)
            {
                return;
            }

            CurrentChannel = channel;
            CatchupProgramme = null;
            _overrideUrl = null;
            _retries = 0;
            Start(PlaybackState.Connecting);
        }

        /// <summary>
        /// Plays a catch-up (timeshift) recording of <paramref name="programme"/> on <paramref name="channel"/>
        /// instead of its live stream. <paramref name="catchupUrl"/> is built by
        /// ZecaTV.Data.PlaylistRepository.BuildCatchupUrl — this method just plays whatever URL it's given.
        /// </summary>
        public void PlayCatchup(Channel channel, EpgProgramme programme, string catchupUrl)
        {
            CurrentChannel = channel;
            CatchupProgramme = programme;
            _overrideUrl = catchupUrl;
            _retries = 0;
            Start(PlaybackState.Connecting);
        }

        /// <summary>Switches back to live playback of the channel that was last watched via catch-up (or does nothing if not currently in catch-up).</summary>
        public void ReturnToLive()
        {
            if (CatchupProgramme == null || CurrentChannel == null) return;
            var channel = CurrentChannel;
            CatchupProgramme = null;
            _overrideUrl = null;
            _retries = 0;
            CurrentChannel = channel;
            Start(PlaybackState.Connecting);
        }

        public void Stop()
        {
            CurrentChannel = null;
            CatchupProgramme = null;
            _overrideUrl = null;
            _retries = 0;
            LastError = null;
            IsPaused = false;
            SetState(PlaybackState.Idle);

            var id = ++_requestId;
            Enqueue(id, () => MediaPlayer.Stop());
        }

        // ---- Audio / subtitle tracks --------------------------------------------------------------
        // LibVLC populates these once a stream actually starts playing, not before — callers (the
        // right-click menu) only need to read them while State == Playing.

        /// <summary>Every audio track LibVLC found in the current stream. Empty before/without playback.</summary>
        public IReadOnlyList<(int Id, string Name)> AudioTracks => Tracks(SafeGet(() => MediaPlayer.AudioTrackDescription, null));

        /// <summary>Every subtitle track LibVLC found in the current stream (never includes the synthetic
        /// "disabled" entry LibVLC itself reports — <see cref="CurrentSubtitleTrack"/> being -1 already
        /// means "off", so the UI only needs one "Off" choice of its own).</summary>
        public IReadOnlyList<(int Id, string Name)> SubtitleTracks => Tracks(SafeGet(() => MediaPlayer.SpuDescription, null));

        /// <summary>LibVLC id of the currently selected audio track, or -1.</summary>
        public int CurrentAudioTrack => SafeGet(() => MediaPlayer.AudioTrack, -1);

        /// <summary>LibVLC id of the currently selected subtitle track, or -1 (subtitles off).</summary>
        public int CurrentSubtitleTrack => SafeGet(() => MediaPlayer.Spu, -1);

        public void SetAudioTrack(int id)
        {
            try { if (!MediaPlayer.SetAudioTrack(id)) Debug.WriteLine($"Setting audio track {id} was rejected by LibVLC"); }
            catch (Exception ex) { Debug.WriteLine($"Setting audio track failed: {ex}"); }
        }

        /// <summary>-1 turns subtitles off.</summary>
        public void SetSubtitleTrack(int id)
        {
            try { if (!MediaPlayer.SetSpu(id)) Debug.WriteLine($"Setting subtitle track {id} was rejected by LibVLC"); }
            catch (Exception ex) { Debug.WriteLine($"Setting subtitle track failed: {ex}"); }
        }

        /// <summary>
        /// Forces LibVLC's display aspect ratio, e.g. "4:3", "16:9", or a computed "&lt;width&gt;:&lt;height&gt;"
        /// to stretch-fill an arbitrary area. Null restores the stream's own reported ratio (letterboxed
        /// to fit, nothing distorted). Applied immediately, and safe to call whether or not something is
        /// playing yet — the caller (MainWindow) re-applies it on every new Playing event too, since a
        /// fresh Media/vout can reset LibVLC's own aspect ratio back to default.
        /// </summary>
        public void SetAspectRatio(string? ratio)
        {
            try { MediaPlayer.AspectRatio = ratio; }
            catch (Exception ex) { Debug.WriteLine($"Setting aspect ratio failed: {ex}"); }
        }

        // ---- Pause / seek (VOD only — Live streams report CanPause/CanSeek false) -----------------

        /// <summary>Whether the current stream can be paused at all — true for Movies/episodes once LibVLC
        /// has opened them, false for Live TV (and before anything has started playing).</summary>
        public bool CanPause => SafeGet(() => MediaPlayer.CanPause, false);

        /// <summary>Whether the current stream supports seeking — same Live-vs-VOD split as <see cref="CanPause"/>.</summary>
        public bool CanSeek => SafeGet(() => MediaPlayer.IsSeekable, false);

        /// <summary>Current playback position, in milliseconds, or -1 if not currently playing/known.</summary>
        public long PositionMillis => SafeGet(() => MediaPlayer.Time, -1L);

        /// <summary>Total stream duration, in milliseconds, or -1 if unknown (always the case for Live TV;
        /// briefly the case for a VOD stream too, until LibVLC has parsed enough of it to know).</summary>
        public long DurationMillis => SafeGet(() => MediaPlayer.Length, -1L);

        /// <summary>Pauses or resumes. A no-op (not an error) when the current stream can't be paused —
        /// callers don't need to check <see cref="CanPause"/> first, though the UI hides the button then anyway.</summary>
        public void SetPaused(bool paused)
        {
            try
            {
                if (!MediaPlayer.CanPause) return;
                MediaPlayer.SetPause(paused);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetPaused({paused}) failed: {ex}");
            }
        }

        public void TogglePause() => SetPaused(!IsPaused);

        /// <summary>Jumps to an absolute position (e.g. from dragging the seek bar). A no-op when the
        /// current stream isn't seekable.</summary>
        public void SeekTo(long positionMillis)
        {
            try
            {
                if (!MediaPlayer.IsSeekable) return;
                MediaPlayer.Time = Math.Max(0, positionMillis);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SeekTo({positionMillis}) failed: {ex}");
            }
        }

        private static IReadOnlyList<(int Id, string Name)> Tracks(TrackDescription[]? descriptions)
        {
            if (descriptions == null || descriptions.Length == 0) return Array.Empty<(int, string)>();
            var list = new List<(int, string)>(descriptions.Length);
            foreach (var d in descriptions)
            {
                if (d.Id == -1) continue; // LibVLC's own "disabled" placeholder — not a real track
                list.Add((d.Id, string.IsNullOrWhiteSpace(d.Name) ? $"Track {d.Id}" : d.Name));
            }
            return list;
        }

        private static T SafeGet<T>(Func<T> getter, T fallback)
        {
            try { return getter(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"Reading a LibVLC track property failed: {ex}");
                return fallback;
            }
        }

        /// <summary>0–100. Applied now if audio is running, and re-applied whenever a stream starts.</summary>
        public int Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0, 100);
                ApplyAudio();
            }
        }

        public bool Muted
        {
            get => _muted;
            set
            {
                _muted = value;
                ApplyAudio();
            }
        }

        // ---- Internals ---------------------------------------------------------------------------

        private void Start(PlaybackState state)
        {
            var channel = CurrentChannel;
            if (channel == null) return;

            LastError = null;
            IsPaused = false;
            _sawProgress = false;
            _lastTime = -1;
            Interlocked.Exchange(ref _attemptError, null);
            Interlocked.Exchange(ref _attemptTlsFailed, 0);

            // http:// instead of a mislabelled https:// if this server is already known to need it —
            // applies just as much to a catch-up URL, since it's the same panel on the same port.
            var url = Http.PreferredUrl(_overrideUrl ?? channel.StreamUrl);
            var suffix = state == PlaybackState.Reconnecting ? $" (retry {_retries})" : "";
            _log.Write((CatchupProgramme != null ? $"PLAY CATCHUP \"{channel.Name}\" - \"{CatchupProgramme.Title}\" " : $"PLAY \"{channel.Name}\" ") + url + suffix);
            SetState(state);

            var id = ++_requestId;
            Enqueue(id, () =>
            {
                using var media = new Media(LibVLC, url, FromType.FromLocation,
                    ":http-user-agent=" + Http.UserAgent,
                    ":network-caching=1000",
                    ":http-reconnect");
                MediaPlayer.Play(media);
            });
        }

        /// <summary>Runs <paramref name="work"/> on the background queue unless request <paramref name="id"/> has been superseded by then.</summary>
        private void Enqueue(int id, Action work)
        {
            lock (_queueLock)
            {
                _queue = _queue.ContinueWith(_ =>
                {
                    if (id != Volatile.Read(ref _requestId)) return; // superseded — skip
                    Volatile.Write(ref _activeRequestId, id);
                    try
                    {
                        work();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"LibVLC call failed: {ex}");
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        /// <summary>From a LibVLC event thread: hop to the UI thread, tagged with the request that was active.</summary>
        private void Post(Action<int> handler)
        {
            var id = Volatile.Read(ref _activeRequestId);
            if (_disposed || _dispatcher.HasShutdownStarted) return;
            _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed) handler(id);
            });
        }

        private bool IsCurrent(int id) => id == _requestId && CurrentChannel != null;

        private void OnPlaying(int id)
        {
            if (!IsCurrent(id)) return;
            var wasPaused = IsPaused;
            IsPaused = false;
            if (State is PlaybackState.Connecting or PlaybackState.Reconnecting)
            {
                _log.Write("Playing");
                SetState(PlaybackState.Playing); // already raises StateChanged
            }
            else if (wasPaused)
            {
                // Resumed from pause — State was already Playing throughout, but IsPaused just changed,
                // so the UI (play/pause button) still needs telling.
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            ApplyAudio();
        }

        private void OnPaused(int id)
        {
            if (!IsCurrent(id) || IsPaused) return;
            IsPaused = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>A Live stream ending is unexpected (dropped connection etc.) and goes through the normal
        /// retry/error path — but a Movie/episode reaching the end of its runtime is the expected outcome,
        /// not a problem, so it just stops cleanly instead of pointlessly retrying/reconnecting.</summary>
        private void OnEndReached(int id)
        {
            if (!IsCurrent(id)) return;
            if (CurrentChannel is { ContentType: not ContentType.Live })
            {
                _log.Write("End of VOD stream reached");
                Stop();
                return;
            }
            OnStreamProblem(id, Strings.ErrorStreamEnded);
        }

        private async void OnStreamProblem(int id, string message)
        {
            if (!IsCurrent(id) || State == PlaybackState.Failed) return;

            // TLS handshake failed on an https:// stream: the server almost certainly speaks plain
            // HTTP on that port (see Http). Switch right away — this isn't a flaky stream, so it
            // doesn't use up one of the retries.
            if (Volatile.Read(ref _attemptTlsFailed) == 1 && Http.MarkPlainHttp(CurrentChannel!.StreamUrl))
            {
                _log.Write("TLS handshake failed — server doesn't speak HTTPS on this port, switching to plain HTTP");
                Start(State == PlaybackState.Reconnecting ? PlaybackState.Reconnecting : PlaybackState.Connecting);
                return;
            }

            var detail = Volatile.Read(ref _attemptError);
            _log.Write($"Problem: {message}" + (detail != null ? $" — {detail}" : ""));

            if (_retries >= MaxAutoRetries)
            {
                LastError = detail != null ? $"{message}\n{VlcLog.Redact(detail)}" : message;
                _log.Write("Giving up after " + MaxAutoRetries + " retries");
                SetState(PlaybackState.Failed);
                return;
            }

            _retries++;
            SetState(PlaybackState.Reconnecting);

            var expected = _requestId;
            await Task.Delay(RetryDelay);
            if (_requestId == expected && CurrentChannel != null && !_disposed)
            {
                Start(PlaybackState.Reconnecting);
            }
        }

        private void WatchdogTick()
        {
            if (CurrentChannel == null) return;
            var now = DateTime.UtcNow;

            // Stuck opening: the server accepted the connection but never delivered anything usable.
            if (State is PlaybackState.Connecting or PlaybackState.Reconnecting &&
                now - _stateSince > ConnectTimeout)
            {
                OnStreamProblem(_requestId, Strings.ErrorChannelNoResponse);
                return;
            }

            if (State != PlaybackState.Playing) return;
            if (IsPaused) return; // an intentionally paused stream isn't "stalled" — see SetPaused

            var time = MediaPlayer.Time;
            if (time != _lastTime)
            {
                _lastTime = time;
                _lastProgressAt = now;
                if (time > 0)
                {
                    if (!_sawProgress)
                    {
                        _sawProgress = true;
                        // Audio output often doesn't exist yet at the Playing event, so volume set
                        // there can be lost — re-apply once the stream is genuinely moving.
                        ApplyAudio();
                    }
                    _retries = 0; // a healthy stream earns a fresh set of retries for its next drop
                }
                return;
            }

            // Only judge stalls once time has been seen advancing — some streams never report time,
            // and those shouldn't be restarted every 15 seconds.
            if (_sawProgress && now - _lastProgressAt > StallTimeout)
            {
                Debug.WriteLine($"Stall >15s on {CurrentChannel.StreamUrl}, restarting");
                _sawProgress = false;
                OnStreamProblem(_requestId, Strings.ErrorStreamStopped);
            }
        }

        // ---- LibVLC log + dialogs (called on LibVLC threads: file writes only, never LibVLC calls
        //      except answering the dialog itself, which is done off-thread) ---------------------

        private void OnVlcLog(object? sender, LogEventArgs e)
        {
            if (e.Level < LogLevel.Warning) return;
            _log.Write(e);
            if (e.Level != LogLevel.Error) return;
            Interlocked.CompareExchange(ref _attemptError, e.Message, null);
            if (e.Module == "gnutls" || e.Message.Contains("TLS", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _attemptTlsFailed, 1);
            }
        }

        private Task OnDialogError(string? title, string? text)
        {
            _log.Write($"VLC error dialog: {title} — {text}");
            if (!string.IsNullOrWhiteSpace(text)) Interlocked.CompareExchange(ref _attemptError, text, null);
            return Task.CompletedTask;
        }

        private Task OnDialogLogin(Dialog dialog, string? title, string? text, string? defaultUsername,
            bool askStore, CancellationToken token)
        {
            _log.Write($"VLC login request (dismissed): {title} — {text}");
            Interlocked.CompareExchange(ref _attemptError, Strings.ErrorNeedsLogin, null);
            return Task.Run(() => TryDialog(() => dialog.Dismiss()));
        }

        /// <summary>
        /// VLC asks before trusting a broken/self-signed HTTPS certificate: "Insecure site" →
        /// [View certificate], then the certificate → [Accept 24 hours]. Answering the first action both
        /// times is LibVLC's version of the Android app's LenientTls: the same deliberate trade-off,
        /// scoped to the streams of the provider the user chose. (With no answer at all, VLC simply
        /// refuses the stream — one likely cause of "Couldn't play this channel".)
        /// </summary>
        private Task OnDialogQuestion(Dialog dialog, string? title, string? text, DialogQuestionType type,
            string? cancelText, string? firstActionText, string? secondActionText, CancellationToken token)
        {
            var shortText = text == null ? "" : new string(text.Take(300).ToArray());
            _log.Write($"VLC question: {title} — {shortText} → answering \"{firstActionText ?? cancelText}\"");
            return Task.Run(() => TryDialog(() =>
            {
                if (firstActionText != null) dialog.PostAction(1);
                else dialog.Dismiss();
            }));
        }

        private Task OnDialogProgress(Dialog dialog, string? title, string? text, bool indeterminate,
            float position, string? cancelText, CancellationToken token) => Task.CompletedTask;

        private Task OnDialogProgressUpdated(Dialog dialog, float position, string? text) => Task.CompletedTask;

        private void TryDialog(Action answer)
        {
            try
            {
                answer();
            }
            catch (Exception ex)
            {
                _log.Write($"Answering VLC dialog failed: {ex.Message}");
            }
        }

        private void ApplyAudio()
        {
            try
            {
                MediaPlayer.Volume = _volume;
                MediaPlayer.Mute = _muted;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Applying volume failed: {ex}");
            }
        }

        private void SetState(PlaybackState state)
        {
            State = state;
            _stateSince = DateTime.UtcNow;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Stops playback (bounded wait so a hung stream can't block app exit) and releases LibVLC.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _watchdog.Stop();

            var stopped = Task.Run(() =>
            {
                try
                {
                    MediaPlayer.Stop();
                    LibVLC.Log -= OnVlcLog;
                    MediaPlayer.Dispose();
                    LibVLC.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Player shutdown failed: {ex}");
                }
            });
            stopped.Wait(TimeSpan.FromSeconds(3));
            _log.Dispose();
        }
    }
}
