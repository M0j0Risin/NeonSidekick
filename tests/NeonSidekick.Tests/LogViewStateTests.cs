using NeonSidekick.Diagnostics;
using NeonSidekick.UI;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>What the log window decides without a window (2026-10-02, <c>/log</c>): wrapping, following, the anchor, the selection, the scroll bar, the keys.</summary>
public class LogViewStateTests
{
    private static List<LogLine> Lines(long from, params string[] texts) =>
        texts.Select((text, i) => new LogLine(from + i, DiagnosticLevel.Info, text)).ToList();

    private static List<LogLine> Numbered(long from, int count) =>
        Enumerable.Range(0, count).Select(i => new LogLine(from + i, DiagnosticLevel.Info, "line " + (from + i))).ToList();

    private static LogViewState View(int columns, int rows, IReadOnlyList<LogLine> lines)
    {
        var state = new LogViewState();
        state.Resize(columns, rows);
        state.Append(lines, lines.Count > 0 ? lines[0].Seq : 0);
        return state;
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a\tb", "a b")]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("bell\u0007", "bell ")]
    [InlineData("ends\r\n\n", "ends")]
    public void Display_MakesBreaksBreaks_AndOtherControlsSpaces(string text, string expected)
    {
        Assert.Equal(expected, LogViewState.Display(text));
    }

    [Fact]
    public void ALine_WrapsAtTheColumns_ABreakStartsARow_AndAnEmptyPieceIsARow()
    {
        Assert.Equal(1, LogViewState.CountRows("", 10));
        Assert.Equal(1, LogViewState.CountRows("0123456789", 10));
        Assert.Equal(2, LogViewState.CountRows("0123456789a", 10));
        Assert.Equal(3, LogViewState.CountRows("ab\n\ncd", 10));
        Assert.Equal((10, 1), LogViewState.RowSpan("0123456789a", 10, 1));
        Assert.Equal((3, 0), LogViewState.RowSpan("ab\n\ncd", 10, 1));
        Assert.Equal((4, 2), LogViewState.RowSpan("ab\n\ncd", 10, 2));
    }

    [Fact]
    public void AWrap_NeverSplitsASurrogatePair()
    {
        string text = "abc\U0001F600de";   // the emoji is two chars at 3 and 4
        Assert.Equal((0, 3), LogViewState.RowSpan(text, 4, 0));
        Assert.Equal((3, 4), LogViewState.RowSpan(text, 4, 1));
    }

    [Fact]
    public void ANewView_Follows_AndNewLinesKeepTheBottomInView()
    {
        var state = View(20, 3, Numbered(0, 5));

        Assert.True(state.Following);
        Assert.Equal(5, state.TotalRows);
        Assert.Equal(2, state.TopRow);
        Assert.Equal(["line 2", "line 3", "line 4"], state.Rows(extra: 0).Select(r => r.Text));
        Assert.Equal(5, state.NextSeq);

        state.Append(Numbered(5, 2), firstHeld: 0);
        Assert.Equal(4, state.TopRow);
        Assert.Equal("line 6", state.Rows(extra: 0)[^1].Text);
    }

    [Fact]
    public void ScrollingAway_Pauses_NewLinesLeaveTheViewStill_AndTheBottomFollowsAgain()
    {
        var state = View(20, 3, Numbered(0, 10));
        state.ScrollBy(-2);

        Assert.False(state.Following);
        Assert.Equal(5, state.TopRow);
        state.Append(Numbered(10, 5), firstHeld: 0);
        Assert.Equal(5, state.TopRow);   // the paused view stays on its lines
        Assert.Equal("line 5", state.Rows()[0].Text);

        state.ScrollBy(100);   // the wheel down past the end lands on the bottom: following again
        Assert.True(state.Following);
        Assert.Equal(12, state.TopRow);
    }

    [Fact]
    public void Top_PausesEvenWhenEverythingFits_AndBottom_FollowsAgain()
    {
        var state = View(20, 10, Numbered(0, 3));
        state.ScrollToTop();
        Assert.False(state.Following);
        Assert.Equal(0, state.TopRow);

        state.Append(Numbered(3, 20), firstHeld: 0);
        Assert.Equal(0, state.TopRow);   // Ctrl+Home holds the top while the log grows past the view

        state.ScrollToBottom();
        Assert.True(state.Following);
        Assert.Equal(13, state.TopRow);
    }

    [Fact]
    public void Apply_ScrollsByRowsAndPages_AndLeavesCopyAndTheFrameToTheWindow()
    {
        var state = View(20, 5, Numbered(0, 30));
        Assert.Equal(4, state.Page);

        Assert.True(state.Apply(LogViewAction.PageUp));
        Assert.Equal(21, state.TopRow);
        Assert.True(state.Apply(LogViewAction.LineUp));
        Assert.Equal(20, state.TopRow);
        Assert.True(state.Apply(LogViewAction.LineDown));
        Assert.True(state.Apply(LogViewAction.PageDown));
        Assert.True(state.Following);
        Assert.True(state.Apply(LogViewAction.Top));
        Assert.Equal(0, state.TopRow);
        Assert.True(state.Apply(LogViewAction.Bottom));
        Assert.True(state.Following);

        Assert.False(state.Apply(LogViewAction.Copy));
        Assert.False(state.Apply(LogViewAction.ToggleFullScreen));
        Assert.False(state.Apply(LogViewAction.Close));
        Assert.False(state.Apply(LogViewAction.None));
    }

    [Fact]
    public void LinesDroppedFromTheFront_LeaveAPausedViewOnItsLines_OrTheFirstWhenItsOwnWentToo()
    {
        var state = View(20, 3, Numbered(0, 10));
        state.ScrollTo(4);
        Assert.Equal("line 4", state.Rows()[0].Text);

        state.Append(Numbered(10, 2), firstHeld: 2);   // the ring dropped lines 0 and 1
        Assert.Equal(10, state.LineCount);
        Assert.Equal("line 4", state.Rows()[0].Text);
        Assert.Equal(2, state.TopRow);

        state.Append([], firstHeld: 6);   // and now the line in view itself
        Assert.Equal("line 6", state.Rows()[0].Text);
        Assert.Equal(0, state.TopRow);
        Assert.False(state.Append([], firstHeld: 6));   // nothing new: nothing changed
    }

    [Fact]
    public void AllLinesDropped_ThenNewOnes_StartAgain()
    {
        var state = View(20, 3, Numbered(0, 3));
        state.Append(Numbered(50, 2), firstHeld: 50);

        Assert.Equal(2, state.LineCount);
        Assert.Equal(["line 50", "line 51"], state.Rows().Select(r => r.Text));
        Assert.Equal(52, state.NextSeq);
        Assert.False(state.Append(Numbered(50, 2), firstHeld: 50));   // lines it has already are not added twice
    }

    [Fact]
    public void Resize_ReWrapsEveryLine_KeepingTheFirstLineInView()
    {
        var state = View(10, 2, Lines(0, "aaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbb", "cc", "dd"));
        Assert.Equal(6, state.TotalRows);
        state.ScrollTo(2);   // the first row of the b line
        Assert.Equal('b', state.Rows()[0].Text[0]);

        state.Resize(5, 2);
        Assert.Equal(10, state.TotalRows);
        Assert.Equal(4, state.TopRow);   // still the b line's first row
        Assert.Equal(5, state.Columns);

        state.Resize(40, 2);   // one row each now: the b line still first
        Assert.Equal(1, state.TopRow);
        Assert.False(state.Following);

        state.Resize(40, 10);   // the view grew past the lines: held to them
        Assert.Equal(0, state.TopRow);
    }

    [Fact]
    public void Rows_AreTheViewAndOneCutOffRowMore()
    {
        var state = View(20, 3, Numbered(0, 10));
        state.ScrollTo(0);

        Assert.Equal(4, state.Rows().Count);
        Assert.Equal(3, state.Rows(extra: 0).Count);
        Assert.Empty(new LogViewState().Rows());
    }

    [Fact]
    public void HitTest_FindsTheCharacterUnderACell_HeldToTheText()
    {
        var state = View(4, 5, [new LogLine(0, DiagnosticLevel.Info, "abcdef"), new LogLine(1, DiagnosticLevel.Warning, "xy")]);
        state.ScrollToTop();

        Assert.Equal(new LogPosition(0, 2), state.HitTest(0, 2));
        Assert.Equal(new LogPosition(0, 5), state.HitTest(1, 1));   // the wrapped row: "ef"
        Assert.Equal(new LogPosition(0, 6), state.HitTest(1, 9));   // past the row's end: its end
        Assert.Equal(new LogPosition(1, 0), state.HitTest(2, -3));
        Assert.Equal(new LogPosition(0, 0), state.HitTest(-1, 3));   // above the lines: the first place
        Assert.Equal(new LogPosition(1, 2), state.HitTest(9, 0));    // below them: the last
        Assert.Null(new LogViewState().HitTest(0, 0));
    }

    [Fact]
    public void ASelection_IsInReadingOrder_AndCopiesWithCrlf()
    {
        var state = View(40, 5, Lines(0, "first line", "second\nhalf", "third"));
        Assert.False(state.HasSelection);

        state.BeginSelection(new LogPosition(2, 3));
        Assert.False(state.HasSelection);   // a press selects nothing until it moves
        state.ExtendSelection(new LogPosition(0, 6));

        Assert.Equal((new LogPosition(0, 6), new LogPosition(2, 3)), state.Selection);
        Assert.Equal("line\r\nsecond\r\nhalf\r\nthi", state.SelectedText());

        state.SelectAll();
        Assert.Equal("first line\r\nsecond\r\nhalf\r\nthird", state.SelectedText());

        state.ClearSelection();
        Assert.Equal("", state.SelectedText());
        Assert.Null(state.Selection);
    }

    [Fact]
    public void RowSelection_IsTheSelectedCells_AndOneMoreWhenTheSelectionRunsPastABreak()
    {
        var state = View(4, 5, Lines(0, "abcdef", "", "xyz"));
        state.ScrollToTop();
        var rows = state.Rows(extra: 0);   // "abcd", "ef", "", "xyz"
        state.BeginSelection(new LogPosition(0, 2));
        state.ExtendSelection(new LogPosition(2, 1));

        Assert.Equal((2, 4), state.RowSelection(rows[0]));   // a wrapped row: to its end, no extra cell
        Assert.Equal((0, 3), state.RowSelection(rows[1]));   // the line's end: the break's cell too
        Assert.Equal((0, 1), state.RowSelection(rows[2]));   // the empty line shows selected
        Assert.Equal((0, 1), state.RowSelection(rows[3]));
        Assert.True(rows[1].EndsSegment);
        Assert.False(rows[0].EndsSegment);

        state.BeginSelection(new LogPosition(2, 2));
        state.ExtendSelection(new LogPosition(2, 3));
        Assert.Null(state.RowSelection(rows[0]));
        Assert.Equal((2, 3), state.RowSelection(rows[3]));
    }

    [Fact]
    public void ASelection_OnDroppedLines_IsCutToWhatIsLeft_OrGone()
    {
        var state = View(40, 5, Numbered(0, 5));
        state.BeginSelection(new LogPosition(3, 2));
        state.ExtendSelection(new LogPosition(1, 0));
        state.Append([], firstHeld: 2);

        Assert.Equal((new LogPosition(2, 0), new LogPosition(3, 2)), state.Selection);

        state.Append([], firstHeld: 4);
        Assert.Null(state.Selection);

        state.SelectAll();
        state.Append(Numbered(5, 1), firstHeld: 5);
        Assert.Null(state.Selection);   // every selected line gone
        new LogViewState().SelectAll();   // nothing held: nothing to select
    }

    [Theory]
    [InlineData(100, 10, 0, 200, 20, 0, 20)]
    [InlineData(100, 10, 90, 200, 20, 180, 20)]
    [InlineData(100, 10, 45, 200, 20, 90, 20)]
    [InlineData(10000, 10, 0, 200, 20, 0, 20)]       // a long log: the shortest thumb
    [InlineData(5, 10, 0, 200, 20, 0, 200)]          // everything in view: the whole track
    [InlineData(100, 10, 0, 0, 20, 0, 0)]
    public void Thumb_IsTheViewsShareOfTheTrack(long total, long visible, long top, int track, int min, int offset, int length)
    {
        Assert.Equal((offset, length), LogViewState.Thumb(total, visible, top, track, min));
    }

    [Fact]
    public void TopForThumb_IsThumbsInverse()
    {
        var (offset, length) = LogViewState.Thumb(100, 10, 45, 200, 20);
        Assert.Equal(45, LogViewState.TopForThumb(offset, 100, 10, 200, length));
        Assert.Equal(0, LogViewState.TopForThumb(-50, 100, 10, 200, length));
        Assert.Equal(90, LogViewState.TopForThumb(500, 100, 10, 200, length));
        Assert.Equal(0, LogViewState.TopForThumb(10, 5, 10, 200, 200));
    }

    [Fact]
    public void WheelRows_AddsUpToNotches_AndHonoursTheSystemsLines()
    {
        int remainder = 0;
        Assert.Equal(-3, LogViewState.WheelRows(ref remainder, 120, 3, 20));   // up a notch: three rows up
        Assert.Equal(6, LogViewState.WheelRows(ref remainder, -240, 3, 20));
        Assert.Equal(0, LogViewState.WheelRows(ref remainder, 60, 3, 20));     // half a notch (a touchpad)
        Assert.Equal(-3, LogViewState.WheelRows(ref remainder, 60, 3, 20));    // and the other half
        Assert.Equal(0, remainder);
        Assert.Equal(-20, LogViewState.WheelRows(ref remainder, 120, -1, 20)); // WHEEL_PAGESCROLL: a page
        Assert.Equal(0, LogViewState.WheelRows(ref remainder, 120, 0, 20));    // scrolling off
    }

    [Theory]
    [InlineData(ViewerState.VkEscape, false, false, LogViewAction.Close)]
    [InlineData(ViewerState.VkEscape, false, true, LogViewAction.LeaveFullScreen)]
    [InlineData(ViewerState.VkF11, false, false, LogViewAction.ToggleFullScreen)]
    [InlineData(ViewerState.VkHome, true, false, LogViewAction.Top)]
    [InlineData(ViewerState.VkEnd, true, false, LogViewAction.Bottom)]
    [InlineData(LogViewState.VkE, true, false, LogViewAction.Bottom)]
    [InlineData(LogViewState.VkA, true, false, LogViewAction.SelectAll)]
    [InlineData(LogViewState.VkC, true, false, LogViewAction.Copy)]
    [InlineData(ViewerState.VkUp, false, false, LogViewAction.LineUp)]
    [InlineData(ViewerState.VkDown, false, false, LogViewAction.LineDown)]
    [InlineData(LogViewState.VkPrior, false, false, LogViewAction.PageUp)]
    [InlineData(LogViewState.VkNext, false, false, LogViewAction.PageDown)]
    [InlineData(ViewerState.VkHome, false, false, LogViewAction.None)]   // Ctrl+Home only (the user's spec)
    [InlineData(LogViewState.VkE, false, false, LogViewAction.None)]
    [InlineData(ViewerState.VkDelete, false, false, LogViewAction.None)]
    public void ActionFor_IsTheUsersKeys(int key, bool control, bool fullScreen, LogViewAction expected)
    {
        Assert.Equal(expected, LogViewState.ActionFor(key, control, fullScreen));
    }

    [Fact]
    public void LogPosition_OrdersByLineThenCharacter()
    {
        var a = new LogPosition(1, 5);
        var b = new LogPosition(2, 0);
        Assert.True(a < b);
        Assert.True(b > a);
        Assert.True(a <= new LogPosition(1, 5));
        Assert.True(b >= a);
        Assert.Equal(0, a.CompareTo(new LogPosition(1, 5)));
    }

    [Fact]
    public void Style_TakesTheTheme_ColoursByLevel_AndIsBlackUnthemed()
    {
        var palette = ThemePalette.Synthwave;
        var style = LogViewStyle.For(palette);

        Assert.Equal(ViewerStyle.ColorRef(palette.Bg), style.Background);
        Assert.Equal(ViewerStyle.ColorRef(palette.Ink), style.ColorOf(DiagnosticLevel.Info));
        Assert.Equal(ViewerStyle.ColorRef(palette.Dim), style.ColorOf(DiagnosticLevel.Trace));
        Assert.Equal(ViewerStyle.ColorRef(palette.Dim), style.ColorOf(DiagnosticLevel.Debug));
        Assert.Equal(ViewerStyle.ColorRef(palette.Warn), style.ColorOf(DiagnosticLevel.Warning));
        Assert.Equal(ViewerStyle.ColorRef(palette.Bad), style.ColorOf(DiagnosticLevel.Error));
        Assert.Equal(ViewerStyle.ColorRef(palette.Primary), style.SelectionBack);
        Assert.Equal(LogViewStyle.Black, LogViewStyle.For(palette, themed: false));
        Assert.Throws<ArgumentNullException>(() => LogViewStyle.For(null!));
    }

    [Fact]
    public void Title_SaysWhenFollowingIsPaused()
    {
        Assert.Equal("NeonSidekick log", LogViewText.TitleFor(following: true));
        Assert.Equal("NeonSidekick log (paused: Ctrl+E follows)", LogViewText.TitleFor(following: false));
    }
}
