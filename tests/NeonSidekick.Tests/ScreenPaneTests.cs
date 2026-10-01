using NeonSidekick.Tests.Fakes;
using System.Globalization;
using NeonSidekick.UI;
using NeonSidekick.UI.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class ScreenPaneTests : IDisposable
{
    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly ManualTimeProvider _time = new();
    private int? _cursorRow;
    private int? _cursorTop;

    public ScreenPaneTests()
    {
        _console.Profile.Width = 40;
        _console.Profile.Height = 10;
    }

    public void Dispose() => _console.Dispose();

    private ScreenPane Pane(bool geometry = true) =>
        new(_console, geometry ? new ScreenGeometry(() => _cursorRow, () => _cursorTop) : null, _time);

    private static string Rule(int width) => new(ScreenPane.RuleGlyph, width);

    private string Output => _console.Output;

    /// <summary>How many times the pane was drawn: two rules per draw.</summary>
    private int Draws => Count(Output, Rule(40)) / 2;

    // ── Disabled ────────────────────────────────────────────────────────────

    [Fact]
    public void WithoutGeometry_IsAPassThrough()
    {
        using var pane = Pane(geometry: false);
        Assert.False(pane.Enabled);

        pane.Show();
        pane.Write(new Markup("hello\n"));
        pane.BeginInput();
        pane.ShowInput("ab", 2);
        pane.CommitInput("ab");
        using (pane.BeginBusy("thinking"))
        {
        }

        Assert.Equal("hello\n› ab› ab\n", Output);
        Assert.Equal(0, _time.TimerCount);
    }

    [Fact]
    public void WithoutMenus_IsAPassThrough_EvenWithGeometry()
    {
        using var plain = new TestConsole();
        plain.Profile.Capabilities.Interactive = false;
        using var pane = new ScreenPane(plain, new ScreenGeometry(() => 0), _time);
        Assert.False(pane.Enabled);
    }

    // ── Geometry ────────────────────────────────────────────────────────────

    [Fact]
    public void Show_PadsTheTranscriptDownToTheLastFourRows()
    {
        using var pane = Pane();
        pane.Show();

        // 10 rows, the flow at row 0: six empty rows, then rule / input / rule / hint.
        Assert.Equal(6, pane.Padding);
        Assert.Equal(new string('\n', 6) + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output);
    }

    // ── The upper rule's title ──────────────────────────────────────────────

    /// <summary>The session's name at the right edge of the rule above the input row (2026-09-18, the user's picture): a space either side, one glyph at the edge, the title cut ahead of the eight glyphs on the left.</summary>
    [Fact]
    public void RuleWithTitle_IsPinned()
    {
        Assert.Equal(8, ScreenPane.RuleTitleMinRule);
        Assert.Equal(Rule(40), ScreenPane.RuleWithTitle("", 40));
        Assert.Equal(Rule(32) + " notes ─", ScreenPane.RuleWithTitle("notes", 40));
        Assert.Equal(Rule(14) + " settings-layout-reorder ─", ScreenPane.RuleWithTitle("settings-layout-reorder", 40));
        Assert.Equal(Rule(8) + " a-title-longer-than-the-rule… ─", ScreenPane.RuleWithTitle("a-title-longer-than-the-rule-can-hold", 40));   // cut ahead of the eight glyphs
        Assert.Equal(Rule(8) + " 日本 ─", ScreenPane.RuleWithTitle("日本", 15));   // two-cell characters count as cells
        Assert.Equal(Rule(11), ScreenPane.RuleWithTitle("notes", 11));            // eight glyphs, a space, a cell, a space, a glyph = twelve: no room, the bare rule
        Assert.Equal(Rule(8) + " … ─", ScreenPane.RuleWithTitle("notes", 12));   // one cell of room: the ellipsis
        Assert.Equal("", ScreenPane.RuleWithTitle("notes", 0));
        Assert.Equal(40, TextCells.Width(ScreenPane.RuleWithTitle("a-title-longer-than-the-rule-can-hold", 40)));
    }

    /// <summary>
    /// The picture strip's rule (2026-09-28, the user's layout): the viewer's button at the left and the close × at the
    /// right, a space cell either side of each (the space at the button's left went later that day and came back later
    /// still, the user's call); no button, the × alone; too narrow, the button goes, then the ×.
    /// </summary>
    [Fact]
    public void StripRule_IsPinned()
    {
        const string film = "\U0001F39E️";
        var parts = ScreenPane.StripRule(film, 40);
        Assert.Equal("─ " + film + " " + Rule(31) + " × ─", parts.Text);
        Assert.Equal(40, TextCells.Width(parts.Text));
        Assert.Equal((2, 37), (parts.ButtonColumn, parts.CloseColumn));
        Assert.Equal((film, "×"), (parts.Button, parts.Close));
        Assert.Equal("─ ", parts.Lead);          // a space cell left of the glyph…
        Assert.StartsWith(" ", parts.Fill);      // …one right of it
        Assert.EndsWith(" ", parts.Fill);        // a space either side of the ×
        Assert.StartsWith(" ", parts.Tail);

        Assert.Equal(Rule(36) + " × ─", ScreenPane.StripRule(null, 40).Text);
        Assert.Equal(-1, ScreenPane.StripRule("", 40).ButtonColumn);
        Assert.Equal(36 + 1, ScreenPane.StripRule("", 40).CloseColumn);

        Assert.Equal("─ " + film + " ─ × ─", ScreenPane.StripRule(film, 10).Text);   // one rule glyph between: the least
        Assert.Equal(Rule(5) + " × ─", ScreenPane.StripRule(film, 9).Text);         // none: the button goes
        Assert.Equal("─ × ─", ScreenPane.StripRule(film, 5).Text);
        var bare = ScreenPane.StripRule(film, 4);
        Assert.Equal((Rule(4), -1, -1), (bare.Text, bare.ButtonColumn, bare.CloseColumn));
    }

    [Fact]
    public void RuleTitle_IsDrawnOnTheUpperRule_AndFollowsOnTheTick()
    {
        string title = "";
        using var pane = Pane();
        pane.RuleTitle = () => title;
        pane.Show();
        Assert.EndsWith(Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output);

        // The name lands (from the pool, in the app): the tick repaints the pane, the upper rule titled, the lower one bare.
        title = "notes";
        int mark = Output.Length;
        _time.Advance(ScreenPane.Tick);
        Assert.Contains(ScreenPane.RuleWithTitle("notes", 40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output[mark..]);

        // The same title again: nothing redrawn.
        mark = Output.Length;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal("", Output[mark..]);

        // Gone (a /clear in the app): the bare rule again.
        title = "";
        _time.Advance(ScreenPane.Tick);
        Assert.EndsWith(Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output);
    }

    /// <summary>
    /// The upper rule's ⤡ (2026-09-29, the user's glyph, in place of the ↘️↖️ pair): after the rule's first glyph with a
    /// space either side, then the titled rule over the rest; without folds, or too narrow to keep the title's least rule
    /// after it, the rule as before.
    /// </summary>
    [Fact]
    public void UpperRule_IsPinned()
    {
        Assert.Equal("⤡", ScreenPane.FoldGlyph);
        Assert.Equal(1, TextCells.Width(ScreenPane.FoldGlyph));
        var parts = ScreenPane.UpperRule("", folds: true, 40);
        Assert.Equal("─ ⤡ " + Rule(36), parts.Text);
        Assert.Equal(40, TextCells.Width(parts.Text));
        Assert.Equal(("─ ", "⤡ "), (parts.Lead, parts.Button));
        Assert.Equal(2, parts.FoldColumn);

        Assert.Equal("─ ⤡ " + ScreenPane.RuleWithTitle("notes", 36), ScreenPane.UpperRule("notes", folds: true, 40).Text);

        var plain = ScreenPane.UpperRule("notes", folds: false, 40);
        Assert.Equal((ScreenPane.RuleWithTitle("notes", 40), -1), (plain.Text, plain.FoldColumn));

        Assert.Equal("─ ⤡ " + Rule(ScreenPane.RuleTitleMinRule), ScreenPane.UpperRule("", folds: true, 4 + ScreenPane.RuleTitleMinRule).Text);
        var narrow = ScreenPane.UpperRule("", folds: true, 3 + ScreenPane.RuleTitleMinRule);   // the button goes
        Assert.Equal((Rule(3 + ScreenPane.RuleTitleMinRule), -1), (narrow.Text, narrow.FoldColumn));
    }

    /// <summary>
    /// Where the upper rule's title lands (2026-09-28, for the double-click that renames the session): the column after
    /// <see cref="ScreenPane.RuleWithTitle"/>'s space, the same with the fold buttons or without (the title keeps to the
    /// right edge), the cut title's cells; none for an empty title or a width with no room for one.
    /// </summary>
    [Fact]
    public void UpperRule_PlacesTheTitle()
    {
        var plain = ScreenPane.UpperRule("notes", folds: false, 40);
        Assert.Equal((33, 5), (plain.TitleColumn, plain.TitleCells));
        Assert.Equal("notes", plain.Text.Substring(plain.TitleColumn, plain.TitleCells));

        var folded = ScreenPane.UpperRule("notes", folds: true, 40);
        Assert.Equal((33, 5), (folded.TitleColumn, folded.TitleCells));

        var cut = ScreenPane.UpperRule("a-title-longer-than-the-rule-can-hold", folds: false, 40);
        Assert.Equal((ScreenPane.RuleTitleMinRule + 1, 29), (cut.TitleColumn, cut.TitleCells));

        var wide = ScreenPane.UpperRule("日本", folds: false, 15);   // two-cell characters count as cells
        Assert.Equal((ScreenPane.RuleTitleMinRule + 1, 4), (wide.TitleColumn, wide.TitleCells));

        Assert.Equal((-1, 0), (ScreenPane.UpperRule("", folds: false, 40).TitleColumn, ScreenPane.UpperRule("", folds: false, 40).TitleCells));
        Assert.Equal(-1, ScreenPane.UpperRule("", folds: true, 40).TitleColumn);
        Assert.Equal(-1, ScreenPane.UpperRule("notes", folds: false, 11).TitleColumn);   // the bare rule: no room
    }

    /// <summary>
    /// The session's name on the upper rule takes a click (2026-09-28): on its cells and the space either side, nothing
    /// past them or on another row; nothing with no title drawn, and nothing under an overlay.
    /// </summary>
    [Fact]
    public void RuleTitle_TakesAClick_OnItsCellsAndTheSpaceEitherSide()
    {
        _cursorTop = 100;   // the upper rule at 99
        string title = "";
        using var pane = Pane();
        pane.RuleTitle = () => title;
        pane.Show();
        Assert.False(pane.TryHitRuleTitle(35, 99));   // no title yet

        title = "notes";   // columns 33..37 at 40 wide
        _time.Advance(ScreenPane.Tick);
        foreach (int x in new[] { 32, 33, 37, 38 })
        {
            Assert.True(pane.TryHitRuleTitle(x, 99));
        }

        Assert.False(pane.TryHitRuleTitle(31, 99));
        Assert.False(pane.TryHitRuleTitle(39, 99));
        Assert.False(pane.TryHitRuleTitle(35, 98));
        Assert.False(pane.TryHitRuleTitle(35, 100));

        pane.ShowOverlay(new Markup("a"), "ESC closes");
        Assert.False(pane.TryHitRuleTitle(35, 99));
        pane.CloseOverlay();
        Assert.True(pane.TryHitRuleTitle(35, 99));

        title = "";   // gone (a /clear in the app)
        _time.Advance(ScreenPane.Tick);
        Assert.False(pane.TryHitRuleTitle(35, 99));
    }

    /// <summary>
    /// The upper rule's ⤡ (2026-09-28 as ↘️↖️, one button since 2026-09-29): drawn once a run folds, not before; it takes a
    /// click on itself and the space either side; under an overlay it goes, and it comes back when it closes.
    /// </summary>
    [Fact]
    public void FoldButton_ShowsOnceARunFolds_AndTakesAClickOnItselfAndItsSpaces()
    {
        _cursorTop = 100;   // the upper rule at 99
        using var pane = Pane();
        pane.Show();
        Assert.False(pane.TryHitFoldButton(1, 99));   // nothing folds yet
        pane.BeginToolGroup(1);
        pane.SetToolGroupSummary(new Markup("S"), new Markup("E"));
        pane.WriteToolLine(new Markup("m1\n"));
        Assert.False(pane.TryHitFoldButton(1, 99));   // one line: under the keep, drawn as it is

        int mark = Output.Length;
        pane.WriteToolLine(new Markup("m2\n"));   // past the keep: the run folds, and the rule shows it (the redraw, or the tick)
        pane.EndToolGroup();
        _time.Advance(ScreenPane.Tick);
        Assert.Contains(ScreenPane.UpperRule("", folds: true, 40).Text + "\n" + InputLine.PromptGlyph, Output[mark..]);

        foreach (int x in new[] { 1, 2, 3 })   // the space, ⤡, the space
        {
            Assert.True(pane.TryHitFoldButton(x, 99));
        }

        Assert.False(pane.TryHitFoldButton(0, 99));
        Assert.False(pane.TryHitFoldButton(4, 99));
        Assert.False(pane.TryHitFoldButton(2, 98));

        // Unchanged: the tick leaves the rule alone.
        mark = Output.Length;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal("", Output[mark..]);

        pane.ShowOverlay(new Markup("a"), "ESC closes");
        Assert.False(pane.TryHitFoldButton(2, 99));
        pane.CloseOverlay();
        Assert.True(pane.TryHitFoldButton(2, 99));
    }

    [Fact]
    public void ALine_MovesTheFlowDown_AndShrinksThePadding()
    {
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("hello" + Environment.NewLine));

        Assert.Equal(1, pane.FlowRow);
        Assert.Equal(0, pane.FlowColumn);
        Assert.Equal(5, pane.Padding);
        Assert.EndsWith("hello\n" + new string('\n', 5) + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output);
    }

    [Fact]
    public void AMidRowWrite_KeepsItsRow_AndThePaneStartsUnderIt()
    {
        using var pane = Pane();
        pane.Show();
        pane.Write(new RawText("abc"));

        Assert.Equal(0, pane.FlowRow);
        Assert.Equal(3, pane.FlowColumn);
        // The flow row is occupied: the pane starts on the next row, so one padding row less.
        Assert.Equal(5, pane.Padding);
        Assert.EndsWith("abc\n" + new string('\n', 5) + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output);
    }

    [Fact]
    public void ARowFilledExactly_WaitsForTheNextCharacterToWrap()
    {
        using var pane = Pane();
        pane.Show();
        pane.Write(new RawText(new string('x', 40)));

        // The terminal's cursor sits on the last cell with the wrap pending: still row 0.
        Assert.Equal(0, pane.FlowRow);
        Assert.Equal(40, pane.FlowColumn);

        pane.Write(new RawText("y"));
        Assert.Equal(1, pane.FlowRow);
        Assert.Equal(1, pane.FlowColumn);
        Assert.Equal(4, pane.Padding);
    }

    [Fact]
    public void ALongToken_WrapsByCells_WideCharactersWhole()
    {
        _console.Profile.Width = 5;
        using var pane = Pane();
        pane.Show();

        // 4 cells, then a 2-cell character that does not fit on the row: it starts the next one.
        pane.Write(new RawText("abcd日"));
        Assert.Equal(1, pane.FlowRow);
        Assert.Equal(2, pane.FlowColumn);
    }

    [Fact]
    public void WhenTheTranscriptReachesThePane_ThePaddingIsZero_AndTheFlowRowStops()
    {
        using var pane = Pane();
        pane.Show();
        for (int i = 0; i < 20; i++)
        {
            pane.Write(new Markup($"line {i}" + Environment.NewLine));
        }

        // Rows 0–5 belong to the flow, the pane holds 6–9; every further line scrolls the screen.
        Assert.Equal(0, pane.Padding);
        Assert.Equal(6, pane.FlowRow);
        Assert.EndsWith("line 19\n" + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output);
    }

    [Fact]
    public void TheConsolesCursorRow_CorrectsTheCount()
    {
        using var pane = Pane();
        pane.Show();
        _cursorRow = 4;   // the terminal wrapped something the count did not see
        pane.Write(new Markup("x" + Environment.NewLine));

        Assert.Equal(4, pane.FlowRow);
        Assert.Equal(2, pane.Padding);
    }

    [Fact]
    public void Clear_StartsTheFlowAtTheTop()
    {
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("a" + Environment.NewLine + "b" + Environment.NewLine));
        Assert.Equal(2, pane.FlowRow);

        using (pane.Batch())
        {
            pane.Clear(home: true);
            pane.Write(new Markup("banner" + Environment.NewLine));
        }

        Assert.Equal(1, pane.FlowRow);
        Assert.Equal(5, pane.Padding);
        Assert.EndsWith("banner\n" + new string('\n', 5) + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output);
    }

    [Fact]
    public void Batch_DrawsThePaneOnce()
    {
        using var pane = Pane();
        pane.Show();
        int before = Draws;
        using (pane.Batch())
        {
            pane.Write(new Markup("a" + Environment.NewLine));
            pane.Write(new Markup("b" + Environment.NewLine));
            Assert.Equal(before, Draws);
        }

        Assert.Equal(before + 1, Draws);
        Assert.Equal(2, pane.FlowRow);
    }

    // ── Sequences ───────────────────────────────────────────────────────────

    [Fact]
    public void AFlowWrite_LiftsThePane_WritesAtTheFlowCursor_AndDrawsItAgain()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 6;
        using var pane = Pane();
        pane.Show();

        // Row 0, two padding rows, the pane on rows 2–5; the cursor back up over the lower rule onto the input row, after the glyph.
        string shown = Output;
        Assert.Contains("\e[?25l", shown);
        Assert.Contains("\n\n" + Rule(20) + "\n", Strip(shown));
        Assert.EndsWith("\e[K\e[2A\e[20D\e[2C\e[?25h", shown);

        int mark = Output.Length;
        pane.Write(new Markup("hi" + Environment.NewLine));
        string written = Output[mark..];

        // Synchronized: up over the padding and the rule (3 rows), column 0, erase down, the line, the pane again.
        Assert.StartsWith("\e[?2026h\e[?25l\e[3A\e[20D\e[J", written);
        Assert.Contains("hi\n\n" + Rule(20) + "\n", Strip(written));
        Assert.EndsWith("\e[?25h\e[?2026l", written);
    }

    [Fact]
    public void ALiveTick_PaintsOverTheOldFrame_NeverErasingItFirst()
    {
        // 2026-09-26, the user's report: the pane flickered under streaming thinking — each tick
        // erased from the flow's end down, then drew. Now each row clears its own end, and the one
        // erase down comes after the pane's last row.
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 12;
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("a\n"));
        pane.SetLive(new ThinkingBlock("t1\nt2\nt3\nt4\nt5\nt6\nt7"));
        _time.Advance(ScreenPane.Tick);

        int mark = Output.Length;
        pane.SetLive(new ThinkingBlock("t1\nt2\nt3\nt4\nt5\nt6\nt7\nt8"));
        _time.Advance(ScreenPane.Tick);
        string written = Output[mark..];

        Assert.StartsWith("\e[?2026h\e[?25l", written);
        Assert.Contains("    t8", Strip(written));
        Assert.DoesNotContain("    t3", Strip(written));
        int erase = written.IndexOf("\e[J", StringComparison.Ordinal);
        Assert.True(erase > written.LastIndexOf(Rule(20), StringComparison.Ordinal), written);
        Assert.Matches(@"t8(\e\[[0-9;]*m)*\e\[K\r\n", written);
        Assert.True(written.IndexOf("\e[J", StringComparison.Ordinal) > written.IndexOf("t8", StringComparison.Ordinal));
        Assert.EndsWith("\e[?25h\e[?2026l", written);

        // A flow write still lifts with the erase.
        mark = Output.Length;
        pane.Write(new Markup("b\n"));
        Assert.Contains("\e[J", Output[mark..][..Output[mark..].IndexOf('b', StringComparison.Ordinal)]);
    }

    [Fact]
    public void AMidRowContinuation_ReturnsToItsColumn()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 6;
        using var pane = Pane();
        pane.Show();
        pane.Write(new RawText("abc"));
        int mark = Output.Length;

        pane.Write(new RawText("def"));

        // Padding 1 + the rule + the occupied flow row = 3 up, then 3 right, then erase from there.
        Assert.StartsWith("\e[?2026h\e[?25l\e[3A\e[20D\e[3C\e[J", Output[mark..]);
        Assert.Equal(6, pane.FlowColumn);
    }

    // ── Input row ───────────────────────────────────────────────────────────

    [Fact]
    public void CommitInput_PutsTheLineInTheFlow_AndEmptiesTheRow()
    {
        using var pane = Pane();
        pane.Show();
        pane.BeginInput();
        pane.ShowInput("hello", 5);
        pane.CommitInput("hello");

        Assert.Equal(1, pane.FlowRow);
        Assert.EndsWith(InputLine.PromptGlyph + "hello\n" + new string('\n', 5) + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output);
    }

    [Fact]
    public void CommitInput_WithAPreview_WritesItUnderTheLine_InTheSameFlowWrite()
    {
        using var pane = Pane();
        pane.Show();
        pane.BeginInput();
        pane.ShowInput("hello", 5);
        pane.CommitInput("hello", "one\ntwo");

        Assert.Equal(3, pane.FlowRow);
        Assert.EndsWith(InputLine.PromptGlyph + "hello\n  one\n  two\n" + new string('\n', 3) + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output);
    }

    [Fact]
    public void CommitInput_WithAPreview_Disabled_WritesItUnderTheLine()
    {
        using var pane = Pane(geometry: false);
        pane.Show();
        pane.BeginInput();
        pane.ShowInput("ab", 2);
        pane.CommitInput("ab", "one\ntwo");

        Assert.Equal("› ab› ab\n  one\n  two\n", Output);
    }

    [Fact]
    public void ClearInput_BlanksTheRow_AndLeavesNothingInTheFlow()
    {
        using var pane = Pane();
        pane.Show();
        pane.BeginInput();
        pane.ShowInput("draft", 5);
        pane.ClearInput();

        Assert.Equal(0, pane.FlowRow);
        Assert.EndsWith("draft" + new string(' ', 5), Output);
    }

    [Fact]
    public void PreviewInput_ShowsTypedAheadText_OnTheRow()
    {
        using var pane = Pane();
        pane.Show();
        pane.PreviewInput("queued");

        Assert.EndsWith("queued", Output);
        pane.Write(new Markup("reply" + Environment.NewLine));
        Assert.EndsWith(Rule(40) + "\n" + InputLine.PromptGlyph + "queued\n" + Rule(40) + "\n", Output);
    }

    [Fact]
    public void ANarrowerWindow_WrapsTheRow()
    {
        using var pane = Pane();
        pane.Show();
        pane.PreviewInput(new string('a', 30));
        Assert.Equal(1, pane.InputRows);
        _console.Profile.Width = 20;
        _time.Advance(ScreenPane.Tick);

        // 17 cells per row at width 20: the word breaks by cells.
        Assert.Equal(2, pane.InputRows);
        Assert.EndsWith(Rule(20) + "\n" + InputLine.PromptGlyph + new string('a', 17) + "\n" + InputLine.ContinuationIndent + new string('a', 13) + "\n" + Rule(20) + "\n", Output);
    }

    // ── Wrapped input ───────────────────────────────────────────────────────

    // 49 characters: on 37 cells the last space that fits is after "the".
    private const string Long = "the quick brown fox jumps over the lazy dog again";
    private const string Row0 = "the quick brown fox jumps over the";
    private const string Row1 = "lazy dog again";

    private static string TwoRows(int width) =>
        Rule(width) + "\n" + InputLine.PromptGlyph + Row0 + "\n" + InputLine.ContinuationIndent + Row1 + "\n" + Rule(width) + "\n";

    [Fact]
    public void ALongDraft_WrapsOntoASecondRow_AndThePaneGrows()
    {
        using var pane = Pane();
        pane.Show();
        pane.BeginInput();
        pane.ShowInput(Long, Long.Length);

        Assert.Equal(2, pane.InputRows);
        Assert.Equal(5, pane.Padding);
        Assert.Equal(0, pane.FlowRow);
        Assert.EndsWith(TwoRows(40), Output);
    }

    [Fact]
    public void AShorterDraft_ShrinksThePaneAgain()
    {
        using var pane = Pane();
        pane.Show();
        pane.ShowInput(Long, Long.Length);
        pane.ShowInput("short", 5);

        Assert.Equal(1, pane.InputRows);
        Assert.Equal(6, pane.Padding);
        Assert.EndsWith(Rule(40) + "\n" + InputLine.PromptGlyph + "short\n" + Rule(40) + "\n", Output);
    }

    [Fact]
    public void AKeyThatKeepsTheRowCount_RewritesTheRowsInPlace()
    {
        using var pane = Pane();
        pane.Show();
        pane.ShowInput(Long, Long.Length);
        int draws = Draws;

        pane.ShowInput(Long + "!", Long.Length + 1);

        Assert.Equal(draws, Draws);
        Assert.EndsWith(Row0 + "\n" + InputLine.ContinuationIndent + Row1 + "!", Output);

        // Shorter on the second row: the stale cell is blanked.
        pane.ShowInput(Long, Long.Length);
        Assert.Equal(draws, Draws);
        Assert.EndsWith(Row0 + "\n" + InputLine.ContinuationIndent + Row1 + " ", Output);
    }

    [Fact]
    public void TheCursor_IsPutOnItsRow_AndMovedBetweenRowsInPlace()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 6;
        using var pane = Pane();
        pane.Show();

        // 17 cells per row: "aaaa bbbb cccc" / "dddd"; the cursor at the end is on row 1, cell 4.
        string text = "aaaa bbbb cccc dddd";
        pane.ShowInput(text, text.Length);
        Assert.Equal(2, pane.InputRows);
        Assert.EndsWith("\e[K\e[2A\e[20D\e[6C\e[?25h\e[?2026l", Output);

        // Home: the same rows rewritten from the first, the cursor left on row 0 after the glyph.
        int mark = Output.Length;
        int draws = Draws;
        pane.ShowInput(text, 0);
        string written = Output[mark..];
        Assert.Equal(draws, Draws);
        Assert.StartsWith("\e[?2026h\e[?25l\e[1A\e[4D", written);
        Assert.Equal("aaaa bbbb cccc\n  dddd", Strip(written));
        Assert.EndsWith("\e[1A\e[20D\e[2C\e[?25h\e[?2026l", written);

        // End again: the cursor ends the last row, so no move at all.
        mark = Output.Length;
        pane.ShowInput(text, text.Length);
        written = Output[mark..];
        Assert.Equal("aaaa bbbb cccc\n  dddd", Strip(written));
        Assert.DoesNotContain("\e[1A", written);
        Assert.EndsWith("\e[?25h\e[?2026l", written);
    }

    [Fact]
    public void WhenThePaddingIsZero_AGrowingDraft_ScrollsTheFlowUp()
    {
        using var pane = Pane();
        pane.Show();
        for (int i = 0; i < 20; i++)
        {
            pane.Write(new Markup($"line {i}" + Environment.NewLine));
        }

        Assert.Equal(0, pane.Padding);
        Assert.Equal(6, pane.FlowRow);

        pane.ShowInput(Long, Long.Length);

        Assert.Equal(2, pane.InputRows);
        Assert.Equal(0, pane.Padding);
        Assert.Equal(5, pane.FlowRow);
        Assert.EndsWith(TwoRows(40), Output);
    }

    [Theory]
    [InlineData(10, 5)]
    [InlineData(24, 12)]
    [InlineData(6, 2)]
    [InlineData(5, 1)]
    [InlineData(1, 1)]
    public void MaxInputRows_IsPinned(int height, int rows) => Assert.Equal(rows, ScreenPane.MaxInputRows(height));

    [Fact]
    public void TheArea_IsCapped_AndTheViewportFollowsTheCursor()
    {
        using var pane = Pane();
        pane.Show();
        var words = Enumerable.Range(0, 6).Select(i => new string((char)('a' + i), 30)).ToArray();
        string text = string.Join(" ", words);   // six rows: no two words share 37 cells

        pane.ShowInput(text, text.Length);

        // Five of the six rows (half of ten), the first scrolled off: an indent row leads.
        Assert.Equal(5, pane.InputRows);
        Assert.Equal(2, pane.Padding);
        string tail = string.Concat(words.Skip(1).Select(w => InputLine.ContinuationIndent + w + "\n"));
        Assert.EndsWith(Rule(40) + "\n" + tail + Rule(40) + "\n", Output);

        // Home: the viewport moves back to the first row, glyph and all.
        pane.ShowInput(text, 0);
        Assert.Equal(5, pane.InputRows);
        string head = InputLine.PromptGlyph + words[0] + "\n" + string.Concat(words.Skip(1).Take(4).Select(w => InputLine.ContinuationIndent + w + "\n"));
        Assert.EndsWith(Rule(40) + "\n" + head + Rule(40) + "\n", Output);
    }

    [Fact]
    public void TheHint_IsRedrawnFromALowerRow()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 6;
        string hint = "one";
        using var pane = Pane();
        pane.Hint = () => hint;
        pane.Show();
        string text = "aaaa bbbb cccc dddd";
        pane.ShowInput(text, text.Length);
        int mark = Output.Length;

        hint = "two";
        _time.Advance(ScreenPane.Tick);

        // Down over the lower rule only (the cursor is on the last row), and back to its cell.
        string written = Output[mark..];
        Assert.StartsWith("\e[?2026h\e[?25l\e[2B\e[20D", written);   // the row's rewrite one synchronized frame (2026-09-29)
        Assert.Equal("two", Strip(written));
        Assert.EndsWith("\e[K\e[2A\e[20D\e[6C\e[?25h\e[?2026l", written);
    }

    [Fact]
    public void DraftEmpty_FollowsTheDraftThePaneDraws()
    {
        using var pane = Pane();
        pane.Show();
        Assert.True(pane.DraftEmpty);

        pane.ShowInput("x", 1);
        Assert.False(pane.DraftEmpty);
        pane.ShowInput("", 0);
        Assert.True(pane.DraftEmpty);

        pane.ShowInput("send me", 7);
        Assert.False(pane.DraftEmpty);
        pane.CommitInput("send me");
        Assert.True(pane.DraftEmpty);

        pane.ShowInput("gone", 4);
        pane.ClearInput();
        Assert.True(pane.DraftEmpty);
    }

    [Fact]
    public void DraftEmpty_DisabledPane_IsAlwaysTrue()
    {
        using var pane = Pane(geometry: false);
        pane.BeginInput();
        pane.ShowInput("ab", 2);
        Assert.True(pane.DraftEmpty);
    }

    [Fact]
    public void ShowInput_RedrawsTheHintRow_OnTheKey_WhenTheHintReadsTheDraft()
    {
        // A hint that reads the draft (the splash hint, 2026-09-20) follows it on the key that
        // fills or empties the draft — no tick between.
        using var pane = Pane();
        pane.Hint = () => pane.DraftEmpty ? "← → slideshow" : "";
        pane.Show();
        Assert.EndsWith(InputLine.PromptGlyph + "\n" + Rule(40) + "\n← → slideshow", Output);

        int mark = Output.Length;
        pane.ShowInput("x", 1);
        Assert.Contains("x", Output[mark..]);
        Assert.DoesNotContain("← → slideshow", Output[mark..]);   // the row rewritten blank: the draft on the input row alone

        mark = Output.Length;
        pane.ShowInput("", 0);
        Assert.EndsWith("← → slideshow", Output);                  // Backspace to nothing brings it back on the same key

        mark = Output.Length;
        pane.ShowInput("", 0);
        Assert.DoesNotContain("← → slideshow", Output[mark..]);    // unchanged: the row is not drawn again
    }

    [Fact]
    public void ShowInput_UnderASpinner_LeavesTheBusyRowAlone_WhateverTheHintSays()
    {
        using var pane = Pane();
        pane.Hint = () => pane.DraftEmpty ? "← → slideshow" : "";
        pane.Show();

        using (pane.BeginBusy("thinking"))
        {
            int mark = Output.Length;
            pane.PreviewInput("x");
            Assert.DoesNotContain("← → slideshow", Output[mark..]);
            Assert.DoesNotContain("thinking", Output[mark..]);   // the busy row is not redrawn by the key
        }
    }

    [Fact]
    public void ShowInput_InsideABatch_IsRemembered_AndDrawnWhenItEnds()
    {
        using var pane = Pane();
        pane.Show();
        int draws = Draws;
        using (pane.Batch())
        {
            pane.ShowInput(Long, Long.Length);
            Assert.Equal(draws, Draws);
        }

        Assert.Equal(2, pane.InputRows);
        Assert.EndsWith(TwoRows(40), Output);
    }

    [Fact]
    public void PreviewInput_WrapsALongLine()
    {
        using var pane = Pane();
        pane.Show();
        pane.PreviewInput(Long);

        Assert.Equal(2, pane.InputRows);
        Assert.EndsWith(TwoRows(40), Output);

        // A flow write keeps the preview, on its two rows.
        pane.Write(new Markup("reply" + Environment.NewLine));
        Assert.EndsWith("reply\n" + new string('\n', 4) + TwoRows(40), Output);
    }

    [Fact]
    public void CommitInput_OfAWrappedDraft_LiftsOverTheWholeArea()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 6;
        using var pane = Pane();
        pane.Show();
        string text = "aaaa bbbb cccc ddd";   // 18 > 17 cells: "aaaa bbbb cccc" / "ddd"; committed, exactly the 20 cells of one flow row
        pane.ShowInput(text, text.Length);
        Assert.Equal(1, pane.Padding);
        int mark = Output.Length;

        pane.CommitInput(text);
        string written = Output[mark..];

        // From the second input row: over the first, the rule and one padding row.
        Assert.StartsWith("\e[?2026h\e[?25l\e[3A\e[20D\e[J", written);
        Assert.Contains(InputLine.PromptGlyph + text + "\n\n" + Rule(20) + "\n" + InputLine.PromptGlyph + "\n" + Rule(20) + "\n", Strip(written));
        Assert.Equal(1, pane.FlowRow);
        Assert.Equal(1, pane.InputRows);
        Assert.Equal(1, pane.Padding);
    }

    [Fact]
    public void AnOverlay_OverAWrappedDraft_ComesBackToIt()
    {
        _console.EmitAnsiSequences();
        using var pane = Pane();
        pane.Show();
        pane.ShowInput(Long, Long.Length);
        pane.ShowOverlay(new Markup("a\nb"), "hint");
        Assert.Equal(2, pane.OverlayRows);

        // Edited under the overlay: remembered only.
        int mark = Output.Length;
        pane.ShowInput(Long + "!", Long.Length + 1);
        Assert.Equal(mark, Output.Length);

        pane.CloseOverlay();
        Assert.Equal(2, pane.InputRows);
        Assert.EndsWith(Rule(40) + "\n" + InputLine.PromptGlyph + Row0 + "\n" + InputLine.ContinuationIndent + Row1 + "!\n" + Rule(40) + "\n", Strip(Output));

        // Close from the second row: over the lower rule to the hint row.
        mark = Output.Length;
        pane.Close();
        Assert.Equal("\e[2B\r\n", Output[mark..]);
    }

    [Fact]
    public void Close_FromTheFirstOfTwoRows_StepsUnderTheHint()
    {
        _console.EmitAnsiSequences();
        using var pane = Pane();
        pane.Show();
        pane.ShowInput(Long, 0);
        int mark = Output.Length;

        pane.Close();

        Assert.Equal("\e[3B\r\n", Output[mark..]);
    }

    // ── Hint row ────────────────────────────────────────────────────────────

    [Fact]
    public void TheHint_IsDrawnUnderTheRow_AndRedrawnWhenItChanges()
    {
        string hint = "ESC clears";
        using var pane = Pane();
        pane.Hint = () => hint;
        pane.Show();
        Assert.EndsWith(InputLine.PromptGlyph + "\n" + Rule(40) + "\nESC clears", Output);

        hint = "⏰ tea 09:00";
        _time.Advance(ScreenPane.Tick);
        Assert.EndsWith("⏰ tea 09:00", Output);

        int mark = Output.Length;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(mark, Output.Length);   // unchanged: nothing drawn
    }

    [Fact]
    public void Busy_ShowsASpinnerWithTheLabel_ThenTheHintAgain()
    {
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();

        var busy = pane.BeginBusy("thinking");
        using (busy)
        {
            Assert.EndsWith(Theme.SpinnerFrames[0] + " thinking 00:00", Output);
            _time.Advance(ScreenPane.Tick);
            Assert.EndsWith(Theme.SpinnerFrames[1] + " thinking 00:00", Output);
            busy.SetLabel("transcribing");
            Assert.EndsWith(Theme.SpinnerFrames[1] + " transcribing 00:00", Output);
        }

        Assert.EndsWith("idle", Output);
        busy.SetLabel("late");
        Assert.EndsWith("idle", Output);
        busy.Dispose();   // a second end is nothing
        Assert.EndsWith("idle", Output);
    }

    [Fact]
    public void AlertHint_StandsInForTheHintAndTheSpinner_UntilItLapsesOrIsTakenBack()
    {
        // The kill switch's first press (2026-10-01): the alert is seen under a spinner too, and goes by itself on the tick.
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();

        pane.SetAlertHint("press again", TimeSpan.FromSeconds(2));
        Assert.EndsWith("press again", Output);
        using (pane.BeginBusy("thinking"))
        {
            Assert.EndsWith("press again", Output);   // the busy row gives way while it stands
            int mark = Output.Length;
            _time.Advance(TimeSpan.FromSeconds(2));
            Assert.Null(pane.AlertHint);
            Assert.Contains(" thinking ", Output[mark..]);
        }

        Assert.EndsWith("idle", Output);
        pane.SetAlertHint("again", TimeSpan.FromSeconds(2));
        Assert.EndsWith("again", Output);
        pane.SetAlertHint(null, TimeSpan.Zero);   // taken back at once
        Assert.Null(pane.AlertHint);
        Assert.EndsWith("idle", Output);
    }

    [Fact]
    public void Busy_NestedScopes_TheOuterLabelChangesUnderTheInner_AndShowsWhenTheInnerEnds()
    {
        // A turn's stage changes (writing → read_file) while /settings' voice listing sits on
        // top: the menu's label stays, and the turn's new one is what comes back after it.
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();

        using (var turn = pane.BeginBusy("thinking"))
        {
            using (var menu = pane.BeginBusy("listing voices"))
            {
                turn.SetLabel("read_file");
                Assert.EndsWith(" listing voices 00:00", Output);
                menu.SetLabel("listing models");
                Assert.EndsWith(" listing models 00:00", Output);
            }

            Assert.EndsWith(" read_file 00:00", Output);
        }

        Assert.EndsWith("idle", Output);
    }

    [Fact]
    public void Busy_DisabledPane_HandsOutANoOpScope()
    {
        using var pane = Pane(geometry: false);
        var busy = pane.BeginBusy("thinking");
        Assert.Same(ScreenPane.BusyScope.None, busy);
        busy.SetLabel("writing");
        busy.Dispose();
        Assert.Equal("", Output);
    }

    [Fact]
    public void Busy_NestedScopes_TheInnerLabelShows_ThenTheOuterAgain_OnOneCount()
    {
        // A menu's spinner (listing voices) under a running turn's: the outer scope's label comes
        // back when the inner ends, and the count is the outer's throughout.
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();

        using (var turn = pane.BeginBusy("thinking"))
        {
            for (int i = 0; i < 10; i++)
            {
                _time.Advance(ScreenPane.Tick);
            }

            Assert.EndsWith(" thinking 00:01", Output);
            using (pane.BeginBusy("listing voices"))
            {
                Assert.EndsWith(" listing voices 00:01", Output);
                _time.Advance(TimeSpan.FromSeconds(1));
                Assert.EndsWith(" listing voices 00:02", Output);
            }

            Assert.EndsWith(" thinking 00:02", Output);
            turn.SetLabel("snarflegrooving");
            Assert.EndsWith(" snarflegrooving 00:02", Output);
        }

        Assert.EndsWith("idle", Output);
    }

    [Fact]
    public void Busy_OverAnOverlay_TheRowCarriesTheOverlaysHint()
    {
        // /help opened mid-turn: the spinner and its count, then the pane's own keys — ESC closes
        // the pane there, not the turn — behind the separator; the bare row again once it closes.
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();

        using (pane.BeginBusy("thinking"))
        {
            pane.ShowOverlay(new Markup("a"), "ESC closes");
            Assert.EndsWith(Theme.SpinnerFrames[0] + " thinking 00:00 · ESC closes", Output);
            _time.Advance(ScreenPane.Tick);
            Assert.EndsWith(Theme.SpinnerFrames[1] + " thinking 00:00 · ESC closes", Output);
            pane.CloseOverlay();
            Assert.EndsWith(" thinking 00:00", Output);
        }

        Assert.EndsWith("idle", Output);
    }

    [Fact]
    public void BusyRow_IsPinned()
    {
        Assert.Equal("thinking 00:12", ScreenPane.BusyRow("thinking", TimeSpan.FromSeconds(12), ""));
        Assert.Equal("thinking 00:12 · ESC closes · ←/→ tabs · ↑/↓ scroll", ScreenPane.BusyRow("thinking", TimeSpan.FromSeconds(12), "ESC closes · ←/→ tabs · ↑/↓ scroll"));
        // The queued part between the label and the overlay's hint (2026-09-18); empty, the row as before.
        Assert.Equal("thinking 00:12 · 📤 2 queued", ScreenPane.BusyRow("thinking", TimeSpan.FromSeconds(12), "", "📤 2 queued"));
        Assert.Equal("thinking 00:12 · 📤 2 queued · ESC closes", ScreenPane.BusyRow("thinking", TimeSpan.FromSeconds(12), "ESC closes", "📤 2 queued"));
        Assert.Equal(ScreenPane.BusyRow("thinking", TimeSpan.FromSeconds(12), "ESC closes"), ScreenPane.BusyRow("thinking", TimeSpan.FromSeconds(12), "ESC closes", ""));
        // The tally right after the label (2026-09-25), ahead of the queued part; empty, the row as before.
        Assert.Equal("thinking 00:12 · ~1.7k / 4.1k · ~42% · 📤 2 queued · ESC closes", ScreenPane.BusyRow("thinking", TimeSpan.FromSeconds(12), "ESC closes", "📤 2 queued", "~1.7k / 4.1k · ~42%"));
        Assert.Equal(ScreenPane.BusyRow("thinking", TimeSpan.FromSeconds(12), "ESC closes", "📤 2 queued"), ScreenPane.BusyRow("thinking", TimeSpan.FromSeconds(12), "ESC closes", "📤 2 queued", ""));
        // A label after the tally (the ComfyUI generation's, 2026-09-25): the two swap, nothing else moves; no tally, the row as before.
        Assert.Equal("1.2k / 4.1k · 30% · 🎨 00:12 · 📤 2 queued · ESC closes", ScreenPane.BusyRow("🎨", TimeSpan.FromSeconds(12), "ESC closes", "📤 2 queued", "1.2k / 4.1k · 30%", labelAfterUsage: true));
        Assert.Equal(ScreenPane.BusyRow("🎨", TimeSpan.FromSeconds(12), "ESC closes", "📤 2 queued"), ScreenPane.BusyRow("🎨", TimeSpan.FromSeconds(12), "ESC closes", "📤 2 queued", "", labelAfterUsage: true));
    }

    /// <summary>
    /// A tool run (2026-09-22): its lines past the keep fold under the summary on the screen (the flow
    /// rebuilt from the store), a click on the summary's row unfolds it and another folds it again;
    /// a click on any other row does nothing.
    /// </summary>
    [Fact]
    public void ToolRun_FoldsOnTheScreen_AndAClickOnItsSummaryToggles()
    {
        _cursorTop = 100;   // rule 99 over the input row: the region's six rows are 93–98
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("a\n"));
        pane.BeginToolGroup(1);
        pane.SetToolGroupSummary(new Markup("S"), new Markup("E"));
        pane.WriteToolLine(new Markup("m1\n"));
        pane.WriteToolLine(new Markup("m2\n"));
        Assert.Equal(3, pane.StoredRows);   // live: a, the summary, the last line
        int mark = Output.Length;
        pane.WriteToolLine(new Markup("m3\n"));
        Assert.Contains("a\nS\nm3\n", Output[mark..]);   // the flow written again from the store
        pane.EndToolGroup();
        Assert.Equal(2, pane.StoredRows);
        Assert.False(pane.ToolGroupOpen);

        Assert.False(pane.TryToggleToolGroupAt(0, 93));   // "a"
        Assert.False(pane.TryToggleToolGroupAt(0, 95));   // under the flow
        mark = Output.Length;
        Assert.True(pane.TryToggleToolGroupAt(3, 94));    // the summary
        Assert.Equal(5, pane.StoredRows);
        Assert.Contains("a\nE\nm1\nm2\nm3\n", Output[mark..]);
        Assert.True(pane.TryToggleToolGroupAt(0, 94));
        Assert.Equal(2, pane.StoredRows);

        // The pane-wide state: every run, and the ones to come.
        pane.SetToolGroupsExpanded(true);
        Assert.True(pane.ToolGroupsExpanded);
        Assert.Equal(5, pane.StoredRows);
        pane.ToggleToolGroups();
        Assert.False(pane.ToolGroupsExpanded);
        Assert.Equal(2, pane.StoredRows);
    }

    /// <summary>
    /// The thinking block (2026-09-26): drawn live under its header, folded to its summary the moment
    /// the slot commits it, and a click on the summary's row unfolds it and folds it again, as a tool
    /// run's does; Ctrl+O's pane-wide state takes it too.
    /// </summary>
    [Fact]
    public void Thinking_FoldsWhenCommitted_AndAClickOnItsSummaryToggles()
    {
        _cursorTop = 100;   // rule 99 over the input row: the region's six rows are 93–98
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("a\n"));
        pane.SetLive(new ThinkingBlock("t1\nt2"));
        _time.Advance(ScreenPane.Tick);
        Assert.Contains(ThinkingFoldText.LiveHeader + "\n    t1\n    t2\n", Output);

        int mark = Output.Length;
        pane.SetLive(new ThinkingBlock("t1\nt2", TimeSpan.FromSeconds(4.2)));
        pane.CommitLive();
        string folded = ThinkingFoldText.Summary(TimeSpan.FromSeconds(4.2), expanded: false);
        Assert.Equal("  ▸ 💭 thought for 4.2s", folded);
        Assert.Contains("a\n" + folded + "\n", Output[mark..]);
        Assert.Equal(2, pane.StoredRows);

        mark = Output.Length;
        Assert.True(pane.TryToggleToolGroupAt(3, 94));
        Assert.Equal(4, pane.StoredRows);
        Assert.Contains(ThinkingFoldText.Summary(TimeSpan.FromSeconds(4.2), expanded: true) + "\n    t1\n    t2\n", Output[mark..]);
        Assert.True(pane.TryToggleToolGroupAt(3, 94));
        Assert.Equal(2, pane.StoredRows);

        pane.SetToolGroupsExpanded(true);
        Assert.Equal(4, pane.StoredRows);
        pane.SetToolGroupsExpanded(false);
        Assert.Equal(2, pane.StoredRows);
    }

    private static string ThinkingLines(int from, int to) =>
        string.Join("\n", Enumerable.Range(from, to - from + 1).Select(i => "t" + i.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// The streaming thinking block shows its header and its last five rows (2026-09-26), scrolling as
    /// it grows, and commits nothing while it streams; folded, it went into the transcript whole.
    /// </summary>
    [Fact]
    public void Thinking_Streaming_ShowsOnlyItsLastFiveRows_AndFoldsWhole()
    {
        _cursorTop = 100;   // rule 99 over the input row: the region's six rows are 93–98
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("a\n"));
        int mark = Output.Length;
        pane.SetLive(new ThinkingBlock(ThinkingLines(1, 12)));
        _time.Advance(ScreenPane.Tick);
        Assert.Contains(ThinkingFoldText.LiveHeader + "\n    t8\n    t9\n    t10\n    t11\n    t12\n", Output[mark..]);
        Assert.DoesNotContain("    t7\n", Output[mark..]);
        Assert.Equal(0, pane.LiveCommitted);
        Assert.Equal(6, pane.LiveRows);

        mark = Output.Length;
        pane.SetLive(new ThinkingBlock(ThinkingLines(1, 13)));
        _time.Advance(ScreenPane.Tick);
        Assert.Contains(ThinkingFoldText.LiveHeader + "\n    t9\n    t10\n    t11\n    t12\n    t13\n", Output[mark..]);
        Assert.Equal(0, pane.LiveCommitted);

        pane.SetLive(new ThinkingBlock(ThinkingLines(1, 13), TimeSpan.FromSeconds(75)));
        pane.CommitLive();
        Assert.Equal(2, pane.StoredRows);

        mark = Output.Length;
        Assert.True(pane.TryToggleToolGroupAt(3, 94));
        Assert.Equal(15, pane.StoredRows);
        pane.SetToolGroupsExpanded(true);
        Assert.Equal(15, pane.StoredRows);
        pane.SetToolGroupsExpanded(false);
        Assert.Equal(2, pane.StoredRows);
    }

    [Fact]
    public void Thinking_Streaming_ATailRowIsALaidOutRow()
    {
        // A wrapped line is several of the five: t3 and t4, then the long line's three rows.
        using var pane = Pane();
        pane.Show();
        int mark = Output.Length;
        pane.SetLive(new ThinkingBlock("t1\nt2\nt3\nt4\n" + new string('w', 80)));
        _time.Advance(ScreenPane.Tick);
        Assert.Contains(ThinkingFoldText.LiveHeader + "\n    t3\n    t4\n    w", Output[mark..]);
        Assert.DoesNotContain("    t2\n", Output[mark..]);
        Assert.Equal(6, pane.LiveRows);
    }

    [Fact]
    public void Thinking_Streaming_OnARegionUnderTheTail_CommitsNothing_AndStillFoldsWhole()
    {
        _console.Profile.Height = 8;
        using var pane = Pane();
        pane.Show();
        pane.SetLive(new ThinkingBlock(ThinkingLines(1, 12)));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(0, pane.LiveCommitted);
        Assert.True(pane.LiveRows < 6);

        pane.SetLive(new ThinkingBlock(ThinkingLines(1, 12), TimeSpan.FromSeconds(4)));
        pane.CommitLive();
        pane.Write(new Markup("answer\n"));
        Assert.Equal(2, pane.StoredRows);
        pane.SetToolGroupsExpanded(true);
        Assert.Equal(14, pane.StoredRows);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("blank")]
    [InlineData("wrapped")]
    [InlineData("indented")]
    [InlineData("short")]
    public void ThinkingLiveView_TailsAsTheWholeBlockDoes(string kind)
    {
        string body = kind switch
        {
            "plain" => ThinkingLines(1, 200),
            "blank" => ThinkingLines(1, 50) + "\n\n\nx\n\ny",
            "wrapped" => ThinkingLines(1, 50) + "\n" + new string('w', 90) + "\nz",
            "indented" => ThinkingLines(1, 50) + "\n    a\n  b\nc\nd\ne",
            _ => "only\n  two",
        };
        static string Rows(List<SegmentLine> lines) =>
            string.Join("\n", lines.Select(l => string.Concat(l.Select(s => s.Text))));

        var block = new ThinkingBlock(body);
        var whole = ThinkingBlock.Tail(ScreenPane.RenderLines(block, _console, 40), 20);
        var view = ThinkingBlock.Tail(ScreenPane.RenderLines(block.LiveView(), _console, 40), 20);
        Assert.Equal(Rows(whole), Rows(view));
        Assert.True(block.LiveView().Text.Split('\n').Length <= ThinkingBlock.LiveTailRows);
    }

    [Fact]
    public void ThinkingTail_KeepsTheHeaderAndTheLastRows()
    {
        static List<SegmentLine> Lines(int n) =>
            Enumerable.Range(0, n).Select(i => new SegmentLine { new Segment(i.ToString(CultureInfo.InvariantCulture)) }).ToList();
        static string Texts(List<SegmentLine> lines) => string.Join(",", lines.Select(l => l[0].Text));

        Assert.Equal("0,1,2", Texts(ThinkingBlock.Tail(Lines(3), 20)));
        Assert.Equal("0,5,6,7,8,9", Texts(ThinkingBlock.Tail(Lines(10), 20)));
        Assert.Equal("0,8,9", Texts(ThinkingBlock.Tail(Lines(10), 3)));
        Assert.Equal("9", Texts(ThinkingBlock.Tail(Lines(10), 1)));
        Assert.Empty(ThinkingBlock.Tail(Lines(10), 0));
        Assert.Empty(ThinkingBlock.Tail(Lines(0), 20));
    }

    [Fact]
    public void ToolRun_WithoutThePane_IsThePlainWrite()
    {
        using var pane = Pane(geometry: false);
        pane.BeginToolGroup(1);
        pane.WriteToolLine(new Markup("m1\n"));
        pane.WriteToolLine(new Markup("m2\n"));
        pane.EndToolGroup();

        Assert.Contains("m1\nm2\n", Output);
        Assert.False(pane.TryToggleToolGroupAt(0, 0));
    }

    /// <summary>The queued part (2026-09-18): after the strip on the standing row and after the spinner's label on the busy row, its place recorded for TryHitQueued in both; hidden with its zone under an overlay's hint and the scroll's; gone when nothing is queued.</summary>
    [Fact]
    public void Queued_LeadsBothRows_AndTryHitQueued_AnswersInBoth_NotUnderAnOverlay()
    {
        _cursorTop = 100;
        string queued = "📤 2 queued";
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Strip = () => "🔊";
        pane.Queued = () => queued;
        pane.Show();
        pane.ShowInput("abc", 3);   // rule 99, row 100, rule 101, hint 102

        // Standing: "🔊 · 📤 2 queued · idle" — the part at columns 5..15 (the strip's two cells and the separator ahead).
        Assert.Contains("\n🔊 · 📤 2 queued · idle", Output);
        Assert.False(pane.TryHitQueued(4, 102));
        Assert.True(pane.TryHitQueued(5, 102));
        Assert.True(pane.TryHitQueued(15, 102));
        Assert.False(pane.TryHitQueued(16, 102));
        Assert.False(pane.TryHitQueued(5, 101));
        Assert.True(pane.TryHitHint(6, 102, out var hit));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Queued, "", 5), hit);
        Assert.True(pane.TryHitHint(2, 102, out hit));
        Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);
        Assert.True(pane.TryHitHint(0, 102, out hit));
        Assert.Equal(ScreenPane.HintZone.Strip, hit.Zone);

        // Busy: "🔊 · ⠋ thinking 00:00 · 📤 2 queued" — the part after the strip (3), the frame (1), the blank, the label and a separator.
        using (pane.BeginBusy("thinking"))
        {
            Assert.Contains("🔊 · " + Theme.SpinnerFrames[0] + " thinking 00:00 · 📤 2 queued", Output);
            int column = TextCells.Width("🔊 · " + Theme.SpinnerFrames[0] + " thinking 00:00 · ");
            Assert.False(pane.TryHitQueued(column - 1, 102));
            Assert.True(pane.TryHitQueued(column, 102));
            Assert.True(pane.TryHitQueued(column + 10, 102));
            Assert.False(pane.TryHitQueued(column + 11, 102));
            // The standing row's zones are not the busy row's: TryHitHint says the row there.
            Assert.True(pane.TryHitHint(column, 102, out hit));
            Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);

            // Under an overlay's hint the part is hidden and its zone with it; closed, back.
            int mark = Output.Length;
            pane.ShowOverlay(new Markup("a"), "ESC closes");
            Assert.Contains("🔊 · " + Theme.SpinnerFrames[0] + " thinking 00:00 · ESC closes", Output[mark..]);
            Assert.DoesNotContain("queued", Output[mark..]);
            Assert.False(pane.TryHitQueued(column, 102));
            pane.CloseOverlay();
            Assert.True(pane.TryHitQueued(column, 102));
        }

        // Nothing queued: the row as before, no zone.
        queued = "";
        int after = Output.Length;
        pane.RefreshHint();
        Assert.Contains("🔊 · idle", Output[after..]);
        Assert.DoesNotContain("queued", Output[after..]);
        Assert.False(pane.TryHitQueued(5, 102));
        Assert.True(pane.TryHitHint(6, 102, out hit));
        Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);

        // No strip: the part leads the row from column 0.
        pane.Strip = () => "";
        queued = "📤 1 queued";
        after = Output.Length;
        pane.RefreshHint();
        Assert.Contains("📤 1 queued · idle", Output[after..]);
        Assert.True(pane.TryHitQueued(0, 102));
        Assert.True(pane.TryHitQueued(10, 102));
        Assert.False(pane.TryHitQueued(11, 102));
    }

    /// <summary>A part the width cut is no button (2026-09-18): the trailer takes half the row and the fit cuts the left from the right — the count leads, so it survives a long hint but not a row too narrow for it.</summary>
    [Fact]
    public void Queued_CutByANarrowRow_HasNoZone()
    {
        _cursorTop = 100;
        _console.Profile.Width = 20;
        using var pane = Pane();
        pane.Hint = () => "a rather long standing hint";
        pane.Queued = () => "📤 12 queued";
        pane.Trailer = () => "llama";
        pane.Show();
        pane.ShowInput("abc", 3);

        // 19 cells, "llama" (5) pinned right with a gap of 2: 12 cells for the left, "📤 12 queued" is 12 — cut to 11 + the ellipsis.
        Assert.False(pane.TryHitQueued(0, 102));
        Assert.True(pane.TryHitHint(0, 102, out var hit));
        Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);

        // Without the trailer the 19 cells hold it whole ahead of the cut hint.
        pane.Trailer = () => "";
        pane.RefreshHint();
        Assert.True(pane.TryHitQueued(0, 102));
        Assert.True(pane.TryHitQueued(11, 102));
        Assert.False(pane.TryHitQueued(12, 102));
    }

    [Fact]
    public void HintHitAt_TheQueuedZone_SitsBetweenTheStripAndTheTrailer()
    {
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", 0), ScreenPane.HintHitAt("🔊", 30, 5, 11, 1));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1), ScreenPane.HintHitAt("🔊", 30, 5, 11, 4));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Queued, "", 5), ScreenPane.HintHitAt("🔊", 30, 5, 11, 5));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Queued, "", 5), ScreenPane.HintHitAt("🔊", 30, 5, 11, 15));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1), ScreenPane.HintHitAt("🔊", 30, 5, 11, 16));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Trailer, "", 30), ScreenPane.HintHitAt("🔊", 30, 5, 11, 30));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1), ScreenPane.HintHitAt("🔊", 30, -1, 0, 5));
        Assert.Equal(ScreenPane.HintHitAt("🔊 🎤", 30, 3), ScreenPane.HintHitAt("🔊 🎤", 30, -1, 0, 3));
    }

    [Fact]
    public void Busy_TheCountRunsFromTheScopesStart_AndALabelChangeKeepsIt()
    {
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();
        _time.Advance(TimeSpan.FromMinutes(5));   // time before the scope is not counted

        using (var busy = pane.BeginBusy("listening"))
        {
            Assert.EndsWith(" listening 00:00", Output);
            for (int i = 0; i < 10; i++)
            {
                _time.Advance(ScreenPane.Tick);
            }

            Assert.EndsWith(" listening 00:01", Output);
            busy.SetLabel("transcribing");
            Assert.EndsWith(" transcribing 00:01", Output);
            _time.Advance(TimeSpan.FromSeconds(61));
            Assert.EndsWith(" transcribing 01:02", Output);
            _time.Advance(TimeSpan.FromHours(1));
            Assert.EndsWith(" transcribing 01:01:02", Output);
        }

        Assert.EndsWith("idle", Output);

        // A new scope starts its own count.
        using (pane.BeginBusy("thinking"))
        {
            Assert.EndsWith(" thinking 00:00", Output);
        }
    }

    [Theory]
    [InlineData("thinking", 0, "thinking 00:00")]
    [InlineData("thinking", 12, "thinking 00:12")]
    [InlineData("compacting", 62, "compacting 01:02")]
    [InlineData("listening", 3661, "listening 01:01:01")]
    public void BusyText_IsPinned(string label, int seconds, string expected) =>
        Assert.Equal(expected, ScreenPane.BusyText(label, TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void TheHint_IsCutToTheRow()
    {
        _console.Profile.Width = 12;
        using var pane = Pane();
        pane.Hint = () => "a very long hint indeed";
        pane.Show();

        Assert.EndsWith("a very lon…", Output);
    }

    // ── The strip ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "idle", "idle")]
    [InlineData("🔊", "idle", "🔊 · idle")]
    [InlineData("🔊", "", "🔊")]
    [InlineData("", "", "")]
    public void HintRow_IsPinned(string lead, string rest, string expected) => Assert.Equal(expected, ScreenPane.HintRow(lead, rest));

    [Theory]
    [InlineData("", "")]
    [InlineData("🔊", "🔊 · ")]
    public void StripPrefix_IsPinned(string strip, string expected) => Assert.Equal(expected, ScreenPane.StripPrefix(strip));

    [Fact]
    public void TheStrip_StartsTheRow_InEveryState()
    {
        using var pane = Pane();
        pane.Strip = () => "🔊 🎤";
        pane.Hint = () => "idle";
        pane.Show();
        Assert.EndsWith("\n🔊 🎤 · idle", Output);

        // Ahead of the spinner, never taken away by it.
        using (pane.BeginBusy("thinking"))
        {
            Assert.EndsWith("🔊 🎤 · " + Theme.SpinnerFrames[0] + " thinking 00:00", Output);
            _time.Advance(ScreenPane.Tick);
            Assert.EndsWith("🔊 🎤 · " + Theme.SpinnerFrames[1] + " thinking 00:00", Output);
        }

        Assert.EndsWith("🔊 🎤 · idle", Output);

        // Under an overlay's own hint too.
        pane.ShowOverlay(new Markup("a"), "Enter = save");
        Assert.EndsWith("🔊 🎤 · Enter = save", Output);
        pane.CloseOverlay();
        Assert.EndsWith("🔊 🎤 · idle", Output);

        // Nothing else on the row: the strip alone, no separator (the in-place redraw follows the last row without a newline).
        pane.Hint = () => "";
        pane.RefreshHint();
        Assert.EndsWith("🔊 🎤 · idle🔊 🎤", Output);
    }

    [Fact]
    public void TheStrip_ChangeIsPickedUpOnTheTick()
    {
        string strip = "🔊";
        using var pane = Pane();
        pane.Strip = () => strip;
        pane.Hint = () => "idle";
        pane.Show();
        Assert.EndsWith("🔊 · idle", Output);

        strip = "🔊 🎤";
        _time.Advance(ScreenPane.Tick);
        Assert.EndsWith("🔊 🎤 · idle", Output);

        // RefreshHint sees the same change without waiting for the tick (an in-place redraw: no newline ahead of it).
        strip = "";
        pane.RefreshHint();
        Assert.EndsWith("🔊 🎤 · idleidle", Output);
    }

    [Fact]
    public void TheStrip_IsCutAheadOfTheLabel()
    {
        // 17 usable cells: the lead ("🔊 🎤 · ", the glyphs two cells each) is 8 of them.
        _console.Profile.Width = 18;
        using var pane = Pane();
        pane.Strip = () => "🔊 🎤";
        pane.Hint = () => "a longer hint";
        pane.Show();
        Assert.EndsWith("🔊 🎤 · a longer…", Output);

        // Under the spinner the lead is fitted first (whole here) and the label gets what is left.
        using (pane.BeginBusy("thinking"))
        {
            Assert.EndsWith("🔊 🎤 · " + Theme.SpinnerFrames[0] + " thinki…", Output);
        }

        // A row too narrow for the strip cuts the strip itself, never splitting a glyph.
        _console.Profile.Width = 14;
        pane.Strip = () => "🔊 🎤 👂 ✋";
        pane.RefreshHint();
        Assert.EndsWith("🔊 🎤 👂 ✋ …", Output);
        using (pane.BeginBusy("thinking"))
        {
            Assert.EndsWith("🔊 🎤 👂 ✋…" + Theme.SpinnerFrames[0], Output);
        }
    }

    // ── The trailer ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("idle", "", 20, "idle")]                                   // no trailer: the row as before
    [InlineData("a very long hint indeed", "", 10, "a very lo…")]
    [InlineData("idle", "llama", 20, "idle           llama")]              // 20 cells: the trailer on the last
    [InlineData("", "llama", 10, "     llama")]
    [InlineData("work · idle", "llama", 20, "work · idle    llama")]
    [InlineData("work · a longer hint", "llama", 20, "work · a lon…  llama")] // the left cut ahead of the gap
    [InlineData("idle", "qwen3-30b-a3b-instruct", 20, "idle      qwen3-30b…")] // the trailer at most half the row
    [InlineData("🔊 🎤", "llama", 12, "🔊 🎤  llama")]                        // two-cell glyphs counted as such
    [InlineData("idle", "llama", 1, "…")]
    [InlineData("idle", "qwen3-30b-a3b-instruct-q8", 40, "idle                qwen3-30b-a3b-instr…")] // a short left: the half, as before
    [InlineData("work · a longer hint", "qwen3-30b-a3b-instruct-q8", 40, "work · a longer hint  qwen3-30b-a3b-ins…")] // the name gives way to a left that fits
    [InlineData("Enter = edit · ESC = close", "qwen3-30b-a3b-instruct-q8", 40, "Enter = edit · ESC = …  qwen3-30b-a3b-i…")] // TrailerMinCells, then the left is cut
    public void PinRight_IsPinned(string left, string trailer, int cells, string expected) =>
        Assert.Equal(expected, ScreenPane.PinRight(left, trailer, cells));

    [Theory]
    [InlineData("idle", "llama", "", 20, "idle           llama")]            // no mark: the three-argument row
    [InlineData("idle", "llama", "◕", 20, "idle         llama ◕")]           // the mark on the last cell, a blank ahead of it
    [InlineData("idle", "qwen3-30b-a3b-instruct", "◕", 20, "idle      qwen3-3… ◕")] // half the row = 10: the text cut to 8, the mark whole
    [InlineData("", "", "◕", 10, "         ◕")]                              // a mark alone
    [InlineData("idle", "llama", "◕", 5, "i…  ◕")]                           // half = 2: the text gone, the mark stands
    [InlineData("work · a longer hint", "qwen3-30b-a3b-instruct-q8", "◕", 40, "work · a longer hint  qwen3-30b-a3b-i… ◕")] // the name gives way, the left whole
    [InlineData("Enter = edit · ESC = close", "qwen3-30b-a3b-instruct-q8", "◕", 40, "Enter = edit · ESC …  qwen3-30b-a3b-i… ◕")] // the name at its floor, then the left is cut
    public void PinRight_WithAMark_IsPinned(string left, string trailer, string mark, int cells, string expected) =>
        Assert.Equal(expected, ScreenPane.PinRight(left, trailer, mark, cells));

    [Theory]
    [InlineData("llama", "", 20, "llama")]
    [InlineData("llama", "◕", 20, "llama ◕")]
    [InlineData("qwen3-30b-a3b-instruct", "", 20, "qwen3-30b…")]
    [InlineData("qwen3-30b-a3b-instruct", "◕", 20, "qwen3-3… ◕")]
    [InlineData("qwen3-30b-a3b-instruct", "◕", 6, "… ◕")]                    // half = 3: one cell for the text
    [InlineData("qwen3-30b-a3b-instruct", "◕", 5, "◕")]                      // half = 2: nothing left for the text
    [InlineData("", "◕", 20, "◕")]
    [InlineData("", "", 20, "")]
    public void Trail_IsPinned(string trailer, string mark, int cells, string expected) =>
        Assert.Equal(expected, ScreenPane.Trail(trailer, mark, cells));

    [Theory]
    [InlineData("qwen3-30b-a3b-instruct-q8", "", 40, 0, "qwen3-30b-a3b-instr…")]    // nothing on the left: the half
    [InlineData("qwen3-30b-a3b-instruct-q8", "", 40, 4, "qwen3-30b-a3b-instr…")]    // a short left: still the half
    [InlineData("qwen3-30b-a3b-instruct-q8", "", 40, 20, "qwen3-30b-a3b-ins…")]     // 40 − 20 − the gap = 18: the name gives way
    [InlineData("qwen3-30b-a3b-instruct-q8", "", 40, 30, "qwen3-30b-a3b-i…")]       // no further than TrailerMinCells
    [InlineData("qwen3-30b-a3b-instruct-q8", "◕", 40, 20, "qwen3-30b-a3b-i… ◕")]   // the mark and its blank come off too
    [InlineData("llama", "", 40, 38, "llama")]                                       // a short name is never cut for the left
    [InlineData("qwen3-30b-a3b-instruct", "", 20, 30, "qwen3-30b…")]               // the half wins over the floor on a narrow row
    [InlineData("qwen3-30b-a3b-instruct", "◕", 20, 30, "qwen3-3… ◕")]
    public void Trail_GivesWayToTheLeft_IsPinned(string trailer, string mark, int cells, int leftCells, string expected) =>
        Assert.Equal(expected, ScreenPane.Trail(trailer, mark, cells, leftCells));

    [Theory]
    [InlineData("llama", "◕", "llama ◕")]
    [InlineData("llama", "", "llama")]
    [InlineData("", "◕", "◕")]
    [InlineData("", "", "")]
    public void TrailerText_IsPinned(string trailer, string mark, string expected) =>
        Assert.Equal(expected, ScreenPane.TrailerText(trailer, mark));

    [Fact]
    public void TheTrailer_IsAtTheRightEdge_InEveryState()
    {
        // 40 columns: 39 cells to the row, the trailer's mark on the last of them.
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Trailer = () => "llama";
        pane.TrailerMark = () => "◕";
        pane.Show();
        Assert.EndsWith("idle" + new string(' ', 28) + "llama ◕", Output);

        using (pane.BeginBusy("thinking"))
        {
            Assert.EndsWith(Theme.SpinnerFrames[0] + " thinking 00:00" + new string(' ', 16) + "llama ◕", Output);
            _time.Advance(ScreenPane.Tick);
            Assert.EndsWith(Theme.SpinnerFrames[1] + " thinking 00:00" + new string(' ', 16) + "llama ◕", Output);
        }

        Assert.EndsWith("idle" + new string(' ', 28) + "llama ◕", Output);

        pane.ShowOverlay(new Markup("a"), "Enter = save");
        Assert.EndsWith("Enter = save" + new string(' ', 20) + "llama ◕", Output);
        pane.CloseOverlay();
        Assert.EndsWith("idle" + new string(' ', 28) + "llama ◕", Output);
    }

    [Fact]
    public void ALongTrailer_GivesWayToTheLeft_InEveryState()
    {
        // 80 columns: 79 cells, the half-row cap 39 — 37 for the name ahead of the mark (the user's picture, 2026-09-28).
        _console.Profile.Width = 80;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Trailer = () => "Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-MTP-GGUF-Q8";
        pane.TrailerMark = () => "◕";
        pane.Show();
        Assert.EndsWith("idle" + new string(' ', 36) + "Qwen3.8-27B-Uncensored-HauhauCS-Aggr… ◕", Output);

        // A 50-cell overlay hint stays whole; the name takes the 25 left after the gap and the mark.
        pane.ShowOverlay(new Markup("a"), "Enter = move, rename, edit or delete · ESC = close");
        Assert.EndsWith("Enter = move, rename, edit or delete · ESC = close  Qwen3.8-27B-Uncensored-H… ◕", Output);
        pane.CloseOverlay();

        // So does a 50-cell spinner row.
        using (pane.BeginBusy("reading every file in the repository first"))
        {
            Assert.EndsWith(Theme.SpinnerFrames[0] + " reading every file in the repository first 00:00  Qwen3.8-27B-Uncensored-H… ◕", Output);
        }
    }

    [Fact]
    public void TheTrailerMark_IsDrawnInItsOwnStyle_IdleAndBusy()
    {
        _console.EmitAnsiSequences();
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Trailer = () => "llama";
        pane.TrailerMark = () => "◕";
        pane.Show();

        // The name rides the hint's colour; the mark opens its own right before the glyph.
        string mark = Sgr(Theme.Tertiary);
        string hint = Sgr(Theme.Dim);
        Assert.Contains(mark + "◕", Output);
        Assert.DoesNotContain(mark + "llama", Output);
        Assert.Contains(hint + "idle", Output);

        int at = Output.Length;
        using (pane.BeginBusy("thinking"))
        {
            Assert.Contains(mark + "◕", Output[at..]);
            Assert.DoesNotContain(mark + "llama", Output[at..]);
        }
    }

    /// <summary>The truecolor foreground sequence Spectre writes for <paramref name="color"/>.</summary>
    private static string Sgr(Color color) => $"\e[38;2;{color.R};{color.G};{color.B}m";

    [Fact]
    public void TheTrailerMark_ChangeIsPickedUpOnTheTick()
    {
        string mark = "";
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Trailer = () => "llama";
        pane.TrailerMark = () => mark;
        pane.Show();
        Assert.EndsWith("idle" + new string(' ', 30) + "llama", Output);

        mark = "◕";
        _time.Advance(ScreenPane.Tick);
        Assert.EndsWith("idle" + new string(' ', 28) + "llama ◕", Output);

        int length = Output.Length;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(length, Output.Length);

        mark = "";
        _time.Advance(ScreenPane.Tick);
        Assert.EndsWith("idle" + new string(' ', 30) + "llama", Output);
    }

    [Fact]
    public void TheTrailerMark_SurvivesTheCut_TheTextAheadOfIt()
    {
        _console.Profile.Width = 12;
        using var pane = Pane();
        pane.Hint = () => "a very long hint";
        pane.Trailer = () => "qwen3-30b-a3b";
        pane.TrailerMark = () => "◕";
        pane.Show();

        // 11 cells: the trailer at most 5 — the mark and its blank take 2, the text the 3 left.
        Assert.EndsWith("a v…  qw… ◕", Output);

        using (pane.BeginBusy("thinking"))
        {
            Assert.EndsWith(Theme.SpinnerFrames[0] + " t…  qw… ◕", Output);
        }
    }

    [Fact]
    public void TheTrailer_ChangeIsPickedUpOnTheTick()
    {
        string trailer = "llama (none)";
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Trailer = () => trailer;
        pane.Show();
        Assert.EndsWith("idle" + new string(' ', 23) + "llama (none)", Output);

        trailer = "llama (high)";
        _time.Advance(ScreenPane.Tick);
        Assert.EndsWith("idle" + new string(' ', 23) + "llama (high)", Output);

        // Nothing drawn on an unchanged tick.
        int length = Output.Length;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(length, Output.Length);

        // And gone with the trailer (the row redrawn in place: no padding after the hint).
        trailer = "";
        pane.RefreshHint();
        Assert.EndsWith("llama (high)idle", Output);
    }

    [Fact]
    public void TheTrailer_IsCutToHalfTheRow_AndTheHintAheadOfIt()
    {
        _console.Profile.Width = 12;
        using var pane = Pane();
        pane.Hint = () => "a very long hint";
        pane.Trailer = () => "qwen3-30b-a3b (high)";
        pane.Show();

        // 11 cells: the trailer at most 5, the gap 2, the hint the 4 left.
        Assert.EndsWith("a v…  qwen…", Output);

        using (pane.BeginBusy("thinking"))
        {
            Assert.EndsWith(Theme.SpinnerFrames[0] + " t…  qwen…", Output);
        }
    }

    // ── Resize ──────────────────────────────────────────────────────────────

    [Fact]
    public void ATallerWindow_RepadsOnTheNextTick()
    {
        _console.EmitAnsiSequences();
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("hello" + Environment.NewLine));
        Assert.Equal(5, pane.Padding);

        _console.Profile.Height = 14;
        int mark = Output.Length;
        _time.Advance(ScreenPane.Tick);

        Assert.Equal(9, pane.Padding);
        Assert.StartsWith("\e[?2026h\e[?25l\e[6A\e[40D\e[J", Output[mark..]);
        Assert.Contains(new string('\n', 9) + Rule(40) + "\n", Strip(Output[mark..]));
    }

    [Fact]
    public void AShorterWindow_ClampsTheFlowRow()
    {
        using var pane = Pane();
        pane.Show();
        for (int i = 0; i < 8; i++)
        {
            pane.Write(new Markup("x" + Environment.NewLine));
        }

        Assert.Equal(6, pane.FlowRow);
        _console.Profile.Height = 5;
        _time.Advance(ScreenPane.Tick);

        Assert.Equal(1, pane.FlowRow);
        Assert.Equal(0, pane.Padding);
    }

    // ── The alternate buffer and the scroll ─────────────────────────────────

    [Fact]
    public void Open_EntersTheAlternateBuffer_Close_LeavesIt_Once()
    {
        _console.EmitAnsiSequences();
        using var pane = Pane();
        pane.Open();
        Assert.Equal("\e[?1049h\e[?1007l", Output);
        pane.Open();
        Assert.Equal("\e[?1049h\e[?1007l", Output);   // already in it

        pane.Show();
        int mark = Output.Length;
        pane.Close();
        Assert.EndsWith("\e[?1007h\e[?1049l", Output);
        Assert.Contains("\r\n", Output[mark..]);       // the cursor under the pane first, as before

        mark = Output.Length;
        pane.Close();
        pane.Dispose();
        Assert.Equal(mark, Output.Length);              // left once
    }

    [Fact]
    public void Dispose_WithoutAClose_LeavesTheAlternateBuffer()
    {
        _console.EmitAnsiSequences();
        var pane = Pane();
        pane.Open();
        pane.Dispose();
        Assert.EndsWith("\e[?1007h\e[?1049l", Output);
    }

    [Fact]
    public void Disabled_Open_WritesNothing()
    {
        _console.EmitAnsiSequences();
        using var pane = Pane(geometry: false);
        pane.Open();
        pane.Close();
        Assert.Equal("", Output);
    }

    [Fact]
    public void Open_StartsTheFlowAtTheTop()
    {
        _cursorRow = 4;
        using var pane = Pane();
        Assert.Equal(4, pane.FlowRow);
        pane.Open();
        Assert.Equal(0, pane.FlowRow);
        _cursorRow = null;
        pane.Show();
        Assert.Equal(6, pane.Padding);
    }

    /// <summary>Twelve lines on a ten-row window: the flow at the bottom, six of them on the screen over the pane.</summary>
    private ScreenPane Scrollable(int lines = 12)
    {
        var pane = Pane();
        pane.Open();
        pane.Show();
        for (int i = 1; i <= lines; i++)
        {
            pane.Write(new Markup(Line(i) + Environment.NewLine));
        }

        return pane;
    }

    private static string Line(int i) => "L" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    private static string Lines(int from, int to) => string.Concat(Enumerable.Range(from, to - from + 1).Select(i => Line(i) + "\n"));

    /// <summary>The pane over an empty row, with <paramref name="hint"/> on the hint row (compared after a TrimEnd: an empty hint ends at the rule).</summary>
    private static string PaneRows(string hint) => Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + (hint.Length == 0 ? "" : "\n" + hint);

    /// <summary>The scrolled hint as the 40-column pane draws it: cut to the row with an ellipsis (the wording itself is pinned apart).</summary>
    private static string ScrolledRow(int below) => ScreenPane.Fit(ScreenPane.ScrolledHint(below), 39);

    [Fact]
    public void ScrollPage_ShowsTheStoresEarlierRows_ThePanePinned_TheHintCounting()
    {
        _console.EmitAnsiSequences();
        using var pane = Scrollable();
        Assert.Equal(12, pane.StoredRows);
        Assert.Equal(6, pane.FlowRow);
        Assert.False(pane.Scrolled);
        Assert.Equal(0, pane.RowsBelow);

        // A page = the region (6 rows) less one: from the bottom window (L07..L12) to L02..L07.
        int mark = Output.Length;
        pane.ScrollPage(-1);
        Assert.True(pane.Scrolled);
        Assert.Equal(1, pane.ScrollTop);
        Assert.Equal(5, pane.RowsBelow);
        Assert.Equal(0, pane.Padding);
        // Lifted from the bottom shape, then the screen erased from the top row, then the window and the pane.
        Assert.StartsWith("\e[?2026h\e[?25l\e[1A\e[40D\e[J\e[?25l\e[6A\e[40D\e[J", Output[mark..]);
        Assert.EndsWith(Lines(2, 7) + PaneRows(ScrolledRow(5)), Strip(Output[mark..]).TrimEnd());

        // Up again: the top, clamped.
        pane.ScrollPage(-1);
        Assert.Equal(0, pane.ScrollTop);
        Assert.Equal(6, pane.RowsBelow);
        mark = Output.Length;
        pane.ScrollPage(-1);
        Assert.Equal(mark, Output.Length);   // nothing to do
        Assert.EndsWith(Lines(1, 6) + PaneRows(ScrolledRow(6)), Strip(Output).TrimEnd());

        // Down a page: one row still below — singular.
        mark = Output.Length;
        pane.ScrollPage(1);
        Assert.Equal(5, pane.ScrollTop);
        Assert.Equal(1, pane.RowsBelow);
        Assert.StartsWith("\e[?2026h\e[?25l\e[7A\e[40D\e[J", Output[mark..]);   // the scrolled lift: from the cursor's row to the top
        Assert.EndsWith(Lines(6, 11) + PaneRows(ScrolledRow(1)), Strip(Output[mark..]).TrimEnd());

        // Down again: the bottom — the flow's tail written back, counted, the standing hint again.
        mark = Output.Length;
        pane.ScrollPage(1);
        Assert.False(pane.Scrolled);
        Assert.Equal(-1, pane.ScrollTop);
        Assert.Equal(6, pane.FlowRow);
        Assert.Equal(0, pane.FlowColumn);
        Assert.Equal(0, pane.Padding);
        Assert.EndsWith(Lines(7, 12) + PaneRows(""), Strip(Output[mark..]).TrimEnd());
    }

    [Fact]
    public void ScrolledHint_Wording()
    {
        Assert.Equal("⇡ 5 rows below · PgUp/PgDn scroll · Ctrl+End bottom", ScreenPane.ScrolledHint(5));
        Assert.Equal("⇡ 1 row below · PgUp/PgDn scroll · Ctrl+End bottom", ScreenPane.ScrolledHint(1));
        Assert.Equal("⇡ 0 rows below · PgUp/PgDn scroll · Ctrl+End bottom", ScreenPane.ScrolledHint(0));
    }

    /// <summary>Ctrl+Home's ScrollToTop (2026-09-18): the first window from wherever the view is; nothing at the top already, nothing on a transcript that fits; Ctrl+End the bottom again.</summary>
    [Fact]
    public void ScrollToTop_ShowsTheFirstRows_FromTheBottomOrMidway_AndIsNothingAtTheTop()
    {
        using var pane = Scrollable();
        pane.ScrollToTop();
        Assert.True(pane.Scrolled);
        Assert.Equal(0, pane.ScrollTop);
        Assert.Equal(6, pane.RowsBelow);
        Assert.EndsWith(Lines(1, 6) + PaneRows(ScrolledRow(6)), Strip(Output).TrimEnd());

        int mark = Output.Length;
        pane.ScrollToTop();
        Assert.Equal(mark, Output.Length);   // at the top already: nothing to do

        pane.ScrollToEnd();
        Assert.False(pane.Scrolled);
        pane.ScrollBy(-2);
        Assert.Equal(4, pane.ScrollTop);
        pane.ScrollToTop();
        Assert.Equal(0, pane.ScrollTop);

        using var fits = Scrollable(lines: 4);
        mark = Output.Length;
        fits.ScrollToTop();
        Assert.False(fits.Scrolled);
        Assert.Equal(mark, Output.Length);
    }

    [Fact]
    public void AShortTranscript_NeverScrolls()
    {
        using var pane = Scrollable(lines: 3);
        int mark = Output.Length;
        pane.ScrollPage(-1);
        pane.ScrollBy(-100);
        Assert.False(pane.Scrolled);
        Assert.Equal(mark, Output.Length);

        // Exactly the region: nothing above the window either.
        using var full = Scrollable(lines: 6);
        full.ScrollPage(-1);
        Assert.False(full.Scrolled);
    }

    [Fact]
    public void ScrollBy_MovesByRows_AndAnOpenLastLine_IsARow()
    {
        using var pane = Scrollable();
        pane.Write(new RawText("tail"));   // an open row: 13 rows now
        Assert.Equal(13, pane.StoredRows);

        pane.ScrollBy(-2);
        Assert.Equal(5, pane.ScrollTop);
        Assert.Equal(2, pane.RowsBelow);
        Assert.EndsWith(Lines(6, 11) + PaneRows(ScrolledRow(2)), Strip(Output).TrimEnd());

        // Back: the open row comes back as the flow's row and column (five closed rows over it).
        pane.ScrollBy(2);
        Assert.False(pane.Scrolled);
        Assert.Equal(5, pane.FlowRow);
        Assert.Equal(4, pane.FlowColumn);
        Assert.EndsWith(Lines(8, 12) + "tail\n" + PaneRows(""), Strip(Output).TrimEnd());
    }

    [Fact]
    public void WhileScrolled_AWrite_GoesToTheStore_AndOnlyTheHintIsRedrawn()
    {
        using var pane = Scrollable();
        pane.ScrollPage(-1);
        int draws = Draws;
        int mark = Output.Length;

        pane.Write(new Markup("L13" + Environment.NewLine));

        Assert.Equal(13, pane.StoredRows);
        Assert.Equal(1, pane.ScrollTop);      // the anchor stays: what is read does not move
        Assert.Equal(6, pane.RowsBelow);
        Assert.Equal(draws, Draws);
        Assert.DoesNotContain("L13", Output[mark..]);
        Assert.EndsWith(ScrolledRow(6), Output.TrimEnd());

        // Back at the bottom the new line is on the screen.
        pane.ScrollToEnd();
        Assert.False(pane.Scrolled);
        Assert.EndsWith(Lines(8, 13) + PaneRows(""), Strip(Output).TrimEnd());
        Assert.Equal(6, pane.FlowRow);
    }

    [Fact]
    public void WhileScrolled_TheLiveBlock_IsNotDrawn_ButCounts()
    {
        using var pane = Scrollable();
        pane.ScrollPage(-1);
        int mark = Output.Length;

        pane.SetLive(new Markup("live1\nlive2"));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(7, pane.RowsBelow);   // 5 stored rows below, the block's 2
        Assert.DoesNotContain("live1", Output[mark..]);
        Assert.EndsWith(ScrolledRow(7), Output.TrimEnd());
        Assert.Equal(0, pane.LiveRows);

        // Committed while scrolled: stored, the count the same; the tick draws nothing more.
        int draws = Draws;
        pane.CommitLive();
        Assert.Equal(14, pane.StoredRows);
        Assert.Equal(7, pane.RowsBelow);
        Assert.Equal(draws, Draws);
        Assert.DoesNotContain("live1", Output[mark..]);

        // The bottom: the block's lines are the flow's tail.
        pane.ScrollToEnd();
        Assert.EndsWith(Lines(9, 12) + "live1\nlive2\n" + PaneRows(""), Strip(Output).TrimEnd());
    }

    [Fact]
    public void WhileScrolled_ADiscardedLiveBlock_LeavesTheCount()
    {
        using var pane = Scrollable();
        pane.ScrollPage(-1);
        pane.SetLive(new Markup("live"));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(6, pane.RowsBelow);

        pane.DiscardLive();
        Assert.Equal(5, pane.RowsBelow);
        Assert.EndsWith(ScrolledRow(5), Output.TrimEnd());
    }

    [Fact]
    public void CommitInput_WhileScrolled_IsTheBottomAgain_ThenTheLine()
    {
        using var pane = Scrollable();
        pane.ScrollPage(-1);
        int mark = Output.Length;

        pane.CommitInput("hi");

        Assert.False(pane.Scrolled);
        // The tail written back with the pane, then the line as any flow write (the stream keeps both draws).
        Assert.Contains(Lines(7, 12) + PaneRows(""), Strip(Output[mark..]));
        Assert.EndsWith(InputLine.PromptGlyph + "hi\n" + PaneRows(""), Strip(Output).TrimEnd());
        Assert.Equal(6, pane.FlowRow);
    }

    [Fact]
    public void AResize_WhileScrolled_RebuildsTheWindow_AtTheBottom_TheTail()
    {
        _console.EmitAnsiSequences();
        using var pane = Scrollable();
        pane.ScrollPage(-1);
        Assert.Equal(1, pane.ScrollTop);

        // Taller: the region is 8 rows now, the anchor stays, the window L02..L09.
        _console.Profile.Height = 12;
        int mark = Output.Length;
        _time.Advance(ScreenPane.Tick);
        Assert.True(pane.Scrolled);
        Assert.Equal(1, pane.ScrollTop);
        Assert.Equal(3, pane.RowsBelow);
        Assert.Contains("\e[12A\e[40D\e[J", Output[mark..]);   // to the top whatever the old height, the screen erased
        Assert.EndsWith(Lines(2, 9) + PaneRows(ScrolledRow(3)), Strip(Output[mark..]).TrimEnd());

        // Taller still: the anchor is past the last window — the bottom, the tail written back.
        _console.Profile.Height = 20;
        mark = Output.Length;
        _time.Advance(ScreenPane.Tick);
        Assert.False(pane.Scrolled);
        Assert.Equal(12, pane.FlowRow);
        Assert.Equal(4, pane.Padding);
        Assert.EndsWith(Lines(1, 12) + new string('\n', 4) + PaneRows(""), Strip(Output[mark..]).TrimEnd());
    }

    [Fact]
    public void AnOverlay_WhileScrolled_TakesFromTheRegion()
    {
        using var pane = Scrollable();
        pane.ScrollBy(-100);
        Assert.Equal(0, pane.ScrollTop);

        // Three overlay rows: the pane is 6 rows, the region 4 — L01..L04 over it.
        pane.ShowOverlay(new Markup("a\nb\nc"), "menu");
        Assert.Equal(0, pane.ScrollTop);
        Assert.Equal(8, pane.RowsBelow);
        Assert.EndsWith(Lines(1, 4) + Rule(40) + "\na\nb\nc\n" + Rule(40) + "\nmenu", Strip(Output).TrimEnd());

        pane.CloseOverlay();
        Assert.Equal(6, pane.RowsBelow);
        Assert.EndsWith(Lines(1, 6) + PaneRows(ScrolledRow(6)), Strip(Output).TrimEnd());
    }

    [Fact]
    public void ScrollPage_UnderABatch_OnlyMovesTheAnchor_TheBatchDrawsIt()
    {
        using var pane = Scrollable();
        int draws;
        using (pane.Batch())
        {
            draws = Draws;
            pane.ScrollPage(-1);
            Assert.True(pane.Scrolled);
            Assert.Equal(draws, Draws);
        }

        Assert.Equal(draws + 1, Draws);
        Assert.EndsWith(Lines(2, 7) + PaneRows(ScrolledRow(5)), Strip(Output).TrimEnd());
    }

    [Fact]
    public void Clear_WhileScrolled_ForgetsTheStore_AndIsTheBottom()
    {
        using var pane = Scrollable();
        pane.ScrollPage(-1);

        pane.Clear(home: true);
        Assert.False(pane.Scrolled);
        Assert.Equal(0, pane.StoredRows);
        pane.Show();
        Assert.Equal(6, pane.Padding);
    }

    [Fact]
    public void WhileScrolled_UnderASpinner_TheCountRidesTheBusyRow()
    {
        _console.Profile.Width = 80;   // room for the whole row
        using var pane = Scrollable();
        using var busy = pane.BeginBusy("thinking");
        pane.ScrollPage(-1);

        Assert.EndsWith(ScreenPane.HintSeparator + ScreenPane.ScrolledHint(5), Output.TrimEnd());
        Assert.Contains("thinking 00:00", Output);
    }

    // ── Modal ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Modal_LiftsThePane_LetsTheWorkDraw_AndDrawsItAgain()
    {
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("before" + Environment.NewLine));
        int draws = Draws;

        int result = await pane.ModalAsync(() =>
        {
            pane.Write(new Markup("menu" + Environment.NewLine));
            Assert.Equal(draws, Draws);
            return Task.FromResult(42);
        });

        Assert.Equal(42, result);
        Assert.Equal(1, pane.FlowRow);   // the menu's rows were not counted: it erases itself
        Assert.Equal(draws + 1, Draws);
    }

    [Fact]
    public async Task ModalAsync_Static_FindsThePaneBehindAConsoleWithInput()
    {
        using var pane = Pane();
        pane.Show();
        var wrapped = new ConsoleWithInput(pane, _console.Input);
        int draws = Draws;

        await ScreenPane.ModalAsync(wrapped, () => Task.FromResult(0));
        Assert.Equal(draws + 1, Draws);

        using var plain = new TestConsole();
        Assert.Equal(1, await ScreenPane.ModalAsync(plain, () => Task.FromResult(1)));
    }

    // ── Overlay ─────────────────────────────────────────────────────────────

    [Fact]
    public void ShowOverlay_LiftsThePane_DrawsRuleContentAndHint_HidesTheCursor()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 6;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();
        int mark = Output.Length;

        pane.ShowOverlay(new Markup("a\nb"), "hint");
        string written = Output[mark..];

        // Up over the padding and the rule (3 rows), erase down; one padding row now, the rule,
        // the content, the lower rule, the overlay's hint; the cursor left hidden on the first content row.
        Assert.StartsWith("\e[?2026h\e[?25l\e[3A\e[20D\e[J", written);
        Assert.Contains("\n" + Rule(20) + "\na\nb\n" + Rule(20) + "\nhint", Strip(written));
        Assert.EndsWith("\e[K\e[3A\e[20D\e[?2026l", written);
        Assert.DoesNotContain("\e[?25h", written);
        Assert.True(pane.OverlayOpen);
        Assert.Equal(2, pane.OverlayRows);
        Assert.Equal(1, pane.Padding);
    }

    [Fact]
    public void ShowOverlay_ClampsToTheWindow_KeepingOneTranscriptRow()
    {
        using var pane = Pane();
        pane.Show();

        pane.ShowOverlay(new Markup(string.Join('\n', Enumerable.Range(1, 30).Select(i => "line " + i))), "hint");

        // 10 rows: one for the transcript, the rule, 6 content rows, the lower rule, the hint.
        Assert.Equal(6, pane.OverlayRows);
        Assert.Equal(1, pane.Padding);
        Assert.Contains("line 6\n" + Rule(40) + "\nhint", Output);
        Assert.DoesNotContain("line 7", Output);
    }

    [Fact]
    public void ShowOverlay_Again_ReplacesTheContent()
    {
        using var pane = Pane();
        pane.Show();
        pane.ShowOverlay(new Markup("first"), "hint");
        int mark = Output.Length;

        pane.ShowOverlay(new Markup("second\nrow"), "hint");

        string written = Output[mark..];
        Assert.Contains(Rule(40) + "\nsecond\nrow\n" + Rule(40) + "\nhint", written);
        Assert.DoesNotContain("first", written);
        Assert.Equal(2, pane.OverlayRows);
    }

    [Fact]
    public void CloseOverlay_DrawsTheInputRowAgain_AndShowsTheCursor()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 6;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();
        pane.ShowOverlay(new Markup("a\nb"), "hint");
        int mark = Output.Length;

        pane.CloseOverlay();
        string written = Output[mark..];

        // Up over one padding row and the rule from the first content row; the normal pane again.
        Assert.StartsWith("\e[?2026h\e[?25l\e[2A\e[20D\e[J", written);
        Assert.Contains("\n\n" + Rule(20) + "\n› \n" + Rule(20) + "\nidle", Strip(written));
        Assert.EndsWith("\e[K\e[2A\e[20D\e[2C\e[?25h\e[?2026l", written);
        Assert.False(pane.OverlayOpen);
        Assert.Equal(0, pane.OverlayRows);
        Assert.Equal(2, pane.Padding);

        // A second close is nothing.
        mark = Output.Length;
        pane.CloseOverlay();
        Assert.Equal(mark, Output.Length);
    }

    [Fact]
    public void AFlowWrite_WhileTheOverlayIsOpen_KeepsIt()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 6;
        using var pane = Pane();
        pane.Show();
        pane.ShowOverlay(new Markup("a\nb"), "hint");
        int mark = Output.Length;

        pane.Write(new Markup("hi" + Environment.NewLine));
        string written = Output[mark..];

        Assert.StartsWith("\e[?2026h\e[?25l\e[2A\e[20D\e[J", written);
        Assert.Contains("hi\n" + Rule(20) + "\na\nb\n" + Rule(20) + "\nhint", Strip(written));
        Assert.EndsWith("\e[K\e[3A\e[20D\e[?2026l", written);
        Assert.DoesNotContain("\e[?25h", written);
        Assert.Equal(1, pane.FlowRow);
        Assert.Equal(0, pane.Padding);
    }

    [Fact]
    public void ATallerWindow_WhileOpen_LaysTheOverlayOutAgain()
    {
        using var pane = Pane();
        pane.Show();
        pane.ShowOverlay(new Markup(string.Join('\n', Enumerable.Range(1, 30).Select(i => "line " + i))), "hint");
        Assert.Equal(6, pane.OverlayRows);

        _console.Profile.Height = 14;
        _time.Advance(ScreenPane.Tick);

        Assert.Equal(10, pane.OverlayRows);
        Assert.Contains("line 10\n" + Rule(40) + "\nhint", Output);
    }

    [Fact]
    public void TheTick_LeavesTheOverlayAlone()
    {
        string hint = "idle";
        using var pane = Pane();
        pane.Hint = () => hint;
        pane.Show();
        pane.ShowOverlay(new Markup("a"), "pane hint");
        int mark = Output.Length;

        // The standing hint changed, but the overlay's hint is on the screen: nothing is redrawn.
        hint = "changed";
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(mark, Output.Length);

        pane.RefreshHint();
        Assert.Equal(mark, Output.Length);

        // Closed, the standing hint comes back as it is now.
        pane.CloseOverlay();
        Assert.EndsWith("› \n" + Rule(40) + "\nchanged", Output);
    }

    [Fact]
    public void TheInputRow_WhileOpen_IsRememberedNotDrawn()
    {
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();
        pane.ShowOverlay(new Markup("a"), "hint");
        int mark = Output.Length;

        pane.PreviewInput("typed");
        pane.ShowInput("ab", 2);
        pane.ClearInput();
        pane.ShowInput("cd", 1);
        Assert.Equal(mark, Output.Length);

        pane.CloseOverlay();
        Assert.EndsWith("› cd\n" + Rule(40) + "\nidle", Output);
    }

    [Fact]
    public void ShowOverlay_InsideABatch_DrawsWhenTheBatchEnds()
    {
        using var pane = Pane();
        pane.Show();
        int draws = Draws;

        using (pane.Batch())
        {
            pane.ShowOverlay(new Markup("a"), "hint");
            Assert.Equal(draws, Draws);
        }

        Assert.Equal(draws + 1, Draws);
        Assert.Equal(1, pane.OverlayRows);
    }

    [Fact]
    public void Close_WhileOpen_StepsUnderTheHint_AndShowsTheCursor()
    {
        _console.EmitAnsiSequences();
        using var pane = Pane();
        pane.Show();
        pane.ShowOverlay(new Markup("a\nb"), "hint");
        int mark = Output.Length;

        pane.Close();

        // Over the two content rows and the lower rule to the hint row.
        Assert.Equal("\e[3B\r\n\e[?25h", Output[mark..]);
        Assert.False(pane.OverlayOpen);
    }

    [Fact]
    public void ShowOverlay_Disabled_IsNothing()
    {
        using var pane = Pane(geometry: false);
        pane.ShowOverlay(new Markup("a"), "hint");
        pane.CloseOverlay();
        Assert.Equal("", Output);
        Assert.False(pane.OverlayOpen);
    }

    // ── Close ───────────────────────────────────────────────────────────────

    [Fact]
    public void Close_LeavesThePane_AndPutsTheCursorUnderIt()
    {
        _console.EmitAnsiSequences();
        using var pane = Pane();
        pane.Show();
        int mark = Output.Length;
        pane.Close();

        // Over the lower rule to the hint row, then under it.
        Assert.Equal("\e[2B\r\n", Output[mark..]);
    }

    // ── Mouse ───────────────────────────────────────────────────────────────

    /// <summary>The two-row draft on the screen with the terminal's cursor reported at buffer row <paramref name="cursorTop"/>.</summary>
    private ScreenPane PaneWithDraft(int cursorTop, string text, int cursor)
    {
        var pane = Pane();
        pane.Show();
        pane.ShowInput(text, cursor);
        _cursorTop = cursorTop;
        return pane;
    }

    [Fact]
    public void TryHitInput_MapsAClickToTheDraft()
    {
        // The cursor at the end of the draft is on the second row (area rows 100 and 101 in the buffer).
        using var pane = PaneWithDraft(101, Long, Long.Length);

        Assert.True(pane.TryHitInput(2, 100, out int at));      // first cell of row 0, after the glyph
        Assert.Equal(0, at);
        Assert.True(pane.TryHitInput(6, 100, out at));          // between "the " and "quick"
        Assert.Equal(4, at);
        Assert.True(pane.TryHitInput(0, 100, out at));          // on the glyph: the start
        Assert.Equal(0, at);
        Assert.True(pane.TryHitInput(2 + 5, 101, out at));      // row 1: "lazy " | "dog"
        Assert.Equal(Long.IndexOf("dog", StringComparison.Ordinal), at);
        Assert.True(pane.TryHitInput(39, 100, out at));         // past row 0's end: the dropped space's slot
        Assert.Equal(Row0.Length, at);
        Assert.True(pane.TryHitInput(39, 101, out at));         // past the draft's end
        Assert.Equal(Long.Length, at);

        // The element under the pointer (later on 2026-09-30, the double-click's word): the character itself, and past a row's
        // end its last character, never the space the wrap dropped.
        Assert.True(pane.TryHitInput(6, 100, out at, out int under));
        Assert.Equal((4, 4), (at, under));
        Assert.True(pane.TryHitInput(39, 100, out at, out under));
        Assert.Equal((Row0.Length, Row0.Length - 1), (at, under));
        Assert.True(pane.TryHitInput(39, 101, out _, out under));
        Assert.Equal(Long.Length - 1, under);
    }

    [Fact]
    public void TryHitInput_OutsideTheArea_IsFalse()
    {
        using var pane = PaneWithDraft(101, Long, Long.Length);

        Assert.False(pane.TryHitInput(5, 99, out _));    // the upper rule
        Assert.False(pane.TryHitInput(5, 102, out _));   // the lower rule
        Assert.False(pane.TryHitInput(5, 50, out _));    // the transcript
        _cursorTop = null;
        Assert.False(pane.TryHitInput(5, 100, out _));   // the console cannot say where the cursor is
    }

    [Fact]
    public void TryHitInput_FollowsTheCursorRow()
    {
        // Home: the cursor is on row 0, so the area starts at the reported row itself.
        using var pane = PaneWithDraft(100, Long, 0);
        Assert.True(pane.TryHitInput(4, 101, out int at));
        Assert.Equal(Row0.Length + 1 + 2, at);
    }

    [Fact]
    public void TryHitInput_OnAWideCharacter_LandsBeforeIt_AndACellsBreakKeepsTheRow()
    {
        _console.Profile.Width = 20;   // 17 cells per row
        using var pane = PaneWithDraft(100, "ab日本", 0);
        Assert.True(pane.TryHitInput(2 + 2, 100, out int at));   // the left half of 日
        Assert.Equal(2, at);
        Assert.True(pane.TryHitInput(2 + 3, 100, out at));       // its right half: still before it
        Assert.Equal(2, at);
        Assert.True(pane.TryHitInput(2 + 4, 100, out at));       // 本
        Assert.Equal(3, at);

        // 30 letters break by cells at 17: a click past row 0's end stays on row 0, before its last letter.
        pane.ShowInput(new string('a', 30), 0);
        Assert.Equal(2, pane.InputRows);
        Assert.True(pane.TryHitInput(19, 100, out at));
        Assert.Equal(16, at);
        Assert.True(pane.TryHitInput(2 + 16, 100, out at));      // the last cell of row 0 itself
        Assert.Equal(16, at);
        Assert.True(pane.TryHitInput(2, 101, out at));           // row 1's first cell
        Assert.Equal(17, at);
    }

    [Fact]
    public void TryHitInput_WithTheViewportScrolled_UsesTheShownRows()
    {
        var words = Enumerable.Range(0, 6).Select(i => new string((char)('a' + i), 30)).ToArray();
        string text = string.Join(" ", words);
        using var pane = PaneWithDraft(104, text, text.Length);   // 5 rows shown (b..f), cursor on the last
        Assert.Equal(5, pane.InputRows);

        Assert.True(pane.TryHitInput(2, 100, out int at));   // the first shown row is "b…"
        Assert.Equal(31, at);
        Assert.True(pane.TryHitInput(2 + 3, 102, out at));   // "d…" + 3
        Assert.Equal(93 + 3, at);
    }

    [Fact]
    public void TryHitInput_UnderAnOverlay_OrLifted_OrDisabled_IsFalse()
    {
        using var pane = PaneWithDraft(101, Long, Long.Length);
        pane.ShowOverlay(new Markup("a"), "hint");
        Assert.False(pane.TryHitInput(2, 100, out _));
        pane.CloseOverlay();
        Assert.True(pane.TryHitInput(2, 100, out _));

        using (pane.Batch())
        {
            Assert.False(pane.TryHitInput(2, 100, out _));
        }

        using var plain = Pane(geometry: false);
        Assert.False(plain.TryHitInput(2, 0, out _));
    }

    [Fact]
    public void Touch_WritesSomething_ThatDrawsNothingNew()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 6;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();
        int mark = Output.Length;

        pane.Touch();

        // The hint row again, in place, and the cursor back.
        Assert.Equal("idle", Strip(Output[mark..]));
        Assert.EndsWith("\e[K\e[2A\e[20D\e[2C\e[?25h\e[?2026l", Output);   // one synchronized frame (2026-09-29)

        // Under an overlay: the hint row again, the cursor left hidden on the overlay's first row.
        pane.ShowOverlay(new Markup("a"), "hint");
        mark = Output.Length;
        pane.Touch();
        Assert.Equal("hint", Strip(Output[mark..]));
        Assert.EndsWith("\e[K\e[2A\e[20D\e[?2026l", Output);
        Assert.DoesNotContain("\e[?25h", Output[mark..]);

        using var plain = Pane(geometry: false);
        mark = Output.Length;
        plain.Touch();
        Assert.Equal(mark, Output.Length);
    }

    [Fact]
    public void Input_IsNotForReading_WhenThePaneIsOn()
    {
        using var pane = Pane();
        Assert.Throws<InvalidOperationException>(() => pane.Input);
        using var plain = Pane(geometry: false);
        Assert.Same(_console.Input, plain.Input);
    }

    // ── Statics ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hello", 10, "hello")]
    [InlineData("hello", 5, "hello")]
    [InlineData("hello", 4, "hel…")]
    [InlineData("hello", 1, "…")]
    [InlineData("hello", 0, "")]
    [InlineData("日本語", 4, "日…")]
    public void Fit_IsPinned(string text, int cells, string expected) => Assert.Equal(expected, ScreenPane.Fit(text, cells));

    [Theory]
    [InlineData("hello", 10, "hello")]
    [InlineData("hello", 5, "hello")]
    [InlineData("hello", 4, "…llo")]
    [InlineData("hello", 1, "…")]
    [InlineData("hello", 0, "")]
    [InlineData("日本語", 4, "…語")]
    [InlineData("日本語", 3, "…語")]
    [InlineData("日本語", 2, "…")]
    [InlineData("xa😀b", 4, "…😀b")]
    [InlineData("xa😀b", 3, "…b")]     // the wide glyph does not fit the two cells left
    [InlineData(@"D:\Repo\NeonSidekick\src", 17, @"…NeonSidekick\src")]
    [InlineData(@"D:\Repo\NeonSidekick\src", 19,@"…o\NeonSidekick\src")]
    public void FitTail_KeepsTheEnd_IsPinned(string text, int cells, string expected) => Assert.Equal(expected, ScreenPane.FitTail(text, cells));

    [Fact]
    public void MeasureRows_CountsTheRowsARenderableTakes()
    {
        Assert.Equal(1, ScreenPane.MeasureRows(new Markup("one"), _console, 40));
        Assert.Equal(3, ScreenPane.MeasureRows(new Markup("a\nb\nc"), _console, 40));
        Assert.Equal(2, ScreenPane.MeasureRows(new Text(new string('x', 50)), _console, 40));
    }

    // ── Overlay with an input slot ──────────────────────────────────────────

    [Fact]
    public void ShowOverlay_WithInput_KeepsTheInputRowUnderTheContent_AndTheCursorOnIt()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 8;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();
        int mark = Output.Length;

        pane.ShowOverlay(new Markup("a\nb"), "hint", input: true);
        string written = Output[mark..];

        // Rule, the two content rows, the input row, the lower rule, the hint; the cursor back on
        // the input row after the glyph, shown.
        Assert.Contains("\n" + Rule(20) + "\na\nb\n› \n" + Rule(20) + "\nhint", Strip(written));
        Assert.EndsWith("\e[K\e[2A\e[20D\e[2C\e[?25h\e[?2026l", written);
        Assert.True(pane.OverlayOpen);
        Assert.True(pane.OverlayHasInput);
        Assert.Equal(2, pane.OverlayRows);
        Assert.Equal(1, pane.InputRows);
        Assert.Equal(2, pane.Padding);
    }

    [Fact]
    public void ShowOverlay_WithInput_LeavesRoomForTheInputRows()
    {
        using var pane = Pane();
        pane.Show();
        pane.ShowInput(string.Join(' ', Enumerable.Repeat("word", 12)), 0);   // two rows at width 40

        pane.ShowOverlay(new Markup(string.Join('\n', Enumerable.Range(1, 30).Select(i => "line " + i))), "hint", input: true);

        // 10 rows: one for the transcript, the rule, 4 content rows, 2 input rows, the lower rule, the hint.
        Assert.Equal(2, pane.InputRows);
        Assert.Equal(4, pane.OverlayRows);
        Assert.Contains("line 4\n› word", Output);
        Assert.DoesNotContain("line 5", Output);
    }

    // A shape change lifts from where the LAST draw left the cursor (the drawn state), never from
    // where the new shape will put it: a slot-to-list switch must clear the old top rule and title.
    [Fact]
    public void ShowOverlay_SlotThenNoSlot_LiftsFromTheInputRow()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 8;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();
        pane.ShowOverlay(new Markup("a\nb"), "hint", input: true);
        Assert.Equal(2, pane.Padding);
        int mark = Output.Length;

        pane.ShowOverlay(new Markup("a\nb"), "hint");
        string written = Output[mark..];

        // From the input row: over the two content rows, the rule and the two padding rows = 5.
        Assert.StartsWith("\e[?2026h\e[?25l\e[5A\e[20D\e[J", written);
        Assert.Contains("\n\n\n" + Rule(20) + "\na\nb\n" + Rule(20) + "\nhint", Strip(written));
        Assert.EndsWith("\e[K\e[3A\e[20D\e[?2026l", written);
        Assert.False(pane.OverlayHasInput);
        Assert.Equal(3, pane.Padding);
    }

    [Fact]
    public void ShowOverlay_NoSlotThenSlot_LiftsFromTheFirstContentRow()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 8;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();
        pane.ShowOverlay(new Markup("a\nb"), "hint");
        Assert.Equal(3, pane.Padding);
        int mark = Output.Length;

        pane.ShowOverlay(new Markup("a\nb"), "hint", input: true);
        string written = Output[mark..];

        // From the first content row: the rule and the three padding rows = 4, not a row more.
        Assert.StartsWith("\e[?2026h\e[?25l\e[4A\e[20D\e[J", written);
        Assert.Contains("\n\n" + Rule(20) + "\na\nb\n› \n" + Rule(20) + "\nhint", Strip(written));
        Assert.EndsWith("\e[K\e[2A\e[20D\e[2C\e[?25h\e[?2026l", written);
        Assert.True(pane.OverlayHasInput);
    }

    [Fact]
    public void CloseOverlay_FromASlot_LiftsFromTheInputRow()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 8;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();
        pane.ShowOverlay(new Markup("a\nb"), "hint", input: true);
        pane.ShowInput("xy", 2);
        int mark = Output.Length;

        pane.CloseOverlay();
        string written = Output[mark..];

        Assert.StartsWith("\e[?2026h\e[?25l\e[5A\e[20D\e[J", written);
        Assert.Contains("\n\n\n\n" + Rule(20) + "\n› xy\n" + Rule(20) + "\nidle", Strip(written));
        Assert.EndsWith("\e[K\e[2A\e[20D\e[4C\e[?25h\e[?2026l", written);
        Assert.False(pane.OverlayOpen);
    }

    [Fact]
    public void MaxOverlayRows_IsPinned()
    {
        Assert.Equal(6, ScreenPane.MaxOverlayRows(10, 0));
        Assert.Equal(5, ScreenPane.MaxOverlayRows(10, 1));
        Assert.Equal(20, ScreenPane.MaxOverlayRows(24, 0));
        Assert.Equal(0, ScreenPane.MaxOverlayRows(4, 0));
        Assert.Equal(0, ScreenPane.MaxOverlayRows(3, 2));
    }

    [Fact]
    public void ShowInput_UnderAnOverlayWithInput_RewritesTheRowInPlace()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 8;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();
        pane.ShowOverlay(new Markup("a\nb"), "hint", input: true);
        int mark = Output.Length;

        pane.ShowInput("cd", 2);

        // In place: no lift, no rule; the text after the glyph and the cursor after it.
        string written = Output[mark..];
        Assert.DoesNotContain(Rule(20), Strip(written));
        Assert.Equal("cd", Strip(written));
        Assert.True(pane.OverlayHasInput);

        // A second row lifts and draws again with the overlay above it.
        mark = Output.Length;
        pane.ShowInput("one two three four five", 23);
        Assert.Contains(Rule(20) + "\na\nb\n› one two three\n  four five\n" + Rule(20) + "\nhint", Strip(Output[mark..]));
        Assert.Equal(2, pane.InputRows);
    }

    [Fact]
    public void CommitInput_UnderAnOverlayWithInput_WritesNothingIntoTheFlow_AndEmptiesTheSlot()
    {
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();
        pane.ShowOverlay(new Markup("a"), "hint", input: true);
        pane.ShowInput("value", 5);
        int flowRow = pane.FlowRow;
        int mark = Output.Length;

        pane.CommitInput("value");

        Assert.DoesNotContain("› value", Output[mark..]);
        Assert.Equal(flowRow, pane.FlowRow);
        Assert.True(pane.OverlayOpen);
        Assert.True(pane.OverlayHasInput);

        // Closed, the input row comes back empty: nothing of the value survives.
        pane.CloseOverlay();
        Assert.EndsWith("› \n" + Rule(40) + "\nidle", Output);
    }

    [Fact]
    public void TryHitInput_UnderAnOverlayWithInput_MapsTheSlotRow()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        pane.ShowOverlay(new Markup("a\nb"), "hint");
        pane.ShowInput("abc", 3);
        Assert.False(pane.TryHitInput(3, 100, out _));

        pane.ShowOverlay(new Markup("a\nb"), "hint", input: true);
        pane.ShowInput("abc", 3);

        // The terminal's cursor is on the input row (buffer row 100): the click's column past the glyph.
        Assert.True(pane.TryHitInput(3, 100, out int index));
        Assert.Equal(1, index);
        Assert.False(pane.TryHitInput(3, 99, out _));   // the content row above it
    }

    /// <summary>The hint row is the pane's last: under the lower rule, wherever the draft's rows put it, any column; nothing else answers (2026-09-18).</summary>
    [Fact]
    public void TryHitHint_IsThePanesLastRow_AnyColumn()
    {
        // A two-row draft, the cursor at its end (row 1 = buffer 101): rule 99, rows 100–101, rule 102, hint 103.
        using var pane = PaneWithDraft(101, Long, Long.Length);

        Assert.True(pane.TryHitHint(0, 103));
        Assert.True(pane.TryHitHint(39, 103));
        Assert.False(pane.TryHitHint(5, 102));    // the lower rule
        Assert.False(pane.TryHitHint(5, 101));    // the draft
        Assert.False(pane.TryHitHint(5, 99));     // the upper rule
        Assert.False(pane.TryHitHint(5, 104));    // under the pane
        Assert.False(pane.TryHitHint(5, 50));     // the transcript

        // Home: the cursor on row 0 (buffer 100), the hint two rows further down.
        pane.ShowInput(Long, 0);
        _cursorTop = 100;
        Assert.True(pane.TryHitHint(0, 103));
        Assert.False(pane.TryHitHint(0, 102));

        // A one-row draft: rule 99, row 100, rule 101, hint 102.
        pane.ShowInput("abc", 3);
        Assert.True(pane.TryHitHint(7, 102));
        Assert.False(pane.TryHitHint(7, 103));

        _cursorTop = null;
        Assert.False(pane.TryHitHint(0, 102));    // the console cannot say where the cursor is
    }

    /// <summary>The zones of the standing row (2026-09-18): each strip glyph over its two cells (the separator between them is the row), the trailer from its first cell to the edge, the row elsewhere; pinned through HintHitAt and read through the drawn row.</summary>
    [Fact]
    public void TryHitHint_NamesTheStripGlyph_TheTrailer_OrTheRow()
    {
        // Pure: "🔊 🎤" at 0–1, 2 (a blank), 3–4; the trailer from column 30.
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", 0), ScreenPane.HintHitAt("🔊 🎤", 30, 0));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", 0), ScreenPane.HintHitAt("🔊 🎤", 30, 1));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1), ScreenPane.HintHitAt("🔊 🎤", 30, 2));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🎤", 3), ScreenPane.HintHitAt("🔊 🎤", 30, 3));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🎤", 3), ScreenPane.HintHitAt("🔊 🎤", 30, 4));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1), ScreenPane.HintHitAt("🔊 🎤", 30, 5));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1), ScreenPane.HintHitAt("🔊 🎤", 30, 29));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Trailer, "", 30), ScreenPane.HintHitAt("🔊 🎤", 30, 30));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Trailer, "", 30), ScreenPane.HintHitAt("🔊 🎤", 30, 38));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1), ScreenPane.HintHitAt("", -1, 0));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1), ScreenPane.HintHitAt("🔊", -1, 38));

        // Drawn: a 40-cell pane, the row at 39 cells, the trailer "llama ◕" (7 cells) from column 32.
        _cursorTop = 100;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Strip = () => "🔊 🎤";
        pane.Trailer = () => "llama";
        pane.TrailerMark = () => "◕";
        pane.Show();
        pane.ShowInput("abc", 3);
        Assert.True(pane.TryHitHint(0, 102, out var hit));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", 0), hit);
        Assert.True(pane.TryHitHint(4, 102, out hit));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🎤", 3), hit);
        Assert.True(pane.TryHitHint(8, 102, out hit));   // "idle"
        Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);
        Assert.True(pane.TryHitHint(31, 102, out hit));
        Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);
        Assert.True(pane.TryHitHint(32, 102, out hit));  // the l of llama
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Trailer, "", 32), hit);
        Assert.True(pane.TryHitHint(37, 102, out hit));  // the separator before the mark: the name's zone still
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Trailer, "", 32), hit);
        Assert.True(pane.TryHitHint(38, 102, out hit));  // the mark: its own zone since 2026-09-21 (/reasoning)
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Mark, "", 38), hit);
        Assert.False(pane.TryHitHint(32, 101, out _));

        // Without a mark (no reasoning glyph drawn) the trailer runs to the edge.
        pane.TrailerMark = () => "";
        pane.RefreshHint();
        Assert.True(pane.TryHitHint(38, 102, out hit));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Trailer, "", 34), hit);
        pane.TrailerMark = () => "◕";

        // No model: the right edge is the row.
        pane.Trailer = () => "";
        pane.TrailerMark = () => "";
        pane.RefreshHint();
        Assert.True(pane.TryHitHint(38, 102, out hit));
        Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);
    }

    /// <summary>The usage zone (2026-09-21): the cells the tally takes on the drawn standing row, behind the trailer and the queued part; nowhere when the hint does not carry it (the timers in its place), when it is cut, or under the busy row's scroll.</summary>
    [Fact]
    public void HintHitAt_TheUsageZone_IsTheTallysCells()
    {
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Usage, "", 5), ScreenPane.HintHitAt("🔊", 30, -1, 0, 5, 11, 5));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Usage, "", 5), ScreenPane.HintHitAt("🔊", 30, -1, 0, 5, 11, 15));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1), ScreenPane.HintHitAt("🔊", 30, -1, 0, 5, 11, 16));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1), ScreenPane.HintHitAt("🔊", 30, -1, 0, 5, 11, 4));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", 0), ScreenPane.HintHitAt("🔊", 30, -1, 0, 5, 11, 1));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Trailer, "", 30), ScreenPane.HintHitAt("🔊", 30, -1, 0, 5, 11, 30));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Queued, "", 3), ScreenPane.HintHitAt("🔊", 30, 3, 8, 5, 11, 6));   // the queued part first
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Scrolled, "", -1), ScreenPane.HintHitAt("🔊", 30, -1, 0, -1, 0, 6, scrolled: true));
        Assert.Equal(ScreenPane.HintHitAt("🔊", 30, 5, 11, 15), ScreenPane.HintHitAt("🔊", 30, 5, 11, -1, 0, 15));
    }

    /// <summary>The mark zone (2026-09-21): from the mark's column to the row's end, ahead of the trailer, which is then the name and the separator; −1 for none.</summary>
    [Fact]
    public void HintHitAt_TheMarkZone_IsTheRowsLastCells()
    {
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Mark, "", 38), ScreenPane.HintHitAt("🔊", 30, 38, -1, 0, -1, 0, 38));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Mark, "", 38), ScreenPane.HintHitAt("🔊", 30, 38, -1, 0, -1, 0, 39));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Trailer, "", 30), ScreenPane.HintHitAt("🔊", 30, 38, -1, 0, -1, 0, 37));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Trailer, "", 30), ScreenPane.HintHitAt("🔊", 30, 38, -1, 0, -1, 0, 30));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1), ScreenPane.HintHitAt("🔊", 30, 38, -1, 0, -1, 0, 29));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Trailer, "", 30), ScreenPane.HintHitAt("🔊", 30, -1, -1, 0, -1, 0, 38));   // no mark: the trailer to the edge
        Assert.Equal(ScreenPane.HintHitAt("🔊", 30, 5, 11, -1, 0, 15), ScreenPane.HintHitAt("🔊", 30, -1, 5, 11, -1, 0, 15));
    }

    /// <summary>The drawn rows: the tally the Usage delegate names is its zone where HintText put it; the timers in its place, or an empty tally, leave the row; the busy row's spinner and label are the zone too, the rest of that row the row.</summary>
    [Fact]
    public void TryHitHint_NamesUsage_OnTheTally_AndOnTheBusyRowsSpinner()
    {
        _cursorTop = 100;
        using var pane = Pane();
        string usage = "1.2k / 4.1k · 30%";
        pane.Hint = () => usage;
        pane.Usage = () => usage;
        pane.Strip = () => "🔊";
        pane.Trailer = () => "llama";
        pane.Show();
        pane.ShowInput("abc", 3);
        Assert.True(pane.TryHitHint(0, 102, out var hit));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", 0), hit);
        Assert.True(pane.TryHitHint(4, 102, out hit));    // the separator
        Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);
        Assert.True(pane.TryHitHint(5, 102, out hit));    // the 1 of 1.2k: "🔊 · " is five cells
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Usage, "", 5), hit);
        Assert.True(pane.TryHitHint(21, 102, out hit));   // the %
        Assert.Equal(ScreenPane.HintZone.Usage, hit.Zone);
        Assert.True(pane.TryHitHint(22, 102, out hit));
        Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);
        Assert.True(pane.TryHitHint(34, 102, out hit));
        Assert.Equal(ScreenPane.HintZone.Trailer, hit.Zone);

        // The timers took the tally's place: no zone.
        pane.Hint = () => "⏳ tea 04:59";
        pane.RefreshHint();
        Assert.True(pane.TryHitHint(6, 102, out hit));
        Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);

        // Nothing counted yet: no zone.
        pane.Hint = () => "";
        pane.Usage = () => "";
        pane.RefreshHint();
        Assert.True(pane.TryHitHint(6, 102, out hit));
        Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);

        // The busy row: "🔊 · " then the frame and " thinking 00:00" (sixteen cells) are the zone, the blank after them the row.
        pane.Hint = () => usage;
        pane.Usage = () => usage;
        using (pane.BeginBusy("thinking"))
        {
            Assert.True(pane.TryHitHint(0, 102, out hit));
            Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", 0), hit);   // the busy row's strip keeps its glyphs (2026-09-24: the brain's mid-turn cancel)
            Assert.True(pane.TryHitHint(4, 102, out hit));    // the separator
            Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);
            Assert.True(pane.TryHitHint(5, 102, out hit));
            Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Usage, "", 5), hit);
            Assert.True(pane.TryHitHint(20, 102, out hit));
            Assert.Equal(ScreenPane.HintZone.Usage, hit.Zone);
            Assert.True(pane.TryHitHint(21, 102, out hit));
            Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);
        }

        // The standing row again after the turn.
        Assert.True(pane.TryHitHint(5, 102, out hit));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Usage, "", 5), hit);
    }

    /// <summary>
    /// The ComfyUI generation's label (2026-09-28, the user's ask: a double-click there cancels the pictures): a label
    /// <see cref="ScreenPane.LabelAfterUsage"/> takes is its own zone, <see cref="ScreenPane.HintZone.Label"/>, after the tally
    /// and its separator, or right after the frame's blank with no tally; the spinner and the tally stay Usage.
    /// </summary>
    [Fact]
    public void TryHitHint_NamesLabel_OnTheGenerationsGlyphAndTime_AfterTheTallyOrWithout()
    {
        _cursorTop = 100;
        using var pane = Pane();
        string tally = "1.2k";
        pane.Strip = () => "🔊";
        pane.BusyUsage = () => tally;
        pane.LabelAfterUsage = label => label == "🎨";
        pane.Show();
        pane.ShowInput("", 0);

        // "🔊 · " (five cells), the frame at 5, a blank, "1.2k" at 7, " · ", then "🎨 00:00" from 14, eight cells.
        using (pane.BeginBusy("🎨"))
        {
            Assert.True(pane.TryHitHint(5, 102, out var hit));
            Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Usage, "", 5), hit);
            Assert.True(pane.TryHitHint(8, 102, out hit));
            Assert.Equal(ScreenPane.HintZone.Usage, hit.Zone);
            Assert.True(pane.TryHitHint(14, 102, out hit));
            Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Label, "", 14), hit);
            Assert.True(pane.TryHitHint(21, 102, out hit));   // the time's last digit
            Assert.Equal(ScreenPane.HintZone.Label, hit.Zone);
            Assert.True(pane.TryHitHint(22, 102, out hit));
            Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);

            // No tally (a /botchat turn): the label right after the frame's blank.
            tally = "";
            _time.Advance(ScreenPane.Tick);   // the busy row redrawn on the tick
            Assert.True(pane.TryHitHint(7, 102, out hit));
            Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Label, "", 7), hit);
            Assert.True(pane.TryHitHint(5, 102, out hit));
            Assert.Equal(ScreenPane.HintZone.Usage, hit.Zone);
        }

        // Any other label is no Label zone.
        using (pane.BeginBusy("thinking"))
        {
            Assert.True(pane.TryHitHint(7, 102, out var hit));
            Assert.Equal(ScreenPane.HintZone.Usage, hit.Zone);
        }

        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Label, "", 9), ScreenPane.HintHitAt("", -1, -1, -1, 0, 5, 12, 9, 4, 10));
        Assert.Equal(ScreenPane.HintZone.Usage, ScreenPane.HintHitAt("", -1, -1, -1, 0, 5, 12, 9, 4, 8).Zone);
    }

    /// <summary>The busy row's strip (2026-09-24): a zone only when drawn whole — a strip the fit cut is no button.</summary>
    [Fact]
    public void TryHitHint_OnTheBusyRow_AStripCutByTheFit_IsNoZone()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Hint = () => "";
        pane.Strip = () => "🧠 🔊 🎤";
        pane.Show();
        pane.ShowInput("abc", 3);
        using (pane.BeginBusy("thinking"))
        {
            Assert.True(pane.TryHitHint(0, 102, out var hit));
            Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🧠", 0), hit);   // whole at 40 columns
        }

        _console.Profile.Width = 9;   // "🧠 🔊 🎤 · " is eleven cells; the frame and the fit leave six
        pane.RefreshHint();
        using (pane.BeginBusy("thinking"))
        {
            Assert.True(pane.TryHitHint(0, 102, out var hit));
            Assert.NotEqual(ScreenPane.HintZone.Strip, hit.Zone);
        }
    }

    [Fact]
    public void HintHitAt_WhileScrolled_TheRowIsTheScrolledZone_TheStripAndTrailerKept()
    {
        // Later on 2026-09-18: the scroll's hint under the click is Scrolled wherever the row would be; a glyph and the trailer answer as before.
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Scrolled, "", -1), ScreenPane.HintHitAt("🔊 🎤", 30, -1, 0, 8, scrolled: true));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Scrolled, "", -1), ScreenPane.HintHitAt("🔊 🎤", 30, -1, 0, 2, scrolled: true));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Scrolled, "", -1), ScreenPane.HintHitAt("", -1, -1, 0, 0, scrolled: true));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🎤", 3), ScreenPane.HintHitAt("🔊 🎤", 30, -1, 0, 4, scrolled: true));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Trailer, "", 30), ScreenPane.HintHitAt("🔊 🎤", 30, -1, 0, 35, scrolled: true));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1), ScreenPane.HintHitAt("🔊 🎤", 30, -1, 0, 8, scrolled: false));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1), ScreenPane.HintHitAt("🔊 🎤", 30, -1, 0, 8));
        // The zone's number sits under every strip key (InputLine.HintPairKey: 8 + column).
        Assert.Equal(4, (int)ScreenPane.HintZone.Scrolled);
    }

    [Fact]
    public void TryHitHint_NamesScrolled_WhileScrolled_AndRowAgainAtTheBottom_UnderTheBusyRowToo()
    {
        _cursorTop = 100;
        using var pane = Scrollable();
        pane.Hint = () => "idle";
        pane.Strip = () => "🔊";
        pane.Trailer = () => "llama";
        pane.ShowInput("abc", 3);
        Assert.True(pane.TryHitHint(8, 102, out var hit));
        Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);

        pane.ScrollPage(-1);
        Assert.True(pane.Scrolled);
        Assert.True(pane.TryHitHint(8, 102, out hit));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Scrolled, "", -1), hit);
        Assert.True(pane.TryHitHint(0, 102, out hit));   // the glyph keeps its zone
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", 0), hit);
        Assert.True(pane.TryHitHint(38, 102, out hit));  // the model name too
        Assert.Equal(ScreenPane.HintZone.Trailer, hit.Zone);

        // The busy row carries the scroll's hint as well; its trailer is nobody's, so the rest of the row is Scrolled —
        // the spinner and its label included (their Usage zone, 2026-09-21, is the bottom's alone) — while the strip's
        // glyph keeps its zone, as on the standing row (2026-09-24).
        using (pane.BeginBusy("thinking"))
        {
            Assert.True(pane.TryHitHint(0, 102, out hit));
            Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", 0), hit);
            Assert.True(pane.TryHitHint(8, 102, out hit));
            Assert.Equal(ScreenPane.HintZone.Scrolled, hit.Zone);
            Assert.True(pane.TryHitHint(38, 102, out hit));
            Assert.Equal(ScreenPane.HintZone.Scrolled, hit.Zone);
            pane.ScrollToEnd();
            Assert.True(pane.TryHitHint(8, 102, out hit));
            Assert.Equal(ScreenPane.HintZone.Usage, hit.Zone);
            Assert.True(pane.TryHitHint(30, 102, out hit));
            Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);
        }

        pane.ScrollPage(-1);
        Assert.True(pane.TryHitHint(8, 102, out hit));
        Assert.Equal(ScreenPane.HintZone.Scrolled, hit.Zone);
        pane.ScrollToEnd();
        Assert.False(pane.Scrolled);
        Assert.True(pane.TryHitHint(8, 102, out hit));
        Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);
    }

    /// <summary>Under an overlay the hint row is the overlay's (its reader takes the click); lifted, or with no pane, nothing.</summary>
    [Fact]
    public void TryHitHint_IsFalse_UnderAnOverlay_WhileLifted_OrWithoutThePane()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        pane.ShowInput("abc", 3);
        Assert.True(pane.TryHitHint(0, 102));

        pane.ShowOverlay(new Markup("a\nb\nc"), "hint");
        Assert.False(pane.TryHitHint(0, 102));
        Assert.False(pane.TryHitHint(0, 104));    // where the overlay's hint row is
        pane.CloseOverlay();
        Assert.True(pane.TryHitHint(0, 102));

        using (pane.Batch())
        {
            Assert.False(pane.TryHitHint(0, 102));
        }

        Assert.True(pane.TryHitHint(0, 102));

        using var plain = Pane(geometry: false);
        Assert.False(plain.TryHitHint(0, 102));
    }

    /// <summary>An overlay shown with close ends its first row in the × glyph at column width − 2 (2026-09-18); one shown without, or with the glyph switched off, or too wide for the gap, does not.</summary>
    [Fact]
    public void ShowOverlay_WithClose_PutsTheCloseGlyphAtTheFirstRowsRightEdge()
    {
        _console.Profile.Width = 20;
        _console.Profile.Height = 8;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();

        pane.ShowOverlay(new Markup("Title" + "\n" + "b"), "hint", close: true);
        Assert.Contains("\n" + Rule(20) + "\nTitle             " + ScreenPane.CloseGlyph + "\nb\n" + Rule(20), Output);
        Assert.Equal(19, TextCells.Width("Title             " + ScreenPane.CloseGlyph));

        // Without close (the input line's completion list): the row as given.
        int mark = Output.Length;
        pane.ShowOverlay(new Markup("Title" + "\n" + "b"), "hint");
        Assert.Contains("\nTitle\nb\n", Output[mark..]);
        Assert.DoesNotContain(ScreenPane.CloseGlyph, Output[mark..]);

        // A first row that leaves less than the gap and the glyph before the last column goes without.
        mark = Output.Length;
        pane.ShowOverlay(new Markup("1234567890123456" + "\n" + "b"), "hint", close: true);   // 16 cells: 16 + 2 + 1 = 19 fits
        Assert.Contains("\n1234567890123456  " + ScreenPane.CloseGlyph + "\n", Output[mark..]);
        mark = Output.Length;
        pane.ShowOverlay(new Markup("12345678901234567" + "\n" + "b"), "hint", close: true);  // 17: no room
        Assert.Contains("\n12345678901234567\nb\n", Output[mark..]);
        Assert.DoesNotContain(ScreenPane.CloseGlyph, Output[mark..]);
    }

    /// <summary>TryHitClose: the glyph's cell, the gap cell before it and the last column, on the overlay's first row alone; nothing when no glyph was drawn.</summary>
    [Fact]
    public void TryHitClose_IsTheGlyphAndItsNeighbours_OnTheFirstRow()
    {
        _console.Profile.Width = 20;
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        Assert.False(pane.TryHitClose(18, 100));   // no overlay

        pane.ShowOverlay(new Markup("a" + "\n" + "b" + "\n" + "c"), "hint", close: true);
        Assert.True(pane.TryHitClose(18, 100));    // the glyph (column width − 2)
        Assert.True(pane.TryHitClose(17, 100));    // the gap cell before it
        Assert.True(pane.TryHitClose(19, 100));    // the last column
        Assert.False(pane.TryHitClose(16, 100));
        Assert.False(pane.TryHitClose(0, 100));
        Assert.False(pane.TryHitClose(18, 101));   // the second content row
        Assert.False(pane.TryHitClose(18, 99));    // the upper rule
        Assert.True(pane.TryHitOverlay(18, 100, out int row) && row == 0);   // the row itself still answers

        using (pane.Batch())
        {
            Assert.False(pane.TryHitClose(18, 100));
        }

        // With a slot the first row is the overlay's rows plus the input row above the cursor.
        pane.ShowOverlay(new Markup("a" + "\n" + "b"), "hint", input: true, close: true);
        pane.ShowInput("abc", 3);
        Assert.True(pane.TryHitClose(18, 98));
        Assert.False(pane.TryHitClose(18, 100));

        // No glyph drawn: no hit.
        pane.ShowOverlay(new Markup("a" + "\n" + "b"), "hint");
        Assert.False(pane.TryHitClose(18, 100));

        _cursorTop = null;
        pane.ShowOverlay(new Markup("a" + "\n" + "b"), "hint", close: true);
        Assert.False(pane.TryHitClose(18, 100));
    }
    /// <summary>TryHitOutside (2026-09-18): the transcript, either rule and the hint row while a close-overlay is drawn; never its rows or its slot, never the input line's completion list (no close), nothing lifted, batched or without a cursor.</summary>
    [Fact]
    public void TryHitOutside_IsTheTranscriptTheRulesAndTheHintRow_NotTheOverlayItsSlotOrTheCompletionList()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        Assert.False(pane.TryHitOutside(0, 50));    // no overlay

        pane.ShowOverlay(new Markup("a\nb\nc"), "hint", close: true);
        Assert.True(pane.TryHitOutside(5, 50));     // the transcript
        Assert.True(pane.TryHitOutside(0, 99));     // the upper rule
        Assert.False(pane.TryHitOutside(0, 100));   // the rows
        Assert.False(pane.TryHitOutside(39, 102));
        Assert.True(pane.TryHitOutside(0, 103));    // the lower rule
        Assert.True(pane.TryHitOutside(0, 104));    // the hint row
        Assert.True(pane.TryHitOutside(0, 200));    // below the screen: off the pane still

        using (pane.Batch())
        {
            Assert.False(pane.TryHitOutside(0, 99));
        }

        // With a slot: the overlay's rows and the input row under them are the pane, the rules either side are not.
        pane.ShowOverlay(new Markup("a\nb"), "hint", input: true, close: true);
        pane.ShowInput("abc", 3);
        Assert.True(pane.TryHitOutside(0, 97));
        Assert.False(pane.TryHitOutside(0, 98));
        Assert.False(pane.TryHitOutside(0, 99));
        Assert.False(pane.TryHitOutside(0, 100));   // the slot
        Assert.True(pane.TryHitOutside(0, 101));
        Assert.True(pane.TryHitOutside(0, 102));

        // The line's own completion list is shown without close: a double-click on the transcript never closes it.
        pane.ShowOverlay(new Markup("a\nb"), "hint", input: true);
        Assert.False(pane.TryHitOutside(0, 50));
        Assert.False(pane.TryHitOutside(0, 102));

        pane.CloseOverlay();
        Assert.False(pane.TryHitOutside(0, 50));

        _cursorTop = null;
        pane.ShowOverlay(new Markup("a\nb"), "hint", close: true);
        Assert.False(pane.TryHitOutside(0, 50));

        using var plain = Pane(geometry: false);
        Assert.False(plain.TryHitOutside(0, 50));
    }

    /// <summary>Dismiss sets Dismissed only while an overlay is drawn; CloseOverlay clears it (the top host's close), Close too.</summary>
    [Fact]
    public void Dismiss_SetsOnlyUnderAnOverlay_AndCloseOverlayClearsIt()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        pane.Dismiss();
        Assert.False(pane.Dismissed);

        pane.ShowOverlay(new Markup("a\nb"), "hint", close: true);
        pane.Dismiss();
        Assert.True(pane.Dismissed);
        pane.ShowOverlay(new Markup("c"), "hint", close: true);   // a nested page drawn: the signal stands
        Assert.True(pane.Dismissed);
        pane.CloseOverlay();
        Assert.False(pane.Dismissed);

        pane.ShowOverlay(new Markup("a\nb"), "hint", close: true);
        pane.Dismiss();
        pane.Close();
        Assert.False(pane.Dismissed);

        using var plain = Pane(geometry: false);
        plain.Dismiss();
        Assert.False(plain.Dismissed);
    }

    /// <summary>Without a slot the hidden cursor rests on the overlay's first row, so that row IS the cursor's buffer row.</summary>
    [Fact]
    public void TryHitOverlay_WithoutASlot_MapsTheContentRows_FromTheCursorsRow()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        Assert.False(pane.TryHitOverlay(0, 100, out _));   // no overlay drawn

        pane.ShowOverlay(new Markup("a\nb\nc"), "hint");
        Assert.Equal(3, pane.OverlayRows);

        Assert.True(pane.TryHitOverlay(0, 100, out int row));
        Assert.Equal(0, row);
        Assert.True(pane.TryHitOverlay(39, 102, out row));   // any column
        Assert.Equal(2, row);
        Assert.False(pane.TryHitOverlay(0, 99, out _));     // the upper rule
        Assert.False(pane.TryHitOverlay(0, 103, out _));    // the lower rule

        pane.CloseOverlay();
        Assert.False(pane.TryHitOverlay(0, 100, out _));
    }

    /// <summary>With a slot the cursor is on its input row under the content, so the content starts that many rows above.</summary>
    [Fact]
    public void TryHitOverlay_WithASlot_CountsUpFromTheInputRow()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        pane.ShowOverlay(new Markup("a\nb"), "hint", input: true);
        pane.ShowInput("abc", 3);

        Assert.True(pane.TryHitOverlay(0, 98, out int row));
        Assert.Equal(0, row);
        Assert.True(pane.TryHitOverlay(0, 99, out row));
        Assert.Equal(1, row);
        Assert.False(pane.TryHitOverlay(0, 100, out _));   // the slot: TryHitInput's
        Assert.True(pane.TryHitInput(3, 100, out _));
    }

    [Fact]
    public void TryHitOverlay_IsFalse_WhileLifted_OrWithoutACursorRow()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        pane.ShowOverlay(new Markup("a\nb"), "hint");
        Assert.True(pane.TryHitOverlay(0, 101, out _));

        using (pane.Batch())
        {
            Assert.False(pane.TryHitOverlay(0, 101, out _));
        }

        Assert.True(pane.TryHitOverlay(0, 101, out _));
        _cursorTop = null;
        Assert.False(pane.TryHitOverlay(0, 101, out _));
    }

    [Fact]
    public void BeginBusy_UnderAnOverlay_ShowsTheSpinnerInTheHintRow()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 8;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();
        pane.ShowOverlay(new Markup("a\nb"), "hint");
        int mark = Output.Length;

        using (pane.BeginBusy("listing voices"))
        {
            // Down over the two content rows and the lower rule, the spinner row, back up; the cursor stays hidden.
            string written = Output[mark..];
            Assert.StartsWith("\e[?2026h\e[?25l\e[3B\e[20D", written);
            Assert.Contains(Theme.SpinnerFrames[0] + " listing voices", Strip(written));
            Assert.EndsWith("\e[K\e[3A\e[20D\e[?2026l", written);
            Assert.DoesNotContain("\e[?25h", written);

            _time.Advance(ScreenPane.Tick);
            Assert.Contains(Theme.SpinnerFrames[1] + " listing voices", Strip(Output[mark..]));
        }

        // The overlay's hint again, and the overlay untouched.
        Assert.EndsWith("hint\e[0m\e[K\e[3A\e[20D\e[?2026l", Output);
        Assert.True(pane.OverlayOpen);

        // With an input slot the cursor comes back to its cell, shown.
        pane.ShowOverlay(new Markup("a\nb"), "hint", input: true);
        pane.ShowInput("xy", 2);
        mark = Output.Length;
        using (pane.BeginBusy("listing"))
        {
            Assert.EndsWith("\e[K\e[2A\e[20D\e[4C\e[?25h\e[?2026l", Output);
        }
    }

    /// <summary>Without the escape sequences and with plain line breaks (an ANSI-emitting <see cref="TestConsole"/> writes <c>Environment.NewLine</c>).</summary>
    // ── The selection ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("abcdef", 0, 2, 5, "ab|cde|f", "U,S,U")]         // inside the row
    [InlineData("abcdef", 0, 0, 6, "abcdef", "S")]               // the whole row
    [InlineData("abcdef", 0, 0, 3, "abc|def", "S,U")]            // from the start
    [InlineData("abcdef", 0, 4, 6, "abcd|ef", "U,S")]            // to the end
    [InlineData("abcdef", 0, 4, 4, "abcdef", "U")]               // empty: nothing selected
    [InlineData("abcdef", 0, -1, -1, "abcdef", "U")]             // none
    [InlineData("abcdef", 10, 2, 5, "abcdef", "U")]              // before the row (row starts at 10)
    [InlineData("abcdef", 10, 20, 25, "abcdef", "U")]            // after the row
    [InlineData("abcdef", 10, 5, 12, "ab|cdef", "S,U")]          // across the row's start (the wrap)
    [InlineData("abcdef", 10, 14, 30, "abcd|ef", "U,S")]         // across the row's end
    [InlineData("abcdef", 10, 0, 100, "abcdef", "S")]            // everything
    [InlineData("", 0, -1, -1, "", "U")]                         // an empty row is one empty run
    public void RowRuns_Selection_IsPinned(string row, int rowStart, int start, int end, string texts, string styles)
    {
        var runs = ScreenPane.RowRuns(row, rowStart, start, end, Array.Empty<(int, int)>());
        Assert.Equal(texts, string.Join('|', runs.Select(r => r.Text)));
        Assert.Equal(styles, string.Join(',', runs.Select(r => StyleName(r.Style))));
    }

    [Theory]
    [InlineData("abcdef", 0, -1, -1, "1:3", "a|bcd|ef", "U,P,U")]          // a label inside the row
    [InlineData("abcdef", 0, -1, -1, "0:6", "abcdef", "P")]                // the whole row is a label
    [InlineData("abcdef", 10, -1, -1, "8:4", "ab|cdef", "P,U")]            // a label wrapped in from the row before
    [InlineData("abcdef", 10, -1, -1, "14:10", "abcd|ef", "U,P")]          // a label wrapping out
    [InlineData("abcdef", 0, -1, -1, "0:2,4:2", "ab|cd|ef", "P,U,P")]      // two labels
    [InlineData("abcdef", 0, 2, 4, "1:4", "a|b|cd|e|f", "U,P,S,P,U")]      // the selection wins over a label
    [InlineData("abcdef", 0, 0, 6, "1:4", "abcdef", "S")]                  // selected whole: one run
    public void RowRuns_Labels_ArePinned(string row, int rowStart, int start, int end, string labels, string texts, string styles)
    {
        var ranges = labels.Split(',').Select(l => l.Split(':')).Select(l => (int.Parse(l[0]), int.Parse(l[1]))).ToList();
        var runs = ScreenPane.RowRuns(row, rowStart, start, end, ranges);
        Assert.Equal(texts, string.Join('|', runs.Select(r => r.Text)));
        Assert.Equal(styles, string.Join(',', runs.Select(r => StyleName(r.Style))));
    }

    private static string StyleName(Style style) =>
        style.Equals(Theme.SelectedText) ? "S" : style.Equals(Theme.PasteLabel) ? "P" : style.Equals(Theme.User) ? "U" : "?";

    [Fact]
    public void APasteLabel_IsDrawnInItsOwnStyle_AndTheSelectionCoversIt()
    {
        _console.EmitAnsiSequences();
        using var pane = Pane();
        pane.Show();

        int mark = Output.Length;
        pane.ShowInput("ab[Pasted text #1 +5 lines]c", 1, -1, new[] { (2, "[Pasted text #1 +5 lines]".Length) });
        string written = Output[mark..];
        Assert.Equal("ab[Pasted text #1 +5 lines]c", Strip(written).TrimEnd());
        Assert.Matches(@"ab(\e\[[0-9;]*m)+\[Pasted text #1 \+5 lines\](\e\[[0-9;]*m)+c", written);

        mark = Output.Length;
        pane.ShowInput("ab[Pasted text #1 +5 lines]c", 28, 0, new[] { (2, "[Pasted text #1 +5 lines]".Length) });
        written = Output[mark..];
        // Everything selected: one run, no label style in it.
        Assert.Contains("ab[Pasted text #1 +5 lines]c", written);
    }

    [Fact]
    public void ASelection_IsDrawnAsItsOwnRun_AndWithoutOneTheRowIsAsBefore()
    {
        _console.EmitAnsiSequences();
        using var pane = Pane();
        pane.Show();

        int mark = Output.Length;
        pane.ShowInput("abcde", 5, 2);
        string written = Output[mark..];
        Assert.Equal("abcde", Strip(written).TrimEnd());
        // "ab" in the user's style, then a new style for "cde": the two never share a run.
        Assert.Matches(@"ab(\e\[[0-9;]*m)+cde", written);
        Assert.DoesNotContain("abcde", written);

        // The anchor at the cursor is no selection; −1 is none: one run, as it always was.
        mark = Output.Length;
        pane.ShowInput("abcde", 5, 5);
        Assert.Contains("abcde", Output[mark..]);
        mark = Output.Length;
        pane.ShowInput("abcde", 5);
        Assert.Contains("abcde", Output[mark..]);

        // Anchored after the cursor, drawn across a redraw (the row count changes): the same split.
        mark = Output.Length;
        pane.ShowInput(Long, 0, 4);
        written = Output[mark..];
        Assert.Matches(Long[..4] + @"(\e\[[0-9;]*m)+" + System.Text.RegularExpressions.Regex.Escape(Long[4..Row0.Length]), written);
    }

    [Fact]
    public void ASelectionAcrossTheWrap_HighlightsTheTailOfOneRow_AndTheHeadOfTheNext()
    {
        _console.EmitAnsiSequences();
        using var pane = Pane();
        pane.Show();

        // From two characters before the break to two characters into the second row.
        int breakAt = Row0.Length;
        int mark = Output.Length;
        pane.ShowInput(Long, breakAt + 3, breakAt - 2);
        string written = Output[mark..];
        string tail = Row0[^2..];
        string head = Row1[..2];
        Assert.Matches(System.Text.RegularExpressions.Regex.Escape(Row0[..^2]) + @"(\e\[[0-9;]*m)+" + System.Text.RegularExpressions.Regex.Escape(tail), written);
        Assert.Matches(@"(\e\[[0-9;]*m)+" + System.Text.RegularExpressions.Regex.Escape(head) + @"(\e\[[0-9;]*m)+" + System.Text.RegularExpressions.Regex.Escape(Row1[2..]), written);
        Assert.Equal(InputLine.PromptGlyph + Row0 + "\n" + InputLine.ContinuationIndent + Row1, Strip(written).Split(Rule(40))[1].Trim('\n'));
    }

    [Fact]
    public void CommitAndClear_ForgetTheSelection()
    {
        _console.EmitAnsiSequences();
        using var pane = Pane();
        pane.Show();
        pane.ShowInput("abcde", 5, 2);
        pane.ClearInput();

        int mark = Output.Length;
        pane.ShowInput("abcde", 5);
        Assert.Contains("abcde", Output[mark..]);
    }

    // ── Placeholder ─────────────────────────────────────────────────────────

    private const string Ghost = "Type a message or /help for more info";   // 37 cells: the row at width 40 exactly

    [Fact]
    public void Placeholder_Default_IsEmpty_AndDrawsNothing()
    {
        using var pane = Pane();
        Assert.Equal("", pane.Placeholder);
        Assert.Throws<ArgumentNullException>(() => pane.Placeholder = null!);
        pane.Show();
        Assert.EndsWith(Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output);
    }

    [Theory]
    [InlineData(40, "Type a message or /help for more info")]
    [InlineData(30, "Type a message or /help fo…")]
    [InlineData(3, "…")]
    public void PlaceholderRow_IsPinned(int width, string expected) =>
        Assert.Equal(expected, ScreenPane.PlaceholderRow(Ghost, width));

    [Fact]
    public void Placeholder_IsDrawnOnTheEmptyRow_AndTheCursorStaysAfterTheGlyph()
    {
        _console.EmitAnsiSequences();
        using var pane = Pane();
        pane.Placeholder = Ghost;
        pane.Show();

        // The ghost after the glyph; the cursor placed from the hint row as ever: up, column 0, past the glyph.
        Assert.EndsWith(Rule(40) + "\n" + InputLine.PromptGlyph + Ghost + "\n" + Rule(40) + "\n", Strip(Output));
        Assert.EndsWith("\e[K\e[2A\e[40D\e[2C\e[?25h", Output);
        Assert.Equal(1, pane.InputRows);
    }

    [Fact]
    public void Placeholder_IsCutToTheRow_AtANarrowWindow()
    {
        _console.Profile.Width = 30;
        using var pane = Pane();
        pane.Placeholder = Ghost;
        pane.Show();

        Assert.EndsWith(Rule(30) + "\n" + InputLine.PromptGlyph + "Type a message or /help fo…\n" + Rule(30) + "\n", Output);
    }

    [Fact]
    public void Placeholder_TheFirstKey_BlanksIt_InPlace_AndAnEmptyDraftBringsItBack()
    {
        _console.EmitAnsiSequences();
        using var pane = Pane();
        pane.Placeholder = Ghost;
        pane.Show();
        int draws = Draws;

        int mark = Output.Length;
        pane.ShowInput("a", 1);
        // One cell of draft, the 36 the ghost still held blanked, the cursor back over the blanks.
        Assert.Equal(draws, Draws);
        Assert.EndsWith("a" + new string(' ', 36), Strip(Output[mark..]));
        Assert.EndsWith("\e[36D", Output[mark..]);

        mark = Output.Length;
        pane.ShowInput("", 0);
        // The ghost over the draft's one cell, wider than it: no blanks, the cursor back to the glyph.
        Assert.Equal(draws, Draws);
        Assert.EndsWith(Ghost, Strip(Output[mark..]));
        Assert.EndsWith("\e[37D", Output[mark..]);

        mark = Output.Length;
        pane.ClearInput();
        Assert.Equal(draws, Draws);
        Assert.EndsWith(Ghost, Strip(Output[mark..]));
        Assert.EndsWith("\e[37D", Output[mark..]);
    }

    [Fact]
    public void Placeholder_IsNotDrawn_UnderTheSpinner_AndComesBackAfter()
    {
        using var pane = Pane();
        pane.Placeholder = Ghost;
        pane.Show();
        int draws = Draws;

        int mark = Output.Length;
        var busy = pane.BeginBusy("thinking");
        // The hint row again, then the row's 37 cells blanked in place.
        Assert.Equal(draws, Draws);
        Assert.Contains("thinking", Output[mark..]);
        Assert.EndsWith(new string(' ', 37), Output[mark..]);

        pane.PreviewInput("q");
        Assert.EndsWith("q", Output);

        mark = Output.Length;
        busy.Dispose();
        // The draft typed ahead stays; nothing but the hint row is redrawn.
        Assert.DoesNotContain(Ghost, Output[mark..]);

        pane.ShowInput("", 0);
        Assert.EndsWith(Ghost, Output);

        mark = Output.Length;
        using (pane.BeginBusy("thinking"))
        {
            Assert.EndsWith(new string(' ', 37), Output[mark..]);
            mark = Output.Length;
        }

        Assert.EndsWith(Ghost, Output[mark..]);
        Assert.Equal(draws, Draws);
    }

    [Fact]
    public void Placeholder_IsNotDrawn_InAnOverlaySlot_AndReturnsWhenItCloses()
    {
        using var pane = Pane();
        pane.Placeholder = Ghost;
        pane.Show();

        pane.ShowOverlay(new Markup("a\nb"), "hint", input: true);
        Assert.Contains("\na\nb\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\nhint", Output);
        Assert.DoesNotContain(Ghost, Output[Output.LastIndexOf("\na\nb\n", StringComparison.Ordinal)..]);

        pane.ShowInput("x", 1);
        pane.CommitInput("x");
        Assert.DoesNotContain(Ghost, Output[Output.LastIndexOf("\na\nb\n", StringComparison.Ordinal)..]);

        pane.CloseOverlay();
        Assert.EndsWith(Rule(40) + "\n" + InputLine.PromptGlyph + Ghost + "\n" + Rule(40) + "\n", Output);
    }

    [Fact]
    public void Placeholder_ComesBack_AfterACommit_AndFollowsAResize()
    {
        using var pane = Pane();
        pane.Placeholder = Ghost;
        pane.Show();
        pane.ShowInput("hello", 5);
        pane.CommitInput("hello");

        Assert.EndsWith(InputLine.PromptGlyph + "hello\n" + new string('\n', 5) + Rule(40) + "\n" + InputLine.PromptGlyph + Ghost + "\n" + Rule(40) + "\n", Output);

        _console.Profile.Width = 30;
        _time.Advance(ScreenPane.Tick);
        Assert.EndsWith(Rule(30) + "\n" + InputLine.PromptGlyph + "Type a message or /help fo…\n" + Rule(30) + "\n", Output);
    }

    // ── The live slot ───────────────────────────────────────────────────────

    private static RawText LinesOf(int count, string prefix = "l") =>
        new(string.Join("\n", Enumerable.Range(1, count).Select(i => prefix + i)));

    [Fact]
    public void SetLive_DrawsNothingUntilTheTick_ThenTheBlockAboveThePadding()
    {
        using var pane = Pane();
        pane.Show();
        int draws = Draws;

        pane.SetLive(new RawText("one\ntwo"));
        Assert.True(pane.LiveOpen);
        Assert.Equal(draws, Draws);
        Assert.Equal(0, pane.LiveRows);

        _time.Advance(ScreenPane.Tick);

        // The flow still at row 0: the two live rows, then four padding rows, then the pane.
        Assert.Equal(draws + 1, Draws);
        Assert.Equal(2, pane.LiveRows);
        Assert.Equal(4, pane.Padding);
        Assert.Equal(0, pane.FlowRow);
        Assert.EndsWith("one\ntwo\n" + new string('\n', 4) + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output);

        // Nothing changed: the next tick redraws nothing.
        draws = Draws;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(draws, Draws);
    }

    [Fact]
    public void SetLive_Again_LiftsOverTheLiveRows_AndLaysTheBlockOutAgain()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Width = 20;
        _console.Profile.Height = 6;
        using var pane = Pane();
        pane.Show();
        pane.SetLive(new RawText("a"));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(1, pane.LiveRows);
        Assert.Equal(1, pane.Padding);

        pane.SetLive(new RawText("a\nb"));
        int mark = Output.Length;
        _time.Advance(ScreenPane.Tick);
        string written = Output[mark..];

        // Up over the padding row, the rule and the live row (3), both rows painted over the old frame
        // (each clearing its end, 2026-09-26), no padding, the pane.
        Assert.StartsWith("\e[?2026h\e[?25l\e[3A\e[20D\e[?25la\e[K\r\nb\e[K\r\n", written);
        Assert.Contains("a\nb\n" + Rule(20) + "\n", Strip(written));
        Assert.Equal(2, pane.LiveRows);
        Assert.Equal(0, pane.Padding);
        Assert.Equal(0, pane.FlowRow);
    }

    [Fact]
    public void CommitLive_WritesTheLinesIntoTheFlow_AndEmptiesTheSlot()
    {
        using var pane = Pane();
        pane.Show();
        pane.SetLive(new RawText("one\ntwo"));
        _time.Advance(ScreenPane.Tick);
        int mark = Output.Length;

        pane.CommitLive();

        Assert.False(pane.LiveOpen);
        Assert.Equal(0, pane.LiveRows);
        Assert.Equal(2, pane.FlowRow);
        Assert.Equal(4, pane.Padding);
        Assert.Equal(1, Count(Output[mark..], "one\ntwo\n"));

        // A flow write lands under them.
        pane.Write(new Markup("three" + Environment.NewLine));
        Assert.Equal(3, pane.FlowRow);
        Assert.EndsWith("three\n" + new string('\n', 3) + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output);
    }

    [Fact]
    public void CommitLive_BeforeAnyTick_StillWritesTheLines()
    {
        using var pane = Pane();
        pane.Show();
        pane.SetLive(new RawText("one"));

        pane.CommitLive();

        Assert.Equal(1, pane.FlowRow);
        Assert.EndsWith("one\n" + new string('\n', 5) + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output);
    }

    [Fact]
    public void AFlowWrite_WhileTheSlotIsOpen_CommitsItFirst()
    {
        using var pane = Pane();
        pane.Show();
        pane.SetLive(new RawText("one"));
        _time.Advance(ScreenPane.Tick);
        int mark = Output.Length;

        pane.Write(new Markup("after" + Environment.NewLine));

        Assert.False(pane.LiveOpen);
        Assert.Contains("one\nafter\n", Output[mark..]);
        Assert.Equal(2, pane.FlowRow);
    }

    [Fact]
    public void AMidRowFlow_IsEndedBeforeTheBlockIsCommitted()
    {
        using var pane = Pane();
        pane.Show();
        pane.Write(new RawText("abc"));
        pane.SetLive(new RawText("one"));

        pane.CommitLive();

        Assert.Equal(2, pane.FlowRow);
        Assert.Equal(0, pane.FlowColumn);
        Assert.EndsWith("one\n" + new string('\n', 4) + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output);
    }

    [Fact]
    public void ATallLiveBlock_CommitsItsTopLines_AndTheRestStaysLive()
    {
        using var pane = Pane();
        pane.Show();

        // 10 rows less the pane's 4: six live rows at most; the first two of eight go into the flow.
        pane.SetLive(LinesOf(8));
        _time.Advance(ScreenPane.Tick);
        // The committed rows scrolled off the top as the block and the pane were drawn: the flow is row 0 again.
        Assert.Equal(2, pane.LiveCommitted);
        Assert.Equal(6, pane.LiveRows);
        Assert.Equal(0, pane.FlowRow);
        Assert.Equal(0, pane.Padding);

        // One more line: one more committed, the tail still six rows.
        pane.SetLive(LinesOf(9));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(3, pane.LiveCommitted);
        Assert.Equal(6, pane.LiveRows);
        Assert.Equal(0, pane.FlowRow);

        int mark = Output.Length;
        pane.CommitLive();

        // The six remaining lines once, under the committed three; the flow six rows down, the pane under it.
        Assert.False(pane.LiveOpen);
        Assert.Equal(0, pane.LiveCommitted);
        Assert.Equal(6, pane.FlowRow);
        Assert.Equal(0, pane.Padding);
        Assert.Equal(1, Count(Output[mark..], "l4\nl5\nl6\nl7\nl8\nl9\n"));
        Assert.DoesNotContain("l3", Output[mark..]);
    }

    private static string CodeLines(int from, int to) =>
        string.Join("\n", Enumerable.Range(from, to - from + 1).Select(i => "c" + i.ToString(CultureInfo.InvariantCulture)));

    [Fact]
    public void AStreamingCodeBlock_ShowsItsLabelAndLastKeepRows_AndFoldsWhole()
    {
        // 2026-09-27 (the user's ask: a long block scrolled and flickered as it streamed): twelve lines,
        // a keep of 3 — the slot shows the label and c10–c12, nothing is committed while the fence is
        // open; the whole block goes in at the commit, folded to its label, "after" under it.
        using var pane = Pane();
        pane.Show();
        int mark = Output.Length;
        pane.SetLive(new ReplyBlock("```text\n" + CodeLines(1, 12), glyph: false, codeKeep: 3));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(0, pane.LiveCommitted);
        Assert.Equal(4, pane.LiveRows);
        Assert.Contains(MarkdownView.CodeHeading("text") + "\n  c10\n  c11\n  c12\n", Output[mark..]);
        Assert.DoesNotContain("  c9\n", Output[mark..]);
        Assert.DoesNotContain(CodeFoldText.Summary("text", 12, expanded: false), Output);

        // One line more: the window moves, still nothing committed.
        mark = Output.Length;
        pane.SetLive(new ReplyBlock("```text\n" + CodeLines(1, 13), glyph: false, codeKeep: 3));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(0, pane.LiveCommitted);
        Assert.Contains(MarkdownView.CodeHeading("text") + "\n  c11\n  c12\n  c13\n", Output[mark..]);

        mark = Output.Length;
        pane.SetLive(new ReplyBlock("```text\n" + CodeLines(1, 13) + "\n```\n\nafter", glyph: false, codeKeep: 3));
        pane.CommitLive();

        string tail = Output[mark..];
        int summary = tail.LastIndexOf(CodeFoldText.Summary("text", 13, expanded: false), StringComparison.Ordinal);
        Assert.True(summary >= 0, tail);
        Assert.Contains("after", tail[summary..]);
        Assert.DoesNotContain("  c", tail[summary..]);

        // Unfolded, every line is there.
        pane.SetToolGroupsExpanded(true);
        Assert.True(pane.StoredRows >= 15);
    }

    [Fact]
    public void AClosedCodeBlockPastItsKeep_FoldsOnTheTickItCloses()
    {
        // The fence closed and nothing after it yet: the block goes in whole on the tick, folded — not
        // drawn at full height until the reply speaks again.
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        pane.SetLive(new ReplyBlock("```text\n" + CodeLines(1, 12), glyph: false, codeKeep: 3));
        _time.Advance(ScreenPane.Tick);

        int mark = Output.Length;
        pane.SetLive(new ReplyBlock("```text\n" + CodeLines(1, 12) + "\n```", glyph: false, codeKeep: 3));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(13, pane.LiveCommitted);
        Assert.Equal(0, pane.LiveRows);
        Assert.Equal(1, pane.StoredRows);
        Assert.Contains(CodeFoldText.Summary("text", 12, expanded: false), Output[mark..]);

        // The reply goes on under it; the block stays one row.
        pane.SetLive(new ReplyBlock("```text\n" + CodeLines(1, 12) + "\n```\n\nafter", glyph: false, codeKeep: 3));
        _time.Advance(ScreenPane.Tick);
        pane.CommitLive();
        Assert.Equal(3, pane.StoredRows);
    }

    [Fact]
    public void TextAboveAStreamingCodeBlock_CommitsUpToItsLabel()
    {
        // Eight lines of prose, then an open block: the prose that does not fit goes into the flow, the
        // label never does while the block streams — the slot keeps it over the window.
        using var pane = Pane();
        pane.Show();
        string prose = string.Join("\n\n", Enumerable.Range(1, 8).Select(i => "p" + i.ToString(CultureInfo.InvariantCulture)));
        string text = prose + "\n\n```text\n" + CodeLines(1, 12);
        pane.SetLive(new ReplyBlock(text, glyph: false, codeKeep: 3));
        _time.Advance(ScreenPane.Tick);

        // p1..p8 with their spacers are 15 rows, the spacer 1, the label at row 16: 20 rows in the
        // window's view, the six the region holds are p8's spacer rows, the label and c10–c12.
        Assert.Equal(14, pane.LiveCommitted);
        Assert.Equal(6, pane.LiveRows);

        // Growing, the prose above goes in up to the label, never the label itself.
        var tall = _console.Profile.Height;
        _console.Profile.Height = 7;
        pane.SetLive(new ReplyBlock(text + "\nc13", glyph: false, codeKeep: 3));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(16, pane.LiveCommitted);
        _console.Profile.Height = tall;

        pane.CommitLive();
        pane.Write(new Markup("next\n"));
        Assert.Contains(CodeFoldText.Summary("text", 13, expanded: false), Output);
    }

    [Fact]
    public void AStreamingCodeBlock_OnARegionUnderItsWindow_KeepsTheLabel()
    {
        _console.Profile.Height = 7;   // three region rows over the pane's four
        using var pane = Pane();
        pane.Show();
        int mark = Output.Length;
        pane.SetLive(new ReplyBlock("```text\n" + CodeLines(1, 12), glyph: false, codeKeep: 5));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(0, pane.LiveCommitted);
        Assert.Equal(3, pane.LiveRows);
        Assert.Contains(MarkdownView.CodeHeading("text") + "\n  c11\n  c12\n", Output[mark..]);
    }

    [Fact]
    public void AStreamingCodeBlock_WithinItsKeep_IsItsWholeRows()
    {
        using var pane = Pane();
        pane.Show();
        int mark = Output.Length;
        pane.SetLive(new ReplyBlock("```text\n" + CodeLines(1, 3), glyph: false, codeKeep: 5));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(4, pane.LiveRows);
        Assert.Contains(MarkdownView.CodeHeading("text") + "\n  c1\n  c2\n  c3\n", Output[mark..]);
    }

    [Fact]
    public void AStreamingCodeBlock_WithoutACodeKeep_StreamsAtFullHeight()
    {
        using var pane = Pane();
        pane.Show();
        pane.SetLive(new ReplyBlock("```text\n" + CodeLines(1, 12), glyph: false));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(7, pane.LiveCommitted);
        Assert.Equal(6, pane.LiveRows);
    }

    [Fact]
    public void AReplyEndingMidFence_FoldsTheWholeBlock()
    {
        using var pane = Pane();
        pane.Show();
        pane.SetLive(new ReplyBlock("```text\n" + CodeLines(1, 12), glyph: false, codeKeep: 3));
        _time.Advance(ScreenPane.Tick);
        pane.CommitLive();
        pane.Write(new Markup("next\n"));
        Assert.Equal(2, pane.StoredRows);
        pane.SetToolGroupsExpanded(true);
        Assert.Equal(14, pane.StoredRows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplyLiveView_TailsAsTheWholeReplyDoes(bool glyph)
    {
        static string Rows(List<SegmentLine> lines) =>
            string.Join("\n", lines.Select(l => string.Concat(l.Select(s => s.Text))));

        string text = "Some **prose** here.\n\n```text\n" + CodeLines(1, 40) + "\n" + new string('w', 90) + "\nz";
        var reply = new ReplyBlock(text, glyph, codeKeep: 4);
        var options = RenderOptions.Create(_console, _console.Profile.Capabilities);
        var wholeLines = ScreenPane.RenderLines(reply, _console, 40);
        var wholeOpen = reply.CodeSpans(options, 40)[^1];
        var view = reply.LiveView();
        var viewLines = ScreenPane.RenderLines(view, _console, 40);
        var viewOpen = view.OpenCodeSpan(options, 40, viewLines.Count)!;

        Assert.True(wholeOpen.Open);
        Assert.Equal(wholeOpen.LabelRow, viewOpen.LabelRow);
        Assert.Equal(Rows(ReplyBlock.Tail(wholeLines, wholeOpen, 4)), Rows(ReplyBlock.Tail(viewLines, viewOpen, 4)));
        Assert.Equal(4, view.OpenCode!.Lines.Count);
    }

    [Fact]
    public void ReplyOpenCode_OnlyAnUnclosedFence_WithAKeep()
    {
        Assert.NotNull(new ReplyBlock("```cs\nx", glyph: false, codeKeep: 3).OpenCode);
        Assert.Null(new ReplyBlock("```cs\nx", glyph: false).OpenCode);
        Assert.Null(new ReplyBlock("```cs\nx\n```", glyph: false, codeKeep: 3).OpenCode);
        Assert.Null(new ReplyBlock("text\n\n    indented", glyph: false, codeKeep: 3).OpenCode);
        Assert.Null(new ReplyBlock("```cs\nx\n```\n\nafter", glyph: false, codeKeep: 3).OpenCode);
        var block = new ReplyBlock("```cs\nx\n```\n\n```cs\n" + CodeLines(1, 5) + "\n```", glyph: false, codeKeep: 3);
        Assert.Equal(1, block.FoldingCode);
        Assert.Same(block, block.LiveView());
    }

    [Fact]
    public void ACodeBlock_WithoutACodeKeep_IsPlainRows()
    {
        using var pane = Pane();
        pane.Show();
        pane.SetLive(new ReplyBlock("```text\na\nb\nc\nd\n```\n\nafter", glyph: false));
        pane.CommitLive();

        Assert.Contains("text\n  a\n  b\n  c\n  d\n", Output);
        Assert.DoesNotContain(ToolGroupText.CollapsedGlyph, Output);
    }

    [Fact]
    public void ANarrowerWindow_ReLaysTheLiveBlock_OnTheTick()
    {
        using var pane = Pane();
        pane.Show();
        pane.SetLive(new Text("one two three four five six seven"));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(1, pane.LiveRows);

        _console.Profile.Width = 20;
        _time.Advance(ScreenPane.Tick);

        Assert.Equal(2, pane.LiveRows);
        Assert.Contains("one two three four \nfive six seven\n", Output);
    }

    [Fact]
    public void AnOverlay_OverALiveBlock_CommitsWhatNoLongerFits()
    {
        using var pane = Pane();
        pane.Show();
        pane.SetLive(LinesOf(6));
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(6, pane.LiveRows);
        Assert.Equal(0, pane.LiveCommitted);

        // Two overlay rows make the pane five rows: one live line goes into the flow.
        pane.ShowOverlay(new Markup("a\nb"), "hint");

        Assert.Equal(1, pane.LiveCommitted);
        Assert.Equal(5, pane.LiveRows);
        Assert.Equal(0, pane.FlowRow);   // scrolled off with the draw
        Assert.True(pane.OverlayOpen);
    }

    [Fact]
    public void DiscardLive_ErasesTheRows_AndWritesNothing()
    {
        _console.EmitAnsiSequences();
        using var pane = Pane();
        pane.Show();
        pane.SetLive(new RawText("one"));
        _time.Advance(ScreenPane.Tick);
        int mark = Output.Length;

        pane.DiscardLive();

        Assert.False(pane.LiveOpen);
        Assert.Equal(0, pane.LiveRows);
        Assert.Equal(0, pane.FlowRow);
        Assert.DoesNotContain("one", Output[mark..]);
        Assert.StartsWith("\e[?2026h\e[?25l\e[7A", Output[mark..]);   // 5 padding rows + the rule + the live row
    }

    [Fact]
    public void Clear_DropsTheSlot()
    {
        using var pane = Pane();
        pane.Show();
        pane.SetLive(new RawText("one"));
        _time.Advance(ScreenPane.Tick);

        pane.Clear(true);

        Assert.False(pane.LiveOpen);
        Assert.Equal(0, pane.LiveRows);
    }

    [Fact]
    public void SetLive_Disabled_IsNothing()
    {
        using var pane = Pane(geometry: false);
        pane.SetLive(new RawText("one"));
        pane.CommitLive();
        pane.DiscardLive();

        Assert.False(pane.LiveOpen);
        Assert.Equal("", Output);
    }

    [Fact]
    public void SetLive_InsideABatch_IsDrawnWhenItEnds()
    {
        using var pane = Pane();
        pane.Show();
        using (pane.Batch())
        {
            pane.SetLive(new RawText("one"));
            _time.Advance(ScreenPane.Tick);
            Assert.Equal(0, pane.LiveRows);
        }

        Assert.Equal(1, pane.LiveRows);
        Assert.EndsWith("one\n" + new string('\n', 5) + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n", Output);
    }

    [Fact]
    public void Busy_TheTickThatRepaintsTheBlock_AdvancesTheSpinnerToo()
    {
        using var pane = Pane();
        pane.Show();
        using var busy = pane.BeginBusy("writing");
        pane.SetLive(new RawText("one"));
        int draws = Draws;

        _time.Advance(ScreenPane.Tick);

        Assert.Equal(draws + 1, Draws);
        Assert.Equal(1, pane.LiveRows);
        Assert.Contains(Theme.SpinnerFrames[1], Output);
    }

    private static string Strip(string ansi) => System.Text.RegularExpressions.Regex.Replace(ansi, "\e\\[[0-9;?]*[A-Za-z]", "").Replace("\r\n", "\n");

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
    [Fact]
    public void ScreenLogLines_ArePinned()
    {
        Assert.Equal("Alternate buffer entered (240×60)", ScreenPane.AlternateEnteredLogLine(240, 60));
        Assert.Equal("Alternate buffer left", ScreenPane.AlternateLeftLogLine);
        Assert.Equal("Resized 240×60 → 200×50", ScreenPane.ResizedLogLine(240, 60, 200, 50));
    }

    // ── The toolbar (2026-09-21) ────────────────────────────────────────────

    /// <summary>The toolbar's text: the strip at column 0, the path ending on the last cell and cut from the front to the room between, gone under eight cells; the strip alone without one, cut to the row.</summary>
    [Fact]
    public void ToolbarRow_IsPinned()
    {
        Assert.Equal(2, ScreenPane.ToolbarGap);
        Assert.Equal(8, ScreenPane.ToolbarPathMinCells);
        const string strip = "🔧 🎓";   // five cells
        const string path = @"D:\Repo\NeonSidekick";   // twenty
        Assert.Equal(strip + new string(' ', 14) + path, ScreenPane.ToolbarRow(strip, path, 39));
        Assert.Equal(39, TextCells.Width(ScreenPane.ToolbarRow(strip, path, 39)));
        Assert.Equal(strip + "  …NeonSidekick", ScreenPane.ToolbarRow(strip, path, 20));   // thirteen cells of room: the tail
        Assert.Equal(strip + "  …idekick", ScreenPane.ToolbarRow(strip, path, 15));        // eight: the least
        Assert.Equal(strip, ScreenPane.ToolbarRow(strip, path, 14));                        // seven: no path
        Assert.Equal(strip, ScreenPane.ToolbarRow(strip, "", 39));
        Assert.Equal(new string(' ', 19) + path, ScreenPane.ToolbarRow("", path, 39));
        Assert.Equal(ScreenPane.Fit(strip, 4), ScreenPane.ToolbarRow(strip, path, 4));      // the strip cut to the row
        Assert.Equal("…", ScreenPane.ToolbarRow(strip, path, 1));
        Assert.Equal("", ScreenPane.ToolbarRow(strip, path, 0));
    }

    /// <summary>The toolbar's zones: each strip glyph with its first column — a variation selector with its glyph (later on 2026-09-21) — the path's cells at the right edge the path, the separators and the blanks the row.</summary>
    [Fact]
    public void ToolbarHitAt_IsPinned()
    {
        const string strip = "⚙️ 🛠️ 🧰";   // a one-cell gear with the selector, a pair with it, a bare pair: two cells each
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "⚙️", 0), ScreenPane.ToolbarHitAt(strip, 35, 4, 0));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "⚙️", 0), ScreenPane.ToolbarHitAt(strip, 35, 4, 1));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Row, "", -1), ScreenPane.ToolbarHitAt(strip, 35, 4, 2));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🛠️", 3), ScreenPane.ToolbarHitAt(strip, 35, 4, 3));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🛠️", 3), ScreenPane.ToolbarHitAt(strip, 35, 4, 4));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🧰", 6), ScreenPane.ToolbarHitAt(strip, 35, 4, 7));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Row, "", -1), ScreenPane.ToolbarHitAt(strip, 35, 4, 8));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Row, "", -1), ScreenPane.ToolbarHitAt(strip, 35, 4, 34));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Path, "", 35), ScreenPane.ToolbarHitAt(strip, 35, 4, 35));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Path, "", 35), ScreenPane.ToolbarHitAt(strip, 35, 4, 38));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Row, "", -1), ScreenPane.ToolbarHitAt(strip, 35, 4, 39));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Row, "", -1), ScreenPane.ToolbarHitAt(strip, -1, 0, 36));   // no path drawn
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Row, "", -1), ScreenPane.ToolbarHitAt("", -1, 0, 0));
        // The hint row's walk is the same one: the tag keeps its selector, the glyph after it its column.
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🏷️", 0), ScreenPane.HintHitAt("🏷️ 🔊", -1, 1));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", 3), ScreenPane.HintHitAt("🏷️ 🔊", -1, 3));
    }

    /// <summary>The row under the hint row while the provider answers: the pane one row taller (the padding one less), the path rewritten in place on the tick, the whole pane again when the row goes or comes.</summary>
    [Fact]
    public void Toolbar_IsDrawnUnderTheHintRow_AndFollowsOnTheTick()
    {
        string path = @"D:\x";
        bool shown = true;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Toolbar = () => shown ? new ScreenPane.ToolbarParts("🔧", path) : null;
        pane.Show();

        // 10 rows, the flow at row 0: five empty rows, then rule / input / rule / hint / toolbar — the toolbar at 39 cells, the path ending on the last.
        Assert.Equal(5, pane.Padding);
        Assert.Equal(1, pane.ToolbarRows);
        Assert.Equal(9, pane.LayoutHeight);
        string toolbar = "🔧" + new string(' ', 33) + @"D:\x";
        Assert.Equal(new string('\n', 5) + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\nidle\n" + toolbar, Output);
        Assert.Equal(1, Draws);

        // The working directory changed: the row again in place, nothing else.
        path = @"D:\Repo";
        int mark = Output.Length;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal("🔧" + new string(' ', 30) + @"D:\Repo", Output[mark..]);
        Assert.Equal(1, Draws);

        // The same again: nothing.
        mark = Output.Length;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal("", Output[mark..]);

        // The switch off: the pane's shape changed, so the whole pane again — the hint row its last.
        shown = false;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(2, Draws);
        Assert.Equal(0, pane.ToolbarRows);
        Assert.Equal(10, pane.LayoutHeight);
        Assert.Equal(6, pane.Padding);
        Assert.EndsWith(Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\nidle", Output);

        // And on again.
        shown = true;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(3, Draws);
        Assert.Equal(5, pane.Padding);
        Assert.EndsWith("\nidle\n🔧" + new string(' ', 30) + @"D:\Repo", Output);
    }

    /// <summary>A window too short for a transcript row over the smallest pane and the toolbar draws none: the pane as before.</summary>
    [Fact]
    public void Toolbar_IsDroppedInAWindowUnderSixRows()
    {
        _console.Profile.Height = 5;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Toolbar = () => new ScreenPane.ToolbarParts("🔧", @"D:\x");
        pane.Show();
        Assert.Equal(0, pane.ToolbarRows);
        Assert.Equal(5, pane.LayoutHeight);
        Assert.Equal(1, pane.Padding);
        Assert.EndsWith(Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\nidle", Output);

        _console.Profile.Height = 6;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(1, pane.ToolbarRows);
        Assert.EndsWith("\nidle\n🔧" + new string(' ', 33) + @"D:\x", Output);
    }

    /// <summary>The overlay and the draft are capped over the rows the toolbar leaves: one transcript row, the rule, the content, the rule, the hint, the toolbar.</summary>
    [Fact]
    public void Toolbar_ShortensTheOverlaysRoom()
    {
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Toolbar = () => new ScreenPane.ToolbarParts("🔧", @"D:\x");
        pane.Show();

        pane.ShowOverlay(new Markup(string.Join('\n', Enumerable.Range(1, 30).Select(i => "line " + i))), "hint");

        // 10 rows: one for the transcript, the rule, 5 content rows, the lower rule, the hint, the toolbar.
        Assert.Equal(5, pane.OverlayRows);
        Assert.Equal(1, pane.Padding);
        Assert.Contains("line 5\n" + Rule(40) + "\nhint\n🔧", Output);
        Assert.DoesNotContain("line 6", Output);
        Assert.False(pane.TryHitToolbar(0, 103, out _));   // under an overlay the row is the overlay's dismiss
    }

    /// <summary>
    /// The performance bar (2026-09-29): the pane's last row, under the toolbar — the toolbar one row up, its clicks with it —
    /// rewritten in place when its text changes on the tick, the whole pane again when it comes or goes.
    /// </summary>
    [Fact]
    public void PerfBar_IsTheLastRow_UnderTheToolbar_AndFollowsOnTheTick()
    {
        _cursorTop = 100;
        string text = "CPU 5%";
        bool shown = true;
        int asked = 0;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Toolbar = () => new ScreenPane.ToolbarParts("🔧", @"D:\x");
        pane.Perf = cells =>
        {
            asked = cells;
            return shown ? new PerfRow([new PerfSegment("CPU ", Theme.DimText), new PerfSegment(text[4..], Theme.DimText)]) : null;
        };
        pane.Show();
        pane.ShowInput("abc", 3);

        // Rule / input / rule / hint / toolbar / the bar: the input row at 100, the toolbar at 103, the bar at 104.
        Assert.Equal((1, 1), (pane.ToolbarRows, pane.PerfRows));
        Assert.Equal(8, pane.LayoutHeight);
        Assert.Equal(39, asked);   // the row's cells: the last column left empty, as every row leaves it
        Assert.Contains("\nidle\n🔧" + new string(' ', 33) + @"D:\x" + "\nCPU 5%", Output);
        Assert.True(pane.TryHitToolbar(1, 103, out var tool));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🔧", 0), tool);
        Assert.True(pane.TryHitToolbar(1, 104, out tool));   // the bar is the toolbar's blanks anywhere on it (2026-09-29): /settings
        Assert.Equal(ScreenPane.PerfBarHit, tool);
        Assert.True(pane.TryHitToolbar(30, 104, out tool));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Row, "", -1), tool);
        Assert.Equal(new ScreenPane.OffPaneHit(null, new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🔧", 0)), pane.OffPaneHitAt(1, 103));
        Assert.Equal(new ScreenPane.OffPaneHit(null, ScreenPane.PerfBarHit), pane.OffPaneHitAt(1, 104));
        Assert.Equal(-4, ScreenPane.OutsideKeyOf(pane.OffPaneHitAt(1, 104)));   // pairs with the blanks
        Assert.False(pane.TryHitToolbar(1, 105, out _));
        int draws = Draws;

        // A new reading: the row again in place, nothing else; the same again: nothing.
        text = "CPU 42%";
        int mark = Output.Length;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal("CPU 42%", Output[mark..]);
        mark = Output.Length;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal("", Output[mark..]);
        Assert.Equal(draws, Draws);

        // Off: the pane's shape changed, the whole pane again, the toolbar its last row.
        shown = false;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(draws + 1, Draws);
        Assert.Equal((1, 0), (pane.ToolbarRows, pane.PerfRows));
        Assert.Equal(9, pane.LayoutHeight);
        Assert.EndsWith("\nidle\n🔧" + new string(' ', 33) + @"D:\x", Output);

        // On again, without the toolbar: the bar under the hint row.
        shown = true;
        pane.Toolbar = static () => null;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal((0, 1), (pane.ToolbarRows, pane.PerfRows));
        Assert.EndsWith("\nidle\nCPU 42%", Output);
        Assert.True(pane.TryHitToolbar(1, 103, out tool));   // the bar alone, the last row at 103 now, answers too
        Assert.Equal(ScreenPane.PerfBarHit, tool);
        Assert.False(pane.TryHitToolbar(1, 102, out _));     // the hint row is the hint row's
    }

    /// <summary>
    /// A batch while drawn (2026-09-29, the user's report: the hint row, the toolbar and the performance bar flickered at
    /// every turn's end — the reply's commit is a batch): one synchronized frame from the lift's erase to the pane's last
    /// row, held on <see cref="ScreenPane.Frames"/> and let go once; a row rewritten inside it opens no second frame.
    /// </summary>
    [Fact]
    public void Batch_WhileDrawn_IsOneSynchronizedFrame_HeldAndLetGo()
    {
        _console.EmitAnsiSequences();
        var frames = new CountingFrames();
        using var pane = Pane();
        pane.Frames = frames;
        pane.Hint = () => "idle";
        pane.Toolbar = () => new ScreenPane.ToolbarParts("🔧", @"D:\x");
        pane.Perf = _ => new PerfRow([new PerfSegment("CPU 5%", Theme.DimText)]);
        pane.Show();
        int mark = Output.Length;

        using (pane.Batch())
        {
            pane.MarkupLine("a reply");
            Assert.Equal(1, frames.Open);
        }

        string written = Output[mark..];
        Assert.StartsWith("\e[?2026h", written);
        Assert.True(written.IndexOf("\e[J", StringComparison.Ordinal) > 0);
        Assert.EndsWith("\e[?2026l", written);
        Assert.Equal(1, Count(written, "\e[?2026h"));
        Assert.Contains("CPU 5%", written[..written.LastIndexOf("\e[?2026l", StringComparison.Ordinal)]);
        Assert.Equal((0, 1), (frames.Open, frames.Released));

        // Every frame after it balanced too: the tick's row rewrites, a redraw.
        _time.Advance(ScreenPane.Tick);
        pane.Touch();
        Assert.Equal(0, frames.Open);
    }

    /// <summary>A frame a draw left open (it threw) is closed on the next tick with no batch or modal running (2026-09-29).</summary>
    [Fact]
    public void AFrameLeftOpen_IsClosed_OnTheTick()
    {
        var frames = new CountingFrames();
        bool fail = false;
        using var pane = Pane();
        pane.Frames = frames;
        pane.Hint = () => fail ? throw new InvalidOperationException("boom") : "idle";
        pane.Show();
        var batch = pane.Batch();
        Assert.Equal(1, frames.Open);
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(1, frames.Open);   // a batch's frame stays while it runs

        fail = true;
        Assert.Throws<InvalidOperationException>(batch.Dispose);   // the closing draw throws: the frame is left open
        Assert.Equal(1, frames.Open);
        fail = false;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(0, frames.Open);
    }

    /// <summary>
    /// A held frame goes out before the console is asked where its cursor is (2026-09-29, the user's report: /clear left
    /// the pane under the banner — the console, not yet sent the clear, answered with the old bottom row, and the flow row
    /// was "corrected" to it). Here the console answers stale until the frame is settled.
    /// </summary>
    [Fact]
    public void AHeldFrame_IsSettled_BeforeTheConsoleIsAsked_SoAClearKeepsThePaneAtTheBottom()
    {
        var frames = new CountingFrames();
        using var pane = Pane();
        pane.Frames = frames;
        pane.Hint = () => "idle";
        pane.Show();
        _cursorRow = _console.Profile.Height - 3;   // the old input row: what the console says until the held output reaches it
        frames.Settled = () => _cursorRow = null;

        using (pane.Batch())
        {
            pane.Clear(home: true);
            pane.MarkupLine("banner");
        }

        Assert.True(frames.Settles > 0);
        Assert.True(pane.Padding > 0);   // at the bottom, the rows between the banner and the pane padded
        Assert.Equal(0, frames.Open);
    }

    private sealed class CountingFrames : IFrameHold
    {
        public int Open { get; private set; }

        public int Released { get; private set; }

        public int Settles { get; private set; }

        public Action? Settled { get; set; }

        public void Settle()
        {
            Settles++;
            Settled?.Invoke();
        }

        public void Hold() => Open++;

        public void Release()
        {
            Open--;
            if (Open == 0)
            {
                Released++;
            }
        }
    }

    /// <summary>A window with no transcript row to spare over the smallest pane, the toolbar and the bar draws no bar; one row more draws it.</summary>
    [Fact]
    public void PerfBar_IsDroppedWhereTheWindowHasNoRowForIt()
    {
        _console.Profile.Height = 6;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Toolbar = () => new ScreenPane.ToolbarParts("🔧", @"D:\x");
        pane.Perf = _ => new PerfRow([new PerfSegment("RAM 7%", Theme.DimText)]);
        pane.Show();
        Assert.Equal((1, 0), (pane.ToolbarRows, pane.PerfRows));
        Assert.DoesNotContain("RAM 7%", Output);

        _console.Profile.Height = 7;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal((1, 1), (pane.ToolbarRows, pane.PerfRows));
        Assert.EndsWith("\nRAM 7%", Output);
    }

    /// <summary>
    /// The off-pane parts under an overlay (later on 2026-09-21): the toolbar's glyph, path and
    /// blanks, the hint row's model name, mark and blanks — each with its own outside key, the
    /// transcript and the rules the readers' −2; nothing lifted or under the busy row.
    /// </summary>
    [Fact]
    public void OffPaneHitAt_AndOutsideKey_NameThePartUnderAnOverlay()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Strip = () => "🔊";
        pane.Trailer = () => "llama";
        pane.TrailerMark = () => "◕";
        pane.Toolbar = () => new ScreenPane.ToolbarParts("🔧 🎓", @"D:\x");
        pane.Show();
        pane.ShowOverlay(new Markup("a\nb"), "keys", close: true);   // rows 100–101, the rule 102, the hint row 103, the toolbar 104

        Assert.Equal(new ScreenPane.OffPaneHit(null, new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🔧", 0)), pane.OffPaneHitAt(1, 104));
        Assert.Equal(new ScreenPane.OffPaneHit(null, new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🎓", 3)), pane.OffPaneHitAt(3, 104));
        Assert.Equal(new ScreenPane.OffPaneHit(null, new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Row, "", -1)), pane.OffPaneHitAt(10, 104));
        Assert.Equal(new ScreenPane.OffPaneHit(null, new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Path, "", 35)), pane.OffPaneHitAt(37, 104));
        Assert.Equal(new ScreenPane.OffPaneHit(new ScreenPane.HintHit(ScreenPane.HintZone.Trailer, "", 32), null), pane.OffPaneHitAt(33, 103));
        Assert.Equal(new ScreenPane.OffPaneHit(new ScreenPane.HintHit(ScreenPane.HintZone.Mark, "", 38), null), pane.OffPaneHitAt(38, 103));
        Assert.Equal(new ScreenPane.OffPaneHit(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", 0), null), pane.OffPaneHitAt(0, 103));
        Assert.Equal(new ScreenPane.OffPaneHit(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1), null), pane.OffPaneHitAt(10, 103));
        Assert.Null(pane.OffPaneHitAt(5, 50));     // the transcript
        Assert.Null(pane.OffPaneHitAt(5, 99));     // the upper rule
        Assert.Null(pane.OffPaneHitAt(5, 102));    // the lower rule
        Assert.Null(pane.OffPaneHitAt(0, 100));    // the overlay's row
        Assert.False(pane.TryHitToolbar(1, 104, out _));   // the guarded test still refuses under the overlay
        Assert.False(pane.TryHitHint(33, 103, out _));

        // The keys: one per part, −2 elsewhere, none the pairing's −1.
        Assert.Equal(-5, pane.OutsideKey(1, 104));
        Assert.Equal(-8, pane.OutsideKey(3, 104));
        Assert.Equal(-4, pane.OutsideKey(10, 104));
        Assert.Equal(-3, pane.OutsideKey(37, 104));
        Assert.Equal(-102, pane.OutsideKey(33, 103));
        Assert.Equal(-106, pane.OutsideKey(38, 103));
        Assert.Equal(-200, pane.OutsideKey(0, 103));
        Assert.Equal(-100, pane.OutsideKey(10, 103));
        Assert.Equal(MenuPane.OutsideRow, pane.OutsideKey(5, 50));
        Assert.Equal(MenuPane.OutsideRow, ScreenPane.OutsideKeyOf(null));

        // The dismiss keeps the part, once; a Dismiss() without a click keeps nothing; a new overlay forgets it.
        pane.Dismiss(3, 104);
        Assert.True(pane.Dismissed);
        Assert.Equal(new ScreenPane.OffPaneHit(null, new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🎓", 3)), pane.TakeDismissHit());
        Assert.Null(pane.TakeDismissHit());
        pane.Dismiss(5, 50);
        Assert.Null(pane.TakeDismissHit());
        pane.Dismiss(3, 104);
        pane.ShowOverlay(new Markup("a\nb"), "keys", close: true);
        Assert.Null(pane.TakeDismissHit());
        pane.Dismiss(3, 104);
        pane.CloseOverlay();
        Assert.Equal(new ScreenPane.OffPaneHit(null, new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🎓", 3)), pane.TakeDismissHit());   // the close keeps it for the host's caller

        // Lifted: nothing.
        using (pane.Batch())
        {
            Assert.Null(pane.OffPaneHitAt(1, 104));
        }

        // The busy row names nothing on the hint row; the toolbar still answers.
        pane.ShowOverlay(new Markup("a\nb"), "keys", close: true);
        using (pane.BeginBusy("thinking"))
        {
            Assert.Null(pane.OffPaneHitAt(33, 103));
            Assert.Equal(new ScreenPane.OffPaneHit(null, new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🔧", 0)), pane.OffPaneHitAt(0, 104));
        }
    }

    /// <summary>The drawn row's zones from the cursor's row: the hint row still two under it, the toolbar three; the glyphs and the path answer under the busy row too, the blanks are the row.</summary>
    [Fact]
    public void TryHitToolbar_NamesTheGlyphsAndThePath_TheHintRowStaysAbove()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Strip = () => "🔊";
        pane.Toolbar = () => new ScreenPane.ToolbarParts("🔧 🎓", @"D:\x");
        pane.Show();
        pane.ShowInput("abc", 3);

        Assert.True(pane.TryHitHint(0, 102, out var hint));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", 0), hint);
        Assert.False(pane.TryHitToolbar(0, 102, out _));
        Assert.False(pane.TryHitHint(0, 103, out _));
        Assert.True(pane.TryHitToolbar(1, 103, out var tool));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🔧", 0), tool);
        Assert.True(pane.TryHitToolbar(3, 103, out tool));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🎓", 3), tool);
        Assert.True(pane.TryHitToolbar(20, 103, out tool));
        Assert.Equal(ScreenPane.ToolbarZone.Row, tool.Zone);
        Assert.True(pane.TryHitToolbar(34, 103, out tool));   // the blank before the path
        Assert.Equal(ScreenPane.ToolbarZone.Row, tool.Zone);
        Assert.True(pane.TryHitToolbar(35, 103, out tool));   // D:\x on the last four cells
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Path, "", 35), tool);
        Assert.True(pane.TryHitToolbar(38, 103, out tool));
        Assert.Equal(ScreenPane.ToolbarZone.Path, tool.Zone);

        using (pane.BeginBusy("thinking"))
        {
            Assert.True(pane.TryHitToolbar(0, 103, out tool));
            Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🔧", 0), tool);
            Assert.True(pane.TryHitToolbar(36, 103, out tool));
            Assert.Equal(ScreenPane.ToolbarZone.Path, tool.Zone);
        }

        // A path the row has no room for: no zone there.
        pane.Toolbar = () => new ScreenPane.ToolbarParts("🔧 🎓", new string('p', 60));
        _time.Advance(ScreenPane.Tick);
        Assert.True(pane.TryHitToolbar(38, 103, out tool));
        Assert.Equal(ScreenPane.ToolbarZone.Path, tool.Zone);   // cut from the front, still the path
        pane.Toolbar = () => new ScreenPane.ToolbarParts(new string('s', 35), @"D:\x");
        _time.Advance(ScreenPane.Tick);
        Assert.True(pane.TryHitToolbar(38, 103, out tool));
        Assert.Equal(ScreenPane.ToolbarZone.Row, tool.Zone);    // two cells of room: no path
        pane.Toolbar = () => new ScreenPane.ToolbarParts("🔧 🎓", @"D:\x");
        _time.Advance(ScreenPane.Tick);

        // A two-row draft: the cursor's row is the first input row, the hint row three under it, the toolbar four.
        pane.ShowInput(new string('x', 50), 3);
        Assert.Equal(2, pane.InputRows);
        Assert.True(pane.TryHitHint(0, 103, out _));
        Assert.True(pane.TryHitToolbar(0, 104, out tool));
        Assert.Equal(ScreenPane.ToolbarZone.Glyph, tool.Zone);

        // No toolbar: nothing.
        pane.Toolbar = static () => null;
        _time.Advance(ScreenPane.Tick);
        Assert.False(pane.TryHitToolbar(0, 104, out _));
        Assert.False(pane.TryHitToolbar(0, 103, out _));
    }

    /// <summary>Scrolled, the toolbar stays the screen's last row and the region is a row shorter.</summary>
    [Fact]
    public void Toolbar_StaysUnderTheScrolledWindow()
    {
        using var pane = Pane();
        pane.Toolbar = () => new ScreenPane.ToolbarParts("🔧", @"D:\x");
        pane.Open();
        pane.Show();
        for (int i = 1; i <= 12; i++)
        {
            pane.Write(new Markup(Line(i) + Environment.NewLine));
        }

        // The region is five rows (10 − the five-row pane): a page is four.
        pane.ScrollPage(-1);
        Assert.True(pane.Scrolled);
        Assert.Equal(3, pane.ScrollTop);
        Assert.EndsWith(Lines(4, 8) + Rule(40) + "\n" + InputLine.PromptGlyph + "\n" + Rule(40) + "\n" + ScreenPane.Fit(ScreenPane.ScrolledHint(4), 39) + "\n🔧" + new string(' ', 33) + @"D:\x", Strip(Output).TrimEnd());

        pane.ScrollToEnd();
        Assert.False(pane.Scrolled);
        Assert.EndsWith("\n🔧" + new string(' ', 33) + @"D:\x", Strip(Output).TrimEnd());
    }

    /// <summary>Pictures on the screen (later on 2026-09-24): a click on a tile's cell names its picture; the text above, the gap and the rows under the flow name none; without the pane nothing does.</summary>
    [Fact]
    public void Pictures_OnTheScreen_AClickNamesWhichOne()
    {
        _cursorTop = 100;   // rule 99 over the input row: the region's six rows are 93–98
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("a\n"));
        static ImageThumbnail Tile(int width) => new(width, 2, Enumerable.Repeat(Color.Red, width * 2).ToArray());
        pane.WritePictures(new ImageStrip([Tile(4), Tile(3)], [5, 6]));

        Assert.Null(pane.PictureAt(0, 93));        // "a"
        Assert.Equal(5, pane.PictureAt(1, 94));
        Assert.Null(pane.PictureAt(4, 94));        // the gap
        Assert.Equal(6, pane.PictureAt(6, 94));
        Assert.Null(pane.PictureAt(1, 95));        // under the flow

        using var plain = Pane(geometry: false);
        plain.WritePictures(new CenteredPicture(Tile(4), 1));
        Assert.Null(plain.PictureAt(0, 0));
    }

    /// <summary>A centred picture (/view, /imagine, the splash): every one of its rows names it across its columns, centred as Spectre centres it — (40 − 4) / 2 = 18.</summary>
    [Fact]
    public void Pictures_ACenteredOne_IsFoundOnEachOfItsRows()
    {
        _cursorTop = 100;   // the region's six rows are 93–98
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("a\n"));
        pane.WritePictures(new CenteredPicture(new ImageThumbnail(4, 4, Enumerable.Repeat(Color.Red, 16).ToArray()), 9));

        Assert.Equal([null, 9, 9, null], new[] { 93, 94, 95, 96 }.Select(y => pane.PictureAt(19, y)));   // a 4×4 picture is two rows
        Assert.Equal([null, 9, 9, null], new[] { 17, 18, 21, 22 }.Select(x => pane.PictureAt(x, 94)));
    }
}
