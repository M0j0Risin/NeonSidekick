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
    public void TheTab_IsAfterStt_WithItsSevenRows_AllReconnecting()
    {
        int tab = (int)SettingsTab.Embedded;
        Assert.Equal("Embedded", SettingsMenu.TabTitles[tab]);
        Assert.Equal("STT", SettingsMenu.TabTitles[tab - 1]);   // the Claude (API) tab sat between until it went to /tools (2026-09-29)
        Assert.Equal("Botchat", SettingsMenu.TabTitles[tab + 1]);
        // The switch first and MTP last (2026-09-29, the user's asks).
        Assert.Equal([SettingsField.EmbeddedLlmServer, SettingsField.EmbeddedModels, SettingsField.EmbeddedBackend, SettingsField.EmbeddedContextSize, SettingsField.EmbeddedGpuLayers, SettingsField.EmbeddedVision, SettingsField.EmbeddedDrafter], SettingsMenu.TabFields[tab]);
        Assert.All(SettingsMenu.TabFields[tab], f => Assert.True(SettingsMenu.IsLlmField(f), f.ToString()));
        Assert.All(SettingsMenu.TabFields[tab], f => Assert.True(SettingsMenu.RefusedMidTurn(f), f.ToString()));
        Assert.True(SettingsMenu.IsToggle(SettingsField.EmbeddedVision) && SettingsMenu.IsToggle(SettingsField.EmbeddedLlmServer) && SettingsMenu.IsToggle(SettingsField.EmbeddedDrafter));
        Assert.False(SettingsMenu.IsToggle(SettingsField.EmbeddedBackend));
        Assert.Equal(["Embedded LLM server enabled", "Embedded models", "Embedded backend", "Embedded context size", "Embedded GPU layers", "Embedded vision", "Embedded drafter"], SettingsMenu.TabFields[tab].Select(SettingsMenu.FieldName));
        var data = new AppSettingsData();
        Assert.True(data.EmbeddedLlmServer && data.EmbeddedDrafter);   // both on by default
        Assert.Equal(("on", "on"), (SettingsMenu.FieldValue(SettingsField.EmbeddedLlmServer, data, "C:\\p"), SettingsMenu.FieldValue(SettingsField.EmbeddedDrafter, data, "C:\\p")));
        var copy = AppSettings.Copy(new AppSettingsData { EmbeddedLlmServer = false, EmbeddedDrafter = false });
        Assert.False(copy.EmbeddedLlmServer || copy.EmbeddedDrafter);
    }

    [Fact]
    public void TheStaticValues_ArePinned()
    {
        var data = new AppSettingsData();
        Assert.Equal(SettingsMenu.EmbeddedModelsDoorLabel, SettingsMenu.FieldValue(SettingsField.EmbeddedModels, data, "C:\\p"));
        Assert.Equal("auto", SettingsMenu.FieldValue(SettingsField.EmbeddedBackend, data, "C:\\p"));
        Assert.Equal("32,768 tokens", SettingsMenu.FieldValue(SettingsField.EmbeddedContextSize, data, "C:\\p"));
        Assert.Equal(SettingsMenu.EmbeddedContextOwnLabel, SettingsMenu.FieldValue(SettingsField.EmbeddedContextSize, new AppSettingsData { EmbeddedContextSize = 0 }, "C:\\p"));
        Assert.Equal("auto", SettingsMenu.FieldValue(SettingsField.EmbeddedGpuLayers, data, "C:\\p"));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.EmbeddedVision, data, "C:\\p"));
        Assert.Equal(EmbeddedLlmText.UrlDisplay, SettingsMenu.FieldValue(SettingsField.LlmUrl, new AppSettingsData { LlmUrl = "http://embedded.localhost/v1" }, "C:\\p"));
        Assert.Equal(EmbeddedLlmText.UrlDisplay, SettingsMenu.FieldValue(SettingsField.LlmUrl, new AppSettingsData { LlmUrl = "embedded" }, "C:\\p"));

        var e2b = EmbeddedModelCatalog.Find("gemma-4-e2b")!;
        // Its MTP drafter counts and shows as the drafter column (2026-09-29); the quantisation column fits COMPACT-LOW.
        Assert.Equal("Gemma 4 E2B                     [#9A8BB8]UD-Q4_K_XL   installed · 4.3 GB[/]  ⚡", SettingsMenu.EmbeddedModelLabel(e2b, EmbeddedModelState.Installed));
        Assert.Equal("Gemma 4 12B                     [#9A8BB8]BF16         download  · 24.5 GB[/]  ⚡", SettingsMenu.EmbeddedModelLabel(EmbeddedModelCatalog.Find("gemma-4-12b-bf16")!, EmbeddedModelState.Absent));   // the · under the installed rows' (2026-09-29)
        Assert.Equal("Qwen3.6 35B A3B                 [#9A8BB8]UD-Q4_K_XL   download  · 23.3 GB[/]", SettingsMenu.EmbeddedModelLabel(EmbeddedModelCatalog.Find("qwen3.6-35b-a3b")!, EmbeddedModelState.Absent));   // no drafter: the column is blank
        Assert.Equal("Qwen3.8 27B NVFP4               [#9A8BB8]COMPACT-LOW  download  · 16.1 GB[/]  ⚡", SettingsMenu.EmbeddedModelLabel(EmbeddedModelCatalog.Find("qwen3.8-27b-nvfp4-compact-low")!, EmbeddedModelState.Absent));   // the head in its weights
        // The list pads every detail to the widest, so the drafter column is one column down the list.
        var labels = SettingsMenu.EmbeddedModelLabels([e2b, EmbeddedModelCatalog.Find("gemma-4-e4b-qat")!], m => m == e2b ? EmbeddedModelState.Installed : new EmbeddedModelState(EmbeddedModelStateKind.Partial, 42));
        Assert.EndsWith("installed · 4.3 GB[/]        ⚡", labels[0], StringComparison.Ordinal);
        Assert.EndsWith("paused    · 5.3 GB · 42%[/]  ⚡", labels[1], StringComparison.Ordinal);
        Assert.Equal(2, TextCells.Width(EmbeddedLlmText.DrafterGlyph));
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
        Assert.Equal("Embedded   " + Theme.ColorMarkup(Theme.Ink, "Gemma 4 E2B".PadRight(SettingsMenu.EmbeddedModelNameWidth)) + Theme.DimMarkup("UD-Q4_K_XL  download  · 4.2 GB") + "  ⚡", SettingsMenu.ServerLabel(row));
        // In a list the details are padded to the widest, the drafter column after; a scanned server's row has none.
        var lm = new Llm.LlmServer(new Uri("http://127.0.0.1:1234/v1"), "LM Studio", new Llm.ProbeResult(true, ["m"], "1 chat model, and more"));
        var labels = SettingsMenu.ServerLabels([lm, row]);
        Assert.EndsWith("  1 chat model, and more[/]", labels[0], StringComparison.Ordinal);
        Assert.EndsWith("download  · 4.2 GB[/]      ⚡", labels[1], StringComparison.Ordinal);
        Assert.Equal("LLM servers: Embedded Gemma 4 E2B UD-Q4_K_XL", SettingsMenu.ServerListLine([row]));
    }

    [Fact]
    public async Task OnThePane_TheRows_ReadTheDiskAndTheMachine()
    {
        var (menu, pane, _) = EmbeddedPane(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        GoTo(SettingsTab.Embedded);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("1 of 39 installed (4.3 GB)", _console.Output);
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
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // Embedded context size (the switch first since 2026-09-29)
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
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal(16_384, _settings.Current.EmbeddedContextSize);
        Assert.Equal("all", _settings.Current.EmbeddedGpuLayers);
        Assert.Contains("Embedded context size " + EmbeddedContextSize.Error + "; keeping 32768.", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheBackendRow_IsAPicker()
    {
        var (menu, pane, _) = EmbeddedPane();
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Down, Keys.Enter); // Embedded backend: the page opens on auto
        Push(Keys.Down, Keys.Down, Keys.Enter); // vulkan
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal("vulkan", _settings.Current.EmbeddedBackend);
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
