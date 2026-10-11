using System.Net;

namespace NeonSidekick.Web;

/// <summary>
/// The one <see cref="HttpClient"/> the web tools share: gzip / deflate / brotli decompression, a
/// cookie jar (a consent cookie set on one page holds for the next), HTTP/2 with a fallback to
/// 1.1, connections recycled every five minutes, and <see cref="LanPolicy"/> at the socket.
/// Redirects are the fetcher's to follow (each hop judged and re-headed), so the handler follows
/// none. No timeout on the client: every call carries its own ceiling.
/// </summary>
public static class WebHttp
{
    /// <summary>How long a pooled connection lives before it is replaced.</summary>
    public static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(5);

    /// <param name="cookies">The jar to send from (2026-10-11: <see cref="WebAccess"/> hands the same one to its fetcher, which fills it from the headless browser); a new one when null.</param>
    public static HttpClient Create(LanPolicy policy, CookieContainer? cookies = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = cookies ?? new CookieContainer(),
            PooledConnectionLifetime = ConnectionLifetime,
            ConnectCallback = policy.ConnectAsync,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }
}
