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
/// </summary>
public sealed class PresentPlanTool : AIFunction
{
    public const string ToolName = "present_plan";

    public const string TitleArgument = "title";
    public const string NameArgument = "name";
    public const string MarkdownArgument = "markdown";
    public const string GapsArgument = "gaps";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string", "description": "A short title for the plan, a few words." },
            "name": { "type": "string", "description": "A short kebab-case file name saying what the plan does, like add-contributing-guide. Optional: the title is used when it is missing. Ignored after the first presentation, which fixes the file." },
            "markdown": { "type": "string", "description": "The whole plan as Markdown: # title, ## Goal, ## Context, ## Steps (- [ ] checkboxes), ## Files and areas, ## Risks, ## Verification." },
            "gaps": { "type": "array", "items": { "type": "string" }, "description": "Only during a gap review: one short line for each gap this revision closed; empty when none is left." }
          },
          "required": ["title", "markdown"]
        }
        """);

    private readonly PlanSession _session;
    private readonly WorkingDirectory _files;
    private readonly TimeProvider _time;
    private readonly Func<PlanPresentation, CancellationToken, Task<PlanVerdict>> _review;
    private readonly Func<int> _gapLimit;

    /// <param name="review">Shows the saved plan and waits for the user's verdict; the token is the turn's.</param>
    /// <param name="gapLimit">The gap-closing loop's pass limit (0 for none), read when a <c>Close gaps…</c> verdict starts one; null for the default.</param>
    public PresentPlanTool(PlanSession session, WorkingDirectory files, TimeProvider time, Func<PlanPresentation, CancellationToken, Task<PlanVerdict>> review, Func<int>? gapLimit = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _review = review ?? throw new ArgumentNullException(nameof(review));
        _gapLimit = gapLimit ?? (() => Settings.AppSettingsData.DefaultPlanGapPasses);
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

        if (title.Length == 0)
        {
            return PlanText.TitleRequired;
        }

        // A pass of the gap-closing loop under way (2026-10-13); outside one (or once it converged) gaps is ignored.
        var loop = _session.Closing is { Pending: false, Converged: false } running ? running : null;
        IReadOnlyList<string> gaps = [];
        if (loop is not null)
        {
            if (!ToolArguments.TryReadStringList(arguments, GapsArgument, out var sent, out string raw))
            {
                return PlanText.GapsInvalidResult(raw);
            }

            gaps = sent.Select(gap => gap.Trim()).Where(gap => gap.Length > 0).ToList();
        }

        PlanPresentation? saved;
        if (loop is not null && gaps.Count == 0 && Unchanged(title, markdown) is { } same)
        {
            // Nothing left and nothing changed: the revision on disk is the one to approve, no new one written.
            saved = same;
        }
        else
        {
            (saved, string? error) = PlanFiles.Save(_session, _files, _time, title, markdown, Named(arguments));
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
                loop.Record(gaps);
                return PlanText.GapPassResult(path, saved.Revision, gaps.Count);
            }

            loop.MarkConverged();
            saved = saved with { Note = PlanText.ConvergedNote(loop.Pass, loop.Closed) };
        }

        var verdict = await _review(saved, cancellationToken).ConfigureAwait(false);
        switch (verdict.Choice)
        {
            case PlanChoice.CloseGaps:
                _session.StartClosing(verdict.Reviewer ?? PlanReviewer.Model, _gapLimit());
                return PlanText.CloseGapsResult(path);
            case PlanChoice.Approve:
            case PlanChoice.ApproveFresh:
                _session.Approve(verdict.Choice == PlanChoice.ApproveFresh);
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

    /// <summary>The plan on disk as a presentation when <paramref name="markdown"/> would save the same body; else null.</summary>
    private PlanPresentation? Unchanged(string title, string markdown)
    {
        if (_session.Path is not { } path || PlanFiles.ReadWhole(_files, path) is not { } text)
        {
            return null;
        }

        string body = PlanDocument.Body(text);
        return string.Equals(body, PlanDocument.SavedBody(title, markdown), StringComparison.Ordinal)
            ? new PlanPresentation(_session.Title ?? title, path, _session.Revision, body)
            : null;
    }

    private static string? Named(AIFunctionArguments arguments)
    {
        string name = ToolArguments.ReadString(arguments, NameArgument).Trim();
        return name.Length == 0 ? null : name;
    }
}
