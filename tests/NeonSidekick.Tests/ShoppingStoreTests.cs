using Microsoft.Data.Sqlite;
using NeonSidekick.Diagnostics;
using NeonSidekick.Shopping;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The shopping list's store (2026-10-11): <c>&lt;profile&gt;\db\shopping\shopping.db</c>, SessionStore's shape.</summary>
public sealed class ShoppingStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly ShoppingStore _store;

    public ShoppingStoreTests()
    {
        _store = new ShoppingStore(_dir, _time);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void TheFile_LivesInTheProfilesDbShoppingFolder_MadeAtTheFirstUse()
    {
        Assert.Equal(Path.Combine("db", "shopping"), ShoppingStore.FolderName);
        Assert.Equal("shopping.db", ShoppingStore.FileName);
        Assert.Equal(Path.Combine(Path.GetFullPath(_dir), "db", "shopping", "shopping.db"), _store.FilePath);
        Assert.Equal(Path.Combine(Path.GetFullPath(_dir), "db", "shopping", "reference"), _store.ReferenceRoot);
        Assert.False(File.Exists(_store.FilePath));

        Assert.Empty(_store.Items());
        Assert.True(File.Exists(_store.FilePath));
        Assert.True(_store.Available);
    }

    [Fact]
    public void Items_AddTickQuantityRenameRemove_AndClearTicked()
    {
        var milk = _store.AddItem("Soy milk", 2)!;
        var eggs = _store.AddItem("Eggs", 0)!;   // a quantity below one is one
        Assert.Equal(1, eggs.Quantity);
        Assert.Equal(["Soy milk", "Eggs"], _store.Items().Select(i => i.Name));

        Assert.True(_store.SetChecked(milk.Id, true));
        Assert.True(_store.Items()[0].Checked);
        Assert.True(_store.SetQuantity(milk.Id, 3));   // a new quantity unticks: wanted again
        Assert.Equal((3, false), (_store.Items()[0].Quantity, _store.Items()[0].Checked));

        Assert.True(_store.RenameItem(eggs.Id, "Brown eggs"));
        Assert.True(_store.SetChecked(eggs.Id, true));
        Assert.Equal(1, _store.Clear(all: false));
        Assert.Equal(["Soy milk"], _store.Items().Select(i => i.Name));
        Assert.True(_store.RemoveItem(milk.Id));
        Assert.False(_store.RemoveItem(milk.Id));
        Assert.Empty(_store.Items());
    }

    [Fact]
    public void Stores_TheFirstIsActive_UntilAnotherIsChosen_AndNamesAreUniqueWithoutCase()
    {
        Assert.Null(_store.ActiveStore());
        var heb = _store.AddStore("H-E-B #372", "h-e-b-372")!;
        var costco = _store.AddStore("Costco", "costco")!;
        Assert.Null(_store.AddStore("h-e-b #372", "other"));
        Assert.Equal(heb, _store.ActiveStore());

        Assert.True(_store.SetActiveStore(costco.Id));
        Assert.Equal(costco, _store.ActiveStore());
        Assert.Equal(costco, _store.FindStore("COSTCO"));
        Assert.Equal(heb, _store.FindStore("h-e-b-372"));   // by its folder too

        // A taken folder gets a number.
        Assert.Equal("costco-2", _store.AddStore("Costco Business", "costco")!.Folder);

        // The active store removed, the first left takes over.
        Assert.True(_store.RemoveStore(costco.Id));
        Assert.Equal(heb, _store.ActiveStore());
    }

    [Fact]
    public void ALocationRemoved_TakesItsPlacementsWithIt_ASectionRemoved_LeavesThemWithoutOne()
    {
        var heb = _store.AddStore("H-E-B", "heb")!;
        var dairy = _store.EnsureSection(heb.Id, "Dairy & Eggs")!;
        Assert.Equal(dairy, _store.EnsureSection(heb.Id, "dairy & eggs"));   // the same one, any case
        var aisle = _store.EnsureLocation(heb.Id, "Dairy", dairy.Id)!;
        Assert.Equal("Dairy & Eggs", aisle.SectionName);
        Assert.True(_store.Place(heb.Id, "Eggs", ShoppingKinds.Category, aisle.Id, null, ShoppingSources.Guess));
        Assert.True(_store.Place(heb.Id, "Butter", ShoppingKinds.Item, null, dairy.Id, ShoppingSources.User));

        // The same term again moves the fact (any case, the first spelling kept), no second row.
        Assert.True(_store.Place(heb.Id, "EGGS", ShoppingKinds.Category, aisle.Id, null, ShoppingSources.User));
        Assert.Equal([("Butter", ShoppingSources.User), ("Eggs", ShoppingSources.User)], _store.Placements(heb.Id).Select(p => (p.Term, p.Source)));

        Assert.True(_store.RemoveSection(dairy.Id));
        var butter = Assert.Single(_store.Placements(heb.Id), p => p.Term == "Butter");
        Assert.Null(butter.SectionId);
        Assert.Null(_store.Locations(heb.Id)[0].SectionId);

        Assert.True(_store.RemoveLocation(aisle.Id));
        Assert.Equal(["Butter"], _store.Placements(heb.Id).Select(p => p.Term));
    }

    [Fact]
    public void ATickedLine_LeavingTheList_IsRememberedAsBought_AnUntickedOneIsNot()
    {
        var list = new ShoppingList(() => _store);
        list.Add("Eggs", 2, null);
        list.Add("Bread", 1, null);
        list.Add("Soap", 1, null);
        var items = _store.Items();
        list.Check(items[0], true);
        Assert.True(list.Remove(items[0]));                     // Eggs, ticked: bought
        Assert.True(list.Remove(items[2]));                     // Soap, unticked: not
        list.Check(items[1], true);
        Assert.Equal(1, list.Clear(all: true));                 // Bread, ticked
        Assert.Equal([("Eggs", 2, 1), ("Bread", 1, 1)], _store.Bought().Select(b => (b.Name, b.Quantity, b.Times)).OrderBy(b => b.Name == "Bread"));

        // Bought again under another spelling: the same line, counted twice, its latest name and quantity kept.
        Assert.True(_store.RecordBought([new ShopItem(0, "EGGS", 6, "", true, DateTimeOffset.UnixEpoch)]));
        var eggs = _store.Bought()[0];
        Assert.Equal(("EGGS", 6, 2), (eggs.Name, eggs.Quantity, eggs.Times));
        Assert.True(_store.ForgetBought(eggs.Id));
        Assert.Equal(["Bread"], _store.Bought().Select(b => b.Name));
    }

    [Fact]
    public void APlacementsKindAndSource_ChangeInPlace()
    {
        var heb = _store.AddStore("H-E-B", "heb")!;
        var aisle = _store.EnsureLocation(heb.Id, "Aisle 2")!;
        Assert.True(_store.Place(heb.Id, "Goya", ShoppingKinds.Category, aisle.Id, null, ShoppingSources.Guess));
        long id = _store.Placements(heb.Id)[0].Id;
        Assert.True(_store.SetKind(id, ShoppingKinds.Brand));
        Assert.True(_store.SetSource(id, ShoppingSources.User));
        var fact = Assert.Single(_store.Placements(heb.Id));
        Assert.Equal((ShoppingKinds.Brand, ShoppingSources.User), (fact.Kind, fact.Source));
        Assert.False(_store.SetKind(id + 1, ShoppingKinds.Item));
    }

    [Fact]
    public void Locations_ComeInWalkOrder_NumberedAislesAsNumbers()
    {
        var heb = _store.AddStore("H-E-B", "heb")!;
        foreach (string label in new[] { "Produce", "Aisle 10", "Aisle 2", "Aisle 1", "Bakery" })
        {
            _store.EnsureLocation(heb.Id, label);
        }

        Assert.Equal(["Aisle 1", "Aisle 2", "Aisle 10", "Bakery", "Produce"], _store.Locations(heb.Id).Select(l => l.Label));

        // A walk number puts a location first, whatever its name.
        var produce = _store.Locations(heb.Id).Single(l => l.Label == "Produce");
        Assert.True(_store.SetWalk(produce.Id, 1));
        Assert.Equal("Produce", _store.Locations(heb.Id)[0].Label);
    }

    [Fact]
    public void AFileAtAnotherSchema_IsRefusedUntouched_WithOneWarning()
    {
        _store.AddItem("Milk", 1);
        _store.Dispose();
        using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _store.FilePath, Pooling = false }.ToString()))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = "UPDATE meta SET value = 99 WHERE key = 'schema'";
            command.ExecuteNonQuery();
        }

        var warnings = new List<string>();
        void OnEmitted(DiagnosticEvent e)
        {
            if (e.Category == ShoppingStore.Category && e.Level == DiagnosticLevel.Warning)
            {
                warnings.Add(e.Message);
            }
        }

        DiagnosticLog.Emitted += OnEmitted;
        try
        {
            using var reopened = new ShoppingStore(_dir, _time);
            Assert.False(reopened.Available);
            Assert.Empty(reopened.Items());
            Assert.Null(reopened.AddItem("Bread", 1));
        }
        finally
        {
            DiagnosticLog.Emitted -= OnEmitted;
        }

        Assert.Equal([ShoppingStore.SchemaMismatchWarning(99)], warnings);
    }
}
