using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using NeonSidekick.Web;

namespace NeonSidekick.Network;

/// <summary>One request of an HTTP check: what was asked, and what came back or why nothing did.</summary>
/// <param name="Status">The status code; null when no answer came (refused, failed, timed out).</param>
/// <param name="Reason">The server's reason phrase, as sent (a stranger's text).</param>
/// <param name="Version">The HTTP version the server spoke (<c>HTTP/1.1</c>, <c>HTTP/2</c>).</param>
/// <param name="Location">A redirect's <c>Location</c>, as sent.</param>
/// <param name="Problem">Why the hop has no answer: the refusal, the failure, the certificate, the time.</param>
/// <param name="Sent">
/// Whether a request went out (2026-11-17, the review's #212): false for a hop refused before any request (the reach, the lists, a blocked
/// port, the time running out before the host was judged), which the head's request count leaves out.
/// </param>
public sealed record HttpHop(string Method, Uri Url, int? Status, string? Reason, string? Version, TimeSpan Elapsed, string? Location = null, string? Problem = null, bool Sent = true);

/// <summary>An HTTP check's whole run: each hop, the last answer's telling headers, and the model's note from the first host's judgement.</summary>
public sealed record HttpReport(Uri Asked, IReadOnlyList<HttpHop> Hops, IReadOnlyList<(string Name, string Value)> Headers, TimeSpan Total, bool CutAtMaxHops);

/// <summary>
/// What one hop's certificate check found (2026-11-17, the review's #63): the validation callback records the errors, so a certificate
/// failure is told apart from another handshake failure. Made per hop and handed to the handler factory; the default verdict stands (the
/// callback accepts only <see cref="SslPolicyErrors.None"/>), so nothing is ever accepted that wasn't before.
/// </summary>
public sealed class TlsVerdict
{
    private int _errors = -1;

    /// <summary>The errors the callback saw; null when it never ran (no TLS, or the handshake failed before the certificate).</summary>
    public SslPolicyErrors? Errors
    {
        get
        {
            int seen = Volatile.Read(ref _errors);
            return seen < 0 ? null : (SslPolicyErrors)seen;
        }
        set => Volatile.Write(ref _errors, value is { } errors ? (int)errors : -1);
    }
}

/// <summary>
/// <c>net_http</c>'s request (2026-11-16, plan sharded-hugging-puppy #15, "is this site up?"): one hop at a time, each host judged and counted as
/// the other network tools' are (<see cref="NetworkAccess.TargetAsync"/>), each connection made only to that hop's judged addresses (the
/// handler's <c>ConnectCallback</c>, never the resolver again), no proxy (the other <c>net_*</c> tools connect directly, and a proxy would hide the
/// judged address), no cookies, no redirect followed by the handler, no decompression, and the body never read: the headers, then the
/// response is let go. Certificates are checked; a failure is an answer ("answered over TLS, but…"), never a refusal. HTTP/2 is asked for over
/// TLS, whatever the server speaks is named; HTTP/3 is left out (it needs msquic, a native library).
/// </summary>
public static partial class HttpProbe
{
    /// <summary>The most requests one check makes, the first among them.</summary>
    public const int MaxHops = 10;

    /// <summary>Each request's time, connect to headers (as <see cref="TlsProbe"/>'s handshake).</summary>
    public static readonly TimeSpan HopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The whole check's time, every hop's together.</summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The headers an answer shows when the last response has them, in this order.</summary>
    public static readonly IReadOnlyList<string> TellingHeaders = ["server", "strict-transport-security", "cache-control", "age", "content-type", "content-length", "retry-after", "www-authenticate"];

    /// <summary>
    /// The ports browsers refuse to fetch from (2026-11-17, the review's #121, the user's call): the Fetch standard's bad-port table,
    /// fetch.spec.whatwg.org §2.9 "Port blocking", copied whole (83 ports). A redirect to one of them is refused, as a browser would; a port the
    /// user types into <c>/net http</c> is the user's own and is kept.
    /// </summary>
    public static readonly FrozenSet<int> BadPorts = new[]
    {
        0, 1, 7, 9, 11, 13, 15, 17, 19, 20, 21, 22, 23, 25, 37, 42, 43, 53, 69, 77, 79, 87, 95, 101, 102, 103, 104, 109, 110, 111, 113, 115, 117, 119,
        123, 135, 137, 139, 143, 161, 179, 389, 427, 465, 512, 513, 514, 515, 526, 530, 531, 532, 540, 548, 554, 556, 563, 587, 601, 636, 989, 990,
        993, 995, 1719, 1720, 1723, 2049, 3659, 4045, 4190, 5060, 5061, 6000, 6566, 6665, 6666, 6667, 6668, 6669, 6679, 6697, 10080,
    }.ToFrozenSet();

    /// <summary>
    /// The handler for one hop: its connections go only to <paramref name="target"/>'s judged addresses, and its certificate check writes what it
    /// found into <paramref name="verdict"/>.
    /// </summary>
    public static HttpMessageHandler Handler(NetworkTarget target, TlsVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(verdict);
        var addresses = target.Addresses;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = HopTimeout,
            PooledConnectionLifetime = TimeSpan.Zero,
            SslOptions = new SslClientAuthenticationOptions
            {
                // The default verdict, recorded (#63): only a certificate without errors passes.
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                {
                    verdict.Errors = errors;
                    return errors == SslPolicyErrors.None;
                },
            },
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = await HappyEyeballs.ConnectAsync(addresses, context.DnsEndPoint.Port, HappyEyeballs.AttemptDelay, HappyEyeballs.ConnectOneAsync, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
    }

    /// <summary>A scheme at the start (RFC 3986 §3.1): a letter, then letters, digits, <c>+</c>, <c>-</c> or <c>.</c>, then the colon.</summary>
    [GeneratedRegex("^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SchemeStart();

    /// <summary>A typed <c>host:port</c> start: the colon followed by digits and then the end, <c>/</c>, <c>?</c> or <c>#</c>.</summary>
    [GeneratedRegex(@"^[^:/?#]+:\d+(?:[/?#]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex HostAndPort();

    /// <summary>
    /// The URL a check asks for: a bare host gets <c>https://</c> (as <see cref="WebFetcher.ParseUrl"/> does, through its same first step,
    /// <see cref="WebFetcher.UrlText"/>: <c>&lt;https://x&gt;</c> reads as <c>https://x</c>, 2026-11-17, #166); only http and https; no user
    /// info (no credentials are ever sent); the host as written must be the host parsed — <see cref="Uri"/> reads <c>0x7f.1</c> as
    /// <c>127.0.0.1</c>, which <see cref="NetworkAccess.OneHostProblem"/> refuses in a bare host, so the model's text is never judged as an
    /// address it didn't write. The refusal's sentence, or the URL.
    /// <para>
    /// A scheme is found by its pattern first (2026-11-17, #3, #206), before <c>https://</c> is ever added: so a Location
    /// (<paramref name="relativeTo"/> set) of <c>/login?next=https://x</c> is relative, and <c>mailto:x</c> is a scheme, refused as not HTTP.
    /// For the caller's own text a colon followed by digits is a port, not a scheme: <c>example.com:8080</c> and <c>localhost:8080/x</c> are
    /// hosts with ports (RFC 3986 §4.2 keeps a colon out of a relative reference's first segment, so a Location is read by the pattern alone).
    /// A Location's refusal names it fenced (#19), a stranger's text.
    /// </para>
    /// </summary>
    public static (Uri? Url, string? Error) ParseUrl(string text, Uri? relativeTo = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        string written = WebFetcher.UrlText(text);
        bool location = relativeTo is not null;
        if (written.Length == 0)
        {
            return (null, location ? NetworkText.BadRedirect(text) : NetworkText.Missing("url"));
        }

        bool scheme = SchemeStart().IsMatch(written) && (location || !HostAndPort().IsMatch(written));
        Uri? url;
        if (written.StartsWith("//", StringComparison.Ordinal))
        {
            // A scheme-relative address names a host: checked as one written in full.
            written = (relativeTo?.Scheme ?? Uri.UriSchemeHttps) + ":" + written;
            scheme = true;
        }
        else if (!scheme && !location)
        {
            written = "https://" + written;
            scheme = true;
        }

        if (!scheme)
        {
            // A relative Location: the same host, so nothing new is written as one.
            if (!Uri.TryCreate(relativeTo, written, out url))
            {
                return (null, NetworkText.BadRedirect(text));
            }
        }
        else
        {
            if (!Uri.TryCreate(written, UriKind.Absolute, out url))
            {
                return (null, location ? NetworkText.BadRedirect(text) : NetworkText.BadUrl(text));
            }

            if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            {
                return (null, location ? NetworkText.LocationNotHttp(text) : NetworkText.NotHttp(text));
            }

            if (!written.Contains("://", StringComparison.Ordinal) || WrittenHost(written) is not { } host || NetworkAccess.OneHostProblem(host) is not null)
            {
                return (null, location ? NetworkText.BadRedirect(text) : NetworkText.BadUrl(text));
            }
        }

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            return (null, location ? NetworkText.LocationNotHttp(text) : NetworkText.NotHttp(text));
        }

        if (url.UserInfo.Length > 0)
        {
            return (null, location ? NetworkText.RedirectUserInfo : NetworkText.UrlUserInfo);
        }

        return (url, null);
    }

    /// <summary>The host as the text writes it: between the scheme's <c>//</c> and the path, the user info and the port taken off; null with none.</summary>
    internal static string? WrittenHost(string absolute)
    {
        int start = absolute.IndexOf("://", StringComparison.Ordinal) + 3;
        int end = absolute.IndexOfAny(['/', '?', '#', '\\'], start);
        string authority = end < 0 ? absolute[start..] : absolute[start..end];
        int at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }

        if (authority.StartsWith('['))
        {
            int close = authority.IndexOf(']', StringComparison.Ordinal);
            return close > 0 ? authority[..(close + 1)] : null;
        }

        int colon = authority.IndexOf(':', StringComparison.Ordinal);
        string host = colon >= 0 ? authority[..colon] : authority;
        return host.Length > 0 ? host : null;
    }

    /// <summary>
    /// A header's field lines as the server sent them (2026-11-17, #36, #177): read through <c>NonValidated</c>, the response's own headers
    /// first, then its content's, so a value .NET's typed parsers would reformat or reject ("nginx/1.18.0 (Ubuntu)", a Location it can't
    /// parse) stays as written. Null when neither has it.
    /// </summary>
    public static IReadOnlyList<string>? Raw(HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(name);
        if (response.Headers.NonValidated.TryGetValues(name, out HeaderStringValues values)
            || response.Content.Headers.NonValidated.TryGetValues(name, out values))
        {
            var lines = new List<string>(values.Count);
            foreach (string line in values)
            {
                lines.Add(line);
            }

            return lines.Count > 0 ? lines : null;
        }

        return null;
    }

    /// <summary>
    /// The headers <see cref="TellingHeaders"/> names from a response (its content's too), each value as sent, field lines joined with ", ";
    /// <c>www-authenticate</c>'s schemes alone, cut per field line (a <c>realm="a, b"</c> holds a comma).
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> Telling(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var found = new List<(string, string)>();
        foreach (string name in TellingHeaders)
        {
            if (name == "retry-after" && response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
            {
                continue;
            }

            if (Raw(response, name) is not { } lines)
            {
                continue;
            }

            string value = name == "www-authenticate"
                ? string.Join(", ", lines.Select(v => v.Trim().Split(' ', 2)[0]).Distinct(StringComparer.OrdinalIgnoreCase))
                : string.Join(", ", lines);
            found.Add((name, value));
        }

        return found;
    }

    /// <summary>The version the server spoke, as a hop line writes it.</summary>
    public static string VersionWord(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Major >= 2 ? "HTTP/" + version.Major.ToString(CultureInfo.InvariantCulture) : "HTTP/" + version.ToString(2);
    }

    /// <summary>
    /// Why a request got no answer, in the hop line's words (2026-11-17, #63, #72, #16, #91): a certificate the callback found errors in (an
    /// answer: the server is there), named as <c>net_tls</c> names them; another handshake failure; else the connection's own words. A .NET
    /// message can quote the server's bytes, so each detail is fenced. <paramref name="command"/> is <c>/net</c>'s hand, which names
    /// <c>/net tls</c> rather than <c>net_tls</c>.
    /// </summary>
    public static string Problem(Exception failure, TlsVerdict verdict, bool command)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(verdict);
        if (verdict.Errors is { } errors && errors != SslPolicyErrors.None)
        {
            return NetworkText.HttpUntrusted(TlsProbe.PolicyWords(errors), command);
        }

        if (Inner<AuthenticationException>(failure) is not null)
        {
            return NetworkText.HttpTlsFailed(failure.GetBaseException().Message);
        }

        return NetworkText.HttpFailed(failure.GetBaseException().Message);
    }

    private static T? Inner<T>(Exception failure) where T : Exception
    {
        for (var e = failure; e is not null; e = e.InnerException)
        {
            if (e is T found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// The request a hop sends: the method, HTTP/2 or lower, and either the app's honest user agent or a browser's headers. A browser's are a
    /// navigation the user started (2026-11-17, #53, the Fetch standard's Fetch Metadata rule): no <c>Referer</c>, and <c>Sec-Fetch-Site:
    /// none</c> on every hop, never changed over its redirects. The body is asked for as is (<c>Accept-Encoding: identity</c>, #215), as
    /// <c>web_fetch</c> asks, so <c>content-length</c> reads alike in both modes under a handler that decompresses nothing.
    /// </summary>
    public static HttpRequestMessage Request(HttpMethod method, Uri url, BrowserIdentity? browser)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(url);
        var request = new HttpRequestMessage(method, url)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        if (browser is not null)
        {
            BrowserHeaders.Apply(request, browser, referer: null, site: "none");
            request.Headers.Remove("Accept-Encoding");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("User-Agent", App.SidekickApp.UserAgent());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        }

        return request;
    }
}
