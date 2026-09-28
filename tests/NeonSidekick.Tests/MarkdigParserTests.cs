using NeonSidekick.UI.Markdown;

namespace NeonSidekick.Tests;

public class MarkdigParserTests
{
    private static readonly MarkdigParser Parser = new();

    private static MarkdownDocument Parse(string text) => Parser.Parse(text);

    [Fact]
    public void Empty_IsTheEmptyDocument()
    {
        Assert.Same(MarkdownDocument.Empty, Parse(""));
        Assert.Empty(Parse("   \n\n").Blocks);
    }

    [Fact]
    public void Prose_IsOneParagraph_WithItsEmphasisAsRuns()
    {
        var doc = Parse("Some **bold** and *italic* and `code` here.");

        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(doc.Blocks));
        Assert.Equal(
            new MarkdownInline[]
            {
                new TextRun("Some "),
                new TextRun("bold", Bold: true),
                new TextRun(" and "),
                new TextRun("italic", Italic: true),
                new TextRun(" and "),
                new TextRun("code", Code: true),
                new TextRun(" here."),
            },
            paragraph.Inlines);
    }

    [Fact]
    public void BoldItalic_CarriesBothFlags()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(Parse("***both***").Blocks));
        Assert.Equal(new TextRun("both", Bold: true, Italic: true), Assert.Single(paragraph.Inlines));
    }

    [Fact]
    public void AdjacentRuns_WithTheSameEmphasis_AreFolded()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(Parse("a &amp; b\nc").Blocks));
        Assert.Equal(new TextRun("a & b c"), Assert.Single(paragraph.Inlines));
    }

    [Fact]
    public void UnclosedBold_IsLiteral()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(Parse("**not closed").Blocks));
        Assert.Equal(new TextRun("**not closed"), Assert.Single(paragraph.Inlines));
    }

    [Fact]
    public void Link_IsARun_WithTextAndUrl()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(Parse("see [the *docs*](https://example.com/x) now").Blocks));
        Assert.Equal(
            new MarkdownInline[] { new TextRun("see "), new LinkRun("the docs", "https://example.com/x"), new TextRun(" now") },
            paragraph.Inlines);
    }

    [Fact]
    public void Autolink_IsALink_OnItsOwnUrl()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(Parse("<https://example.com>").Blocks));
        Assert.Equal(new LinkRun("https://example.com", "https://example.com"), Assert.Single(paragraph.Inlines));
    }

    [Fact]
    public void HardBreak_IsItsOwnInline_ASoftBreakASpace()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(Parse("one  \ntwo\nthree").Blocks));
        Assert.Equal(new MarkdownInline[] { new TextRun("one"), new HardBreak(), new TextRun("two three") }, paragraph.Inlines);
    }

    [Fact]
    public void Headings_CarryTheirLevel()
    {
        var doc = Parse("# One\n\n### Three");
        Assert.Equal(2, doc.Blocks.Count);
        Assert.Equal(1, Assert.IsType<HeadingBlock>(doc.Blocks[0]).Level);
        Assert.Equal(3, Assert.IsType<HeadingBlock>(doc.Blocks[1]).Level);
        Assert.Equal(new TextRun("Three"), Assert.Single(Assert.IsType<HeadingBlock>(doc.Blocks[1]).Inlines));
    }

    [Fact]
    public void SetextHeading_IsAHeading_ARuleAfterABlankLineARule()
    {
        var heading = Assert.IsType<HeadingBlock>(Assert.Single(Parse("Title\n---").Blocks));
        Assert.Equal(2, heading.Level);

        var doc = Parse("Title\n\n---");
        Assert.Equal(2, doc.Blocks.Count);
        Assert.IsType<ParagraphBlock>(doc.Blocks[0]);
        Assert.IsType<RuleBlock>(doc.Blocks[1]);
    }

    [Fact]
    public void Fence_KeepsItsLanguage_AndItsLinesVerbatim()
    {
        var code = Assert.IsType<CodeBlock>(Assert.Single(Parse("```csharp title\nvar x = **1**;\n\n  y();\n```").Blocks));
        Assert.Equal("csharp", code.Language);
        Assert.Equal(new[] { "var x = **1**;", "", "  y();" }, code.Lines);
    }

    [Fact]
    public void Fence_WithoutALanguage_HasNone()
    {
        var code = Assert.IsType<CodeBlock>(Assert.Single(Parse("```\nx\n```").Blocks));
        Assert.Null(code.Language);
    }

    [Fact]
    public void UnclosedFence_RunsToTheEnd()
    {
        var code = Assert.IsType<CodeBlock>(Assert.Single(Parse("```python\ndef f():\n    pass").Blocks));
        Assert.Equal("python", code.Language);
        Assert.Equal(new[] { "def f():", "    pass" }, code.Lines);
        Assert.True(code.Open);
    }

    [Fact]
    public void ClosedFence_AndIndentedBlock_AreNotOpen()
    {
        Assert.False(Assert.IsType<CodeBlock>(Assert.Single(Parse("```python\nx\n```").Blocks)).Open);
        Assert.False(Assert.IsType<CodeBlock>(Assert.Single(Parse("```python\nx\n```\n").Blocks)).Open);
        Assert.False(Assert.IsType<CodeBlock>(Assert.Single(Parse("    x = 1").Blocks)).Open);
    }

    [Fact]
    public void Lists_Nest_AndNumberFromTheirStart()
    {
        var doc = Parse("- one\n- two\n  - nested\n\n7. seven\n8. eight");

        Assert.Equal(2, doc.Blocks.Count);
        var bullets = Assert.IsType<ListBlock>(doc.Blocks[0]);
        Assert.False(bullets.Ordered);
        Assert.Equal(2, bullets.Items.Count);
        Assert.Equal(new TextRun("one"), Assert.Single(Assert.IsType<ParagraphBlock>(Assert.Single(bullets.Items[0].Blocks)).Inlines));
        Assert.Equal(2, bullets.Items[1].Blocks.Count);
        var nested = Assert.IsType<ListBlock>(bullets.Items[1].Blocks[1]);
        Assert.Equal(new TextRun("nested"), Assert.Single(Assert.IsType<ParagraphBlock>(Assert.Single(Assert.Single(nested.Items).Blocks)).Inlines));

        var numbers = Assert.IsType<ListBlock>(doc.Blocks[1]);
        Assert.True(numbers.Ordered);
        Assert.Equal(7, numbers.Start);
        Assert.Equal(2, numbers.Items.Count);
    }

    [Fact]
    public void Quote_HoldsItsBlocks()
    {
        var quote = Assert.IsType<QuoteBlock>(Assert.Single(Parse("> a quote\n> continues").Blocks));
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(quote.Blocks));
        Assert.Equal(new TextRun("a quote continues"), Assert.Single(paragraph.Inlines));
    }

    [Fact]
    public void PipeTable_HasAHeader_Rows_AndAlignments()
    {
        var table = Assert.IsType<TableBlock>(Assert.Single(Parse("| a | b |\n|:--|--:|\n| 1 | **2** |\n| 3 |").Blocks));
        Assert.Equal(2, table.Header.Cells.Count);
        Assert.Equal(new TextRun("a"), Assert.Single(table.Header.Cells[0].Inlines));
        Assert.Equal(new[] { TableAlignment.Left, TableAlignment.Right }, table.Alignments);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(new TextRun("2", Bold: true), Assert.Single(table.Rows[0].Cells[1].Inlines));
    }

    [Fact]
    public void RawHtml_IsText()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(Parse("<b>x</b>").Blocks));
        Assert.Equal(new TextRun("<b>x</b>"), Assert.Single(paragraph.Inlines));
    }

    [Fact]
    public void Markup_IsText()
    {
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(Parse("use [bold]never[/] here").Blocks));
        Assert.Equal(new TextRun("use [bold]never[/] here"), Assert.Single(paragraph.Inlines));
    }
}
