// SPDX-License-Identifier: AGPL-3.0-or-later

using NeonSidekick.Tests.Fakes;
using SearXNG.Net.Engines;

namespace SearXNG.Net.Tests;

/// <summary>
/// Time ranges, as SearXNG <c>3e95f6c</c> has them (2026-11-15, the web-tools plan's §4): Brave's <c>tf</c>, Startpage's <c>with_date</c>,
/// the Brave API's <c>freshness</c> (a difference from SearXNG), each engine's <c>time_range_support</c> after <c>settings.yml</c>, and an
/// engine without it sitting a ranged search out as skipped, never failed or rested.
/// </summary>
public sealed class TimeRangeTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly HttpClient _http = new(new StubHandler(_ => StubHandler.Text("")));

    [Theory]
    [InlineData(TimeRange.Day, "pd")]
    [InlineData(TimeRange.Week, "pw")]
    [InlineData(TimeRange.Month, "pm")]
    [InlineData(TimeRange.Year, "py")]
    public void Brave_AsksForTheRange_InTf_AndTheBraveApi_InFreshness(TimeRange range, string word)
    {
        Assert.Equal("https://search.brave.com/search?q=rust+aot&source=web&offset=1&tf=" + word, BraveEngine.BuildUrl(new EngineQuery("rust aot", PageNumber: 2, TimeRange: range)).AbsoluteUri);
        Assert.EndsWith("&freshness=" + word, BraveApiEngine.BuildUrl(new EngineQuery("rust aot", TimeRange: range)).AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutARange_NeitherBraveAsksForOne()
    {
        Assert.DoesNotContain("tf=", BraveEngine.BuildUrl(new EngineQuery("x")).AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("freshness", BraveApiEngine.BuildUrl(new EngineQuery("x")).AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("", StartpageEngine.BuildForm(new EngineQuery("x"), "code").Single(p => p.Key == "with_date").Value);
    }

    [Theory]
    [InlineData(TimeRange.Day, "d")]
    [InlineData(TimeRange.Week, "w")]
    [InlineData(TimeRange.Month, "m")]
    [InlineData(TimeRange.Year, "y")]
    public void Startpage_FillsWithDate(TimeRange range, string word) =>
        Assert.Equal(word, StartpageEngine.BuildForm(new EngineQuery("x", TimeRange: range), "code").Single(p => p.Key == "with_date").Value);

    [Fact]
    public void EachEngine_SupportsARange_AsSearXNGsSettingsHaveIt()
    {
        IEngine[] engines = [new BingEngine(), new BraveEngine(), new BraveApiEngine(() => null), new DuckDuckGoEngine(_time), new StartpageEngine(_time), new WikipediaEngine()];

        Assert.Equal(["brave", "braveapi", "startpage"], engines.Where(e => e.TimeRangeSupport).Select(e => e.Name));
    }

    [Fact]
    public async Task ARangedSearch_SkipsTheEnginesWithoutSupport_NeitherFailedNorRested()
    {
        var dated = new FakeEngine("dated", new EngineResult("https://a.example/", "A", "")) { TimeRangeSupport = true };
        var undated = new FakeEngine("undated", new EngineResult("https://b.example/", "B", ""));
        var search = new MetaSearch([dated, undated], _http, new MetaSearchOptions(), _time);

        var ranged = await search.SearchAsync(new EngineQuery("x", TimeRange: TimeRange.Week));

        Assert.Equal(["undated"], ranged.Skipped);
        Assert.Empty(ranged.Failures);
        Assert.Equal(0, undated.Calls);
        Assert.Equal(TimeRange.Week, Assert.Single(dated.Queries).TimeRange);

        var plain = await search.SearchAsync(new EngineQuery("x"));
        Assert.Empty(plain.Skipped);
        Assert.Equal(1, undated.Calls);
        Assert.False(search.AnyAvailable(new EngineQuery("x", TimeRange: TimeRange.Day, Engines: new HashSet<string> { "undated" })));
    }
}
