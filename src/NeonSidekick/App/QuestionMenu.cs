using System.Globalization;
using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The <c>ask_user</c> pane (2026-09-15): the model's questions as tabs on the bottom pane — one
/// tab per question, the question text as the page's caption over its options, a <c>Submit</c>
/// tab last, so always two tabs at least — and the answers back when Submit is chosen, or null
/// when ESC closed it. A single-choice question is a picker: Enter marks the row and moves to the
/// next tab (Submit after the last question), Space marks it and stays; a multi-choice one is a
/// checkbox list: Enter and Space both toggle the row and stay, ←/→ move on. Every question ends
/// with an <see cref="OtherRow"/> that opens the pane's input slot (<see cref="MenuPane.EditAsync"/>)
/// for a typed answer — on a single it replaces the mark, on a multi it stands beside the checked
/// ones. The Submit tab lists every question with its answer (Enter on one goes back to it) and the
/// <see cref="SubmitRow"/>; Submit with a question unanswered says so on the status line and
/// submits nothing. The question is shown whole (2026-10-10, the user's ask: a long one was cut by the ellipsis), its
/// caption growing past three rows as the pane's room allows (<see cref="MenuPage.CaptionGrows"/>).
/// The pane's mechanics are <see cref="MenuPane"/>'s (<see cref="MenuPage.Caption"/>,
/// <see cref="MenuPage.SpaceToggles"/>, <see cref="MenuPage.TabCursors"/>); this class keeps the
/// answers and rebuilds the tabs' rows on every showing. Without the pane there is nothing to draw
/// and <see cref="AskAsync"/> answers null (the tool is not offered then).
/// </summary>
internal sealed class QuestionMenu
{
    // The strip's label and the tabs' hints. Pinned.
    public const string Title = "Questions";
    public const string SingleKeys = "Enter = choose · Space = mark · ←/→ tabs · Esc = cancel";
    public const string MultiKeys = "Space / Enter = toggle · ←/→ tabs · Esc = cancel";
    public const string SubmitKeys = "Enter = submit or revisit · ←/→ tabs · Esc = cancel";
    public const string OtherKeys = "Enter = save · Esc = back";

    // The marks ahead of an option, ASCII on purpose (no East-Asian-ambiguous glyph on the row). Pinned.
    public const string SingleOn = "(x) ";
    public const string SingleOff = "( ) ";
    public const string MultiOn = "[x] ";
    public const string MultiOff = "[ ] ";

    public const string OtherRow = "Other…";
    public const string OtherPrefix = "Other: ";
    public const string SubmitRow = "Submit";
    public const string SubmitTitle = "Submit";
    public const string SubmitCaption = "Check your answers, then choose Submit.";
    public const string NoAnswer = "(no answer)";

    /// <summary>The most cells a question's title takes on the strip; a longer one ends in an ellipsis.</summary>
    public const int TabTitleCells = 12;

    private readonly MenuPane _pane;
    private readonly InputLine _input;

    public QuestionMenu(MenuPane pane, InputLine input)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    // ── Pinned statics ──────────────────────────────────────────────────────

    /// <summary>A question's tab title: its own, cut to <see cref="TabTitleCells"/>, else <c>Q1</c>, <c>Q2</c> …</summary>
    public static string TabTitle(int number, string? title) =>
        string.IsNullOrWhiteSpace(title) ? "Q" + number.ToString(CultureInfo.InvariantCulture) : ScreenPane.Fit(title.Trim(), TabTitleCells);

    /// <summary>How the Submit tab and the unanswered error name a question: <c>Q1 Colour</c>, or <c>Q1</c> with no title.</summary>
    public static string SummaryLabel(int number, string? title)
    {
        string n = "Q" + number.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(title) ? n : n + " " + ScreenPane.Fit(title.Trim(), TabTitleCells);
    }

    /// <summary>A Submit-tab row as markup: the label, then the answer (or <see cref="NoAnswer"/>, dim). Escaped.</summary>
    public static string SummaryRow(string label, string? answer) =>
        Markup.Escape(label) + " — " + (answer is null ? Theme.DimMarkup(NoAnswer) : Markup.Escape(answer));

    /// <summary>The status line for Submit over an unanswered question. Pinned.</summary>
    public static string UnansweredError(string label) => $"{label} has no answer yet.";

    /// <summary>An option's row as markup: the mark for its kind and state, then the option. Escaped (the multi marks are brackets).</summary>
    public static string OptionRow(bool multi, bool marked, string option) =>
        Markup.Escape(Mark(multi, marked) + option);

    /// <summary>The Other row as markup: marked while a text is typed, the text after <see cref="OtherPrefix"/>; else <see cref="OtherRow"/>. Escaped.</summary>
    public static string OtherRowMarkup(bool multi, string other) =>
        Markup.Escape(Mark(multi, other.Length > 0) + (other.Length > 0 ? OtherPrefix + other : OtherRow));

    private static string Mark(bool multi, bool on) => multi ? (on ? MultiOn : MultiOff) : (on ? SingleOn : SingleOff);

    // ── The visit ───────────────────────────────────────────────────────────

    /// <summary>The state of one question while the pane is open.</summary>
    private sealed class Draft(AskQuestion question)
    {
        public AskQuestion Question { get; } = question;

        /// <summary>The marked option of a single-choice question; null for none or a typed answer.</summary>
        public int? Chosen { get; set; }

        /// <summary>The checked options of a multi-choice question.</summary>
        public bool[] Checked { get; } = new bool[question.Options.Count];

        /// <summary>The typed answer; empty for none.</summary>
        public string Other { get; set; } = "";

        public bool Answered => Other.Length > 0 || (Question.Multi ? Array.IndexOf(Checked, true) >= 0 : Chosen is not null);

        /// <summary>The choices in option order, the typed answer last; empty when unanswered.</summary>
        public IReadOnlyList<string> Choices()
        {
            var choices = new List<string>();
            if (Question.Multi)
            {
                for (int i = 0; i < Checked.Length; i++)
                {
                    if (Checked[i])
                    {
                        choices.Add(Question.Options[i]);
                    }
                }
            }
            else if (Chosen is { } chosen)
            {
                choices.Add(Question.Options[chosen]);
            }

            if (Other.Length > 0)
            {
                choices.Add(Other);
            }

            return choices;
        }
    }

    /// <summary>
    /// Shows <paramref name="questions"/> and reads until Submit (the answers, one per question in
    /// the order asked) or ESC / the token / no keyboard (null). Null at once without the pane.
    /// </summary>
    public async Task<IReadOnlyList<AskAnswer>?> AskAsync(IReadOnlyList<AskQuestion> questions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(questions);
        if (!_pane.Enabled || questions.Count == 0)
        {
            return null;
        }

        var drafts = questions.Select(q => new Draft(q)).ToArray();
        int n = drafts.Length;
        // The cursor each tab lands on: a question's on its first row, Submit's on its Submit row.
        var cursors = new int[n + 1];
        cursors[n] = n;
        int tab = 0;
        try
        {
            while (true)
            {
                var page = Page(drafts, tab, cursors);
                var pick = await _pane.PickAsync(page, cursors[tab], cancellationToken).ConfigureAwait(false);
                if (pick is not { } picked)
                {
                    return null;
                }

                tab = picked.Tab;
                cursors[tab] = picked.Row;
                if (tab < n)
                {
                    var draft = drafts[tab];
                    int row = picked.Row;
                    if (row < draft.Question.Options.Count)
                    {
                        if (draft.Question.Multi)
                        {
                            draft.Checked[row] = !draft.Checked[row];
                        }
                        else
                        {
                            draft.Chosen = row;
                            draft.Other = "";
                            if (!picked.Toggle)
                            {
                                tab++;
                            }
                        }
                    }
                    else
                    {
                        // The Other row: the slot under the list, pre-filled with what was typed before. The
                        // page is rebuilt for the tab picked on: a ←/→ inside the pane left `page` on another.
                        var result = await _pane.EditAsync(Page(drafts, tab, cursors) with { Hint = OtherKeys }, row, _input, draft.Other, allowEmpty: true, cancellationToken).ConfigureAwait(false);
                        if (result is InputResult.Submitted submitted)
                        {
                            draft.Other = submitted.Text.ReplaceLineEndings(" ").Trim();
                            if (!draft.Question.Multi && draft.Other.Length > 0)
                            {
                                draft.Chosen = null;
                                if (!picked.Toggle)
                                {
                                    tab++;
                                }
                            }
                        }
                    }
                }
                else if (picked.Row < n)
                {
                    // A summary row: back to that question, on the row it was left at; Submit lands on its own row again.
                    tab = picked.Row;
                    cursors[n] = n;
                }
                else
                {
                    int unanswered = Array.FindIndex(drafts, d => !d.Answered);
                    if (unanswered >= 0)
                    {
                        _pane.Error(UnansweredError(SummaryLabel(unanswered + 1, drafts[unanswered].Question.Title)));
                        continue;
                    }

                    return drafts.Select(d => new AskAnswer(d.Question, d.Choices())).ToArray();
                }
            }
        }
        finally
        {
            _pane.Close();
        }
    }

    /// <summary>The page for the drafts as they stand: every tab's rows rebuilt, the strip on <paramref name="tab"/>.</summary>
    private static MenuPage Page(Draft[] drafts, int tab, int[] cursors)
    {
        var tabs = new List<MenuTab>(drafts.Length + 1);
        for (int i = 0; i < drafts.Length; i++)
        {
            var draft = drafts[i];
            var q = draft.Question;
            var rows = new List<string>(q.Options.Count + 1);
            for (int j = 0; j < q.Options.Count; j++)
            {
                rows.Add(OptionRow(q.Multi, q.Multi ? draft.Checked[j] : draft.Chosen == j, q.Options[j]));
            }

            rows.Add(OtherRowMarkup(q.Multi, draft.Other));
            tabs.Add(new MenuTab(TabTitle(i + 1, q.Title), rows) { Caption = q.Question, Hint = q.Multi ? MultiKeys : SingleKeys });
        }

        var summary = new List<string>(drafts.Length + 1);
        for (int i = 0; i < drafts.Length; i++)
        {
            var choices = drafts[i].Choices();
            summary.Add(SummaryRow(SummaryLabel(i + 1, drafts[i].Question.Title), choices.Count == 0 ? null : AskUserText.AnswerText(choices)));
        }

        summary.Add(SubmitRow);
        tabs.Add(new MenuTab(SubmitTitle, summary) { Caption = SubmitCaption, Hint = SubmitKeys });
        return MenuPage.Tabbed(Title, tabs, tab, SingleKeys) with { SpaceToggles = true, TabCursors = cursors, CaptionGrows = true };
    }
}
