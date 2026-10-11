namespace NeonSidekick.Web;

/// <summary>
/// The request headers a desktop Chrome sends for a top-level navigation, as one set so every web
/// request looks the same; the browser they claim (version, brand, platform) is a <see cref="BrowserIdentity"/>,
/// since 2026-10-11 the installed browser's version rather than a pin. This is the reachable end of
/// "look like a browser": headers are ours to choose, the TLS and HTTP/2 fingerprints are the runtime's
/// (SChannel on Windows) and a fingerprint-reading gate tells them from Chrome's — that is what the
/// headless-browser leg of <see cref="WebFetcher"/> is for. Chrome's <c>zstd</c> is left out of
/// <c>Accept-Encoding</c>: .NET has no zstd decoder, and a body it cannot read is worse than a missing token.
/// <para>
/// Since later on 2026-10-11 (the user's ask: get the HTTP client past more bot checks) the headers go in the order Chrome sends
/// them (<see cref="Order"/>), since a gate may read the order as well as the values. <c>Accept-Encoding</c> is now ours, put in
/// its place, rather than the handler's appended at the end; the handler (<see cref="WebHttp"/>) finds its three tokens there,
/// adds none, and still decodes the body. The cookie header is the handler's and goes after the rest, where Chrome puts it
/// before <c>Priority</c> — the one place the order still differs.
/// </para>
/// </summary>
public static class BrowserHeaders
{
    public const string Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7";
    public const string AcceptLanguage = "en-US,en;q=0.9";
    public const string SecChUaMobile = "?0";

    /// <summary>The encodings asked for: Chrome's, less <c>zstd</c>. Every one is a <see cref="System.Net.DecompressionMethods"/> the handler decodes.</summary>
    public const string AcceptEncoding = "gzip, deflate, br";

    /// <summary>Chrome's priority for a top-level document (RFC 9218), sent since 2026-10-11.</summary>
    public const string Priority = "u=0, i";

    /// <summary>The headers in the order Chrome sends a navigation (Chrome 13x to 15x), <c>Referer</c> only with one. Pinned.</summary>
    public static readonly string[] Order =
    [
        "Sec-CH-UA", "Sec-CH-UA-Mobile", "Sec-CH-UA-Platform", "Upgrade-Insecure-Requests", "User-Agent", "Accept",
        "Sec-Fetch-Site", "Sec-Fetch-Mode", "Sec-Fetch-User", "Sec-Fetch-Dest", "Referer", "Accept-Encoding", "Accept-Language", "Priority",
    ];

    /// <summary>
    /// Applies the set to <paramref name="request"/> as a top-level navigation by <paramref name="identity"/>, in <see cref="Order"/>:
    /// <c>Sec-Fetch-Site</c> is <c>none</c> for a typed address, <c>same-origin</c> when <paramref name="referer"/> is on the
    /// same host, else <c>cross-site</c>; <paramref name="accept"/> in place of <see cref="Accept"/> when given. Header values are
    /// added without validation, as sent.
    /// </summary>
    public static void Apply(HttpRequestMessage request, BrowserIdentity identity, Uri? referer = null, string? accept = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        string site = referer is null ? "none"
            : request.RequestUri is { } target && string.Equals(target.Host, referer.Host, StringComparison.OrdinalIgnoreCase) ? "same-origin"
            : "cross-site";
        var headers = request.Headers;
        headers.TryAddWithoutValidation("Sec-CH-UA", identity.SecChUa);
        headers.TryAddWithoutValidation("Sec-CH-UA-Mobile", SecChUaMobile);
        headers.TryAddWithoutValidation("Sec-CH-UA-Platform", identity.PlatformHint);
        headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        headers.TryAddWithoutValidation("User-Agent", identity.UserAgent);
        headers.TryAddWithoutValidation("Accept", accept ?? Accept);
        headers.TryAddWithoutValidation("Sec-Fetch-Site", site);
        headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        if (referer is not null)
        {
            headers.TryAddWithoutValidation("Referer", referer.AbsoluteUri);
        }

        headers.TryAddWithoutValidation("Accept-Encoding", AcceptEncoding);
        headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
        headers.TryAddWithoutValidation("Priority", Priority);
    }
}
