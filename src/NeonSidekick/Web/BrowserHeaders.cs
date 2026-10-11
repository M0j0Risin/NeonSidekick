namespace NeonSidekick.Web;

/// <summary>
/// The request headers a desktop Chrome sends for a top-level navigation, as one set so every web
/// request looks the same; the browser they claim (version, brand, platform) is a <see cref="BrowserIdentity"/>,
/// since 2026-10-11 the installed browser's version rather than a pin. This is the reachable end of
/// "look like a browser": headers are ours to choose, the TLS and HTTP/2 fingerprints are the runtime's
/// (SChannel on Windows) and a fingerprint-reading gate tells them from Chrome's — that is what the
/// headless-browser leg of <see cref="WebFetcher"/> is for. Chrome's <c>zstd</c> is left out of
/// <c>Accept-Encoding</c> (the handler's own list): .NET has no zstd decoder, and a body it cannot read is
/// worse than a missing token.
/// </summary>
public static class BrowserHeaders
{
    public const string Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7";
    public const string AcceptLanguage = "en-US,en;q=0.9";
    public const string SecChUaMobile = "?0";

    /// <summary>Chrome's priority for a top-level document (RFC 9218), sent since 2026-10-11.</summary>
    public const string Priority = "u=0, i";

    /// <summary>
    /// Applies the set to <paramref name="request"/> as a top-level navigation by <paramref name="identity"/>: <c>Sec-Fetch-Site</c>
    /// is <c>none</c> for a typed address, <c>same-origin</c> when <paramref name="referer"/> is on the
    /// same host, else <c>cross-site</c>. Header values are added without validation, as sent.
    /// </summary>
    public static void Apply(HttpRequestMessage request, BrowserIdentity identity, Uri? referer = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        var headers = request.Headers;
        headers.TryAddWithoutValidation("User-Agent", identity.UserAgent);
        headers.TryAddWithoutValidation("Accept", Accept);
        headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
        headers.TryAddWithoutValidation("Sec-CH-UA", identity.SecChUa);
        headers.TryAddWithoutValidation("Sec-CH-UA-Mobile", SecChUaMobile);
        headers.TryAddWithoutValidation("Sec-CH-UA-Platform", identity.PlatformHint);
        headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        headers.TryAddWithoutValidation("Priority", Priority);
        if (referer is null)
        {
            headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
            return;
        }

        headers.TryAddWithoutValidation("Referer", referer.AbsoluteUri);
        bool sameOrigin = request.RequestUri is { } target && string.Equals(target.Host, referer.Host, StringComparison.OrdinalIgnoreCase);
        headers.TryAddWithoutValidation("Sec-Fetch-Site", sameOrigin ? "same-origin" : "cross-site");
    }
}
