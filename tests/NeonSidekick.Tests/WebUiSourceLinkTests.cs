using System.Text.RegularExpressions;
using NeonSidekick.App;

namespace NeonSidekick.Tests;

/// <summary>
/// Every Web UI page links to the source (2026-11-06): the app combined with the metasearch's AGPL code, and the Web UI is how remote users reach it.
/// The host sends <c>no-referrer</c> on every page but the YouTube tab, so <c>noreferrer</c> is a second guard there. The YouTube tab sends
/// <c>strict-origin-when-cross-origin</c> for the player, so it's the only guard on that page, whose URL carries no token.
/// </summary>
public class WebUiSourceLinkTests
{
    private static readonly Regex Link = new("<a class=\"source-link\" href=\"([^\"]+)\" target=\"_blank\" rel=\"noopener noreferrer\">Source code</a>", RegexOptions.CultureInvariant);

    private static string Page(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NeonSidekick.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir.FullName, "assets", "webui", name)).Replace("\r\n", "\n");
    }

    [Theory]
    [InlineData("index.html", "</main>")]
    [InlineData("home.html", "</main>")]
    [InlineData("shopping.html", "</main>")]
    [InlineData("youtube.html", "</main>")]
    [InlineData("signin.html", "</div>\n</body>")]
    [InlineData("notabs.html", "</div>\n</body>")]
    public void EveryPage_EndsItsContent_WithTheSourceLink(string page, string after)
    {
        string html = Page(page);

        Assert.Equal(AboutText.RepositoryUrl, Assert.Single(Link.Matches(html)).Groups[1].Value);
        Assert.Contains(Link.Match(html).Value + "\n" + after, html, StringComparison.Ordinal);
    }

    /// <summary>Outside the nav, where common.js would take it for a tab, and after the log on the chat page, not inside it.</summary>
    [Fact]
    public void TheChatPage_HasItAfterTheLog()
    {
        string html = Page("index.html");

        Assert.Contains("<div id=\"log\" aria-live=\"polite\"></div>\n  " + Link.Match(html).Value, html, StringComparison.Ordinal);
    }

    [Fact]
    public void FillingTheWindow_HidesIt()
    {
        string css = Page("app.css");

        Assert.Contains("body.yt-windowed .source-link { display: none; }", css, StringComparison.Ordinal);
    }

    /// <summary>The splash's room counts it, or the welcome page would scroll by its height.</summary>
    [Fact]
    public void TheSplash_MakesRoomForIt()
    {
        string js = Page("chat.js");

        Assert.Contains("main.querySelector('.source-link')", js, StringComparison.Ordinal);
        Assert.Contains("- others - linkRoom)", js, StringComparison.Ordinal);
    }

    /// <summary>A page added later is held to the same rule. The theory above names each page's own place for the link.</summary>
    [Fact]
    public void EveryPageInTheFolder_HasTheLink()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NeonSidekick.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var pages = Directory.GetFiles(Path.Combine(dir.FullName, "assets", "webui"), "*.html");

        Assert.NotEmpty(pages);
        Assert.All(pages, page => Assert.Matches(Link, File.ReadAllText(page)));
    }

    /// <summary>The sign-in page keeps the repository's home: a signed-out visitor gets no hint of the version.</summary>
    [Theory]
    [InlineData("index.html", true)]
    [InlineData("home.html", true)]
    [InlineData("shopping.html", true)]
    [InlineData("youtube.html", true)]
    [InlineData("notabs.html", true)]
    [InlineData("signin.html", false)]
    public void TheServedPage_LinksToItsVersionsSource(string page, bool versioned)
    {
        var asset = NeonSidekick.WebUi.WebUiAssets.Find(page)!;
        string expected = versioned ? AboutText.RepositoryUrl + "/tree/v" + SidekickApp.Version : AboutText.RepositoryUrl;

        Assert.Equal(expected, Link.Match(System.Text.Encoding.UTF8.GetString(asset.Bytes)).Groups[1].Value);

        // The tag a browser caches by covers what's served, link included.
        Assert.Equal("\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(asset.Bytes))[..16] + "\"", asset.ETag);
    }
}
