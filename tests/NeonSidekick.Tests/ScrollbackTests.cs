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
        store.Append(Segments("  n1\n"), 40, member: true);
        store.Append(Segments("  n2\n"), 40, member: true);   // two writes: a run counts writes, not lines (2026-10-03)
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
    /// What the upper rule's ⤡ is drawn for (2026-09-28): a run past its keep folds, one under it does not, a code block
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

    /// <summary>
    /// A picture's lines carry its tiles' columns (later on 2026-09-24): a click finds the tile under it, not the gap, not another line.
    /// Narrower than the strip, its lines stay one row each, cut at the edge (2026-10-29), so the tile still answers.
    /// </summary>
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
        Assert.Equal(new[] { "text", "aaaa ", "aaaa ", "after" }, Texts(store.Rows(5)));   // narrower than the strip: cut, one row each
        Assert.Equal(7, store.PictureAt(1, 3));
    }

    // ── Diffs (2026-10-04) ──────────────────────────────────────────────────

    /// <summary>An edit's write as <see cref="DiffView"/> makes it: the note, the elbow (the fold's head, line 1), then <paramref name="rows"/> rows.</summary>
    private static List<Segment> DiffWrite(int rows, string note = "  N")
    {
        var lines = new List<string> { note + "\n", "  └ H\n" };
        for (int i = 1; i <= rows; i++)
        {
            lines.Add($"    r{i}\n");
        }

        return Segments([.. lines]);
    }

    private static Scrollback.FoldSpec Fold(int keep, int size) => new(1, keep, size, Segments("  ▸ F"), Segments("  ▾ O"));

    [Fact]
    public void Diff_InARun_ShowsOpenWhileTheRunGoesOn_FoldsWhenItEnds_AndTogglesOpen()
    {
        var store = new Scrollback();
        store.BeginGroup(0);
        store.Append(DiffWrite(3), 40, member: true, null, Fold(keep: 2, size: 3));
        store.Append(Segments("  m2\n"), 40, member: true);   // the next tool line: the run goes on, the diff stays open

        Assert.Equal(new[] { "  N", "  └ H", "    r1", "    r2", "    r3", "  m2" }, Texts(store.Rows(40)));
        Assert.False(store.AnyFolds);
        Assert.Null(store.GroupAtRow(1));   // its head is only a row while it is live

        store.Append(Segments("reply\n"), 40);   // the run ends: the diff folds
        Assert.Equal(new[] { "  N", "  ▸ F", "  m2", "reply" }, Texts(store.Rows(40)));
        Assert.True(store.TakeReshaped());
        Assert.True(store.AnyFolds);

        int id = Assert.NotNull(store.GroupAtRow(1));
        Assert.Null(store.GroupAtRow(0));
        Assert.True(store.Toggle(id));
        Assert.Equal(new[] { "  N", "  ▾ O", "    r1", "    r2", "    r3", "  m2", "reply" }, Texts(store.Rows(40)));
        Assert.True(store.Toggle(id));
        Assert.Equal(new[] { "  N", "  ▸ F", "  m2", "reply" }, Texts(store.Rows(40)));
    }

    [Fact]
    public void Diff_WithinItsKeep_NeverFolds_AndItsHeadTogglesNothing()
    {
        var store = new Scrollback();
        store.BeginGroup(0);
        store.Append(DiffWrite(2), 40, member: true, null, Fold(keep: 2, size: 2));
        store.EndGroup();

        Assert.Equal(new[] { "  N", "  └ H", "    r1", "    r2" }, Texts(store.Rows(40)));
        Assert.Null(store.GroupAtRow(1));
        Assert.False(store.AnyFolds);
    }

    [Fact]
    public void Diff_WithNoRun_FoldsAtTheNextWrite_AnyKind()
    {
        var store = new Scrollback();
        store.Append(Segments("● "), 40);   // the reply's bare glyph, an open line the note continues: the head is still line 1
        store.Append(DiffWrite(3, note: "N"), 40, member: false, null, Fold(keep: 1, size: 3));
        Assert.Equal(new[] { "● N", "  └ H", "    r1", "    r2", "    r3" }, Texts(store.Rows(40)));

        store.Append(DiffWrite(2, note: "  N2"), 40, member: false, null, Fold(keep: 1, size: 2));
        Assert.Equal(new[] { "● N", "  ▸ F", "  N2", "  └ H", "    r1", "    r2" }, Texts(store.Rows(40)));

        store.Append(Segments("  m\n"), 40, member: true);   // a run's first line ends the diff before it too
        Assert.Equal(new[] { "● N", "  ▸ F", "  N2", "  ▸ F", "  m" }, Texts(store.Rows(40)));
    }

    [Fact]
    public void Diff_UnfoldsWithTheRuns_OnExpandAll_AndARunFoldedOverItHidesItWhole()
    {
        var store = new Scrollback();
        store.BeginGroup(1);
        store.SetGroupSummary(Segments("  S"), Segments("  E"));
        store.Append(DiffWrite(3), 40, member: true, null, Fold(keep: 1, size: 3));
        store.Append(Segments("  m2\n"), 40, member: true);
        store.EndGroup();

        Assert.Equal(new[] { "  S" }, Texts(store.Rows(40)));   // the run folds over it

        int run = store.GroupAtRow(0)!.Value;
        store.Toggle(run);
        Assert.Equal(new[] { "  E", "  N", "  ▸ F", "  m2" }, Texts(store.Rows(40)));   // the run open, the diff still folded

        store.SetAllExpanded(true);
        Assert.Equal(new[] { "  E", "  N", "  ▾ O", "    r1", "    r2", "    r3", "  m2" }, Texts(store.Rows(40)));
        store.SetAllExpanded(false);
        Assert.Equal(new[] { "  S" }, Texts(store.Rows(40)));
    }

    [Fact]
    public void Diff_WithNoRun_GoesWholeOnTrim_AndClearForgetsIt()
    {
        var store = new Scrollback();
        store.Append(Segments("a\n"), 40);
        store.Append(DiffWrite(Scrollback.MaxRows), 40, member: false, null, Fold(keep: 1, size: Scrollback.MaxRows));
        store.Append(Segments("after\n"), 40);   // folds it; the open rows were past the cap all the same

        // Past the cap the oldest lines go: "a", the note, then the diff whole, its summary with its rows.
        Assert.Equal(new[] { "after" }, Texts(store.Rows(40)));
        Assert.False(store.AnyFolds);

        store.Append(DiffWrite(3), 40, member: false, null, Fold(keep: 1, size: 3));
        store.Clear();
        store.Append(Segments("x\n"), 40);
        Assert.Equal(new[] { "x" }, Texts(store.Rows(40)));
        Assert.False(store.AnyFolds);
    }

    // ── A mark and what was written since (2026-10-11: the welcome splash drawn first, fitted again over the startup's lines) ──

    [Fact]
    public void Since_AClosedMark_CopiesTheLinesAfterIt_AsOneWriteThatPutsThemBack()
    {
        var store = new Scrollback();
        store.Append(Segments("banner\n"), 10);
        var mark = store.Mark;
        store.Append(Segments("one two three\n", "four\n"), 10);

        var copied = Assert.IsType<Scrollback.Copied>(store.Since(mark, 10));
        Assert.Equal(3, copied.Rows);   // "one two th" + "ree" + "four"

        var again = new Scrollback();
        again.Append(Segments("banner\n"), 10);
        again.Append(copied.Segments, 10);
        Assert.Equal(Texts(store.Rows(10)), Texts(again.Rows(10)));
    }

    [Fact]
    public void Since_AMarkInsideAnOpenLine_CopiesWhatJoinedIt_AndTheRowsItAdded()
    {
        // A picture's write leaves its last row open; the next write joins it.
        var store = new Scrollback();
        store.Append(Segments("0123456789"), 10);
        var mark = store.Mark;
        store.Append(Segments("  error\n"), 10);

        var copied = Assert.IsType<Scrollback.Copied>(store.Since(mark, 10));
        Assert.Equal(1, copied.Rows);
        Assert.Equal("  error", string.Concat(copied.Segments.Where(s => !s.IsLineBreak).Select(s => s.Text)));

        var again = new Scrollback();
        again.Append(Segments("ABCDEFGHIJ"), 10);
        again.Append(copied.Segments, 10);
        Assert.Equal(new[] { "ABCDEFGHIJ", "  error" }, Texts(again.Rows(10)));
    }

    [Fact]
    public void Since_IsNull_WithNothingToSee_AMarkPastTheEnd_OrALineThatFolds()
    {
        var store = new Scrollback();
        store.Append(Segments("0123456789"), 10);
        var mark = store.Mark;
        Assert.Null(store.Since(mark, 10));   // nothing written
        store.Append(Segments("\n", "   \n"), 10);
        Assert.Null(store.Since(mark, 10));   // the break that closed the row and a blank line
        Assert.Null(new Scrollback().Since(mark, 10));   // a store that no longer holds the mark

        store.BeginGroup(1);
        store.Append(Segments("tool line\n"), 10, member: true);
        Assert.Null(store.Since(mark, 10));   // a tool run's line does not survive a copy
    }

    // ── A resize (2026-10-29) ───────────────────────────────────────────────

    /// <summary><paramref name="renderable"/> rendered at <paramref name="width"/> as the pane's render does (the console's capabilities, the width as the room).</summary>
    private static List<Segment> RenderAt(IRenderable renderable, int width, bool unicode = true)
    {
        using var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Capabilities.Unicode = unicode;
        return renderable.Render(new RenderOptions(console.Profile.Capabilities, new Size(width, 20)), width).ToList();
    }

    /// <summary>A store that renders its sources again (<see cref="Scrollback.Render"/>), as the pane's does.</summary>
    private static Scrollback Reflowing() => new() { Render = (renderable, width) => RenderAt(renderable, width) };

    /// <summary>A renderable that gives what <paramref name="render"/> says at each width.</summary>
    private sealed class Sized(Func<int, List<Segment>> render) : IRenderable
    {
        public int Renders { get; private set; }

        public Measurement Measure(RenderOptions options, int maxWidth) => new(1, maxWidth);

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
        {
            Renders++;
            return render(maxWidth);
        }
    }

    /// <summary>Two lines narrower than 10 cells, one wider: a source whose re-render shows.</summary>
    private static Sized TwoWhenNarrow(string end = "\n") => new(w => w < 10 ? Segments("x\ny" + end) : Segments("xy" + end));

    [Fact]
    public void Narrower_DropsTheBlanksAndRuleThatOverflow_AndWrapsAtWords_UnderTheIndent()
    {
        var store = new Scrollback();
        store.Append([new Segment("abc"), new Segment(new string(' ', 12), new Style(background: Color.Blue)), Segment.LineBreak], 15);
        store.Append(Segments(new string(ScreenPane.RuleGlyph, 15) + "\n"), 15);
        store.Append(Segments("  one two three\n"), 15);
        store.Append(Segments("abcdefghijklmno\n"), 15);
        string[] written = ["abc" + new string(' ', 12), new string(ScreenPane.RuleGlyph, 15), "  one two three", "abcdefghijklmno"];
        Assert.Equal(written, Texts(store.Rows(15)));

        // The slab ends at the edge, the rule spans it, a word goes whole to the next row under the indent, a row with no space cuts.
        Assert.Equal(["abc       ", new string(ScreenPane.RuleGlyph, 10), "  one two", "  three", "abcdefghij", "klmno"], Texts(store.Rows(10)));
        Assert.Equal(written, Texts(store.Rows(20)));   // wider: as written
        Assert.Equal(written, Texts(store.Rows(15)));   // and exactly the rows again at its own width
    }

    [Fact]
    public void Narrower_TheOpenLastLine_KeepsTheExactCut()
    {
        var store = new Scrollback();
        store.Append(Segments("  one two three"), 15);
        Assert.Equal(["  one two ", "three"], Texts(store.Rows(10)));   // the next write joins it where the screen left off
    }

    [Fact]
    public void ARunsSummary_SetAtOneWidth_WrapsAtWordsNarrower_OneSetBeforeAnyWidth_KeepsTheExactCut()
    {
        var store = new Scrollback();
        store.Append(Segments("before\n"), 30);
        store.BeginGroup(1);
        store.Append(Segments("  m1\n"), 30, member: true);
        store.Append(Segments("  m2\n"), 30, member: true);
        store.SetGroupSummary(Segments("  > tools read_file write_file"), Segments("  v tools read_file write_file"));
        store.EndGroup();
        Assert.Equal(["before", "  > tools read_file write_file"], Texts(store.Rows(30)));
        Assert.Equal(["before", "  > tools read_file", "    write_file"], Texts(store.Rows(20)));   // under the text after its marker

        var early = new Scrollback();
        early.BeginGroup(1);
        early.SetGroupSummary(Segments("  > tools read_file write_file"), Segments("  v tools read_file write_file"));
        early.Append(Segments("  m1\n"), 30, member: true);
        early.Append(Segments("  m2\n"), 30, member: true);
        early.EndGroup();
        Assert.Equal(["  > tools read_file ", "write_file"], Texts(early.Rows(20)));
    }

    /// <summary>
    /// A centered picture narrowed (<see cref="CenteredPicture"/>, Spectre's <see cref="Canvas"/>): every line stays one row and loses
    /// only its padding first — every pixel cell is still there — in both of the canvas's looks: half blocks, or background-colored
    /// spaces without Unicode. A click still finds it where it is drawn now.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACenteredPicture_Narrowed_LosesItsPaddingFirst_AndAClickStillFindsIt(bool unicode)
    {
        var picture = new CenteredPicture(new ImageThumbnail(4, 4, Enumerable.Repeat(Color.Red, 16).ToArray()), 9);
        var segments = RenderAt(picture, 20, unicode);
        var store = new Scrollback();
        store.Append(segments, 20, member: false, picture.Spans);
        store.Append(Segments("\n"), 20);   // its last row closed, as the transcript's next line does
        int cells = unicode ? 4 : 8;   // a pixel column is a half block, or two spaces

        var rows = store.Rows(cells);
        Assert.Equal(picture.Spans.Count, rows.Count);
        foreach (var row in rows)
        {
            var drawn = row.SelectMany(s => s.Text.Select(c => (Char: c, s.Style))).ToList();
            Assert.Equal(cells, drawn.Count);
            Assert.All(drawn, cell => Assert.True(cell.Char != ' ' || cell.Style.Background != Color.Default));
        }

        Assert.Equal(9, store.PictureAt(0, 2));
    }

    [Fact]
    public void ASource_IsRenderedAgainAtANewWidth_AndIsItsOwnSegmentsAtItsWidth()
    {
        var store = Reflowing();
        var source = new Markup("one two three four five six seven" + Environment.NewLine);
        store.Append(RenderAt(source, 40), 40, member: false, null, null, source);
        store.Append(Segments("after\n"), 40);
        var mark = store.Mark;
        var written = Texts(store.Rows(40));
        Assert.Equal(["one two three four five six seven", "after"], written.Select(t => t.TrimEnd()));

        Assert.Equal(["one two three", "four five six", "seven", "after"], Texts(store.Rows(14)).Select(t => t.TrimEnd()));
        Assert.Equal(2, store.LineCount);   // the line count never changes: marks and groups read the store as before
        Assert.Equal(mark, store.Mark);
        Assert.Equal(written, Texts(store.Rows(40)));
    }

    /// <summary>A <c>MarkupLine</c>'s shape — <c>\n</c> inside, <c>\r\n</c> at the end — splits as an append splits: no <c>\r</c> cell, no blank row after.</summary>
    [Fact]
    public void ASource_ThatEndsWithACarriageReturnAndBreak_RendersNoStrayCellOrRow()
    {
        var store = Reflowing();
        var source = TwoWhenNarrow("\r\n");
        store.Append(Segments("xy\r\n"), 20, member: false, null, null, source);
        Assert.Equal(["xy"], Texts(store.Rows(20)));
        Assert.Equal(["x", "y"], Texts(store.Rows(5)));

        // One other width's rows are kept while laid out there; none at its own width (the resize review), so going home and back
        // renders once more.
        int renders = source.Renders;
        store.Rows(5);
        Assert.Equal(renders, source.Renders);
        store.Rows(20);
        store.Rows(5);
        Assert.Equal(renders + 1, source.Renders);
    }

    [Fact]
    public void AWaitingSource_IsKeptByABareBreak_AndDroppedByAnythingElse()
    {
        var store = Reflowing();
        store.Append(Segments("xy"), 20, member: false, null, null, TwoWhenNarrow(""));
        Assert.Equal(["xy"], Texts(store.Rows(5)));   // open: the exact cut, unused while it waits
        store.Append(Segments("\r\n"), 20);
        Assert.Equal(["x", "y"], Texts(store.Rows(5)));

        store = Reflowing();
        store.Append(Segments("xy"), 20, member: false, null, null, TwoWhenNarrow(""));
        store.Append(Segments("z\n"), 20);
        Assert.Equal(["xyz"], Texts(store.Rows(5)));

        store = Reflowing();
        store.Append(Segments("xy"), 20, member: false, null, null, TwoWhenNarrow(""));
        store.BeginGroup(0);
        Assert.Equal(["xy"], Texts(store.Rows(5)));

        store = Reflowing();
        store.Append(Segments("xy"), 20, member: false, null, null, TwoWhenNarrow(""));
        store.BeginGroup(0, Segments("•"), absorbOpenLine: true);
        Assert.Empty(store.Rows(5));

        store = Reflowing();
        store.Append(Segments("xy"), 20, member: false, null, null, TwoWhenNarrow(""));
        store.BeginCodeGroup(0, Segments("lbl"));
        Assert.Equal(["xy", "lbl"], Texts(store.Rows(5)));
    }

    [Fact]
    public void ASource_IsKeptOnlyOnAStandaloneWrite()
    {
        var store = Reflowing();
        store.Append(Segments("open"), 20);
        store.Append(Segments("xy\n"), 20, member: false, null, null, TwoWhenNarrow());   // joins an open line
        store.BeginGroup(0);
        store.Append(Segments("xy\n"), 20, member: true, null, null, TwoWhenNarrow());    // a tool run's line
        store.EndGroup();
        IReadOnlyList<PictureSpan> none = [];
        store.Append(Segments("xy\n"), 20, member: false, [none], null, TwoWhenNarrow());  // pictures
        Assert.Equal(["openxy", "xy", "xy"], Texts(store.Rows(9)));
    }

    /// <summary>
    /// A source the render budget does not reach wraps on its own at that width until <see cref="Scrollback.RenderPending"/> renders it
    /// there — at least one per call, the budget spent or not — with the held places told before and after (the resize review, R4).
    /// </summary>
    [Fact]
    public void ASource_PastTheRenderBudget_WrapsOnItsOwn_UntilRenderPending()
    {
        var store = Reflowing();
        var source = new Sized(w => w < 10 ? Segments("AB\nCD\n") : Segments("ab cd\n"));
        store.Append(Segments("ab cd\n"), 20, member: false, null, null, source);
        store.RenderBudget = TimeSpan.Zero;
        Assert.Equal(["ab", "cd"], Texts(store.Rows(4)));   // the narrower rule: at the word
        Assert.Equal(0, source.Renders);
        Assert.True(store.Pending);

        int relaying = 0;
        int relaid = 0;
        store.Relaying = () => relaying++;
        store.Relaid = _ => relaid++;
        Assert.True(store.RenderPending());
        Assert.Equal(["AB", "CD"], Texts(store.Rows(4)));
        Assert.Equal((1, 1, 1), (source.Renders, relaying, relaid));
        Assert.False(store.Pending);
        Assert.False(store.RenderPending());   // nothing left: no re-layout
        Assert.Equal(1, relaid);
    }

    [Fact]
    public void ASourceWhoseRenderFails_IsDroppedForGood_ItsLinesWrapOnTheirOwn()
    {
        var store = new Scrollback { Render = (_, _) => null };
        store.Append(Segments("xy\n"), 20, member: false, null, null, TwoWhenNarrow());
        Assert.Equal(["xy"], Texts(store.Rows(5)));
        store.Render = (renderable, width) => RenderAt(renderable, width);
        Assert.Equal(["xy"], Texts(store.Rows(6)));
    }

    [Fact]
    public void TheCap_DropsASourceWhole_AndCountsItsRows()
    {
        var store = Reflowing();
        store.Append(Segments("a\nb\nc\n"), 20, member: false, null, null, new Sized(w => w < 10 ? Segments("A\nB\nC\nD\n") : Segments("a\nb\nc\n")));
        Assert.Equal(["A", "B", "C", "D"], Texts(store.Rows(5)));
        Assert.Equal(1, store.LineCount);   // one write, one line (the resize review)

        // One row past the cap: the write goes whole, the rows dropped are the four it took.
        int dropped = 0;
        for (int i = 0; i < Scrollback.MaxRows - 3; i++)
        {
            dropped += store.Append(Segments("x\n"), 5);
        }

        Assert.Equal(4, dropped);
        Assert.Equal(["x", "x"], Texts(store.Rows(5)).Take(2));
        Assert.Equal(Scrollback.MaxRows - 3, store.Rows(20).Count);
    }

    [Fact]
    public void Since_CarriesASourceWithItsLines()
    {
        var store = Reflowing();
        store.Append(Segments("start\n"), 20);
        var mark = store.Mark;
        var source = TwoWhenNarrow();
        store.Append(Segments("xy\n"), 20, member: false, null, null, source);
        store.Append(Segments("plain\n"), 20);

        var copied = store.Since(mark, 20)!;
        Assert.Equal(2, copied.Chunks.Count);
        Assert.Same(source, copied.Chunks[0].Source);
        Assert.Equal("xy", string.Concat(copied.Chunks[0].Segments.Where(s => !s.IsLineBreak).Select(s => s.Text)));
        Assert.Null(copied.Chunks[1].Source);
        Assert.Equal(copied.Segments.Count, copied.Chunks.Sum(c => c.Segments.Count));
        Assert.Equal(20, copied.Chunks[0].Born);
    }

    // ── The resize review (2026-10-29) ──────────────────────────────────────

    [Fact]
    public void ATwoLineSource_IsOneLine_AndAFoldBelowItKeepsItsRows()
    {
        var store = Reflowing();
        var source = new Markup("one two three four" + Environment.NewLine + "five six seven eight" + Environment.NewLine);
        store.Append(RenderAt(source, 40), 40, member: false, null, null, source);
        Assert.Equal(1, store.LineCount);
        store.BeginGroup(1);
        store.Append(Segments("  m1\n"), 40, member: true);
        store.Append(Segments("  m2\n"), 40, member: true);
        store.SetGroupSummary(Segments("  > two"), Segments("  v two"));
        store.EndGroup();

        var narrow = Texts(store.Rows(10));
        Assert.Equal(["one two", "three four", "five six", "seven", "eight"], narrow.Take(5).Select(t => t.TrimEnd()));
        int id = store.GroupAtRow(narrow.Length - 1)!.Value;
        Assert.True(store.Toggle(id));
        Assert.True(store.Toggle(id));
        Assert.Equal(narrow, Texts(store.Rows(10)));   // the partial re-layout from the summary on loses none of the write's rows
    }

    [Fact]
    public void AWaitingSource_ClosedByAMembersBreak_IsNotKept_AControlCodeLeavesItWaiting()
    {
        var store = Reflowing();
        store.Append(Segments("xy"), 20, member: false, null, null, TwoWhenNarrow(""));
        store.Append(Segments("\n"), 20, member: true);
        Assert.Equal("xy", Texts(store.Rows(5))[0]);

        store = Reflowing();
        store.Append(Segments("xy"), 20, member: false, null, null, TwoWhenNarrow(""));
        store.Append([Segment.Control("\u001b[0m")], 20);
        store.Append(Segments("\n"), 20);
        Assert.Equal(["x", "y"], Texts(store.Rows(5)));
    }

    [Fact]
    public void ABlankWrite_AndOnePastTheLineCap_KeepNoSource()
    {
        var store = Reflowing();
        var blank = new Sized(_ => Segments("   \n"));
        store.Append(Segments("   \n"), 20, member: false, null, null, blank);
        store.Rows(5);
        Assert.Equal(0, blank.Renders);

        var lines = string.Concat(Enumerable.Repeat("a\n", Scrollback.MaxSourceLines + 1));
        var big = new Sized(_ => Segments(lines));
        store.Append(Segments(lines), 20, member: false, null, null, big);
        Assert.Equal(Scrollback.MaxSourceLines + 2, store.LineCount);   // plain lines
        store.Rows(6);
        Assert.Equal(0, big.Renders);
    }

    [Fact]
    public void AnAnchor_InTheMiddleOfALine_FollowsANarrowingAndAWidening()
    {
        var store = new Scrollback();
        string words = string.Join(' ', Enumerable.Range(1, 40).Select(i => "w" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)));
        store.Append(Segments("before\n", words + "\n", "after\n"), 80);

        (int Row, int Start) Find(IReadOnlyList<SegmentLine> rows, string word)
        {
            for (int r = 0; r < rows.Count; r++)
            {
                int at = Text(rows[r]).IndexOf(word, StringComparison.Ordinal);
                if (at >= 0)
                {
                    return (r, at);
                }
            }

            return (-1, -1);
        }

        var place = Find(store.Rows(80), "w20");
        var anchor = store.Locate(place.Row, place.Start)!;
        foreach (int width in (int[])[20, 13, 120, 80])
        {
            var rows = store.Rows(width);
            var spot = store.Resolve(anchor);
            Assert.Equal(Find(rows, "w20"), (spot.Row, spot.Start));
            Assert.Equal(1, spot.First);
        }
    }

    [Fact]
    public void Locate_ClampsPastTheEnd_GivesNoneEmpty_AndAClearedLineResolvesNowhere()
    {
        var store = new Scrollback();
        Assert.Null(store.Locate(0, 0));
        store.Append(Segments("one\n", "two\n"), 20);
        store.Rows(20);
        var last = store.Locate(99, 0)!;
        Assert.Equal(1, store.Resolve(last).Row);

        store.Clear();
        store.Append(Segments("new\n"), 20);
        store.Rows(20);
        Assert.Equal(-1, store.Resolve(last).Start);
    }

    [Fact]
    public void ARender_ThatLosesLetters_WrapsTheWrittenLines_AndIsNotTriedAgainThere()
    {
        var store = Reflowing();
        var cropped = new Sized(w => w < 10 ? Segments("ab…\n") : Segments("abcdef\n"));   // a table crops rather than throw
        store.Append(Segments("abcdef\n"), 20, member: false, null, null, cropped);
        Assert.Equal(["abcde", "f"], Texts(store.Rows(5)));
        store.Rows(20);
        store.Rows(5);
        Assert.Equal(2, cropped.Renders);   // home drops the answer with the copy; the same width alone never asks again
        store.Rows(5);
        Assert.Equal(2, cropped.Renders);
    }

    [Fact]
    public void NotNow_WrapsTheSourceThisTime_AndKeepsItForRenderPending()
    {
        var store = new Scrollback { Render = (_, _) => Scrollback.NotNow };
        var source = TwoWhenNarrow();
        store.Append(Segments("xy\n"), 20, member: false, null, null, source);
        Assert.Equal(["xy"], Texts(store.Rows(5)));
        store.Render = (renderable, width) => RenderAt(renderable, width);
        store.MarkPending();
        Assert.True(store.RenderPending());
        Assert.Equal(["x", "y"], Texts(store.Rows(5)));
    }

    [Fact]
    public void Since_ALineLaidOutNarrowerThanWritten_CopiesNothing_AndAMarkPastAClearIsGone()
    {
        var store = new Scrollback();
        var mark = store.Mark;
        store.Append(Segments("aaa bbb ccc\n"), 20);
        Assert.NotNull(store.Since(mark, 20));
        Assert.Null(store.Since(mark, 5));

        var cleared = store.Mark;
        store.Clear();
        store.Append(Segments("new\n"), 20);
        Assert.Null(store.Since(cleared, 20));
        Assert.Null(store.Since(mark, 20));
    }

    [Fact]
    public void Narrower_KeepsAJoinerInAnotherSegment_WithItsEmoji()
    {
        var store = new Scrollback();
        store.Append([new Segment("ab 👍"), new Segment("️", new Style(Color.Red)), new Segment(" cd ef\n")], 20);
        var rows = Texts(store.Rows(6));
        Assert.All(rows, row => Assert.False(row.StartsWith('️')));
        Assert.Contains(rows, row => row.Contains("👍️", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("• one two three four", "• one two", "  three four")]
    [InlineData("▎ one two three four", "▎ one two", "▎ three four")]
    [InlineData("12. one two three", "12. one", "    two")]
    [InlineData("The one two three", "The one two", "three")]
    [InlineData("a\tbb cc dd", "a bb", "cc dd")]
    public void Narrower_HangsUnderAMarker_RepeatsABar_AndDrawsATabAsASpace(string line, string first, string second)
    {
        var store = new Scrollback();
        store.Append(Segments(line + "\n"), 30);
        int width = line.Contains('\t') ? 6 : line.StartsWith("12", StringComparison.Ordinal) ? 10 : 12;
        Assert.Equal([first, second], Texts(store.Rows(width)).Take(2));
    }

    [Fact]
    public void Narrower_AtTwoCells_KeepsAnEmojiWithNoBlankRow()
    {
        var store = new Scrollback();
        store.Append(Segments("👍 ok\n"), 20);
        Assert.Equal(["👍", "ok"], Texts(store.Rows(2)));
    }

    [Fact]
    public void ANarrowedPicture_ShiftsEveryRowAlike()
    {
        var pixel = new Style(background: Color.Red);
        var segments = new List<Segment>
        {
            new("  "), new("    ", pixel), Segment.LineBreak,
            new("    "), new("    ", pixel),
        };
        IReadOnlyList<IReadOnlyList<PictureSpan>> spans = [[new PictureSpan(2, 4, 9)], [new PictureSpan(4, 4, 9)]];
        var store = new Scrollback();
        store.Append(segments, 10, member: false, spans);
        store.Append(Segments("\n"), 10);
        Assert.Equal(["    ", "      "], Texts(store.Rows(6)));   // both lose two blanks: the picture keeps its shape
        Assert.Equal(9, store.PictureAt(0, 0));
        Assert.Equal(9, store.PictureAt(1, 2));
        Assert.Null(store.PictureAt(1, 1));
    }

    [Fact]
    public void Fit_IsTheStoresNarrowerRule()
    {
        var line = Segments("  one two three");
        Assert.Equal(["  one two", "  three"], Texts(Scrollback.Fit(line, 10, 15)));
        Assert.Equal(["  one two ", "three"], Texts(Scrollback.Fit(line, 10, 0)));   // no born width: the exact cut
    }
}
