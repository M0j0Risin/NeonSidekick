using NeonSidekick.UI;
using NeonSidekick.UI.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class MarkdownViewTests : IDisposable
{
    private readonly TestConsole _console = new();

    public MarkdownViewTests()
    {
        _console.Profile.Width = 80;
    }

    public void Dispose() => _console.Dispose();

    private List<SegmentLine> Render(IRenderable renderable, int width = 80) => ScreenPane.RenderLines(renderable, _console, width);

    private string[] Lines(string markdown, int width = 80) =>
        Render(MarkdownView.Of(markdown), width).Select(Text).ToArray();

    private static string Text(SegmentLine line) => string.Concat(line.Select(s => s.Text));

    [Fact]
    public void Paragraph_WrapsAtTheWidth()
    {
        var lines = Lines("one two three four five six seven eight nine ten", width: 20);

        Assert.Equal(new[] { "one two three four ", "five six seven eight", "nine ten" }, lines);
    }

    [Fact]
    public void Bold_IsConsumed_AndStyledBold()
    {
        var line = Assert.Single(Render(MarkdownView.Of("Some **bold** here")));

        Assert.Equal("Some bold here", Text(line));
        var bold = Assert.Single(line, s => s.Text == "bold");
        Assert.Equal(Theme.MarkdownBold, bold.Style);
        Assert.True(bold.Style.Decoration.HasFlag(Decoration.Bold));
        var plain = Assert.Single(line, s => s.Text == "here");
        Assert.Equal(Theme.Assistant, plain.Style);
    }

    [Fact]
    public void Italic_AndInlineCode_HaveTheirStyles()
    {
        var line = Assert.Single(Render(MarkdownView.Of("*italic* and `code`")));

        Assert.Equal("italic and code", Text(line));
        Assert.Equal(Theme.MarkdownItalic, Assert.Single(line, s => s.Text == "italic").Style);
        Assert.Equal(Theme.MarkdownCode, Assert.Single(line, s => s.Text == "code").Style);
    }

    [Fact]
    public void BoldItalic_CarriesBothDecorations()
    {
        var line = Assert.Single(Render(MarkdownView.Of("***x***")));
        var style = Assert.Single(line, s => s.Text == "x").Style;
        Assert.True(style.Decoration.HasFlag(Decoration.Bold));
        Assert.True(style.Decoration.HasFlag(Decoration.Italic));
    }

    [Fact]
    public void RunStyle_IsPinned()
    {
        Assert.Equal(Theme.Assistant, MarkdownView.RunStyle(new TextRun("x")));
        Assert.Equal(Theme.MarkdownBold, MarkdownView.RunStyle(new TextRun("x", Bold: true)));
        Assert.Equal(Theme.MarkdownItalic, MarkdownView.RunStyle(new TextRun("x", Italic: true)));
        Assert.Equal(Theme.MarkdownCode, MarkdownView.RunStyle(new TextRun("x", Bold: true, Code: true)));
        Assert.Equal(Theme.MarkdownQuote, MarkdownView.RunStyle(new TextRun("x"), Theme.MarkdownQuote));
        Assert.True(MarkdownView.RunStyle(new TextRun("x", Bold: true), Theme.MarkdownQuote).Decoration.HasFlag(Decoration.Bold));
    }

    [Fact]
    public void Paragraphs_HaveABlankRowBetweenThem()
    {
        Assert.Equal(new[] { "one", " ", "two" }, Lines("one\n\ntwo"));
    }

    [Fact]
    public void HardBreak_BreaksTheLine_ASoftBreakDoesNot()
    {
        Assert.Equal(new[] { "one", "two three" }, Lines("one  \ntwo\nthree"));
    }

    [Fact]
    public void BulletList_UsesTheGlyph_AndNestsUnderTheMarker()
    {
        var lines = Lines("- one\n- two\n  - nested\n- three");

        Assert.Equal(new[] { "• one", "• two", "  • nested", "• three" }, lines);
        var first = Render(MarkdownView.Of("- one"))[0];
        Assert.Equal(Theme.MarkdownBullet, first[0].Style);
        Assert.Equal(MarkdownView.BulletGlyph, first[0].Text);
    }

    [Fact]
    public void ListItem_WrapsUnderItsOwnText()
    {
        var lines = Lines("- one two three four five six", width: 15);

        Assert.Equal(new[] { "• one two three", "  four five six" }, lines.Select(l => l.TrimEnd()));
    }

    [Fact]
    public void OrderedList_NumbersFromItsStart()
    {
        Assert.Equal(new[] { "7. seven", "8. eight" }, Lines("7. seven\n8. eight"));
    }

    [Fact]
    public void Fence_HasTheLanguageLabel_AndIndentedCode_BlankLinesKept()
    {
        var lines = Render(MarkdownView.Of("```csharp\nvar x = 1;\n\n\ty();\n```"));

        Assert.Equal(new[] { "📜 csharp", "  var x = 1;", "   ", "      y();" }, lines.Select(Text));   // the scroll ahead of the label since 2026-09-22
        Assert.Equal(Theme.MarkdownCodeLabel, lines[0][0].Style);
        Assert.All(lines.Skip(1).SelectMany(l => l).Where(s => s.Text.Length > 0), s => Assert.Equal(Theme.PanelBg, s.Style.Background));
    }

    [Fact]
    public void Fence_WithAKnownLanguage_IsHighlighted()
    {
        var line = Render(MarkdownView.Of("```csharp\nvar x = 1;\n```"))[1];

        Assert.Equal(Theme.CodeKeyword, line.Single(s => s.Text == "var").Style);
        Assert.Equal(Theme.CodeNumber, line.Single(s => s.Text == "1").Style);
        Assert.Equal(Theme.MarkdownCodeBlock, line.Single(s => s.Text == "x").Style);
    }

    [Fact]
    public void Fence_WithAnUnknownLanguage_StaysPlain()
    {
        var lines = Render(MarkdownView.Of("```text\nvar x = 1;\n```"));

        Assert.Equal("📜 text", Text(lines[0]));
        Assert.All(lines[1].Where(s => s.Text.Length > 0), s => Assert.Equal(Theme.MarkdownCodeBlock, s.Style));
    }

    [Fact]
    public void Fence_BlockCommentSpanningLines_ColoursEveryLine()
    {
        var lines = Render(MarkdownView.Of("```c\n/* one\ntwo */ x\n```"));

        Assert.Equal(new[] { "📜 c", "  /* one", "  two */ x" }, lines.Select(Text));
        Assert.Equal(Theme.CodeComment, lines[1].Single(s => s.Text.Contains("one", StringComparison.Ordinal)).Style);
        Assert.Equal(Theme.CodeComment, lines[2].Single(s => s.Text.Contains("two", StringComparison.Ordinal)).Style);
    }

    [Fact]
    public void Fence_HighlightedLongLine_WrapsInsideTheIndent()
    {
        var lines = Render(MarkdownView.Of("```js\nconst alpha = beta + gamma + delta;\n```"), width: 20).Select(Text).ToArray();

        Assert.True(lines.Length > 2);
        Assert.All(lines.Skip(1), l => Assert.StartsWith(MarkdownView.CodeIndent, l, StringComparison.Ordinal));
        Assert.Equal("const alpha = beta + gamma + delta;", string.Join(" ", lines.Skip(1).Select(l => l.Trim())));
    }

    [Fact]
    public void Fence_WithoutALanguage_IsLabelledCode()
    {
        Assert.Equal(new[] { MarkdownView.CodeGlyph + MarkdownView.CodeLabel, "  x" }, Lines("```\nx\n```"));
    }

    [Fact]
    public void UnclosedFence_RunsToTheEnd()
    {
        Assert.Equal(new[] { "📜 python", "  def f():", "      pass" }, Lines("```python\ndef f():\n    pass"));
    }

    [Fact]
    public void UnclosedBold_IsLiteral()
    {
        Assert.Equal(new[] { "**not closed" }, Lines("**not closed"));
    }

    [Fact]
    public void Blockquote_HasTheGutterOnEveryLine()
    {
        var lines = Render(MarkdownView.Of("> a quote\n>\n> more"));

        Assert.Equal(new[] { "▎ a quote", "▎  ", "▎ more" }, lines.Select(Text));
        Assert.Equal(Theme.MarkdownQuoteBar, lines[0][0].Style);
        Assert.All(lines[0].Skip(1).Where(s => s.Text.Length > 0), s => Assert.Equal(Theme.MarkdownQuote, s.Style));
    }

    [Fact]
    public void Headings_HaveTheirStyles()
    {
        var lines = Render(MarkdownView.Of("# One\n\n## Two **bold**"));

        Assert.Equal(new[] { "One", " ", "Two bold" }, lines.Select(Text));
        Assert.Equal(Theme.MarkdownHeading1, lines[0][0].Style);
        Assert.All(lines[2].Where(s => s.Text.Length > 0), s => Assert.Equal(Theme.MarkdownHeading, s.Style));
    }

    [Fact]
    public void Rule_SpansTheWidth()
    {
        var line = Assert.Single(Render(MarkdownView.Of("---"), width: 30));
        Assert.Equal(30, TextCells.Width(Text(line)));
        Assert.Equal(Theme.MarkdownRule, line[0].Style);
    }

    [Fact]
    public void Table_IsATable_FittedToItsContent()
    {
        var lines = Lines("| a | b |\n|---|--:|\n| 1 | 22 |");

        Assert.Equal(5, lines.Length);
        Assert.StartsWith("╭", lines[0]);
        Assert.Contains("a", lines[1]);
        Assert.Contains("22", lines[3]);
        Assert.True(TextCells.Width(lines[0]) < 20, lines[0]);
    }

    [Fact]
    public void Link_ShowsTheText_ThenTheUrlDim()
    {
        var line = Assert.Single(Render(MarkdownView.Of("see [docs](https://example.com)")));

        Assert.Equal("see docs (https://example.com)", Text(line));
        Assert.Equal(Theme.MarkdownLinkUrl, Assert.Single(line, s => s.Text.Contains("example.com", StringComparison.Ordinal)).Style);
    }

    [Fact]
    public void Autolink_ShowsTheUrlOnce()
    {
        Assert.Equal(new[] { "https://example.com" }, Lines("<https://example.com>"));
    }

    [Fact]
    public void Markup_IsLiteral()
    {
        Assert.Equal(new[] { "use [bold]never[/] here" }, Lines("use [bold]never[/] here"));
    }

    [Fact]
    public void Empty_RendersNothing()
    {
        Assert.Empty(Render(MarkdownView.Of("")));
    }

    [Fact]
    public void ReplyBlock_GlyphOnTheFirstLine_TheIndentUnderIt()
    {
        var lines = Render(new ReplyBlock("Hello\n\n- one", glyph: true));

        Assert.Equal(new[] { "● Hello", "   ", "  • one" }, lines.Select(Text));
        Assert.Equal(Theme.Accent, lines[0][0].Style);
        Assert.Equal(TranscriptRenderer.AssistantGlyph, lines[0][0].Text);
        Assert.Equal(ReplyBlock.ContinuationIndent, lines[2][0].Text);
    }

    [Fact]
    public void ReplyBlock_WithoutTheGlyph_IsFlushLeft()
    {
        Assert.Equal(new[] { "Hello" }, Render(new ReplyBlock("Hello", glyph: false)).Select(Text));
    }

    [Fact]
    public void ReplyBlock_Empty_IsTheBareGlyph()
    {
        var line = Assert.Single(Render(new ReplyBlock("", glyph: true)));
        Assert.Equal(TranscriptRenderer.AssistantGlyph, Text(line));
        Assert.Empty(Render(new ReplyBlock("", glyph: false)));
    }

    [Fact]
    public void ReplyBlock_WrapsInsideTheIndent()
    {
        var lines = Render(new ReplyBlock("one two three four", glyph: true), width: 12).Select(Text).ToArray();

        Assert.Equal(new[] { "● one two ", "  three four" }, lines);
    }

    // ── Code spans (2026-09-22, the code fold) ──────────────────────────────

    private const string TwoBlocks = "Intro text here.\n\n```csharp\nint a = 1;\nint b = 2;\n```\n\nBetween.\n\n```\nplain one\nplain two that is long enough to wrap at twenty\nplain three\n```\n\n- item\n\n  ```js\n  let x;\n  ```\n\nEnd.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CodeSpans_LineUp_WithTheRenderedRows_TopLevelBlocksOnly(bool glyph)
    {
        const int width = 30;
        var block = new ReplyBlock(TwoBlocks, glyph, codeKeep: 1);
        var rows = Render(block, width).Select(Text).ToArray();
        var spans = block.CodeSpans(RenderOptions.Create(_console, _console.Profile.Capabilities), width);

        Assert.Equal(2, spans.Count);   // the block in the list item is not one
        var cs = spans[0];
        Assert.Equal(("csharp", 1, 2, 2), (cs.Label, cs.LabelRows, cs.BodyRows, cs.SourceLines));
        Assert.EndsWith("csharp", rows[cs.LabelRow]);
        Assert.Contains("int a = 1;", rows[cs.LabelRow + 1]);
        Assert.Contains("int b = 2;", rows[cs.End - 1]);
        Assert.Equal("", rows[cs.End].Trim());   // the spacer

        var plain = spans[1];
        Assert.Equal((MarkdownView.CodeLabel, 3), (plain.Label, plain.SourceLines));
        Assert.True(plain.BodyRows > 3);          // the long line wrapped
        Assert.EndsWith(MarkdownView.CodeLabel, rows[plain.LabelRow]);
        Assert.Contains("plain one", rows[plain.LabelRow + 1]);
        Assert.Contains("plain three", rows[plain.End - 1]);
        Assert.Equal(1, block.CodeKeep);
    }

    [Fact]
    public void CodeFoldText_Summary_ReadsAsTheLabel_ItsLinesAndTheTriangle()
    {
        Assert.Equal("▸ 📜 csharp · 57 lines", CodeFoldText.Summary("csharp", 57, expanded: false));
        Assert.Equal("▾ 📜 code · 1 line", CodeFoldText.Summary("code", 1, expanded: true));
        Assert.Equal(3, TextCells.Width(MarkdownView.CodeGlyph));
        Assert.Equal("📜 csharp", MarkdownView.CodeHeading("csharp"));
        Assert.Equal("📜 code", MarkdownView.CodeHeading(null));
    }
}
