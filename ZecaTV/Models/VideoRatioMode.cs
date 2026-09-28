namespace ZecaTV.Models
{
    /// <summary>
    /// How the video is fitted into its display area — set from the right-click menu on the video
    /// itself (Overlay_ContextMenuOpening in MainWindow), applied via PlayerService.SetAspectRatio.
    /// </summary>
    public enum VideoRatioMode
    {
        /// <summary>Default: whatever the stream reports, letterboxed/pillarboxed to fit — nothing cropped or distorted.</summary>
        Fit,

        /// <summary>Stretched to exactly fill the video area, distorting the picture if its own ratio doesn't already match.</summary>
        Fill,

        /// <summary>Forces classic 4:3, distorting a widescreen stream that reports the wrong ratio.</summary>
        Ratio4_3,

        /// <summary>Forces widescreen 16:9, distorting a stream that reports the wrong ratio.</summary>
        Ratio16_9
    }
}
