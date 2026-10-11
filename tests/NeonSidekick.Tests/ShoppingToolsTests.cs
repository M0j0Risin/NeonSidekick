using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Shopping;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The shopping tools (2026-10-11, the user's ask: the shopping list a feature of its own): the list, its lines and the stores' facts,
/// over a profile's <c>db\shopping\shopping.db</c>, and the group's place in a turn.
/// </summary>
public sealed class ShoppingToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly ShoppingStoreHolder _holder;
    private readonly ShoppingList _list;

    public ShoppingToolsTests()
    {
        _holder = new ShoppingStoreHolder(() => _dir, _time);
        _list = new ShoppingList(_holder.Current);
    }

    public void Dispose()
    {
        _holder.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static async Task<string> Call(AIFunction tool, params (string Name, object? Value)[] pairs) =>
        (string)(await tool.InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))))!;

    private AIFunction Tool(string name, Func<string, CancellationToken, Task<bool?>>? confirm = null) =>
        ChatScreen.ShoppingTools(_list, confirm).Single(t => t.Name == name);

    [Fact]
    public void TheGroup_IsNineTools_EachClassifiedForPlanMode()
    {
        var names = ChatScreen.ShoppingTools(_list, null).Select(t => t.Name).ToList();
        Assert.Equal(
            ["shopping_list", "shopping_add", "shopping_check", "shopping_remove", "shopping_clear", "shopping_locate", "shopping_place", "shopping_stores", "shopping_layout"],
            names);
        Assert.Equal(["shopping_list", "shopping_locate", "shopping_stores"], names.Where(PlanTools.Allowed));
        Assert.All(names.Where(n => !PlanTools.Allowed(n)), n => Assert.Contains(n, PlanTools.Mutating));
    }

    [Fact]
    public async Task WithNoStore_TheListIsInTheOrderAdded_AndAddingTheSameNameBumpsIt()
    {
        var add = Tool(ShoppingAddTool.ToolName);
        Assert.Equal("Added Soy milk ×2.\nAdded Bread.", await Call(add, ("items", new[] { "2 Soy milk", "Bread" })));
        Assert.Equal("Already on the list; now Soy milk ×3.", await Call(add, ("items", new[] { "Soy Milk" })));

        Assert.Equal(
            "Shopping list (no store set up: lines in the order added): 2 items to get.\nUnplaced\n- Soy milk ×3\n- Bread",
            await Call(Tool(ShoppingListTool.ToolName)));
        Assert.Equal(ShoppingText.NoStore, await Call(Tool(ShoppingLocateTool.ToolName), ("term", "bread")));
    }

    [Fact]
    public async Task AStoresFacts_PlaceTheList_ABrandBeforeACategory_AndAGuessIsSaidToBeOne()
    {
        var layout = Tool(ShoppingLayoutTool.ToolName);
        var place = Tool(ShoppingPlaceTool.ToolName);
        string added = await Call(layout, ("action", "add_store"), ("name", "H-E-B #372"));
        Assert.StartsWith("Added the store \"H-E-B #372\" (the active store); its reference documents go in ", added, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(_dir, "db", "shopping", "reference", "h-e-b-372")));

        Assert.Equal(
            "\"Aisle 2\" is a location of H-E-B #372 in International Foods, walked 1st.",
            await Call(layout, ("action", "add_location"), ("name", "Aisle 2"), ("section", "International Foods"), ("walk", 1)));
        Assert.Equal(
            "At H-E-B #372, brand \"Goya\" is in Aisle 2 (International Foods).",
            await Call(place, ("term", "Goya"), ("kind", "brand"), ("location", "aisle 2")));
        Assert.Equal(
            "At H-E-B #372, category \"Nectar\" is in Aisle 10 (Beverages). Made: new section \"Beverages\", new location \"Aisle 10\".",
            await Call(place, ("term", "Nectar"), ("kind", "category"), ("location", "Aisle 10"), ("section", "Beverages")));

        Assert.Equal(
            "Added Goya guava nectar — Aisle 2 (International Foods) at H-E-B #372.\n" +
            "Added Peach nectar — Aisle 10 (Beverages) at H-E-B #372.\n" +
            "Added Babybel — not placed at H-E-B #372 yet.\n" + ShoppingText.UnplacedHint,
            await Call(Tool(ShoppingAddTool.ToolName), ("items", new[] { "Goya guava nectar", "Peach nectar", "Babybel" })));

        // A guess placed later moves the line, marked as one.
        Assert.Equal(
            "At H-E-B #372, item \"Babybel\" is in Deli (guess). Made: new location \"Deli\". On the list there now: Babybel.",
            await Call(place, ("term", "Babybel"), ("location", "Deli"), ("source", "guess")));

        Assert.Equal(
            "Shopping list at H-E-B #372: 3 items to get.\n" +
            "Aisle 2 · International Foods\n- Goya guava nectar\n" +
            "Aisle 10 · Beverages\n- Peach nectar\n" +
            "Deli\n- Babybel (guess)",
            await Call(Tool(ShoppingListTool.ToolName)));

        string located = await Call(Tool(ShoppingLocateTool.ToolName), ("term", "Goya mango nectar"));
        Assert.StartsWith(
            "\"Goya mango nectar\" at H-E-B #372: Aisle 2 (International Foods) — by the brand \"Goya\" (user).\nRelated facts: category \"Nectar\" → Aisle 10 (Beverages)\nNo reference documents",
            located, StringComparison.Ordinal);

        // The user's correction moves the fact and the line with it, no longer a guess.
        await Call(place, ("term", "Babybel"), ("location", "Dairy"), ("source", "user"));
        Assert.Contains("Dairy\n- Babybel\n", await Call(Tool(ShoppingListTool.ToolName)) + "\n", StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRemoveAndClear_ByNameOrNumber_TheWholeListOnlyWithAYes()
    {
        await Call(Tool(ShoppingAddTool.ToolName), ("items", new[] { "Eggs", "Bread", "Soy milk", "Almond milk" }));
        Assert.Equal("Ticked Eggs.\nTicked Bread.", await Call(Tool(ShoppingCheckTool.ToolName), ("items", new[] { "eggs", "2" })));
        Assert.Equal(ShoppingText.Ambiguous("milk", _list.Find("milk")), await Call(Tool(ShoppingCheckTool.ToolName), ("items", new[] { "milk" })));
        Assert.Equal(ShoppingText.NotOnList("butter"), await Call(Tool(ShoppingRemoveTool.ToolName), ("items", new[] { "butter" })));
        Assert.Equal("Unticked Bread.", await Call(Tool(ShoppingCheckTool.ToolName), ("items", new[] { "bread" }), ("checked", false)));

        Assert.Equal("Cleared the ticked lines (1 line).", await Call(Tool(ShoppingClearTool.ToolName)));
        Assert.Equal("Removed Almond milk from the list.", await Call(Tool(ShoppingRemoveTool.ToolName), ("items", new[] { "almond milk" })));

        // The whole list: refused with nothing to ask on (headless), declined, then cleared on a yes.
        Assert.Equal(ShoppingText.ClearAllNoPane, await Call(Tool(ShoppingClearTool.ToolName), ("all", true)));
        string? asked = null;
        Assert.Equal(ShoppingText.ClearAllDeclined, await Call(Tool(ShoppingClearTool.ToolName, (q, _) => { asked = q; return Task.FromResult<bool?>(false); }), ("all", true)));
        Assert.Equal(ShoppingText.ClearAllQuestion(2), asked);
        Assert.Equal("Cleared the whole list (2 lines).", await Call(Tool(ShoppingClearTool.ToolName, (_, _) => Task.FromResult<bool?>(true)), ("all", true)));
        Assert.Empty(_list.Store.Items());
    }

    [Fact]
    public async Task Stores_ListsThemAndOneLayout_AndUseStoreSwitchesTheWalk()
    {
        var layout = Tool(ShoppingLayoutTool.ToolName);
        await Call(layout, ("action", "add_store"), ("name", "H-E-B"));
        await Call(layout, ("action", "add_store"), ("name", "Costco"), ("notes", "the one on 1604"));
        await Call(layout, ("action", "add_location"), ("name", "Aisle 3"), ("section", "Pantry"));
        Assert.Equal("\"Costco\" is the active store; the list now walks through it.", await Call(layout, ("action", "use_store"), ("name", "costco")));
        Assert.Equal(ShoppingText.NoSuchLocation(_holder.Current().FindStore("Costco")!, "Aisle 3", []), await Call(layout, ("action", "set_walk"), ("name", "Aisle 3"), ("walk", 2)));

        string stores = await Call(Tool(ShoppingStoresTool.ToolName));
        Assert.StartsWith("Stores: \"Costco\" (active), \"H-E-B\".\n\"Costco\" (active) — the one on 1604\nWalk: no locations yet", stores, StringComparison.Ordinal);
        Assert.Contains("Walk: Aisle 3 (Pantry)\nSections: Pantry\nPlacement facts: 0", await Call(Tool(ShoppingStoresTool.ToolName), ("store", "H-E-B")), StringComparison.Ordinal);
        Assert.Equal(ShoppingText.NoSuchStore("Kroger", _holder.Current().Stores()), await Call(Tool(ShoppingListTool.ToolName), ("store", "Kroger")));
    }

    [Fact]
    public void TheGroup_IsOfferedWithItsSwitch_AndItsSentenceRidesWithIt()
    {
        var tools = ChatScreen.ShoppingTools(_list, null);
        var on = ChatScreen.ComposeTurnTools(new TurnToolInputs { Standing = [], Shopping = tools, ShoppingEnabled = true });
        Assert.Equal(tools.Select(t => t.Name), on.Tools.Select(t => t.Name));
        Assert.True(on.Rules.Shopping);
        Assert.Contains(Assistant.ShoppingRule, on.Rules.DefaultRules(markdown: false), StringComparison.Ordinal);

        var off = ChatScreen.ComposeTurnTools(new TurnToolInputs { Standing = [], Shopping = tools, ShoppingEnabled = false });
        Assert.Empty(off.Tools);
        Assert.DoesNotContain(Assistant.ShoppingRule, off.Rules.DefaultRules(markdown: false), StringComparison.Ordinal);

        // Plan mode keeps the reads alone.
        var planning = ChatScreen.ComposeTurnTools(new TurnToolInputs { Standing = [], Shopping = tools, ShoppingEnabled = true, PlanReadOnly = true });
        Assert.Equal(["shopping_list", "shopping_locate", "shopping_stores"], planning.Tools.Select(t => t.Name));
    }

    [Fact]
    public void TheHolder_OpensTheProfileLoadedNow()
    {
        string other = Path.Combine(_dir, "other");
        string profile = _dir;
        using var holder = new ShoppingStoreHolder(() => profile, _time);
        var first = holder.Current();
        Assert.Same(first, holder.Current());
        profile = other;
        var second = holder.Current();
        Assert.NotSame(first, second);
        Assert.Equal(Path.Combine(Path.GetFullPath(other), "db", "shopping"), second.Folder);
        Assert.False(first.Available);   // the old one closed
    }
}
