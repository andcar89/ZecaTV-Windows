using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using ZecaTV.Localization;

namespace ZecaTV.UI
{
    /// <summary>Turns load exceptions into short messages worth showing a user.</summary>
    internal static class ErrorText
    {
        public static string Describe(Exception ex) => ex switch
        {
            UriFormatException => Strings.ErrorInvalidUrl,
            TaskCanceledException or TimeoutException => Strings.ErrorServerTimeout,
            HttpRequestException { InnerException: SocketException s } => string.Format(Strings.ErrorCouldntReachServerFormat, s.Message),
            HttpRequestException h when h.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                => string.Format(Strings.ErrorAccessRefusedFormat, h.Message),
            FileNotFoundException or DirectoryNotFoundException => ex.Message,
            _ => ex.Message
        };
    }
}
