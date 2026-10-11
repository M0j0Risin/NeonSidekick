using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Shopping;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>shopping_locate(term, store?)</c> (2026-10-11): what a store knows of a product — where the list would put it and by which fact, the
/// facts that share a word with it (a brand, a category), and the store's reference documents (<see cref="ShoppingList.Locate"/>). Reads only.
/// </summary>
public sealed class ShoppingLocateTool(ShoppingList list) : ShoppingTool(list)
{
    public const string ToolName = "shopping_locate";
    public const string TermArgument = "term";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "term": { "type": "string", "description": "The product to find: its name, its brand or its kind (\"Goya guava nectar\", \"Goya\", \"nectar\")." },
            {{StoreProperty}}
          },
          "required": ["term"]
        }
        """);

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Finds where a product is at a store: the aisle or zone the shopping list would put it in and why, related brands and categories the store knows, and the store's reference documents to look in when nothing places it.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<string> RunAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string term = ToolArguments.ReadString(arguments, TermArgument).Trim();
        if (term.Length == 0)
        {
            return ValueTask.FromResult("Error: " + ShoppingText.NoTerm + ".");
        }

        if (StoreOf(arguments, out string? error) is not { } shop)
        {
            return ValueTask.FromResult(error!);
        }

        return ValueTask.FromResult(ShoppingText.Located(List.Locate(term, shop), List.Store.ReferenceFolder(shop)));
    }
}
