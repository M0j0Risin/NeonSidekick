using System.Net;
using System.Text.Json;

namespace NeonSidekick.Web;

/// <summary>
/// A cookie the headless browser holds, as DevTools' <c>Storage.getCookies</c> gives it (2026-10-11, the user's ask: get the HTTP
/// client past more bot checks): <see cref="Domain"/> with a leading dot for a domain cookie and bare for a host-only one,
/// <see cref="Expires"/> in seconds since 1970 and zero or less for a session cookie. A page the browser got past a bot check
/// leaves its clearance here (<c>cf_clearance</c>, <c>__cf_bm</c>, <c>_abck</c>, <c>datadome</c>…); <see cref="WebFetcher"/>
/// hands those to the HTTP client's jar (<see cref="AddTo"/>), so the next fetch from that site goes through without the browser.
/// </summary>
public sealed record BrowserCookie(string Name, string Value, string Domain, string Path, double Expires, bool Secure, bool HttpOnly)
{
    /// <summary>A cookie set without a <c>Domain</c> attribute: sent to its own host alone.</summary>
    public bool HostOnly => !Domain.StartsWith('.');

    /// <summary>RFC 6265's domain match: a host-only cookie for its host alone, a domain cookie for its domain and every host under it. Pure; pinned.</summary>
    public bool AppliesTo(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        string domain = Domain.TrimStart('.');
        if (domain.Length == 0)
        {
            return false;
        }

        if (string.Equals(host, domain, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !HostOnly && host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The cookies of a <c>Storage.getCookies</c> answer; one missing its name or domain is skipped. Pure; pinned.</summary>
    public static IReadOnlyList<BrowserCookie> Parse(JsonElement result)
    {
        var list = new List<BrowserCookie>();
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var cookie in cookies.EnumerateArray())
        {
            string name = Text(cookie, "name");
            string domain = Text(cookie, "domain");
            if (name.Length == 0 || domain.Length == 0)
            {
                continue;
            }

            bool session = cookie.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.True;
            double expires = !session && cookie.TryGetProperty("expires", out var e) && e.TryGetDouble(out double seconds) ? seconds : -1;
            list.Add(new BrowserCookie(name, Text(cookie, "value"), domain, Text(cookie, "path"), expires, Flag(cookie, "secure"), Flag(cookie, "httpOnly")));
        }

        return list;

        static string Text(JsonElement cookie, string key) => cookie.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static bool Flag(JsonElement cookie, string key) => cookie.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// The cookie put into <paramref name="jar"/> as its own site set it: host-only stays host-only (no <c>Domain</c> given to .NET),
    /// a domain cookie keeps its domain, and the expiry, path and flags carry over. False when .NET refuses it (a value with a comma
    /// or a semicolon, a name it will not take, an expiry out of range), which is skipped.
    /// </summary>
    public bool AddTo(CookieContainer jar)
    {
        ArgumentNullException.ThrowIfNull(jar);
        try
        {
            string path = Path.StartsWith('/') ? Path : "/";
            var cookie = new Cookie(Name, Value, path) { Secure = Secure, HttpOnly = HttpOnly };
            if (!HostOnly)
            {
                cookie.Domain = Domain;
            }

            if (Expires > 0)
            {
                cookie.Expires = DateTime.UnixEpoch.AddSeconds(Expires);
            }

            jar.Add(new UriBuilder(Secure ? Uri.UriSchemeHttps : Uri.UriSchemeHttp, Domain.TrimStart('.')) { Path = path }.Uri, cookie);
            return true;
        }
        catch (Exception ex) when (ex is CookieException or ArgumentException or UriFormatException)
        {
            return false;
        }
    }
}
