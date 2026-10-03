using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// The embedded model lists' filters (later on 2026-09-29, the user's ask): the 8GB / 16GB / 32GB radio buttons, the
/// uncensored switch, what they keep, measured by the size a row shows (<c>Embedded filter type</c> chose until 2026-10-02).
/// </summary>
public class EmbeddedModelFilterTests
{
    private static readonly EmbeddedSampling Sampling = new(1.0, 0.95, 64);

    // A model of <weights> bytes with a 1 GB projector and, optionally, a drafter.
    private static EmbeddedModel Model(string display, long weights, long drafter = 0, bool mtpHead = false) =>
        new("m", display, "Q4", "r/r", "c", new EmbeddedFile("m.gguf", weights, "0"), new EmbeddedFile("mmproj.gguf", 1_000_000_000, "0"), Sampling,
            drafter > 0 ? new EmbeddedFile("mtp.gguf", drafter, "0") : null, mtpHead);

    [Fact]
    public void TheButtons_ArePinned_AndStartDark()
    {
        // Uncensored last on X since later on 2026-09-30 (the user's ask), after sort size.
        var buttons = EmbeddedModelFilter.None.Buttons();
        Assert.Equal(["8GB", "16GB", "32GB", "drafter", "sort (name)", "uncensored"], buttons.Select(b => b.Title));
        Assert.Equal(new char?[] { '1', '2', '3', 'd', 's', 'x' }, buttons.Select(b => b.Key));
        Assert.All(buttons, b => Assert.False(b.On));
        Assert.False(EmbeddedModelFilter.None.Active);
        Assert.Equal("1 / 2 / 3 = GB · D = drafter · S = sort · X = unc", EmbeddedModelFilter.Keys);   // shortened later on 2026-09-30
        Assert.Equal("no model matches the filter", EmbeddedLlmText.NoFilterMatch);
    }

    [Fact]
    public void TheSizes_AreRadioButtons_ThatGoDarkPressedAgain_AndUncensoredIsItsOwn()
    {
        var eight = EmbeddedModelFilter.None.Press(0);
        Assert.Equal(new EmbeddedModelFilter(8, false), eight);
        Assert.Equal([true, false, false, false, false, false], eight.Buttons().Select(b => b.On));
        Assert.Equal(new EmbeddedModelFilter(16, false), eight.Press(1));    // another size takes its place
        Assert.Equal(EmbeddedModelFilter.None, eight.Press(0));             // the lit one pressed again: none

        Assert.Equal(5, EmbeddedModelFilter.UncensoredIndex());
        Assert.Equal(7, EmbeddedModelFilter.UncensoredIndex(withInstalled: true));
        var both = eight.Press(5);
        Assert.Equal(new EmbeddedModelFilter(8, true), both);
        Assert.Equal([true, false, false, false, false, true], both.Buttons().Select(b => b.On));
        Assert.Equal(new EmbeddedModelFilter(32, true), both.Press(2));     // the size moves, uncensored stays
        Assert.Equal(new EmbeddedModelFilter(8, false), both.Press(5));
        Assert.Equal(both, both.Press(9));                                  // no such button
        Assert.True(both.Active);
    }

    [Fact]
    public void TheCatalogsButtons_PutInstalledAndUninstalled_AfterTheSizes_ARadioPair()
    {
        // Later on 2026-09-29 (the user's ask): the catalog alone; /server keeps the others. Uninstalled on U since later on 2026-09-30.
        var buttons = EmbeddedModelFilter.None.Buttons(withInstalled: true);
        Assert.Equal(["8GB", "16GB", "32GB", "installed", "uninstalled", "drafter", "sort (name)", "uncensored"], buttons.Select(b => b.Title));
        Assert.Equal(new char?[] { '1', '2', '3', 'i', 'u', 'd', 's', 'x' }, buttons.Select(b => b.Key));
        Assert.Equal(6, EmbeddedModelFilter.None.Buttons().Count);
        Assert.Equal("1 / 2 / 3 = GB · I / U = inst / uninst · D = drafter · S = sort · X = unc", EmbeddedModelFilter.CatalogKeys);

        var installed = EmbeddedModelFilter.None.Press(3, withInstalled: true);
        Assert.Equal(new EmbeddedModelFilter(null, false, true), installed);
        Assert.Equal([false, false, false, true, false, false, false, false], installed.Buttons(withInstalled: true).Select(b => b.On));
        var uninstalled = installed.Press(4, withInstalled: true);
        Assert.Equal(new EmbeddedModelFilter(null, false, false), uninstalled);
        Assert.Equal(EmbeddedModelFilter.None, uninstalled.Press(4, withInstalled: true));   // the lit one again: none
        Assert.Equal(new EmbeddedModelFilter(16, true, false), uninstalled.Press(1, withInstalled: true).Press(7, withInstalled: true));   // the others left be
        Assert.True(uninstalled.Active);

        var small = new EmbeddedModelFilter(8, false, true);
        Assert.True(small.Matches(Model("A", 5_000_000_000), installed: true));
        Assert.False(small.Matches(Model("A", 5_000_000_000), installed: false));
        Assert.False(small.Matches(Model("A", 9_000_000_000), installed: true));
        Assert.True(new EmbeddedModelFilter(null, false, false).Matches(Model("A", 5_000_000_000), installed: false));
    }

    [Fact]
    public void ASize_KeepsTheModelsAtMostThatBig_AsTheRowReadsThem()
    {
        var filter = new EmbeddedModelFilter(8, false);
        Assert.True(filter.Matches(Model("A", 6_900_000_000)));    // 7.9 GB
        Assert.True(filter.Matches(Model("A", 7_040_000_000)));    // 8.04 GB reads "8 GB": it passes
        Assert.False(filter.Matches(Model("A", 7_060_000_000)));   // "8.1 GB"
        Assert.True(new EmbeddedModelFilter(16, false).Matches(Model("A", 7_060_000_000)));
        Assert.True(EmbeddedModelFilter.None.Matches(Model("A", 90_000_000_000)));

        // The row's bytes, the drafter and projector too (the weights alone were a choice until 2026-10-02): 7.5 + 1 + 0.5 is 9 GB.
        Assert.False(filter.Matches(Model("A", 7_500_000_000, 500_000_000)));
        Assert.Equal(9_000_000_000, EmbeddedModelFilter.Bytes(Model("A", 7_500_000_000, 500_000_000)));
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
        Assert.True(filter.Matches(Model("B Uncensored", 1_000_000_000)));
        Assert.False(filter.Matches(Model("B", 1_000_000_000)));

        // Dark, the other half (later on 2026-09-30, the user's ask): the uncensored builds are left out by default.
        Assert.False(EmbeddedModelFilter.None.Matches(Model("B Uncensored", 1_000_000_000)));
        Assert.True(EmbeddedModelFilter.None.Matches(Model("B", 1_000_000_000)));
        Assert.All(EmbeddedModelCatalog.Models, m => Assert.NotEqual(m.Uncensored, EmbeddedModelFilter.None.Matches(m)));

        var small = new EmbeddedModelFilter(8, true);
        Assert.True(small.Matches(Model("B Uncensored", 5_000_000_000)));
        Assert.False(small.Matches(Model("B Uncensored", 9_000_000_000)));
        Assert.False(small.Matches(Model("B", 5_000_000_000)));
    }

    [Fact]
    public void For_LightsUncensored_WhenTheModelInUseIsAnUncensoredBuild()
    {
        // Later on 2026-09-30 (the user's pick): the row the pane opens on is shown.
        Assert.Equal(new EmbeddedModelFilter(null, true), EmbeddedModelFilter.For(Model("B Uncensored", 1_000_000_000)));
        Assert.Equal(EmbeddedModelFilter.None, EmbeddedModelFilter.For(Model("B", 1_000_000_000)));
        Assert.Equal(EmbeddedModelFilter.None, EmbeddedModelFilter.For(null));
    }

    [Fact]
    public void SortSize_IsItsOwnSwitch_AfterDrafter_AndNoFilter()
    {
        // 2026-09-30 (the user's ask); last until uncensored moved after it later that day.
        Assert.Equal(4, EmbeddedModelFilter.SortSizeIndex());
        Assert.Equal(6, EmbeddedModelFilter.SortSizeIndex(withInstalled: true));

        var sorted = EmbeddedModelFilter.None.Press(4);
        Assert.Equal(new EmbeddedModelFilter(null, false, null, true), sorted);
        Assert.All(sorted.Buttons(), b => Assert.False(b.On));   // named for the order shown (2026-10-02), never lit
        Assert.Equal("sort (size)", sorted.Buttons()[EmbeddedModelFilter.SortSizeIndex()].Title);
        Assert.Equal("sort (name)", EmbeddedModelFilter.None.Buttons()[EmbeddedModelFilter.SortSizeIndex()].Title);
        Assert.Equal(("sort (size)", "sort (name)"), (EmbeddedModelFilter.SortSizeButton, EmbeddedModelFilter.SortNameButton));
        Assert.False(sorted.Active);                                          // it thins nothing
        Assert.Equal(EmbeddedModelFilter.None, sorted.Press(4));              // pressed again: name order
        Assert.Equal(new EmbeddedModelFilter(8, true, null, true), sorted.Press(0).Press(5));   // the filters left be

        var catalog = EmbeddedModelFilter.None.Press(6, withInstalled: true);
        Assert.True(catalog.SortSize);
        Assert.All(catalog.Buttons(withInstalled: true), b => Assert.False(b.On));
        Assert.Equal("sort (size)", catalog.Buttons(withInstalled: true)[EmbeddedModelFilter.SortSizeIndex(withInstalled: true)].Title);
        Assert.True(catalog.Press(7, withInstalled: true) is { Uncensored: true, SortSize: true });
    }

    [Fact]
    public void Drafter_IsItsOwnSwitch_BeforeSortSize_AndKeepsTheModelsThatDraft()
    {
        // Later on 2026-09-30 (the user's ask): a drafter file (MTP or DFlash) or a head built into the weights.
        Assert.Equal(3, EmbeddedModelFilter.DrafterIndex());
        Assert.Equal(5, EmbeddedModelFilter.DrafterIndex(withInstalled: true));

        var drafter = EmbeddedModelFilter.None.Press(3);
        Assert.Equal(new EmbeddedModelFilter(null, false, null, false, true), drafter);
        Assert.Equal([false, false, false, true, false, false], drafter.Buttons().Select(b => b.On));
        Assert.True(drafter.Active);
        Assert.Equal(EmbeddedModelFilter.None, drafter.Press(3));             // pressed again: dark
        Assert.True(drafter.Press(0).Press(5).Press(4) is { MaxGb: 8, Uncensored: true, SortSize: true, Drafter: true });   // the others left be
        Assert.True(EmbeddedModelFilter.None.Press(5, withInstalled: true).Press(3, withInstalled: true) is { Drafter: true, Installed: true });

        Assert.True(drafter.Matches(Model("A", 5_000_000_000, 500_000_000)));   // its own drafter file
        Assert.True(drafter.Matches(Model("A", 5_000_000_000, mtpHead: true)));  // built in
        Assert.False(drafter.Matches(Model("A", 5_000_000_000)));
        Assert.False(new EmbeddedModelFilter(8, false, Drafter: true).Matches(Model("A", 9_000_000_000, mtpHead: true)));
        // Each half by its own uncensored state (later on 2026-09-30).
        Assert.All(EmbeddedModelCatalog.Models, m => Assert.Equal(m.HasMtp, (drafter with { Uncensored = m.Uncensored }).Matches(m)));
    }

    [Fact]
    public void Arrange_KeepsTheListsOrder_UntilSortSize_ThenSmallestFirst_TheOtherRowsInTheirPlaces()
    {
        // Rows 0 and 3 are no model (a server on the network, the Claude API); 1, 2, 4 and 5 are models of 9, 3, 5 and 3 GB on the row.
        EmbeddedModel?[] rows = [null, Model("Big", 8_000_000_000), Model("Small", 2_000_000_000), null, Model("Mid", 4_000_000_000), Model("Small too", 2_000_000_000)];
        List<int> shown = [0, 1, 2, 3, 4, 5];

        Assert.Equal(shown, EmbeddedModelFilter.None.Arrange(shown, i => rows[i]));

        var sorted = new EmbeddedModelFilter(null, false, SortSize: true);
        Assert.Equal([0, 2, 5, 3, 4, 1], sorted.Arrange(shown, i => rows[i]));   // equal sizes keep their order
        Assert.Equal([2, 4], sorted.Arrange([4, 2], i => rows[i]));             // a filtered list sorts too
        Assert.Equal([0, 6], sorted.Arrange([0, 6], i => i < rows.Length ? rows[i] : null));

        // The row's bytes, never the weights alone (the only way since 2026-10-02): 7.5 GB of weights + 1 GB of projector + 2 GB
        // of drafter is 10.5 GB on the row, so it sorts after 9 GB.
        EmbeddedModel[] pair = [Model("Drafted", 7_500_000_000, 2_000_000_000), Model("Plain", 8_000_000_000)];
        Assert.Equal([1, 0], sorted.Arrange([0, 1], i => pair[i]));
    }
}
