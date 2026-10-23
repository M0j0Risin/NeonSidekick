using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Plans;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// How a Ralph loop pass ends (2026-10-18, the user's ask: <c>Approve &amp; Ralph loop</c>, <see cref="RalphLoop"/>):
/// <c>finish_step(outcome, summary, verified[], notes?, learned?[], substeps?[])</c> records what the pass did with its step — <c>done</c>,
/// <c>partial</c>, <c>blocked</c> or <c>split</c> — on the running loop, and the app decides after the turn whether the step stands (its own check
/// command, the evidence, a review). Refused, in this order: no pass running, an outcome it does not know, an empty summary, <c>verified</c> empty on
/// <c>done</c>, <c>substeps</c> missing, out of 2–6 or without <c>done_when</c> on a split (or given with another outcome), a split the loop will not
/// take (a child step, one with children, one split before, the final fix, a by-hand step's split with no by-hand substep since 2026-10-21), a pass
/// that reported already. Offered only in a pass (the turn's own
/// slot, <c>TurnToolInputs.Pass</c>), quiet on the transcript (its <c>reported</c> seam writes the 🔁 line), and in no <see cref="PlanTools"/> list.
/// </summary>
public sealed class FinishStepTool : AIFunction
{
    public const string ToolName = "finish_step";

    public const string OutcomeArgument = "outcome";
    public const string SummaryArgument = "summary";
    public const string VerifiedArgument = "verified";
    public const string NotesArgument = "notes";
    public const string LearnedArgument = "learned";
    public const string SubstepsArgument = "substeps";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "outcome": { "type": "string", "enum": ["done", "partial", "blocked", "split"], "description": "done: the step is finished and you checked it. partial: some of it is done and files changed. blocked: you cannot go on without the user. split: the step is too big for one pass." },
            "summary": { "type": "string", "description": "One or two sentences on what this pass did (for blocked: the question for the user)." },
            "verified": { "type": "array", "items": { "type": "string" }, "description": "One short line for each check you ran and what it showed. Required with done." },
            "notes": { "type": "string", "description": "Optional: what the next pass on this step should know." },
            "learned": { "type": "array", "items": { "type": "string" }, "description": "Optional: facts every later pass needs, one short line each (a command that works, what a file is for, a trap)." },
            "substeps": { "type": "array", "items": { "type": "object", "properties": { "text": { "type": "string" }, "done_when": { "type": "string" } }, "required": ["text", "done_when"] }, "description": "Only with split: 2 to 6 sub-steps in order, each with its done_when line. A step whose Done when starts with By hand: keeps By hand: at the start of at least one substep's done_when." }
          },
          "required": ["outcome", "summary"]
        }
        """);

    private readonly Func<RalphLoop?> _loop;
    private readonly Action<FinishReport>? _reported;

    /// <param name="loop">The running loop, or null between loops.</param>
    /// <param name="reported">What the screen shows of a recorded report (its 🔁 line); null for nothing.</param>
    public FinishStepTool(Func<RalphLoop?> loop, Action<FinishReport>? reported = null)
    {
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
        _reported = reported;
    }

    public override string Name => ToolName;

    public override string Description => RalphText.FinishToolDescription;

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return ValueTask.FromResult<object?>(Invoke(arguments));
    }

    private string Invoke(AIFunctionArguments arguments)
    {
        if (_loop() is not { InPass: true } loop)
        {
            return RalphText.NotInPassResult;
        }

        string word = ToolArguments.ReadString(arguments, OutcomeArgument).Trim().ToLowerInvariant();
        RalphOutcome? outcome = word switch
        {
            "done" => RalphOutcome.Done,
            "partial" => RalphOutcome.Partial,
            "blocked" => RalphOutcome.Blocked,
            "split" => RalphOutcome.Split,
            _ => null,
        };
        if (outcome is not { } chosen)
        {
            return RalphText.BadOutcomeResult(word.Length == 0 ? "nothing" : word);
        }

        string summary = ToolArguments.ReadString(arguments, SummaryArgument).Trim();
        if (summary.Length == 0)
        {
            return RalphText.SummaryRequiredResult;
        }

        if (!ToolArguments.TryReadStringList(arguments, VerifiedArgument, out var verifiedSent, out string rawVerified))
        {
            return RalphText.ListInvalidResult(VerifiedArgument, rawVerified);
        }

        var verified = Lines(verifiedSent);
        if (chosen == RalphOutcome.Done && verified.Count == 0)
        {
            return RalphText.VerifiedRequiredResult;
        }

        if (!ToolArguments.TryReadStringList(arguments, LearnedArgument, out var learnedSent, out string rawLearned))
        {
            return RalphText.ListInvalidResult(LearnedArgument, rawLearned);
        }

        if (!ToolArguments.TryReadObjectList(arguments, SubstepsArgument, out var objects, out string rawSubsteps))
        {
            return RalphText.ListInvalidResult(SubstepsArgument, rawSubsteps);
        }

        var substeps = new List<PlanSubstep>(objects.Count);
        if (chosen == RalphOutcome.Split)
        {
            if (objects.Count == 0)
            {
                return RalphText.SubstepsRequiredResult;
            }

            if (objects.Count is < 2 or > 6)
            {
                return RalphText.SubstepsCountResult;
            }

            foreach (var item in objects)
            {
                string text = StringOf(item, "text");
                // The plan's own line shape sent as the words ("Done when: By hand: …", 2026-10-22, a review): the label dropped, so the check reads as sent.
                string doneWhen = PlanSteps.WithoutDoneWhenLabel(StringOf(item, "done_when"));
                if (text.Length == 0 || doneWhen.Length == 0)
                {
                    return RalphText.SubstepDoneWhenResult;
                }

                substeps.Add(new PlanSubstep(text, doneWhen));
            }
        }
        else if (objects.Count > 0)
        {
            return RalphText.SubstepsOnlyOnSplitResult;
        }

        string notes = ToolArguments.ReadString(arguments, NotesArgument).Trim();
        var report = new FinishReport(chosen, summary, verified, notes.Length == 0 ? null : notes, Lines(learnedSent), substeps);
        if (loop.Record(report) is { } refused)
        {
            return refused;
        }

        _reported?.Invoke(report);
        return RalphText.RecordedResult(chosen);
    }

    private static List<string> Lines(IEnumerable<string> sent) => sent.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private static string StringOf(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";
}
