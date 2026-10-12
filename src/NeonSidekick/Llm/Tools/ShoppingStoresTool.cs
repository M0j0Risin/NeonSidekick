using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Shopping;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>shopping_stores(store?)</c> (2026-10-11): the stores the list knows, the active one marked, and one store's layout — its locations in
/// walk order with their sections, its sections, how many facts it holds and its reference folder (<see cref="ShoppingText.Layout"/>). Reads only.
/// </summary>
public sealed class ShoppingStoresTool(ShoppingList list) : ShoppingTool(list)
{
    public const string ToolName = "shopping_stores";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { \"store\": { \"type\": \"string\", \"description\": \"The store whose layout to show; leave it out for the active store.\" } } }");

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description => "Lists the stores the shopping list knows and shows one store's layout: its aisles and zones in walking order, its sections and its reference documents.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<string> RunAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var store = List.Store;
        var stores = store.Stores();
        var active = store.ActiveStore();
        var shop = List.StoreFor(ToolArguments.ReadString(arguments, StoreArgument), out string? error);
        if (error is not null)
        {
            return ValueTask.FromResult(error);
        }

        string answer = ShoppingText.Stores(stores, active);
        if (shop is not null)
        {
            answer += "\n" + ShoppingText.Layout(shop, shop.Id == active?.Id, store.Sections(shop.Id), store.Locations(shop.Id), store.Placements(shop.Id).Count, store.ReferenceFolder(shop), List.ReferenceFiles(shop));
        }

        return ValueTask.FromResult(answer);
    }
}
