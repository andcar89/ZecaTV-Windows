using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Media;
using ZecaTV.Models;

namespace ZecaTV.UI
{
    /// <summary>
    /// One row in the TV Shows tab's show grid (before a show is opened to its episode list — once
    /// opened, episodes are plain <see cref="ChannelItem"/> rows reusing ChannelTemplate). Mirrors
    /// ChannelItem's lazy-cover-loading pattern: the cover art is only requested the first time the UI
    /// reads <see cref="Cover"/>, so a virtualized list only fetches covers actually scrolled into view.
    /// </summary>
    public sealed class ShowItem : INotifyPropertyChanged
    {
        private ImageSource? _cover;
        private bool _coverRequested;

        public ShowItem(ShowSummary show)
        {
            Show = show;
            var first = show.Name.Trim();
            Initial = first.Length > 0 ? char.ToUpperInvariant(first[0]).ToString() : "?";
        }

        public ShowSummary Show { get; }

        public string Name => Show.Name;

        /// <summary>Placeholder letter shown until (or instead of) the cover art.</summary>
        public string Initial { get; }

        public ImageSource? Cover
        {
            get
            {
                EnsureCoverRequested();
                return _cover;
            }
        }

        public bool HasCover
        {
            get
            {
                EnsureCoverRequested();
                return _cover != null;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void EnsureCoverRequested()
        {
            if (_coverRequested) return;
            _coverRequested = true;

            var url = Show.CoverUrl;
            if (string.IsNullOrWhiteSpace(url)) return;

            var task = LogoCache.GetAsync(url.Trim());
            if (task.IsCompletedSuccessfully)
            {
                _cover = task.Result;
            }
            else
            {
                _ = ApplyWhenLoadedAsync(task);
            }
        }

        private async Task ApplyWhenLoadedAsync(Task<ImageSource?> task)
        {
            var cover = await task; // resumes on the UI thread (captured context)
            if (cover == null) return;
            _cover = cover;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCover)));
        }
    }
}
