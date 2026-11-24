using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class QuestionMenuTests : IDisposable
{
    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly ManualTimeProvider _time = new();
    private readonly KeySource _keys;

    public QuestionMenuTests()
    {
        _console.Profile.Width = 60;
        _console.Profile.Height = 24;
        _keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
    }

    public void Dispose() => _console.Dispose();

    private static readonly AskQuestion Colour = new("Which color?", "Color", AskKind.Single, ["red", "blue"]);
    private static readonly AskQuestion Toppings = new("Toppings?", null, AskKind.Multi, ["cheese", "olives", "ham"]);
    private static readonly AskQuestion FileName = new("What should the file be called?", "Name", AskKind.Text, []);

    private static string Rule(int width) => new(ScreenPane.RuleGlyph, width);

    /// <summary>A hint as the 60-column pane draws it: cut to the row (the question pane's hints are written for 80 columns and up).</summary>
    private static string Hint(string keys) => ScreenPane.Fit(keys, 59);

    /// <summary><paramref name="before"/>, then the blank rows that hold every question at the tallest one's height (2026-10-01), then <paramref name="after"/>.</summary>
    private void AssertPadded(string before, string after) =>
        Assert.Matches(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(before) + "(?: ?\n)*" + System.Text.RegularExpressions.Regex.Escape(after)), Output);

    /// <summary>A title or strip row as the pane prints it since 2026-09-18: the text, then the × close glyph in column width − 2.</summary>
    private static string Titled(string row, int width = 60) => row + new string(' ', width - 2 - TextCells.Width(row)) + ScreenPane.CloseGlyph;

    private string Output => _console.Output;

    private (QuestionMenu Menu, ScreenPane Pane) Menu(bool geometry = true)
    {
        var pane = new ScreenPane(_console, geometry ? new ScreenGeometry(() => null) : null, _time) { Hint = () => "idle" };
        if (geometry)
        {
            pane.Show();
        }

        return (new QuestionMenu(new MenuPane(pane, _keys), new InputLine(pane, _keys)), pane);
    }

    private void Push(params ConsoleKeyInfo[] keys)
    {
        foreach (var key in keys)
        {
            _console.Input.PushKey(key);
        }
    }

    private void Type(string text)
    {
        foreach (char c in text)
        {
            _console.Input.PushKey(Keys.Char(c));
        }
    }

    /// <summary>The answers as the model reads them, a line each.</summary>
    private static string[] Flat(AskReply? reply) =>
        reply!.Answers!.Select(a => AskUserText.AnswerLine(a.Question.Question, AskUserText.AnswerText(a))).ToArray();

    /// <summary>A reply that is the user's decline, with <paramref name="note"/>.</summary>
    private static void AssertDeclined(AskReply? reply, string? note = null)
    {
        Assert.NotNull(reply);
        Assert.Null(reply.Answers);
        Assert.True(reply.Shown);
        Assert.Equal(note, reply.DeclineNote);
    }

    [Fact]
    public async Task Single_EnterMarksAndAdvances_Multi_SpaceToggles_SubmitReturnsTheAnswers()
    {
        var (menu, pane) = Menu();
        Push(Keys.Down, Keys.Enter, Keys.Char(' '), Keys.Down, Keys.Char(' '), Keys.Right, Keys.Enter);

        var reply = await menu.AskAsync([Colour, Toppings], CancellationToken.None);

        Assert.Equal(["Which color? — blue", "Toppings? — cheese, olives"], Flat(reply));
        Assert.Same(Colour, reply!.Answers![0].Question);
        Assert.False(pane.OverlayOpen);
        // The first tab: the strip, the question as the caption, the spacer, the options, the Other row, the single keys.
        AssertPadded(Rule(60) + "\n" + Titled("Questions │ Color · Q2 · Submit ") + "\nWhich color?\n \n▸ ( ) red\n  ( ) blue\n  ( ) Other…\n", Rule(60) + "\n" + Hint(QuestionMenu.SingleKeys) + "\n");
        // Enter on blue: the next tab, its own caption and keys.
        AssertPadded("\nToppings?\n \n▸ [ ] cheese\n  [ ] olives\n  [ ] ham\n  [ ] Other…\n", Rule(60) + "\n" + Hint(QuestionMenu.MultiKeys) + "\n");
        Assert.Contains("\n▸ [x] cheese\n  [ ] olives\n", Output);
        Assert.Contains("\n  [x] cheese\n▸ [x] olives\n", Output);
        // The Submit tab: every question with its answer, the cursor on Submit, the decline row under it.
        AssertPadded("\n" + Titled("Questions │ Color · Q2 · Submit ") + "\n" + QuestionMenu.SubmitCaption(2, 2) + "\n \n  Q1 Color — blue\n  Q2 — cheese, olives\n▸ Submit\n  " + QuestionMenu.DeclineRow + "\n", Rule(60) + "\n" + Hint(QuestionMenu.SubmitKeys));
    }

    /// <summary>A long question is drawn whole over its options (2026-10-10, the user's ask: past three rows it was cut by the ellipsis).</summary>
    [Fact]
    public async Task ALongQuestion_IsShownWhole()
    {
        var (menu, _) = Menu();
        Push(Keys.Escape);
        // Forty six-cell words: eight to a 60-cell row, five rows.
        string text = string.Join(' ', Enumerable.Range(1, 40).Select(i => "word" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)));

        AssertDeclined(await menu.AskAsync([new AskQuestion(text, null, AskKind.Single, ["red", "blue"])], CancellationToken.None));

        Assert.Contains("\nword01 word02 word03 word04 word05 word06 word07 word08\n", Output);
        Assert.Contains("\nword33 word34 word35 word36 word37 word38 word39 word40\n \n▸ ( ) red\n  ( ) blue\n  ( ) Other…\n", Output);
        Assert.DoesNotContain("word32…", Output);
    }

    [Fact]
    public async Task Single_SpaceMarksWithoutAdvancing_AndATabRemembersItsRow()
    {
        var (menu, _) = Menu();
        Push(Keys.Down, Keys.Char(' '), Keys.Enter, Keys.Left, Keys.Escape);

        AssertDeclined(await menu.AskAsync([Colour, Toppings], CancellationToken.None));

        // Space: marked, still on the first tab; Enter: on to the second; Left: back on the row it was left at.
        Assert.Contains("\nWhich color?\n \n  ( ) red\n▸ (x) blue\n  ( ) Other…\n", Output);
        Assert.Contains("\nToppings?\n \n▸ [ ] cheese\n", Output);
        int marked = Output.IndexOf("\n  ( ) red\n▸ (x) blue\n", StringComparison.Ordinal);
        Assert.True(Output.LastIndexOf("\n  ( ) red\n▸ (x) blue\n", StringComparison.Ordinal) > marked);
    }

    /// <summary>Space on the marked row clears it (2026-11-24, the user's pick), so a mis-pick becomes a skip; Enter on it only marks.</summary>
    [Fact]
    public async Task Single_SpaceOnTheMarkedRow_ClearsIt_EnterKeepsIt()
    {
        var (menu, _) = Menu();
        Push(Keys.Char(' '), Keys.Char(' '));        // red marked, then cleared
        Push(Keys.Right, Keys.Right, Keys.Enter);    // Submit
        Assert.Equal(["Which color? — (skipped)", "Toppings? — (skipped)"], Flat(await menu.AskAsync([Colour, Toppings], CancellationToken.None)));
        Assert.Contains("\n▸ (x) red\n", Output);

        var (again, _) = Menu();
        Push(Keys.Char(' '), Keys.Enter);            // red marked, Enter on it: still marked, on to the next tab
        Push(Keys.Right, Keys.Enter);
        Assert.Equal(["Which color? — red", "Toppings? — (skipped)"], Flat(await again.AskAsync([Colour, Toppings], CancellationToken.None)));
    }

    [Fact]
    public async Task Other_OnASingle_TypedTextIsTheAnswer_Tagged_ReplacesTheMark_AndSubmitSkipsTheRest()
    {
        var (menu, _) = Menu();
        Push(Keys.Down, Keys.Enter);            // blue, on to the second tab
        Push(Keys.Left, Keys.End, Keys.Enter);  // back, the Other row, the slot
        Type("teal");
        Push(Keys.Enter);                       // saved: on to the second tab again
        Push(Keys.Left, Keys.Right, Keys.Right, Keys.Enter);   // a look back, then the Submit tab: Submit with Q2 unanswered

        var reply = await menu.AskAsync([Colour, Toppings], CancellationToken.None);

        Assert.Equal(["Which color? — teal (typed)", "Toppings? — (skipped)"], Flat(reply));
        Assert.True(reply!.Answers![1].Skipped);
        // The slot under the list with the Other keys, then the row marked with the text and blue unmarked.
        // (The tab was reached by ← inside the pane: the slot must draw THAT tab, not the one the page was built for.)
        AssertPadded("\nWhich color?\n \n  ( ) red\n  (x) blue\n▸ ( ) Other…\n", "› \n" + Rule(60) + "\n" + QuestionMenu.OtherKeys);
        Assert.Contains("\n  ( ) red\n  ( ) blue\n▸ (x) Other: teal\n", Output);
        // The Submit tab shows the answers as the model reads them, and the count (C).
        Assert.Contains("\n  Q1 Color — teal (typed)\n  Q2 — (skipped)\n▸ Submit\n", Output);
        Assert.Contains("1 of 2 answered;", Output);
    }

    [Fact]
    public async Task Other_TypedAsAnOptionsLabel_IsThatOption_NeverTyped()
    {
        var (menu, _) = Menu();
        Push(Keys.End, Keys.Enter);             // the Other row
        Type("  BLUE ");
        Push(Keys.Enter);                       // blue: marked, on to the next tab
        Push(Keys.End, Keys.Enter);             // Toppings' Other row
        Type("Ham");
        Push(Keys.Enter, Keys.Right, Keys.Enter);

        Assert.Equal(["Which color? — blue", "Toppings? — ham"], Flat(await menu.AskAsync([Colour, Toppings], CancellationToken.None)));
        Assert.Contains("\n  Q1 Color — blue\n  Q2 — ham\n", Output);
    }

    [Fact]
    public async Task Other_OnAMulti_StandsBesideTheChecks_EmptyClears_EscKeepsWhatWasThere()
    {
        var (menu, _) = Menu();
        Push(Keys.Char(' '), Keys.End, Keys.Enter);   // cheese checked, the Other slot
        Type("extra");
        Push(Keys.Enter);                             // saved, still on the tab
        Push(Keys.Enter, Keys.Escape);                // the slot again, pre-filled; ESC keeps it
        Push(Keys.Enter);                             // the slot again
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Enter);   // emptied: cleared
        Push(Keys.Right, Keys.Enter);                 // Submit (a lone multi keeps its Submit tab)

        var reply = await menu.AskAsync([Toppings], CancellationToken.None);

        Assert.Equal(["Toppings? — cheese"], Flat(reply));
        // (The slot's pre-filled text is written by the input line's own cursor moves, not in the overlay's draw.)
        Assert.Contains("\n▸ [x] Other: extra\n› \n" + Rule(60) + "\n" + QuestionMenu.OtherKeys, Output);
        Assert.Contains("\n  Q1 — cheese\n▸ Submit\n", Output);
        Assert.Contains(Titled("Questions │ Q1 · Submit "), Output);
    }

    [Fact]
    public async Task Other_TypedOnAMulti_IsLastInTheAnswer()
    {
        var (menu, _) = Menu();
        Push(Keys.End, Keys.Enter);
        Type("bacon");
        Push(Keys.Enter, Keys.Home, Keys.Char(' '), Keys.Right, Keys.Enter);

        Assert.Equal(["Toppings? — cheese, bacon (typed)"], Flat(await menu.AskAsync([Toppings], CancellationToken.None)));
    }

    [Fact]
    public async Task Submit_ASummaryRow_GoesBackToThatQuestion_OnItsRememberedRow()
    {
        var (menu, _) = Menu();
        Push(Keys.Down, Keys.Enter);           // blue: on to Q2
        Push(Keys.Right);                      // Submit, on its row
        Push(Keys.Up, Keys.Up, Keys.Enter);    // Q1's summary row: back to the question
        Push(Keys.Enter);                      // blue again: on to Q2
        Push(Keys.Right, Keys.Enter);          // Submit

        var reply = await menu.AskAsync([Colour, Toppings], CancellationToken.None);

        Assert.Equal(["Which color? — blue", "Toppings? — (skipped)"], Flat(reply));
        Assert.Contains("\n▸ Q1 Color — blue\n  Q2 — (skipped)\n  Submit\n", Output);
        Assert.Contains("\nWhich color?\n \n  ( ) red\n▸ (x) blue\n", Output);
    }

    // ── 2026-11-24, the ask_user plan ──

    [Fact]
    public async Task AText_Question_OneRow_TheSlotSavesAndMovesOn()
    {
        var (menu, _) = Menu();
        Push(Keys.Enter);                      // the slot
        Type("notes-2026");
        Push(Keys.Enter);                      // saved: on to Color
        Push(Keys.Left);                       // back: the row shows the answer
        Push(Keys.Right, Keys.Right, Keys.Enter);

        var reply = await menu.AskAsync([FileName, Colour], CancellationToken.None);

        Assert.Equal(["What should the file be called? — notes-2026 (typed)", "Which color? — (skipped)"], Flat(reply));
        Assert.Contains("\nWhat should the file be called?\n \n▸ " + QuestionMenu.TextRow + "\n", Output);
        Assert.Contains("\n▸ " + QuestionMenu.AnswerPrefix + "notes-2026\n", Output);
        Assert.Contains(Hint(QuestionMenu.TextKeys), Output);
    }

    /// <summary>B: one single-choice or text question has no Submit tab; choosing answers it, an empty save stays.</summary>
    [Fact]
    public async Task ALoneSingleOrTextQuestion_HasNoSubmitTab_ChoosingAnswersIt()
    {
        var (menu, _) = Menu();
        Push(Keys.Down, Keys.Enter);
        Assert.Equal(["Which color? — blue"], Flat(await menu.AskAsync([Colour], CancellationToken.None)));
        Assert.Contains(Titled("Questions │ Color "), Output);
        Assert.DoesNotContain("· Submit", Output);
        Assert.Contains("\n  ( ) Other…\n  " + QuestionMenu.DeclineRow + "\n", Output);

        var (text, _) = Menu();
        Push(Keys.Enter, Keys.Enter);          // an empty save: no answer and no skip, the tab stays
        Push(Keys.Enter);
        Type("draft");
        Push(Keys.Enter);
        Assert.Equal(["What should the file be called? — draft (typed)"], Flat(await text.AskAsync([FileName], CancellationToken.None)));
    }

    [Fact]
    public async Task Recommended_IsTagged_AndTheCursorStartsOnIt_NeverChosen()
    {
        var (menu, _) = Menu();
        var question = new AskQuestion("Which color?", null, AskKind.Single, ["red", new AskOption("blue", null, true)]);
        Push(Keys.Escape);
        AssertDeclined(await menu.AskAsync([question], CancellationToken.None));
        Assert.Contains("\n  ( ) red\n▸ ( ) blue" + QuestionMenu.RecommendedTag + "\n", Output);
    }

    [Fact]
    public async Task TheFooter_SaysTheCursorsDescription_AndAWholeLabelTheRowCuts()
    {
        var (menu, _) = Menu();
        string longLabel = "a label long enough that the sixty-cell row cuts it off";
        var question = new AskQuestion("Which?", null, AskKind.Single,
            [new AskOption("red", "Warm and bold."), new AskOption(longLabel), "green"]);
        Push(Keys.Down, Keys.Escape);
        AssertDeclined(await menu.AskAsync([question], CancellationToken.None));
        Assert.Contains("Warm and bold.", Output);
        Assert.Contains(longLabel, Output);
    }

    [Fact]
    public async Task Digits_JumpToARow_OnQuestionTabsAndTheSubmitTab_PastTheEndIgnored()
    {
        var (menu, _) = Menu();
        Push(Keys.Char('2'), Keys.Enter);              // blue: on to Toppings
        Push(Keys.Char('9'), Keys.Char('3'), Keys.Char(' '));   // 9: no row; 3: ham, checked
        Push(Keys.Right, Keys.Char('1'), Keys.Enter);  // Submit tab, 1: Q1's summary row, back to Color
        Push(Keys.Right, Keys.Right, Keys.Enter);      // Submit

        Assert.Equal(["Which color? — blue", "Toppings? — ham"], Flat(await menu.AskAsync([Colour, Toppings], CancellationToken.None)));
    }

    [Fact]
    public async Task Esc_IsTheUsersDecline_AndClosesThePane()
    {
        var (menu, pane) = Menu();
        Push(Keys.Down, Keys.Escape);
        AssertDeclined(await menu.AskAsync([Colour], CancellationToken.None));
        Assert.False(pane.OverlayOpen);
    }

    [Fact]
    public async Task CtrlC_IsADecline_AndClosesThePane_LikeEsc()
    {
        // Not answered, the turn runs on (2026-09-17): the pane is backed out of, never the reply cancelled from inside it.
        var (menu, pane) = Menu();
        Push(Keys.Down, Keys.CtrlC);
        AssertDeclined(await menu.AskAsync([Colour], CancellationToken.None));
        Assert.False(pane.OverlayOpen);
    }

    /// <summary>E: the decline row takes a note; an empty one goes back to the tab.</summary>
    [Fact]
    public async Task DeclineWithANote_FromTheSubmitTab_AndFromALoneQuestionsTab_AnEmptyNoteGoesBack()
    {
        var (menu, _) = Menu();
        Push(Keys.Right, Keys.Right, Keys.Down, Keys.Enter, Keys.Enter);   // the Submit tab's decline row, an empty note: back
        Push(Keys.Enter);
        Type("ask me later");
        Push(Keys.Enter);
        AssertDeclined(await menu.AskAsync([Colour, Toppings], CancellationToken.None), "ask me later");
        Assert.Contains(Hint(QuestionMenu.DeclineKeys), Output);

        var (lone, _) = Menu();
        Push(Keys.End, Keys.Enter);
        Type("nah");
        Push(Keys.Enter);
        AssertDeclined(await lone.AskAsync([Colour], CancellationToken.None), "nah");
    }

    [Fact]
    public async Task ACancelledToken_IsNoDecline_Null()
    {
        var (menu, _) = Menu();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            Assert.Null(await menu.AskAsync([Colour], cts.Token));
        }
        catch (OperationCanceledException)
        {
            // The pane may end by cancellation instead: either way, never a decline.
        }
    }

    [Fact]
    public async Task ADisabledPane_OrNoQuestions_AnswerNull()
    {
        var (menu, _) = Menu();
        Assert.Null(await menu.AskAsync([], CancellationToken.None));

        var (disabled, _) = Menu(geometry: false);
        Push(Keys.Enter);
        Assert.Null(await disabled.AskAsync([Colour], CancellationToken.None));
    }

    [Fact]
    public void Statics_ArePinned()
    {
        Assert.Equal(12, QuestionMenu.TabTitleCells);

        Assert.Equal("Q1", QuestionMenu.TabTitle(1, null));
        Assert.Equal("Q3", QuestionMenu.TabTitle(3, "  "));
        Assert.Equal("Color", QuestionMenu.TabTitle(1, " Color "));
        Assert.Equal("A very long…", QuestionMenu.TabTitle(1, "A very long title indeed"));
        Assert.Equal("Q2", QuestionMenu.SummaryLabel(2, null));
        Assert.Equal("Q2 Color", QuestionMenu.SummaryLabel(2, "Color"));
        Assert.Equal("Q1 — " + Theme.DimMarkup(AskUserText.Skipped), QuestionMenu.SummaryRow("Q1", null));
        Assert.Equal("Q1 — a, b", QuestionMenu.SummaryRow("Q1", "a, b"));
        Assert.Equal("(x) a[[b]]", QuestionMenu.OptionRow(false, true, "a[b]"));
        Assert.Equal("( ) a", QuestionMenu.OptionRow(false, false, "a"));
        Assert.Equal("[[x]] a", QuestionMenu.OptionRow(true, true, "a"));
        Assert.Equal("[[ ]] a", QuestionMenu.OptionRow(true, false, "a"));
        Assert.Equal("( ) a" + Theme.DimMarkup(QuestionMenu.RecommendedTag), QuestionMenu.OptionRow(false, false, "a", recommended: true));
        Assert.Equal("( ) Other…", QuestionMenu.OtherRowMarkup(false, ""));
        Assert.Equal("[[x]] Other: t[[1]]", QuestionMenu.OtherRowMarkup(true, "t[1]"));
        Assert.Equal(QuestionMenu.TextRow, QuestionMenu.TextRowMarkup(""));
        Assert.Equal(QuestionMenu.AnswerPrefix + "a[[b]]", QuestionMenu.TextRowMarkup("a[b]"));
        // C: the count only while some are unanswered.
        Assert.Contains("1 of 3 answered", QuestionMenu.SubmitCaption(1, 3), StringComparison.Ordinal);
        Assert.DoesNotContain(" of ", QuestionMenu.SubmitCaption(3, 3), StringComparison.Ordinal);
    }
}
