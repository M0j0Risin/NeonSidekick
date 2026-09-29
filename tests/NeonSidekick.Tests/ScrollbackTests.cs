using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.Tests;

public class ScrollbackTests
{
    private static List<Segment> Segments(params string[] texts) => texts.Select(t => new Segment(t)).ToList();

    private static string Text(SegmentLine row) => string.Concat(row.Select(s => s.Text));

    private static string[] Texts(IReadOnlyList<SegmentLine> rows) => rows.Select(Text).ToArray();

    [Fact]
    public void Empty_HasNoRows_AndNoOpenLine()
    {
        var store = new Scrollback();
        Assert.Equal(0, store.Count);
        Assert.False(store.LastLineOpen);
        Assert.Empty(store.Rows(40));
    }

    [Fact]
    public void Lines_EndWithABreak_TheLastOneOpenUntilItComes()
    {
        var store = new Scrollback();
        store.Append(Segments("hello\n", "wor"), 40);

        Assert.Equal(new[] { "hello", "wor" }, Texts(store.Rows(40)));
        Assert.True(store.LastLineOpen);
        Assert.Equal(2, store.LineCount);

        // What joins the open line continues its row.
        store.Append(Segments("ld", "\n"), 40);
        Assert.Equal(new[] { "hello", "world" }, Texts(store.Rows(40)));
        Assert.False(store.LastLineOpen);
    }

    [Fact]
    public void ABreakSegment_ClosesTheLine_LikeANewline()
    {
        var store = new Scrollback();
        store.Append(new List<Segment> { new("a"), Segment.LineBreak, new("b"), Segment.LineBreak }, 40);

        Assert.Equal(new[] { "a", "b" }, Texts(store.Rows(40)));
        Assert.False(store.LastLineOpen);
    }

    [Fact]
    public void BlankLines_AreRows_AndAControlCodeOrACarriageReturn_IsNothing()
    {
        var store = new Scrollback();
        store.Append(new List<Segment> { Segment.Control("\e[J"), new("a\r\n\nb\n") }, 40);

        Assert.Equal(new[] { "a", "", "b" }, Texts(store.Rows(40)));
    }

    [Fact]
    public void Wraps_ByCells_AWideCharacterWhole_AndAFullRowWaitsForTheNextCharacter()
    {
        var store = new Scrollback();
        // 4 cells, then a 2-cell character: it starts the next row (the terminal's rule, ScreenPane.Track's).
        store.Append(Segments("abcd日\n"), 5);
        Assert.Equal(new[] { "abcd", "日" }, Texts(store.Rows(5)));

        // A row filled exactly and ended: one row, not an empty second one.
        store.Append(Segments("xxxxx\n"), 5);
        Assert.Equal(new[] { "abcd", "日", "xxxxx" }, Texts(store.Rows(5)));

        // Filled exactly and continued: the next character wraps.
        store.Append(Segments("yyyyy", "z\n"), 5);
        Assert.Equal(new[] { "abcd", "日", "xxxxx", "yyyyy", "z" }, Texts(store.Rows(5)));
    }

    [Fact]
    public void Styles_AreKept_PerRun_AcrossAWrap()
    {
        var store = new Scrollback();
        var red = new Style(Color.Red);
        store.Append(new List<Segment> { new("ab", red), new("cdef"), Segment.LineBreak }, 4);

        var rows = store.Rows(4);
        Assert.Equal(2, rows.Count);
        Assert.Equal(("ab", red), (rows[0][0].Text, rows[0][0].Style));
        Assert.Equal(("cd", Style.Plain), (rows[0][1].Text, rows[0][1].Style));
        Assert.Equal(("ef", Style.Plain), (rows[1][0].Text, rows[1][0].Style));
    }

    [Fact]
    public void AWidthChange_LaysTheRowsOutAgain()
    {
        var store = new Scrollback();
        store.Append(Segments("abcdefgh\n"), 40);
        Assert.Single(store.Rows(40));

        Assert.Equal(new[] { "abc", "def", "gh" }, Texts(store.Rows(3)));
        Assert.Equal(3, store.Count);
        Assert.Equal(new[] { "abcdefgh" }, Texts(store.Rows(40)));
    }

    [Fact]
    public void PastTheCap_TheOldestLinesGo_AndTheRowsDroppedAreReturned()
    {
        var store = new Scrollback();
        for (int i = 0; i < Scrollback.MaxRows; i++)
        {
            Assert.Equal(0, store.Append(Segments("x\n"), 40));
        }

        Assert.Equal(Scrollback.MaxRows, store.Count);
        // A two-row line: two rows over, the first two one-row lines go.
        Assert.Equal(2, store.Append(Segments("first\nsecond\n"), 40));
        Assert.Equal(Scrollback.MaxRows, store.Count);
        Assert.Equal("second", Text(store.Rows(40)[^1]));
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var store = new Scrollback();
        store.Append(Segments("a\nb"), 40);
        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.False(store.LastLineOpen);
        Assert.Equal(0, store.LineCount);
    }

    // ── Tool runs (2026-09-22) ──────────────────────────────────────────────

    /// <summary>A run keeping <paramref name="keep"/>, its summary "S" folded and "E" unfolded, and <paramref name="members"/> member lines m1…mN.</summary>
    private static Scrollback Run(int keep, int members, int width = 40, bool end = false)
    {
        var store = new Scrollback();
        store.Append(Segments("before\n"), width);
        store.BeginGroup(keep);
        store.SetGroupSummary(Segments("  S"), Segments("  E"));
        for (int i = 1; i <= members; i++)
        {
            store.Append(Segments($"  m{i}\n"), width, member: true);
        }

        if (end)
        {
            store.EndGroup();
        }

        return store;
    }

    [Fact]
    public void ToolRun_WithinItsKeep_ShowsEveryLine_NoSummary_NothingReshaped()
    {
        var store = Run(keep: 2, members: 2);

        Assert.Equal(new[] { "before", "  m1", "  m2" }, Texts(store.Rows(40)));
        Assert.False(store.TakeReshaped());   // appends at the end: the pane just writes them
        store.EndGroup();
        Assert.Equal(new[] { "before", "  m1", "  m2" }, Texts(store.Rows(40)));
        Assert.False(store.TakeReshaped());
    }

    [Fact]
    public void ToolRun_PastItsKeep_WhileLive_IsTheSummaryAndTheLastLines_ThenTheSummaryAloneOnceItEnds()
    {
        var store = Run(keep: 2, members: 3);

        Assert.Equal(new[] { "before", "  S", "  m2", "  m3" }, Texts(store.Rows(40)));
        Assert.True(store.TakeReshaped());   // m1 hid and the summary showed above the end: the pane rebuilds

        store.Append(Segments("  m4\n"), 40, member: true);
        Assert.Equal(new[] { "before", "  S", "  m3", "  m4" }, Texts(store.Rows(40)));

        store.EndGroup();
        Assert.Equal(new[] { "before", "  S" }, Texts(store.Rows(40)));
        Assert.True(store.TakeReshaped());
        Assert.False(store.GroupOpen);
    }

    [Fact]
    public void ToolRun_AnyOtherAppend_EndsIt()
    {
        var store = Run(keep: 1, members: 3);
        store.Append(Segments("reply\n"), 40);

        Assert.Equal(new[] { "before", "  S", "reply" }, Texts(store.Rows(40)));
        Assert.False(store.GroupOpen);
    }

    [Fact]
    public void ToolRun_KeepZero_NeverFolds()
    {
        var store = Run(keep: 0, members: 5, end: true);

        Assert.Equal(new[] { "before", "  m1", "  m2", "  m3", "  m4", "  m5" }, Texts(store.Rows(40)));
    }

    [Fact]
    public void ToolRun_Toggle_UnfoldsThatRun_AndAgainFoldsIt()
    {
        var store = Run(keep: 2, members: 3, end: true);
        store.TakeReshaped();

        // The summary's row answers GroupAtRow; a member's or another line's does not.
        Assert.Null(store.GroupAtRow(0));
        int id = Assert.NotNull(store.GroupAtRow(1));
        Assert.Null(store.GroupAtRow(2));

        Assert.True(store.Toggle(id));
        Assert.Equal(new[] { "before", "  E", "  m1", "  m2", "  m3" }, Texts(store.Rows(40)));
        Assert.True(store.TakeReshaped());
        Assert.Null(store.GroupAtRow(2));   // a member's row

        Assert.True(store.Toggle(id));
        Assert.Equal(new[] { "before", "  S" }, Texts(store.Rows(40)));
        Assert.False(store.Toggle(id + 1));
    }

    [Fact]
    public void ToolRun_SetAllExpanded_UnfoldsEveryRun_AndTheRunsToCome_ForgettingEachOwnState()
    {
        var store = Run(keep: 1, members: 2, end: true);
        int first = store.GroupAtRow(1)!.Value;
        store.Toggle(first);   // its own state: unfolded
        store.SetAllExpanded(false);
        Assert.Equal(new[] { "before", "  S" }, Texts(store.Rows(40)));   // the pane-wide state wins, the run's own forgotten

        store.SetAllExpanded(true);
        Assert.True(store.ExpandAll);
        Assert.Equal(new[] { "before", "  E", "  m1", "  m2" }, Texts(store.Rows(40)));

        // A new run follows it: every line, under its unfolded summary.
        store.BeginGroup(1);
        store.SetGroupSummary(Segments("  S2"), Segments("  E2"));
        store.Append(Segments("  n1\n", "  n2\n"), 40, member: true);
        store.EndGroup();
        Assert.Equal(new[] { "before", "  E", "  m1", "  m2", "  E2", "  n1", "  n2" }, Texts(store.Rows(40)));
    }

    [Fact]
    public void ToolRun_TheLead_StandsOverTheFirstVisibleRowsIndent()
    {
        var store = new Scrollback();
        store.Append(Segments("● "), 40);   // the plain path's bare glyph, an open line
        store.BeginGroup(1, Segments("● "), absorbOpenLine: true);
        Assert.True(store.TakeReshaped());   // the glyph's row went
        store.SetGroupSummary(Segments("  S"), Segments("  E"));
        store.Append(Segments("  m1\n"), 40, member: true);
        Assert.Equal(new[] { "● m1" }, Texts(store.Rows(40)));   // not folded: the first member leads

        store.Append(Segments("  m2\n"), 40, member: true);
        store.EndGroup();
        Assert.Equal(new[] { "● S" }, Texts(store.Rows(40)));    // folded: the summary leads

        store.SetAllExpanded(true);
        Assert.Equal(new[] { "● E", "  m1", "  m2" }, Texts(store.Rows(40)));
    }

    [Fact]
    public void ToolRun_Rewraps_AtANewWidth_InItsFoldedShape()
    {
        var store = Run(keep: 1, members: 3, end: true);
        store.SetAllExpanded(true);

        Assert.Equal(new[] { "befo", "re", "  E", "  m1", "  m2", "  m3" }, Texts(store.Rows(4)));
        store.SetAllExpanded(false);
        Assert.Equal(new[] { "befo", "re", "  S" }, Texts(store.Rows(4)));
    }

    [Fact]
    public void ToolRun_Trim_DropsARunWhole_AndHiddenLinesCountTowardTheCap()
    {
        var store = new Scrollback();
        store.BeginGroup(1);
        for (int i = 0; i < Scrollback.MaxRows; i++)
        {
            store.Append(Segments("m\n"), 40, member: true);
        }

        store.EndGroup();
        Assert.Equal(1, store.Count);   // the summary alone shows
        store.Append(Segments("after\n"), 40);

        // The run's lines passed the cap: it went whole, summary and members, the line after it stays.
        Assert.Equal(new[] { "after" }, Texts(store.Rows(40)));
        Assert.Null(store.GroupAtRow(0));
    }

    /// <summary>
    /// What the upper rule's ↘️↖️ are drawn for (2026-09-28): a run past its keep folds, one under it does not, a code block
    /// folds only once it ends, and a clear takes every fold with it.
    /// </summary>
    [Fact]
    public void AnyFolds_CountsOnlyWhatFolds()
    {
        Assert.False(new Scrollback().AnyFolds);
        Assert.False(Run(keep: 2, members: 2, end: true).AnyFolds);   // under its keep: drawn as it is
        Assert.False(Run(keep: 0, members: 3, end: true).AnyFolds);   // keeps everything

        var run = Run(keep: 1, members: 2);
        Assert.True(run.AnyFolds);   // past its keep while it runs, too
        run.Clear();
        Assert.False(run.AnyFolds);

        var code = Code(keep: 2, size: 3, rows: 3);
        Assert.False(code.AnyFolds);   // still streaming
        code.EndGroup();
        Assert.True(code.AnyFolds);
    }

    [Fact]
    public void ToolRun_Clear_ForgetsTheRuns_ButNotExpandAll()
    {
        var store = Run(keep: 1, members: 3);
        store.SetAllExpanded(true);
        store.Clear();

        Assert.False(store.GroupOpen);
        Assert.True(store.ExpandAll);
        Assert.False(store.TakeReshaped());
    }

    // ── Thinking (2026-09-26) ───────────────────────────────────────────────

    /// <summary>A thinking block: its header "  ▾ thinking", folded "  ▸ T", unfolded "  ▾ T", and <paramref name="rows"/> rows t1…tN.</summary>
    private static Scrollback Thinking(int rows, bool end = false)
    {
        var store = new Scrollback();
        store.Append(Segments("before\n"), 40);
        store.BeginThinkingGroup(Segments("  ▾ thinking"));
        for (int i = 1; i <= rows; i++)
        {
            store.Append(Segments($"    t{i}\n"), 40, member: true);
        }

        if (end)
        {
            store.SetThinkingGroupSummary(Segments("  ▸ T"), Segments("  ▾ T"));
            store.EndGroup();
        }

        return store;
    }

    [Fact]
    public void Thinking_WhileLive_ShowsItsHeaderAndEveryRow_AndTogglesNothing()
    {
        var store = Thinking(rows: 3);

        Assert.Equal(new[] { "before", "  ▾ thinking", "    t1", "    t2", "    t3" }, Texts(store.Rows(40)));
        Assert.False(store.TakeReshaped());
        Assert.True(store.ThinkingGroupOpen);
        Assert.False(store.CodeGroupOpen);
        Assert.False(store.GroupOpen);
        Assert.False(store.Toggle(Assert.NotNull(store.GroupAtRow(1))));
    }

    [Fact]
    public void Thinking_FoldsToItsSummary_TheMomentItEnds_EvenAsOneRow_AndTogglesOpen()
    {
        var store = Thinking(rows: 1, end: true);

        Assert.Equal(new[] { "before", "  ▸ T" }, Texts(store.Rows(40)));
        Assert.True(store.TakeReshaped());
        Assert.False(store.ThinkingGroupOpen);

        int id = Assert.NotNull(store.GroupAtRow(1));
        Assert.True(store.Toggle(id));
        Assert.Equal(new[] { "before", "  ▾ T", "    t1" }, Texts(store.Rows(40)));
        Assert.True(store.Toggle(id));
        Assert.Equal(new[] { "before", "  ▸ T" }, Texts(store.Rows(40)));
    }

    [Fact]
    public void Thinking_UnfoldsWithTheRuns_OnExpandAll()
    {
        var store = Thinking(rows: 2, end: true);
        store.Append(Segments("answer\n"), 40);

        store.SetAllExpanded(true);
        Assert.Equal(new[] { "before", "  ▾ T", "    t1", "    t2", "answer" }, Texts(store.Rows(40)));
        store.SetAllExpanded(false);
        Assert.Equal(new[] { "before", "  ▸ T", "answer" }, Texts(store.Rows(40)));
    }

    [Fact]
    public void Thinking_SummaryIsForThinkingOnly()
    {
        var store = Code(keep: 1, size: 2, rows: 2);
        store.SetThinkingGroupSummary(Segments("  ▸ T"), Segments("  ▾ T"));
        store.EndGroup();

        Assert.Equal(new[] { "before", "  ▸ S" }, Texts(store.Rows(40)));
    }

    // ── Code blocks (later on 2026-09-22) ───────────────────────────────────

    /// <summary>A code block keeping <paramref name="keep"/> of <paramref name="size"/> source lines: its label "  cs", folded "  ▸ S", unfolded "  ▾ E", and <paramref name="rows"/> body rows c1…cN.</summary>
    private static Scrollback Code(int keep, int size, int rows, bool end = false)
    {
        var store = new Scrollback();
        store.Append(Segments("before\n"), 40);
        store.BeginCodeGroup(keep, Segments("  cs"));
        store.SetCodeGroupSummary(Segments("  ▸ S"), Segments("  ▾ E"), size);
        for (int i = 1; i <= rows; i++)
        {
            store.Append(Segments($"    c{i}\n"), 40, member: true);
        }

        if (end)
        {
            store.EndGroup();
        }

        return store;
    }

    [Fact]
    public void CodeBlock_WhileLive_ShowsItsLabelAndEveryRow_NothingReshaped_AndIsNoToolRun()
    {
        var store = Code(keep: 2, size: 3, rows: 3);

        Assert.Equal(new[] { "before", "  cs", "    c1", "    c2", "    c3" }, Texts(store.Rows(40)));
        Assert.False(store.TakeReshaped());   // the label took its row at once; the rows are appends at the end
        Assert.True(store.CodeGroupOpen);
        Assert.False(store.GroupOpen);        // a tool line never joins it
        Assert.Equal("  cs", string.Concat(store.CodeGroupLabel.Select(s => s.Text)));
        Assert.False(store.Toggle(Assert.NotNull(store.GroupAtRow(1))));   // nothing to toggle while it streams
    }

    [Fact]
    public void CodeBlock_PastItsKeep_FoldsToItsSummary_OnceItEnds_AndTogglesOpen()
    {
        var store = Code(keep: 2, size: 3, rows: 3, end: true);

        Assert.Equal(new[] { "before", "  ▸ S" }, Texts(store.Rows(40)));
        Assert.True(store.TakeReshaped());
        Assert.False(store.CodeGroupOpen);

        int id = Assert.NotNull(store.GroupAtRow(1));
        Assert.True(store.Toggle(id));
        Assert.Equal(new[] { "before", "  ▾ E", "    c1", "    c2", "    c3" }, Texts(store.Rows(40)));
    }

    [Fact]
    public void CodeBlock_WithinItsKeep_StaysWhole_UnderItsPlainLabel_AndItsLabelToggles_Nothing()
    {
        var store = Code(keep: 3, size: 3, rows: 3, end: true);
        store.Append(Segments("after\n"), 40);

        Assert.Equal(new[] { "before", "  cs", "    c1", "    c2", "    c3", "after" }, Texts(store.Rows(40)));
        Assert.False(store.Toggle(Assert.NotNull(store.GroupAtRow(1))));
        store.SetAllExpanded(true);
        Assert.Equal(new[] { "before", "  cs", "    c1", "    c2", "    c3", "after" }, Texts(store.Rows(40)));
    }

    [Fact]
    public void CodeBlock_IsMeasuredByItsSourceLines_NotItsRows()
    {
        // Two source lines, one wrapped into three rows: within a keep of 2.
        var store = Code(keep: 2, size: 2, rows: 3, end: true);

        Assert.Equal(new[] { "before", "  cs", "    c1", "    c2", "    c3" }, Texts(store.Rows(40)));
    }

    [Fact]
    public void CodeBlock_AnotherAppend_EndsIt_AndCtrlO_UnfoldsIt_WithTheToolRuns()
    {
        var store = Code(keep: 1, size: 2, rows: 2);
        store.Append(Segments(" \n"), 40);   // the spacer after the block

        Assert.Equal(new[] { "before", "  ▸ S", " " }, Texts(store.Rows(40)));
        store.SetAllExpanded(true);
        Assert.Equal(new[] { "before", "  ▾ E", "    c1", "    c2", " " }, Texts(store.Rows(40)));
    }

    /// <summary>A picture's lines carry its tiles' columns (later on 2026-09-24): a click finds the tile under it, not the gap, not another line — and nothing once the line wraps.</summary>
    [Fact]
    public void Pictures_TagTheirLines_AndAClickFindsTheTile()
    {
        var store = new Scrollback();
        store.Append(Segments("text\n"), 40);
        IReadOnlyList<PictureSpan> row = [new(0, 4, 7), new(6, 3, 8)];
        store.Append(Segments("aaaa  bbb\n", "aaaa  bbb\n"), 40, member: false, [row, row]);
        store.Append(Segments("after\n"), 40);

        Assert.Null(store.PictureAt(0, 0));        // the text
        Assert.Equal(7, store.PictureAt(1, 3));
        Assert.Null(store.PictureAt(1, 4));        // the gap
        Assert.Equal(8, store.PictureAt(2, 6));
        Assert.Null(store.PictureAt(2, 9));        // past the last tile
        Assert.Null(store.PictureAt(3, 0));        // after
        store.Rows(5);                             // narrower than the strip: its lines wrap
        Assert.Null(store.PictureAt(1, 3));
    }
}
