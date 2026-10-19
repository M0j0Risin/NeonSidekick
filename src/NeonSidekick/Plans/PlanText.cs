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

    /// <summary>
    /// <c>/plan chat</c> and <c>/plan ready</c> (2026-10-17, the user's ask): plan mode opened to talk a plan through before it is written,
    /// and the way to ask for it. Each only as the whole argument; with words after it, a requirement or detail as before.
    /// </summary>
    public const string ChatWord = "chat";
    public const string ReadyWord = "ready";

    /// <summary>
    /// <c>/plan open &lt;name&gt; --chat</c> (2026-10-18, the user's ask): the plan opened to talk it through, nothing sent; a whole word at
    /// either end of what follows <c>open</c>, case aside. <c>/plan open --chat</c> alone opens the picker to talk.
    /// </summary>
    public const string ChatSwitch = "--chat";

    /// <summary>
    /// <c>/plan open &lt;name&gt; --chat</c>'s line after <see cref="OpenedNotice"/> (2026-10-18): nothing was sent; the first message talks it
    /// through. Pinned.
    /// </summary>
    public const string OpenChatNotice = Glyph + " Talking it through: nothing was sent. Say what should change; /plan ready asks for the plan, /plan approve starts it as it stands.";

    /// <summary>The input row's placeholder while planning.</summary>
    public const string Placeholder = "Planning — add detail, or /plan approve · /plan cancel";

    /// <summary>The placeholder while talking a plan through (2026-10-17, <c>/plan chat</c>).</summary>
    public const string TalkingPlaceholder = "Planning — talk it through; /plan ready for the plan · /plan cancel";

    public const string UsageError = "Usage: /plan <what you want done>, or /plan chat to talk it through first — the model plans it with you and nothing is changed until you approve";

    /// <summary><c>/plan chat</c> outside plan mode (2026-10-17): nothing sent, the first message opens it.</summary>
    public const string ChatEnteredNotice = Glyph + " Plan mode, talking it through: the model reads and researches but changes nothing, and writes the plan when you ask for it (or /plan ready). /plan cancel leaves.";

    /// <summary><c>/plan chat</c> while planning (2026-10-17): talking again.</summary>
    public const string ChatResumedNotice = Glyph + " Talking it through again: the model won't present until you ask.";

    /// <summary>The turn <c>/plan ready</c> sends (2026-10-17). Pinned.</summary>
    public const string ReadyMessage = "I'm ready: present the plan now with present_plan.";

    /// <summary><c>/plan ready</c> before the first message of a <c>/plan chat</c> (2026-10-17): nothing sent. Pinned.</summary>
    public const string ReadyTooSoonError = "Say what the plan is for first: your first message becomes its requirement";

    /// <summary>The line after <see cref="DoneNotice"/> when a plan is complete (2026-10-17, the user's ask and words). Pinned.</summary>
    public const string ComesTogetherNotice = Glyph + " I ❤️ it when a plan comes together.";
    public const string NeedsToolsError = "Plan mode needs the model's tools: switch LLM offer tools on (/settings) and try again";
    public const string NothingPresentedError = "No plan has been presented yet: ask for it (\"present the plan\") and approve it on the pane, or /plan cancel";
    public static string PlanUnreadableError(string path) => $"The plan file {path} could not be read; plan mode stays on — present it again, or /plan cancel";
    public static string NoSuchPlanError(string name) => $"No plan named '{name}' under {PlanSlug.Folder}/ — /plan open lists them";
    public const string NoPlansNotice = Glyph + " No plans yet under " + PlanSlug.Folder + "/.";
    public const string NothingToSaveError = "No reply to save as the plan: ask the model for the plan first";
    public const string NotPlanningError ="Plan mode is not on: /plan <what you want done> starts it";

    /// <summary>
    /// <c>/plan gaps [model|advisor|any] [passes]</c> (2026-10-13): the gap-closing loop started by typing, the reviewer its one word; a pass
    /// count (2026-10-17, the user's ask) caps this run instead of <see cref="Settings.AppSettingsData.PlanGapPasses"/>.
    /// </summary>
    public const string GapsWord = "gaps";

    /// <summary>The reviewer words <c>/plan gaps</c> takes, in <see cref="PlanReviewer"/>'s order.</summary>
    public static readonly IReadOnlyList<string> ReviewerWords = ["model", "advisor", "any", "confirm"];

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

    /// <summary>
    /// The words after <c>/plan gaps</c> (2026-10-17): none, a reviewer, a pass count, or one of each in either order is
    /// <see cref="GapsArgs.Ok"/>; one other word <see cref="GapsArgs.Unknown"/>; a count outside 0–50 (a sign, an overflow)
    /// <see cref="GapsArgs.OutOfRange"/>; anything else <see cref="GapsArgs.NotGaps"/>, plan detail. <paramref name="word"/> is the
    /// word at fault, else empty. Pure.
    /// </summary>
    public static GapsArgs ParseGapsArgs(string after, out PlanReviewer? reviewer, out int? passes, out string word)
    {
        ArgumentNullException.ThrowIfNull(after);
        reviewer = null;
        passes = null;
        word = "";
        string[] words = after.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 2)
        {
            return GapsArgs.NotGaps;
        }

        string? numberWord = null;
        foreach (string w in words)
        {
            if (ParseReviewer(w) is { } named && reviewer is null)
            {
                reviewer = named;
            }
            else if (IsNumber(w) && numberWord is null)
            {
                numberWord = w;
            }
            else
            {
                reviewer = null;
                word = words.Length == 1 ? w : "";
                return words.Length == 1 ? GapsArgs.Unknown : GapsArgs.NotGaps;
            }
        }

        if (numberWord is not null)
        {
            if (!int.TryParse(numberWord, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                || count < Settings.AppSettingsData.MinPlanGapPasses || count > Settings.AppSettingsData.MaxPlanGapPasses)
            {
                reviewer = null;
                word = numberWord;
                return GapsArgs.OutOfRange;
            }

            passes = count;
        }

        return GapsArgs.Ok;
    }

    /// <summary>A whole number as typed, a sign allowed, so <c>-1</c> and an overflow read as a count out of range rather than a reviewer.</summary>
    private static bool IsNumber(string word)
    {
        string digits = word.Length > 1 && word[0] is '-' or '+' ? word[1..] : word;
        return digits.Length > 0 && digits.All(char.IsAsciiDigit);
    }

    public static string UnknownReviewerError(string word) =>
        $"No reviewer named '{word}': /plan gaps takes model, advisor, any or confirm, and a pass count ({N(Settings.AppSettingsData.MinPlanGapPasses)}–{N(Settings.AppSettingsData.MaxPlanGapPasses)})";

    /// <summary><c>/plan gaps 25</c> (2026-10-17): a pass count out of range, nothing started. Pinned.</summary>
    public static string GapPassesRangeError(string word) =>
        $"'{word}' passes: /plan gaps takes {N(Settings.AppSettingsData.MinPlanGapPasses)} to {N(Settings.AppSettingsData.MaxPlanGapPasses)}, 0 for no cap";

    public static string EnteredNotice => $"{Glyph} Plan mode: the model can read and research but not change anything until you approve the plan. Add details as messages; /plan cancel leaves.";

    public static string CancelledNotice(string? path) => path is null
        ? $"{Glyph} Plan mode off; nothing was saved."
        : $"{Glyph} Plan mode off; {path} is kept, marked canceled.";

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
        string status = StatusWord(plan) + ReviewMarkPart(plan.Review);
        string steps = plan.Total > 0 ? $" · {N(plan.Done)}/{N(plan.Total)}" : "";
        string when = plan.Updated is { } u && u != DateTimeOffset.MinValue ? " · " + u.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
        return $"  {plan.Name} — {plan.Title} ({status}{steps}{when})";
    }

    // ── The plans picker (2026-10-18, bare /plan open, /plan open --chat, bare /plan ralph) ──

    /// <summary>The picker's title.</summary>
    public const string PickerTitle = Glyph + " Plans";

    /// <summary>The picker's buttons and their keys: open, talk it through, and outside plan mode run it as a Ralph loop.</summary>
    public const string OpenButton = "↗ open";
    public const char OpenKey = 'o';
    public const string TalkButton = "💬 talk";
    public const char TalkKey = 'c';
    public const string RalphButton = "🔁 Ralph loop";
    public const char RalphKey = 'l';

    /// <summary>
    /// The picker's hint row (2026-10-18): what Enter does in <paramref name="mode"/>, then the buttons that do something else, <c>l</c> only with
    /// <paramref name="ralphButton"/>. Each within 119 cells (<c>ConsistencyPassTests</c>). Pinned.
    /// </summary>
    public static string PickerKeys(PlanPickMode mode, bool ralphButton)
    {
        string ralph = ralphButton ? "l = Ralph loop · " : "";
        return mode switch
        {
            PlanPickMode.Talk => "Enter = talk · o = open · " + ralph + UI.MenuFilter.TypeOrShiftAndCloseKeys,
            PlanPickMode.Ralph => "Enter = Ralph loop · o = open · c = talk · " + UI.MenuFilter.TypeOrShiftAndCloseKeys,
            _ => "Enter = open · c = talk · " + ralph + UI.MenuFilter.TypeOrShiftAndCloseKeys,
        };
    }

    /// <summary>
    /// The picker's footer for <paramref name="plan"/>: its requirement (else its title), the line <paramref name="ralph"/> when it has run as a
    /// Ralph loop, and its path last.
    /// </summary>
    public static UI.MenuFooter PickerFooter(PlanListing plan, string? ralph = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string requirement = plan.Requirement.Length > 0 ? "Requirement: " + Flatten(plan.Requirement) : plan.Title;
        return ralph is { Length: > 0 }
            ? new UI.MenuFooter(requirement + "\n" + ralph, plan.Path) { KeepLines = true }
            : new UI.MenuFooter(requirement, plan.Path);
    }

    /// <summary>A plan picked that cannot be read whole (2026-10-18): said on the picker's status line, which stays open. Pinned.</summary>
    public static string PickerUnreadableError(string path) => $"{path} could not be read whole: open it in an editor to see why";

    /// <summary>The note beside a plan on the <c>/plan open</c> completion list.</summary>
    public static string CompletionNote(PlanListing plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string status = StatusWord(plan) + ReviewMarkPart(plan.Review);
        return plan.Total > 0 ? $"{status} · {N(plan.Done)}/{N(plan.Total)} · {plan.Title}" : $"{status} · {plan.Title}";
    }

    /// <summary>The words <c>/plan open</c> puts after the status for a plan's last review (2026-10-17): <c>confirmed</c>, <c>reviewed</c>, <c>edited since review</c>. Pinned.</summary>
    public static string ReviewMarkWord(PlanReviewMark mark) => mark switch
    {
        PlanReviewMark.Confirmed => "confirmed",
        PlanReviewMark.Reviewed => "reviewed",
        PlanReviewMark.EditedSince => "edited since review",
        _ => "",
    };

    private static string ReviewMarkPart(PlanReviewMark mark) => mark == PlanReviewMark.None ? "" : " · " + ReviewMarkWord(mark);

    /// <summary>A listed plan's status word: its header's, <see cref="UnreadableWord"/> for a file not read whole (2026-10-17), else <c>no header</c>.</summary>
    private static string StatusWord(PlanListing plan) =>
        plan.Unreadable ? UnreadableWord : plan.Status is { } s ? PlanDocument.Word(s) : "no header";

    /// <summary>The status word of a plan file that cannot be read whole (2026-10-17).</summary>
    public const string UnreadableWord = "unreadable";

    /// <summary><c>MarkFile</c> on a plan file that is there but cannot be read whole (2026-10-17): the status was left as it was. Pinned.</summary>
    public static string StatusUnwrittenWarning(string path) => $"The plan file {path} could not be read whole; its status was not updated";

    /// <summary>A plan complete; <see cref="ComesTogetherNotice"/> follows it on the next line (2026-10-17).</summary>
    public static string DoneNotice(string path) => $"{Glyph} Every step of {path} is ticked: marked done.";

    public static string IncompleteNotice(string path, int done, int total) =>
        $"{Glyph} {path}: {N(done)} of {N(total)} steps done, marked incomplete — /plan open {System.IO.Path.GetFileNameWithoutExtension(path)} picks it up.";

    /// <summary>The hint after a planning reply that reads as a plan the model never presented (2026-09-26).</summary>
    public const string UnpresentedHint = Glyph + " That reply looks like a plan but was not presented: /plan save [name] keeps it as the plan, or ask for it to be presented.";

    public const string ApprovalInterruptedNotice = Glyph + " The plan was approved but the reply was stopped: /plan approve starts it.";

    // ── The gap-closing loop (2026-10-13) ───────────────────────────────────

    /// <summary>
    /// The message each pass sends: one line, since the rules ride in the directive (<see cref="GapReviewLines"/>). Pinned. A pass after a clean
    /// one (2026-10-17, <paramref name="cleanSoFar"/> above 0) asks for another look, not a "confirm", so the model isn't steered toward agreeing.
    /// </summary>
    public static string GapPassMessage(string path, int pass, int limit, PlanReviewer reviewer, int cleanSoFar = 0) => cleanSoFar > 0
        ? $"Gap review pass {PassOf(pass, limit)} for the plan in {path} (reviewer: {ReviewerWord(reviewer)}): the last pass found no gaps; review it again, close any you find, then call present_plan."
        : $"Gap review pass {PassOf(pass, limit)} for the plan in {path} (reviewer: {ReviewerWord(reviewer)}): find and close its gaps, then call present_plan.";

    /// <summary>
    /// A pass that saved a revision with gaps closed: the count (and since 2026-10-17 its minor fixes), then each gap and each minor fix
    /// (at most <see cref="PassNoticeGaps"/> lines, then how many more).
    /// </summary>
    public static IReadOnlyList<string> PassNoticeLines(int pass, int limit, int revision, IReadOnlyList<string> gaps, IReadOnlyList<string>? minor = null)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        minor ??= [];
        var lines = new List<string>(PassNoticeGaps + 2)
        {
            $"{Glyph} Gap review pass {PassOf(pass, limit)}: revision {N(revision)} saved, {Gaps(gaps.Count)} closed{MinorPart(minor.Count)}.",
        };
        lines.AddRange(ItemLines(gaps.Select(Flatten).Concat(minor.Select(m => MinorLabel + Flatten(m))).ToList()));
        return lines;
    }

    /// <summary>The most gaps a pass's notice lists; the --log line has them all.</summary>
    public const int PassNoticeGaps = 10;

    /// <summary>
    /// A clean pass that did not end the review (2026-10-17): the streak, its minor fixes, then what it checked and each minor fix (at most
    /// <see cref="PassNoticeGaps"/> lines). Said only when another pass follows. Pinned.
    /// </summary>
    public static IReadOnlyList<string> CleanPassNoticeLines(int pass, int limit, int clean, int needed, IReadOnlyList<string> @checked, IReadOnlyList<string>? minor = null)
    {
        ArgumentNullException.ThrowIfNull(@checked);
        minor ??= [];
        var lines = new List<string>(PassNoticeGaps + 2) { CleanPassNotice(pass, limit, clean, needed, minor.Count) };
        lines.AddRange(ReportLines(@checked, minor));
        return lines;
    }

    /// <summary>The head of <see cref="CleanPassNoticeLines"/>: <c>📝 Gap review pass 2 of 5: no gaps found (1 of 2 clean passes); checking again.</c> Pinned.</summary>
    public static string CleanPassNotice(int pass, int limit, int clean, int needed, int minor = 0) =>
        $"{Glyph} Gap review pass {PassOf(pass, limit)}: no gaps found ({N(clean)} of {CleanPassesWord(needed)}){MinorPart(minor)}; checking again.";

    /// <summary>A pass limit below the clean count (2026-10-17, the user's call): said once as pass 1 begins. Pinned.</summary>
    public static string CleanLoweredNotice(int limit, int asked) =>
        $"{Glyph} The pass limit ({N(limit)}) is below LLM plan gap clean passes ({N(asked)}): this review finishes on {CleanRun(limit)}.";

    /// <summary>The approval pane's note once enough passes found no gaps; with <paramref name="clean"/> above 1 (2026-10-17), the confirmation said.</summary>
    public static string ConvergedNote(int passes, int closed, int clean = 1) =>
        $"no gaps left after {Passes(passes)}, {Gaps(closed)} closed" + Confirmed(clean);

    public static string ConvergedNotice(string path, int passes, int closed, int clean = 1) =>
        $"{Glyph} Gap review done: no gaps left in {path} after {Passes(passes)}, {Gaps(closed)} closed" + Confirmed(clean) + ".";

    /// <summary>The lines under a convergence's notice (2026-10-17): what its last pass checked, and its minor fixes.</summary>
    public static IReadOnlyList<string> ReportLines(IReadOnlyList<string> @checked, IReadOnlyList<string>? minor = null)
    {
        ArgumentNullException.ThrowIfNull(@checked);
        return ItemLines(@checked.Select(c => CheckedLabel + Flatten(c)).Concat((minor ?? []).Select(m => MinorLabel + Flatten(m))).ToList());
    }

    private const string CheckedLabel = "checked: ";
    private const string MinorLabel = "minor: ";

    /// <summary><c>  · item</c> lines, at most <see cref="PassNoticeGaps"/>, then <c>  … and N more</c>.</summary>
    private static List<string> ItemLines(IReadOnlyList<string> items)
    {
        var lines = items.Take(PassNoticeGaps).Select(item => "  · " + item).ToList();
        if (items.Count > PassNoticeGaps)
        {
            lines.Add($"  … and {N(items.Count - PassNoticeGaps)} more");
        }

        return lines;
    }

    /// <summary>
    /// The approval pane's note when the loop stopped short at its limit or on a stall, the last pass's gaps counted; at the limit with the
    /// last pass clean (2026-10-17, <see cref="PlanGapStop.Unconfirmed"/>) the streak it got to.
    /// </summary>
    public static string StoppedNote(PlanGapStop reason, int passes, int left, int clean = 0, int needed = 1) => reason switch
    {
        PlanGapStop.Limit => $"gap review stopped at its limit after {Passes(passes)}; the last pass still closed {Gaps(left)}",
        PlanGapStop.Unconfirmed => $"no gaps in the last pass, but the limit came after {N(clean)} of {CleanPassesWord(needed)}",
        _ => $"gap review stalled after {Passes(passes)}: the plan stopped changing or the same gaps came back",
    };

    /// <summary>Why the loop stopped, said once it has (a convergence has <see cref="ConvergedNotice"/>).</summary>
    public static string GapStopNotice(PlanGapStop reason, int passes, int closed) => reason switch
    {
        PlanGapStop.Limit => $"{Glyph} Gap review stopped at its limit after {Passes(passes)}, {Gaps(closed)} closed; the last pass still found some.",
        PlanGapStop.Unconfirmed => $"{Glyph} Gap review stopped at its limit after {Passes(passes)}, {Gaps(closed)} closed: the last pass found no gaps, but there was no pass left to check it again.",
        PlanGapStop.Stalled => $"{Glyph} Gap review stopped after {Passes(passes)}, {Gaps(closed)} closed: the plan stopped changing or the same gaps came back.",
        PlanGapStop.NoRevision => $"{Glyph} Gap review stopped: pass {N(passes)} saved no revision. /plan gaps starts a new review; /plan approve [--ralph] starts the plan.",
        PlanGapStop.UserSpoke => $"{Glyph} Gap review stopped after {Passes(passes)} for your message. /plan gaps starts a new review.",
        _ => $"{Glyph} Gap review stopped at pass {N(passes)}, {Gaps(closed)} closed. /plan gaps starts a new review; /plan approve [--ralph] starts the plan.",
    };

    public const string GapsDroppedNotice = Glyph + " The gap review was chosen but the reply was stopped: /plan gaps starts it.";

    /// <summary>Close gaps… chosen inside a reply while a message of the user's was queued (2026-10-14): the message goes first.</summary>
    public const string GapsYieldedNotice = Glyph + " The gap review was chosen but your message goes first: /plan gaps starts it.";

    /// <summary>Headless (2026-10-13): no confirm to ask on, so every advisor call would be refused.</summary>
    public const string GapsConfirmFallbackNotice = Glyph + " Advisor confirm is on and headless cannot ask, so the model reviews the plan alone.";

    /// <summary>Headless (2026-10-13): no Esc to stop a loop with no limit; <paramref name="fromCommand"/> (2026-10-17) when the 0 was typed as <c>/plan gaps 0</c>.</summary>
    public static string GapsHeadlessCapNotice(int cap, bool fromCommand = false) => fromCommand
        ? $"{Glyph} /plan gaps 0 means no limit, but headless has no Esc: at most {N(cap)} passes."
        : $"{Glyph} LLM plan gap passes is 0 (no limit), but headless has no Esc: at most {N(cap)} passes.";

    /// <summary>The --log line of a pass: its gaps whole, whether it asked an advisor, and since 2026-10-17 its minor fixes and its cost.</summary>
    public static string PassLogLine(int pass, int revision, IReadOnlyList<string> gaps, bool advisorAsked, IReadOnlyList<string>? minor = null, PlanPassRecord? cost = null)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        minor ??= [];
        return $"plan gaps: pass {N(pass)}, revision {N(revision)}, {Gaps(gaps.Count)} closed{MinorPart(minor.Count)}" + (advisorAsked ? ", an advisor asked" : "") + CostPart(cost)
            + (gaps.Count > 0 ? ": " + string.Join(" | ", gaps.Select(Flatten)) : "")
            + (minor.Count > 0 ? (gaps.Count > 0 ? "; " : ": ") + MinorLabel + string.Join(" | ", minor.Select(Flatten)) : "");
    }

    /// <summary>The --log line of a clean pass (2026-10-17): <c>plan gaps: pass N clean (k of n)</c>, its cost, what it checked and its minor fixes, every item.</summary>
    public static string CleanPassLogLine(int pass, int clean, int needed, IReadOnlyList<string> @checked, IReadOnlyList<string>? minor = null, bool advisorAsked = false, PlanPassRecord? cost = null)
    {
        ArgumentNullException.ThrowIfNull(@checked);
        minor ??= [];
        return $"plan gaps: pass {N(pass)} clean ({N(clean)} of {N(needed)})" + (advisorAsked ? ", an advisor asked" : "") + CostPart(cost)
            + (@checked.Count > 0 ? ": " + CheckedLabel + string.Join(" | ", @checked.Select(Flatten)) : "")
            + (minor.Count > 0 ? (@checked.Count > 0 ? "; " : ": ") + MinorLabel + string.Join(" | ", minor.Select(Flatten)) : "");
    }

    /// <summary>
    /// What each confirming pass looks hardest at (2026-10-17, the user's pick of idea #1), in turn: the loop's <see cref="PlanGapLoop.Confirmations"/>
    /// counts across streaks, the fifth wrapping to the first. Pinned.
    /// </summary>
    public static readonly IReadOnlyList<string> ConfirmFocuses =
    [
        "the steps' order and dependencies: what each step needs done before it",
        "how each step is verified, and what proves the whole plan worked",
        "the risks: what could go wrong, how it would show, and how it is undone",
        "every file, API, specification, version or setting the plan assumes, and whether it was read or looked up",
    ];

    /// <summary><see cref="ConfirmFocuses"/>' short labels for the review summary (2026-10-17). Pinned.</summary>
    public static readonly IReadOnlyList<string> ConfirmFocusLabels = ["order", "verification", "risks", "assumptions"];

    /// <summary>
    /// The review summary said at every stop with a pass recorded (2026-10-17, the user's approval of later idea #2): a heading with the
    /// totals, one line per pass (its gaps or <c>clean</c>, its minor fixes, a confirming pass's focus, an advisor's under <c>confirm</c>, its
    /// time and tokens when metered), the last line ending with how it stopped. Pinned.
    /// </summary>
    public static IReadOnlyList<string> ReviewSummaryLines(IReadOnlyList<PlanPassRecord> history, PlanGapStop reason, int cleanPasses = 1)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0)
        {
            return [];
        }

        var timed = history.Where(p => p.Elapsed is not null).ToList();
        string totals = timed.Count == 0 ? ""
            : ", " + NeonSidekick.UI.ThinkingFoldText.Duration(timed.Aggregate(TimeSpan.Zero, (sum, p) => sum + p.Elapsed!.Value))
              + TokensPart(timed.Sum(p => p.Tokens ?? 0), 0);
        var lines = new List<string>(history.Count + 1) { $"{Glyph} Gap review, {Passes(history.Count)}{totals}:" };
        for (int i = 0; i < history.Count; i++)
        {
            var p = history[i];
            string outcome = p.Gaps > 0 ? Gaps(p.Gaps) : p.Clean ? "clean" : "nothing presented";
            string marks = string.Join(", ", new[] { p.Focus >= 0 ? ConfirmFocusLabels[p.Focus % ConfirmFocusLabels.Count] : null, p.Advisor ? "advisor" : null }.OfType<string>());
            string line = $"  pass {N(p.Pass)}: {outcome}{MinorPart(p.Minor)}" + (marks.Length > 0 ? $" ({marks})" : "")
                + (i == history.Count - 1 ? ", " + EndingWord(reason, cleanPasses) : "");
            if (p.Elapsed is { } elapsed)
            {
                line += " · " + NeonSidekick.UI.ThinkingFoldText.Duration(elapsed) + TokensPart(p.Tokens ?? 0, p.AdvisorTokens ?? 0, " · ");
            }

            lines.Add(line);
        }

        return lines;
    }

    /// <summary>How a review ended, as the summary's last line says it (2026-10-17).</summary>
    private static string EndingWord(PlanGapStop reason, int cleanPasses) => reason switch
    {
        PlanGapStop.Converged => cleanPasses >= 2 ? "confirmed" : "no gaps left",
        PlanGapStop.Unconfirmed => "cut short",
        PlanGapStop.Stopped => "stopped",
        PlanGapStop.Limit => "limit",
        PlanGapStop.Stalled => "stalled",
        PlanGapStop.NoRevision => "no revision",
        _ => "your message",
    };

    /// <summary>
    /// The plan file's <c>review:</c> line (2026-10-17, the user's approval of later idea #3) for a review that ended on its own, with the body's
    /// fingerprint (<see cref="PlanDocument.Fingerprint"/>); read back by <see cref="PlanReview.TryParse"/>. Pinned.
    /// </summary>
    public static string ReviewLine(int passes, int closed, PlanGapStop outcome, int cleanPasses, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        string how = outcome switch
        {
            PlanGapStop.Converged => cleanPasses >= 2 ? $"confirmed by {N(cleanPasses)} clean passes" : ReviewOneCleanPass,
            PlanGapStop.Unconfirmed => "cut short at the limit",
            PlanGapStop.Limit => "stopped at the limit with gaps left",
            _ => "stalled",
        };
        return $"{Passes(passes)}, {Gaps(closed)} closed, {how} (body {fingerprint})";
    }

    /// <summary>A review that converged on one clean pass, as its <c>review:</c> line says it (2026-10-17): "reviewed", not "confirmed".</summary>
    public const string ReviewOneCleanPass = "no gaps left after 1 clean pass";

    /// <summary><c>2 of 5</c>, or <c>2</c> with no limit; shared with the code audit's words since 2026-10-18 (<see cref="Audits.AuditText"/>).</summary>
    internal static string PassOf(int pass, int limit) => limit > 0 ? $"{N(pass)} of {N(limit)}" : N(pass);

    private static string Passes(int passes) => passes == 1 ? "1 pass" : $"{N(passes)} passes";

    private static string Gaps(int gaps) => gaps == 1 ? "1 gap" : $"{N(gaps)} gaps";

    /// <summary><c>, 2 minor fixes</c> / <c>, 1 minor fix</c>, nothing at 0 (2026-10-17).</summary>
    private static string MinorPart(int minor) => minor <= 0 ? "" : minor == 1 ? ", 1 minor fix" : $", {N(minor)} minor fixes";

    /// <summary><c>2 clean passes</c> / <c>1 clean pass</c>.</summary>
    private static string CleanPassesWord(int count) => count == 1 ? "1 clean pass" : $"{N(count)} clean passes";

    /// <summary><c>1 clean pass</c> / <c>2 clean passes in a row</c>.</summary>
    private static string CleanRun(int count) => count == 1 ? "1 clean pass" : $"{N(count)} clean passes in a row";

    private static string Confirmed(int clean) => clean > 1 ? $", confirmed by {N(clean)} clean passes" : "";

    /// <summary><c>, 41.2k tokens (12.0k advisor)</c>; nothing when no tokens were reported (a server that sends no usage).</summary>
    private static string TokensPart(long tokens, long advisor, string lead = ", ") =>
        tokens <= 0 && advisor <= 0 ? "" : lead + NeonSidekick.App.UsageText.Compact(tokens) + (advisor > 0 ? " (" + NeonSidekick.App.UsageText.CompactNumber(advisor) + " advisor)" : "");

    /// <summary>A pass's time and tokens for its --log line, nothing when it was not metered.</summary>
    private static string CostPart(PlanPassRecord? cost) => cost is { Elapsed: { } elapsed }
        ? ", " + NeonSidekick.UI.ThinkingFoldText.Duration(elapsed) + TokensPart(cost.Tokens ?? 0, cost.AdvisorTokens ?? 0)
        : "";

    public static IReadOnlyList<string> ShowLines(PlanSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string requirement = session.RequirementIsOpening ? "(the conversation began with) " + session.Requirement : session.Requirement;
        var lines = new List<string>(4) { $"{Glyph} Planning: {requirement}" };
        if (session.Talking)
        {
            lines.Add("  Talking it through: the model writes the plan when you ask (or /plan ready)");
        }

        lines.Add(session.Path is { } path
            ? $"  Plan: {path} (revision {N(session.Revision)}) — /plan approve [--fresh | --ralph] starts it"
            : "  Plan: not presented yet");
        if (session.Closing is { Pending: false } loop)
        {
            lines.Add($"  Closing gaps: pass {PassOf(loop.Pass, loop.Limit)} ({ReviewerWord(loop.Reviewer)}), {Gaps(loop.Closed)} closed so far"
                + (loop.CleanStreak > 0 ? $", {N(loop.CleanStreak)} of {CleanPassesWord(loop.CleanPasses)}" : ""));
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
    public const string ApprovalKeys = "a / f / l / r / g / c = pick · Enter = choose · Esc = keep refining";

    /// <summary>The words ahead of the Ralph loop's check line on the approval pane's caption (2026-10-18).</summary>
    public const string RalphCaptionLead = "Ralph loop: ";

    /// <summary>The second page Close gaps… opens while advisors are offered: who reviews.</summary>
    public const string ReviewerTitle = "Close gaps: who reviews?";
    public static readonly IReadOnlyList<string> ReviewerRows = ["Close gaps (model)", "Close gaps (advisor)", "Close gaps (any)", "Close gaps (model, advisor confirms)"];
    public const string ReviewerKeys = "m / a / y / c = pick · Enter = choose · Esc = back";
    public const string FeedbackKeys = "Enter = send · Esc = back";
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
    public static string Directive(string requirement, string? path, int revision, IReadOnlyList<Settings.AdvisorEntry>? advisors, PlanGapState? gap) =>
        Directive(requirement, path, revision, advisors, gap, null);

    /// <summary>
    /// <see cref="Directive(string, string?, int, IReadOnlyList{Settings.AdvisorEntry}?, PlanGapState?)"/> with <paramref name="research"/>
    /// (2026-10-17, the user's ask: a plan that rests on an API or a spec should look it up, not guess): the web tools the turn offers of
    /// <see cref="ResearchTools"/>, named by <see cref="WebResearchLine"/> after the first rule and, in a gap pass, by its research line;
    /// null or none, the directive as before. Pinned.
    /// </summary>
    public static string Directive(string requirement, string? path, int revision, IReadOnlyList<Settings.AdvisorEntry>? advisors, PlanGapState? gap, IReadOnlyList<string>? research) =>
        Directive(requirement, path, revision, advisors, gap, research, default);

    /// <summary>
    /// <see cref="Directive(string, string?, int, IReadOnlyList{Settings.AdvisorEntry}?, PlanGapState?, IReadOnlyList{string}?)"/> with
    /// <paramref name="talk"/> (2026-10-17, <c>/plan chat</c>): while talking, the opening says the user wants to talk it through (and what the
    /// conversation began with, or that nothing is said yet) and <see cref="TalkingLine"/> replaces the present bullet; the default, the
    /// directive as before. Pinned.
    /// </summary>
    public static string Directive(string requirement, string? path, int revision, IReadOnlyList<Settings.AdvisorEntry>? advisors, PlanGapState? gap, IReadOnlyList<string>? research, PlanTalk talk) =>
        Directive(requirement, path, revision, advisors, gap, research, talk, null);

    /// <summary>
    /// <see cref="Directive(string, string?, int, IReadOnlyList{Settings.AdvisorEntry}?, PlanGapState?, IReadOnlyList{string}?, PlanTalk)"/> with
    /// <paramref name="references"/> (2026-10-18, plan reference pictures): the <c>ref-N</c> names the conversation's pictures are saved as beside the
    /// plan, named by <see cref="ReferencesLine"/>; null or none, no such bullet. Pinned.
    /// </summary>
    public static string Directive(string requirement, string? path, int revision, IReadOnlyList<Settings.AdvisorEntry>? advisors, PlanGapState? gap, IReadOnlyList<string>? research, PlanTalk talk, IReadOnlyList<string>? references)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        bool offered = advisors is { Count: > 0 };
        bool web = research is { Count: > 0 };
        string state = path is null
            ? "No plan has been presented yet."
            : $"The plan so far is in {path} (revision {N(revision)}); present_plan overwrites it with the next revision."
              // Talking over a plan already written (2026-10-18, /plan open <name> --chat): it may be a file the conversation never saw.
              + (talk.Talking ? " Read it before you discuss it if its text isn't in this conversation." : "");
        string opening = talk.Talking && requirement.Trim().Length == 0
            ? "PLAN MODE is on. The user wants to talk a plan through before anything is done; they haven't said what it is for yet.\n"
            : talk.Opening
                ? (talk.Talking ? "PLAN MODE is on. The user wants to talk a plan through before anything is done." : "PLAN MODE is on. The user asked for a plan before anything is done.")
                  + " The conversation began with: \"" + Flatten(requirement) + "\"\n"
                : talk.Talking
                    // A settled requirement while talking (2026-10-18): a reopened plan's, or one presented before /plan chat.
                    ? "PLAN MODE is on. The user wants to talk the plan through before anything is done. Their requirement: \"" + Flatten(requirement) + "\"\n"
                    : "PLAN MODE is on. The user asked for a plan before anything is done. Their requirement: \"" + Flatten(requirement) + "\"\n";
        return
            opening +
            "- Do not change anything: write no file, change no repository, run no command that changes state, send no message and change no setting. Only read-only tools are offered; use them to research what the plan needs.\n" +
            (web ? WebResearchLine(research!) + "\n" : "") +
            (gap is { AskUser: false } ? GapNoAskLine + "\n" : "- Ask what you need to know, a few questions at a time (with ask_user when it is offered), and fold the user's answers and later messages into the plan.\n") +
            "- Never claim anything was done: nothing is carried out until the user approves.\n" +
            (talk.Talking
                ? TalkingLine + "\n"
                : "- When no open question is left, call present_plan with the whole plan as Markdown: " + PlanShape + "\n") +
            VerifyLine + "\n" +
            (references is { Count: > 0 } pictures ? ReferencesLine(pictures) + "\n" : "") +
            "- Never write the plan only in your reply: call present_plan with it.\n" +
            "- present_plan shows the plan to the user for approval; follow its result.\n" +
            (offered && gap?.Who is not PlanReviewer.Model ? AdvisorsLine(advisors!) + "\n" : "") +
            (gap is { } review ? GapReviewLines(review, offered, web) + "\n" : "") +
            state;
    }

    /// <summary>
    /// The ask bullet's place in a gap pass that may not wait on the user (2026-10-18, the user's ask: <see cref="Settings.AppSettingsData.PlanGapAskUser"/>
    /// off, so a review can run unattended). Pinned.
    /// </summary>
    public const string GapNoAskLine =
        "- Do not ask the user anything during this review, and take no photo or screen capture: settle what research can, and fold the user's later messages into the plan.";

    /// <summary>
    /// The research bullet's second sentence in a gap pass that may not ask (2026-10-18): the user's call written into <c>## Risks</c> as an open
    /// question. A plain bullet, never a checkbox: <see cref="PlanDocument"/>'s progress counts every task box in the body, so an unticked one
    /// would hold back the plan's done state. Listed in <c>minor</c>, it shows in the pass's notice without breaking the clean streak. Pinned.
    /// </summary>
    public const string GapOpenQuestionSentence =
        "A decision only the user can make, write into ## Risks as a plain bullet starting \"Open question (your call):\", never a checkbox; it is not a gap, so leave it out of gaps and list it in minor as \"open question: …\".";

    /// <summary>
    /// What the gap definition's "a choice left open" gains in a gap pass that may not ask (2026-10-18): without it a later pass would report the
    /// open question as a gap and the review could never converge. Pinned.
    /// </summary>
    public const string GapOpenQuestionException = " (except an open question written into ## Risks as the user's call)";

    /// <summary>
    /// The plan's shape as the directive asks for it (2026-10-18, the Ralph loop: each step carries its own check, and the only checkboxes are the
    /// steps', so a loop and the progress line count the same things). Pinned.
    /// </summary>
    public const string PlanShape =
        "a # title, then ## Goal, ## Context, ## Steps (as - [ ] checkboxes, in order, each with an indented Done when: line saying how to tell it is done), " +
        "## Files and areas, ## Risks, ## Verification. Use plain bullets outside ## Steps, never checkboxes.";

    /// <summary>
    /// The check commands' bullet (2026-10-18, the Ralph loop: the app runs them after each step and at the end): only commands seen in the project,
    /// the examples generic, never a guess. Pinned.
    /// </summary>
    public const string VerifyLine =
        "- If you have seen how this project checks itself (its README, CI file or scripts), pass present_plan a quick command as verify (it builds, or its tests pass) " +
        "and, when there is one, a slower whole check as final, such as dotnet build, npm test, cargo test or pytest -q; leave both out rather than guess.";

    /// <summary>
    /// The pictures' bullet (2026-10-18, plan reference pictures): the conversation's pictures are saved beside the plan as <paramref name="names"/>;
    /// the plan lists each under <c>## References</c> with what it shows, and a step that relies on one names it. Pinned.
    /// </summary>
    public static string ReferencesLine(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return "- The pictures in this conversation are saved beside the plan as " + string.Join(", ", names) + ": list each under ## References as - ref-N: what it shows, " +
            "and name the ref-N a step relies on in that step, so whoever carries it out is shown the picture.";
    }

    /// <summary>A reference picture as a stored session keeps it (2026-10-18): the file beside the plan, not its bytes. Pinned.</summary>
    public static string ReferenceNotKept(string path) => $"(reference picture {path}, kept beside the plan)";

    /// <summary>The dim line under a run's message for the reference pictures it carries (2026-10-18): they are not drawn again. Pinned.</summary>
    public static string ReferencesAttachedNotice(int count) =>
        count == 1 ? Glyph + " 1 reference picture attached." : $"{Glyph} {N(count)} reference pictures attached.";

    /// <summary>A run whose server cannot see pictures (2026-10-18): the plan's references left out. Pinned.</summary>
    public static string ReferencesUnseenNotice(int count) =>
        $"{Glyph} The plan lists {(count == 1 ? "a reference picture" : N(count) + " reference pictures")}, but the model in use cannot see pictures: sent without.";

    /// <summary>The present bullet's place while talking a plan through (2026-10-17, <c>/plan chat</c>). Pinned.</summary>
    public const string TalkingLine =
        "- The user wants to talk this through first: discuss, ask and research, but do not call present_plan and do not write the whole plan out until they ask for it, in their own words or with /plan ready. " +
        "When they do, call present_plan with the whole plan as Markdown: " + PlanShape;

    /// <summary>
    /// The gap-closing pass's rules (2026-10-13, the user's ask): what a gap is and is not, how to research and ask, and how to
    /// report the pass through <c>present_plan</c>'s <c>gaps</c>; then the reviewer's sentence, which falls back to the model
    /// alone when no advisor is offered (Any adds none: the advisors bullet above already says to consult only where a role fits). Pinned.
    /// </summary>
    public static string GapReviewLines(PlanGapState gap, bool advisorsOffered) => GapReviewLines(gap, advisorsOffered, web: false);

    /// <summary>
    /// <see cref="GapReviewLines(PlanGapState, bool)"/> with the web offered (2026-10-17): an API, specification or version the plan
    /// assumes without checking is a gap, and the research line sends the model to the web for it. Pinned. The clean passes (later that
    /// day): the first bullet names the count a review ends on when it is above 1, a confirming pass gets its own bullet with its focus
    /// (<see cref="ConfirmFocuses"/>), the last bullet asks for <c>minor</c> and <c>checked</c>, and the reviewer's sentence follows the
    /// pass's reviewer (<see cref="PlanGapState.Who"/>), an advisor's on a confirming pass naming the focus.
    /// </summary>
    public static string GapReviewLines(PlanGapState gap, bool advisorsOffered, bool web)
    {
        ArgumentNullException.ThrowIfNull(gap);
        bool confirming = gap.CleanSoFar > 0;
        // A pass that may not ask (2026-10-18): the user's call becomes an open question in ## Risks, never a question on a pane.
        string decision = gap.AskUser ? "A decision only the user can make, ask them (with ask_user when it is offered)." : GapOpenQuestionSentence;
        string focus = ConfirmFocuses[(Math.Max(1, gap.Confirmation) - 1) % ConfirmFocuses.Count];
        string reviewer = gap.Who switch
        {
            PlanReviewer.Model => "- Review it yourself; do not consult advisors on this pass.",
            _ when !advisorsOffered => "- No advisors are offered: review it yourself.",
            PlanReviewer.Advisor when confirming => "- Ask at least one advisor with " + NeonSidekick.Llm.Tools.NeonAdvisorTool.ToolName + ", the one whose role fits best, to find the gaps: put the plan's text and this pass's focus in the question, then close what they name.",
            PlanReviewer.Advisor => "- Ask at least one advisor with " + NeonSidekick.Llm.Tools.NeonAdvisorTool.ToolName + ", the one whose role fits best, to find the gaps: put the plan's text in the question, then close what they name.",
            _ => "",
        };
        return
            "- A gap review is running (pass " + PassOf(gap.Pass, gap.Limit) + "): present_plan's gaps lists what each revision closed; " +
            (gap.CleanPasses > 1 ? $"only {N(gap.CleanPasses)} passes in a row with no gaps return the plan to the user.\n" : "only a pass with no gaps returns the plan to the user.\n") +
            (confirming
                ? $"- The last pass found no gaps; this pass checks again (clean pass {N(gap.CleanSoFar + 1)} of {N(gap.CleanPasses)}). Review the plan as if seeing it for the first time, and look hardest at {focus}, which an earlier pass may have skimmed; then the rest as usual. Report every gap you find; an empty gaps list is right only when you find none.\n"
                : "") +
            "- Read the plan again from its file. A gap is anything that would make whoever carries the plan out guess, fail or stop to ask: a missing or out-of-order step, a choice left open" + (gap.AskUser ? "" : GapOpenQuestionException) + ", " +
            (web ? "a file, API, specification, version or setting assumed but not read or looked up" : "a file, API or setting assumed but not read") +
            ", a step with no Done when: line, a risk with no answer, no way to verify. Wording and style are not gaps.\n" +
            (web
                ? "- Research what you need with the read-only tools, and on the web for whatever the plan rests on from outside the working directory. " + decision + "\n"
                : "- Research what you need with the read-only tools. " + decision + "\n") +
            "- Then call present_plan with the whole revised plan and gaps, one short line for each gap this pass closed; put a small fix that is not a gap (wording, formatting, a clarification nobody would stop over) in minor instead. " +
            "If you find no gaps, call present_plan with the plan as it stands (or with only minor fixes), gaps empty, and checked: one short line for each part you checked." +
            (reviewer.Length > 0 ? "\n" + reviewer : "");
    }

    /// <summary>The web tools plan mode's research rule names when the turn offers them (2026-10-17), in this order.</summary>
    public static readonly IReadOnlyList<string> ResearchTools = [NeonSidekick.Llm.Tools.WebSearchTool.ToolName, NeonSidekick.Llm.Tools.WebFetchTool.ToolName];

    /// <summary>
    /// Plan mode's web bullet (2026-10-17, the user's ask): the research tools offered by name, what to look up with them rather than
    /// guess, and the sources named in the plan's Context. Pinned.
    /// </summary>
    public static string WebResearchLine(IReadOnlyList<string> research)
    {
        ArgumentNullException.ThrowIfNull(research);
        return "- You can research on the web with " + string.Join(" and ", research) + ": where the plan depends on something outside the working directory " +
            "(a library's API or version, a specification or standard, a service's limits, a setting's meaning), look it up and read the source rather than guess. " +
            "In ## Context, name the pages the plan relies on.";
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
        "The result says whether the user approved, wants changes (with their feedback), or canceled.";

    public const string NotPlanningResult = "Error: plan mode is not on; there is no plan to present";
    public const string MarkdownRequired = "Error: markdown is required: the whole plan as Markdown";

    /// <summary>
    /// <c>present_plan</c>'s answer to a pruned stub sent as <c>markdown</c> (2026-10-18): a compaction stubs the earlier presentations
    /// (<c>ConversationCompactor.StubSupersededPlans</c>), and one copied back would pass the blank check and be saved as the plan. Pinned.
    /// </summary>
    public const string MarkdownStubSent = "Error: markdown is a pruned stub, not the plan; read the plan file again and send the whole plan";
    public const string TitleRequired = "Error: title is required: a short title for the plan";
    public const string AlreadyApprovedResult = "The plan is already approved; it starts when this reply ends. Reply with one short line and call no more tools.";

    public static string ApprovedResult(string path) =>
        $"The user approved the plan in {path}. Plan mode ends with this reply and the plan is carried out in the next turn, with every tool. Reply with one short line and call no more tools.";

    public static string RefineResult(string path, string? feedback) => string.IsNullOrWhiteSpace(feedback)
        ? $"The plan is saved to {path} but the user did not approve it yet. Ask what they would like changed, or wait for their next message."
        : $"The plan is saved to {path} but the user wants changes: \"{Flatten(feedback)}\". Revise the plan and call present_plan again.";

    public static string SavedResult(string path) =>
        $"The plan is saved to {path}. The user reads it and approves it with /plan approve, or sends changes. Summarize the plan in two or three lines and stop.";

    public static string CancelledResult(string path) =>
        $"The user canceled the plan ({path} is kept, marked canceled). Plan mode is off. Acknowledge in one short line and do nothing else.";

    public static string CouldNotSaveResult(string detail) => $"Error: the plan could not be saved: {detail}";

    /// <summary>A gap-closing pass's revision saved (2026-10-13): the pane is not shown, the next pass starts by itself; its minor fixes since 2026-10-17. Pinned.</summary>
    public static string GapPassResult(string path, int revision, int gaps, int minor = 0) =>
        $"Revision {N(revision)} of {path} is saved with {Gaps(gaps)} closed{MinorAnd(minor)}. Reply with one short line and call no more tools; the next review pass starts by itself.";

    /// <summary>A clean pass that does not end the review (2026-10-17): no pane, the streak said, the next pass starts by itself. Pinned.</summary>
    public static string CleanPassResult(string path, int revision, int clean, int needed, int minor = 0) =>
        $"No gaps found in this pass of {path} (revision {N(revision)}){MinorWith(minor)}, {N(clean)} of {CleanPassesWord(needed)} in a row. Reply with one short line and call no more tools; the next review pass starts by itself.";

    /// <summary><c> and 2 minor fixes</c> after a gap count, nothing at 0.</summary>
    private static string MinorAnd(int minor) => minor <= 0 ? "" : minor == 1 ? " and 1 minor fix" : $" and {N(minor)} minor fixes";

    /// <summary><c> with 2 minor fixes</c>, nothing at 0.</summary>
    private static string MinorWith(int minor) => minor <= 0 ? "" : minor == 1 ? " with 1 minor fix" : $" with {N(minor)} minor fixes";

    /// <summary>An empty gaps list during a review with no checked list (2026-10-17): nothing saved, no pass counted. Pinned.</summary>
    public const string CheckedRequiredResult = "Error: with gaps empty, checked is required: one short line for each part of the plan you checked this pass. Call present_plan again with it.";

    public static string CheckedInvalidResult(string raw) => $"Error: checked must be a list of strings, one short line for each part of the plan you checked; got {raw}";

    public static string MinorInvalidResult(string raw) => $"Error: minor must be a list of strings, one short line for each small fix that is not a gap; got {raw}";

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
