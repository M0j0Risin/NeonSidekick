using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Plans;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The model's way out of plan mode (2026-09-26): <c>present_plan(title, markdown, name?)</c> saves the
/// whole plan as <c>.neon/plans/&lt;name&gt;.md</c> under the working directory (<see cref="PlanSlug"/>; the first
/// save picks the path, every revision after overwrites it) with the header the app owns
/// (<see cref="PlanDocument"/>), then asks the user through the seam it is built over — the screen's
/// approval pane, headless's "saved" — and answers with what they chose (<see cref="PlanText"/>). An
/// approval is noted on the <see cref="PlanSession"/>, and the screen carries the plan out when the
/// turn ends: the tool list is the turn's, so the plan cannot start inside the reply that planned it.
/// A cancel marks the file and ends plan mode here. Offered only while planning, whatever the File
/// tools setting says — the write goes through <see cref="WorkingDirectory.WriteText"/>, atomic and
/// inside the sandbox. The <see cref="SaveMemoryTool"/> pattern throughout.
/// <para>The gap-closing loop (2026-10-13, the user's ask, <see cref="PlanGapLoop"/>): a <c>Close gaps…</c> verdict starts
/// one; while a pass runs, <c>gaps</c> lists what the revision closed and the pane is not shown — only a revision with no gaps
/// goes back to the user, with a note saying how the loop ended.</para>
/// <para>Clean passes in a row (2026-10-17, <see cref="PlanGapLoop.CleanPasses"/>): an empty <c>gaps</c> ends the review only when the loop
/// would converge on it; otherwise the pass is marked clean and the next starts by itself. An empty <c>gaps</c> must come with <c>checked</c>,
/// what the pass looked at, and <c>minor</c> lists small fixes that are not gaps, which never break the streak. Every argument is checked
/// before the unchanged test and the save, so a refused call saves and counts nothing. A convergence moves the loop to
/// <see cref="PlanSession.Finished"/>, so the pane it opens can start another review or end plan mode without losing it.</para>
/// </summary>
public sealed class PresentPlanTool : AIFunction, IRefusesPrunedStubs
{
    public const string ToolName = "present_plan";

    public const string TitleArgument = "title";
    public const string NameArgument = "name";
    public const string MarkdownArgument = "markdown";
    public const string GapsArgument = "gaps";
    public const string CheckedArgument = "checked";
    public const string MinorArgument = "minor";
    public const string VerifyArgument = "verify";
    public const string FinalArgument = "final";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string", "description": "A short title for the plan, a few words." },
            "name": { "type": "string", "description": "A short kebab-case file name saying what the plan does, like add-contributing-guide. Optional: the title is used when it is missing. Ignored after the first presentation, which fixes the file." },
            "markdown": { "type": "string", "description": "The whole plan as Markdown, under 20,000 characters: # title, ## Goal, ## Context, ## Steps (- [ ] checkboxes, each small enough for a fresh session to do and check in one sitting, each with an indented Done when: line), ## Files and areas, ## Risks, ## Verification." },
            "gaps": { "type": "array", "items": { "type": "string" }, "description": "Only during a gap review: one short line for each gap this revision closed; empty when none is left." },
            "checked": { "type": "array", "items": { "type": "string" }, "description": "Only during a gap review, with gaps empty: one short line for each part of the plan you checked this pass." },
            "minor": { "type": "array", "items": { "type": "string" }, "description": "Only during a gap review: one short line for each small fix this revision made that is not a gap (wording, formatting, a clarification nobody would stop over)." },
            "verify": { "type": "string", "description": "Optional: a quick command on one line that checks the project (it builds, or its tests pass), run by the app after each step when the plan is carried out step by step. Only a command you have seen this project use (its README, CI file or scripts), such as dotnet build, npm test, cargo test or pytest -q; never a guess. Left out, a revision keeps the plan's; empty clears it." },
            "final": { "type": "string", "description": "Optional: a slower, whole check on one line, run once when every step is done, from the project as verify is. Left out, a revision keeps the plan's; empty clears it." }
          },
          "required": ["title", "markdown"]
        }
        """);

    private readonly PlanSession _session;
    private readonly WorkingDirectory _files;
    private readonly TimeProvider _time;
    private readonly Func<PlanPresentation, CancellationToken, Task<PlanVerdict>> _review;
    private readonly Func<int> _gapLimit;
    private readonly Func<int> _cleanPasses;
    private readonly Func<string, string, string>? _references;
    private readonly Func<int> _inlineLimit;

    /// <param name="review">Shows the saved plan and waits for the user's verdict; the token is the turn's.</param>
    /// <param name="gapLimit">The gap-closing loop's pass limit (0 for none), read when a <c>Close gaps…</c> verdict starts one; null for the default.</param>
    /// <param name="cleanPasses">The clean passes in a row that loop ends on (2026-10-17), read with the limit; null for the default.</param>
    /// <param name="references">
    /// The pictures seam (2026-10-18, <c>PlanReferences</c>): the plan's path and body to the body with its <c>## References</c> reconciled,
    /// the conversation's pictures saved beside the plan first; null saves the body as sent (headless without pictures, the tests).
    /// </param>
    /// <param name="inlineLimit">
    /// The most characters of a plan a Ralph pass sends whole (later on 2026-10-18, <see cref="RalphText.InlineLimit"/> over the session's window), read
    /// after each gap pass's save for <see cref="PlanFit.Shape"/>; null for <see cref="RalphText.InlinePlanChars"/>.
    /// </param>
    public PresentPlanTool(PlanSession session, WorkingDirectory files, TimeProvider time, Func<PlanPresentation, CancellationToken, Task<PlanVerdict>> review, Func<int>? gapLimit = null, Func<int>? cleanPasses = null, Func<string, string, string>? references = null, Func<int>? inlineLimit = null)
    {
        _references = references;
        _inlineLimit = inlineLimit ?? (() => RalphText.InlinePlanChars);
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _review = review ?? throw new ArgumentNullException(nameof(review));
        _gapLimit = gapLimit ?? (() => Settings.AppSettingsData.DefaultPlanGapPasses);
        _cleanPasses = cleanPasses ?? (() => Settings.AppSettingsData.DefaultPlanGapCleanPasses);
    }

    public override string Name => ToolName;

    public override string Description => PlanText.ToolDescription;

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!_session.Active)
        {
            return PlanText.NotPlanningResult;
        }

        if (_session.Approved is not null)
        {
            return PlanText.AlreadyApprovedResult;
        }

        string title = ToolArguments.ReadString(arguments, TitleArgument).Trim();
        string markdown = ToolArguments.ReadString(arguments, MarkdownArgument);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return PlanText.MarkdownRequired;
        }

        // A superseded presentation's stub copied back (2026-10-18, ConversationCompactor.StubSupersededPlans) would be saved as the plan.
        if (ConversationCompactor.IsPrunedStub(markdown))
        {
            return PlanText.MarkdownStubSent;
        }

        if (title.Length == 0)
        {
            return PlanText.TitleRequired;
        }

        // A check runs as the header keeps it, on one line (2026-10-19): one sent on several would be shown as several and run as one.
        var checks = Checks(arguments);
        if (checks is { Verify: { } multiVerify } && IsMultiLine(multiVerify))
        {
            return PlanText.CheckOneLineResult(VerifyArgument);
        }

        if (checks is { Final: { } multiFinal } && IsMultiLine(multiFinal))
        {
            return PlanText.CheckOneLineResult(FinalArgument);
        }

        // A pass of the gap-closing loop under way (2026-10-13); outside one (or once it converged) gaps is ignored.
        var loop = _session.Closing is { Pending: false, Converged: false } running ? running : null;
        IReadOnlyList<string> gaps = [];
        IReadOnlyList<string> @checked = [];
        IReadOnlyList<string> minor = [];
        if (loop is not null)
        {
            // Every argument checked before anything is saved or counted (2026-10-17).
            if (!ToolArguments.TryReadStringList(arguments, GapsArgument, out var sent, out string raw))
            {
                return PlanText.GapsInvalidResult(raw);
            }

            if (!ToolArguments.TryReadStringList(arguments, CheckedArgument, out var looked, out string rawChecked))
            {
                return PlanText.CheckedInvalidResult(rawChecked);
            }

            if (!ToolArguments.TryReadStringList(arguments, MinorArgument, out var fixes, out string rawMinor))
            {
                return PlanText.MinorInvalidResult(rawMinor);
            }

            gaps = Lines(sent);
            @checked = Lines(looked);
            minor = Lines(fixes);
            if (gaps.Count == 0 && @checked.Count == 0)
            {
                // "0 gaps" shows its work (2026-10-17, the user's pick): a clean report says what it checked.
                return PlanText.CheckedRequiredResult;
            }
        }

        PlanPresentation? saved;
        if (loop is not null && gaps.Count == 0 && Unchanged(title, markdown, checks) is { } same)
        {
            // Nothing left and nothing changed: the revision on disk is the one to approve, no new one written.
            saved = same;
        }
        else
        {
            (saved, string? error) = PlanFiles.Save(_session, _files, _time, title, markdown, Named(arguments), checks, _references);
            if (saved is null)
            {
                return PlanText.CouldNotSaveResult(error ?? "");
            }
        }

        string path = saved.Path;
        if (loop is not null)
        {
            if (gaps.Count > 0)
            {
                loop.Record(gaps, minor);
                return PlanText.GapPassResult(path, saved.Revision, gaps.Count, minor.Count, Measured(saved), saved.Markdown.Length);
            }

            if (!loop.WouldConverge)
            {
                // Clean, but the streak is short (2026-10-17): no pane; the next pass starts by itself.
                int clean = loop.CleanCount;
                loop.MarkClean(@checked, minor);
                return PlanText.CleanPassResult(path, saved.Revision, clean, loop.CleanPasses, minor.Count, Measured(saved), saved.Markdown.Length);
            }

            int streak = loop.CleanCount;
            loop.MarkConverged(@checked, minor);
            _session.FinishClosing();
            saved = saved with { Note = PlanText.ConvergedNote(loop.Pass, loop.Closed, streak) };
        }

        var verdict = await _review(saved, cancellationToken).ConfigureAwait(false);
        switch (verdict.Choice)
        {
            case PlanChoice.CloseGaps:
                _session.StartClosing(verdict.Reviewer ?? PlanReviewer.Model, _gapLimit(), _cleanPasses());
                return PlanText.CloseGapsResult(path);
            case PlanChoice.Approve:
            case PlanChoice.ApproveFresh:
            case PlanChoice.ApproveRalph:
                _session.Approve(verdict.Choice);
                return PlanText.ApprovedResult(path);
            case PlanChoice.Cancel:
                PlanFiles.MarkFile(_files, path, PlanStatus.Cancelled, _time.GetLocalNow(), _session.Requirement);
                _session.Exit();
                return PlanText.CancelledResult(path);
            case PlanChoice.Saved:
                return PlanText.SavedResult(path);
            default:
                return PlanText.RefineResult(path, verdict.Feedback);
        }
    }

    /// <summary>
    /// The plan on disk as a presentation when <paramref name="markdown"/> would save the same body and <paramref name="checks"/> change neither of its
    /// commands (2026-10-19: a clean pass that changed only its <c>verify</c> was dropped with the unchanged body); else null.
    /// </summary>
    private PlanPresentation? Unchanged(string title, string markdown, PlanChecks? checks)
    {
        if (_session.Path is not { } path || PlanFiles.ReadWhole(_files, path) is not { } text)
        {
            return null;
        }

        var header = PlanDocument.TryParse(text);
        if (checks is not null && (Changes(checks.Verify, header?.Verify) || Changes(checks.Final, header?.Final)))
        {
            return null;
        }

        string body = PlanDocument.Body(text);
        return string.Equals(body, PlanDocument.SavedBody(title, markdown), StringComparison.Ordinal)
            ? new PlanPresentation(_session.Title ?? title, path, _session.Revision, body)
            : null;
    }

    /// <summary>
    /// What the app measured of the revision a gap pass leaves (later on 2026-10-18, <see cref="PlanFit.Shape"/>): the body the save wrote or the
    /// unchanged one reused, so a clean pass that rewrote nothing is measured as one that did. The convergence's pane shows them on its caption instead.
    /// </summary>
    private IReadOnlyList<string> Measured(PlanPresentation saved) => PlanFit.Shape(saved.Markdown, _inlineLimit());

    /// <summary>A list argument's lines, trimmed, blanks dropped.</summary>
    private static List<string> Lines(IEnumerable<string> sent) => sent.Select(line => line.Trim()).Where(line => line.Length > 0).ToList();

    /// <summary>The <c>verify</c> and <c>final</c> sent (2026-10-18): null for one left out, so a revision keeps the file's.</summary>
    private static PlanChecks? Checks(AIFunctionArguments arguments)
    {
        string? verify = arguments.ContainsKey(VerifyArgument) ? ToolArguments.ReadString(arguments, VerifyArgument).Trim() : null;
        string? final = arguments.ContainsKey(FinalArgument) ? ToolArguments.ReadString(arguments, FinalArgument).Trim() : null;
        return verify is null && final is null ? null : new PlanChecks(verify, final);
    }

    /// <summary>Whether a check sent (null: left out, kept) differs from the file's (null: none).</summary>
    private static bool Changes(string? sent, string? held) =>
        sent is not null && !string.Equals(sent.Length == 0 ? null : sent, held, StringComparison.Ordinal);

    /// <summary>A check with a line break in it.</summary>
    private static bool IsMultiLine(string command) => command.AsSpan().IndexOfAny('\r', '\n') >= 0;

    private static string? Named(AIFunctionArguments arguments)
    {
        string name = ToolArguments.ReadString(arguments, NameArgument).Trim();
        return name.Length == 0 ? null : name;
    }
}
