using System.Globalization;

namespace NeonSidekick.Plans;

/// <summary>
/// The Ralph loop's words (2026-10-18, the user's ask: <c>Approve &amp; Ralph loop</c>, an approved plan carried out one step per fresh conversation,
/// the app deciding whether a step stands; <see cref="RalphLoop"/>): the strip, the pass's directive and message, the transcript line, the session
/// title, the notices, the summary, the approval pane's row and check line, the baseline question, the checkpoint and the reviewer. Pinned by tests
/// where they are a contract. Never a repository-specific command: the examples are generic.
/// </summary>
public static class RalphText
{
    /// <summary>The loop's glyph: the strip, its notices.</summary>
    public const string Glyph = "🔁";

    /// <summary><c>/plan ralph [name] [passes]</c>: run a plan as a Ralph loop, or resume the paused one.</summary>
    public const string RalphWord = "ralph";

    /// <summary><c>/plan approve --ralph [passes]</c>: the presented plan approved and run as a Ralph loop.</summary>
    public const string RalphSwitch = "--ralph";

    /// <summary>The approval pane's row (between Approve, clear context &amp; run and Keep refining), hotkey <c>l</c>.</summary>
    public const string ApproveRalphRow = "Approve & Ralph loop";

    // ── The strip, the transcript and the session ──────────────────────────

    /// <summary>The strip while a loop runs: <c>🔁 step 4/9 · pass 6</c>; the final fix and the final check say so. Pinned.</summary>
    public static string StripText(int step, int steps, int pass, bool final = false) =>
        final ? $"{Glyph} final check · pass {N(pass)}" : $"{Glyph} step {N(step)}/{N(steps)} · pass {N(pass)}";

    /// <summary>The line a pass shows in the transcript (the PC's and the page's): the full message goes only to the model. Pinned.</summary>
    public static string TranscriptLine(int pass, int step, int steps, string text) =>
        $"{Glyph} Pass {N(pass)} · step {N(step)} of {N(steps)}: {Flatten(text)}";

    /// <summary>The final fix pass's transcript line. Pinned.</summary>
    public static string FinalFixLine(int pass) => $"{Glyph} Pass {N(pass)} · the final check: make it pass";

    /// <summary>The words the final fix pass stands for in the summary and the log.</summary>
    public const string FinalFixStep = "make the final check pass";

    /// <summary>A pass's session row title: <c>Ralph: Add a guide · step 4/9 · pass 6</c>. Pinned.</summary>
    public static string SessionTitle(string plan, int step, int steps, int pass) =>
        $"Ralph: {Flatten(plan)} · step {N(step)}/{N(steps)} · pass {N(pass)}";

    /// <summary>The Hints bar while a loop runs (within 119 cells). Pinned.</summary>
    public const string Hint = "Esc stops the loop · typing pauses it · /plan ralph resumes";

    // ── The pass ───────────────────────────────────────────────────────────

    /// <summary>The most characters the pass's directive takes; notes shrink first, then the learned facts.</summary>
    public const int DirectiveBudget = 6000;

    /// <summary>The most characters of the plan a pass's message carries inline.</summary>
    public const int InlinePlanChars = 24_000;

    /// <summary>
    /// The pass's directive, carried by the system prompt (so a compaction cannot lose it): the step, the rules of a pass, what the app checks, the
    /// notes (this step's earlier passes, a failed check's tail) and the learned facts, cut to <see cref="DirectiveBudget"/> — notes first. Without
    /// asking tools (<paramref name="canAsk"/> false), a question goes into <c>finish_step(blocked)</c>. Pinned.
    /// </summary>
    public static string Directive(string path, int step, int steps, string stepText, string? doneWhen, string? verify, bool reviewed, bool canAsk, string? notes, string? learned, bool finalFix = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(stepText);
        string head = finalFix
            ? $"RALPH LOOP PASS: every step of the approved plan in {path} is done, but its final check fails. This pass makes it pass, in a fresh conversation; the failing output is in the message.\n"
            : $"RALPH LOOP PASS: you are carrying out one step of the approved plan in {path}, in a fresh conversation. This pass is step {N(step)} of {N(steps)}: \"{Flatten(stepText)}\"" +
              (doneWhen is { Length: > 0 } d ? $" (done when: {Flatten(d)})" : "") + ".\n";
        string rules =
            "- First check whether the work is already done: search the code before building anything. If it is, verify it and call finish_step with outcome done.\n" +
            (finalFix ? "- Fix what the final check reports and nothing else.\n" : "- Work on this step only; the steps after it come in passes of their own.\n") +
            "- Do not tick or untick boxes in the plan file, nor reword, add or delete its steps: the app does that from finish_step and its own checks.\n" +
            "- Check your work with the project's own checks (its build, its tests) before you report done.\n" +
            "- Do not commit unless the step says to.\n" +
            (finalFix ? "" : "- If the step is too big for one pass, call finish_step with outcome split and 2 to 6 substeps, each with a done_when line, and do none of it.\n") +
            "- Record facts later passes need (a command that works, what a file is for, a trap you found) in finish_step's learned list.\n" +
            (canAsk ? "" : "- Do not ask the user anything and take no photo or screen capture: if you cannot go on without an answer, call finish_step with outcome blocked and the question in summary.\n") +
            (finalFix
                ? "- End the pass by calling finish_step: done (the check passes now), partial (some of it, files changed) or blocked (you cannot go on).\n"
                : "- End the pass by calling finish_step: done (the step is finished and checked), partial (some of it, files changed), blocked (you cannot go on) or split.\n") +
            "- " + (verify is { Length: > 0 } v ? $"After finish_step the app runs `{Flatten(v)}`" + (reviewed ? " and has your work reviewed" : "") + "; the step stands only if that passes." : reviewed ? "After finish_step your work is reviewed; the step stands only if the review passes." : "After finish_step the app looks at what changed.") + "\n";
        string learnedPart = learned is { Length: > 0 } l ? "Learned in earlier passes:\n" + l.Trim() + "\n" : "";
        string notesPart = notes is { Length: > 0 } n ? "Notes for this pass:\n" + n.Trim() + "\n" : "";
        int room = DirectiveBudget - head.Length - rules.Length - learnedPart.Length;
        if (notesPart.Length > Math.Max(0, room))
        {
            notesPart = Cut(notesPart, Math.Max(0, room));
        }

        room = DirectiveBudget - head.Length - rules.Length - notesPart.Length;
        if (learnedPart.Length > Math.Max(0, room))
        {
            learnedPart = Cut(learnedPart, Math.Max(0, room));
        }

        return (head + rules + notesPart + learnedPart).TrimEnd();
    }

    /// <summary>
    /// The pass's message (2026-10-18): the plan inline when it fits the smaller of <see cref="InlinePlanChars"/> and a quarter of the known context
    /// window (<paramref name="windowChars"/>, 0 for unknown), else the step, the plan's headings and where to read the rest. Pinned.
    /// </summary>
    public static string PassMessage(string path, string body, int step, int steps, string stepText, string? doneWhen, int windowChars)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(body);
        string ask = $"Carry out step {N(step)} of {N(steps)} of the plan in {path}: \"{Flatten(stepText)}\"" + (doneWhen is { Length: > 0 } d ? $" (done when: {Flatten(d)})" : "") +
            ". End with finish_step.";
        if (body.Length <= InlineLimit(windowChars))
        {
            return ask + "\n\nThe plan:\n\n" + body.Trim();
        }

        var headings = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.TrimStart().StartsWith('#')).Select(l => "  " + l.Trim()).ToList();
        return ask + "\n\nThe plan is too long to send whole; its headings:\n" + string.Join('\n', headings) + $"\n\nread_file {path} for the rest.";
    }

    /// <summary>
    /// The most characters of a plan a pass sends whole (later on 2026-10-18, out of <see cref="PassMessage"/> for <see cref="PlanFit"/>): the smaller of
    /// <see cref="InlinePlanChars"/> and a quarter of the known context window in characters, <see cref="InlinePlanChars"/> when it is unknown (0).
    /// </summary>
    public static int InlineLimit(int windowChars) => windowChars > 0 ? Math.Min(InlinePlanChars, windowChars / 4) : InlinePlanChars;

    /// <summary>The final fix pass's message, the failing check's tail inline. Pinned.</summary>
    public static string FinalFixMessage(string path, string command, string tail) =>
        $"Every step of the plan in {path} is done, but the final check `{Flatten(command)}` fails. Make it pass, then call finish_step. Its output ends:\n\n" + Fence(tail);

    /// <summary>A failed check's tail as the next pass's note. Pinned.</summary>
    public static string CheckFailedNote(string command, string tail) => $"The last pass's check `{Flatten(command)}` failed; its output ends:\n" + Fence(tail);

    /// <summary>A checkpoint's Redo as the next pass's note. Pinned.</summary>
    public static string RedoNote(string note) => "The user looked at the last pass's work and wants it redone: \"" + Flatten(note) + "\"";

    /// <summary>A failed review as the next pass's note. Pinned.</summary>
    public static string ReviewFailedNote(string reason) => "A review of the last pass found the step not done: " + Flatten(reason);

    // ── The approval pane and the start ────────────────────────────────────

    /// <summary>
    /// The pane's check line (2026-10-18, <see cref="PlanPresentation.Checks"/>): the commands, the reviewer and the budget; <c>unchecked</c> with
    /// neither command; with <c>Shell command policy</c> at ask, the warning that an unattended run stalls on its asks. Pinned. Since later on
    /// 2026-10-18 <paramref name="problems"/> (<see cref="PlanFit.Shape"/>'s, never the no-check item: the line says <c>unchecked</c> and the reviewer
    /// already) follow, one part each, so the pane shows them before its row is picked; none, the line as before.
    /// </summary>
    public static string PaneCheckLine(PlanChecks? checks, RalphReviewer reviewer, long budget, bool policyAsks, IReadOnlyList<string>? problems = null)
    {
        var parts = new List<string>();
        if (checks is { Verify.Length: > 0 } v)
        {
            parts.Add($"verify `{Flatten(v.Verify!)}`");
        }

        if (checks is { Final.Length: > 0 } f)
        {
            parts.Add($"final `{Flatten(f.Final!)}`");
        }

        if (parts.Count == 0)
        {
            parts.Add("unchecked");
        }

        parts.Add("reviewer " + RalphReviewerMode.Name(reviewer));
        if (budget > 0)
        {
            parts.Add("budget " + App.UsageText.CompactNumber(budget));
        }

        return string.Join(" · ", parts) + (policyAsks ? " · " + PolicyAsksWarning : "") + (problems is { Count: > 0 } p ? " · " + string.Join(" · ", p) : "");
    }

    /// <summary>The check line's tail under <c>Shell command policy</c> ask. Pinned.</summary>
    public const string PolicyAsksWarning = "shell policy ask: an unattended loop waits on each command";

    /// <summary>The baseline run's busy label.</summary>
    public const string BaselineLabel = "checking the project before the loop";

    /// <summary>A check's busy label while it runs.</summary>
    public static string CheckLabel(string command) => "checking: " + Cut(Flatten(command), 60);

    /// <summary>The baseline failed: its question, on the PC and the page. Pinned.</summary>
    public static string BaselineQuestion(string command) =>
        $"The check `{Flatten(command)}` fails before the loop starts. Start anyway? The steps then go unchecked and it runs once at the end.";

    /// <summary>The baseline failed headless, where nobody can be asked. Pinned.</summary>
    public static string BaselineRefusedHeadless(string command) =>
        $"The check `{Flatten(command)}` fails before the loop starts, and headless cannot ask: fix the project, or take the verify line out of the plan's header.";

    /// <summary>
    /// Headless, before the baseline (2026-10-19, the review: the plan's commands ran unannounced): the checks the loop will run, as the plan's header
    /// names them. Pinned.
    /// </summary>
    public static string HeadlessChecksNotice(string? verify, string? final) =>
        $"{Glyph} The loop's checks: verify {(verify is null ? "none" : "`" + Flatten(verify) + "`")}, final {(final is null ? "none" : "`" + Flatten(final) + "`")}.";

    /// <summary>
    /// Headless under <c>Shell command policy</c> ask (2026-10-19, the review): a check not on the allow list is refused, as <c>run_command</c>'s
    /// would be, since nobody can be asked. Pinned.
    /// </summary>
    public static string HeadlessAskRefusedError(string command) =>
        $"The check `{Flatten(command)}` is not on the shell allow list, and headless cannot ask: allow it, or run with NEONSIDEKICK_COMMAND_POLICY=yolo.";

    /// <summary>
    /// A check refused when it was about to run (2026-10-19, the review: the police and the policy are asked again before every run, since a pass may
    /// rewrite the script a command runs): the loop stops. Pinned.
    /// </summary>
    public static string CheckRefusedError(string command, string reason) =>
        $"The check `{Flatten(command)}` was refused: {reason.TrimEnd('.')}. The Ralph loop stops.";

    /// <summary>The policy turned <c>off</c> while a loop ran (2026-10-19): its next check is refused and the loop stops. Pinned.</summary>
    public const string PolicyOffNowReason = "Shell command policy is off now";

    /// <summary>
    /// A loop started under <c>Shell command policy</c> off (2026-10-19, the second review: the screen and headless each built it inline, and it
    /// read "…ask or yolo.: the steps go unchecked."): no check runs this run, and a resume under ask or yolo runs the plan's again. Pinned.
    /// </summary>
    public const string PolicyOffUncheckedNotice =
        Glyph + " Shell command policy is off, so the steps go unchecked this run; a resume with it on runs the plan's checks. /tools shell sets it to ask or yolo.";

    /// <summary>The 🔁 line a recorded <c>finish_step</c> writes: its outcome and summary, cut at 200 characters. Pinned.</summary>
    public static string FinishStepLine(RalphOutcome outcome, string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return $"{Glyph} finish_step: {outcome.ToString().ToLowerInvariant()} — {(summary.Length <= 200 ? summary : summary[..199] + "…")}";
    }

    /// <summary>The prefix of the error a failed Ralph confirm pane writes (the pane's own message after it).</summary>
    public const string ConfirmPaneFailed = "The Ralph loop's confirm pane failed: ";

    /// <summary>The prefix of the error a failed Ralph checkpoint pane writes (the pane's own message after it).</summary>
    public const string CheckpointPaneFailed = "The Ralph checkpoint pane failed: ";

    /// <summary>
    /// A pass or its judgment threw (2026-10-19, the second review: the loop was left running with its pass "under way", its lock held and the
    /// asking tools withheld from every turn after): the loop stops, the plan paused. Pinned.
    /// </summary>
    public static string LoopFailedError(string reason) =>
        $"{Glyph} The Ralph loop failed: {Flatten(reason).TrimEnd('.')}. It stops here; /plan ralph resumes it.";

    /// <summary>
    /// <c>/plan ralph</c> on a paused loop started in another folder (2026-10-19, the review: its approved commands would have run in this one).
    /// Pinned.
    /// </summary>
    public static string ResumeElsewhereError(string path, string root) =>
        $"The paused Ralph loop on {path} ran in {root}, not the working directory now: go back there to resume it, or /plan ralph <name> to start this folder's plan anew";

    /// <summary>The baseline failed and the user would not start: the plan stays as it was. Pinned.</summary>
    public const string BaselineRefusedNotice = Glyph + " Not started: the plan stays as it was. Fix the check, or approve it again.";

    // ── The fit (later on 2026-10-18, PlanFit) ──────────────────────────────

    /// <summary>A plan whose one step is long: a single pass gets the whole job. Pinned.</summary>
    public const string FitOneStep = "1 step: one pass does the whole plan";

    /// <summary>Unticked steps with no <c>Done when:</c> line. Pinned, both forms.</summary>
    public static string FitNoDoneWhen(int count) => count == 1 ? "1 step has no Done when line" : $"{Count(count)} steps have no Done when line";

    /// <summary>A plan longer than a pass sends whole (<see cref="InlineLimit"/>). Pinned.</summary>
    public static string FitTooLong(int chars, int limit) => $"the plan is {Count(chars)} characters; a pass sends it whole only up to {Count(limit)}";

    /// <summary>Neither <c>verify</c> nor <c>final</c>, and the reviewer off. Pinned.</summary>
    public const string FitNoCheck = "no check: steps stand unchecked";

    /// <summary>
    /// The question before a loop starts on a plan with problems (the pane's row, <c>/plan approve --ralph</c>), on the PC and the page: every problem,
    /// the no-check one too, since this question shows no check line of its own. Pinned.
    /// </summary>
    public static string FitQuestion(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return $"This plan may not suit a Ralph loop: {string.Join("; ", problems)}. Start anyway?";
    }

    /// <summary>The problems folded into <c>/plan ralph &lt;name&gt;</c>'s one confirm, after its check line (<see cref="ConfirmQuestion"/>). Pinned.</summary>
    public static string FitSentence(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return $"It may not suit a Ralph loop: {string.Join("; ", problems)}.";
    }

    /// <summary>Headless, where nobody can be asked: said before the start notice, and the loop goes on. Pinned.</summary>
    public static string FitNotice(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return $"{Glyph} This plan may not suit a Ralph loop: {string.Join("; ", problems)}. Starting it anyway.";
    }

    /// <summary>
    /// The fit question answered no: nothing started, the plan still presented. The typed form asks the same question again, so the notice says
    /// to answer yes there rather than promising it runs as it is. Pinned.
    /// </summary>
    public const string FitDeclinedNotice = Glyph + " Not started: revise the plan (/plan gaps tightens it), or run /plan approve --ralph and say yes to start it as it is.";

    /// <summary>The loop began. Pinned.</summary>
    public static string StartedNotice(string path, int steps, int cap, bool finalOnly) =>
        $"{Glyph} Ralph loop on {path}: {Steps(steps)} to go, at most {Passes(cap)}" + (finalOnly ? "; checked once at the end." : ".");

    /// <summary>A resume. Pinned.</summary>
    public static string ResumedNotice(string path, int steps, int cap) => $"{Glyph} Ralph loop resumed on {path}: {Steps(steps)} to go, at most {Passes(cap)}.";

    // ── Refusals ───────────────────────────────────────────────────────────

    public const string NoStepsError = "The plan has no steps to run one at a time: a Ralph loop needs - [ ] checkboxes under ## Steps";
    public const string NeedsToolsError = "A Ralph loop needs the model's tools: switch LLM offer tools on (/settings) and try again";
    public const string UnderAuditError = "A code audit is running: a Ralph loop waits until it ends";
    public const string UnderBotChatError = "A /botchat is running: a Ralph loop waits until it ends";
    public const string WhilePlanningError = "While planning, the plan being planned goes through its pane's Ralph row or /plan approve --ralph";
    public const string NothingPausedNotice = Glyph + " No Ralph loop is paused: /plan ralph <name> runs one.";
    public static string LockedError(string path) => $"Another NeonSidekick is running {path} as a Ralph loop; wait for it to end";
    public static string NotRunnableError(string path, PlanStatus status) => $"{path} is {PlanDocument.Word(status)}: a Ralph loop runs a plan that is not done or canceled";
    public static string BadPassesError(string word) =>
        $"'{word}' passes: a Ralph loop takes {N(Settings.AppSettingsData.MinRalphLoopPasses)} to {N(Settings.AppSettingsData.MaxRalphLoopPasses)}, 0 for three a step";

    /// <summary>
    /// <c>/plan ralph &lt;name&gt;</c>'s confirm on a plan not yet approved (a draft after <c>/plan open</c>). Pinned. <paramref name="problems"/> (later on
    /// 2026-10-18) follow as <see cref="FitSentence"/>, so one question covers both; none, as before.
    /// </summary>
    public static string ConfirmDraftQuestion(string path, string checks, IReadOnlyList<string>? problems = null) => $"Approve {path} and run it as a Ralph loop? {checks}" + FitPart(problems);

    /// <summary><c>/plan ralph &lt;name&gt;</c>'s confirm on an approved or incomplete plan, <paramref name="problems"/> as the draft's. Pinned.</summary>
    public static string ConfirmQuestion(string path, string checks, IReadOnlyList<string>? problems = null) => $"Run {path} as a Ralph loop? {checks}" + FitPart(problems);

    private static string FitPart(IReadOnlyList<string>? problems) => problems is { Count: > 0 } p ? ". " + FitSentence(p) : "";

    /// <summary><c>/plan open</c> on a paused Ralph plan: the loop is left, its log kept. Pinned.</summary>
    public static string LeftOnOpenNotice(string path) =>
        $"{Glyph} The Ralph loop on {path} is left; its log is kept, and /plan ralph {System.IO.Path.GetFileNameWithoutExtension(path)} runs it again.";

    /// <summary>The approval interrupted under a Ralph choice. Pinned.</summary>
    public const string ApprovalInterruptedNotice = Glyph + " The plan was approved but the reply was stopped: /plan approve --ralph starts the loop.";

    // ── A pass's notices ───────────────────────────────────────────────────

    public static string StepDoneNotice(int step, int steps, string text) => $"{Glyph} Step {N(step)} of {N(steps)} stands: {Flatten(text)}";
    public static string StepUncheckedNotice(int step, int steps, string text) => $"{Glyph} Step {N(step)} of {N(steps)} ticked, unchecked: {Flatten(text)}";
    public static string CheckFailedNotice(string command) => $"{Glyph} The check `{Flatten(command)}` failed: the step is unticked, and the next pass gets its output.";
    public static string CheckTimedOutNotice(string command, int seconds) => $"{Glyph} The check `{Flatten(command)}` ran past {N(seconds)} s and was stopped: the step is unticked.";
    public const string CheckStoppedNotice = Glyph + " The check was stopped: the step is unticked.";
    public static string ReviewFailedNotice(string reason) => $"{Glyph} The review found the step not done: {Flatten(reason)}";
    public const string ReviewUnclearNotice = Glyph + " The review gave no clear verdict: the step stands, unchecked.";
    public static string SplitNotice(int count, string text) => $"{Glyph} Split into {N(count)} sub-steps: {Flatten(text)}";
    public static string PartialNotice(int changed) => $"{Glyph} Part of the step done ({Files(changed)} changed); the next pass goes on.";
    public const string NothingChangedNotice = Glyph + " The pass reported done but changed no file.";
    public static string WordsPutBackNotice(int count) => $"{Glyph} The pass reworded {(count == 1 ? "a step" : N(count) + " steps")} of the plan: put back.";
    public static string StepsRemovedNotice(IReadOnlyList<string> steps) =>
        $"{Glyph} The pass deleted {(steps.Count == 1 ? "an unfinished step" : N(steps.Count) + " unfinished steps")} from the plan: " + string.Join("; ", steps.Take(5).Select(Flatten)) + (steps.Count > 5 ? $" … and {N(steps.Count - 5)} more" : "");
    public static string InPassPutBackNotice(string text) => $"{Glyph} The last run ended in the middle of a pass on \"{Flatten(text)}\": its box is put back, unchecked.";
    public static string BoxesPutBackNotice(int count) => $"{Glyph} The pass changed {(count == 1 ? "another step's box" : N(count) + " other steps' boxes")}: put back.";
    public static string ParentTickedNotice(string text) => $"{Glyph} Every part done: {Flatten(text)}";
    public const string RetryNotice = Glyph + " The pass failed: trying the step once more.";
    public const string PicturesDroppedNotice = Glyph + " The retry goes without the plan's pictures.";
    public static string FinalFailedOnceNotice(string command) => $"{Glyph} The final check `{Flatten(command)}` fails: one pass to fix it.";
    public static string BlockedNotice(string summary) => $"{Glyph} The pass is blocked: {Flatten(summary)}";

    // ── The stop ───────────────────────────────────────────────────────────

    /// <summary>Why the loop stopped, said once. Pinned.</summary>
    public static string StopNotice(RalphStop stop, string path, int done, int total)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        string resume = $" /plan ralph{(stop == RalphStop.Paused ? "" : " " + name)} goes on.";
        return stop switch
        {
            RalphStop.Done => $"{Glyph} Ralph loop done: every step of {path} stands.",
            RalphStop.Blocked => $"{Glyph} Ralph loop stopped: a step is blocked ({N(done)} of {N(total)} steps done)." + resume,
            RalphStop.Stalled => $"{Glyph} Ralph loop stopped: two passes in a row made no progress ({N(done)} of {N(total)} steps done)." + resume,
            RalphStop.StepFailing => $"{Glyph} Ralph loop stopped: a step keeps failing its checks ({N(done)} of {N(total)} steps done); its edits are left in place." + resume,
            RalphStop.Limit => $"{Glyph} Ralph loop stopped at its pass limit ({N(done)} of {N(total)} steps done)." + resume,
            RalphStop.Budget => $"{Glyph} Ralph loop stopped: its token budget is spent ({N(done)} of {N(total)} steps done)." + resume,
            RalphStop.Stopped => $"{Glyph} Ralph loop stopped ({N(done)} of {N(total)} steps done)." + resume,
            RalphStop.Paused => $"{Glyph} Ralph loop paused for your message ({N(done)} of {N(total)} steps done)." + resume,
            RalphStop.FinalFailed => $"{Glyph} Ralph loop stopped: the final check still fails after its fix pass." + resume,
            RalphStop.PlanGone => $"{Glyph} Ralph loop stopped: {path} is gone or cannot be read whole.",
            RalphStop.WorkdirMoved => $"{Glyph} Ralph loop stopped: the working directory changed." + resume,
            RalphStop.Locked => $"{Glyph} Ralph loop not started: another NeonSidekick is running {path}.",
            RalphStop.UserStopped => $"{Glyph} Ralph loop stopped at the checkpoint ({N(done)} of {N(total)} steps done)." + resume,
            RalphStop.StepsRemoved => $"{Glyph} Ralph loop stopped: the pass deleted unfinished steps from {path} ({N(done)} of {N(total)} steps done); put them back or edit the plan, then" + resume,
            _ => $"{Glyph} Ralph loop not started.",
        };
    }

    /// <summary>A step that kept failing: the files its passes changed, left in place. Pinned.</summary>
    public static string LeftChangedLine(IReadOnlyList<string> files) => "  edits left in place: " + string.Join(", ", files.Take(12)) + (files.Count > 12 ? $" … and {N(files.Count - 12)} more" : "");

    /// <summary>The summary said at every stop with a pass recorded: a heading with the totals, a line a pass. Pinned.</summary>
    public static IReadOnlyList<string> SummaryLines(IReadOnlyList<RalphPassRecord> history, RalphStop stop)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0)
        {
            return [];
        }

        var timed = history.Where(p => p.Elapsed is not null).ToList();
        string totals = timed.Count == 0 ? "" : ", " + UI.ThinkingFoldText.Duration(timed.Aggregate(TimeSpan.Zero, (sum, p) => sum + p.Elapsed!.Value)) + TokensPart(timed.Sum(p => (p.Tokens ?? 0) + p.ReviewTokens));
        var lines = new List<string>(history.Count + 1) { $"{Glyph} Ralph loop, {Passes(history.Count)}{totals}:" };
        for (int i = 0; i < history.Count; i++)
        {
            var p = history[i];
            string which = p.Step > 0 ? $"step {N(p.Step)}" : "final fix";
            string line = $"  pass {N(p.Pass)}: {which}, {p.Outcome}" + (i == history.Count - 1 ? ", " + StopWord(stop) : "");
            if (p.Elapsed is { } elapsed)
            {
                line += " · " + UI.ThinkingFoldText.Duration(elapsed) + TokensPart((p.Tokens ?? 0) + p.ReviewTokens, " · ");
            }

            lines.Add(line);
        }

        return lines;
    }

    private static string StopWord(RalphStop stop) => stop switch
    {
        RalphStop.Done => "done",
        RalphStop.Blocked => "blocked",
        RalphStop.Stalled => "stalled",
        RalphStop.StepFailing => "step failing",
        RalphStop.Limit => "limit",
        RalphStop.Budget => "budget",
        RalphStop.Paused => "paused",
        RalphStop.FinalFailed => "final failed",
        RalphStop.UserStopped => "stopped at the checkpoint",
        RalphStop.StepsRemoved => "steps removed",
        _ => "stopped",
    };

    /// <summary><c>/plan show</c> outside plan mode while a loop runs or is paused: where it stands. Pinned.</summary>
    public static IReadOnlyList<string> ShowLines(string path, int done, int total, int pass, bool paused, string log) =>
    [
        $"{Glyph} Ralph loop {(paused ? "paused" : "running")} on {path}: {N(done)} of {N(total)} steps done, pass {N(pass)}",
        "  Log: " + log + (paused ? " — /plan ralph resumes" : ""),
    ];

    /// <summary>The picker's footer line for a plan with a Ralph log: its last pass, where it stood and how it stopped. Pinned.</summary>
    public static string PickerLine(int pass, int done, int total, string? stopped) =>
        $"{Glyph} Ralph: pass {N(pass)}, step {N(done)}/{N(total)}" + (stopped is { Length: > 0 } s ? ", " + s : "");

    // ── finish_step ────────────────────────────────────────────────────────

    public const string FinishToolDescription =
        "Ends a Ralph loop pass: says what the pass did with its step. Call it once, last. outcome is done (the step is finished and you checked it), " +
        "partial (some of it is done and files changed; the next pass goes on), blocked (you cannot go on without the user; put the question in summary) " +
        "or split (the step is too big for one pass: give 2 to 6 substeps, each with text and done_when, and do none of the work). The app then ticks the step " +
        "or not from its own checks.";

    public const string NotInPassResult = "Error: no Ralph loop pass is running; finish_step is for a pass";
    public const string AlreadyFinishedResult = "Error: this pass already called finish_step; reply with one short line and call no more tools";
    public const string SplitFinalResult = "Error: the final check's fix cannot be split; fix what it reports and call finish_step with done, partial or blocked";
    public const string SplitRefusedResult = "Error: only a top-level step with no sub-steps of its own can be split, once; work this step, or report partial or blocked";
    public static string BadOutcomeResult(string raw) => $"Error: outcome must be done, partial, blocked or split; got {raw}";
    public const string SummaryRequiredResult = "Error: summary is required: one or two sentences on what this pass did";
    public const string VerifiedRequiredResult = "Error: with outcome done, verified is required: one short line for each check you ran and what it showed";
    public static string ListInvalidResult(string name, string raw) => $"Error: {name} must be a list of strings; got {raw}";
    public const string SubstepsRequiredResult = "Error: with outcome split, substeps is required: 2 to 6 objects, each with text and done_when";
    public const string SubstepsCountResult = "Error: a split takes 2 to 6 substeps";
    public const string SubstepDoneWhenResult = "Error: every substep needs text and done_when: how to tell it is done";
    public const string SubstepsOnlyOnSplitResult = "Error: substeps go only with outcome split";

    /// <summary>finish_step's answer once recorded. Pinned.</summary>
    public static string RecordedResult(RalphOutcome outcome) => outcome switch
    {
        RalphOutcome.Split => "Recorded: the step will be split. Reply with one short line and call no more tools; the next pass starts by itself.",
        RalphOutcome.Blocked => "Recorded: the step is blocked; the loop stops for the user. Reply with one short line and call no more tools.",
        _ => "Recorded. Reply with one short line and call no more tools; the app checks the step and the next pass starts by itself.",
    };

    // ── The checkpoint ─────────────────────────────────────────────────────

    public const string CheckpointTitle = Glyph + " Checkpoint: keep this step?";
    public static readonly IReadOnlyList<string> CheckpointRows = ["Keep", "Redo with a note…", "View the changes", "Stop the loop"];
    public const string CheckpointKeys = "k / r / v / s = pick · Enter = choose · Esc = keep";
    public const string RedoPrompt = "What should the next pass do differently? Type it below.";
    public static string CheckpointCaption(int step, int steps, string text, string summary) => $"Step {N(step)} of {N(steps)}: {Flatten(text)} — {Flatten(summary)}";
    public const string NoChangesLine = "(no file changed)";

    // ── The reviewer ───────────────────────────────────────────────────────

    public const string ReviewerSystem =
        "You review one step of a plan another model carried out. You are given the step, how to tell it is done, what the model reported, what the app " +
        "measured and the changes it made. Judge only whether this step is done as described; do not redo the work. Call step_verdict with pass or fail and " +
        "one sentence why; with no tool, end your answer with a line reading verdict: pass or verdict: fail.";

    public const string VerdictToolDescription = "Gives your verdict on the step: pass (it is done as described) or fail (it is not), and one sentence why.";

    /// <summary>The review's message. Pinned.</summary>
    public static string ReviewRequest(string stepText, string? doneWhen, string summary, IReadOnlyList<string> verified, IReadOnlyList<string> flags, IReadOnlyList<string> changed, string diff)
    {
        var text = new System.Text.StringBuilder();
        text.Append("The step: ").Append(Flatten(stepText)).Append('\n');
        text.Append("Done when: ").Append(doneWhen is { Length: > 0 } d ? Flatten(d) : "(not said)").Append('\n');
        text.Append("The model's summary: ").Append(Flatten(summary)).Append('\n');
        text.Append("What it checked: ").Append(verified.Count == 0 ? "(nothing)" : string.Join("; ", verified.Select(Flatten))).Append('\n');
        if (flags.Count > 0)
        {
            text.Append("The app noticed: ").Append(string.Join("; ", flags)).Append('\n');
        }

        text.Append("Files changed: ").Append(changed.Count == 0 ? "(none)" : string.Join(", ", changed)).Append("\n\nThe changes:\n");
        text.Append(diff.Length == 0 ? NoChangesLine : Fence(diff));
        return text.ToString();
    }

    public const string FlagNothingChanged = "no file changed";
    public static string FlagNamedMissing(IReadOnlyList<string> paths) => "files the step names are missing: " + string.Join(", ", paths);
    public const string FlagTestsTouched = "test files were changed";

    /// <summary>A secret-looking file's line in a pass's diff, in place of its text.</summary>
    public static string SecretNotDiffed(string path) => $"({path} looks like a secrets file: changed, not shown)";

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary><paramref name="text"/> in a code fence long enough to hold it.</summary>
    public static string Fence(string text)
    {
        string fence = "```";
        while (text.Contains(fence, StringComparison.Ordinal))
        {
            fence += "`";
        }

        return fence + "\n" + text.TrimEnd() + "\n" + fence;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : max <= 1 ? "" : text[..(max - 1)] + "…";

    private static string Steps(int steps) => steps == 1 ? "1 step" : $"{N(steps)} steps";

    private static string Passes(int passes) => passes == 1 ? "1 pass" : $"{N(passes)} passes";

    private static string Files(int files) => files == 1 ? "1 file" : $"{N(files)} files";

    private static string TokensPart(long tokens, string lead = ", ") => tokens <= 0 ? "" : lead + App.UsageText.Compact(tokens);

    private static string Flatten(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Replace('"', '\'');

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A count with thousands separators (<c>170,298</c>), invariant.</summary>
    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
