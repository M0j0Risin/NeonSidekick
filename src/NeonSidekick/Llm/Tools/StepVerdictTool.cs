using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Plans;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The Ralph loop reviewer's way to answer (2026-10-18): <c>step_verdict(verdict pass|fail|needs_user, reason)</c> (<c>needs_user</c> 2026-10-21: only a
/// person can tell), offered on the one-shot review request and
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
            "verdict": { "type": "string", "enum": ["pass", "fail", "needs_user"], "description": "pass: the step is done as described. fail: it is not. needs_user: the work you can see is right, but only a person can tell whether the Done when holds (something seen or felt, which no command, output or file could show)." },
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
