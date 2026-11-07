using NeonSidekick.Files;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The one vetting of what the app hands on (2026-11-02, review P1-9, P2-3, P12-3, P12-4, P12-8): <see cref="SafeText"/>'s web links,
/// shown paths and clipboard text, and the callers that used to spell the rule their own way agreeing on it.
/// </summary>
public class SafeTextTests
{
    [Fact]
    public void WebLink_TakesTheThreeSchemes_InAnyCase_AndNothingElse()
    {
        Assert.NotNull(SafeText.WebLink("HTTP://A.example/"));
        Assert.NotNull(SafeText.WebLink("https://a.example/x?y=1#z"));
        Assert.Equal("mailto:a@b.example", SafeText.WebLink("mailto:a@b.example")!.Target);
        Assert.Null(SafeText.WebLink("javascript:alert(1)"));
        Assert.Null(SafeText.WebLink("file:///C:/Windows/notepad.exe"));
        Assert.Null(SafeText.WebLink("obsidian://open?vault=x"));
        Assert.Null(SafeText.WebLink("https://"));
        Assert.Null(SafeText.WebLink(null));
    }

    /// <summary>It parses, but reading its host throws.</summary>
    [Fact]
    public void WebLink_OfAHostItCantRead_IsNoLink()
    {
        Assert.Null(SafeText.WebLink("https://xn--\u00fc.de/"));
    }

    [Fact]
    public void WebLink_RefusesHiddenCharacters_AndPastTheCap()
    {
        Assert.Null(SafeText.WebLink("https://a.example/\u202Etxt.exe"));   // a right-to-left override
        Assert.Null(SafeText.WebLink("https://pay\u200Bpal.example/"));     // a zero-width space
        Assert.Null(SafeText.WebLink("https://a.example/a\u0085b"));        // a C1 control (at an end it's trimmed as whitespace)
        Assert.Null(SafeText.WebLink("https://a.example/a\u009Bb"));
        Assert.Null(SafeText.WebLink("https://a.example/a\tb"));
        Assert.Null(SafeText.WebLink("https://a.example/" + new string('x', SafeText.MaxWebLinkLength)));
        Assert.NotNull(SafeText.WebLink("https://a.example/a b"));          // a plain space, escaped in the target
        Assert.False(SafeText.IsSafeWebText("https://a.example/a b"));      // never in a terminal's link
    }

    [Fact]
    public void WebLink_OpensTheAsciiHost_AndItsHintLeadsWithIt()
    {
        var idn = SafeText.WebLink("https://аpple.com/login")!;   // a Cyrillic а
        Assert.Equal("https://xn--pple-43d.com/login", idn.Target);
        Assert.StartsWith("https://xn--pple-43d.com/", idn.Hint, StringComparison.Ordinal);

        var userinfo = SafeText.WebLink("https://www.bank.example" + new string('x', 200) + "@evil.example/")!;
        Assert.StartsWith("https://…@evil.example/", userinfo.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("bank", userinfo.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ShownPath_EscapesWhatItWouldHide()
    {
        string shown = SafeText.ShownPath("C:\\a\u202Etxt.exe");
        Assert.DoesNotContain("\u202E", shown, StringComparison.Ordinal);
        Assert.Equal("C:\\plain.txt", SafeText.ShownPath("C:\\plain.txt"));
    }

    [Fact]
    public void ForClipboard_DropsHiddenCharacters_KeepsBreaksTabsAndAnEmojisJoiner()
    {
        Assert.Equal("ab", SafeText.ForClipboard("a\u200Bb", out int one));
        Assert.Equal(1, one);
        Assert.Equal("a\tb\r\nc\n", SafeText.ForClipboard("a\tb\r\nc\n", out int none));
        Assert.Equal(0, none);
        Assert.Equal("👩\u200D💻", SafeText.ForClipboard("👩\u200D💻", out int family));   // a ZWJ sequence keeps its glyph
        Assert.Equal(0, family);
        Assert.Equal("paypal.example", SafeText.ForClipboard("pay\u200Dpal.\u202Eexample", out int two));
        Assert.Equal(2, two);
    }

    /// <summary>The callers that spelled the rule four ways now agree: the transcript, its hover hint, the Web UI and the opener.</summary>
    [Theory]
    [InlineData("https://a.example/c", true)]
    [InlineData("HTTPS://A.EXAMPLE/C", true)]
    [InlineData("mailto:a@b.example", true)]
    [InlineData("https://a.example/\u202Ec", false)]
    [InlineData("https://a\u200B.example/", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/x.exe", false)]
    public void TheCallers_AgreeOnALink(string url, bool linked)
    {
        Assert.Equal(linked, LinkText.IsWeb(url) && LinkText.IsLinkable(url, web: true));
        Assert.Equal(linked, LinkText.WebTarget(url) is not null);
        Assert.Equal(linked, WebUiMarkdown.SafeLink(url) is not null);
        Assert.Equal(linked, SafeText.WebLink(url) is not null);
        Assert.Equal(linked, LinkText.HoverHint(url, null, outside: false, LinkText.HintWidth) is not null);
    }
}
