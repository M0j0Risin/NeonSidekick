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

    /// <summary>
    /// Set on a request that keeps out of the jar, neither sending from it nor adding to it, though a <c>Cookie</c> header the request
    /// carries itself still goes. The metasearch's (<see cref="BuiltInMetaSearch"/>): the jar holds the headless browser's cookies, and
    /// what a search engine sets would otherwise link one search to the next and follow <c>web_fetch</c> to that site.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> NoCookiesKey = new("NeonSidekick.Web.NoCookies");

    /// <param name="cookies">The jar to send from (2026-10-11: <see cref="WebAccess"/> hands the same one to its fetcher, which fills it from the headless browser); a new one when null.</param>
    /// <remarks>
    /// One pool per judgement (2026-10-23, the admin review's U2): the reach is judged as a socket is opened, and a pooled socket was reused for 5
    /// minutes, so one opened by the app's exempt SearXNG request, or under the reach before a change in <c>/settings</c>, carried a later request
    /// past the check. Now an exempt request uses the <c>both</c> pool, and every other request goes to the pool of the reach in force as it is sent,
    /// each pool's sockets judged under that reach alone. Cookies are a separate choice, so a <see cref="NoCookiesKey"/> request has its own pools.
    /// </remarks>
    public static HttpClient Create(LanPolicy policy, CookieContainer? cookies = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new HttpClient(new PerReachHandler(policy, cookies ?? new CookieContainer()), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    /// <summary>A sockets handler whose connects are judged under <paramref name="reach"/> alone; with no <paramref name="cookies"/> it keeps none.</summary>
    private static SocketsHttpHandler Pool(NetworkReach reach, CookieContainer? cookies)
    {
        var pool = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            UseCookies = cookies is not null,
            PooledConnectionLifetime = ConnectionLifetime,
            ConnectCallback = new LanPolicy(() => reach).ConnectAsync,
        };
        if (cookies is not null)
        {
            pool.CookieContainer = cookies;
        }

        return pool;
    }

    /// <summary>The request's pool: <c>both</c>'s for <see cref="LanPolicy.ExemptKey"/>, else the reach's in force now (U2), apart for <see cref="NoCookiesKey"/>.</summary>
    private sealed class PerReachHandler(LanPolicy policy, CookieContainer cookies) : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(NetworkReach Reach, bool NoCookies), HttpMessageInvoker> _pools = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bool exempt = request.Options.TryGetValue(LanPolicy.ExemptKey, out bool value) && value;
            bool none = request.Options.TryGetValue(NoCookiesKey, out bool off) && off;
            var pool = _pools.GetOrAdd((exempt ? NetworkReach.Both : policy.Reach, none), key => new HttpMessageInvoker(Pool(key.Reach, key.NoCookies ? null : cookies), disposeHandler: true));
            return pool.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var pool in _pools.Values)
                {
                    pool.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
