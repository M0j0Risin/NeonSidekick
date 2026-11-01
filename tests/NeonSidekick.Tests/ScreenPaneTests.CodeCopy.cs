using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.UI.Markdown;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// Copy a code block (2026-11-01, plan memoized-dancing-horizon item #3): a closed block's label ends with ⧉, folded or not, and with
/// <i>Code collapse count</i> 0; a click on that one cell copies the block's source as written (a wrapped line still one line), the rest
/// of the label still folds; a block still streaming shows none.
/// </summary>
public partial class ScreenPaneTests
{
    /// <summary>The cells on buffer row <paramref name="y"/> where <see cref="ScreenPane.TryHitCodeCopy"/> answers, with what each copies.</summary>
    private static List<(int X, string Text)> CopyHits(ScreenPane pane, int y)
    {
        var hits = new List<(int, string)>();
        for (int x = 0; x < 60; x++)
        {
            if (pane.TryHitCodeCopy(x, y, out string text))
            {
                hits.Add((x, text));
            }
        }

        return hits;
    }

    [Fact]
    public void AClosedBlock_Folded_EndsWithTheCopyGlyph_ItsCellAloneCopies_TheRestOfTheLabelFolds()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("a\n"));   // row 93
        pane.SetLive(new ReplyBlock("```text\n" + CodeLines(1, 4) + "\n```", glyph: false, codeKeep: 3));
        pane.CommitLive();               // the folded label on row 94

        Assert.Contains(CodeFoldText.Summary("text", 4, expanded: false) + CodeFoldText.CopyTail, Output);
        var hit = Assert.Single(CopyHits(pane, 94));
        Assert.Equal(CodeLines(1, 4), hit.Text);
        Assert.Empty(CopyHits(pane, 93));
        Assert.True(pane.TryToggleToolGroupAt(3, 94));   // the label away from the glyph: unfolds

        // Unfolded, the label still carries it and still copies the whole block.
        Assert.Contains(pane.TranscriptRows(), row => string.Concat(row.Select(s => s.Text)).EndsWith(CodeFoldText.Summary("text", 4, expanded: true) + CodeFoldText.CopyTail, StringComparison.Ordinal));
        Assert.Equal(CodeLines(1, 4), Assert.Single(Enumerable.Range(88, 12).SelectMany(y => CopyHits(pane, y))).Text);   // wherever the label sits now
    }

    [Fact]
    public void AClosedBlock_WithACollapseCountOf0_EndsItsLabelWithTheGlyph_AndCopies()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("a\n"));
        pane.SetLive(new ReplyBlock("```text\n" + CodeLines(1, 3) + "\n```", glyph: false, codeKeep: 0));
        pane.CommitLive();   // the label on row 94, c1–c3 on 95–97

        Assert.Contains(MarkdownView.CodeHeading("text") + CodeFoldText.CopyTail + "\n", Output);
        Assert.Equal(CodeLines(1, 3), Assert.Single(CopyHits(pane, 94)).Text);
        Assert.Empty(CopyHits(pane, 95));
    }

    [Fact]
    public void ABlockStillStreaming_ShowsNoGlyph_AndNothingCopies()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        pane.SetLive(new ReplyBlock("```text\n" + CodeLines(1, 12), glyph: false, codeKeep: 3));
        _time.Advance(ScreenPane.Tick);

        Assert.DoesNotContain(CodeFoldText.CopyGlyph, Output);
        for (int y = 90; y < 100; y++)
        {
            Assert.Empty(CopyHits(pane, y));
        }
    }

    [Fact]
    public void AWrappedCodeLine_IsCopiedAsTheOneLineItWas()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        string longLine = "x = " + new string('y', 60);   // wider than the 40-cell window
        pane.Write(new Markup("a\n"));
        pane.SetLive(new ReplyBlock("```text\n" + longLine + "\nshort\n```", glyph: false, codeKeep: 0));
        pane.CommitLive();

        Assert.Equal(longLine + "\nshort", Assert.Single(CopyHits(pane, 94)).Text);
    }

    [Fact]
    public void AfterAResize_TheGlyphIsFoundWhereTheRowIsDrawnNow()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("a\n"));
        pane.SetLive(new ReplyBlock("```text\n" + CodeLines(1, 12) + "\n```", glyph: false, codeKeep: 3));
        pane.CommitLive();
        int before = Assert.Single(CopyHits(pane, 94)).X;

        _console.Profile.Width = 60;
        _time.Advance(ScreenPane.Tick);

        var hits = Enumerable.Range(88, 12).SelectMany(y => CopyHits(pane, y)).ToList();
        var hit = Assert.Single(hits);
        Assert.Equal(before, hit.X);   // the summary keeps its cells; only the window grew
        Assert.Equal(CodeLines(1, 12), hit.Text);
    }

    [Fact]
    public void AReplyWithNoPane_HasNoGlyph()
    {
        using var console = new TestConsole();
        var renderer = new TranscriptRenderer(console);
        renderer.BeginAssistant(markdown: true);
        renderer.AppendDelta("```text\nc1\n```");
        renderer.EndAssistant();

        Assert.DoesNotContain(CodeFoldText.CopyGlyph, console.Output);
    }
}
