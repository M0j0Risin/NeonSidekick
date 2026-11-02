using NeonSidekick.UI;
using NeonSidekick.UI.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// Wide tables that fit (2026-11-01, feature idea #5): a reply's table too wide for its window shows each row as a card of aligned
/// <c>Header  value</c> lines, every other card on the code block's slab; one that fits, or can wrap readably, stays a grid. Since 2026-11-02
/// (the review): each header laid out once, the stripes by cards shown, a segment's own background kept, a rule between cards with no color,
/// no cards under four cells, and the grid measured once a width.
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
        Assert.False(FittingTable.Stacks(new Measurement(90, 90), 4, 1, FittingTable.HeaderGap + 1));   // no room for a header cell and a value cell
        Assert.True(FittingTable.Stacks(new Measurement(90, 90), 4, 1, FittingTable.HeaderGap + 2));
        Assert.Equal(10, FittingTable.MinColumnCells);
    }

    /// <summary>A renderable that counts its measures and renders.</summary>
    private sealed class Counting(string text) : IRenderable
    {
        public int Measures { get; private set; }

        public int Renders { get; private set; }

        public Measurement Measure(RenderOptions options, int maxWidth)
        {
            Measures++;
            return ((IRenderable)new Text(text)).Measure(options, maxWidth);
        }

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
        {
            Renders++;
            return ((IRenderable)new Text(text)).Render(options, maxWidth);
        }
    }

    private static FittingTable Cards(IRenderable[] headers, params IRenderable?[][] rows)
    {
        var grid = new Table();
        foreach (var header in headers)
        {
            grid.AddColumn(new TableColumn(header));
        }

        foreach (var row in rows)
        {
            grid.AddRow(row.Select(c => c ?? new Text("")).ToArray());
        }

        return new FittingTable(grid, headers, rows);
    }

    [Fact]
    public void EachHeader_IsLaidOutOnce_ARender_AndTheGridMeasuredOnceAWidth()
    {
        var name = new Counting("Name");
        var notes = new Counting("Notes");
        var table = Cards([name, notes], [new Text(new string('a', 30)), new Text(new string('n', 60))], [new Text("b"), new Text(new string('m', 60))]);
        var options = RenderOptions.Create(_console, _console.Profile.Capabilities);

        int measured = name.Measures;
        table.Measure(options, 40);
        int once = name.Measures;
        table.Measure(options, 40);
        Assert.True(once > measured);
        Assert.Equal(once, name.Measures);   // the same width: the answer kept

        _ = table.Render(options, 40).ToList();
        Assert.Equal(1, name.Renders);   // two cards, one layout of the header
        Assert.Equal(1, notes.Renders);
    }

    [Fact]
    public void ARowOfEmptyCells_IsNoCard_AndTheStripesStillAlternate()
    {
        string blank = "|  |  |  |\n";
        var lines = Render("| Server | Model | Notes |\n|---|---|---|\n| one | " + new string('a', 30) + " | x |\n" + blank + "| two | " + new string('b', 30) + " | y |\n| three | c | z |", 40);
        var slab = Theme.MarkdownCodeBlock.Background;
        int two = lines.FindIndex(l => Text(l).StartsWith("Server  two", StringComparison.Ordinal));
        int three = lines.FindIndex(l => Text(l).StartsWith("Server  three", StringComparison.Ordinal));

        Assert.True(two > 0 && three > two);
        Assert.All(lines[two..three], l => Assert.All(l, s => Assert.Equal(slab, s.Style.Background)));   // the second card shown
        Assert.All(lines[three..], l => Assert.All(l, s => Assert.NotEqual(slab, s.Style.Background)));
    }

    [Fact]
    public void AStripedCard_KeepsASegmentsOwnBackground()
    {
        var own = new Style(Color.White, Color.Red);
        var table = Cards([new Text("Name"), new Text("Notes")],
            [new Text(new string('a', 30)), new Text(new string('n', 60))],
            [new Text("b"), new Markup("[white on red]own[/] " + new string('m', 60))]);
        var lines = Segment.SplitLines(table.Render(RenderOptions.Create(_console, _console.Profile.Capabilities), 40));

        var segment = lines.SelectMany(l => l).Single(s => s.Text == "own");
        Assert.Equal(own.Background, segment.Style.Background);
        Assert.Contains(lines.SelectMany(l => l), s => s.Style.Background == Theme.MarkdownCodeBlock.Background);   // the rest on the slab
    }

    [Fact]
    public void WithNoColor_ADimRule_StandsBetweenTheCards_AndNoSlab()
    {
        _console.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        var lines = Render(Wide, 60);
        var texts = lines.Select(Text).ToList();
        int second = texts.FindIndex(l => l.StartsWith("Server   vLLM", StringComparison.Ordinal));

        Assert.Equal(new string('─', 60), texts[second - 1]);
        Assert.Single(texts, l => l.StartsWith('─'));   // between the two, none before the first
        Assert.All(lines, l => Assert.All(l, s => Assert.NotEqual(Theme.MarkdownCodeBlock.Background, s.Style.Background)));
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
