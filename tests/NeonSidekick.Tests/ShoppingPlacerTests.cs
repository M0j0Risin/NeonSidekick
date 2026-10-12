using NeonSidekick.Shopping;

namespace NeonSidekick.Tests;

/// <summary>
/// Where an item is at a store (2026-10-11): what the old shopping skill told the model to do by hand, in code — a brand before a
/// category, the longest term among the rest, word boundaries, plurals folded, a term's slash alternatives, numbered aisles as numbers.
/// </summary>
public sealed class ShoppingPlacerTests
{
    private static readonly ShopSection International = new(1, 1, "International Foods");
    private static readonly ShopSection Beverages = new(2, 1, "Beverages");
    private static readonly ShopSection Condiments = new(3, 1, "Condiments");
    private static readonly ShopSection Dairy = new(4, 1, "Dairy & Eggs");
    private static readonly ShopLocation Aisle2 = new(1, 1, "Aisle 2", 1, "International Foods", null);
    private static readonly ShopLocation Aisle10 = new(2, 1, "Aisle 10", 2, "Beverages", null);
    private static readonly ShopLocation DairyZone = new(3, 1, "Dairy", 4, "Dairy & Eggs", null);
    private static readonly ShopSection[] Sections = [International, Beverages, Condiments, Dairy];
    private static readonly ShopLocation[] Locations = [Aisle10, Aisle2, DairyZone];

    private static ShopPlacement Fact(long id, string term, string kind, ShopLocation? at, ShopSection? section = null, string source = ShoppingSources.User) =>
        new(id, 1, term, kind, at?.Id, section?.Id, source, "");

    private static readonly ShopPlacement[] Facts =
    [
        Fact(1, "Goya", ShoppingKinds.Brand, Aisle2),
        Fact(2, "Nectar", ShoppingKinds.Category, Aisle10),
        Fact(3, "Jam/Jelly", ShoppingKinds.Category, Aisle2, Condiments),
        Fact(4, "Ham", ShoppingKinds.Category, DairyZone),
        Fact(5, "Eggs", ShoppingKinds.Category, DairyZone, source: ShoppingSources.Guess),
        Fact(6, "Soy milk", ShoppingKinds.Item, null, Dairy),
        Fact(7, "Milk", ShoppingKinds.Category, DairyZone),
    ];

    private static ShopPlace Place(string name) => ShoppingPlacer.Place(name, Facts, Locations, Sections);

    [Fact]
    public void ABrand_OutranksACategory_InTheName()
    {
        // The old skill's pitfall: "Goya guava nectar" is where Goya is, not where nectar is.
        var place = Place("Goya guava nectar");
        Assert.Equal("Goya", place.Placement!.Term);
        Assert.Equal(Aisle2, place.Location);
        Assert.Equal(International, place.Section);
        Assert.Equal("Nectar", Place("Peach nectar").Placement!.Term);
    }

    [Fact]
    public void TheLongestTerm_Wins_AndAWholeNameMatchBeatsEverything()
    {
        // "Soy milk" (two words) over "Milk"; the item names its section alone, so no location.
        var soy = Place("Silk soy milk");
        Assert.Equal("Soy milk", soy.Placement!.Term);
        Assert.Null(soy.Location);
        Assert.Equal(Dairy, soy.Section);
        Assert.Equal("Milk", Place("Chocolate milk").Placement!.Term);
    }

    [Fact]
    public void Terms_MatchAtWordBoundaries_PluralsFold_AndSlashesAreAlternatives()
    {
        Assert.False(Place("Shampoo").IsPlaced);          // ham is not in shampoo
        Assert.Equal("Ham", Place("Honey ham").Placement!.Term);
        Assert.Equal("Eggs", Place("Brown egg").Placement!.Term);
        Assert.Equal("Jam/Jelly", Place("Grape jelly").Placement!.Term);
        // The fact names a section beside the location: the section is the fact's.
        Assert.Equal(Condiments, Place("Strawberry jam").Section);
        Assert.True(Place("Eggs").IsGuess);
        Assert.False(Place("Goya beans").IsGuess);
    }

    [Theory]
    [InlineData("berries", "berry")]
    [InlineData("cookies", "cookie")]
    [InlineData("tomatoes", "tomato")]
    [InlineData("boxes", "box")]
    [InlineData("glasses", "glass")]
    [InlineData("hummus", "hummus")]
    [InlineData("chips", "chip")]
    public void Plurals_FoldToTheSameKey(string plural, string singular) =>
        Assert.Equal(ShoppingPlacer.Key(singular), ShoppingPlacer.Key(plural));

    [Fact]
    public void Walk_GroupsByLocationInWalkOrder_ThenSectionsAlone_ThenTheUnplaced()
    {
        ShopItem Item(long id, string name) => new(id, name, 1, "", false, DateTimeOffset.UnixEpoch);
        var groups = ShoppingPlacer.Walk([Item(1, "Widgets"), Item(2, "Soy milk"), Item(3, "Peach nectar"), Item(4, "Goya beans"), Item(5, "Eggs"), Item(6, "Apple nectar")], Facts, Locations, Sections);

        Assert.Equal(["Aisle 2", "Aisle 10", "Dairy", "Dairy & Eggs", "Unplaced"], groups.Select(ShoppingText.Heading).Select(h => h.Split(" · ")[0]));
        Assert.Equal(["Apple nectar", "Peach nectar"], groups[1].Items.Select(p => p.Item.Name));
        Assert.Equal(["Widgets"], groups[^1].Items.Select(p => p.Item.Name));
    }

    [Fact]
    public void NaturalComparer_ComparesDigitRunsAsNumbers()
    {
        string[] labels = ["Aisle 10", "aisle 2", "Aisle 1", "Aisle 02b", "Bakery"];
        Assert.Equal(["Aisle 1", "aisle 2", "Aisle 02b", "Aisle 10", "Bakery"], labels.Order(NaturalComparer.Instance));
    }

    [Theory]
    [InlineData("2 soy milk", "soy milk", 2)]
    [InlineData("eggs x3", "eggs", 3)]
    [InlineData("eggs ×12", "eggs", 12)]
    [InlineData("2x bread", "bread", 2)]
    [InlineData("2% milk", "2% milk", 1)]
    [InlineData("7up", "7up", 1)]
    [InlineData("0 bananas", "0 bananas", 1)]
    [InlineData("milk", "milk", 1)]
    public void ParseLine_ReadsALeadingOrTrailingQuantity(string line, string name, int quantity) =>
        Assert.Equal((name, quantity), ShoppingList.ParseLine(line));

    [Theory]
    [InlineData("H-E-B San Antonio #372", "h-e-b-san-antonio-372")]
    [InlineData("  Trader Joe's  ", "trader-joe-s")]
    [InlineData("###", "store")]
    public void FolderFor_MakesAPlainFolderName(string name, string folder) =>
        Assert.Equal(folder, ShoppingList.FolderFor(name));
}
