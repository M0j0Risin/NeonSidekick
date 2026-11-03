using System.Globalization;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// The scroll's anchor and every row-moving change (2026-11-02, the review's part 3): the anchor is kept from the view's last move, every
/// re-layout goes through the store's one relay, an append's trim moves the find, the window is drawn again only when what it shows
/// changed, PgUp from the bottom starts at the row on top, and a resize counts no new rows.
/// </summary>
public partial class ScreenPaneTests
{
    /// <summary>
    /// A resize there and back, scrolled to a line's second row (review P7-1: the anchor was taken afresh from the resolved row's start at
    /// every re-layout, so the view crept to the line's first row): the same row is on top again.
    /// </summary>
    [Fact]
    public void AResizeThereAndBack_ScrolledToALinesSecondRow_KeepsThatRow()
    {
        _console.Profile.Width = 20;
        using var pane = LongLines();
        pane.ScrollBy(-100);
        pane.ScrollBy(1);
        string before = RowTexts(pane)[pane.ScrollTop];
        Assert.DoesNotContain("line", before, StringComparison.Ordinal);   // a line's second row

        _console.Profile.Width = 13;
        _time.Advance(ScreenPane.Tick);
        _console.Profile.Width = 20;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(before, RowTexts(pane)[pane.ScrollTop]);
    }

    /// <summary>A write laid out at another width while scrolled, then the window back before a tick (review P7-4): the top is the row drawn.</summary>
    [Fact]
    public void AWriteAtAnotherWidth_WhileScrolled_ThenBack_KeepsTheRowDrawnOnTop()
    {
        _console.Profile.Width = 20;
        using var pane = LongLines();
        pane.ScrollBy(-100);
        pane.ScrollBy(3);
        string before = RowTexts(pane)[pane.ScrollTop];

        _console.Profile.Width = 13;
        pane.Write(new Markup("a write at 13" + Environment.NewLine));
        _console.Profile.Width = 20;
        _time.Advance(ScreenPane.Tick);
        Assert.True(pane.Scrolled);
        Assert.Equal(before, RowTexts(pane)[pane.ScrollTop]);
    }

    /// <summary>
    /// Ctrl+O while scrolled (review P7-5: the fold's re-layout told the pane nothing, so the scroll kept its row number and the view jumped
    /// as the run above it opened): the same text is on top, folded and unfolded.
    /// </summary>
    [Fact]
    public void CtrlO_WhileScrolled_KeepsTheTextOnTop()
    {
        using var pane = Pane();
        pane.Open();
        pane.Show();
        pane.Write(new Markup("a\n"));
        pane.BeginToolGroup(1);
        pane.SetToolGroupSummary(new Markup("S"), new Markup("E"));
        pane.WriteToolLine(new Markup("m1\n"));
        pane.WriteToolLine(new Markup("m2\n"));
        pane.WriteToolLine(new Markup("m3\n"));
        pane.EndToolGroup();
        for (int i = 1; i <= 12; i++)
        {
            pane.Write(new Markup(Line(i) + Environment.NewLine));
        }

        pane.ScrollBy(-100);
        pane.ScrollBy(6);
        string before = RowTexts(pane)[pane.ScrollTop];
        Assert.StartsWith("L", before, StringComparison.Ordinal);

        pane.SetToolGroupsExpanded(true);
        Assert.Equal(before, RowTexts(pane)[pane.ScrollTop]);
        pane.SetToolGroupsExpanded(false);
        Assert.Equal(before, RowTexts(pane)[pane.ScrollTop]);
    }

    /// <summary>
    /// An append that trims the store's front (review P6-5: the scroll moved with it, the find's match didn't, and the layout's version stood
    /// still, so the find's next Enter took a stale row): the match is on its own text still, and rows read before are refused.
    /// </summary>
    [Fact]
    public void AnAppendThatTrimsTheFront_MovesTheFindsMatch()
    {
        using var pane = Pane();
        pane.Open();
        pane.Show();
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < Scrollback.MaxRows; i++)
        {
            text.Append("row ").Append(i.ToString("00000", CultureInfo.InvariantCulture)).Append('\n');
        }

        pane.Write(new RawText(text.ToString()));
        Assert.Equal(Scrollback.MaxRows, pane.StoredRows);
        pane.TranscriptRows(out int version, out _);
        Assert.True(pane.ShowFind("row 09000", 9000, 0, version));

        pane.Write(new RawText("one more\n"));   // the cap trims the oldest line
        var rows = pane.TranscriptRows(out int after, out var place);
        Assert.NotEqual(version, after);
        Assert.Equal(new TextFind.Hit(8999, 0), place);
        Assert.StartsWith("row 09000", TextFind.LineText(rows[place.Line]), StringComparison.Ordinal);
        Assert.False(pane.ShowFind("row 09001", 9001, 0, version));   // read before the trim
    }

    /// <summary>
    /// A write rendered again off the scrolled window (review P7-7: scrolled, every catch-up tick drew the whole region again though the lines
    /// were above it): laid out with no draw, and the ticks after write nothing either.
    /// </summary>
    [Fact]
    public void WhileScrolled_AWriteRenderedAgain_OffTheWindow_IsNotDrawn()
    {
        using var pane = Pane();
        pane.Open();
        pane.Show();
        pane.Write(new WidthMarkup(width => new string('=', width)));
        for (int i = 1; i <= 20; i++)
        {
            pane.Write(new RawText("line " + i.ToString(CultureInfo.InvariantCulture) + "\n"));
        }

        pane.ScrollPage(-1);
        string top = RowTexts(pane)[pane.ScrollTop];
        pane.RenderBudget = TimeSpan.Zero;
        _console.Profile.Width = 30;
        _time.Advance(ScreenPane.Tick);   // rebuilt, the rule wrapped for want of time
        int mark = Output.Length;
        _time.Advance(ScreenPane.Tick);   // rendered again, far above the window
        _time.Advance(ScreenPane.Tick);
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(new string('=', 30), RowTexts(pane)[0]);
        Assert.Equal(mark, Output.Length);
        Assert.Equal(top, RowTexts(pane)[pane.ScrollTop]);   // the top moved with the rows, the same text on it
    }

    /// <summary>
    /// PgUp from the bottom under a tall live block (review P1-1: the scroll started from the last window's top, so the store rows the live
    /// block pushed off the screen were skipped): the first page up shows the store's last rows.
    /// </summary>
    [Fact]
    public void PgUp_FromTheBottom_UnderATallLiveBlock_StartsAtTheRowOnTop()
    {
        using var pane = Scrollable(lines: 30);
        pane.SetLive(new ThinkingBlock(string.Join("\n", Enumerable.Range(1, 30).Select(i => "thought " + i.ToString(CultureInfo.InvariantCulture)))));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(30, pane.StoredRows);

        pane.ScrollPage(-1);
        Assert.Equal(25, pane.ScrollTop);   // L26..L30, the rows the live block hid; from the last window's top it was 21
    }

    /// <summary>
    /// A resize while scrolled under a live block (review P6-2: the mark was taken at one width and the count at another, so a narrowing
    /// showed new rows nothing wrote): no new rows; more text at the same width is counted.
    /// </summary>
    [Fact]
    public void AResize_WhileScrolled_UnderALiveBlock_CountsNoNewRows()
    {
        using var pane = Scrollable();
        string words = "alpha beta gamma delta epsilon zeta eta";
        pane.SetLive(new Markup(words + "\n" + words + "\n" + words));
        _time.Advance(ScreenPane.Tick);
        pane.ScrollPage(-1);
        _time.Advance(ScreenPane.Tick);

        int mark = Output.Length;
        _console.Profile.Width = 20;
        _time.Advance(ScreenPane.Tick);
        _time.Advance(ScreenPane.Tick);
        Assert.True(pane.Scrolled);
        Assert.DoesNotContain("new row", Output[mark..], StringComparison.Ordinal);

        mark = Output.Length;
        pane.SetLive(new Markup(words + "\n" + words + "\n" + words + "\nmore"));
        _time.Advance(ScreenPane.Tick);
        Assert.Contains("↓ 1 new row", Output[mark..], StringComparison.Ordinal);
    }
}
