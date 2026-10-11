using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Shopping;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>shopping_locate(term, store?)</c> (2026-10-11): what a store knows of a product — where the list would put it and by which fact, the
/// facts that share a word with it (a brand, a category), and the store's reference documents (<see cref="ShoppingList.Locate"/>): with
/// <paramref name="reference"/> (phase 3, the same day) the lines of them that hold its words, the folder read again first when a file
/// in it changed. Reads only.
/// </summary>
public sealed class ShoppingLocateTool(ShoppingList list, ShoppingReference? reference = null) : ShoppingTool(list)
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

    protected override async ValueTask<string> RunAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string term = ToolArguments.ReadString(arguments, TermArgument).Trim();
        if (term.Length == 0)
        {
            return "Error: " + ShoppingText.NoTerm + ".";
        }

        if (StoreOf(arguments, out string? error) is not { } shop)
        {
            return error!;
        }

        if (reference is null)
        {
            return ShoppingText.Located(List.Locate(term, shop), List.Store.ReferenceFolder(shop));
        }

        string? reading = null;
        if (reference.IsStale(shop))
        {
            var summary = await reference.RefreshAsync(shop, force: false, cancellationToken).ConfigureAwait(false);
            reading = summary.Problems.Count > 0 ? " (not read: " + string.Join("; ", summary.Problems) + ")" : null;
        }

        return ShoppingText.Located(List.Locate(term, shop), List.Store.ReferenceFolder(shop), reference.Search(shop, term), reading);
    }
}
