using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// The embedded model lists' filters (later on 2026-09-29, the user's ask): the 8GB / 16GB / 32GB radio buttons, the
/// uncensored switch, what they keep and which size <c>Embedded filter type</c> measures.
/// </summary>
public class EmbeddedModelFilterTests
{
    private static readonly EmbeddedSampling Sampling = new(1.0, 0.95, 64);

    // A model of <weights> bytes with a 1 GB projector and, optionally, a drafter.
    private static EmbeddedModel Model(string display, long weights, long drafter = 0) =>
        new("m", display, "Q4", "r/r", "c", new EmbeddedFile("m.gguf", weights, "0"), new EmbeddedFile("mmproj.gguf", 1_000_000_000, "0"), Sampling,
            drafter > 0 ? new EmbeddedFile("mtp.gguf", drafter, "0") : null);

    [Fact]
    public void TheButtons_ArePinned_AndStartDark()
    {
        var buttons = EmbeddedModelFilter.None.Buttons();
        Assert.Equal(["8GB", "16GB", "32GB", "uncensored", "sort size"], buttons.Select(b => b.Title));
        Assert.Equal(new char?[] { '1', '2', '3', 'u', 's' }, buttons.Select(b => b.Key));
        Assert.All(buttons, b => Assert.False(b.On));
        Assert.False(EmbeddedModelFilter.None.Active);
        Assert.Equal("1 / 2 / 3 = 8 / 16 / 32 GB · U = uncensored · S = sort by size", EmbeddedModelFilter.Keys);
        Assert.Equal("no model matches the filter", EmbeddedLlmText.NoFilterMatch);
    }

    [Fact]
    public void TheSizes_AreRadioButtons_ThatGoDarkPressedAgain_AndUncensoredIsItsOwn()
    {
        var eight = EmbeddedModelFilter.None.Press(0);
        Assert.Equal(new EmbeddedModelFilter(8, false), eight);
        Assert.Equal([true, false, false, false, false], eight.Buttons().Select(b => b.On));
        Assert.Equal(new EmbeddedModelFilter(16, false), eight.Press(1));    // another size takes its place
        Assert.Equal(EmbeddedModelFilter.None, eight.Press(0));             // the lit one pressed again: none

        var both = eight.Press(3);
        Assert.Equal(new EmbeddedModelFilter(8, true), both);
        Assert.Equal([true, false, false, true, false], both.Buttons().Select(b => b.On));
        Assert.Equal(new EmbeddedModelFilter(32, true), both.Press(2));     // the size moves, uncensored stays
        Assert.Equal(new EmbeddedModelFilter(8, false), both.Press(3));
        Assert.Equal(both, both.Press(9));                                  // no such button
        Assert.True(both.Active);
    }

    [Fact]
    public void TheCatalogsButtons_PutInstalledAndUninstalled_BetweenTheSizesAndUncensored_ARadioPair()
    {
        // Later on 2026-09-29 (the user's ask): the catalog alone; /server keeps the four.
        var buttons = EmbeddedModelFilter.None.Buttons(withInstalled: true);
        Assert.Equal(["8GB", "16GB", "32GB", "installed", "uninstalled", "uncensored", "sort size"], buttons.Select(b => b.Title));
        Assert.Equal(new char?[] { '1', '2', '3', 'i', 'n', 'u', 's' }, buttons.Select(b => b.Key));
        Assert.Equal(5, EmbeddedModelFilter.None.Buttons().Count);
        Assert.Equal("1 / 2 / 3 = 8 / 16 / 32 GB · I / N = installed / uninstalled · U = uncensored · S = sort by size", EmbeddedModelFilter.CatalogKeys);

        var installed = EmbeddedModelFilter.None.Press(3, withInstalled: true);
        Assert.Equal(new EmbeddedModelFilter(null, false, true), installed);
        Assert.Equal([false, false, false, true, false, false, false], installed.Buttons(withInstalled: true).Select(b => b.On));
        var uninstalled = installed.Press(4, withInstalled: true);
        Assert.Equal(new EmbeddedModelFilter(null, false, false), uninstalled);
        Assert.Equal(EmbeddedModelFilter.None, uninstalled.Press(4, withInstalled: true));   // the lit one again: none
        Assert.Equal(new EmbeddedModelFilter(16, true, false), uninstalled.Press(1, withInstalled: true).Press(5, withInstalled: true));   // the others left be
        Assert.True(uninstalled.Active);

        var small = new EmbeddedModelFilter(8, false, true);
        Assert.True(small.Matches(Model("A", 5_000_000_000), EmbeddedFilterType.File, installed: true));
        Assert.False(small.Matches(Model("A", 5_000_000_000), EmbeddedFilterType.File, installed: false));
        Assert.False(small.Matches(Model("A", 9_000_000_000), EmbeddedFilterType.File, installed: true));
        Assert.True(new EmbeddedModelFilter(null, false, false).Matches(Model("A", 5_000_000_000), EmbeddedFilterType.File, installed: false));
    }

    [Fact]
    public void ASize_KeepsTheModelsAtMostThatBig_AsTheRowReadsThem()
    {
        var filter = new EmbeddedModelFilter(8, false);
        Assert.True(filter.Matches(Model("A", 6_900_000_000), EmbeddedFilterType.File));    // 7.9 GB
        Assert.True(filter.Matches(Model("A", 7_040_000_000), EmbeddedFilterType.File));    // 8.04 GB reads "8 GB": it passes
        Assert.False(filter.Matches(Model("A", 7_060_000_000), EmbeddedFilterType.File));   // "8.1 GB"
        Assert.True(new EmbeddedModelFilter(16, false).Matches(Model("A", 7_060_000_000), EmbeddedFilterType.File));
        Assert.True(EmbeddedModelFilter.None.Matches(Model("A", 90_000_000_000), EmbeddedFilterType.File));
    }

    [Fact]
    public void TheFilterType_SaysWhichBytes_TheRowsOrTheWeights()
    {
        // 7.5 GB of weights, 1 GB of projector, 0.5 GB of drafter: 9 GB on the row.
        var model = Model("A", 7_500_000_000, 500_000_000);
        var eight = new EmbeddedModelFilter(8, false);
        Assert.False(eight.Matches(model, EmbeddedFilterType.File));
        Assert.True(eight.Matches(model, EmbeddedFilterType.Gguf));

        Assert.Equal("file", EmbeddedFilterTypes.Default);
        Assert.Equal(["file", "gguf"], EmbeddedFilterTypes.Names);
        Assert.Equal("file", new AppSettingsData().EmbeddedFilterType);
        Assert.Equal(EmbeddedFilterType.Gguf, EmbeddedFilterTypes.Resolve(new AppSettingsData { EmbeddedFilterType = " GGUF " }));
        Assert.Equal(EmbeddedFilterType.File, EmbeddedFilterTypes.Resolve(new AppSettingsData { EmbeddedFilterType = "bytes" }));   // a hand-edited word: the default
        Assert.Equal("the size the row shows: weights, vision projector and drafter", EmbeddedFilterTypes.Describe("file"));
        Assert.Equal("the weights' GGUF alone", EmbeddedFilterTypes.Describe("gguf"));
        Assert.Equal("gguf", AppSettings.Copy(new AppSettingsData { EmbeddedFilterType = "gguf" }).EmbeddedFilterType);
    }

    [Fact]
    public void Uncensored_KeepsTheUncensoredBuilds_AloneOrWithASize()
    {
        var uncensored = EmbeddedModelCatalog.Models.Where(m => m.Uncensored).ToList();
        Assert.NotEmpty(uncensored);
        Assert.All(uncensored, m => Assert.Contains("HauhauCS", m.Repository, StringComparison.Ordinal));   // every one is a HauhauCS build
        Assert.All(EmbeddedModelCatalog.Models.Where(m => m.Repository.StartsWith("HauhauCS/", StringComparison.Ordinal)), m => Assert.True(m.Uncensored, m.Id));

        // Aggressive exactly where the repository's own name says so (later on 2026-09-29): HauhauCS's E2B/E4B, Qwen3.6 35B A3B and Qwen3.8 27B.
        Assert.Equal(
            ["gemma-4-e2b-uncensored", "gemma-4-e4b-uncensored", "qwen3.6-35b-a3b-uncensored", "qwen3.8-27b-uncensored", "qwen3.8-27b-uncensored-q5"],
            EmbeddedModelCatalog.Models.Where(m => m.UncensoredKind == UncensoredKind.Aggressive).Select(m => m.Id).Order(StringComparer.Ordinal));
        Assert.All(uncensored.Where(m => m.UncensoredKind != UncensoredKind.Aggressive), m => Assert.Equal(UncensoredKind.Uncensored, m.UncensoredKind));
        Assert.All(EmbeddedModelCatalog.Models.Where(m => !m.Uncensored), m => Assert.Equal(UncensoredKind.None, m.UncensoredKind));

        var filter = new EmbeddedModelFilter(null, true);
        Assert.True(filter.Matches(Model("B Uncensored", 1_000_000_000), EmbeddedFilterType.File));
        Assert.False(filter.Matches(Model("B", 1_000_000_000), EmbeddedFilterType.File));

        var small = new EmbeddedModelFilter(8, true);
        Assert.True(small.Matches(Model("B Uncensored", 5_000_000_000), EmbeddedFilterType.File));
        Assert.False(small.Matches(Model("B Uncensored", 9_000_000_000), EmbeddedFilterType.File));
        Assert.False(small.Matches(Model("B", 5_000_000_000), EmbeddedFilterType.File));
    }

    [Fact]
    public void SortSize_IsItsOwnSwitch_LastOnBothLists_AndNoFilter()
    {
        // 2026-09-30 (the user's ask).
        Assert.Equal(4, EmbeddedModelFilter.SortSizeIndex());
        Assert.Equal(6, EmbeddedModelFilter.SortSizeIndex(withInstalled: true));

        var sorted = EmbeddedModelFilter.None.Press(4);
        Assert.Equal(new EmbeddedModelFilter(null, false, null, true), sorted);
        Assert.Equal([false, false, false, false, true], sorted.Buttons().Select(b => b.On));
        Assert.False(sorted.Active);                                          // it thins nothing
        Assert.Equal(EmbeddedModelFilter.None, sorted.Press(4));              // pressed again: dark
        Assert.Equal(new EmbeddedModelFilter(8, true, null, true), sorted.Press(0).Press(3));   // the filters left be

        var catalog = EmbeddedModelFilter.None.Press(6, withInstalled: true);
        Assert.True(catalog.SortSize);
        Assert.Equal([false, false, false, false, false, false, true], catalog.Buttons(withInstalled: true).Select(b => b.On));
        Assert.True(catalog.Press(5, withInstalled: true) is { Uncensored: true, SortSize: true });
    }

    [Fact]
    public void Arrange_KeepsTheListsOrder_UntilSortSize_ThenSmallestFirst_TheOtherRowsInTheirPlaces()
    {
        // Rows 0 and 3 are no model (a server on the network, the Claude API); 1, 2, 4 and 5 are models of 9, 3, 5 and 3 GB on the row.
        EmbeddedModel?[] rows = [null, Model("Big", 8_000_000_000), Model("Small", 2_000_000_000), null, Model("Mid", 4_000_000_000), Model("Small too", 2_000_000_000)];
        List<int> shown = [0, 1, 2, 3, 4, 5];

        Assert.Equal(shown, EmbeddedModelFilter.None.Arrange(shown, i => rows[i], EmbeddedFilterType.File));

        var sorted = new EmbeddedModelFilter(null, false, SortSize: true);
        Assert.Equal([0, 2, 5, 3, 4, 1], sorted.Arrange(shown, i => rows[i], EmbeddedFilterType.File));   // equal sizes keep their order
        Assert.Equal([2, 4], sorted.Arrange([4, 2], i => rows[i], EmbeddedFilterType.File));             // a filtered list sorts too
        Assert.Equal([0, 6], sorted.Arrange([0, 6], i => i < rows.Length ? rows[i] : null, EmbeddedFilterType.File));

        // The filter type's bytes: 7.5 GB of weights + 1 GB of projector + 2 GB of drafter is 10.5 GB on the row, 7.5 GB alone.
        EmbeddedModel[] pair = [Model("Drafted", 7_500_000_000, 2_000_000_000), Model("Plain", 8_000_000_000)];
        Assert.Equal([1, 0], sorted.Arrange([0, 1], i => pair[i], EmbeddedFilterType.File));
        Assert.Equal([0, 1], sorted.Arrange([0, 1], i => pair[i], EmbeddedFilterType.Gguf));
    }
}
