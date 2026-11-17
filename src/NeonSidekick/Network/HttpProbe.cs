using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using NeonSidekick.Web;

namespace NeonSidekick.Network;

/// <summary>One request of an HTTP check: what was asked, and what came back or why nothing did.</summary>
/// <param name="Status">The status code; null when no answer came (refused, failed, timed out).</param>
/// <param name="Reason">The server's reason phrase, as sent (a stranger's text).</param>
/// <param name="Version">The HTTP version the server spoke (<c>HTTP/1.1</c>, <c>HTTP/2</c>).</param>
/// <param name="Location">A redirect's <c>Location</c>, as sent.</param>
/// <param name="Problem">Why the hop has no answer: the refusal, the failure, the certificate, the time.</param>
public sealed record HttpHop(string Method, Uri Url, int? Status, string? Reason, string? Version, TimeSpan Elapsed, string? Location = null, string? Problem = null);

/// <summary>An HTTP check's whole run: each hop, the last answer's telling headers, and the model's note from the first host's judgement.</summary>
public sealed record HttpReport(Uri Asked, IReadOnlyList<HttpHop> Hops, IReadOnlyList<(string Name, string Value)> Headers, TimeSpan Total, bool Followed, bool CutAtMaxHops);

/// <summary>
/// <c>net_http</c>'s request (2026-11-16, plan sharded-hugging-puppy #15, "is this site up?"): one hop at a time, each host judged and counted as
/// the other network tools' are (<see cref="NetworkAccess.TargetAsync"/>), each connection made only to that hop's judged addresses (the
/// handler's <c>ConnectCallback</c>, never the resolver again), no proxy (the other <c>net_*</c> tools connect directly, and a proxy would hide the
/// judged address), no cookies, no redirect followed by the handler, no decompression, and the body never read: the headers, then the
/// response is let go. Certificates are checked; a failure is an answer ("answered over TLS, but…"), never a refusal. HTTP/2 is asked for over
/// TLS, whatever the server speaks is named; HTTP/3 is left out (it needs msquic, a native library).
/// </summary>
public static class HttpProbe
{
    /// <summary>The most requests one check makes, the first among them.</summary>
    public const int MaxHops = 10;

    /// <summary>Each request's time, connect to headers (as <see cref="TlsProbe"/>'s handshake).</summary>
    public static readonly TimeSpan HopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The whole check's time, every hop's together.</summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The headers an answer shows when the last response has them, in this order.</summary>
    public static readonly IReadOnlyList<string> TellingHeaders = ["server", "strict-transport-security", "cache-control", "age", "content-type", "content-length", "retry-after", "www-authenticate"];

    /// <summary>The handler for one hop: its connections go only to <paramref name="target"/>'s judged addresses.</summary>
    public static HttpMessageHandler Handler(NetworkTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var addresses = target.Addresses;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = HopTimeout,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = await HappyEyeballs.ConnectAsync(addresses, context.DnsEndPoint.Port, HappyEyeballs.AttemptDelay, HappyEyeballs.ConnectOneAsync, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
    }

    /// <summary>
    /// The URL a check asks for: a bare host gets <c>https://</c> (as <see cref="WebFetcher.ParseUrl"/> does); only http and https; no user info
    /// (no credentials are ever sent); the host as written must be the host parsed — <see cref="Uri"/> reads <c>0x7f.1</c> as <c>127.0.0.1</c>,
    /// which <see cref="NetworkAccess.OneHostProblem"/> refuses in a bare host, so the model's text is never judged as an address it didn't
    /// write. The refusal's sentence, or the URL.
    /// </summary>
    public static (Uri? Url, string? Error) ParseUrl(string text, Uri? relativeTo = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        string written = text.Trim();
        if (written.Length == 0)
        {
            return (null, NetworkText.Missing("url"));
        }

        if (relativeTo is null && !written.Contains("://", StringComparison.Ordinal))
        {
            written = "https://" + written;
        }
        else if (relativeTo is not null && written.StartsWith("//", StringComparison.Ordinal))
        {
            // A scheme-relative Location names a host: checked as one written in full.
            written = relativeTo.Scheme + ":" + written;
        }

        Uri? url;
        if (relativeTo is not null && !written.Contains("://", StringComparison.Ordinal))
        {
            // A relative Location: the same host, so nothing new is written as one.
            if (!Uri.TryCreate(relativeTo, written, out url))
            {
                return (null, NetworkText.BadUrl(text));
            }
        }
        else if (!Uri.TryCreate(written, UriKind.Absolute, out url))
        {
            return (null, NetworkText.BadUrl(text));
        }
        else if (WrittenHost(written) is not { } host || NetworkAccess.OneHostProblem(host) is not null)
        {
            return (null, NetworkText.BadUrl(text));
        }

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            return (null, NetworkText.NotHttp(text));
        }

        if (url.UserInfo.Length > 0)
        {
            return (null, NetworkText.UrlUserInfo);
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

    /// <summary>The headers <see cref="TellingHeaders"/> names from a response (its content's too), each value as sent; <c>www-authenticate</c>'s scheme alone.</summary>
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

            IEnumerable<string>? values = null;
            if (!response.Headers.TryGetValues(name, out values))
            {
                response.Content.Headers.TryGetValues(name, out values);
            }

            if (values is null)
            {
                continue;
            }

            string value = string.Join(", ", values);
            if (name == "www-authenticate")
            {
                value = string.Join(", ", values.Select(v => v.Trim().Split(' ', 2)[0]).Distinct(StringComparer.OrdinalIgnoreCase));
            }

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
    /// Why a request got no answer, in the hop line's words: the certificate (an answer: the server is there), the connection, the time.
    /// </summary>
    public static string Problem(Exception failure, bool timedOut)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (timedOut)
        {
            return NetworkText.HttpTimedOut(HopTimeout);
        }

        if (failure is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } secure)
        {
            var auth = Inner<AuthenticationException>(secure);
            return auth is not null ? NetworkText.HttpUntrusted(auth.Message) : NetworkText.HttpTlsFailed(Innermost(secure).Message);
        }

        return NetworkText.HttpFailed(Innermost(failure).Message);
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

    private static Exception Innermost(Exception failure)
    {
        var e = failure;
        while (e.InnerException is not null)
        {
            e = e.InnerException;
        }

        return e;
    }

    /// <summary>The request a hop sends: the method, HTTP/2 or lower, and either the app's honest user agent or a browser's headers.</summary>
    public static HttpRequestMessage Request(HttpMethod method, Uri url, BrowserIdentity? browser, Uri? referer)
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
            BrowserHeaders.Apply(request, browser, referer);
        }
        else
        {
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(App.SidekickApp.Name, App.SidekickApp.Version));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        }

        return request;
    }
}
