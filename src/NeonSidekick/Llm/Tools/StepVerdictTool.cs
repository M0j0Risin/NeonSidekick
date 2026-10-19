using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Plans;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The Ralph loop reviewer's way to answer (2026-10-18): <c>step_verdict(verdict pass|fail, reason)</c>, offered on the one-shot review request and
/// never run — its call is read from the response (<see cref="Assistant.SideResponse.Calls"/>, <see cref="RalphReview.FromCall"/>), as the skill
/// learner reads its own. A hand-written <see cref="AIFunction"/> with a schema literal, per the hard rule.
/// </summary>
public sealed class StepVerdictTool : AIFunction
{
    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "verdict": { "type": "string", "enum": ["pass", "fail"], "description": "pass: the step is done as described. fail: it is not." },
            "reason": { "type": "string", "description": "One sentence why." }
          },
          "required": ["verdict", "reason"]
        }
        """);

    public override string Name => RalphReview.ToolName;

    public override string Description => RalphText.VerdictToolDescription;

    public override JsonElement JsonSchema => Schema;

    /// <summary>Never run: the review reads the call.</summary>
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        ValueTask.FromResult<object?>("Recorded.");
}
