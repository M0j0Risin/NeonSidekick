using NeonSidekick.App;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The Embedded LLM tab of <c>/settings</c> (2026-09-29): its live rows, the catalog door, the typed rows and the backend picker.</summary>
public partial class SettingsMenuTests
{
    private (SettingsMenu Menu, ScreenPane Pane, FakeEmbeddedLlm Embedded) EmbeddedPane(FakeEmbeddedLlm? embedded = null)
    {
        _console.Profile.Width = 240;
        var (menu, pane) = PaneMenu();
        embedded ??= new FakeEmbeddedLlm();
        menu.EmbeddedLlm = embedded;
        return (menu, pane, embedded);
    }

    [Fact]
    public void TheTab_IsAfterGeneral_WithItsNineRows_AllButTheFilterTypeReconnecting()
    {
        int tab = (int)SettingsTab.Embedded;
        Assert.Equal("Embedded", SettingsMenu.TabTitles[tab]);
        Assert.Equal("General", SettingsMenu.TabTitles[tab - 1]);   // second since later on 2026-09-29 (the user's order); after STT until then, the Claude (API) tab between until it went to /tools
        Assert.Equal("LLM", SettingsMenu.TabTitles[tab + 1]);
        // The switch first and MTP last (2026-09-29, the user's asks); the filter type under the catalog, the VRAM budget under
        // the GPU layers (later that day), the HF download type under the filter type (2026-09-30).
        Assert.Equal([SettingsField.EmbeddedLlmServer, SettingsField.EmbeddedModels, SettingsField.EmbeddedFilterType, SettingsField.EmbeddedHfDownloadType, SettingsField.EmbeddedBackend, SettingsField.EmbeddedContextSize, SettingsField.EmbeddedGpuLayers, SettingsField.EmbeddedVramBudget, SettingsField.EmbeddedVision, SettingsField.EmbeddedDrafter], SettingsMenu.TabFields[tab]);
        var reconnecting = SettingsMenu.TabFields[tab].Where(f => f is not (SettingsField.EmbeddedFilterType or SettingsField.EmbeddedHfDownloadType)).ToList();
        Assert.All(reconnecting, f => Assert.True(SettingsMenu.IsLlmField(f), f.ToString()));
        Assert.All(reconnecting, f => Assert.True(SettingsMenu.RefusedMidTurn(f), f.ToString()));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.EmbeddedFilterType));   // display only: the lists read it as they open
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.EmbeddedFilterType));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.EmbeddedHfDownloadType));   // read as each download starts
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.EmbeddedHfDownloadType));
        Assert.True(SettingsMenu.IsToggle(SettingsField.EmbeddedVision) && SettingsMenu.IsToggle(SettingsField.EmbeddedLlmServer) && SettingsMenu.IsToggle(SettingsField.EmbeddedDrafter));
        Assert.False(SettingsMenu.IsToggle(SettingsField.EmbeddedBackend));
        Assert.Equal(["Embedded LLM server enabled", "Embedded models", "Embedded filter type", "Embedded HF download type", "Embedded backend", "Embedded context size", "Embedded GPU layers", "Embedded VRAM budget", "Embedded vision", "Embedded drafter"], SettingsMenu.TabFields[tab].Select(SettingsMenu.FieldName));
        var data = new AppSettingsData();
        Assert.True(data.EmbeddedLlmServer && data.EmbeddedDrafter);   // both on by default
        Assert.Equal(("on", "on"), (SettingsMenu.FieldValue(SettingsField.EmbeddedLlmServer, data, "C:\\p"), SettingsMenu.FieldValue(SettingsField.EmbeddedDrafter, data, "C:\\p")));
        var copy = AppSettings.Copy(new AppSettingsData { EmbeddedLlmServer = false, EmbeddedDrafter = false });
        Assert.False(copy.EmbeddedLlmServer || copy.EmbeddedDrafter);
    }

    // The capability columns of a model with a drafter, vision and tools (2026-09-29: ⚡, then 👁️ and 🛠️), and of one without a drafter.
    private const string Marks = "  ⚡  👁️  🛠️";
    private const string NoDrafter = "      👁️  🛠️";

    [Fact]
    public void TheStaticValues_ArePinned()
    {
        var data = new AppSettingsData();
        Assert.Equal(SettingsMenu.EmbeddedModelsDoorLabel, SettingsMenu.FieldValue(SettingsField.EmbeddedModels, data, "C:\\p"));
        Assert.Equal("auto", SettingsMenu.FieldValue(SettingsField.EmbeddedBackend, data, "C:\\p"));
        // Fit by default since later on 2026-09-29 (the user's call; 32768 until then).
        Assert.Equal("fit", SettingsMenu.EmbeddedContextFitLabel);
        Assert.Equal(SettingsMenu.EmbeddedContextFitLabel, SettingsMenu.FieldValue(SettingsField.EmbeddedContextSize, data, "C:\\p"));
        Assert.Equal("32,768 tokens", SettingsMenu.FieldValue(SettingsField.EmbeddedContextSize, new AppSettingsData { EmbeddedContextSize = 32_768 }, "C:\\p"));
        Assert.Equal("91 %", SettingsMenu.FieldValue(SettingsField.EmbeddedVramBudget, data, "C:\\p"));   // 91 by default since 2026-09-30 (the user's call)
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.EmbeddedVramBudget, new AppSettingsData { EmbeddedVramBudget = 0 }, "C:\\p"));
        Assert.Equal("92 %", SettingsMenu.FieldValue(SettingsField.EmbeddedVramBudget, new AppSettingsData { EmbeddedVramBudget = 92 }, "C:\\p"));
        Assert.Equal("file", SettingsMenu.FieldValue(SettingsField.EmbeddedFilterType, data, "C:\\p"));
        Assert.Equal("gguf  " + Theme.DimMarkup("the weights' GGUF alone"), SettingsMenu.EmbeddedFilterTypeLabel("gguf"));
        Assert.Equal("auto", SettingsMenu.FieldValue(SettingsField.EmbeddedGpuLayers, data, "C:\\p"));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.EmbeddedVision, data, "C:\\p"));
        Assert.Equal(EmbeddedLlmText.UrlDisplay, SettingsMenu.FieldValue(SettingsField.LlmUrl, new AppSettingsData { LlmUrl = "http://embedded.localhost/v1" }, "C:\\p"));
        Assert.Equal(EmbeddedLlmText.UrlDisplay, SettingsMenu.FieldValue(SettingsField.LlmUrl, new AppSettingsData { LlmUrl = "embedded" }, "C:\\p"));

        var e2b = EmbeddedModelCatalog.Find("gemma-4-e2b")!;
        // Its MTP drafter counts and shows as the drafter column (2026-09-29); the quantisation column fits COMPACT-LOW.
        Assert.Equal("Gemma 4 E2B                     [#9A8BB8]UD-Q4_K_XL   installed · 4.3 GB[/]" + Marks, SettingsMenu.EmbeddedModelLabel(e2b, EmbeddedModelState.Installed));
        Assert.Equal("Gemma 4 12B                     [#9A8BB8]BF16         download  · 24.5 GB[/]" + Marks, SettingsMenu.EmbeddedModelLabel(EmbeddedModelCatalog.Find("gemma-4-12b-bf16")!, EmbeddedModelState.Absent));   // the · under the installed rows' (2026-09-29)
        Assert.Equal("Qwen3.6 35B A3B                 [#9A8BB8]UD-Q4_K_XL   download  · 23.3 GB[/]" + NoDrafter, SettingsMenu.EmbeddedModelLabel(EmbeddedModelCatalog.Find("qwen3.6-35b-a3b")!, EmbeddedModelState.Absent));   // no drafter: its slot is blank, vision and tools keep their columns
        Assert.Equal("Qwen3.8 27B NVFP4               [#9A8BB8]COMPACT-LOW  download  · 16.1 GB[/]" + Marks, SettingsMenu.EmbeddedModelLabel(EmbeddedModelCatalog.Find("qwen3.8-27b-nvfp4-compact-low")!, EmbeddedModelState.Absent));   // the head in its weights
        // The list pads every detail to the widest, so the drafter column is one column down the list.
        var labels = SettingsMenu.EmbeddedModelLabels([e2b, EmbeddedModelCatalog.Find("gemma-4-e4b-qat")!], m => m == e2b ? EmbeddedModelState.Installed : new EmbeddedModelState(EmbeddedModelStateKind.Partial, 42));
        Assert.EndsWith("installed · 4.3 GB[/]      " + Marks, labels[0], StringComparison.Ordinal);
        Assert.EndsWith("paused    · 5.3 GB · 42%[/]" + Marks, labels[1], StringComparison.Ordinal);
        Assert.Equal(2, TextCells.Width(EmbeddedLlmText.DrafterGlyph));
        Assert.Equal(2, TextCells.Width(EmbeddedLlmText.VisionGlyph));   // 2026-09-29: the vision and tools columns
        Assert.Equal(2, TextCells.Width(EmbeddedLlmText.ToolsGlyph));
        Assert.Equal(12, TextCells.Width(Marks));
        Assert.Equal(12, TextCells.Width(NoDrafter));
        Assert.Equal("", EmbeddedLlmText.CapabilityColumns(null, "x", 10));   // a scanned server's row has none
        Assert.Equal(Marks, EmbeddedLlmText.CapabilityColumns(e2b, "", 0));
        Assert.Equal("  ⚡  " + EmbeddedLlmText.VisionGlyph, EmbeddedLlmText.CapabilityColumns(e2b with { ToolCalls = false }, "", 0));   // a blank last slot leaves no trailing blanks
        Assert.True(EmbeddedModelCatalog.Models.All(m => m.Vision && m.ToolCalls));   // every catalog model does both today
        Assert.Equal("Gemma 4 26B A4B QAT Uncensored  ", SettingsMenu.EmbeddedModelLabel(EmbeddedModelCatalog.Find("gemma-4-26b-a4b-qat-uncensored")!, EmbeddedModelState.Absent)[..32]);   // the longest name, two to spare
        Assert.Equal(32, SettingsMenu.EmbeddedModelNameWidth);
        Assert.Equal("Remove (4.3 GB)", SettingsMenu.RemoveRow(e2b));
        Assert.Equal("Install (download 4.3 GB + llama.cpp runtime 577 MB)", SettingsMenu.InstallRow(e2b, 577_081_932));
        Assert.Equal("vulkan  [#9A8BB8]any GPU: NVIDIA, AMD, Intel[/]", SettingsMenu.EmbeddedBackendLabel("vulkan"));
    }

    [Fact]
    public void AnEmbeddedServerRow_ShowsTheModel_WhereAUrlWouldBe()
    {
        var row = new Llm.LlmServer(EmbeddedEndpoint.BaseUrl, EmbeddedEndpoint.ServerName, new Llm.ProbeResult(false, ["gemma-4-e2b"], "download  · 4.2 GB"));

        // The name padded as the catalog pads it, the quantisation dim after it (2026-09-29: the 12B's builds share a name).
        Assert.Equal("Embedded   " + Theme.ColorMarkup(Theme.Ink, "Gemma 4 E2B".PadRight(SettingsMenu.EmbeddedModelNameWidth)) + Theme.DimMarkup("UD-Q4_K_XL  download  · 4.2 GB") + Marks, SettingsMenu.ServerLabel(row));
        // In a list the details are padded to the widest, the drafter column after; a scanned server's row has none.
        var lm = new Llm.LlmServer(new Uri("http://127.0.0.1:1234/v1"), "LM Studio", new Llm.ProbeResult(true, ["m"], "1 chat model, and more"));
        var labels = SettingsMenu.ServerLabels([lm, row]);
        Assert.EndsWith("  1 chat model, and more[/]", labels[0], StringComparison.Ordinal);
        Assert.EndsWith("download  · 4.2 GB[/]    " + Marks, labels[1], StringComparison.Ordinal);
        Assert.Equal("LLM servers: Embedded Gemma 4 E2B UD-Q4_K_XL", SettingsMenu.ServerListLine([row]));
    }

    [Fact]
    public async Task OnThePane_TheRows_ReadTheDiskAndTheMachine()
    {
        var (menu, pane, _) = EmbeddedPane(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        GoTo(SettingsTab.Embedded);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("1 of 41 installed (4.3 GB)", _console.Output);
        Assert.Contains("auto (cuda: fake driver)", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_UseNow_OnAnInstalledModel_ClosesThePane_AndHandsItOver()
    {
        var (menu, pane, embedded) = EmbeddedPane(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Enter);            // Embedded models (under the switch since 2026-09-29): the catalog, on Gemma 4 12B
        Down(18);
        Push(Keys.Enter);                       // Gemma 4 E2B, the nineteenth row since the 26B A4B and 31B builds: its page
        Push(Keys.Enter);                       // Use now

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal("gemma-4-e2b", menu.TakePendingEmbeddedModel()!.Id);
        Assert.Null(menu.TakePendingEmbeddedModel());   // taken once
        Assert.Contains(SettingsMenu.UseNowRow, _console.Output);
        Assert.Contains(SettingsMenu.RemoveRow(EmbeddedModelCatalog.Find("gemma-4-e2b")!), _console.Output);
        Assert.Equal("", _settings.Current.LlmUrl);   // the screen saves it, after an install if need be
        Assert.Empty(embedded.Removes);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheCatalogsFilters_ThinTheRows_ASizeAndUncensoredTogether()
    {
        // Later on 2026-09-29 (the user's ask): 8GB (key 1) and uncensored (U) lit, the first row is the first catalog model
        // at most 8 GB that is uncensored.
        var (menu, pane, _) = EmbeddedPane();
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Enter);            // the catalog
        Push(Keys.Char('1'), Keys.Char('U'));
        Push(Keys.Enter);                       // the first row left: its page
        Push(Keys.Enter);                       // Install

        await menu.ShowAsync(CancellationToken.None);

        var filter = new EmbeddedModelFilter(8, true);
        var first = EmbeddedModelCatalog.Models.First(m => filter.Matches(m, EmbeddedFilterType.File));
        Assert.True(first.Uncensored);
        Assert.Equal(first.Id, menu.TakePendingEmbeddedModel()!.Id);
        Assert.Contains(" 8GB    16GB    32GB    installed    uninstalled    uncensored ", _console.Output);   // the catalog's pair between (later on 2026-09-29)
        Assert.Contains(EmbeddedModelFilter.CatalogKeys, _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_SortSize_PutsTheSmallestModelFirst_AndPressedAgain_TheCatalogsOrder()
    {
        // 2026-09-30 (the user's ask): S lights sort size, the smallest model on top; S again, the catalog's order.
        var big = EmbeddedModelCatalog.Find("gemma-4-31b")!;
        var small = EmbeddedModelCatalog.Find("gemma-4-e2b")!;
        var (menu, pane, _) = EmbeddedPane(new FakeEmbeddedLlm { Catalog = [big, small] });
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Enter);            // the catalog: 31B, then E2B
        Push(Keys.Char('s'), Keys.Home);        // sort size: E2B, then 31B; the top row
        Push(Keys.Enter, Keys.Escape);          // E2B's page, closed
        Push(Keys.Char('s'), Keys.Home);        // dark again: 31B on top
        Push(Keys.Enter);                       // 31B's page
        Push(Keys.Enter);                       // Install

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(big.Id, menu.TakePendingEmbeddedModel()!.Id);
        Assert.Contains(" › " + small.Display, _console.Output);   // the sorted top row's page came first
        Assert.Contains(" uncensored    drafter    sort size ", _console.Output);   // drafter between (later on 2026-09-30)
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_Drafter_KeepsTheModelsThatDraft()
    {
        // Later on 2026-09-30 (the user's ask): D lights drafter, and HauhauCS's E2B, which has none, goes.
        var plain = EmbeddedModelCatalog.Find("gemma-4-e2b-uncensored")!;
        var drafted = EmbeddedModelCatalog.Find("gemma-4-e2b")!;
        Assert.False(plain.HasMtp);
        Assert.True(drafted.HasMtp);
        var (menu, pane, _) = EmbeddedPane(new FakeEmbeddedLlm { Catalog = [plain, drafted] });
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Enter);            // the catalog: the uncensored E2B, then Unsloth's
        Push(Keys.Char('d'), Keys.Home);        // drafter: Unsloth's alone; the top row
        Push(Keys.Enter);                       // its page
        Push(Keys.Enter);                       // Install

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(drafted.Id, menu.TakePendingEmbeddedModel()!.Id);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_InstalledAndUninstalled_AreARadioPair_OnTheDisksState()
    {
        // Later on 2026-09-29 (the user's ask): I keeps the installed models, N the others, N again every one.
        var (menu, pane, _) = EmbeddedPane(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Enter);            // the catalog
        Push(Keys.Char('i'));                   // installed: E2B alone, the cursor on it
        Push(Keys.Enter, Keys.Escape);          // its page (Use now, Remove, Back), closed
        Push(Keys.Char('n'));                   // uninstalled: E2B gone, the first row the catalog's first
        Push(Keys.Enter);                       // Gemma 4 12B's page
        Push(Keys.Enter);                       // Install

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("gemma-4-12b", menu.TakePendingEmbeddedModel()!.Id);
        Assert.Contains(SettingsMenu.UseNowRow, _console.Output);   // the installed one's page came first
        pane.Dispose();
    }

    [Fact]
    public void TheUncensoredColumn_MarksEachKind_ANormalModelBlank()
    {
        // Later on 2026-09-29 (the user's picks): ⛓️‍💥 an uncensored build, 💢 an aggressive one, after 🛠️.
        Assert.Equal("⛓️‍💥", EmbeddedLlmText.UncensoredGlyph);
        Assert.Equal("💢", EmbeddedLlmText.AggressiveGlyph);
        Assert.Equal(2, TextCells.Width(EmbeddedLlmText.UncensoredGlyph));   // one glyph in two cells, as Windows Terminal draws the ZWJ sequence
        var balanced = EmbeddedModelCatalog.Find("gemma-4-12b-qat-uncensored")!;
        var aggressive = EmbeddedModelCatalog.Find("gemma-4-e2b-uncensored")!;
        var normal = EmbeddedModelCatalog.Find("gemma-4-12b")!;
        Assert.Equal(Marks + "  ⛓️‍💥", EmbeddedLlmText.CapabilityColumns(balanced, "", 0));
        Assert.EndsWith("  👁️  🛠️  💢", EmbeddedLlmText.CapabilityColumns(aggressive, "", 0));
        Assert.Equal(Marks, EmbeddedLlmText.CapabilityColumns(normal, "", 0));
        Assert.EndsWith("  💢", SettingsMenu.EmbeddedModelLabel(aggressive, EmbeddedModelState.Absent));
    }

    [Fact]
    public async Task OnThePane_ASizePressedAgain_ShowsEveryModel_AndOneThatPassesNothingSaysSo()
    {
        var big = EmbeddedModelCatalog.Find("gemma-4-31b")!;
        var small = EmbeddedModelCatalog.Find("gemma-4-e2b")!;
        var (menu, pane, _) = EmbeddedPane(new FakeEmbeddedLlm { Catalog = [big, small] });
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Enter);            // the catalog: 31B, then E2B
        Push(Keys.Char('1'));                   // 8GB: E2B alone, the cursor on it
        Push(Keys.Char('1'));                   // again: every model, the cursor kept on E2B
        Push(Keys.Char('u'));                   // uncensored: neither is
        Push(Keys.Enter);                       // the no-match row: nothing opens
        Push(Keys.Char('u'));                   // dark again: both, the cursor still on E2B
        Push(Keys.Enter);                       // E2B's page
        Push(Keys.Enter);                       // Install

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(small.Id, menu.TakePendingEmbeddedModel()!.Id);
        Assert.Contains(EmbeddedLlmText.NoFilterMatch, _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_Install_OnAMissingModel_ClosesThePane_AndHandsItOver()
    {
        var (menu, pane, embedded) = EmbeddedPane(new FakeEmbeddedLlm { RuntimeBytes = 577_081_932 });
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Enter);            // the catalog, on Gemma 4 12B
        Down(21);
        Push(Keys.Enter);                       // Gemma 4 E4B QAT, the twenty-second row: its page
        Push(Keys.Enter);                       // Install

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("gemma-4-e4b-qat", menu.TakePendingEmbeddedModel()!.Id);
        Assert.Contains(SettingsMenu.InstallRow(EmbeddedModelCatalog.Find("gemma-4-e4b-qat")!, 577_081_932), _console.Output);
        Assert.Empty(embedded.Installs);   // the screen installs it, under the spinner
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_Remove_AsksFirst_ThenDeletes_AndStaysOnTheCatalog()
    {
        var (menu, pane, embedded) = EmbeddedPane(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Enter);            // the catalog, on Gemma 4 12B
        Down(18);
        Push(Keys.Enter);                       // Gemma 4 E2B, the nineteenth row
        Push(Keys.Down, Keys.Enter);            // Remove (4.2 GB)
        Push(Keys.Down, Keys.Enter);            // Yes (the cursor opens on No)
        Push(Keys.Escape, Keys.Escape);         // out of the catalog, then the settings

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["gemma-4-e2b"], embedded.Removes);
        Assert.Contains(EmbeddedLlmText.RemoveQuestion(EmbeddedModelCatalog.Find("gemma-4-e2b")!), _console.Output);
        Assert.Contains(EmbeddedLlmText.Removed(EmbeddedModelCatalog.Find("gemma-4-e2b")!), _console.Output);
        Assert.Null(menu.TakePendingEmbeddedModel());
        pane.Dispose();
    }

    /// <summary>
    /// A partly downloaded model offers Remove too (2026-09-29, the user's ask): after a yes the screen's hook runs first — it
    /// stops a download of that model under way — then the folder goes, and the catalog stays.
    /// </summary>
    [Fact]
    public async Task OnThePane_APartialModel_OffersRemove_TheHookFirst_ThenTheFolder()
    {
        var (menu, pane, embedded) = EmbeddedPane(new FakeEmbeddedLlm().Partial("gemma-4-e4b-qat", 40));
        var removesAtHook = new List<int>();
        menu.BeforeEmbeddedRemove = (model, _) =>
        {
            Assert.Equal("gemma-4-e4b-qat", model.Id);
            removesAtHook.Add(embedded.Removes.Count);
            return Task.CompletedTask;
        };
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Enter);            // the catalog, on Gemma 4 12B
        Down(21);
        Push(Keys.Enter);                       // Gemma 4 E4B QAT, the twenty-second row: its page
        Push(Keys.Down, Keys.Enter);            // Remove (the partial download)
        Push(Keys.Down, Keys.Enter);            // Yes
        Push(Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal(["gemma-4-e4b-qat"], embedded.Removes);
        Assert.Equal([0], removesAtHook);       // the hook before the removal
        Assert.Contains(SettingsMenu.RemovePartialRow, _console.Output);
        Assert.Contains(EmbeddedLlmText.RemovePartialQuestion(EmbeddedModelCatalog.Find("gemma-4-e4b-qat")!), _console.Output);
        Assert.Null(menu.TakePendingEmbeddedModel());
        pane.Dispose();
    }

    /// <summary>
    /// Removing the model the saved LLM names (2026-09-29, the user's ask): the URL and model are cleared with a notice, and
    /// the menu reports an LLM change so the screen reconnects — to none. Another model's removal leaves them be.
    /// </summary>
    [Fact]
    public async Task OnThePane_RemovingTheModelInUse_ClearsTheLlmUrlAndModel_AndAsksForAReconnect()
    {
        _settings.Update(d =>
        {
            d.LlmUrl = "embedded";
            d.LlmModel = "gemma-4-e2b";
        });
        var (menu, pane, embedded) = EmbeddedPane(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Enter);            // the catalog
        Down(18);
        Push(Keys.Enter);                       // Gemma 4 E2B
        Push(Keys.Down, Keys.Enter);            // Remove
        Push(Keys.Down, Keys.Enter);            // Yes
        Push(Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal(["gemma-4-e2b"], embedded.Removes);
        Assert.Equal(("", ""), (_settings.Current.LlmUrl, _settings.Current.LlmModel));
        Assert.Contains(SettingsMenu.EmbeddedLlmClearedNotice, _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_RemovingAnotherModel_LeavesTheLlmBe()
    {
        _settings.Update(d =>
        {
            d.LlmUrl = "embedded";
            d.LlmModel = "gemma-4-12b";
        });
        var (menu, pane, embedded) = EmbeddedPane(new FakeEmbeddedLlm().Installed("gemma-4-e2b", "gemma-4-12b"));
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Enter);
        Down(18);
        Push(Keys.Enter);                       // Gemma 4 E2B
        Push(Keys.Down, Keys.Enter);
        Push(Keys.Down, Keys.Enter);
        Push(Keys.Escape, Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal(["gemma-4-e2b"], embedded.Removes);
        Assert.Equal(("embedded", "gemma-4-12b"), (_settings.Current.LlmUrl, _settings.Current.LlmModel));
        Assert.DoesNotContain(SettingsMenu.EmbeddedLlmClearedNotice, _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheTypedRows_CheckTheirRanges()
    {
        var (menu, pane, _) = EmbeddedPane();
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // Embedded context size (the switch first since 2026-09-29, the filter type above since later that day, the HF download type since 2026-09-30)
        Backspace(10);
        _console.Input.PushText("100");
        Push(Keys.Enter);                       // refused
        Push(Keys.Enter);
        Backspace(10);
        _console.Input.PushText("16384");
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Enter);            // Embedded GPU layers
        Backspace(10);
        _console.Input.PushText("ALL");
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Enter);            // Embedded VRAM budget (later on 2026-09-29)
        Backspace(10);
        _console.Input.PushText("49");
        Push(Keys.Enter);                       // refused
        Push(Keys.Enter);
        Backspace(10);
        _console.Input.PushText("92 %");
        Push(Keys.Enter);
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal(16_384, _settings.Current.EmbeddedContextSize);
        Assert.Equal("all", _settings.Current.EmbeddedGpuLayers);
        Assert.Equal(92, _settings.Current.EmbeddedVramBudget);
        Assert.Contains("Embedded context size " + EmbeddedContextSize.Error + "; keeping 0.", _console.Output);   // fit, the default since later on 2026-09-29
        Assert.Contains("Embedded VRAM budget " + EmbeddedVramBudget.Error + "; keeping 91.", _console.Output);   // the default since 2026-09-30
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheBackendRow_IsAPicker()
    {
        var (menu, pane, _) = EmbeddedPane();
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // Embedded backend: the page opens on auto
        Push(Keys.Down, Keys.Down, Keys.Enter); // vulkan
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal("vulkan", _settings.Current.EmbeddedBackend);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheFilterTypeRow_IsAPicker_NeedingNoReconnect()
    {
        // Later on 2026-09-29 (the user's ask): file or gguf, under the catalog; display only.
        var (menu, pane, _) = EmbeddedPane();
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Down, Keys.Enter); // Embedded filter type: the page opens on file
        Push(Keys.Down, Keys.Enter);            // gguf
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal("gguf", _settings.Current.EmbeddedFilterType);
        Assert.Contains("file  the size the row shows: weights, vision projector and drafter", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheHfDownloadTypeRow_IsAPicker_NeedingNoReconnect()
    {
        // 2026-09-30 (the user's ask): single or parallel, under the filter type; read as each download starts.
        var (menu, pane, _) = EmbeddedPane();
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // Embedded HF download type: the page opens on parallel
        Push(Keys.Up, Keys.Enter);              // single
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal("single", _settings.Current.EmbeddedHfDownloadType);
        Assert.Contains("parallel  " + NeonSidekick.EmbeddedLlm.EmbeddedHfDownloadTypes.Describe("parallel"), _console.Output);
        Assert.Equal("single", SettingsMenu.FieldValue(SettingsField.EmbeddedHfDownloadType, _settings.Current, "C:\\p"));
        pane.Dispose();
    }

    [Fact]
    public async Task Open_EmbeddedModels_StartsInTheCatalog_AnInstallClosingThePane()
    {
        // 2026-09-30 (the user's ask): the app's start with nothing to connect to opens the catalog itself.
        var (menu, pane, _) = EmbeddedPane();
        Push(Keys.Enter);                       // the catalog's first row: its page
        Push(Keys.Enter);                       // Install

        await menu.ShowAsync(CancellationToken.None, midTurn: false, SettingsField.EmbeddedModels);

        Assert.Equal(EmbeddedModelCatalog.Models[0].Id, menu.TakePendingEmbeddedModel()!.Id);
        pane.Dispose();
    }

    [Fact]
    public async Task Open_EmbeddedModels_Escape_LandsOnItsRow_OnTheEmbeddedTab()
    {
        var (menu, pane, _) = EmbeddedPane();
        Push(Keys.Escape);                      // out of the catalog: the settings list, on Embedded models
        Push(Keys.Down, Keys.Enter);            // Embedded filter type, the row under it
        Push(Keys.Down, Keys.Enter);            // gguf
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None, midTurn: false, SettingsField.EmbeddedModels));

        Assert.Equal("gguf", _settings.Current.EmbeddedFilterType);
        Assert.Null(menu.TakePendingEmbeddedModel());
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheReasoningEstimateRow_IsAPicker_UnderPreserveThinking()
    {
        _console.Profile.Width = 240;
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Llm);
        Down(SettingsMenu.TabFields[(int)SettingsTab.Llm].ToList().IndexOf(SettingsField.LlmReasoningEstimate));
        Push(Keys.Enter);                       // the page opens on chars
        Push(Keys.Down, Keys.Enter);            // tokenize
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));   // read at each request: no reconnect

        Assert.Equal("tokenize", _settings.Current.LlmReasoningEstimate);
        Assert.Contains("  · 🖥️ LLM reasoning estimate: tokenize", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task WithNoEmbeddedModelOffered_TheCatalogSaysSo()
    {
        _console.Profile.Width = 240;
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Enter);            // the catalog, under the switch
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.NoEmbeddedModelNotice, _console.Output);
        Assert.Contains(SettingsMenu.EmbeddedModelsDoorLabel, _console.Output);
        pane.Dispose();
    }
}
