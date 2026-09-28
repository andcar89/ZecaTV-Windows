using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

namespace ZecaTV.Network
{
    /// <summary>
    /// The one shared HttpClient for playlist, Xtream API, EPG and logo requests.
    /// Windows counterpart of the Android app's LenientTls + OkHttp setup, and it carries over both
    /// lessons from there:
    ///
    /// 1. Lenient TLS. Many IPTV panels serve HTTPS with incomplete or self-signed certificate chains.
    ///    Certificate validation is skipped for requests made through THIS client only — a deliberate,
    ///    contained trade-off (traffic to the provider the user chose), not the app's TLS trust in
    ///    general. It does mean these requests would accept a forged certificate from a man-in-the-middle.
    ///
    /// 2. Cross-protocol redirects. .NET's built-in redirect handling refuses HTTPS -> HTTP redirects,
    ///    which IPTV panels do constantly (the same silent failure OkHttp had). So automatic redirects
    ///    are OFF and <see cref="GetAsync"/> follows them manually, in any direction.
    ///
    /// 3. https:// addresses that aren't really HTTPS. Some playlists label streams https:// on ports
    ///    (like 8080) where the server actually speaks plain HTTP, so the TLS handshake can never
    ///    succeed. When a handshake fails on an https:// address, that host:port is remembered as
    ///    plain-HTTP and the request is retried as http:// — here and, via <see cref="PreferredUrl"/>,
    ///    for video streams in PlayerService too.
    /// </summary>
    public static class Http
    {
        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ZecaTV/1.0";

        private const int MaxRedirects = 10;

        private static readonly HttpClient Client = CreateClient();

        /// <summary>"host:port" of servers whose https:// addresses turned out to be plain HTTP.</summary>
        private static readonly ConcurrentDictionary<string, byte> PlainHttpServers = new(StringComparer.OrdinalIgnoreCase);

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(20),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }
            };

            var client = new HttpClient(handler)
            {
                // Covers the wait for response headers only (every request here reads with
                // ResponseHeadersRead), so a slow multi-MB playlist/EPG body isn't cut off by it.
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            return client;
        }

        /// <summary>
        /// GETs <paramref name="url"/>, following redirects manually (any protocol direction).
        /// Returns the response with headers read and the body still unread — the caller disposes it.
        /// Throws <see cref="HttpRequestException"/> ("HTTP 404" etc.) on a non-success status.
        /// </summary>
        public static async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default)
        {
            var current = new Uri(url.Trim());

            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (IsKnownPlainHttp(current)) current = ToPlainHttp(current);

                HttpResponseMessage response;
                using (var request = new HttpRequestMessage(HttpMethod.Get, current))
                {
                    try
                    {
                        response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                            .ConfigureAwait(false);
                    }
                    catch (HttpRequestException ex) when (
                        ex.HttpRequestError == HttpRequestError.SecureConnectionError &&
                        current.Scheme == Uri.UriSchemeHttps)
                    {
                        // TLS handshake failed on an https:// address: mark the server and try the
                        // same address over plain HTTP (the loop top rewrites it). Can't loop — once
                        // marked, this server is never contacted over https:// again.
                        MarkPlainHttp(current.AbsoluteUri);
                        continue;
                    }
                }

                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    response.Dispose();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException($"HTTP {code}", null, (HttpStatusCode)code);
                }

                return response;
            }

            throw new HttpRequestException("Too many redirects");
        }

        /// <summary>
        /// <paramref name="url"/> as it should actually be requested: rewritten to http:// if its
        /// server is known to speak plain HTTP despite the https:// label, otherwise unchanged.
        /// </summary>
        public static string PreferredUrl(string url)
        {
            if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && IsKnownPlainHttp(uri))
            {
                return ToPlainHttp(uri).AbsoluteUri;
            }
            return url;
        }

        /// <summary>
        /// Records that <paramref name="url"/>'s server answered an HTTPS handshake with plain HTTP.
        /// Returns true only if this is an https:// address whose server wasn't already marked —
        /// i.e. when a retry over http:// is worth making (and can't loop).
        /// </summary>
        public static bool MarkPlainHttp(string url)
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }
            return PlainHttpServers.TryAdd(ServerKey(uri), 0);
        }

        private static bool IsKnownPlainHttp(Uri uri) =>
            uri.Scheme == Uri.UriSchemeHttps && PlainHttpServers.ContainsKey(ServerKey(uri));

        private static string ServerKey(Uri uri) => $"{uri.Host}:{uri.Port}";

        private static Uri ToPlainHttp(Uri uri) =>
            new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp, Port = uri.Port }.Uri;

        public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
        {
            using var response = await GetAsync(url, ct).ConfigureAwait(false);
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        public static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
        {
            using var response = await GetAsync(url, ct).ConfigureAwait(false);
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        private static bool IsRedirect(HttpStatusCode code) =>
            code is HttpStatusCode.MovedPermanently      // 301
                 or HttpStatusCode.Found                 // 302
                 or HttpStatusCode.SeeOther              // 303
                 or HttpStatusCode.TemporaryRedirect     // 307
                 or HttpStatusCode.PermanentRedirect;    // 308
    }
}
