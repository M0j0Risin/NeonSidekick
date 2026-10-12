using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Shopping;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>shopping_recent(limit?)</c> (2026-10-11, the user's ask with the pane's recently bought): the lines bought before and not on the list
/// now, the most often first (<see cref="ShoppingList.Bought"/>): how many times each, its last quantity and when last. A line is
/// recorded as bought when it leaves the list ticked. Reads only.
/// </summary>
public sealed class ShoppingRecentTool(ShoppingList list) : ShoppingTool(list)
{
    public const string ToolName = "shopping_recent";
    public const string LimitArgument = "limit";

    /// <summary>The lines listed when the call names no limit, and the most it may name.</summary>
    public const int DefaultLimit = 25;
    public const int MaxLimit = 200;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "limit": { "type": "integer", "description": "The most lines to list (25 if left out)." }
          }
        }
        """);

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Lists what the user bought before and is not on the shopping list now, the most often first, with how many times and when last: for \"what do I usually get?\" or a list to suggest from. Add only what the user agrees to.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<string> RunAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryReadInt32(arguments, LimitArgument, out int? limit, out string raw) || limit is < 1)
        {
            return ValueTask.FromResult("Error: limit must be a whole number from 1, not " + raw + ".");
        }

        var bought = List.Bought();
        return ValueTask.FromResult(ShoppingText.Recent(bought.Take(Math.Min(limit ?? DefaultLimit, MaxLimit)).ToList(), bought.Count));
    }
}
