using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Shopping;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>shopping_list(store?)</c> (2026-10-11): the shopping list walked through a store (the active one by default) — each aisle or zone
/// in the order the store is walked, its lines under it, a guessed place marked, the unplaced and the ticked lines last, likely
/// duplicates named (<see cref="ShoppingText.List"/>). Reads only.
/// </summary>
public sealed class ShoppingListTool(ShoppingList list) : ShoppingTool(list)
{
    public const string ToolName = "shopping_list";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { " + StoreProperty + " } }");

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Shows the user's shopping list, grouped by aisle in the order the store is walked (the active store unless one is named), with ticked lines and likely duplicates.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<string> RunAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string named = ToolArguments.ReadString(arguments, StoreArgument);
        var shop = List.StoreFor(named, out string? error);
        return ValueTask.FromResult(error ?? ShoppingText.List(List.View(shop)));
    }
}
