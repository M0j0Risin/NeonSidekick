using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Shopping;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>shopping_add(items, store?)</c> (2026-10-11): lines put on the shopping list, each a name with an optional quantity
/// (<see cref="ShoppingList.ParseLine"/>: <c>2 milk</c>, <c>milk x2</c>); a name already there has its quantity added
/// (<see cref="ShoppingList.Add"/>). Each answer says where the line is at the store, a guess said so, and how to place an unplaced one.
/// </summary>
public sealed class ShoppingAddTool(ShoppingList list) : ShoppingTool(list)
{
    public const string ToolName = "shopping_add";
    public const string ItemsArgument = "items";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "items": { "type": "array", "items": { "type": "string" }, "description": "What to add, one product per entry; a quantity may lead or follow it: \"2 soy milk\", \"eggs x3\"." },
            {{StoreProperty}}
          },
          "required": ["items"]
        }
        """);

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Adds items to the user's shopping list (an item already on it gets the quantity added) and says where each is at the store. " +
        "Add only real products the user asked for; if a name is unclear (two unrelated words, a typo), ask which product they meant first.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<string> RunAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (ItemsOf(arguments, ItemsArgument, out string? error) is not { } names)
        {
            return ValueTask.FromResult(error!);
        }

        var shop = List.StoreFor(ToolArguments.ReadString(arguments, StoreArgument), out error);
        if (error is not null)
        {
            return ValueTask.FromResult(error);
        }

        var added = new List<ShopAdded>();
        foreach (string line in names)
        {
            var (name, quantity) = ShoppingList.ParseLine(line);
            if (List.Add(name, quantity, shop) is not { } a)
            {
                return ValueTask.FromResult("Error: " + ShoppingText.WriteFailed + (added.Count > 0 ? "\n" + ShoppingText.Added(added, shop) : ""));
            }

            added.Add(a);
        }

        return ValueTask.FromResult(ShoppingText.Added(added, shop));
    }
}
