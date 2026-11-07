// SPDX-License-Identifier: AGPL-3.0-or-later

using SearXNG.Net.Results;

namespace SearXNG.Net.Tests;

/// <summary>Expected values follow urllib.parse's documented behavior and the code in searx/result_types/_base.py.</summary>
public class ResultNormalizationTests
{
    [Fact]
    public void Parse_SplitsEveryPart_LeavingEscapesAlone()
    {
        var url = ParsedUrl.Parse("HTTPS://Example.com:8080/a%20b/c;p=1?q=%C3%A4&r=2#top");

        Assert.Equal("https", url.Scheme);
        Assert.Equal("Example.com:8080", url.Netloc);
        Assert.Equal("/a%20b/c", url.Path);
        Assert.Equal("p=1", url.Params);
        Assert.Equal("q=%C3%A4&r=2", url.Query);
        Assert.Equal("top", url.Fragment);
    }

    [Fact]
    public void Parse_TakesParamsOnlyFromTheLastSegment()
    {
        var url = ParsedUrl.Parse("https://example.com/a;x/b");

        Assert.Equal("/a;x/b", url.Path);
        Assert.Equal("", url.Params);
    }

    [Fact]
    public void Parse_RemovesTabsAndNewlines_AndLeadingSpace()
    {
        Assert.Equal("example.com", ParsedUrl.Parse("  https://exa\tmple\n.com/").Netloc);
    }

    [Theory]
    [InlineData("https://example.com/a;p?q=1#f")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/")]
    public void GetUrl_PutsBackWhatParseSplit(string url)
    {
        Assert.Equal(url, ParsedUrl.Parse(url).GetUrl());
    }

    [Fact]
    public void GetUrl_DropsAnEmptyQueryAndFragment_AsPython()
    {
        Assert.Equal("https://example.com/a", ParsedUrl.Parse("https://example.com/a?#").GetUrl());
    }

    [Fact]
    public void NormalizeUrl_GivesASchemelessUrlHttp()
    {
        Assert.Equal("http://example.com/x", ResultNormalizer.NormalizeUrl("//example.com/x")!.GetUrl());
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hello")]
    [InlineData("file:///etc/passwd")]
    [InlineData("example.com/no-host")]
    [InlineData("")]
    public void NormalizeUrl_RefusesWhatIsNotAWebAddress(string url)
    {
        Assert.Null(ResultNormalizer.NormalizeUrl(url));
    }

    [Theory]
    [InlineData("https://xn--strung-xxa.de/a", "störung.de")] // The example in SearXNG's own comment.
    [InlineData("https://xn--strung-xxa.de:8443/a", "störung.de:8443")]
    public void NormalizeUrl_DecodesInternationalDomains(string url, string netloc)
    {
        Assert.Equal(netloc, ResultNormalizer.NormalizeUrl(url)!.Netloc);
    }

    /// <summary>Many short labels, as one long label is refused quickly anyway. Decoding this took about 4 seconds.</summary>
    [Fact]
    public void NormalizeUrl_ComparesAHostLongerThanAnyDnsName_AsSent_WithoutDecodingIt()
    {
        string host = string.Concat(Enumerable.Repeat("xn--a.", 250_000)) + "example";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var parsed = ResultNormalizer.NormalizeUrl("https://" + host + "/");

        Assert.Equal(host, parsed!.Netloc);
        Assert.True(watch.ElapsedMilliseconds < 1000, watch.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public void NormalizeUrl_KeepsAMalformedInternationalDomainAsSent()
    {
        Assert.Equal("xn--ü.de", ResultNormalizer.NormalizeUrl("https://xn--ü.de/")!.Netloc);
    }

    [Fact]
    public void NormalizeText_CollapsesAndTrims()
    {
        Assert.Equal("a b c", ResultNormalizer.NormalizeText("  a \t b\n\n c ", 200));
    }

    [Fact]
    public void NormalizeText_ShortensAtAWord()
    {
        Assert.Equal("hello …", ResultNormalizer.NormalizeText("hello world", 9));
    }

    [Fact]
    public void NormalizeText_ShortensInsideAWordWhenThereIsNoSpace()
    {
        Assert.Equal("helloworl …", ResultNormalizer.NormalizeText("helloworldagain", 9));
    }

    [Fact]
    public void NormalizeText_CountsCodePoints_SoAnEmojiIsOneCharacter()
    {
        var emoji = "\U0001F600";

        Assert.Equal(emoji + emoji, ResultNormalizer.NormalizeText(emoji + emoji, 2));
        Assert.Equal(emoji + emoji + " …", ResultNormalizer.NormalizeText(emoji + emoji + emoji, 2));
        Assert.Equal(2, ResultNormalizer.CodePoints(emoji + emoji));
    }
}
