using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

public class SearchMethodTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder()
    {
        Assert.Equal(new[] { "metasearch", "duckduckgo", "searxng" }, SearchMethod.Names);
        Assert.Equal("metasearch", SearchMethod.Default);   // the user's call, 2026-11-06
        Assert.Equal(SearchMethod.Default, new AppSettingsData().WebSearchMethod);
    }

    [Theory]
    [InlineData("duckduckgo", SearchProvider.DuckDuckGo)]
    [InlineData("searxng", SearchProvider.Searxng)]
    [InlineData("metasearch", SearchProvider.MetaSearch)]
    [InlineData(" MetaSearch ", SearchProvider.MetaSearch)]
    [InlineData("  SearXNG ", SearchProvider.Searxng)]
    [InlineData("DuckDuckGo", SearchProvider.DuckDuckGo)]
    public void TryParse_TrimsAndIgnoresCase(string text, SearchProvider expected)
    {
        Assert.True(SearchMethod.TryParse(text, out var provider));
        Assert.Equal(expected, provider);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("google")]
    [InlineData("ddg")]
    public void TryParse_RejectsAnythingElse_AsDuckDuckGo(string? text)
    {
        Assert.False(SearchMethod.TryParse(text, out var provider));
        Assert.Equal(SearchProvider.DuckDuckGo, provider);
    }

    [Fact]
    public void Name_RoundTripsEveryMethod_AndEveryMethodHasAHint()
    {
        foreach (var name in SearchMethod.Names)
        {
            Assert.True(SearchMethod.TryParse(name, out var provider));
            Assert.Equal(name, SearchMethod.Name(provider));
            Assert.NotEqual("", SearchMethod.Describe(name));
        }

        Assert.Equal("built-in DuckDuckGo scrape, no setup", SearchMethod.Describe("duckduckgo"));
        Assert.Equal("external SearXNG instance at Web SearXNG URL", SearchMethod.Describe("searxng"));
        Assert.Equal("", SearchMethod.Describe("google"));
    }

    [Fact]
    public void Resolve_MapsTheSavedMethod()
    {
        Assert.Equal(SearchProvider.Searxng, SearchMethod.Resolve(new AppSettingsData { WebSearchMethod = "searxng" }));
        Assert.Equal(SearchProvider.Searxng, SearchMethod.Resolve(new AppSettingsData { WebSearchMethod = "SearXNG" }));
        Assert.Equal(SearchProvider.MetaSearch, SearchMethod.Resolve(new AppSettingsData()));
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Web" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(SearchProvider.MetaSearch, SearchMethod.Resolve(new AppSettingsData { WebSearchMethod = "google" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Contains("WebSearchMethod='google' is not one of metasearch, duckduckgo, searxng. Using metasearch.", warning.Message);
    }
}
