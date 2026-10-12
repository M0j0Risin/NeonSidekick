using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Shopping;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>shopping_layout(action, name?, to?, section?, walk?, notes?, store?)</c> (2026-10-11): a store's layout changed — a store added, made
/// the active one or renamed; a section or a location (an aisle or zone) added, renamed or removed; a location's place on the walk set.
/// Removing a whole store is the user's own (<c>/shopping</c>). Every change is audited.
/// </summary>
public sealed class ShoppingLayoutTool(ShoppingList list) : ShoppingTool(list)
{
    public const string ToolName = "shopping_layout";
    public const string ActionArgument = "action";
    public const string NameArgument = "name";
    public const string ToArgument = "to";
    public const string SectionArgument = "section";
    public const string WalkArgument = "walk";
    public const string NotesArgument = "notes";

    /// <summary>The actions, in the schema's order. Pinned.</summary>
    public static readonly IReadOnlyList<string> Actions =
    [
        "add_store", "use_store", "rename_store",
        "add_section", "rename_section", "remove_section",
        "add_location", "rename_location", "set_walk", "remove_location",
    ];

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["add_store", "use_store", "rename_store", "add_section", "rename_section", "remove_section", "add_location", "rename_location", "set_walk", "remove_location"], "description": "What to change." },
            "name": { "type": "string", "description": "The store (add_store, use_store, rename_store), section or location (\"Aisle 7\", \"Dairy\") it acts on." },
            "to": { "type": "string", "description": "The new name, for a rename." },
            "section": { "type": "string", "description": "add_location: the section the location belongs to (made if missing)." },
            "walk": { "type": "integer", "description": "add_location, set_walk: its place in the order the store is walked (1 first); leave it out to order by name, numbered aisles by number." },
            "notes": { "type": "string", "description": "add_store: anything to remember about the store (its address, its hours)." },
            {{StoreProperty}}
          },
          "required": ["action", "name"]
        }
        """);

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Changes the stores and their layouts for the shopping list: add a store or make it the active one, add, rename or remove a section or an aisle/zone, set the order a store is walked in.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<string> RunAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Run(arguments));

    private string Run(AIFunctionArguments arguments)
    {
        string action = ToolArguments.ReadString(arguments, ActionArgument).Trim().ToLowerInvariant();
        string name = ToolArguments.ReadString(arguments, NameArgument).Trim();
        string to = ToolArguments.ReadString(arguments, ToArgument).Trim();
        if (!Actions.Contains(action))
        {
            return "Error: action must be one of " + string.Join(", ", Actions) + ", not " + action + ".";
        }

        if (name.Length == 0)
        {
            return "Error: name is required.";
        }

        if (action.StartsWith("rename", StringComparison.Ordinal) && to.Length == 0)
        {
            return "Error: a rename needs to, the new name.";
        }

        if (!ToolArguments.TryReadInt32(arguments, WalkArgument, out int? walk, out string rawWalk) || walk is < 1)
        {
            return "Error: walk must be a whole number from 1, not " + rawWalk + ".";
        }

        var store = List.Store;
        switch (action)
        {
            case "add_store":
                return List.AddStore(name, ToolArguments.ReadString(arguments, NotesArgument).Trim());
            case "use_store":
                return store.FindStore(name) is { } used ? List.UseStore(used) : ShoppingText.NoSuchStore(name, store.Stores());
            case "rename_store":
                return store.FindStore(name) is not { } renamed ? ShoppingText.NoSuchStore(name, store.Stores())
                    : List.RenameStore(renamed, to);   // the folder follows the name (2026-10-11), audited there
        }

        if (StoreOf(arguments, out string? error) is not { } shop)
        {
            return error!;
        }

        switch (action)
        {
            case "add_section":
                return store.EnsureSection(shop.Id, name) is { } section ? Done(ShoppingText.SectionAdded(shop, section)) : "Error: " + ShoppingText.WriteFailed;
            case "rename_section":
            case "remove_section":
                if (store.Sections(shop.Id).FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } found)
                {
                    return ShoppingText.NoSuchSection(shop, name, store.Sections(shop.Id));
                }

                return action == "rename_section"
                    ? store.RenameSection(found.Id, to) ? Done(ShoppingText.Renamed("section", found.Name, to)) : "Error: " + ShoppingText.NameTaken("section", to)
                    : store.RemoveSection(found.Id) ? Done(ShoppingText.SectionRemoved(shop, found)) : "Error: " + ShoppingText.WriteFailed;
            case "add_location":
                string sectionName = ToolArguments.ReadString(arguments, SectionArgument).Trim();
                var inSection = sectionName.Length == 0 ? null : store.EnsureSection(shop.Id, sectionName);
                return store.EnsureLocation(shop.Id, name, inSection?.Id, walk) is { } location ? Done(ShoppingText.LocationAdded(shop, location)) : "Error: " + ShoppingText.WriteFailed;
        }

        if (store.Locations(shop.Id).FirstOrDefault(l => string.Equals(l.Label, name, StringComparison.OrdinalIgnoreCase)) is not { } at)
        {
            return ShoppingText.NoSuchLocation(shop, name, store.Locations(shop.Id));
        }

        return action switch
        {
            "rename_location" => store.RenameLocation(at.Id, to) ? Done(ShoppingText.Renamed("location", at.Label, to)) : "Error: " + ShoppingText.NameTaken("location", to),
            "set_walk" => store.SetWalk(at.Id, walk) ? Done(ShoppingText.WalkSet(shop, at, walk)) : "Error: " + ShoppingText.WriteFailed,
            _ => store.RemoveLocation(at.Id) ? Done(ShoppingText.LocationRemoved(shop, at)) : "Error: " + ShoppingText.WriteFailed,
        };
    }

    /// <summary>The answer, audited.</summary>
    private static string Done(string answer)
    {
        Diagnostics.DiagnosticLog.Info(ShoppingStore.Category, "Layout: " + answer);
        return answer;
    }
}
