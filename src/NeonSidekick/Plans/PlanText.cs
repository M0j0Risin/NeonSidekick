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

    /// <summary><c>/plan approve --fresh</c>: the conversation cleared before the plan is carried out, the plan's text sent with the message.</summary>
    public const string FreshSwitch = "--fresh";

    /// <summary>The input row's placeholder while planning.</summary>
    public const string Placeholder = "Planning — add detail, or /plan approve · /plan cancel";

    public const string UsageError = "Usage: /plan <what you want done> — the model plans it with you and nothing is changed until you approve";
    public const string NeedsToolsError = "Plan mode needs the model's tools: switch LLM offer tools on (/settings) and try again";
    public const string NothingPresentedError = "No plan has been presented yet: ask for it (\"present the plan\") and approve it on the pane, or /plan cancel";
    public static string PlanUnreadableError(string path) => $"The plan file {path} could not be read; plan mode stays on — present it again, or /plan cancel";
    public const string NotPlanningError ="Plan mode is not on: /plan <what you want done> starts it";

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

    public const string ApprovalInterruptedNotice = Glyph + " The plan was approved but the reply was stopped: /plan approve starts it.";

    public static IReadOnlyList<string> ShowLines(PlanSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var lines = new List<string>(3) { $"{Glyph} Planning: {session.Requirement}" };
        lines.Add(session.Path is { } path
            ? $"  Plan: {path} (revision {N(session.Revision)}) — /plan approve [--fresh] starts it"
            : "  Plan: not presented yet");
        return lines;
    }

    // ── The approval pane ───────────────────────────────────────────────────

    public const string ApprovalTitle = "Approve the plan?";
    public const string ApproveRow = "Approve & run";
    public const string ApproveFreshRow = "Approve, clear context & run";
    public const string RefineRow = "Keep refining…";
    public const string CancelRow = "Cancel plan";
    public const string ApprovalKeys = "a / f / r / c = pick · Enter = choose · ESC = keep refining";
    public const string FeedbackKeys = "Enter = send · ESC = back";
    public const string FeedbackPrompt = "What should change? Type it below (empty for nothing in particular).";

    public static string FeedbackNotice(string feedback) => $"{Glyph} Sent back with: {feedback}";

    public static string Caption(PlanPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        return $"{presentation.Title} — {presentation.Path} (revision {N(presentation.Revision)})";
    }

    // ── The model's side ────────────────────────────────────────────────────

    /// <summary>
    /// The directive the system prompt carries while planning, after the skills and before the voice
    /// directive, under a custom <c>operata.md</c> too: the requirement itself (so a compaction cannot
    /// lose it), what planning allows, the shape of the plan and how it is presented. Pinned.
    /// </summary>
    public static string Directive(string requirement, string? path, int revision)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        string state = path is null
            ? "No plan has been presented yet."
            : $"The plan so far is in {path} (revision {N(revision)}); present_plan overwrites it with the next revision.";
        return
            "PLAN MODE is on. The user asked for a plan before anything is done. Their requirement: \"" + Flatten(requirement) + "\"\n" +
            "- Do not change anything: no file, repository, command, message or setting. Only read-only tools are offered; use them to research what the plan needs.\n" +
            "- Ask what you need to know, a few questions at a time (with ask_user when it is offered), and fold the user's answers and later messages into the plan.\n" +
            "- Never claim anything was done: nothing is carried out until the user approves.\n" +
            "- When no open question is left, call present_plan with the whole plan as Markdown: a # title, then ## Goal, ## Context, ## Steps (as - [ ] checkboxes, in order), ## Files and areas, ## Risks, ## Verification.\n" +
            "- present_plan shows the plan to the user for approval; follow its result.\n" +
            state;
    }

    public const string ToolDescription =
        "Saves the plan as a Markdown file under plans/ in the working directory and asks the user to approve it. " +
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

    /// <summary>The message that starts an approved plan in the same conversation. Pinned.</summary>
    public static string ExecuteMessage(string path) =>
        $"The plan in {path} is approved. Carry it out step by step, ticking each checkbox in {path} as you finish the step, and tell me when it is done or if something blocks you.";

    /// <summary>The message that starts an approved plan in a fresh conversation: the plan's text inline, since nothing of the planning is left. Pinned.</summary>
    public static string ExecuteFreshMessage(string path, string plan) =>
        ExecuteMessage(path) + "\n\n" + plan.Trim();

    private static string Flatten(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Replace('"', '\'');

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
