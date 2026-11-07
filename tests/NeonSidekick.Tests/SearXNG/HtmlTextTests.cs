// SPDX-License-Identifier: AGPL-3.0-or-later

using HtmlAgilityPack;
using NeonSidekick.Tests.Fakes;
using SearXNG.Net.Engines;
using SearXNG.Net.Html;
using SearXNG.Net.Net;

namespace SearXNG.Net.Tests;

public class HtmlTextTests
{
    private static string Nested(int depth) => string.Concat(Enumerable.Repeat("<div>", depth)) + "x" + string.Concat(Enumerable.Repeat("</div>", depth));

    /// <summary>All three engines that read HTML stop at the limit, rather than handing a stack overflow to whatever walks the tree next.</summary>
    [Fact]
    public void DeepNesting_ThrowsInEveryEngine()
    {
        string page = Nested(10_000);

        Assert.ThrowsAny<Exception>(() => BingEngine.Parse(page));
        Assert.ThrowsAny<Exception>(() => DuckDuckGoEngine.ExtractFirstPageLink(page));
        Assert.ThrowsAny<Exception>(() => StartpageEngine.ParseCode(page));
    }

    [Fact]
    public void NestingWellPastARealPage_StillParses()
    {
        string page = Nested(200);

        Assert.Null(DuckDuckGoEngine.ExtractFirstPageLink(page));
        Assert.Null(StartpageEngine.ParseCode(page));
    }

    [Fact]
    public async Task DeepNesting_IsThatEnginesFailure_NotTheSearchs()
    {
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Text(Nested(10_000))));
        var healthy = new FakeEngine("healthy", new EngineResult("https://a.example/", "A", ""));
        var search = new MetaSearch([new BingEngine(), healthy], http);

        var response = await search.SearchAsync(new EngineQuery("x"));

        Assert.Equal(EngineFailureKind.UnexpectedResponse, Assert.Single(response.Failures).Kind);
        Assert.Single(response.Results);
    }

    /// <summary>The first five are the doctests of html_to_text in searx/utils.py, verbatim.</summary>
    [Theory]
    [InlineData("Example <span id=\"42\">#2</span>", "Example #2")]
    [InlineData("<style>.span { color: red; }</style><span>Example</span>", "Example")]
    [InlineData("regexp: (?&lt;![a-zA-Z]", "regexp: (?<![a-zA-Z]")]
    [InlineData("<p><b>Lorem ipsum </i>dolor sit amet</p>", "Lorem ipsum </i>dolor sit amet</p>")]
    [InlineData("&#x3e &#x3c &#97", "> < a")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("<b>Native</b> <b>AOT</b> samples", "Native AOT samples")]
    [InlineData("Let&#x27;s go", "Let's go")]
    [InlineData("a<br/>b", "a b")]
    [InlineData("a <script>var x = '<b>no</b>';</script> b", "a  b")] // Whitespace collapses before tags are parsed, so both spaces stay.
    [InlineData("a <!-- hidden --> b", "a  b")]
    [InlineData("1 < 2", "1 < 2")]
    [InlineData("x <a href=\"a>b\">y</a>", "x y")]
    [InlineData("</b>stray", "stray")]
    [InlineData("  many   spaces\n\nhere ", "many spaces here")]
    [InlineData("unclosed <b", "unclosed")] // Confirmed against SearXNG: a cut-off tag is dropped.
    public void FromFragment_MatchesHtmlToText(string? html, string expected)
    {
        Assert.Equal(expected, HtmlText.FromFragment(html));
    }

    [Theory]
    [InlineData("&amp;", "&")]
    [InlineData("&amp", "&")]
    [InlineData("&lt;b&gt;", "<b>")]
    [InlineData("&#183;", "·")]
    [InlineData("&#0183;", "·")]
    [InlineData("&#xB7;", "·")]
    [InlineData("&#150;", "–")] // Windows-1252's en dash, as browsers and html.unescape read the C1 range.
    [InlineData("&#0;", "�")]
    [InlineData("&#xD800;", "�")]
    [InlineData("&#99999999999;", "�")]
    [InlineData("&#128512;", "\U0001F600")]
    [InlineData("&apos;", "'")]
    [InlineData("&notanentity;", "¬anentity;")] // Confirmed against SearXNG: the longest known start of a name decodes.
    [InlineData("&xyzzy;", "&xyzzy;")]
    [InlineData("AT&T", "AT&T")]
    [InlineData("no references", "no references")]
    public void Unescape_MatchesHtmlUnescape(string text, string expected)
    {
        Assert.Equal(expected, HtmlText.Unescape(text));
    }

    [Fact]
    public void Extract_DecodesEntities_AndSkipsComments_AndCollapsesWhitespace()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<p>  docs &#183; <b>dotnet</b><!-- no -->\n /docs&nbsp;now </p>");

        Assert.Equal("docs · dotnet /docs now", HtmlText.Extract(doc.DocumentNode.SelectSingleNode("//p")));
    }

    [Fact]
    public void Extract_OfAList_ConcatenatesWithoutASeparator_AsSearXNG()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<p> one </p><p> two </p>");

        Assert.Equal("onetwo", HtmlText.Extract(doc.DocumentNode.SelectNodes("//p")));
    }

    [Fact]
    public void Extract_OfNothing_IsEmpty()
    {
        Assert.Equal("", HtmlText.Extract((HtmlNode?)null));
        Assert.Equal("", HtmlText.Extract((IEnumerable<HtmlNode>?)null));
    }

    [Theory]
    [InlineData("rust async", "rust+async")]
    [InlineData("a_b.c-d~e", "a_b.c-d~e")]
    [InlineData("c# & d=1", "c%23+%26+d%3D1")]
    [InlineData("ä", "%C3%A4")]
    public void QuotePlus_MatchesPython(string text, string expected)
    {
        Assert.Equal(expected, UrlEncoding.QuotePlus(text));
    }

    [Theory]
    [InlineData("a=1&u=two&u=three", "u", "two")]
    [InlineData("u=&u=second", "u", "second")] // parse_qs drops blank values.
    [InlineData("u=a%20b+c", "u", "a b c")]
    [InlineData("!&&u=x&ntb=1", "u", "x")]
    [InlineData("a=1", "u", null)]
    [InlineData("", "u", null)]
    public void FirstQueryValue_MatchesParseQs(string query, string name, string? expected)
    {
        Assert.Equal(expected, UrlEncoding.FirstQueryValue(query, name));
    }

    [Theory]
    [InlineData("<a b=c/>x</i>y", "x</i>y")]
    [InlineData("<a b=\"c\"/>x</i>y", "xy")]
    [InlineData("<script/>x</script>y", "xy")]
    [InlineData("a<!-->b-->c", "ac")]
    [InlineData("<![CDATA[a>b]]>c", "c")]
    [InlineData("<title>a&amp;b</title>", "a&b")]
    [InlineData("<xmp>a&amp;b</xmp>", "a&amp;b")]
    [InlineData("<xmp>a</xmpx>b", "a</xmpx>b")]
    [InlineData("<plaintext>a</plaintext>b", "a</plaintext>b")]
    [InlineData("<noscript>a<b>c</b></noscript>", "ac")]
    [InlineData("\u001Ca\u001Db\u001E", "a b")]
    [InlineData("<ſcript>x</script>", "<ſcript>x")]
    [InlineData("<script>x</ſcript>y", "")]
    [InlineData("<xmp>x</ſxmp>y", "x</ſxmp>y")]
    [InlineData("<a", "")]
    [InlineData("a</", "a</")]
    [InlineData("a<", "a<")]
    public void FromFragment_DecidesAsPythonsParserDoes(string input, string expected)
    {
        Assert.Equal(expected, HtmlText.FromFragment(input));
    }

    private static string Repeat(string unit) => string.Concat(Enumerable.Repeat(unit, 2_000_000 / unit.Length));

    private static readonly Dictionary<string, Func<string>> Hostile = new()
    {
        ["attributes"] = () => "<a " + Repeat("b ") + ">",
        ["attributes with values"] = () => "<a " + Repeat("b=c ") + ">",
        ["quoted values"] = () => "<a " + Repeat("b=\"c\" ") + ">",
        ["an unclosed tag"] = () => "<a " + Repeat("b "),
        ["an unclosed quote"] = () => "<a b=\"" + Repeat("x"),
        ["slashes"] = () => "<a" + Repeat("/") + ">",
        ["opening brackets"] = () => Repeat("<a "),
        ["tags"] = () => Repeat("<a>"),
        ["end tags"] = () => Repeat("</a>"),
        ["unclosed end tags"] = () => Repeat("</a "),
        ["comment openings"] = () => Repeat("<!--"),
        ["empty comments"] = () => Repeat("<!---->"),
        ["dashes"] = () => "<!--" + Repeat("-"),
        ["CDATA openings"] = () => Repeat("<![CDATA["),
        ["near ends of a raw element"] = () => "<xmp>" + Repeat("</xmpx "),
        ["raw elements"] = () => Repeat("<xmp></xmp>"),
        ["equals signs"] = () => "<a b" + Repeat("=") + ">",
        ["less-than signs"] = () => Repeat("<"),
        ["ampersands"] = () => Repeat("&"),
    };

    public static TheoryData<string> HostileNames() => new(Hostile.Keys);

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void FromFragment_StaysLinear_On2MBOf(string name)
    {
        string page = Hostile[name]();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        HtmlText.FromFragment(page);

        Assert.True(watch.ElapsedMilliseconds < 3000, watch.ElapsedMilliseconds + " ms");
    }
}
