using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;
using SearXNG.Net;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>web_search</c>'s <c>since</c> (2026-11-15, the web-tools plan's §4, feature idea #25): the word read, a SearXNG instance's
/// <c>time_range</c>, the metasearch passing it on and refusing when no engine turned on can filter, and the header naming the range.
/// </summary>
public sealed class WebSearchSinceTests
{
    private static readonly FetchOptions Options = new(FetchEngine.HttpClient, "", NetworkReach.Internet);

    private readonly StubHttpMessageHandler _http = new();
    private readonly ManualTimeProvider _time = new();

    [Theory]
    [InlineData("week", SearchSince.Week)]
    [InlineData(" Day ", SearchSince.Day)]
    [InlineData("MONTH", SearchSince.Month)]
    [InlineData("year", SearchSince.Year)]
    public void Since_IsReadTrimmed_InAnyCase(string raw, SearchSince expected) =>
        Assert.Equal((expected, (string?)null), WebSearchTool.ParseSince(raw));

    [Fact]
    public void ABlankSince_IsNotGiven_AndAnythingElse_IsRefusedNamingTheFour()
    {
        Assert.Equal((null, null), WebSearchTool.ParseSince("  "));
        var (since, error) = WebSearchTool.ParseSince("fortnight‮");
        Assert.Null(since);
        Assert.StartsWith("Error: since must be day, week, month or year, not \"fortnight", error, StringComparison.Ordinal);
        Assert.DoesNotContain('‮', error!);
    }

    [Fact]
    public void TheSchema_OffersTheFourWords()
    {
        var since = new WebSearchTool(new WebAccess(new HttpClient(_http), new FakeHeadlessBrowser(), _time), () => new AppSettingsData()).JsonSchema.GetProperty("properties").GetProperty("since");

        Assert.Equal(["day", "week", "month", "year"], since.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData(SearchSince.Day, "day")]
    [InlineData(SearchSince.Week, "week")]
    [InlineData(SearchSince.Month, "month")]
    [InlineData(SearchSince.Year, "year")]
    public void AnInstance_IsAskedForSearXNGsLowercaseWord(SearchSince since, string word)
    {
        Assert.Equal("https://searx.example/search?q=x&format=json&language=en&time_range=" + word, SearxngSearch.QueryUrl(new Uri("https://searx.example/"), "x", since).AbsoluteUri);
        Assert.Equal("https://searx.example/search?q=x&format=json&language=en", SearxngSearch.QueryUrl(new Uri("https://searx.example/"), "x").AbsoluteUri);
    }

    /// <summary>A SearXNG hit is vetted as the built-in one's are (2026-11-16, the web-tools review's #82): a disguised address is dropped.</summary>
    [Fact]
    public void ASearxngHit_WithAnAddressNoLinkWouldTake_IsDropped()
    {
        var hits = SearxngSearch.Parse("""{"results":[{"url":"https://bad.example/a\nb","title":"Bad","content":""},{"url":"https://ok.example/","title":"Ok","content":""}]}""");

        Assert.Equal(["https://ok.example/"], hits.Select(h => h.Url));
    }

    [Fact]
    public void TheHeader_NamesTheRange_AndTheEnginesThatAnswered()
    {
        var hit = new SearchResult("T", "https://a.example/", "");

        Assert.StartsWith("Searched \"x\" (1 result, past week; brave, startpage):", WebText.Results("x", [hit], WebText.MetaSearchName, SearchSince.Week, "brave, startpage"), StringComparison.Ordinal);
        Assert.StartsWith("Searched \"x\" (1 result, past day, " + WebText.SearxngName + "):", WebText.Results("x", [hit], WebText.SearxngName, SearchSince.Day), StringComparison.Ordinal);
        Assert.StartsWith("Searched \"x\" (1 result, " + WebText.MetaSearchName + "):", WebText.Results("x", [hit], WebText.MetaSearchName), StringComparison.Ordinal);
        // The hint is the engine's to give (2026-11-16, the web-tools review's #16): only when an engine sat the range out.
        Assert.Equal("Searched \"x\" (past year, " + WebText.SearxngName + "): no results" + WebText.NoResultsSinceMayHint, WebText.Results("x", [], WebText.SearxngName, SearchSince.Year, hint: WebText.NoResultsSinceMayHint));
        Assert.Equal("Searched \"x\" (past year, " + WebText.SearxngName + "): no results", WebText.Results("x", [], WebText.SearxngName, SearchSince.Year));
        Assert.Equal("Searched \"x\" (" + WebText.SearxngName + "): no results", WebText.Results("x", [], WebText.SearxngName));
    }

    [Fact]
    public async Task TheMetasearch_PassesTheRange_AndNamesTheEnginesThatAnswered()
    {
        var dated = new FakeEngine("brave", new EngineResult("https://a.example/", "A", "")) { TimeRangeSupport = true };
        var undated = new FakeEngine("bing", new EngineResult("https://b.example/", "B", ""));
        var search = new BuiltInMetaSearch([undated, dated], new HttpClient(_http), _time).For(new AppSettingsData { WebMetasearchEngines = ["bing", "brave"] });

        var outcome = await search.SearchAsync("x", 5, Options, CancellationToken.None, SearchSince.Month);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal("brave", outcome.Scope);
        Assert.Equal(TimeRange.Month, Assert.Single(dated.Queries).TimeRange);
        Assert.Empty(undated.Queries);
    }

    [Fact]
    public async Task WithNoEngineOnThatFilters_ARangedSearch_IsRefusedBeforeAnyRequest_NamingTheOnesThatCan()
    {
        var dated = new FakeEngine("brave") { TimeRangeSupport = true };
        var undated = new FakeEngine("bing", new EngineResult("https://b.example/", "B", ""));
        var search = new BuiltInMetaSearch([undated, dated], new HttpClient(_http), _time).For(new AppSettingsData { WebMetasearchEngines = ["bing"] });

        var outcome = await search.SearchAsync("x", 5, Options, CancellationToken.None, SearchSince.Week);

        Assert.Equal(WebText.MetaSearchNoTimeRange(["brave"]), outcome.Error);
        Assert.Empty(undated.Queries);
        Assert.True((await search.SearchAsync("x", 5, Options, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task TheTool_RefusesABadSince_BeforeSearching()
    {
        var tool = new WebSearchTool(new WebAccess(new HttpClient(_http), new FakeHeadlessBrowser(), _time), () => new AppSettingsData());

        string answer = (string)(await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "x", ["since"] = "decade" })))!;

        Assert.StartsWith("Error: since must be", answer, StringComparison.Ordinal);
        Assert.Empty(_http.Requests);
    }
}
