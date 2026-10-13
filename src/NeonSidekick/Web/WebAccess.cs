using NeonSidekick.Settings;

namespace NeonSidekick.Web;

/// <summary>
/// Everything the two web tools share, built once per app (the screen and headless hand the same
/// one to <c>ChatScreen.WebTools</c>): the client, the fetcher over it and the headless browser,
/// the page cache, and the built-in DuckDuckGo engine with its pacing. A SearXNG engine is made per
/// call from the setting, so a URL typed in <c>/settings</c> applies at the next search. Tests build
/// one over a stub client and a fake browser.
/// </summary>
public sealed class WebAccess
{
    /// <param name="openUrl">What <c>open_url</c> hands a link to: the user's default browser (<see cref="Llm.PersonaFile.OpenInBrowser"/>) in the app, a recorder in tests.</param>
    /// <param name="cookies">The jar <paramref name="http"/> sends from, which the fetcher fills from the headless browser (2026-10-11); null keeps the browser's cookies out.</param>
    /// <param name="hostInterval">The fetcher's gap between two requests to one host (<see cref="WebFetcher.HostInterval"/>, 2026-10-11); none by default, the app's in <see cref="Create"/>.</param>
    public WebAccess(HttpClient http, IHeadlessBrowser browser, TimeProvider time, Func<string, CancellationToken, Task<System.Net.IPAddress[]>>? resolve = null, Action<string>? openUrl = null, System.Net.CookieContainer? cookies = null, TimeSpan hostInterval = default)
    {
        OpenUrl = openUrl ?? Llm.PersonaFile.OpenInBrowser;
        Http = http ?? throw new ArgumentNullException(nameof(http));
        Browser = browser ?? throw new ArgumentNullException(nameof(browser));
        Time = time ?? throw new ArgumentNullException(nameof(time));
        Fetcher = new WebFetcher(http, browser, resolve, cookies, time) { HostInterval = hostInterval };
        Pages = new PageCache(time);
        DuckDuckGo = new DuckDuckGoSearch(Fetcher, time);
    }

    /// <summary>The app's own: the client over <see cref="LanPolicy"/> and the real browser, sharing one cookie jar, a host's requests a second apart.</summary>
    public static WebAccess Create(Func<NetworkReach> reach, TimeProvider time)
    {
        var cookies = new System.Net.CookieContainer();
        return new(WebHttp.Create(new LanPolicy(reach), cookies), new HeadlessBrowser(), time, cookies: cookies, hostInterval: WebFetcher.DefaultHostInterval);
    }

    public HttpClient Http { get; }
    public IHeadlessBrowser Browser { get; }
    public TimeProvider Time { get; }
    public WebFetcher Fetcher { get; }
    public PageCache Pages { get; }

    /// <summary>The hits <c>web_search</c> listed and the results page each was on, for <c>web_fetch</c>'s <c>Referer</c> (2026-10-11).</summary>
    public ResultReferers Referers { get; } = new();
    public DuckDuckGoSearch DuckDuckGo { get; }

    /// <summary>Opens a URL on the user's screen; throws when the shell refuses.</summary>
    public Action<string> OpenUrl { get; }

    /// <summary>
    /// The engine the settings name: SearXNG when <see cref="AppSettingsData.WebSearchMethod"/> is
    /// <c>searxng</c> AND <see cref="AppSettingsData.WebSearxngUrl"/> is an http(s) URL, else DuckDuckGo
    /// (a <c>searxng</c> pick with no usable URL falls back quietly — the 🛠️ header names the engine
    /// that ran). Under <c>duckduckgo</c> the URL is ignored, so it keeps its value between switches.
    /// </summary>
    public ISearchEngine Engine(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return SearxngBase(effective) is { } url ? new SearxngSearch(Http, url) : DuckDuckGo;
    }

    /// <summary>The SearXNG instance <see cref="Engine"/> would ask under <paramref name="effective"/>; null when that is DuckDuckGo.</summary>
    public static Uri? SearxngBase(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return SearchMethod.Resolve(effective) == SearchProvider.Searxng
            && Uri.TryCreate(effective.WebSearxngUrl?.Trim(), UriKind.Absolute, out var url) && WebFetcher.IsHttp(url)
            ? url
            : null;
    }

    /// <summary>How a tool's fetch runs under <paramref name="effective"/>: the mode, the browser path and the network mode.</summary>
    public static FetchOptions Options(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return new FetchOptions(BrowserMode.Resolve(effective), effective.WebBrowserPath ?? "", NetworkMode.Resolve(effective));
    }
}
