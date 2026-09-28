using System;
using System.IO;
using System.Text.RegularExpressions;
using LibVLCSharp.Shared;
using ZecaTV.Data;

namespace ZecaTV.Player
{
    /// <summary>
    /// Writes LibVLC's own warnings/errors (plus one line per play attempt and every VLC dialog) to
    /// %AppData%\ZecaTV\vlc.log, overwritten on each app start. This is the "why won't it play"
    /// record — attach it to playback bug reports.
    ///
    /// Called from LibVLC's threads, so it only ever touches the file (never LibVLC itself).
    /// Stream URLs are shortened to scheme://host:port/…/file so Xtream logins don't end up in it.
    /// </summary>
    internal sealed class VlcLog : IDisposable
    {
        private static readonly Regex UrlRegex = new(@"\b([a-z][a-z0-9+.-]*://[^/\s""']+)/[^\s""']*?([^/\s""'?]*)(\?[^\s""']*)?(?=[\s""']|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const int MaxLines = 20000; // stops a long session's repeated warnings filling the disk

        private readonly object _lock = new();
        private readonly StreamWriter? _writer;
        private int _lines;

        public static string FilePath => Path.Combine(SettingsStore.AppDataDir, "vlc.log");

        public VlcLog()
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.AppDataDir);
                _writer = new StreamWriter(FilePath, append: false) { AutoFlush = true };
                Write("ZecaTV started");
            }
            catch
            {
                _writer = null; // logging is best-effort
            }
        }

        public void Write(string message)
        {
            if (_writer == null) return;
            lock (_lock)
            {
                try
                {
                    if (_lines > MaxLines) return;
                    _lines++;
                    _writer.WriteLine(_lines > MaxLines
                        ? "… log size limit reached, nothing more is written this session"
                        : $"{DateTime.Now:HH:mm:ss.fff}  {Redact(message)}");
                }
                catch
                {
                    // ignore
                }
            }
        }

        public void Write(LogEventArgs e) =>
            Write($"[{e.Level}] {e.Module ?? "?"}: {e.Message}");

        public static string Redact(string text) =>
            UrlRegex.Replace(text, m => $"{m.Groups[1].Value}/…/{m.Groups[2].Value}");

        public void Dispose()
        {
            lock (_lock)
            {
                try { _writer?.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
