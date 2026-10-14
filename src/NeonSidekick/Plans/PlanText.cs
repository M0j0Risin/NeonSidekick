using System.Globalization;

namespace NeonSidekick.Plans;

/// <summary>
/// Plan mode's words (2026-09-26, the user's ask: <c>/plan &lt;requirement&gt;</c>, a plan agreed
/// before anything is changed): the notices and errors of <c>/plan</c>, the directive the system
/// prompt carries while planning, what <c>present_plan</c> answers for each verdict, the approval
/// pane's rows and the message that starts the approved plan. Pinned by tests where they are a
/// contract (the directive, the tool results, the execute message).
/// </summary>
public static class PlanText
{
    /// <summary>The <c>/plan</c> word, what a double-click on the strip's glyph sends.</summary>
    public const string Word = "/plan";

    /// <summary>The status strip's glyph while planning.</summary>
    public const string Glyph = "📝";

    /// <summary>The subcommands, recognised only while planning and only as the whole argument (<c>approve</c> may take <see cref="FreshSwitch"/>).</summary>
    public const string ShowWord = "show";
    public const string ApproveWord = "approve";
    public const string CancelWord = "cancel";

    /// <summary><c>/plan open [name]</c> and <c>/plan save [name]</c> (2026-09-26, round two): a word and what follows it.</summary>
    public const string OpenWord = "open";
    public const string SaveWord = "save";

    /// <summary><c>/plan approve --fresh</c>: the conversation cleared before the plan is carried out, the plan's text sent with the message.</summary>
    public const string FreshSwitch = "--fresh";

    /// <summary>The input row's placeholder while planning.</summary>
    public const string Placeholder = "Planning — add detail, or /plan approve · /plan cancel";

    public const string UsageError = "Usage: /plan <what you want done> — the model plans it with you and nothing is changed until you approve";
    public const string NeedsToolsError = "Plan mode needs the model's tools: switch LLM offer tools on (/settings) and try again";
    public const string NothingPresentedError = "No plan has been presented yet: ask for it (\"present the plan\") and approve it on the pane, or /plan cancel";
    public static string PlanUnreadableError(string path) => $"The plan file {path} could not be read; plan mode stays on — present it again, or /plan cancel";
    public static string NoSuchPlanError(string name) => $"No plan named '{name}' under {PlanSlug.Folder}/ — /plan open lists them";
    public const string NoPlansNotice = Glyph + " No plans yet under " + PlanSlug.Folder + "/.";
    public const string NothingToSaveError = "No reply to save as the plan: ask the model for the plan first";
    public const string NotPlanningError ="Plan mode is not on: /plan <what you want done> starts it";

    /// <summary><c>/plan gaps [model|advisor|any]</c> (2026-10-13): the gap-closing loop started by typing, the reviewer its one word.</summary>
    public const string GapsWord = "gaps";

    /// <summary>The reviewer words <c>/plan gaps</c> takes, in <see cref="PlanReviewer"/>'s order.</summary>
    public static readonly IReadOnlyList<string> ReviewerWords = ["model", "advisor", "any"];

    /// <summary>The word for <paramref name="reviewer"/>, as <c>/plan gaps</c> takes it.</summary>
    public static string ReviewerWord(PlanReviewer reviewer) => ReviewerWords[(int)reviewer];

    /// <summary>The reviewer <paramref name="word"/> names, case aside; null for any other word.</summary>
    public static PlanReviewer? ParseReviewer(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        for (int i = 0; i < ReviewerWords.Count; i++)
        {
            if (string.Equals(ReviewerWords[i], word.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return (PlanReviewer)i;
            }
        }

        return null;
    }

    public static string UnknownReviewerError(string word) => $"No reviewer named '{word}': /plan gaps takes model, advisor or any";

    public static string EnteredNotice => $"{Glyph} Plan mode: the model can read and research but not change anything until you approve the plan. Add details as messages; /plan cancel leaves.";

    public static string CancelledNotice(string? path) => path is null
        ? $"{Glyph} Plan mode off; nothing was saved."
        : $"{Glyph} Plan mode off; {path} is kept, marked cancelled.";

    public static string LeftOnResetNotice(string? path) => path is null
        ? $"{Glyph} Plan mode off with the conversation."
        : $"{Glyph} Plan mode off with the conversation; {path} is kept as a draft.";

    public static string SavedNotice(string path, int revision) =>
        revision <= 1 ? $"{Glyph} Plan saved to {path}" : $"{Glyph} Plan revision {N(revision)} saved to {path}";

    public static string ApprovedNotice(string path, bool fresh) =>
        fresh ? $"{Glyph} Plan approved: {path}. Starting it in a new conversation." : $"{Glyph} Plan approved: {path}. Starting it.";

    /// <summary>The plan in progress left as it stands when <c>/plan open</c> picks up another.</summary>
    public static string SwitchedNotice(string? path) => path is null
        ? $"{Glyph} The plan in progress was never presented, so there is no draft to keep."
        : $"{Glyph} {path} is left as a draft.";

    /// <summary>What <c>/plan open</c> says before its turn (2026-09-26): the file, what it was, and how far it got.</summary>
    public static string OpenedNotice(string path, PlanStatus? was, int done, int total) =>
        $"{Glyph} Plan mode on {path}" + (was is { } status ? $" (was {PlanDocument.Word(status)}" + (total > 0 ? $", {N(done)} of {N(total)} steps done)" : ")") : total > 0 ? $" ({N(done)} of {N(total)} steps done)" : "") + ".";

    /// <summary>One row of the <c>/plan open</c> list: the name, the status, the steps ticked and the last update.</summary>
    public static string ListLine(PlanListing plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string status = plan.Status is { } s ? PlanDocument.Word(s) : "no header";
        string steps = plan.Total > 0 ? $" · {N(plan.Done)}/{N(plan.Total)}" : "";
        string when = plan.Updated is { } u && u != DateTimeOffset.MinValue ? " · " + u.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
        return $"  {plan.Name} — {plan.Title} ({status}{steps}{when})";
    }

    /// <summary>The note beside a plan on the <c>/plan open</c> completion list.</summary>
    public static string CompletionNote(PlanListing plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string status = plan.Status is { } s ? PlanDocument.Word(s) : "no header";
        return plan.Total > 0 ? $"{status} · {N(plan.Done)}/{N(plan.Total)} · {plan.Title}" : $"{status} · {plan.Title}";
    }

    public static string DoneNotice(string path) => $"{Glyph} Every step of {path} is ticked: marked done.";

    public static string IncompleteNotice(string path, int done, int total) =>
        $"{Glyph} {path}: {N(done)} of {N(total)} steps done, marked incomplete — /plan open {System.IO.Path.GetFileNameWithoutExtension(path)} picks it up.";

    /// <summary>The hint after a planning reply that reads as a plan the model never presented (2026-09-26).</summary>
    public const string UnpresentedHint = Glyph + " That reply looks like a plan but was not presented: /plan save [name] keeps it as the plan, or ask for it to be presented.";

    public const string ApprovalInterruptedNotice = Glyph + " The plan was approved but the reply was stopped: /plan approve starts it.";

    // ── The gap-closing loop (2026-10-13) ───────────────────────────────────

    /// <summary>The message each pass sends: one line, since the rules ride in the directive (<see cref="GapReviewLines"/>). Pinned.</summary>
    public static string GapPassMessage(string path, int pass, int limit, PlanReviewer reviewer) =>
        $"Gap review pass {PassOf(pass, limit)} for the plan in {path} (reviewer: {ReviewerWord(reviewer)}): find and close its gaps, then call present_plan.";

    /// <summary>A pass that saved a revision with gaps closed: the count, then each gap (at most <see cref="PassNoticeGaps"/>, then how many more).</summary>
    public static IReadOnlyList<string> PassNoticeLines(int pass, int limit, int revision, IReadOnlyList<string> gaps)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        var lines = new List<string>(Math.Min(gaps.Count, PassNoticeGaps) + 2)
        {
            $"{Glyph} Gap review pass {PassOf(pass, limit)}: revision {N(revision)} saved, {Gaps(gaps.Count)} closed.",
        };
        lines.AddRange(gaps.Take(PassNoticeGaps).Select(gap => "  · " + Flatten(gap)));
        if (gaps.Count > PassNoticeGaps)
        {
            lines.Add($"  … and {N(gaps.Count - PassNoticeGaps)} more");
        }

        return lines;
    }

    /// <summary>The most gaps a pass's notice lists; the --log line has them all.</summary>
    public const int PassNoticeGaps = 10;

    /// <summary>The approval pane's note once a pass found no gaps.</summary>
    public static string ConvergedNote(int passes, int closed) =>
        $"no gaps left after {Passes(passes)}, {Gaps(closed)} closed";

    public static string ConvergedNotice(string path, int passes, int closed) =>
        $"{Glyph} Gap review done: no gaps left in {path} after {Passes(passes)}, {Gaps(closed)} closed.";

    /// <summary>The approval pane's note when the loop stopped short at its limit or on a stall, the last pass's gaps counted.</summary>
    public static string StoppedNote(PlanGapStop reason, int passes, int left) => reason == PlanGapStop.Limit
        ? $"gap review stopped at its limit after {Passes(passes)}; the last pass still closed {Gaps(left)}"
        : $"gap review stalled after {Passes(passes)}: the plan stopped changing or the same gaps came back";

    /// <summary>Why the loop stopped, said once it has (a convergence has <see cref="ConvergedNotice"/>).</summary>
    public static string GapStopNotice(PlanGapStop reason, int passes, int closed) => reason switch
    {
        PlanGapStop.Limit => $"{Glyph} Gap review stopped at its limit after {Passes(passes)}, {Gaps(closed)} closed; the last pass still found some.",
        PlanGapStop.Stalled => $"{Glyph} Gap review stopped after {Passes(passes)}, {Gaps(closed)} closed: the plan stopped changing or the same gaps came back.",
        PlanGapStop.NoRevision => $"{Glyph} Gap review stopped: pass {N(passes)} saved no revision. /plan gaps starts a new review; /plan approve starts the plan.",
        PlanGapStop.UserSpoke => $"{Glyph} Gap review stopped after {Passes(passes)} for your message. /plan gaps starts a new review.",
        _ => $"{Glyph} Gap review stopped at pass {N(passes)}, {Gaps(closed)} closed. /plan gaps starts a new review; /plan approve starts the plan.",
    };

    public const string GapsDroppedNotice = Glyph + " The gap review was chosen but the reply was stopped: /plan gaps starts it.";

    /// <summary>Close gaps… chosen inside a reply while a message of the user's was queued (2026-10-14): the message goes first.</summary>
    public const string GapsYieldedNotice = Glyph + " The gap review was chosen but your message goes first: /plan gaps starts it.";

    /// <summary>Headless (2026-10-13): no confirm to ask on, so every advisor call would be refused.</summary>
    public const string GapsConfirmFallbackNotice = Glyph + " Advisor confirm is on and headless cannot ask, so the model reviews the plan alone.";

    /// <summary>Headless (2026-10-13): no ESC to stop a loop with no limit.</summary>
    public static string GapsHeadlessCapNotice(int cap) => $"{Glyph} Plan gap passes is 0 (no limit), but headless has no ESC: at most {N(cap)} passes.";

    /// <summary>The --log line of a pass: its gaps whole, and whether it asked an advisor.</summary>
    public static string PassLogLine(int pass, int revision, IReadOnlyList<string> gaps, bool advisorAsked)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        return $"plan gaps: pass {N(pass)}, revision {N(revision)}, {Gaps(gaps.Count)} closed" + (advisorAsked ? ", an advisor asked" : "") + (gaps.Count > 0 ? ": " + string.Join(" | ", gaps.Select(Flatten)) : "");
    }

    private static string PassOf(int pass, int limit) => limit > 0 ? $"{N(pass)} of {N(limit)}" : N(pass);

    private static string Passes(int passes) => passes == 1 ? "1 pass" : $"{N(passes)} passes";

    private static string Gaps(int gaps) => gaps == 1 ? "1 gap" : $"{N(gaps)} gaps";

    public static IReadOnlyList<string> ShowLines(PlanSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var lines = new List<string>(3) { $"{Glyph} Planning: {session.Requirement}" };
        lines.Add(session.Path is { } path
            ? $"  Plan: {path} (revision {N(session.Revision)}) — /plan approve [--fresh] starts it"
            : "  Plan: not presented yet");
        if (session.Closing is { Pending: false } loop)
        {
            lines.Add($"  Closing gaps: pass {PassOf(loop.Pass, loop.Limit)} ({ReviewerWord(loop.Reviewer)}), {Gaps(loop.Closed)} closed so far");
        }

        return lines;
    }

    // ── The approval pane ───────────────────────────────────────────────────

    public const string ApprovalTitle = "Approve the plan?";
    public const string ApproveRow = "Approve & run";
    public const string ApproveFreshRow = "Approve, clear context & run";
    public const string RefineRow = "Keep refining…";
    public const string CancelRow = "Cancel plan";

    /// <summary>The gap-closing loop's row (2026-10-13), after Keep refining.</summary>
    public const string CloseGapsRow = "Close gaps…";
    public const string ApprovalKeys = "a / f / r / g / c = pick · Enter = choose · ESC = keep refining";

    /// <summary>The second page Close gaps… opens while advisors are offered: who reviews.</summary>
    public const string ReviewerTitle = "Close gaps: who reviews?";
    public static readonly IReadOnlyList<string> ReviewerRows = ["Close gaps (model)", "Close gaps (advisor)", "Close gaps (any)"];
    public const string ReviewerKeys = "m / a / y = pick · Enter = choose · ESC = back";
    public const string FeedbackKeys = "Enter = send · ESC = back";
    public const string FeedbackPrompt = "What should change? Type it below (empty for nothing in particular).";

    public static string FeedbackNotice(string feedback) => $"{Glyph} Sent back with: {feedback}";

    public static string Caption(PlanPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        return $"{presentation.Title} — {presentation.Path} (revision {N(presentation.Revision)})" + (presentation.Note is { Length: > 0 } note ? " · " + note : "");
    }

    // ── The model's side ────────────────────────────────────────────────────

    /// <summary>
    /// The directive the system prompt carries while planning, after the skills and before the voice
    /// directive, under a custom <c>operata.md</c> too: the requirement itself (so a compaction cannot
    /// lose it), what planning allows, the shape of the plan and how it is presented. Pinned.
    /// </summary>
    public static string Directive(string requirement, string? path, int revision) => Directive(requirement, path, revision, null);

    /// <summary>
    /// <see cref="Directive(string, string?, int)"/> with <paramref name="advisors"/> (2026-10-10, the user's ask): while the turn offers
    /// <c>neon_advisor</c>, <see cref="AdvisorsLine"/> follows the <c>present_plan</c> bullets, before the state line; null or none, the
    /// directive as ever. Pinned.
    /// </summary>
    public static string Directive(string requirement, string? path, int revision, IReadOnlyList<Settings.AdvisorEntry>? advisors) =>
        Directive(requirement, path, revision, advisors, null);

    /// <summary>
    /// <see cref="Directive(string, string?, int, IReadOnlyList{Settings.AdvisorEntry}?)"/> with a gap-closing pass under way (2026-10-13):
    /// <see cref="GapReviewLines"/> before the state line, and no advisors bullet when the model reviews alone. Pinned.
    /// </summary>
    public static string Directive(string requirement, string? path, int revision, IReadOnlyList<Settings.AdvisorEntry>? advisors, PlanGapState? gap)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        bool offered = advisors is { Count: > 0 };
        string state = path is null
            ? "No plan has been presented yet."
            : $"The plan so far is in {path} (revision {N(revision)}); present_plan overwrites it with the next revision.";
        return
            "PLAN MODE is on. The user asked for a plan before anything is done. Their requirement: \"" + Flatten(requirement) + "\"\n" +
            "- Do not change anything: write no file, change no repository, run no command that changes state, send no message and change no setting. Only read-only tools are offered; use them to research what the plan needs.\n" +
            "- Ask what you need to know, a few questions at a time (with ask_user when it is offered), and fold the user's answers and later messages into the plan.\n" +
            "- Never claim anything was done: nothing is carried out until the user approves.\n" +
            "- When no open question is left, call present_plan with the whole plan as Markdown: a # title, then ## Goal, ## Context, ## Steps (as - [ ] checkboxes, in order), ## Files and areas, ## Risks, ## Verification.\n" +
            "- Never write the plan only in your reply: call present_plan with it.\n" +
            "- present_plan shows the plan to the user for approval; follow its result.\n" +
            (offered && gap is not { Reviewer: PlanReviewer.Model } ? AdvisorsLine(advisors!) + "\n" : "") +
            (gap is { } review ? GapReviewLines(review, offered) + "\n" : "") +
            state;
    }

    /// <summary>
    /// The gap-closing pass's rules (2026-10-13, the user's ask): what a gap is and is not, how to research and ask, and how to
    /// report the pass through <c>present_plan</c>'s <c>gaps</c>; then the reviewer's sentence, which falls back to the model
    /// alone when no advisor is offered (Any adds none: the advisors bullet above already says to consult only where a role fits). Pinned.
    /// </summary>
    public static string GapReviewLines(PlanGapState gap, bool advisorsOffered)
    {
        ArgumentNullException.ThrowIfNull(gap);
        string reviewer = gap.Reviewer switch
        {
            PlanReviewer.Model => "- Review it yourself; do not consult advisors on this pass.",
            _ when !advisorsOffered => "- No advisors are offered: review it yourself.",
            PlanReviewer.Advisor => "- Ask at least one advisor with " + NeonSidekick.Llm.Tools.NeonAdvisorTool.ToolName + ", the one whose role fits best, to find the gaps: put the plan's text in the question, then close what they name.",
            _ => "",
        };
        return
            "- A gap review is running (pass " + PassOf(gap.Pass, gap.Limit) + "): present_plan's gaps lists what each revision closed; only a pass with no gaps returns the plan to the user.\n" +
            "- Read the plan again from its file. A gap is anything that would make whoever carries the plan out guess, fail or stop to ask: a missing or out-of-order step, a choice left open, a file, API or setting assumed but not read, a risk with no answer, no way to verify. Wording and style are not gaps.\n" +
            "- Research what you need with the read-only tools. A decision only the user can make, ask them (with ask_user when it is offered).\n" +
            "- Then call present_plan with the whole revised plan and gaps, one short line for each gap this pass closed. If you find none, call present_plan with the plan as it stands and gaps empty." +
            (reviewer.Length > 0 ? "\n" + reviewer : "");
    }

    /// <summary>
    /// Plan mode's advisors bullet (2026-10-10, the user's ask: consult the advisors whose role matches the subject; their call, the
    /// plan's Context says whose advice it took): the advisors by name and role, consulted before the plan is presented, only where
    /// a role fits. Pinned.
    /// </summary>
    public static string AdvisorsLine(IReadOnlyList<Settings.AdvisorEntry> advisors)
    {
        ArgumentNullException.ThrowIfNull(advisors);
        return "- You have advisors with " + NeonSidekick.Llm.Tools.NeonAdvisorTool.ToolName + ": " + string.Join("; ", advisors.Select(Advisors.AdvisorRoster.Label)) + ". " +
            "Before presenting the plan, consult each one whose role covers part of it, with one self-contained question about that part, and weigh the answer. " +
            "Consult only those whose role fits; skip the rest. In ## Context, say which advisors you consulted and what advice you followed or set aside.";
    }

    public const string ToolDescription =
        "Saves the plan as a Markdown file under .neon/plans/ in the working directory and asks the user to approve it. " +
        "Call it only in plan mode, with the whole plan each time (it replaces the previous revision). " +
        "The result says whether the user approved, wants changes (with their feedback), or cancelled.";

    public const string NotPlanningResult = "Error: plan mode is not on; there is no plan to present";
    public const string MarkdownRequired = "Error: markdown is required: the whole plan as Markdown";
    public const string TitleRequired = "Error: title is required: a short title for the plan";
    public const string AlreadyApprovedResult = "The plan is already approved; it starts when this reply ends. Reply with one short line and call no more tools.";

    public static string ApprovedResult(string path) =>
        $"The user approved the plan in {path}. Plan mode ends with this reply and the plan is carried out in the next turn, with every tool. Reply with one short line and call no more tools.";

    public static string RefineResult(string path, string? feedback) => string.IsNullOrWhiteSpace(feedback)
        ? $"The plan is saved to {path} but the user did not approve it yet. Ask what they would like changed, or wait for their next message."
        : $"The plan is saved to {path} but the user wants changes: \"{Flatten(feedback)}\". Revise the plan and call present_plan again.";

    public static string SavedResult(string path) =>
        $"The plan is saved to {path}. The user reads it and approves it with /plan approve, or sends changes. Summarise the plan in two or three lines and stop.";

    public static string CancelledResult(string path) =>
        $"The user cancelled the plan ({path} is kept, marked cancelled). Plan mode is off. Acknowledge in one short line and do nothing else.";

    public static string CouldNotSaveResult(string detail) => $"Error: the plan could not be saved: {detail}";

    /// <summary>A gap-closing pass's revision saved (2026-10-13): the pane is not shown, the next pass starts by itself. Pinned.</summary>
    public static string GapPassResult(string path, int revision, int gaps) =>
        $"Revision {N(revision)} of {path} is saved with {Gaps(gaps)} closed. Reply with one short line and call no more tools; the next review pass starts by itself.";

    /// <summary>The user chose Close gaps… on the pane (2026-10-13). Pinned.</summary>
    public static string CloseGapsResult(string path) =>
        $"The user wants the plan's gaps closed before approving it ({path} is kept). Reply with one short line and call no more tools; the first review pass starts by itself.";

    public static string GapsInvalidResult(string raw) => $"Error: gaps must be a list of strings, one short line for each gap closed; got {raw}";

    /// <summary>The message that starts an approved plan in the same conversation. Pinned.</summary>
    public static string ExecuteMessage(string path) =>
        $"The plan in {path} is approved. Carry out its steps that are not ticked yet, in order, ticking each checkbox in {path} as you finish the step, and tell me when it is done or if something blocks you.";

    /// <summary>The turn <c>/plan open</c> sends (2026-09-26, the user's call: the model reads the plan and asks). Pinned.</summary>
    public static string OpenMessage(string path, int done, int total) =>
        $"I've reopened the plan in {path}" + (total > 0 ? $" ({N(done)} of {N(total)} steps done)" : "") + ". Read it, then ask me what should change, or tell me it is ready to approve.";

    /// <summary>The turn <c>/plan save</c> sends when the pane asked for changes (2026-09-26). Pinned.</summary>
    public static string SavedFeedbackMessage(string path, string feedback) =>
        $"I saved your plan to {path}. Please change: \"{Flatten(feedback)}\" — then call present_plan with the revised plan.";

    /// <summary>The turn a gap review that stopped short sends when its pane asked for changes (2026-10-13). Pinned.</summary>
    public static string RevisionFeedbackMessage(string path, string feedback) =>
        $"Please change the plan in {path}: \"{Flatten(feedback)}\" — then call present_plan with the revised plan.";

    /// <summary>The message that starts an approved plan in a fresh conversation: the plan's text inline, since nothing of the planning is left. Pinned.</summary>
    public static string ExecuteFreshMessage(string path, string plan) =>
        ExecuteMessage(path) + "\n\n" + plan.Trim();

    private static string Flatten(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Replace('"', '\'');

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
