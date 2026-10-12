using NeonSidekick.Shopping;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>A store's layout editor (2026-10-11, the user's ask): a guess kept, an aisle moved up the walk, an aisle added in a section.</summary>
public partial class SettingsMenuTests
{
    [Fact]
    public async Task OnThePane_TheLayoutEditor_KeepsAGuess_MovesAnAisleUp_AndAddsOneInASection()
    {
        using var holder = new ShoppingStoreHolder(() => _settings.ProfileDirectory);
        var list = new ShoppingList(holder.Current);
        list.AddStore("H-E-B");
        var shop = list.StoreFor(null, out _)!;
        list.AddSection(shop, "Bakery");
        list.AddLocation(shop, "Aisle 1", null);
        list.AddLocation(shop, "Aisle 2", null);
        list.Place(shop, "Eggs", ShoppingKinds.Category, "Aisle 2", null, ShoppingSources.Guess);
        var (menu, pane) = PaneMenu();
        menu.Shopping = list;

        Push(Keys.Enter, Keys.Enter);                    // the Guesses tab, open first: Eggs › keep
        Push(Keys.Left, Keys.Left, Keys.Left);           // Aisles, the cursor on Aisle 1
        Push(Keys.Down, Keys.Enter);                     // Aisle 2's actions
        Push(Keys.Down, Keys.Down, Keys.Enter);          // move up the walk
        Push(Keys.Up, Keys.Up, Keys.Enter);              // the add row
        _console.Input.PushText("Deli");
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Enter);                     // its section: Bakery
        Push(Keys.Escape);

        await menu.EditStoreLayoutAsync(shop, CancellationToken.None);

        var store = list.Store;
        Assert.Equal(ShoppingSources.User, Assert.Single(store.Placements(shop.Id)).Source);
        Assert.Equal([("Aisle 2", 1), ("Aisle 1", 2)], store.Locations(shop.Id).Where(l => l.Walk is not null).Select(l => (l.Label, l.Walk!.Value)));
        var deli = Assert.Single(store.Locations(shop.Id), l => l.Label == "Deli");
        Assert.Equal("Bakery", deli.SectionName);
        Assert.Contains(ShoppingText.FactConfirmed(store.Placements(shop.Id)[0]), Output);
        Assert.Contains(ShoppingText.GuessesTab, Output);
        pane.Dispose();
    }
}
