using System.Net;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>Resolves once per assembly whether the web is reachable (<c>https://example.com</c> answers in 3 s). Skipped offline; never a CI safety net.</summary>
internal static class LiveWeb
{
    public static readonly bool Reachable;
    public static readonly string Unavailable;

    static LiveWeb()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = http.GetAsync("https://example.com/", HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            Reachable = response.IsSuccessStatusCode;
            Unavailable = Reachable ? "" : $"example.com answered {(int)response.StatusCode}.";
        }
        catch (Exception ex)
        {
            Unavailable = "No web: " + ex.Message;
        }
    }
}

/// <summary>Skips unless the web is reachable: one real fetch, one real search — a local pre-merge gate, kept to one request each.</summary>
public sealed class LiveWebFactAttribute : FactAttribute
{
    public LiveWebFactAttribute()
    {
        if (!LiveWeb.Reachable)
        {
            Skip = LiveWeb.Unavailable;
        }
    }
}

public class LiveWebTests
{
    private static WebAccess Web(NetworkReach reach = NetworkReach.Internet) => new(WebHttp.Create(new LanPolicy(() => reach)), new FakeHeadlessBrowser { Executable = null }, TimeProvider.System);

    [LiveWebFact]
    public async Task Fetch_ExampleCom_OverTheRealClient_AndTheLanPolicy()
    {
        var page = await Web().Fetcher.FetchAsync(new Uri("https://example.com/"), new FetchOptions(FetchEngine.HttpClient, "", NetworkReach.Internet), CancellationToken.None);

        Assert.True(page.Ok, page.Error);
        Assert.Equal("Example Domain", page.Page!.Title);
        // The body's paragraph, not a heading: the page has had no <h1> since its 2026 rewrite (seen 2026-09-28).
        Assert.Contains("This domain is for use in documentation examples", page.Page.Markdown);
        Assert.Equal(WebText.HttpEngine, page.Engine);

        // The socket policy: loopback refused at the connect, the request never made.
        var refused = await Web().Fetcher.FetchAsync(new Uri("http://127.0.0.1:1/"), new FetchOptions(FetchEngine.HttpClient, "", NetworkReach.Internet), CancellationToken.None);
        Assert.Equal(FetchOutcome.LanRefused, refused.Outcome);
    }

    [LiveWebFact]
    public async Task Fetch_ExampleCom_IsRefused_UnderLocalAreaNetwork_AtTheSocket()
    {
        // The fetcher's pre-check is skipped with Both so the socket policy alone judges (2026-09-18).
        var refused = await Web(NetworkReach.LocalAreaNetwork).Fetcher.FetchAsync(new Uri("https://example.com/"), new FetchOptions(FetchEngine.HttpClient, "", NetworkReach.Both), CancellationToken.None);

        Assert.Equal(FetchOutcome.InternetRefused, refused.Outcome);
        Assert.Equal(WebText.InternetRefused("example.com"), refused.Error);
    }

    [LiveWebFact]
    public async Task Search_DuckDuckGo_OverTheRealClient()
    {
        var outcome = await Web().DuckDuckGo.SearchAsync("example domain reserved iana", 3, new FetchOptions(FetchEngine.HttpClient, "", NetworkReach.Internet), CancellationToken.None);

        // A refusal is the endpoint's rate limit, not a bug: the test reports it and stops rather than failing.
        Assert.True(outcome.Ok || outcome.Error.Contains("refused the search", StringComparison.Ordinal), outcome.Error);
        if (outcome.Ok)
        {
            Assert.InRange(outcome.Results.Count, 1, 3);
            Assert.All(outcome.Results, r => Assert.StartsWith("http", r.Url));
        }
    }
}
