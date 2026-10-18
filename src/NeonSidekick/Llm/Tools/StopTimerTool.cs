using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Timers;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>stop_timer(name)</c>: removes a running timer, or silences one that is ringing. An unknown
/// name answers with the names that do exist, so the model can ask rather than guess. A stopwatch (2026-10-18) stops the same way,
/// its result saying the time it ran.
/// </summary>
public sealed class StopTimerTool : AIFunction
{
    public const string ToolName = "stop_timer";

    public const string NameArgument = "name";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "The timer's name, as it was started." }
          },
          "required": ["name"]
        }
        """);

    private readonly TimerBoard _board;

    public StopTimerTool(TimerBoard board)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
    }

    public override string Name => ToolName;

    public override string Description =>
        "Stops a running timer or stopwatch by name, or silences one that has gone off. Call " + ListTimersTool.ToolName + " first if unsure of the name.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The sentence for a name. Pinned through <see cref="TimerText"/>.</summary>
    public string Describe(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string normalized = TimerText.NormalizeName(name);
        if (normalized.Length == 0)
        {
            return TimerText.NoName;
        }

        return _board.Stop(normalized, out var removed)
            ? TimerText.Stopped(removed)
            : TimerText.NoSuchTimer(normalized, _board.Snapshot());
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new ValueTask<object?>(Describe(ToolArguments.ReadString(arguments, NameArgument)));
    }
}
