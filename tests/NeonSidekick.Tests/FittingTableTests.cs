using NeonSidekick.UI;
using NeonSidekick.UI.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// Wide tables that fit (2026-11-01, feature idea #5): a reply's table too wide for its window shows each row as a card of aligned
/// <c>Header  value</c> lines, every other card on the code block's slab; one that fits, or can wrap readably, stays a grid.
/// </summary>
public class FittingTableTests : IDisposable
{
    private readonly TestConsole _console = new();

    public FittingTableTests() => _console.Profile.Width = 80;

    public void Dispose() => _console.Dispose();

    private List<SegmentLine> Render(string markdown, int width) => ScreenPane.RenderLines(MarkdownView.Of(markdown), _console, width);

    private static string Text(SegmentLine line) => string.Concat(line.Select(s => s.Text));

    private const string Wide =
        "| Server | Model | Notes | Started | Share |\n|---|---|---|---|---|\n" +
        "| llama.cpp | qwen3-32b-q4_k_m | Docker server, started with --enforce-eager and a 0.9 GPU memory share | yesterday | 0.9 |\n" +
        "| vLLM | gemma-3-27b | none | today | 0.8 |";

    [Fact]
    public void ANarrowTable_StaysAGrid()
    {
        var lines = Render("| a | b |\n|---|---|\n| 1 | 22 |", 80);

        Assert.StartsWith("╭", Text(lines[0]));
    }

    [Fact]
    public void AWideTable_IsCards_HeadersAligned_ALongValueWrappingUnderItsColumn()
    {
        var lines = Render(Wide, 60).Select(Text).ToList();

        Assert.DoesNotContain(lines, l => l.StartsWith('╭'));
        Assert.Equal("Server   llama.cpp", lines[0].TrimEnd());
        Assert.Equal("Model    qwen3-32b-q4_k_m", lines[1].TrimEnd());
        Assert.StartsWith("Notes    Docker server", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("         ", lines[3], StringComparison.Ordinal);   // the wrap, under the value column
        Assert.NotEqual(' ', lines[3][9]);
        Assert.Contains(lines, l => l.TrimEnd() == "Server   vLLM");
        Assert.All(lines, l => Assert.True(TextCells.Width(l) <= 60, l));
    }

    [Fact]
    public void EveryOtherCard_IsOnTheSlab_ToTheFullWidth()
    {
        var lines = Render(Wide, 60);
        int second = lines.FindIndex(l => Text(l).StartsWith("Server   vLLM", StringComparison.Ordinal));
        var slab = Theme.MarkdownCodeBlock.Background;

        Assert.All(lines.Take(second), l => Assert.All(l, s => Assert.NotEqual(slab, s.Style.Background)));
        Assert.All(lines.Skip(second), l =>
        {
            Assert.Equal(60, Segment.CellCount(l));
            Assert.All(l, s => Assert.Equal(slab, s.Style.Background));
        });
        var header = Assert.Single(lines[second], s => s.Text == "Server");
        Assert.Equal(Theme.TableHeader.Foreground, header.Style.Foreground);   // the header keeps its color on the slab
    }

    [Fact]
    public void ALongHeader_WrapsInItsThird()
    {
        var lines = Render("| A very long header name here | b | c |\n|---|---|---|\n| " + new string('x', 40) + " | " + new string('y', 40) + " | z |", 60).Select(Text).ToList();

        Assert.StartsWith("A very long header ", lines[0], StringComparison.Ordinal);
        Assert.Equal(' ', lines[0][21]);   // a third of 60 is 20, then the gap
        Assert.Equal('x', lines[0][22]);
        Assert.StartsWith("name here", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AShortRowsMissingCells_AndAnEmptyCell_AddNoLines()
    {
        var lines = Render("| Server | Model | Notes |\n|---|---|---|\n| " + new string('a', 40) + " |  | " + new string('n', 40) + " |\n| b |", 40).Select(Text).ToList();

        Assert.DoesNotContain(lines, l => l.StartsWith("Model", StringComparison.Ordinal));
        Assert.Equal(1, lines.Count(l => l.StartsWith("Notes", StringComparison.Ordinal)));
        Assert.Equal("Server  b", lines[^1].TrimEnd());
    }

    [Fact]
    public void OneColumn_OrNoBody_NeverStacks()
    {
        Assert.StartsWith("╭", Text(Render("| a |\n|---|\n| " + new string('x', 200) + " |", 40)[0]));
        Assert.StartsWith("╭", Text(Render("| " + string.Join(" | ", Enumerable.Range(0, 8).Select(i => "header" + i)) + " |\n|" + string.Concat(Enumerable.Repeat("---|", 8)), 40)[0]));
    }

    [Fact]
    public void TheRules_AtTheirEdges()
    {
        Assert.False(FittingTable.Stacks(new Measurement(10, 60), 3, 1, 60));   // fits
        Assert.False(FittingTable.Stacks(new Measurement(30, 61), 3, 1, 60));   // one cell too wide, the average column 17: a wrapping grid
        Assert.True(FittingTable.Stacks(new Measurement(61, 90), 3, 1, 60));    // a word a line won't fit
        Assert.False(FittingTable.Stacks(new Measurement(30, 90), 4, 1, 53));   // (53 - 13) / 4 = 10: still a grid
        Assert.True(FittingTable.Stacks(new Measurement(30, 90), 4, 1, 52));    // (52 - 13) / 4 = 9: cards
        Assert.False(FittingTable.Stacks(new Measurement(90, 90), 1, 1, 52));   // one column
        Assert.False(FittingTable.Stacks(new Measurement(90, 90), 4, 0, 52));   // no body
        Assert.Equal(10, FittingTable.MinColumnCells);
    }

    [Fact]
    public void ACardsLink_AndInlineCode_KeepWhatTheyCarry()
    {
        var lines = Render("| Name | Where | Code |\n|---|---|---|\n| " + new string('a', 30) + " | [docs](https://example.com/) | `run it` |", 40);

        Assert.Contains(lines, l => l.Any(s => s.Link?.Url == "https://example.com/"));
        Assert.Contains(lines, l => l.Any(s => s.Text == "run" && s.Style == Theme.MarkdownCode));   // a word a segment
    }

    [Fact]
    public void AWideTableInAQuote_StacksWithTheQuotesColorOnItsValues()
    {
        var lines = Render("> | Server | Notes |\n> |---|---|\n> | " + new string('a', 40) + " | " + new string('n', 40) + " |", 40);

        Assert.DoesNotContain(lines, l => Text(l).Contains('╭'));
        var value = lines.SelectMany(l => l).First(s => s.Text.Contains("aaaa", StringComparison.Ordinal));
        Assert.Equal(Theme.MarkdownQuote.Foreground, value.Style.Foreground);
    }
}
