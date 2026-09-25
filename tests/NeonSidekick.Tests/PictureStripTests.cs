using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>The picture strip over the pane's upper rule (later still on 2026-09-24): the model, its window and drawing, and the pane around it.</summary>
public class PictureStripTests : IDisposable
{
    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly ManualTimeProvider _time = new();

    public PictureStripTests()
    {
        _console.Profile.Width = 40;
        _console.Profile.Height = 20;
    }

    public void Dispose() => _console.Dispose();

    private static ImageThumbnail Tile(int width, int height) => new(width, height, Enumerable.Repeat(Color.Red, width * height).ToArray());

    private static string Blocks(int cells) => new('▀', cells);

    private List<string> Render(PictureStrip strip, int cells, bool highlight, out List<PictureSpan> spans)
    {
        var (lines, drawn) = strip.Render(RenderOptions.Create(_console, _console.Profile.Capabilities), cells, highlight);
        spans = drawn;
        return lines.Select(line => string.Concat(line.Select(segment => segment.Text))).ToList();
    }

    private static PictureStrip StripOf(int count, int width = 12, int height = 12)
    {
        var strip = new PictureStrip();
        for (int id = 0; id < count; id++)
        {
            strip.Add(Tile(width, height), id);
        }

        return strip;
    }

    // ── The model ───────────────────────────────────────────────────────────

    [Fact]
    public void Add_PutsTheNewestFirst_AndLetsGoOfTheHighlight()
    {
        var strip = StripOf(3);   // ids 0, 1, 2 added in order: 2 is the newest
        Assert.Equal(3, strip.Count);
        Assert.Null(strip.SelectedId);

        Assert.True(strip.Step(+1));
        Assert.Equal(2, strip.SelectedId);
        Assert.True(strip.Step(+1));
        Assert.Equal(1, strip.SelectedId);

        int version = strip.Version;
        strip.Add(Tile(12, 12), 7);
        Assert.Equal(-1, strip.Selected);
        Assert.True(strip.Version > version);
        Assert.True(strip.Step(-1));
        Assert.Equal(7, strip.SelectedId);
    }

    [Fact]
    public void Step_TakesTheNewestFirst_WalksOlderAndNewer_AndLeftOffTheNewestLetsGo()
    {
        Assert.False(new PictureStrip().Step(+1));   // nothing to walk: the key is the line's

        var strip = StripOf(3);
        var seen = new List<int>();
        foreach (int step in new[] { -1, +1, +1, +1, -1, -1, -1, +1 })
        {
            Assert.True(strip.Step(step));
            seen.Add(strip.Selected);
        }

        Assert.Equal([0, 1, 2, 2, 1, 0, -1, 0], seen);
    }

    [Fact]
    public void Add_PastTheCap_DropsTheOldest()
    {
        var strip = StripOf(PictureStrip.MaxPictures + 1);
        Assert.Equal(PictureStrip.MaxPictures, strip.Count);
        for (int i = 0; i < PictureStrip.MaxPictures; i++)
        {
            strip.Step(+1);
        }

        Assert.Equal(1, strip.SelectedId);   // id 0, the first added, is gone
    }

    [Fact]
    public void Clear_EmptiesIt_AndCountsAsAChangeOnlyWhenThereWasSomething()
    {
        var strip = new PictureStrip();
        int version = strip.Version;
        strip.Clear();
        Assert.Equal(version, strip.Version);

        strip.Add(Tile(4, 4), 0);
        strip.Step(+1);
        version = strip.Version;
        strip.Clear();
        Assert.Equal(0, strip.Count);
        Assert.Null(strip.SelectedId);
        Assert.True(strip.Version > version);
    }

    // ── The window ──────────────────────────────────────────────────────────

    [Fact]
    public void Window_IsPinned()
    {
        int[] tens = [10, 10, 10, 10];
        // One edge cell before, two after, two between: three tens are 37 cells, four 49.
        Assert.Equal((0, 3), PictureStrip.Window(tens, -1, 0, 40));
        Assert.Equal((0, 3), PictureStrip.Window(tens, 2, 0, 40));
        Assert.Equal((1, 3), PictureStrip.Window(tens, 3, 0, 40));   // the least move that shows the fourth whole
        Assert.Equal((1, 3), PictureStrip.Window(tens, 2, 1, 40));   // back a step: the window stays
        Assert.Equal((0, 3), PictureStrip.Window(tens, 0, 1, 40));   // the first: the window follows it
        Assert.Equal((0, 4), PictureStrip.Window(tens, 3, 1, 49));   // all fit from the start: back to it
        Assert.Equal((0, 1), PictureStrip.Window(tens, 0, 0, 5));    // too narrow for one: one anyway
        Assert.Equal((0, 0), PictureStrip.Window([], 0, 0, 40));
        Assert.Equal((3, 1), PictureStrip.Window(tens, -1, 9, 13));  // a start past the end is clamped
    }

    // ── The drawing ─────────────────────────────────────────────────────────

    [Fact]
    public void Render_IsSixRows_TheTilesAGapApart_WithTheMoreMarkOnTheMiddleRow()
    {
        var strip = StripOf(3);
        var lines = Render(strip, 39, highlight: true, out var spans);

        // 12 + 12 + the edges and the gap is 29 cells; a third would be 43.
        Assert.Equal(PictureStrip.Rows, lines.Count);
        string row = " " + Blocks(12) + "  " + Blocks(12) + " ";
        Assert.Equal([row, row, row, row + PictureStrip.MoreRight, row, row], lines);
        Assert.Equal([new PictureSpan(1, 12, 2), new PictureSpan(15, 12, 1)], spans);
    }

    [Fact]
    public void Render_BarsTheHighlightedTile_OnlyWhileHighlighting()
    {
        var strip = StripOf(3);
        strip.Step(+1);
        strip.Step(+1);   // the second tile

        var lines = Render(strip, 39, highlight: true, out _);
        Assert.All(lines, line => Assert.StartsWith(" " + Blocks(12) + " " + PictureStrip.LeftBar + Blocks(12) + PictureStrip.RightBar, line));

        lines = Render(strip, 39, highlight: false, out _);
        Assert.All(lines, line => Assert.DoesNotContain(PictureStrip.LeftBar, line));
    }

    [Fact]
    public void Render_ScrollsToTheHighlight_WithTheMoreMarksEitherSide()
    {
        var strip = StripOf(4);
        for (int i = 0; i < 3; i++)
        {
            strip.Step(+1);   // the third, off the first window
        }

        var lines = Render(strip, 39, highlight: true, out var spans);
        Assert.Equal(PictureStrip.MoreLeft + Blocks(12) + " " + PictureStrip.LeftBar + Blocks(12) + PictureStrip.RightBar + PictureStrip.MoreRight, lines[PictureStrip.Rows / 2]);
        Assert.Equal([new PictureSpan(1, 12, 2), new PictureSpan(15, 12, 1)], spans);
        Assert.Equal(" " + Blocks(12) + " " + PictureStrip.LeftBar + Blocks(12) + PictureStrip.RightBar, lines[0]);
    }

    [Fact]
    public void Render_SitsAShortTileOnTheBottom()
    {
        var strip = new PictureStrip();
        strip.Add(Tile(12, 4), 0);   // two rows

        var lines = Render(strip, 39, highlight: false, out _);
        Assert.All(lines.Take(4), line => Assert.Equal(new string(' ', 14), line));
        Assert.All(lines.Skip(4), line => Assert.Equal(" " + Blocks(12) + " ", line));
    }

    // ── The pane ────────────────────────────────────────────────────────────

    private int? _cursorTop;

    private ScreenPane Pane(PictureStrip strip, Func<bool>? shown = null)
    {
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => _cursorTop), _time)
        {
            Hint = () => "idle",
            PictureStrip = () => shown is null || shown() ? strip : null,
        };
        return pane;
    }

    private static string Rule(int width) => new(ScreenPane.RuleGlyph, width);

    private string Output => _console.Output;

    [Fact]
    public void Pane_DrawsTheStripOverTheUpperRule_ItsRowsInThePanes()
    {
        var strip = StripOf(2, 12, 12);
        using var pane = Pane(strip);
        pane.Show();

        // 20 rows, the flow at row 0: nine empty rows, the strip's rule (2026-09-25) and its six, then rule / input / rule / hint.
        Assert.Equal(ScreenPane.StripPaneRows, pane.StripRows);
        Assert.Equal(9, pane.Padding);
        string row = " " + Blocks(12) + "  " + Blocks(12) + " ";
        Assert.EndsWith(row + "\n" + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\nidle", Output);
        Assert.StartsWith(new string('\n', 9) + Rule(40) + "\n" + row + "\n", Output);
    }

    [Fact]
    public void Pane_DrawsNoStrip_InAShortWindow_UnderAnOverlay_OrEmpty()
    {
        _console.Profile.Height = 14;   // 4 + 7 + 4 = 15 needed: the strip's rule is one of its rows
        using (var pane = Pane(StripOf(1)))
        {
            pane.Show();
            Assert.Equal(0, pane.StripRows);
        }

        _console.Profile.Height = 20;
        using (var pane = Pane(new PictureStrip()))
        {
            pane.Show();
            Assert.Equal(0, pane.StripRows);
        }

        using (var pane = Pane(StripOf(1)))
        {
            pane.Show();
            Assert.Equal(ScreenPane.StripPaneRows, pane.StripRows);
            pane.ShowOverlay(new Markup("menu"), "hint");
            Assert.Equal(0, pane.StripRows);
            pane.CloseOverlay();
            Assert.Equal(ScreenPane.StripPaneRows, pane.StripRows);
        }
    }

    [Fact]
    public void Pane_FollowsTheStrip_OnTheTickAndOnRedrawStrip_AndTheHighlightFollowsTheDraft()
    {
        var strip = new PictureStrip();
        bool on = true;
        using var pane = Pane(strip, () => on);
        pane.Show();
        Assert.Equal(0, pane.StripRows);

        // A picture from another thread: the tick draws it.
        strip.Add(Tile(12, 12), 0);
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(ScreenPane.StripPaneRows, pane.StripRows);

        // Nothing changed: nothing drawn.
        int mark = Output.Length;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal("", Output[mark..]);

        // A step, and the screen's RedrawStrip: the bars at once.
        strip.Step(+1);
        mark = Output.Length;
        pane.RedrawStrip();
        Assert.Contains(PictureStrip.LeftBar + Blocks(12) + PictureStrip.RightBar, Output[mark..]);

        // A key on the line: the highlight goes with the empty draft, and comes back with it.
        mark = Output.Length;
        pane.ShowInput("a", 1);
        Assert.Contains(" " + Blocks(12) + " ", Output[mark..]);
        Assert.DoesNotContain(PictureStrip.LeftBar, Output[mark..]);
        mark = Output.Length;
        pane.ShowInput("", 0);
        Assert.Contains(PictureStrip.LeftBar, Output[mark..]);

        // The setting off: the strip goes on the tick.
        on = false;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(0, pane.StripRows);
    }

    [Fact]
    public void Pane_PictureAt_AnswersTheStripsTiles_AndTheHintRowStaysUnderTheCursor()
    {
        _cursorTop = 100;
        var strip = new PictureStrip();
        strip.Add(Tile(12, 12), 4);
        strip.Add(Tile(12, 4), 5);   // two rows, sat on the bottom
        using var pane = Pane(strip);
        pane.Show();

        // The input row at 100, the upper rule at 99, the strip on 93–98, its own rule at 92.
        Assert.Equal(5, pane.PictureAt(1, 97));
        Assert.Equal(5, pane.PictureAt(12, 98));
        Assert.Equal(4, pane.PictureAt(15, 93));
        Assert.Equal(4, pane.PictureAt(26, 98));
        Assert.True(pane.TryHitStrip(1, 93, out int id));   // above the short tile: the row is the strip's, the column its span
        Assert.Equal(5, id);
        Assert.Null(pane.PictureAt(0, 95));    // the edge cell
        Assert.Null(pane.PictureAt(13, 95));   // the gap
        Assert.Null(pane.PictureAt(1, 99));    // the rule
        Assert.Null(pane.PictureAt(1, 92));    // the strip's own rule
        Assert.False(pane.TryHitStrip(1, 92, out _));
        Assert.Null(pane.PictureAt(1, 91));    // over the strip
        Assert.True(pane.TryHitHint(0, 102));  // the hint row two under the cursor, the strip notwithstanding
        Assert.False(pane.TryHitHint(0, 103));
    }
}
