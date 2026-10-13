using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.Viewer;

/// <summary>Where the address box's text goes (<see cref="BrowserAddress.Resolve"/>): an address, a refusal with its reason, or nowhere (blank).</summary>
public readonly record struct BrowserGo(Uri? Url, string? Refusal)
{
    public static BrowserGo Nowhere { get; } = new(null, null);
}

/// <summary>
/// The Browser window's address box (2026-10-13, the user's ask): what its text means. An address goes there — an http(s) URL, a bare
/// <c>example.com/page</c> as https (<see cref="WebFetcher.ParseUrl"/>, the model's own rule), <c>localhost[:port]</c> as http, the
/// blank page — a refused scheme (<c>file:</c>, <c>javascript:</c>, a Windows path, a registered protocol) is refused, and anything
/// else is words, searched with the app's own <c>Web search method</c> (<see cref="SearchUrl"/>): DuckDuckGo's site, or the SearXNG
/// instance's results page when SearXNG is picked and its URL is set. The navigation filter (<see cref="BrowserPage.Allows"/>) still
/// stands behind whatever this lets through. Pure; pinned.
/// </summary>
public static class BrowserAddress
{
    /// <summary>DuckDuckGo's own results page, the one a person reads (not the scrape's html endpoint).</summary>
    public const string DuckDuckGoPage = "https://duckduckgo.com/?q=";

    // Schemes that reach the machine or the browser's insides rather than the web: refused rather than searched.
    private static readonly string[] RefusedSchemes = ["file", "javascript", "vbscript", "data", "blob", "filesystem", "about", "chrome", "edge", "view-source", "devtools", "search-ms", "shell"];

    public static BrowserGo Resolve(string? text, Func<string, Uri> search)
    {
        ArgumentNullException.ThrowIfNull(search);
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return BrowserGo.Nowhere;
        }

        if (string.Equals(trimmed, BrowserPage.Blank, StringComparison.OrdinalIgnoreCase))
        {
            return new BrowserGo(new Uri(BrowserPage.Blank), null);
        }

        if (trimmed.Any(char.IsWhiteSpace))
        {
            return new BrowserGo(search(trimmed), null);
        }

        if (Localhost(trimmed) is { } local)
        {
            return new BrowserGo(local, null);
        }

        if (WebFetcher.ParseUrl(trimmed) is { } url)
        {
            return new BrowserGo(url, null);
        }

        return Refused(trimmed) ? new BrowserGo(null, BrowserText.Refused(trimmed)) : new BrowserGo(search(trimmed), null);
    }

    /// <summary>
    /// The results page for <paramref name="words"/> under <paramref name="effective"/>'s <c>Web search method</c>: the SearXNG
    /// instance's (<see cref="SearxngSearch.PageUrl"/>) when <see cref="WebAccess.SearxngBase"/> names one, else DuckDuckGo's.
    /// </summary>
    public static Uri SearchUrl(AppSettingsData effective, string words)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(words);
        return WebAccess.SearxngBase(effective) is { } instance ? SearxngSearch.PageUrl(instance, words) : DuckDuckGo(words);
    }

    /// <summary>DuckDuckGo's results page for <paramref name="words"/>. Pinned.</summary>
    public static Uri DuckDuckGo(string words)
    {
        ArgumentNullException.ThrowIfNull(words);
        return new Uri(DuckDuckGoPage + Uri.EscapeDataString(words.Trim()));
    }

    /// <summary>
    /// The page the setting <c>Web browser home page</c> names (2026-10-13): a web address as the box reads one, else null — the blank
    /// page, for an empty setting and for a hand-edited value that is no address (never searched: a home page is a place).
    /// </summary>
    public static Uri? Home(string? setting)
    {
        string trimmed = (setting ?? "").Trim();
        return trimmed.Length == 0 || trimmed.Any(char.IsWhiteSpace) ? null : Localhost(trimmed) ?? WebFetcher.ParseUrl(trimmed);
    }

    /// <summary>What the box shows for the page's address: nothing on the blank page (or before there is one), else the address.</summary>
    public static string Shown(string? source) =>
        source is null || string.Equals(source, BrowserPage.Blank, StringComparison.OrdinalIgnoreCase) ? "" : source;

    // localhost, localhost:3000, localhost/x, localhost:3000/x → http (a dev server is rarely https; ParseUrl wants a dot).
    private static Uri? Localhost(string text)
    {
        const string Name = "localhost";
        if (!text.StartsWith(Name, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = text[Name.Length..];
        if (rest.Length > 0 && rest[0] is not (':' or '/' or '?' or '#'))
        {
            return null;
        }

        return Uri.TryCreate("http://" + text, UriKind.Absolute, out var url) && url.IsLoopback ? url : null;
    }

    // An explicit scheme that is not the web's, a Windows path or a share: never navigated to, never searched.
    private static bool Refused(string text)
    {
        if (text.Contains("://", StringComparison.Ordinal) || text.StartsWith('\\') || (text.Length > 2 && text[1] == ':' && text[2] is '\\' or '/'))
        {
            return true;
        }

        int colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        string scheme = text[..colon].ToLowerInvariant();
        return RefusedSchemes.Contains(scheme) || scheme.StartsWith("ms-", StringComparison.Ordinal);
    }
}
