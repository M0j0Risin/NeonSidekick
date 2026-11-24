using System.Globalization;
using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The <c>ask_user</c> pane (2026-09-15): the model's questions as tabs on the bottom pane — one
/// tab per question, the question text as the page's caption over its options, a <c>Submit</c>
/// tab last — and the reply back (<see cref="AskReply"/>) when Submit is chosen or the user declines.
/// A single-choice question is a picker: Enter marks the row and moves to the next tab (Submit after
/// the last question), Space marks it and stays, Space on the marked row clears it (2026-11-24); a
/// multi-choice one is a checkbox list: Enter and Space both toggle the row and stay, ←/→ move on; a
/// text question (2026-11-24) is one row that opens the pane's input slot. Every choice question ends
/// with an <see cref="OtherRow"/> that opens the slot (<see cref="MenuPane.EditAsync"/>) for a typed
/// answer — on a single it replaces the mark, on a multi it stands beside the checked ones; one that
/// is an option's label becomes that option (<see cref="AskAnswer.MatchOption"/>). The Submit tab
/// lists every question with its answer as the model will read it (Enter on one goes back to it), the
/// <see cref="SubmitRow"/> and the <see cref="DeclineRow"/>; Submit sends unanswered questions as
/// skipped (2026-11-24, the user's call). One single-choice or text question alone has no Submit tab
/// (2026-11-24, B): choosing answers it, and its tab ends with the decline row. Digits 1–9 jump to a
/// row; an option's description, or its whole label when the row cuts it, is the footer band's. The
/// question is shown whole (2026-10-10, the user's ask), its caption growing as the pane's room
/// allows (<see cref="MenuPage.CaptionGrows"/>). The pane's mechanics are <see cref="MenuPane"/>'s;
/// this class keeps the answers and rebuilds the tabs' rows on every showing. Without the pane there
/// is nothing to draw and <see cref="AskAsync"/> answers null (the tool is not offered then).
/// </summary>
internal sealed class QuestionMenu
{
    // The strip's label and the tabs' hints. Each fits a 120-column window.
    public const string Title = "Questions";
    public const string SingleKeys = "Enter = choose · Space = mark/clear · 1–9 = jump · ←/→ tabs · Esc = cancel";
    public const string MultiKeys = "Space / Enter = toggle · 1–9 = jump · ←/→ tabs · Esc = cancel";
    public const string TextKeys = "Enter = type · ←/→ tabs · Esc = cancel";
    public const string SubmitKeys = "Enter = submit or revisit · 1–9 = jump · ←/→ tabs · Esc = cancel";
    public const string OtherKeys = "Enter = save · Esc = back";
    public const string DeclineKeys = "Enter = decline with this note · Esc = back";

    /// <summary>The hints of a lone question with no Submit tab (2026-11-24, B): choosing answers it.</summary>
    public const string LoneSingleKeys = "Enter = answer · Space = mark/clear · 1–9 = jump · Esc = cancel";
    public const string LoneTextKeys = "Enter = type your answer · Esc = cancel";

    // The marks ahead of an option, ASCII on purpose (no East-Asian-ambiguous glyph on the row). Pinned.
    public const string SingleOn = "(x) ";
    public const string SingleOff = "( ) ";
    public const string MultiOn = "[x] ";
    public const string MultiOff = "[ ] ";

    public const string OtherRow = "Other…";
    public const string OtherPrefix = "Other: ";
    public const string SubmitRow = "Submit";
    public const string SubmitTitle = "Submit";

    /// <summary>A text question's row before anything is typed, and after (<see cref="AnswerPrefix"/> and the text).</summary>
    public const string TextRow = "Type an answer…";
    public const string AnswerPrefix = "Answer: ";

    /// <summary>The row that declines every question with a note (2026-11-24, E): on the Submit tab, or last on a lone question's tab.</summary>
    public const string DeclineRow = "Decline with a note…";

    /// <summary>The word after a recommended option, dim (2026-11-24); the page's too.</summary>
    public const string RecommendedWord = "recommended";

    /// <summary>The most cells a question's title takes on the strip; a longer one ends in an ellipsis.</summary>
    public const int TabTitleCells = 12;

    /// <summary>The most rows a digit jumps to: 1–9.</summary>
    public const int DigitRows = 9;

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

    /// <summary>How the Submit tab names a question: <c>Q1 Colour</c>, or <c>Q1</c> with no title.</summary>
    public static string SummaryLabel(int number, string? title)
    {
        string n = "Q" + number.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(title) ? n : n + " " + ScreenPane.Fit(title.Trim(), TabTitleCells);
    }

    /// <summary>A Submit-tab row as markup: the label, then the answer as the model reads it, or <see cref="AskUserText.Skipped"/> dim for none. Escaped.</summary>
    public static string SummaryRow(string label, string? answer) =>
        Markup.Escape(label) + " — " + (answer is null ? Theme.DimMarkup(AskUserText.Skipped) : Markup.Escape(answer));

    /// <summary>
    /// The Submit tab's caption (2026-11-24, C): with every question answered the plain sentence, else how many are, and that the rest
    /// go back as skipped.
    /// </summary>
    public static string SubmitCaption(int answered, int total) =>
        answered >= total
            ? "Check your answers, then choose Submit."
            : string.Create(CultureInfo.InvariantCulture, $"{answered} of {total} answered; the rest go back as skipped. Check your answers, then choose Submit.");

    /// <summary>An option's row as markup: the mark for its kind and state, then the option, then a dim <see cref="RecommendedWord"/> tag when the model recommends it. Escaped.</summary>
    public static string OptionRow(bool multi, bool marked, string option, bool recommended = false) =>
        Markup.Escape(Mark(multi, marked) + option) + (recommended ? Theme.DimMarkup(RecommendedTag) : "");

    /// <summary>The tag as the row draws it: a space and the word in parentheses.</summary>
    public const string RecommendedTag = " (" + RecommendedWord + ")";

    /// <summary>The Other row as markup: marked while a text is typed, the text after <see cref="OtherPrefix"/>; else <see cref="OtherRow"/>. Escaped.</summary>
    public static string OtherRowMarkup(bool multi, string other) =>
        Markup.Escape(Mark(multi, other.Length > 0) + (other.Length > 0 ? OtherPrefix + other : OtherRow));

    /// <summary>A text question's row as markup: <see cref="TextRow"/>, or the answer after <see cref="AnswerPrefix"/>. Escaped.</summary>
    public static string TextRowMarkup(string answer) => Markup.Escape(answer.Length > 0 ? AnswerPrefix + answer : TextRow);

    private static string Mark(bool multi, bool on) => multi ? (on ? MultiOn : MultiOff) : (on ? SingleOn : SingleOff);

    /// <summary>The digits' rows (2026-11-24, A): <c>1</c>–<c>9</c> to rows 0–8 of whatever tab is shown.</summary>
    private static readonly IReadOnlyDictionary<char, int> Digits =
        Enumerable.Range(0, DigitRows).ToDictionary(i => (char)('1' + i), i => i);

    // ── The visit ───────────────────────────────────────────────────────────

    /// <summary>The state of one question while the pane is open.</summary>
    private sealed class Draft(AskQuestion question)
    {
        public AskQuestion Question { get; } = question;

        /// <summary>The marked option of a single-choice question; null for none or a typed answer.</summary>
        public int? Chosen { get; set; }

        /// <summary>The checked options of a multi-choice question.</summary>
        public bool[] Checked { get; } = new bool[question.Options.Count];

        /// <summary>The typed answer (a text question's whole answer); empty for none.</summary>
        public string Other { get; set; } = "";

        /// <summary>The answer as the tool reads it (<see cref="AskAnswer.From"/>).</summary>
        public AskAnswer Answer()
        {
            var picked = Question.Multi
                ? Enumerable.Range(0, Checked.Length).Where(i => Checked[i])
                : Chosen is { } chosen ? [chosen] : Enumerable.Empty<int>();
            return AskAnswer.From(Question, picked, Other);
        }

        /// <summary>
        /// Saves a typed answer: flattened and trimmed; on a single question it replaces the mark, and one that is an option's label is
        /// that option instead (marked, or checked on a multi), never a typed answer.
        /// </summary>
        public void Type(string text)
        {
            string flat = text.ReplaceLineEndings(" ").Trim();
            int match = AskAnswer.MatchOption(Question, flat);
            if (match >= 0)
            {
                if (Question.Multi)
                {
                    Checked[match] = true;
                }
                else
                {
                    Chosen = match;
                }

                Other = "";
                return;
            }

            Other = flat;
            if (!Question.Multi && flat.Length > 0)
            {
                Chosen = null;
            }
        }
    }

    /// <summary>
    /// Shows <paramref name="questions"/> and reads until Submit (the answers, one per question in the order asked, unanswered ones
    /// skipped), the user's decline (Esc, or the decline row with its note), or the token / no keyboard (null: nothing the user chose).
    /// Null at once without the pane.
    /// </summary>
    public async Task<AskReply?> AskAsync(IReadOnlyList<AskQuestion> questions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(questions);
        if (!_pane.Enabled || questions.Count == 0)
        {
            return null;
        }

        var drafts = questions.Select(q => new Draft(q)).ToArray();
        int n = drafts.Length;
        // B (2026-11-24): one single-choice or text question has no Submit tab; choosing answers it.
        bool lone = n == 1 && !drafts[0].Question.Multi;
        // The cursor each tab lands on: a question's on its first recommended option, else its first row; Submit's on its Submit row.
        var cursors = new int[lone ? 1 : n + 1];
        for (int i = 0; i < n; i++)
        {
            int recommended = drafts[i].Question.Options.ToList().FindIndex(o => o.Recommended);
            cursors[i] = Math.Max(0, recommended);
        }

        if (!lone)
        {
            cursors[n] = n;
        }

        // The footer band (descriptions, D's whole label): only when a question needs it, so a plain ask keeps its height.
        int rowCells = Math.Max(1, _pane.ListWidth(false) - TextCells.Width(MenuPane.Pointer));
        bool footer = drafts.Any(d => d.Question.Options.Any(o => o.Description is not null || Cut(o, rowCells)));
        int tab = 0;
        try
        {
            while (true)
            {
                var page = Page(drafts, tab, cursors, lone, footer ? rowCells : 0);
                var pick = await _pane.PickAsync(page, cursors[tab], cancellationToken).ConfigureAwait(false);
                if (pick is not { } picked)
                {
                    // Esc (or Ctrl+C, a double-click off) is the user's decline; a cancelled token (the turn, the wake phrase) is not.
                    return cancellationToken.IsCancellationRequested ? null : AskReply.Declined();
                }

                tab = picked.Tab;
                cursors[tab] = picked.Row;
                if (tab < n)
                {
                    var draft = drafts[tab];
                    var q = draft.Question;
                    int row = picked.Row;
                    int answerRows = q.IsText ? 1 : q.Options.Count + 1;
                    if (lone && row >= answerRows)
                    {
                        if (await DeclineAsync(Page(drafts, tab, cursors, lone, footer ? rowCells : 0), row, cancellationToken).ConfigureAwait(false) is { } declined)
                        {
                            return declined;
                        }

                        continue;
                    }

                    if (!q.IsText && row < q.Options.Count)
                    {
                        if (q.Multi)
                        {
                            draft.Checked[row] = !draft.Checked[row];
                        }
                        else if (picked.Toggle && draft.Chosen == row)
                        {
                            // Space on the marked row clears it (2026-11-24, the user's pick): a mis-pick can become a skip.
                            draft.Chosen = null;
                        }
                        else
                        {
                            draft.Chosen = row;
                            draft.Other = "";
                            if (!picked.Toggle)
                            {
                                if (lone)
                                {
                                    return Answered(drafts);
                                }

                                tab++;
                            }
                        }
                    }
                    else
                    {
                        // The Other row or a text question's row: the slot under the list, pre-filled with what was typed before. The
                        // page is rebuilt for the tab picked on: a ←/→ inside the pane left `page` on another.
                        var edit = Page(drafts, tab, cursors, lone, footer ? rowCells : 0) with { Hint = OtherKeys };
                        var result = await _pane.EditAsync(edit, row, _input, draft.Other, allowEmpty: true, cancellationToken).ConfigureAwait(false);
                        if (result is InputResult.Submitted submitted)
                        {
                            draft.Type(submitted.Text);
                            bool typed = submitted.Text.Trim().Length > 0;
                            bool answered = !draft.Answer().Skipped;
                            if (!picked.Toggle && (q.IsText || q.Multi || answered))
                            {
                                if (lone)
                                {
                                    // An empty save is no answer, and with no Submit there is no skip: the tab stays (B).
                                    if (typed && answered)
                                    {
                                        return Answered(drafts);
                                    }
                                }
                                else if (!q.Multi)
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
                else if (picked.Row == n)
                {
                    // Submit: unanswered questions go back as skipped (2026-11-24, the user's call).
                    return Answered(drafts);
                }
                else if (await DeclineAsync(Page(drafts, tab, cursors, lone, footer ? rowCells : 0), picked.Row, cancellationToken).ConfigureAwait(false) is { } declined)
                {
                    return declined;
                }
            }
        }
        finally
        {
            _pane.Close();
        }
    }

    /// <summary>The decline row (E): the slot for a note; a saved note declines with it, an empty one (or Esc) goes back to the tab (null).</summary>
    private async Task<AskReply?> DeclineAsync(MenuPage page, int row, CancellationToken cancellationToken)
    {
        var result = await _pane.EditAsync(page with { Hint = DeclineKeys }, row, _input, "", allowEmpty: true, cancellationToken).ConfigureAwait(false);
        return result is InputResult.Submitted submitted && submitted.Text.Trim().Length > 0 ? AskReply.Declined(submitted.Text) : null;
    }

    private static AskReply Answered(Draft[] drafts) => AskReply.Answered(drafts.Select(d => d.Answer()).ToArray());

    /// <summary>Whether an option's row is wider than <paramref name="cells"/>: the mark, the label and the tag.</summary>
    private static bool Cut(AskOption option, int cells) =>
        TextCells.Width(SingleOff + option.Label + (option.Recommended ? RecommendedTag : "")) > cells;

    /// <summary>
    /// The page for the drafts as they stand: every tab's rows rebuilt, the strip on <paramref name="tab"/>. With <paramref name="rowCells"/>
    /// above 0 the footer band says the cursor's option: its description, its whole label first when the row cuts it (D).
    /// </summary>
    private static MenuPage Page(Draft[] drafts, int tab, int[] cursors, bool lone, int rowCells)
    {
        var tabs = new List<MenuTab>(drafts.Length + 1);
        for (int i = 0; i < drafts.Length; i++)
        {
            var draft = drafts[i];
            var q = draft.Question;
            var rows = new List<string>(q.Options.Count + 2);
            string hint;
            if (q.IsText)
            {
                rows.Add(TextRowMarkup(draft.Other));
                hint = lone ? LoneTextKeys : TextKeys;
            }
            else
            {
                for (int j = 0; j < q.Options.Count; j++)
                {
                    var option = q.Options[j];
                    rows.Add(OptionRow(q.Multi, q.Multi ? draft.Checked[j] : draft.Chosen == j, option.Label, option.Recommended));
                }

                rows.Add(OtherRowMarkup(q.Multi, draft.Other));
                hint = q.Multi ? MultiKeys : lone ? LoneSingleKeys : SingleKeys;
            }

            if (lone)
            {
                rows.Add(Markup.Escape(DeclineRow));
            }

            tabs.Add(new MenuTab(TabTitle(i + 1, q.Title), rows) { Caption = q.Question, Hint = hint });
        }

        if (!lone)
        {
            var summary = new List<string>(drafts.Length + 2);
            int answered = 0;
            for (int i = 0; i < drafts.Length; i++)
            {
                var answer = drafts[i].Answer();
                answered += answer.Skipped ? 0 : 1;
                summary.Add(SummaryRow(SummaryLabel(i + 1, drafts[i].Question.Title), answer.Skipped ? null : AskUserText.AnswerText(answer)));
            }

            summary.Add(SubmitRow);
            summary.Add(Markup.Escape(DeclineRow));
            tabs.Add(new MenuTab(SubmitTitle, summary) { Caption = SubmitCaption(answered, drafts.Length), Hint = SubmitKeys });
        }

        var page = MenuPage.Tabbed(Title, tabs, tab, SingleKeys) with { SpaceToggles = true, TabCursors = cursors, CaptionGrows = true, Hotkeys = Digits };
        return rowCells > 0 ? page with { Footer = (t, row) => FooterText(drafts, t, row, rowCells) } : page;
    }

    /// <summary>What the band says for a row (D): an option's description, its whole label first when the row cuts it; null elsewhere.</summary>
    private static MenuFooter? FooterText(Draft[] drafts, int tab, int row, int rowCells)
    {
        if (tab < 0 || tab >= drafts.Length || row < 0 || row >= drafts[tab].Question.Options.Count)
        {
            return null;
        }

        var option = drafts[tab].Question.Options[row];
        bool cut = Cut(option, rowCells);
        string? text = cut
            ? option.Description is { } both ? option.Label + " — " + both : option.Label
            : option.Description;
        return text is null ? null : new MenuFooter(text);
    }
}
