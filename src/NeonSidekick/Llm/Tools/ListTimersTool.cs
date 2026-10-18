using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Timers;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>list_timers()</c>: every timer and how long each has left. The only way the model learns
/// what remains — nothing about timers is in the prompt, so a long turn cannot carry a stale
/// figure. Since 2026-10-18 it lists the user's stopwatches too (<c>/timer stopwatch</c>), each with its elapsed time.
/// </summary>
public sealed class ListTimersTool : AIFunction
{
    public const string ToolName = "list_timers";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {}
        }
        """);

    private readonly TimerBoard _board;

    public ListTimersTool(TimerBoard board)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
    }

    public override string Name => ToolName;

    public override string Description =>
        "Every running timer or stopwatch and how long each has left, or for a stopwatch how long it has run. Call it to answer how much time remains on a timer, or which timers exist.";

    public override JsonElement JsonSchema => Schema;

    public string Describe() => TimerText.List(_board.Snapshot());

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new ValueTask<object?>(Describe());
    }
}
