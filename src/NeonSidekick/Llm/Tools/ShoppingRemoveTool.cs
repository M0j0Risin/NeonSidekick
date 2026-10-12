using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Shopping;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>shopping_remove(items)</c> (2026-10-11): lines taken off the shopping list, named as <c>shopping_check</c> names them
/// (<see cref="ShoppingCheckTool.Apply"/>). No pane: a line removed by mistake is added again; every removal is audited.
/// </summary>
public sealed class ShoppingRemoveTool(ShoppingList list) : ShoppingTool(list)
{
    public const string ToolName = "shopping_remove";
    public const string ItemsArgument = "items";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "items": { "type": "array", "items": { "type": "string" }, "description": "The lines to remove, by name or by the number shopping_list shows beside them." }
          },
          "required": ["items"]
        }
        """);

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description => "Removes lines from the user's shopping list (a name written twice goes in both lines).";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<string> RunAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ItemsOf(arguments, ItemsArgument, out string? error) is not { } names
            ? error!
            : ShoppingCheckTool.Apply(List, names, item => List.Remove(item) ? ShoppingText.Removed(item) : null));
}
