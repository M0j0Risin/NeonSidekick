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
        Assert.Equal(["8GB", "16GB", "32GB", "uncensored"], buttons.Select(b => b.Title));
        Assert.Equal(new char?[] { '1', '2', '3', 'u' }, buttons.Select(b => b.Key));
        Assert.All(buttons, b => Assert.False(b.On));
        Assert.False(EmbeddedModelFilter.None.Active);
        Assert.Equal("1 / 2 / 3 = 8 / 16 / 32 GB · U = uncensored", EmbeddedModelFilter.Keys);
        Assert.Equal("no model matches the filter", EmbeddedLlmText.NoFilterMatch);
    }

    [Fact]
    public void TheSizes_AreRadioButtons_ThatGoDarkPressedAgain_AndUncensoredIsItsOwn()
    {
        var eight = EmbeddedModelFilter.None.Press(0);
        Assert.Equal(new EmbeddedModelFilter(8, false), eight);
        Assert.Equal([true, false, false, false], eight.Buttons().Select(b => b.On));
        Assert.Equal(new EmbeddedModelFilter(16, false), eight.Press(1));    // another size takes its place
        Assert.Equal(EmbeddedModelFilter.None, eight.Press(0));             // the lit one pressed again: none

        var both = eight.Press(3);
        Assert.Equal(new EmbeddedModelFilter(8, true), both);
        Assert.Equal([true, false, false, true], both.Buttons().Select(b => b.On));
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
        Assert.Equal(["8GB", "16GB", "32GB", "installed", "uninstalled", "uncensored"], buttons.Select(b => b.Title));
        Assert.Equal(new char?[] { '1', '2', '3', 'i', 'n', 'u' }, buttons.Select(b => b.Key));
        Assert.Equal(4, EmbeddedModelFilter.None.Buttons().Count);
        Assert.Equal("1 / 2 / 3 = 8 / 16 / 32 GB · I / N = installed / uninstalled · U = uncensored", EmbeddedModelFilter.CatalogKeys);

        var installed = EmbeddedModelFilter.None.Press(3, withInstalled: true);
        Assert.Equal(new EmbeddedModelFilter(null, false, true), installed);
        Assert.Equal([false, false, false, true, false, false], installed.Buttons(withInstalled: true).Select(b => b.On));
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
}
