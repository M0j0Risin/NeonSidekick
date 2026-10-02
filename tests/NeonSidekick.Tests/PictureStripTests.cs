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

    /// <summary>The viewer's keys (2026-09-28, ComfyUI picture strip sync): the newest tile with a matching id highlighted; a miss changes nothing.</summary>
    [Fact]
    public void Highlight_TakesTheNewestMatch_AndAMissKeepsTheHighlight()
    {
        Assert.False(new PictureStrip().Highlight(_ => true));

        var strip = StripOf(4);   // newest first: ids 3, 2, 1, 0
        int version = strip.Version;
        Assert.True(strip.Highlight(id => id is 1 or 0));
        Assert.Equal(1, strip.SelectedId);   // the newer of the two
        Assert.Equal(2, strip.Selected);
        Assert.True(strip.Version > version);

        version = strip.Version;
        Assert.False(strip.Highlight(id => id == 1));   // already there: no change, no redraw
        Assert.False(strip.Highlight(id => id == 99));   // not in the strip: ignored
        Assert.Equal(1, strip.SelectedId);
        Assert.Equal(version, strip.Version);

        Assert.True(strip.Highlight(id => id == 3));
        Assert.Equal(0, strip.Selected);
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

    /// <summary>The rule's × (2026-09-28): the strip put away with its highlight let go, until the next picture or a clear; nothing to close when empty.</summary>
    [Fact]
    public void Close_PutsItAway_UntilTheNextPictureOrAClear()
    {
        var empty = new PictureStrip();
        int version = empty.Version;
        empty.Close();
        Assert.False(empty.Closed);
        Assert.Equal(version, empty.Version);

        var strip = StripOf(3);
        strip.Step(+1);
        version = strip.Version;
        strip.Close();
        Assert.True(strip.Closed);
        Assert.Null(strip.SelectedId);
        Assert.Equal(3, strip.Count);   // the pictures are kept
        Assert.True(strip.Version > version);

        version = strip.Version;
        strip.Close();   // already closed: no change
        Assert.Equal(version, strip.Version);

        strip.Add(Tile(12, 12), 9);
        Assert.False(strip.Closed);

        strip.Close();
        strip.Clear();
        Assert.False(strip.Closed);
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

        // 20 rows, the flow at row 0: nine empty rows, the strip's rule (2026-09-25, its close × since 2026-09-28) and its
        // six, then rule / input / rule / hint.
        Assert.Equal(ScreenPane.StripPaneRows, pane.StripRows);
        Assert.Equal(9, pane.Padding);
        string row = " " + Blocks(12) + "  " + Blocks(12) + " ";
        Assert.EndsWith(row + "\n" + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\nidle", Output);
        Assert.StartsWith(new string('\n', 9) + Rule(36) + " " + ScreenPane.CloseGlyph + " ─\n" + row + "\n", Output);
    }

    /// <summary>A click on the strip rule's × or the cell either side hits it (2026-09-28); the button's with none drawn misses.</summary>
    [Fact]
    public void Pane_HitsTheStripsClose_OnItsRule()
    {
        _cursorTop = 100;
        var strip = StripOf(2, 12, 12);
        using var pane = Pane(strip);
        pane.Show();

        // The input row at 100, the upper rule at 99, the strip on 93–98, its own rule at 92; the × at column 37 of 40.
        Assert.True(pane.TryHitStripClose(37, 92));
        Assert.True(pane.TryHitStripClose(36, 92));
        Assert.True(pane.TryHitStripClose(38, 92));
        Assert.False(pane.TryHitStripClose(35, 92));
        Assert.False(pane.TryHitStripClose(37, 93));
        Assert.False(pane.TryHitStripButton(2, 92));
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

    /// <summary>
    /// A click on a tool run's summary with the strip drawn (2026-09-25, the user's report): the transcript's rows sit over
    /// the strip, not on the upper rule — read from the rule they were the strip's height too high and the click missed.
    /// </summary>
    [Fact]
    public void Pane_ToggleToolGroupAt_FindsTheSummary_OverTheStrip()
    {
        _cursorTop = 100;
        using var pane = Pane(StripOf(1));
        pane.Show();
        pane.Write(new Markup("a\n"));
        pane.BeginToolGroup(1);
        pane.SetToolGroupSummary(new Markup("S"), new Markup("E"));
        pane.WriteToolLine(new Markup("m1\n"));
        pane.WriteToolLine(new Markup("m2\n"));
        pane.WriteToolLine(new Markup("m3\n"));
        pane.EndToolGroup();
        Assert.Equal(ScreenPane.StripPaneRows, pane.StripRows);
        Assert.Equal(2, pane.StoredRows);

        // The upper rule at 99, the strip on 93–98 under its rule at 92: the region is 83–91, "a" on 83, the summary on 84.
        Assert.False(pane.TryToggleToolGroupAt(0, 83));
        Assert.False(pane.TryToggleToolGroupAt(0, 91));
        Assert.True(pane.TryToggleToolGroupAt(0, 84));
        Assert.Equal(5, pane.StoredRows);
        Assert.True(pane.TryToggleToolGroupAt(0, 84));
        Assert.Equal(2, pane.StoredRows);
    }

    /// <summary>
    /// Scrolled with the strip drawn (2026-10-01): the scroll's row is the pane's top row, over the strip's rule — the
    /// transcript's rows over it, a click on a tool run's summary still landing, a double-click's row the scroll's.
    /// </summary>
    [Fact]
    public void Pane_WhileScrolled_TheScrollsRowSitsOverTheStripsRule()
    {
        _cursorTop = 100;
        using var pane = Pane(StripOf(1));
        pane.Show();
        pane.Write(new Markup("a\n"));
        pane.BeginToolGroup(1);
        pane.SetToolGroupSummary(new Markup("S"), new Markup("E"));
        pane.WriteToolLine(new Markup("m1\n"));
        pane.WriteToolLine(new Markup("m2\n"));
        pane.EndToolGroup();
        for (int i = 1; i <= 12; i++)
        {
            pane.Write(new Markup("L" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"));
        }

        Assert.Equal(14, pane.StoredRows);
        int mark = Output.Length;
        pane.ScrollToTop();
        Assert.True(pane.Scrolled);

        // The upper rule at 99, the strip on 93–98 under its rule at 92, the scroll's row at 91: the region is 83–90,
        // eight rows — "a" on 83, the summary on 84 — and six below.
        Assert.Equal(6, pane.RowsBelow);
        Assert.Contains(ScreenPane.ScrolledRow(6, 39) + "\n" + Rule(36) + " " + ScreenPane.CloseGlyph + " ─\n", Output[mark..]);
        Assert.True(pane.TryHitHint(0, 91, out var hit));
        Assert.Equal(ScreenPane.HintZone.Scrolled, hit.Zone);
        Assert.False(pane.TryHitHint(0, 92, out _));
        Assert.False(pane.TryToggleToolGroupAt(0, 91));
        Assert.Null(pane.PictureAt(1, 91));
        Assert.True(pane.TryHitStripClose(37, 92));   // the strip's own rule where it was
        Assert.True(pane.TryToggleToolGroupAt(0, 84));
        Assert.Equal(16, pane.StoredRows);
    }
}
