using System.Text.RegularExpressions;
using NeonSidekick.App;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The About page (2026-11-08, the user's ask): the source link left the bottom of every Web UI page (there since 2026-11-06, the AGPL offer for
/// the metasearch) for a short card behind ☰ › ℹ️ About, the menu's last entry. The card names the version and links its tag's source and both
/// licenses; no other page names the version, so a signed-out visitor gets no hint of it. The host sends <c>no-referrer</c> on the page, and
/// <c>noreferrer</c> on each link is a second guard.
/// </summary>
public class WebUiAboutPageTests
{
    private const string AboutAnchor = "<a href=\"/about\" role=\"menuitem\">ℹ️ About</a>";
    private const string AboutAnchorHere = "<a href=\"/about\" role=\"menuitem\" class=\"here\">ℹ️ About</a>";
    private static readonly Regex ExternalLink = new("<a href=\"(https://[^\"]+)\"([^>]*)>", RegexOptions.CultureInvariant);

    private static string Folder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NeonSidekick.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "assets", "webui");
    }

    private static string Page(string name) => File.ReadAllText(Path.Combine(Folder(), name)).Replace("\r\n", "\n");

    private static string Served(string name) => System.Text.Encoding.UTF8.GetString(WebUiAssets.Find(name)!.Bytes);

    /// <summary>The inline link is gone everywhere (the sign-in and no-tabs pages too): only the About card says "Source code".</summary>
    [Fact]
    public void NoPageButTheAboutPage_LinksToTheSource()
    {
        var pages = Directory.GetFiles(Folder(), "*.html");

        Assert.Contains(pages, p => Path.GetFileName(p) == WebUiAssets.AboutPage);
        Assert.All(pages, p => Assert.DoesNotContain("class=\"source-link\"", File.ReadAllText(p), StringComparison.Ordinal));
        Assert.All(pages.Where(p => Path.GetFileName(p) != WebUiAssets.AboutPage), p => Assert.DoesNotContain("Source code", File.ReadAllText(p), StringComparison.Ordinal));
        Assert.DoesNotContain(".source-link", Page("app.css"), StringComparison.Ordinal);
        Assert.DoesNotContain(".source-link", Page("chat.js"), StringComparison.Ordinal);
    }

    /// <summary>Every page with a tab menu ends it with About, marked as the open one on the About page alone.</summary>
    [Theory]
    [InlineData("index.html", false)]
    [InlineData("home.html", false)]
    [InlineData("shopping.html", false)]
    [InlineData("youtube.html", false)]
    [InlineData("about.html", true)]
    public void EveryTabMenu_EndsWithAbout(string page, bool here)
    {
        string html = Page(page);

        Assert.Contains((here ? AboutAnchorHere : AboutAnchor) + "</div></div></nav>", html, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(html, "href=\"/about\""));
    }

    /// <summary>The served card: this version, its tag's source, both licenses at that tag, the © line; nothing left unstamped.</summary>
    [Fact]
    public void TheServedAboutPage_NamesThisVersion_AndLinksItsSourceAndLicenses()
    {
        string html = Served(WebUiAssets.AboutPage);
        string version = SidekickApp.Version;

        Assert.Contains("<h1>NeonSidekick " + version + "</h1>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"" + AboutText.SourceUrl(version) + "\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"" + AboutText.SourceFileUrl(version) + "LICENSE\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"" + AboutText.SourceFileUrl(version) + "LICENSE-AGPL\"", html, StringComparison.Ordinal);
        Assert.Contains(AboutText.Copyright + " · " + AboutText.LicenseName, html, StringComparison.Ordinal);
        Assert.Contains("SearXNG.Net", html, StringComparison.Ordinal);
        Assert.Contains("AGPL-3.0-or-later", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", html, StringComparison.Ordinal);

        var links = ExternalLink.Matches(html);
        Assert.Equal(3, links.Count);
        Assert.All(links, m => Assert.Contains("target=\"_blank\" rel=\"noopener noreferrer\"", m.Groups[2].Value, StringComparison.Ordinal));
    }

    /// <summary>No other served page names the version: a signed-out visitor (the sign-in page) gets no hint of it.</summary>
    [Theory]
    [InlineData("index.html")]
    [InlineData("home.html")]
    [InlineData("shopping.html")]
    [InlineData("youtube.html")]
    [InlineData("notabs.html")]
    [InlineData("signin.html")]
    public void NoOtherServedPage_NamesTheVersion(string page)
    {
        string html = Served(page);

        Assert.DoesNotContain("/tree/v", html, StringComparison.Ordinal);
        Assert.DoesNotContain(AboutText.RepositoryUrl, html, StringComparison.Ordinal);
    }

    /// <summary>The tag a browser caches by covers what's served, the stamped version included.</summary>
    [Fact]
    public void TheAboutPagesETag_CoversTheStampedBytes()
    {
        var asset = WebUiAssets.Find(WebUiAssets.AboutPage)!;

        Assert.Equal("\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(asset.Bytes))[..16] + "\"", asset.ETag);
    }

    /// <summary>About is no tab, so the menu never empties: the "menu goes with its last tab" toggle went, and the button's label says Menu.</summary>
    [Fact]
    public void TheMenu_IsNeverHidden_AndItsLabelSaysMenu()
    {
        string js = Page("common.js");

        Assert.DoesNotContain("menuItems().length === 0", js, StringComparison.Ordinal);
        Assert.Contains("'Menu: '", js, StringComparison.Ordinal);
        Assert.DoesNotContain("'Tabs: '", js, StringComparison.Ordinal);
    }
}
