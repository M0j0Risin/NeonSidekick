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

    private static readonly AskQuestion Colour = new("Which colour?", "Colour", false, ["red", "blue"]);
    private static readonly AskQuestion Toppings = new("Toppings?", null, true, ["cheese", "olives", "ham"]);

    private static string Rule(int width) => new(ScreenPane.RuleGlyph, width);

    /// <summary><paramref name="before"/>, then the blank rows that hold every question at the tallest one's height (2026-10-01), then <paramref name="after"/>.</summary>
    private void AssertPadded(string before, string after) =>
        Assert.Matches(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(before) + "(?: \n)*" + System.Text.RegularExpressions.Regex.Escape(after)), Output);

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

    private static string[] Flat(IReadOnlyList<AskAnswer> answers) => answers.Select(a => a.Question.Question + " — " + string.Join(", ", a.Choices)).ToArray();

    [Fact]
    public async Task Single_EnterMarksAndAdvances_Multi_SpaceToggles_SubmitReturnsTheAnswers()
    {
        var (menu, pane) = Menu();
        Push(Keys.Down, Keys.Enter, Keys.Char(' '), Keys.Down, Keys.Char(' '), Keys.Right, Keys.Enter);

        var answers = await menu.AskAsync([Colour, Toppings], CancellationToken.None);

        Assert.Equal(["Which colour? — blue", "Toppings? — cheese, olives"], Flat(answers!));
        Assert.Same(Colour, answers![0].Question);
        Assert.False(pane.OverlayOpen);
        // The first tab: the strip, the question as the caption, the spacer, the options, the Other row, the single keys.
        AssertPadded(Rule(60) + "\n" + Titled("Questions │ Colour · Q2 · Submit ") + "\nWhich colour?\n \n▸ ( ) red\n  ( ) blue\n  ( ) Other…\n", Rule(60) + "\n" + QuestionMenu.SingleKeys + "\n");
        // Enter on blue: the next tab, its own caption and keys.
        AssertPadded("\nToppings?\n \n▸ [ ] cheese\n  [ ] olives\n  [ ] ham\n  [ ] Other…\n", Rule(60) + "\n" + QuestionMenu.MultiKeys + "\n");
        Assert.Contains("\n▸ [x] cheese\n  [ ] olives\n", Output);
        Assert.Contains("\n  [x] cheese\n▸ [x] olives\n", Output);
        // The Submit tab: every question with its answer, the cursor on Submit.
        AssertPadded("\n" + Titled("Questions │ Colour · Q2 · Submit ") + "\n" + QuestionMenu.SubmitCaption + "\n \n  Q1 Colour — blue\n  Q2 — cheese, olives\n▸ Submit\n", Rule(60) + "\n" + QuestionMenu.SubmitKeys);
    }

    [Fact]
    public async Task Single_SpaceMarksWithoutAdvancing_AndATabRemembersItsRow()
    {
        var (menu, _) = Menu();
        Push(Keys.Down, Keys.Char(' '), Keys.Enter, Keys.Left, Keys.Escape);

        Assert.Null(await menu.AskAsync([Colour, Toppings], CancellationToken.None));

        // Space: marked, still on the first tab; Enter: on to the second; Left: back on the row it was left at.
        Assert.Contains("\nWhich colour?\n \n  ( ) red\n▸ (x) blue\n  ( ) Other…\n", Output);
        Assert.Contains("\nToppings?\n \n▸ [ ] cheese\n", Output);
        int marked = Output.IndexOf("\n  ( ) red\n▸ (x) blue\n", StringComparison.Ordinal);
        Assert.True(Output.LastIndexOf("\n  ( ) red\n▸ (x) blue\n", StringComparison.Ordinal) > marked);
    }

    [Fact]
    public async Task Other_OnASingle_TypedTextIsTheAnswer_ReplacesTheMark_AndShowsOnTheRow()
    {
        var (menu, _) = Menu();
        Push(Keys.Down, Keys.Enter);            // blue, on to the second tab
        Push(Keys.Left, Keys.End, Keys.Enter);  // back, the Other row, the slot
        Type("teal");
        Push(Keys.Enter);                       // saved: on to the second tab again
        Push(Keys.Left, Keys.Right, Keys.Right, Keys.Enter);   // the Submit tab, Submit with Q2 unanswered
        Push(Keys.Escape);

        Assert.Null(await menu.AskAsync([Colour, Toppings], CancellationToken.None));

        // The slot under the list with the Other keys, then the row marked with the text and blue unmarked.
        // (The tab was reached by ← inside the pane: the slot must draw THAT tab, not the one the page was built for.)
        AssertPadded("\nWhich colour?\n \n  ( ) red\n  (x) blue\n▸ ( ) Other…\n", "› \n" + Rule(60) + "\n" + QuestionMenu.OtherKeys);
        Assert.Contains("\n  ( ) red\n  ( ) blue\n▸ (x) Other: teal\n", Output);
        Assert.Contains("\n  Q1 Colour — teal\n  Q2 — " + QuestionMenu.NoAnswer + "\n▸ Submit\n", Output);
        // Submit with a gap: the error on the status line, the tab still up.
        Assert.Contains("\n" + QuestionMenu.SubmitCaption + "\n  ✗ Q2 has no answer yet.\n  Q1 Colour — teal\n", Output);
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
        Push(Keys.Right, Keys.Enter);                 // Submit

        var answers = await menu.AskAsync([Toppings], CancellationToken.None);

        Assert.Equal(["Toppings? — cheese"], Flat(answers!));
        // (The slot's pre-filled text is written by the input line's own cursor moves, not in the overlay's draw.)
        Assert.Contains("\n▸ [x] Other: extra\n› \n" + Rule(60) + "\n" + QuestionMenu.OtherKeys, Output);
        Assert.Contains("\n  Q1 — cheese\n▸ Submit\n", Output);
    }

    [Fact]
    public async Task Other_TypedOnAMulti_IsLastInTheAnswer()
    {
        var (menu, _) = Menu();
        Push(Keys.End, Keys.Enter);
        Type("bacon");
        Push(Keys.Enter, Keys.Home, Keys.Char(' '), Keys.Right, Keys.Enter);

        var answers = await menu.AskAsync([Toppings], CancellationToken.None);

        Assert.Equal(["Toppings? — cheese, bacon"], Flat(answers!));
    }

    [Fact]
    public async Task Submit_ASummaryRow_GoesBackToThatQuestion_OnItsRememberedRow()
    {
        var (menu, _) = Menu();
        Push(Keys.Down, Keys.Enter);   // blue: on to Submit (the one question)
        Push(Keys.Up, Keys.Enter);     // the summary row: back to the question
        Push(Keys.Enter);              // blue again: Submit, on its own row
        Push(Keys.Enter);

        var answers = await menu.AskAsync([Colour], CancellationToken.None);

        Assert.Equal(["Which colour? — blue"], Flat(answers!));
        Assert.Contains("\n" + Titled("Questions │ Colour · Submit ") + "\n" + QuestionMenu.SubmitCaption + "\n \n▸ Q1 Colour — blue\n  Submit\n", Output);
        Assert.Contains("\nWhich colour?\n \n  ( ) red\n▸ (x) blue\n", Output);
    }

    [Fact]
    public async Task Esc_ReturnsNull_AndClosesThePane()
    {
        var (menu, pane) = Menu();
        Push(Keys.Down, Keys.Escape);
        Assert.Null(await menu.AskAsync([Colour], CancellationToken.None));
        Assert.False(pane.OverlayOpen);
    }

    [Fact]
    public async Task CtrlC_ReturnsNull_AndClosesThePane_LikeEsc()
    {
        // Not answered, the turn runs on (2026-09-17): the pane is backed out of, never the reply cancelled from inside it.
        var (menu, pane) = Menu();
        Push(Keys.Down, Keys.CtrlC);
        Assert.Null(await menu.AskAsync([Colour], CancellationToken.None));
        Assert.False(pane.OverlayOpen);
    }

    [Fact]
    public async Task NoKeyboard_ADisabledPane_OrNoQuestions_AnswerNull()
    {
        var (menu, _) = Menu();
        Assert.Null(await menu.AskAsync([Colour], CancellationToken.None));   // the drained source: no keyboard

        var (disabled, _) = Menu(geometry: false);
        Push(Keys.Enter);
        Assert.Null(await disabled.AskAsync([Colour], CancellationToken.None));
        Assert.Null(await menu.AskAsync([], CancellationToken.None));
    }

    [Fact]
    public void Statics_ArePinned()
    {
        Assert.Equal(12, QuestionMenu.TabTitleCells);

        Assert.Equal("Q1", QuestionMenu.TabTitle(1, null));
        Assert.Equal("Q3", QuestionMenu.TabTitle(3, "  "));
        Assert.Equal("Colour", QuestionMenu.TabTitle(1, " Colour "));
        Assert.Equal("A very long…", QuestionMenu.TabTitle(1, "A very long title indeed"));
        Assert.Equal("Q2", QuestionMenu.SummaryLabel(2, null));
        Assert.Equal("Q2 Colour", QuestionMenu.SummaryLabel(2, "Colour"));
        Assert.Equal("Q2 Colour has no answer yet.", QuestionMenu.UnansweredError("Q2 Colour"));
        Assert.Equal("Q1 — " + Theme.DimMarkup("(no answer)"), QuestionMenu.SummaryRow("Q1", null));
        Assert.Equal("Q1 — a, b", QuestionMenu.SummaryRow("Q1", "a, b"));
        Assert.Equal("(x) a[[b]]", QuestionMenu.OptionRow(false, true, "a[b]"));
        Assert.Equal("( ) a", QuestionMenu.OptionRow(false, false, "a"));
        Assert.Equal("[[x]] a", QuestionMenu.OptionRow(true, true, "a"));
        Assert.Equal("[[ ]] a", QuestionMenu.OptionRow(true, false, "a"));
        Assert.Equal("( ) Other…", QuestionMenu.OtherRowMarkup(false, ""));
        Assert.Equal("[[x]] Other: t[[1]]", QuestionMenu.OtherRowMarkup(true, "t[1]"));
    }
}
