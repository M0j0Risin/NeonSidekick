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
/// </summary>
public sealed class PresentPlanTool : AIFunction
{
    public const string ToolName = "present_plan";

    public const string TitleArgument = "title";
    public const string NameArgument = "name";
    public const string MarkdownArgument = "markdown";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string", "description": "A short title for the plan, a few words." },
            "name": { "type": "string", "description": "A short kebab-case file name saying what the plan does, like add-contributing-guide. Optional: the title is used when it is missing. Ignored after the first presentation, which fixes the file." },
            "markdown": { "type": "string", "description": "The whole plan as Markdown: # title, ## Goal, ## Context, ## Steps (- [ ] checkboxes), ## Files and areas, ## Risks, ## Verification." }
          },
          "required": ["title", "markdown"]
        }
        """);

    private readonly PlanSession _session;
    private readonly WorkingDirectory _files;
    private readonly TimeProvider _time;
    private readonly Func<PlanPresentation, CancellationToken, Task<PlanVerdict>> _review;

    /// <param name="review">Shows the saved plan and waits for the user's verdict; the token is the turn's.</param>
    public PresentPlanTool(PlanSession session, WorkingDirectory files, TimeProvider time, Func<PlanPresentation, CancellationToken, Task<PlanVerdict>> review)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _review = review ?? throw new ArgumentNullException(nameof(review));
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

        var (saved, error) = PlanFiles.Save(_session, _files, _time, title, markdown, Named(arguments));
        if (saved is null)
        {
            return PlanText.CouldNotSaveResult(error ?? "");
        }

        string path = saved.Path;
        var verdict = await _review(saved, cancellationToken).ConfigureAwait(false);
        switch (verdict.Choice)
        {
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

    private static string? Named(AIFunctionArguments arguments)
    {
        string name = ToolArguments.ReadString(arguments, NameArgument).Trim();
        return name.Length == 0 ? null : name;
    }
}
