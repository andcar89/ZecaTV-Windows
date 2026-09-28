using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Media;
using ZecaTV.Data;
using ZecaTV.Models;

namespace ZecaTV.UI
{
    /// <summary>
    /// One row in the channel list. Created once per channel per playlist load and reused across
    /// categories, so a logo loaded in "All channels" is already there in "News".
    ///
    /// The logo is requested the first time the UI reads <see cref="Logo"/> — and the list is
    /// virtualized, so that only happens for rows actually scrolled into view.
    /// </summary>
    public sealed class ChannelItem : INotifyPropertyChanged
    {
        private ImageSource? _logo;
        private bool _logoRequested;
        private bool _isPlaying;
        private bool _isFavorite;
        private string _subtitle = "";

        /// <param name="staticSubtitle">For a Movie/Series row (Number is 0 for both): an initial, fixed
        /// Subtitle — e.g. "S01E02" — that <see cref="UpdateEpg"/> will never overwrite, since those
        /// rows have no live EPG guide entry. Null for a Live channel, whose Subtitle instead tracks the
        /// EPG's now/next text via UpdateEpg.</param>
        public ChannelItem(Channel channel, int number, string? staticSubtitle = null)
        {
            Channel = channel;
            Number = number;
            var first = channel.Name.Trim();
            Initial = first.Length > 0 ? char.ToUpperInvariant(first[0]).ToString() : "?";
            _isFavorite = FavoritesManager.IsFavorite(channel.Key);
            if (staticSubtitle != null) _subtitle = staticSubtitle;
        }

        public Channel Channel { get; }

        /// <summary>1-based position in the full playlist (stable across categories).</summary>
        public int Number { get; }

        public string Name => Channel.Name;

        /// <summary>Placeholder letter shown until (or instead of) the logo.</summary>
        public string Initial { get; }

        public ImageSource? Logo
        {
            get
            {
                EnsureLogoRequested();
                return _logo;
            }
        }

        public bool HasLogo
        {
            get
            {
                EnsureLogoRequested();
                return _logo != null;
            }
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (_isPlaying == value) return;
                _isPlaying = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying)));
            }
        }

        /// <summary>Now/next EPG text ("News · until 21:00", "Next: Movie · 22:00", or "" if no guide data). See UpdateEpg.</summary>
        public string Subtitle
        {
            get => _subtitle;
            private set
            {
                if (_subtitle == value) return;
                _subtitle = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
            }
        }

        /// <summary>
        /// Recomputes <see cref="Subtitle"/> from the EPG store for the given moment. Cheap to call
        /// often (a hashmap lookup plus a binary search) but only worth calling for channels actually
        /// on screen — see MainWindow's periodic refresh of the visible lists.
        /// </summary>
        public void UpdateEpg(EpgStore store, DateTimeOffset now)
        {
            // Movies/episodes have no live EPG guide entry — leave whatever Subtitle the row was given
            // (e.g. a static "S01E02" label for an episode; blank for a movie) alone.
            if (Channel.ContentType != ContentType.Live) return;

            var (current, next) = store.NowNext(Channel, now.ToUnixTimeMilliseconds());
            if (current != null)
            {
                var until = DateTimeOffset.FromUnixTimeMilliseconds(current.StopMillis).ToLocalTime();
                Subtitle = $"{current.Title} · until {until:HH:mm}";
            }
            else if (next != null)
            {
                var start = DateTimeOffset.FromUnixTimeMilliseconds(next.StartMillis).ToLocalTime();
                Subtitle = $"Next: {next.Title} · {start:HH:mm}";
            }
            else
            {
                Subtitle = "";
            }
        }

        /// <summary>Mirrors FavoritesManager for this channel's Key; updated by MainWindow when toggled.</summary>
        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite == value) return;
                _isFavorite = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void EnsureLogoRequested()
        {
            if (_logoRequested) return;
            _logoRequested = true;

            var url = Channel.LogoUrl;
            if (string.IsNullOrWhiteSpace(url)) return;

            var task = LogoCache.GetAsync(url.Trim());
            if (task.IsCompletedSuccessfully)
            {
                // Already cached: just use it — no change notification needed (or wanted) while
                // the binding engine is in the middle of reading this very property.
                _logo = task.Result;
            }
            else
            {
                _ = ApplyWhenLoadedAsync(task);
            }
        }

        private async Task ApplyWhenLoadedAsync(Task<ImageSource?> task)
        {
            var logo = await task; // resumes on the UI thread (captured context)
            if (logo == null) return;
            _logo = logo;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Logo)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLogo)));
        }
    }
}
