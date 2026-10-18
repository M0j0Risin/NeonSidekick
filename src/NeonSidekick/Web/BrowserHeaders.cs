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
/// <para>
/// The same day (code review): a redirect keeps the page the navigation came from as its <c>Referer</c> and folds
/// <c>Sec-Fetch-Site</c> over every URL so far (<see cref="SiteOf"/>), as Chrome does, rather than naming the hop before it; the
/// <c>Referer</c> goes as Chrome's default policy lets it (<see cref="RefererFor"/>: none from https to http).
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

    /// <summary>Safari's <c>Accept</c> for a navigation (2026-10-18, captured from the Mac's Browser window).</summary>
    public const string SafariAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    /// <summary>
    /// The headers in the order Safari sends a navigation over HTTP/2 (2026-10-18, the Mac Browser plan's Phase 0: the window, with Safari's
    /// user agent, at a header-echo page, Safari 26.6), <c>Referer</c> only with one: no client hints, no <c>Upgrade-Insecure-Requests</c>,
    /// <c>Sec-Fetch-User</c> or <c>Priority</c>. Pinned.
    /// </summary>
    public static readonly string[] SafariOrder =
    [
        "Accept", "Sec-Fetch-Site", "Accept-Encoding", "Sec-Fetch-Mode", "User-Agent", "Accept-Language", "Sec-Fetch-Dest", "Referer",
    ];

    /// <summary>The headers in the order Chrome sends a navigation (Chrome 13x to 15x), <c>Referer</c> only with one. Pinned.</summary>
    public static readonly string[] Order =
    [
        "Sec-CH-UA", "Sec-CH-UA-Mobile", "Sec-CH-UA-Platform", "Upgrade-Insecure-Requests", "User-Agent", "Accept",
        "Sec-Fetch-Site", "Sec-Fetch-Mode", "Sec-Fetch-User", "Sec-Fetch-Dest", "Referer", "Accept-Encoding", "Accept-Language", "Priority",
    ];

    /// <summary>
    /// Applies the set to <paramref name="request"/> as a top-level navigation by <paramref name="identity"/>, in <see cref="Order"/>,
    /// coming from the page <paramref name="referer"/> (null for a typed address): <c>Sec-Fetch-Site</c> is <paramref name="site"/>
    /// when given (a redirect's, <see cref="SiteOf"/> over the chain), else worked out from <paramref name="referer"/> and the request's
    /// URL alone; the <c>Referer</c> is what Chrome's default policy lets through (<see cref="RefererFor"/>); <paramref name="accept"/>
    /// in place of <see cref="Accept"/> when given. Header values are added without validation, as sent. For Safari (2026-10-18) the set
    /// and order are <see cref="SafariOrder"/>'s, with <see cref="SafariAccept"/>.
    /// </summary>
    public static void Apply(HttpRequestMessage request, BrowserIdentity identity, Uri? referer = null, string? accept = null, string? site = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        site ??= request.RequestUri is { } target ? SiteOf(referer, [target]) : referer is null ? "none" : "cross-site";
        string? sent = referer is not null && request.RequestUri is { } to ? RefererFor(referer, to) : null;
        var headers = request.Headers;
        if (!identity.IsChromium)
        {
            // Safari (2026-10-18, the Mac's Browser window): its own set and order (SafariOrder), as the window sent it.
            headers.TryAddWithoutValidation("Accept", accept ?? SafariAccept);
            headers.TryAddWithoutValidation("Sec-Fetch-Site", site);
            headers.TryAddWithoutValidation("Accept-Encoding", AcceptEncoding);
            headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
            headers.TryAddWithoutValidation("User-Agent", identity.UserAgent);
            headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
            headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
            if (sent is not null)
            {
                headers.TryAddWithoutValidation("Referer", sent);
            }

            return;
        }

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
        if (sent is not null)
        {
            headers.TryAddWithoutValidation("Referer", sent);
        }

        headers.TryAddWithoutValidation("Accept-Encoding", AcceptEncoding);
        headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
        headers.TryAddWithoutValidation("Priority", Priority);
    }

    /// <summary>
    /// <c>Sec-Fetch-Site</c> for a navigation from <paramref name="initiator"/> (null: typed, <c>none</c> whatever it is redirected to)
    /// through <paramref name="chain"/>, every URL it has been sent to so far, as Fetch Metadata folds it (code review, 2026-10-11: a
    /// redirect used to start again from the hop before it, and only hosts were compared): <c>same-origin</c> while every URL has the
    /// initiator's scheme, host and port, <c>same-site</c> while they share its scheme and site (<see cref="SameSite"/>), else
    /// <c>cross-site</c>. Pure; pinned.
    /// </summary>
    public static string SiteOf(Uri? initiator, IEnumerable<Uri> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (initiator is null)
        {
            return "none";
        }

        string site = "same-origin";
        foreach (var url in chain)
        {
            if (SameOrigin(initiator, url))
            {
                continue;
            }

            if (!SameSite(initiator, url))
            {
                return "cross-site";
            }

            site = "same-site";
        }

        return site;
    }

    /// <summary>
    /// The <c>Referer</c> Chrome sends from <paramref name="referrer"/> to <paramref name="target"/> under its default policy,
    /// <c>strict-origin-when-cross-origin</c> (code review, 2026-10-11): the whole URL to the same origin, its origin alone to another,
    /// none from https to http; never a fragment or a user name. Pure; pinned.
    /// </summary>
    public static string? RefererFor(Uri referrer, Uri target)
    {
        ArgumentNullException.ThrowIfNull(referrer);
        ArgumentNullException.ThrowIfNull(target);
        if (referrer.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp)
        {
            return null;
        }

        return SameOrigin(referrer, target)
            ? referrer.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)
            : referrer.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + "/";
    }

    /// <summary>The same scheme, host and port.</summary>
    public static bool SameOrigin(Uri a, Uri b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Scheme == b.Scheme && a.Port == b.Port && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same scheme and the same site (<see cref="RegistrableDomain"/>): <c>example.com</c> and <c>www.example.com</c> over https.</summary>
    public static bool SameSite(Uri a, Uri b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Scheme == b.Scheme && string.Equals(RegistrableDomain(a.IdnHost), RegistrableDomain(b.IdnHost), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Second-level labels that sit under a country code as a suffix of their own (<c>co.uk</c>, <c>com.au</c>, <c>ac.jp</c>…).</summary>
    private static readonly HashSet<string> CountrySecondLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "ac", "co", "com", "edu", "gob", "gov", "go", "ltd", "mil", "ne", "net", "nic", "or", "org", "plc", "sch",
    };

    /// <summary>
    /// A host's site, its registrable domain, roughly: the last two labels, or three under a country code's own second level
    /// (<see cref="CountrySecondLevels"/>); an address, or a name of one or two labels, is its own. No public suffix list ships with
    /// the app, so a private suffix (<c>github.io</c>) counts as a site. Pure; pinned.
    /// </summary>
    public static string RegistrableDomain(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        string h = host.TrimEnd('.');
        if (System.Net.IPAddress.TryParse(h.Trim('[', ']'), out _))
        {
            return h;
        }

        string[] labels = h.Split('.');
        if (labels.Length <= 2)
        {
            return h;
        }

        int take = labels[^1].Length == 2 && CountrySecondLevels.Contains(labels[^2]) ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }
}
