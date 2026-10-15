using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

public class BrowserModeTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder()
    {
        Assert.Equal(new[] { "auto", "http", "browser" }, BrowserMode.Names);
        Assert.Equal("auto", BrowserMode.Default);
        Assert.Equal(BrowserMode.Default, new AppSettingsData().WebBrowserMode);
    }

    [Theory]
    [InlineData("auto", FetchEngine.Default)]
    [InlineData("http", FetchEngine.HttpClient)]
    [InlineData("browser", FetchEngine.Chromium)]
    [InlineData("  Chromium ", FetchEngine.Chromium)]
    public void TryParse_TrimsAndIgnoresCase(string text, FetchEngine expected)
    {
        Assert.True(BrowserMode.TryParse(text, out var engine));
        Assert.Equal(expected, engine);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("firefox")]
    [InlineData("edge")]
    public void TryParse_RejectsAnythingElse_AsDefault(string? text)
    {
        Assert.False(BrowserMode.TryParse(text, out var engine));
        Assert.Equal(FetchEngine.Default, engine);
    }

    [Fact]
    public void Name_RoundTripsEveryMode_AndEveryModeHasAHint()
    {
        foreach (var name in BrowserMode.Names)
        {
            Assert.True(BrowserMode.TryParse(name, out var engine));
            Assert.Equal(name, BrowserMode.Name(engine));
            Assert.NotEqual("", BrowserMode.Describe(name));
        }

        Assert.Equal("the HTTP client, then a headless browser when a page is blocked or empty", BrowserMode.Describe("auto"));
        Assert.Equal("the HTTP client alone, with browser-like headers", BrowserMode.Describe("http"));
        Assert.Equal("a headless Edge, Chrome or Brave for every page; a PDF over HTTP", BrowserMode.Describe("browser"));
        Assert.Equal("", BrowserMode.Describe("edge"));
    }

    [Fact]
    public void Resolve_MapsTheSavedMode()
    {
        Assert.Equal(FetchEngine.Chromium, BrowserMode.Resolve(new AppSettingsData { WebBrowserMode = "browser" }));
        Assert.Equal(FetchEngine.HttpClient, BrowserMode.Resolve(new AppSettingsData { WebBrowserMode = "HttpClient" }));
        Assert.Equal(FetchEngine.Default, BrowserMode.Resolve(new AppSettingsData()));
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Web" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(FetchEngine.Default, BrowserMode.Resolve(new AppSettingsData { WebBrowserMode = "edge" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Contains("WebBrowserMode='edge' is not one of auto, http, browser. Using auto.", warning.Message);
    }
}
