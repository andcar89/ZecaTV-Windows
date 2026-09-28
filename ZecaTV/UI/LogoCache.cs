using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZecaTV.Data;
using ZecaTV.Models;
using ZecaTV.Network;

namespace ZecaTV.UI
{
    /// <summary>
    /// Downloads and decodes channel logos (the Glide role from Android). Logos are fetched through
    /// <see cref="Http"/> — not WPF's built-in image downloader — so they get the same lenient TLS and
    /// cross-protocol redirect handling as everything else; WPF's own loader fails silently on both.
    ///
    /// Two layers of caching:
    ///  - In-memory, for this run only: one shared task per URL (so 30 channels sharing a logo fetch
    ///    it once), at most 6 downloads at a time. A failed fetch caches as null for this run — the
    ///    item keeps showing its letter placeholder and isn't retried until the next app start.
    ///  - On disk, across runs: a successfully downloaded logo is saved under %AppData%\ZecaTV\logocache
    ///    keyed by a hash of its URL, so a restart doesn't re-download every logo in the playlist. This
    ///    is split into two buckets with different lifetimes, because they change for different reasons:
    ///    "local" (the active playlist is a local M3U file — logos only ever change when the user
    ///    re-imports that file, so cached copies are kept indefinitely) and "remote" (M3U URL or Xtream —
    ///    fetched from a provider that can swap a channel's icon at any time, so a cached copy is only
    ///    trusted for <see cref="RemoteCacheLifetime"/> before being re-downloaded). A failed download is
    ///    never written to disk, matching the in-memory behaviour above.
    /// </summary>
    public static class LogoCache
    {
        private const int DecodeHeight = 96;
        private static readonly TimeSpan RemoteCacheLifetime = TimeSpan.FromDays(30);

        private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Cache = new(StringComparer.Ordinal);
        private static readonly SemaphoreSlim Gate = new(6);

        public static Task<ImageSource?> GetAsync(string url) => Cache.GetOrAdd(url, LoadAsync);

        private static async Task<ImageSource?> LoadAsync(string url)
        {
            // Which bucket a logo belongs to follows the playlist that's asking for it right now — see
            // the class doc comment. Only one playlist is ever active at a time, so this is stable for
            // the lifetime of a single fetch.
            var isLocalSource = SettingsStore.ActivePlaylist()?.Source.Type == SourceType.M3uFile;
            var diskPath = DiskCachePath(url, isLocalSource);

            var cached = TryReadCached(diskPath, isLocalSource);
            if (cached != null)
            {
                var cachedImage = Decode(cached);
                if (cachedImage != null) return cachedImage;
                TryDelete(diskPath); // corrupt or no-longer-decodable entry — fall through and re-fetch
            }

            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var bytes = await Http.GetBytesAsync(url, cts.Token).ConfigureAwait(false);
                var image = Decode(bytes);
                if (image != null) TryWriteCache(diskPath, bytes);
                return image;
            }
            catch
            {
                return null;
            }
            finally
            {
                Gate.Release();
            }
        }

        private static string DiskCachePath(string url, bool isLocalSource)
        {
            var bucket = isLocalSource ? "local" : "remote";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
            return Path.Combine(SettingsStore.AppDataDir, "logocache", bucket, hash + ".img");
        }

        private static byte[]? TryReadCached(string path, bool isLocalSource)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                if (!isLocalSource && DateTime.UtcNow - info.LastWriteTimeUtc > RemoteCacheLifetime) return null;
                return File.ReadAllBytes(path);
            }
            catch
            {
                return null; // Corrupt file, permissions hiccup, etc. — just re-download instead.
            }
        }

        private static void TryWriteCache(string path, byte[] bytes)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Write-then-move so a crash or a concurrent reader never sees a half-written file.
                var tempPath = path + ".tmp";
                File.WriteAllBytes(tempPath, bytes);
                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                // Best-effort: a failed cache write just means this logo gets re-downloaded next time.
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }

        private static ImageSource? Decode(byte[] bytes)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelHeight = DecodeHeight;
                bitmap.StreamSource = new MemoryStream(bytes);
                bitmap.EndInit();
                bitmap.Freeze();
                return TrimPadding(bitmap) ?? bitmap;
            }
            catch
            {
                // SVG, HTML error pages served as 200, truncated files... all just mean "no logo".
                return null;
            }
        }

        /// <summary>
        /// Many IPTV logo images are a small mark centered on a much larger transparent (or flat-color)
        /// canvas — fine at full size, but tiny once WPF's Stretch="Uniform" fits that whole canvas into
        /// a small tile, since there's nothing empty left to crop into at display time. This finds the
        /// actual visible content's bounding box and crops down to it (with a little breathing room
        /// left around it), so a padded logo ends up filling the tile the way an already-tight one does.
        /// Returns null (meaning: use the bitmap as decoded) whenever there's nothing sensible to trim —
        /// a genuinely full-bleed logo, a solid/blank image, or a non-flat border it can't safely judge.
        /// </summary>
        private static ImageSource? TrimPadding(BitmapSource source)
        {
            try
            {
                var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                var width = converted.PixelWidth;
                var height = converted.PixelHeight;
                if (width < 6 || height < 6) return null;

                var stride = width * 4;
                var pixels = new byte[height * stride];
                converted.CopyPixels(pixels, stride, 0);

                var hasAlpha = FormatHasAlpha(source.Format);
                const int colorTolerance = 14;
                byte bgB = 0, bgG = 0, bgR = 0;

                if (!hasAlpha)
                {
                    // No alpha channel to judge by — fall back to whatever flat color the four
                    // corners agree on (most padded logo canvases are a solid color, not just
                    // transparent, when they carry no alpha at all). If the corners disagree, there's
                    // no single background to trim against, so leave the image alone.
                    var (b0, g0, r0) = PixelAt(pixels, stride, 0, 0);
                    var (b1, g1, r1) = PixelAt(pixels, stride, width - 1, 0);
                    var (b2, g2, r2) = PixelAt(pixels, stride, 0, height - 1);
                    var (b3, g3, r3) = PixelAt(pixels, stride, width - 1, height - 1);
                    if (!ColorsClose(b0, g0, r0, b1, g1, r1, colorTolerance) ||
                        !ColorsClose(b0, g0, r0, b2, g2, r2, colorTolerance) ||
                        !ColorsClose(b0, g0, r0, b3, g3, r3, colorTolerance))
                    {
                        return null;
                    }
                    (bgB, bgG, bgR) = (b0, g0, r0);
                }

                int minX = width, minY = height, maxX = -1, maxY = -1;
                const byte alphaThreshold = 12;

                for (var y = 0; y < height; y++)
                {
                    var rowOffset = y * stride;
                    for (var x = 0; x < width; x++)
                    {
                        var i = rowOffset + x * 4;
                        var isContent = hasAlpha
                            ? pixels[i + 3] >= alphaThreshold
                            : !ColorsClose(pixels[i], pixels[i + 1], pixels[i + 2], bgB, bgG, bgR, colorTolerance);
                        if (!isContent) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }

                if (maxX < minX || maxY < minY) return null; // blank/uniform image — nothing to crop to

                var contentWidth = maxX - minX + 1;
                var contentHeight = maxY - minY + 1;

                // Barely any padding to begin with — not worth the crop.
                if (contentWidth >= width * 0.94 && contentHeight >= height * 0.94) return null;

                // A little breathing room around the trimmed content rather than an edge-to-edge crop.
                var padX = Math.Max(1, (int)(contentWidth * 0.06));
                var padY = Math.Max(1, (int)(contentHeight * 0.06));
                var x0 = Math.Max(0, minX - padX);
                var y0 = Math.Max(0, minY - padY);
                var x1 = Math.Min(width, maxX + 1 + padX);
                var y1 = Math.Min(height, maxY + 1 + padY);

                var cropped = new CroppedBitmap(source, new Int32Rect(x0, y0, x1 - x0, y1 - y0));
                cropped.Freeze();
                return cropped;
            }
            catch
            {
                // Any surprise here (odd pixel format, etc.) just means: show the logo untrimmed.
                return null;
            }
        }

        private static bool FormatHasAlpha(PixelFormat format) =>
            format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 ||
            format == PixelFormats.Rgba64 || format == PixelFormats.Prgba64 ||
            format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float;

        private static (byte b, byte g, byte r) PixelAt(byte[] pixels, int stride, int x, int y)
        {
            var i = y * stride + x * 4;
            return (pixels[i], pixels[i + 1], pixels[i + 2]);
        }

        private static bool ColorsClose(byte b1, byte g1, byte r1, byte b2, byte g2, byte r2, int tolerance) =>
            Math.Abs(b1 - b2) <= tolerance && Math.Abs(g1 - g2) <= tolerance && Math.Abs(r1 - r2) <= tolerance;
    }
}
