using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// YouTube open links in (2026-10-16, the user's ask and words): the four words and the default pinned, a case-insensitive parse, and a
/// hand-edited value read as the default with one warning per value.
/// </summary>
public class YouTubeOpenLinksInTests
{
    [Fact]
    public void TheWords_AndTheDefault_ArePinned()
    {
        Assert.Equal(["youtube-window", "browser-window", "default-browser", "webui-window"], YouTubeOpenLinksIn.Names);
        Assert.Equal("youtube-window", YouTubeOpenLinksIn.Default);
        Assert.Equal(YouTubeOpenLinksIn.Default, new AppSettingsData().YouTubeOpenLinksIn);
        Assert.All(YouTubeOpenLinksIn.Names, n => Assert.NotEmpty(YouTubeOpenLinksIn.Describe(n)));
        Assert.Equal("", YouTubeOpenLinksIn.Describe("phone"));
    }

    [Theory]
    [InlineData("youtube-window", YouTubeOpenLinksInKind.YouTubeWindow)]
    [InlineData(" Browser-Window ", YouTubeOpenLinksInKind.BrowserWindow)]
    [InlineData("DEFAULT-BROWSER", YouTubeOpenLinksInKind.DefaultBrowser)]
    [InlineData("webui-window", YouTubeOpenLinksInKind.WebUiWindow)]
    public void Parse_IgnoresCaseAndBlanks_AndNamesRoundTrip(string text, YouTubeOpenLinksInKind kind)
    {
        Assert.True(YouTubeOpenLinksIn.TryParse(text, out var parsed));
        Assert.Equal(kind, parsed);
        Assert.Equal(text.Trim().ToLowerInvariant(), YouTubeOpenLinksIn.Name(parsed));
    }

    [Fact]
    public void AnUnknownValue_ReadsAsTheDefault_WarningOncePerValue()
    {
        Assert.False(YouTubeOpenLinksIn.TryParse("phone", out var kind));
        Assert.Equal(YouTubeOpenLinksInKind.YouTubeWindow, kind);
        Assert.False(YouTubeOpenLinksIn.TryParse(null, out _));

        var warnings = new List<string>();
        void OnEmitted(DiagnosticEvent entry)
        {
            if (entry.Category == "YouTube" && entry.Message.Contains("YouTubeOpenLinksIn", StringComparison.Ordinal))
            {
                warnings.Add(entry.Message);
            }
        }

        DiagnosticLog.Emitted += OnEmitted;
        try
        {
            string odd = "tv-" + Guid.NewGuid().ToString("N")[..6];
            var data = new AppSettingsData { YouTubeOpenLinksIn = odd };
            Assert.Equal(YouTubeOpenLinksInKind.YouTubeWindow, YouTubeOpenLinksIn.Resolve(data));
            Assert.Equal(YouTubeOpenLinksInKind.YouTubeWindow, YouTubeOpenLinksIn.Resolve(data));
            Assert.Single(warnings);
            Assert.Contains(odd, warnings[0]);
            Assert.Equal(YouTubeOpenLinksInKind.WebUiWindow, YouTubeOpenLinksIn.Resolve(new AppSettingsData { YouTubeOpenLinksIn = "webui-window" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= OnEmitted;
        }
    }
}
