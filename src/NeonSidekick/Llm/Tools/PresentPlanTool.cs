using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Plans;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The model's way out of plan mode (2026-09-26): <c>present_plan(title, markdown, name?)</c> saves the
/// whole plan as <c>plans/&lt;name&gt;.md</c> under the working directory (<see cref="PlanSlug"/>; the first
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

        var now = _time.GetLocalNow();
        string path = _session.Path ?? PlanSlug.Choose(PlanSlug.From(Named(arguments) ?? title), Taken, now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        int revision = _session.Path is null ? 1 : _session.Revision + 1;
        var header = new PlanHeader(PlanStatus.Draft, revision, _session.Created ?? now, now, _session.Requirement);
        var written = _files.WriteText(path, PlanDocument.Render(header, title, markdown), overwrite: true);
        if (written.Outcome != FileOutcome.Ok)
        {
            return PlanText.CouldNotSaveResult(FileText.Error(written.Outcome, path, "write", written.Detail));
        }

        _session.Presented(path, title, revision, now);
        var verdict = await _review(new PlanPresentation(title, path, revision, PlanDocument.Body(markdown)), cancellationToken).ConfigureAwait(false);
        switch (verdict.Choice)
        {
            case PlanChoice.Approve:
            case PlanChoice.ApproveFresh:
                _session.Approve(verdict.Choice == PlanChoice.ApproveFresh);
                return PlanText.ApprovedResult(path);
            case PlanChoice.Cancel:
                MarkFile(_files, path, PlanStatus.Cancelled, _time.GetLocalNow(), _session.Requirement);
                _session.Exit();
                return PlanText.CancelledResult(path);
            case PlanChoice.Saved:
                return PlanText.SavedResult(path);
            default:
                return PlanText.RefineResult(path, verdict.Feedback);
        }
    }

    /// <summary>
    /// The plan file's status rewritten (<see cref="PlanDocument.WithStatus"/>), the body untouched: approval
    /// and cancellation, from the tool and from <c>/plan</c>. Null when done, else the error sentence.
    /// </summary>
    public static string? MarkFile(WorkingDirectory files, string path, PlanStatus status, DateTimeOffset now, string requirement)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(path);
        if (ReadWhole(files, path) is not { } text)
        {
            return FileText.Missing(path);
        }

        var written = files.WriteText(path, PlanDocument.WithStatus(text, status, now, requirement), overwrite: true);
        return written.Outcome == FileOutcome.Ok ? null : FileText.Error(written.Outcome, path, "write", written.Detail);
    }

    /// <summary>The plan file's whole text, or null when it cannot be read whole.</summary>
    public static string? ReadWhole(WorkingDirectory files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        var read = files.ReadText(path, null, null);
        return read.Outcome == FileOutcome.Ok && !read.Truncated ? read.Text : null;
    }

    private bool Taken(string path) =>
        _files.Resolve(path, forWrite: false, out string full) != FileOutcome.Ok || File.Exists(full) || Directory.Exists(full);

    private static string? Named(AIFunctionArguments arguments)
    {
        string name = ToolArguments.ReadString(arguments, NameArgument).Trim();
        return name.Length == 0 ? null : name;
    }
}
