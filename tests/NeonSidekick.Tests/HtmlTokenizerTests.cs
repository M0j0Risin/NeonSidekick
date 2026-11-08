using NeonSidekick.Web;

namespace NeonSidekick.Tests;

public class HtmlTokenizerTests
{
    private static List<HtmlToken> Tokens(string html) => HtmlTokenizer.Tokenize(html).ToList();

    [Fact]
    public void Tags_Text_AndEntities()
    {
        var tokens = Tokens("<p class=\"a b\">Fish &amp; chips</p>");

        Assert.Equal(3, tokens.Count);
        Assert.Equal(HtmlTokenKind.Open, tokens[0].Kind);
        Assert.Equal("p", tokens[0].Name);
        Assert.Equal("a b", tokens[0].Attribute("class"));
        Assert.False(tokens[0].SelfClosing);
        Assert.Equal(HtmlTokenKind.Text, tokens[1].Kind);
        Assert.Equal("Fish & chips", tokens[1].Text);
        Assert.Equal(HtmlTokenKind.Close, tokens[2].Kind);
        Assert.Equal("p", tokens[2].Name);
    }

    [Fact]
    public void Names_AreLowerCased_AndAttributes_TakeEveryForm()
    {
        var tokens = Tokens("<A HREF='x.html' data-Id=42 disabled title=\"a &quot;b&quot;\">t</A>");

        Assert.Equal("a", tokens[0].Name);
        Assert.Equal("x.html", tokens[0].Attribute("href"));
        Assert.Equal("42", tokens[0].Attribute("data-id"));
        Assert.Equal("", tokens[0].Attribute("disabled"));
        Assert.Equal("a \"b\"", tokens[0].Attribute("title"));
        Assert.Null(tokens[0].Attribute("nope"));
        Assert.Equal("a", tokens[2].Name);
    }

    [Fact]
    public void VoidAndSelfClosing_NeverExpectAClose()
    {
        var tokens = Tokens("a<br>b<img src=x alt=\"y\"/>c<hr />d");

        Assert.Equal(new[] { "", "br", "", "img", "", "hr", "" }, tokens.Select(t => t.Name));
        Assert.All(tokens.Where(t => t.Kind == HtmlTokenKind.Open), t => Assert.True(t.SelfClosing));
        Assert.Equal("y", tokens[3].Attribute("alt"));
        Assert.Equal("abcd", string.Concat(tokens.Where(t => t.Kind == HtmlTokenKind.Text).Select(t => t.Text)));
    }

    [Fact]
    public void Comments_Doctype_Cdata_AndInstructions_AreSkipped()
    {
        var tokens = Tokens("<!DOCTYPE html><?xml version=\"1.0\"?><!-- <p>not a tag</p> -->x<![CDATA[ <b>raw</b> ]]>y");

        Assert.Equal(2, tokens.Count);
        Assert.Equal("x", tokens[0].Text);
        Assert.Equal("y", tokens[1].Text);
    }

    [Fact]
    public void ScriptAndStyle_BodiesAreRaw_UntilTheirEndTag()
    {
        var tokens = Tokens("<script>if (a < b && c) { x = '</div>'; }</script><style>p > a { }</style>t");

        Assert.Equal(new[] { "script", "", "script", "style", "", "style", "" }, tokens.Select(t => t.Name));
        Assert.Equal("if (a < b && c) { x = '</div>'; }", tokens[1].Text);
        Assert.Equal("p > a { }", tokens[4].Text);
        Assert.Equal("t", tokens[6].Text);
    }

    [Fact]
    public void Title_IsRawText_WithEntitiesDecoded()
    {
        var tokens = Tokens("<title>Fish &amp; <b>chips</b></title>");

        Assert.Equal(3, tokens.Count);
        Assert.Equal("Fish & <b>chips</b>", tokens[1].Text);
    }

    [Fact]
    public void AStrayLessThan_IsText()
    {
        var tokens = Tokens("1 < 2 and 3 <4 but <b>x</b>");

        Assert.Equal("1 < 2 and 3 <4 but ", tokens[0].Text);
        Assert.Equal("b", tokens[1].Name);
    }

    [Fact]
    public void UnterminatedTag_RunsToTheEnd_WithoutThrowing()
    {
        var tokens = Tokens("a<div class=\"x");

        Assert.Equal("a", tokens[0].Text);
        Assert.Equal("div", tokens[1].Name);
        Assert.Equal(2, tokens.Count);
    }

    [Fact]
    public void RawTextElement_WithoutAnEndTag_RunsToTheEnd()
    {
        var tokens = Tokens("<script>var x = 1;");

        Assert.Equal(2, tokens.Count);
        Assert.Equal("var x = 1;", tokens[1].Text);
    }

    [Fact]
    public void Empty_YieldsNothing()
    {
        Assert.Empty(Tokens(""));
    }
}
