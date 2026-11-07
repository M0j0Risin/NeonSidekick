using System.Globalization;
using System.Net;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using SearXNG.Net;
using SearXNG.Net.Engines;

namespace NeonSidekick.Web;

/// <summary>
/// The built-in metasearch (<c>metasearch</c> under <see cref="SearchMethod"/>): the SearXNG.Net library asks Bing, Brave, DuckDuckGo,
/// Startpage and Wikipedia at once in this process, then merges and ranks what they say, with no SearXNG server to run. One instance
/// lives on <see cref="WebAccess"/>, unlike <see cref="SearxngSearch"/>'s one per call, because the library remembers which engines are
/// resting after a refusal; <see cref="For"/> carries a search's settings over it. Every request goes through the app's client (<see cref="HostClientHandler"/>), so
/// <see cref="LanPolicy"/> judges every connection, but stays out of its cookie jar (<see cref="WebHttp.NoCookiesKey"/>).
/// A search reaches several outside services instead of one.
/// </summary>
public sealed class BuiltInMetaSearch
{
    /// <summary>The gap between two searches, as <see cref="DuckDuckGoSearch.MinInterval"/>: a model can search in bursts, and these sites rate-limit.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// The most one answer may hold: real pages were at most about 313 KB, and an uncapped client would buffer up to 2 GB of whatever a site or a
    /// decompression bomb sends. It's this low because a hostile page's parse can't be canceled and grows much faster than its size (47 s at 1 MB,
    /// against 5.4 s at this size). Past it the engine fails and the others still answer.
    /// </summary>
    internal const long MaxResponseBytes = 512 * 1024;

    /// <summary>The engines by name: the setting's words and the menu's rows, in the order they're asked. A test holds it to the real engines'.</summary>
    public static readonly IReadOnlyList<string> EngineNames = ["bing", "brave", "duckduckgo", "startpage", "wikipedia"];

    /// <summary>The filter asked for until the setting says otherwise (2026-11-06, the user's call: off).</summary>
    public const string DefaultSafeSearch = "off";

    /// <summary>The setting's words in menu order.</summary>
    public static readonly string[] SafeSearchNames = [DefaultSafeSearch, "moderate", "strict"];

    // The same language the other two engines ask for.
    private const string Locale = "en-US";
    private const string Category = "Web";

    private readonly MetaSearch _search;
    private readonly SearchPacer _pacer;

    public BuiltInMetaSearch(HttpClient http, TimeProvider time)
        : this(RealEngines(time), http, time)
    {
    }

    internal static IEngine[] RealEngines(TimeProvider time) =>
    [
        new BingEngine(),
        new BraveEngine(),
        new DuckDuckGoEngine(time),
        new StartpageEngine(time),

        // Wikimedia asks API clients to say who they are and how to reach them: the project's page, not a person's address.
        new WikipediaEngine { UserAgent = App.SidekickApp.Name + "/" + App.SidekickApp.Version + " (+" + App.AboutText.RepositoryUrl + ")" },
    ];

    internal BuiltInMetaSearch(IEnumerable<IEngine> engines, HttpClient http, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(time);
        _pacer = new SearchPacer(time, MinInterval);
        var client = new HttpClient(new HostClientHandler(http), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaxResponseBytes };
        _search = new MetaSearch(engines, client, new MetaSearchOptions(), time);
    }

    /// <summary>
    /// A search under <paramref name="effective"/>'s <c>Web metasearch engines</c> and <c>Web metasearch safe search</c> (2026-11-06). Made per
    /// search, as the settings can change between two, over this one instance, so the pacing and the rests carry over.
    /// </summary>
    public ISearchEngine For(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var saved = effective.WebMetasearchEngines;
        foreach (string unknown in (saved ?? []).Where(s => !EngineNames.Contains(s?.Trim(), StringComparer.OrdinalIgnoreCase)))
        {
            DiagnosticLog.Warn(Category, $"{nameof(AppSettingsData.WebMetasearchEngines)} has '{unknown}', which is not one of {string.Join(", ", EngineNames)}. Skipping it.");
        }

        return new Configured(this, saved is null ? null : EnginesOn(saved).ToHashSet(StringComparer.Ordinal), ResolveSafeSearch(effective.WebMetasearchSafeSearch));
    }

    /// <summary>The engines a saved list turns on, in <see cref="EngineNames"/>' order: all of them for null, the known names in any case otherwise.</summary>
    public static IReadOnlyList<string> EnginesOn(IReadOnlyList<string>? saved) =>
        saved is null ? EngineNames : EngineNames.Where(name => saved.Any(s => string.Equals(s?.Trim(), name, StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>The menu hint next to a safe-search word. Pinned.</summary>
    public static string DescribeSafeSearch(string name) => name switch
    {
        "off" => WebText.SafeSearchOffHint,
        "moderate" => WebText.SafeSearchModerateHint,
        "strict" => WebText.SafeSearchStrictHint,
        _ => "",
    };

    /// <summary>The saved word as the library's level: trimmed, in any case; anything else reads as off with a warning, as <see cref="SearchMethod.Resolve"/> does.</summary>
    private static SafeSearch ResolveSafeSearch(string? saved)
    {
        switch (saved?.Trim().ToLowerInvariant())
        {
            case "off": return SafeSearch.Off;
            case "moderate": return SafeSearch.Moderate;
            case "strict": return SafeSearch.Strict;
            default:
                DiagnosticLog.Warn(Category, $"{nameof(AppSettingsData.WebMetasearchSafeSearch)}='{saved}' is not one of {string.Join(", ", SafeSearchNames)}. Using {DefaultSafeSearch}.");
                return SafeSearch.Off;
        }
    }

    /// <summary>One search's settings over the shared instance.</summary>
    private sealed class Configured(BuiltInMetaSearch core, IReadOnlySet<string>? engines, SafeSearch safeSearch) : ISearchEngine
    {
        public string Name => WebText.MetaSearchName;

        /// <summary>None: the hits come from several engines, so no one results page is theirs.</summary>
        public Uri? ResultReferer => null;

        public Task<SearchOutcome> SearchAsync(string query, int max, FetchOptions options, CancellationToken cancellationToken) =>
            engines is { Count: 0 }
                ? Task.FromResult(SearchOutcome.Failed(WebText.MetaSearchNoEngines))
                : core.SearchAsync(query, max, options, engines, safeSearch, cancellationToken);
    }

    private async Task<SearchOutcome> SearchAsync(string query, int max, FetchOptions options, IReadOnlySet<string>? engines, SafeSearch safeSearch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);

        // Every engine is on the internet (2026-11-06). Asking would make five DNS lookups to hear the same refusal five times, then leave
        // every engine resting for a moment after a switch back. The socket check stays the real enforcement.
        if (options.Reach == NetworkReach.LocalAreaNetwork)
        {
            DiagnosticLog.Info(Category, $"Metasearch \"{query}\": not asked under network mode lan.");
            return SearchOutcome.Failed(WebText.MetaSearchNeedsInternet);
        }

        var asked = new EngineQuery(query.Trim(), SafeSearch: safeSearch, Locale: Locale, Engines: engines);
        if (_search.AnyAvailable(asked))
        {
            await _pacer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var response = await _search.SearchAsync(asked, cancellationToken).ConfigureAwait(false);
        string failures = string.Join("; ", response.Failures.Select(f => f.Engine + ": " + f.Message));

        // Info, not a warning (2026-11-06): warnings reach the transcript and headless output, and a slow or resting engine would print a
        // line on every search. A search this leaves empty tells the model why, below.
        if (failures.Length > 0)
        {
            DiagnosticLog.Info(Category, $"Metasearch \"{query}\": no answer from some engines. {failures}");
        }

        // The app's one rule for links, before anything is counted: a hit it couldn't show takes no slot, and only such hits are no results.
        var usable = response.Results
            .Select(r => (Hit: r, Link: Files.SafeText.WebLink(r.Url)))
            .Where(r => r.Link is not null)
            .ToList();
        if (response.Results.Count > usable.Count)
        {
            DiagnosticLog.Info(Category, $"Metasearch \"{query}\": left out {(response.Results.Count - usable.Count).ToString(CultureInfo.InvariantCulture)} hit(s) with no link the app would show.");
        }

        // Any failure with no results is an error (2026-11-06), even when another engine answered empty: "no results" would say the web
        // has nothing when most of the engines were never heard. Only when none answered and all refused would a retry be pointless.
        if (usable.Count == 0 && response.Failures.Count > 0)
        {
            bool refused = response.Timings.Count == 0 && response.Failures.All(f => IsRefusal(f.Kind == EngineFailureKind.Suspended ? f.RestCause : f.Kind));
            return SearchOutcome.Failed(refused ? WebText.MetaSearchRefused(failures) : WebText.SearchFailed(WebText.MetaSearchName, failures));
        }

        var results = usable
            .Take(max)
            .Select(r => new SearchResult(HtmlToMarkdown.Collapse(r.Hit.Title), r.Link!.Target, HtmlToMarkdown.Collapse(r.Hit.Content)))
            .ToArray();
        DiagnosticLog.Info(Category, $"Metasearch \"{query}\": {results.Length.ToString(CultureInfo.InvariantCulture)} results.");
        return new SearchOutcome(results, "");
    }

    private static bool IsRefusal(EngineFailureKind? kind) =>
        kind is EngineFailureKind.AccessDenied or EngineFailureKind.Captcha or EngineFailureKind.TooManyRequests;

    /// <summary>
    /// Hands the library's requests to the app's client. That client follows no redirects, because <see cref="WebFetcher"/> judges each
    /// hop, but some engines rely on them (Wikipedia's API sends a title on to its article's). So this follows them, with each hop a new
    /// request through the same client. It only follows to https, sends an engine's cookie to that site alone, and leaves a redirect that
    /// would resend a form for the engine to report. The app's client only reads the headers, so a body is buffered once, under
    /// <see cref="MaxResponseBytes"/>.
    /// </summary>
    internal sealed class HostClientHandler(HttpClient http) : HttpMessageHandler
    {
        internal const int MaxRedirects = 3;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // A message can only be sent once, and the library's HttpClient has already sent this one.
            var current = Copy(request, request.Method, request.RequestUri!, request.Content, keepCookie: true);
            for (int hop = 0; ; hop++)
            {
                var response = await http.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);   // the outer client reads the body, under its cap
                if (hop == MaxRedirects || RedirectTarget(current, response) is not { } target)
                {
                    response.RequestMessage ??= current;
                    return response;
                }

                response.Dispose();
                current = Copy(current, HttpMethod.Get, target, content: null, keepCookie: SameOrigin(current.RequestUri!, target));
            }
        }

        /// <summary>Where to go next as a GET, or null to hand the response to the engine.</summary>
        private static Uri? RedirectTarget(HttpRequestMessage request, HttpResponseMessage response)
        {
            if (response.Headers.Location is not { } location)
            {
                return null;
            }

            // These two keep the method, and only a GET is safe to repeat at an address the engine didn't choose.
            bool followable = response.StatusCode switch
            {
                HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther => true,
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect => request.Method == HttpMethod.Get,
                _ => false,
            };
            var target = new Uri(request.RequestUri!, location);
            return followable && target.Scheme == Uri.UriSchemeHttps ? target : null;
        }

        private static bool SameOrigin(Uri a, Uri b) =>
            a.Scheme == b.Scheme && a.Port == b.Port && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);

        private static HttpRequestMessage Copy(HttpRequestMessage request, HttpMethod method, Uri url, HttpContent? content, bool keepCookie)
        {
            var copy = new HttpRequestMessage(method, url)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy,
                Content = content,
            };
            copy.Options.Set(WebHttp.NoCookiesKey, true);
            foreach (var header in request.Headers.NonValidated)
            {
                if (keepCookie || !header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var value in header.Value)
                    {
                        copy.Headers.TryAddWithoutValidation(header.Key, value);
                    }
                }
            }

            return copy;
        }
    }
}
