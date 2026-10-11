using NeonSidekick.App;
using NeonSidekick.Shopping;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The shopping stores' manager (2026-10-11, the user's ask): added, made active, renamed and removed on the pane.</summary>
public partial class SettingsMenuTests
{
    [Fact]
    public async Task OnThePane_TheStoresManager_Adds_Uses_Renames_AndRemovesAStore()
    {
        using var holder = new ShoppingStoreHolder(() => _settings.ProfileDirectory);
        var list = new ShoppingList(holder.Current);
        var (menu, pane) = PaneMenu();
        menu.Shopping = list;

        Push(Keys.Enter);                                        // the add row
        _console.Input.PushText("H-E-B");
        Push(Keys.Enter);                                        // added, the active store (the first), the cursor on it
        Push(Keys.Up, Keys.Enter);                               // the add row again
        _console.Input.PushText("Costco");
        Push(Keys.Enter);                                        // added; the cursor on it (stores by name: Costco, H-E-B)
        Push(Keys.Enter);                                        // shop at Costco
        Push(Keys.Char(ShoppingText.RenameStoreKey));            // rename it: its name to start from
        foreach (char _ in "Costco")
        {
            Push(Keys.Backspace);
        }

        _console.Input.PushText("Costco Business");
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Char(ShoppingText.RemoveStoreKey)); // H-E-B: remove, asked
        Push(Keys.Down, Keys.Enter);                             // Yes
        Push(Keys.Escape);

        Assert.True(await menu.ManageShoppingStoresAsync(CancellationToken.None));   // something changed

        var stores = list.Store.Stores();
        Assert.Equal(["Costco Business"], stores.Select(s => s.Name));
        Assert.Equal("costco-business", stores[0].Folder);   // the folder follows the name (later on 2026-10-11)
        Assert.True(Directory.Exists(list.Store.ReferenceFolder(stores[0])));
        Assert.False(Directory.Exists(Path.Combine(list.Store.ReferenceRoot, "costco")));
        Assert.Equal("Costco Business", list.Store.ActiveStore()!.Name);
        Assert.Contains(ShoppingText.Renamed("store", "Costco", "Costco Business"), Output);
        Assert.Contains("Remove the store \"H-E-B\" with its layout (0 placement facts)? Its reference folder stays.", Output);
        Assert.Contains("● Costco Business (active)", Output);
        Assert.Contains(ShoppingText.RenameStoreButton, Output);
        Assert.Contains("0 locations · 0 sections · 0 placement facts · 0 reference documents", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheListRow_OpensTheChecklist_AndLeavesThePaneOpenForItsHost()
    {
        using var holder = new ShoppingStoreHolder(() => _settings.ProfileDirectory);
        var list = new ShoppingList(holder.Current);
        list.Add("Bread", 1, null);
        list.Add("Milk", 2, null);
        var (menu, pane) = PaneMenu();
        menu.Shopping = list;
        Push(Keys.Enter);                                        // Bread ticked
        Push(Keys.Escape);

        await menu.ManageShoppingListAsync(CancellationToken.None);

        Assert.Equal([("Bread", true), ("Milk", false)], list.Store.Items().Select(i => (i.Name, i.Checked)));
        Assert.True(pane.OverlayOpen);   // the row's page comes back over it; /shopping's own pane closes
        Assert.Contains(ShoppingText.TickAllButton, Output);
        pane.Dispose();
    }

    [Fact]
    public void TheStoresRow_IsAnActionRow_OnTheShoppingMenu()
    {
        Assert.Equal("Shopping stores", SettingsMenu.FieldName(SettingsField.ShoppingStores));
        Assert.Equal(ShoppingText.ManageStoresLabel, SettingsMenu.FieldValue(SettingsField.ShoppingStores, new Settings.AppSettingsData(), _settings.ProfileDirectory));
        Assert.Equal([SettingsField.ShoppingTools, SettingsField.ShoppingList, SettingsField.ShoppingStores], SettingsMenu.ToolsTabFields[ToolsText.MenuIndex(ToolsText.ShoppingMenuTitle)]);
        Assert.Equal("Shopping list", SettingsMenu.FieldName(SettingsField.ShoppingList));
        Assert.Equal(ShoppingText.ManageListLabel, SettingsMenu.FieldValue(SettingsField.ShoppingList, new Settings.AppSettingsData(), _settings.ProfileDirectory));
        Assert.False(SettingsMenu.IsToggle(SettingsField.ShoppingStores));
        Assert.Equal(["✎ rename", "✖  remove", "📂 folder", "⟳ reindex"], SettingsMenu.StoreButtons.Select(b => b.Title));
        Assert.Equal("● H-E-B (active)", ShoppingText.StoreRow(new ShopStore(1, "H-E-B", "h-e-b", ""), active: true));
    }
}
