// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace SearXNG.Net.Net;

/// <summary>A page an engine fetched.</summary>
internal sealed record EngineResponse(HttpStatusCode Status, string Text, Uri Url, string Server, Uri? Location);

/// <summary>
/// An engine's HTTP: requests go through the host's <see cref="HttpClient"/>, so the host's own network rules still apply.
/// Failed responses are classified as in SearXNG's <c>searx/network/raise_for_httperror.py</c>.
/// </summary>
internal static partial class EngineHttp
{
    /// <summary>
    /// The browser that Bing, Brave and Startpage are asked as. With this request (measured 2026-11-06), Bing answered <c>HttpClient</c> as it did an
    /// impersonating client, Brave blocked impersonating clients and answered <c>HttpClient</c>, and Startpage answered <c>HttpClient</c>.
    /// </summary>
    internal const string ChromeUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36";

    /// <summary>What that browser accepts for a page.</summary>
    internal const string PageAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    // SearXNG's defaults, from searx/settings.yml's search.suspended_times.
    internal static readonly TimeSpan CaptchaRest = TimeSpan.FromSeconds(3600);
    internal static readonly TimeSpan AccessDeniedRest = TimeSpan.FromSeconds(180);
    internal static readonly TimeSpan TooManyRequestsRest = TimeSpan.FromSeconds(180);
    internal static readonly TimeSpan CloudflareCaptchaRest = TimeSpan.FromSeconds(1296000);
    internal static readonly TimeSpan CloudflareDeniedRest = TimeSpan.FromSeconds(86400);
    internal static readonly TimeSpan RecaptchaRest = TimeSpan.FromSeconds(604800);

    /// <summary>Sends a GET and returns the page.</summary>
    /// <exception cref="EngineException">The response was an HTTP error.</exception>
    public static async Task<EngineResponse> GetAsync(
        HttpClient http,
        Uri url,
        IEnumerable<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken)
    {
        var response = await GetWithoutRaisingAsync(http, url, headers, cancellationToken).ConfigureAwait(false);
        RaiseForHttpError((int)response.Status, response.Server, response.Text, response.Url);
        return response;
    }

    /// <summary>Sends a GET and returns the page, for an engine that has its own reading of some HTTP errors.</summary>
    public static Task<EngineResponse> GetWithoutRaisingAsync(
        HttpClient http,
        Uri url,
        IEnumerable<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken) =>
        SendAsync(http, HttpMethod.Get, url, null, headers, cancellationToken);

    /// <summary>Posts a form and returns the page.</summary>
    /// <exception cref="EngineException">The response was an HTTP error.</exception>
    public static async Task<EngineResponse> PostFormAsync(
        HttpClient http,
        Uri url,
        IEnumerable<KeyValuePair<string, string>> form,
        IEnumerable<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        var response = await SendAsync(http, HttpMethod.Post, url, content, headers, cancellationToken).ConfigureAwait(false);
        RaiseForHttpError((int)response.Status, response.Server, response.Text, response.Url);
        return response;
    }

    private static async Task<EngineResponse> SendAsync(
        HttpClient http,
        HttpMethod method,
        Uri url,
        HttpContent? content,
        IEnumerable<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = content,

            // A request message doesn't inherit HttpClient's default version. SearXNG's outgoing.enable_http2 defaults on.
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var server = response.Headers.TryGetValues("Server", out var values) ? string.Join(' ', values) : "";
        return new EngineResponse(response.StatusCode, text, response.RequestMessage?.RequestUri ?? url, server, response.Headers.Location);
    }

    /// <summary>The <c>Accept-Language</c> SearXNG's online processor sends for <paramref name="locale"/>.</summary>
    public static string AcceptLanguage(string locale)
    {
        // Checked because the locale goes into a header unvalidated: a bad value gets the default, not a malformed header.
        if (locale == EngineQuery.AllLocales || !Locale().IsMatch(locale))
        {
            return "en-US,en;q=0.9";
        }

        var parts = locale.Split('-');
        var language = parts[0].ToLowerInvariant();

        // Babel's parse_locale: after the language and an optional four-letter script, two letters or three digits.
        int next = parts.Length > 1 && parts[1].Length == 4 && parts[1].All(char.IsAsciiLetter) ? 2 : 1;
        var candidate = parts.Length > next ? parts[next] : "";
        var territory = candidate.Length == 2 && candidate.All(char.IsAsciiLetter) ? candidate.ToUpperInvariant()
            : candidate.Length == 3 && candidate.All(char.IsAsciiDigit) ? candidate
            : language;
        return string.Create(CultureInfo.InvariantCulture, $"{language},{language}-{territory};q=0.7,en;q=0.3");
    }

    /// <summary>SearXNG's <c>raise_for_httperror</c>.</summary>
    internal static void RaiseForHttpError(int status, string server, string text, Uri url)
    {
        if (status < 400)
        {
            return;
        }

        if (server.StartsWith("cloudflare", StringComparison.Ordinal))
        {
            if (IsCloudflareChallenge(status, text))
            {
                throw new EngineException(EngineFailureKind.Captcha, "Cloudflare CAPTCHA", CloudflareCaptchaRest, url);
            }

            if (status == 403 && text.Contains("<span class=\"cf-error-code\">1020</span>", StringComparison.Ordinal))
            {
                throw new EngineException(EngineFailureKind.AccessDenied, "Cloudflare Firewall", CloudflareDeniedRest, url);
            }
        }

        if (status == 503 && text.Contains("\"https://www.google.com/recaptcha/", StringComparison.Ordinal))
        {
            throw new EngineException(EngineFailureKind.Captcha, "ReCAPTCHA", RecaptchaRest, url);
        }

        var code = status.ToString(CultureInfo.InvariantCulture);
        throw status switch
        {
            402 or 403 => new EngineException(EngineFailureKind.AccessDenied, "HTTP error " + code, AccessDeniedRest, url),
            429 => new EngineException(EngineFailureKind.TooManyRequests, "Too many requests", TooManyRequestsRest, url),
            _ => new EngineException(EngineFailureKind.HttpError, "HTTP error " + code),
        };
    }

    private static bool IsCloudflareChallenge(int status, string text)
    {
        if (status is 429 or 503
            && (text.Contains("__cf_chl_jschl_tk__=", StringComparison.Ordinal)
                || (text.Contains("/cdn-cgi/challenge-platform/", StringComparison.Ordinal)
                    && text.Contains("orchestrate/jsch/v1", StringComparison.Ordinal)
                    && text.Contains("window._cf_chl_enter(", StringComparison.Ordinal))))
        {
            return true;
        }

        return status == 403 && text.Contains("__cf_chl_captcha_tk__=", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\A[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*\z")]
    private static partial Regex Locale();
}
