using NeonSidekick.App;
using NeonSidekick.LocalLlm;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The Local LLM tab of <c>/settings</c> (2026-09-29): its live rows, the catalog door, the typed rows and the backend picker.</summary>
public partial class SettingsMenuTests
{
    private (SettingsMenu Menu, ScreenPane Pane, FakeLocalLlm Local) LocalPane(FakeLocalLlm? local = null)
    {
        _console.Profile.Width = 240;
        var (menu, pane) = PaneMenu();
        local ??= new FakeLocalLlm();
        menu.LocalLlm = local;
        return (menu, pane, local);
    }

    [Fact]
    public void TheTab_IsBesideTheClaudeApis_WithItsFiveRows_AllReconnecting()
    {
        int tab = (int)SettingsTab.LocalModel;
        Assert.Equal("Local LLM", SettingsMenu.TabTitles[tab]);
        Assert.Equal(SettingsMenu.ClaudeApiTabTitle, SettingsMenu.TabTitles[tab - 1]);
        Assert.Equal("Botchat", SettingsMenu.TabTitles[tab + 1]);
        Assert.Equal([SettingsField.LocalModels, SettingsField.LocalBackend, SettingsField.LocalContextSize, SettingsField.LocalGpuLayers, SettingsField.LocalVision], SettingsMenu.TabFields[tab]);
        Assert.All(SettingsMenu.TabFields[tab], f => Assert.True(SettingsMenu.IsLlmField(f), f.ToString()));
        Assert.All(SettingsMenu.TabFields[tab], f => Assert.True(SettingsMenu.RefusedMidTurn(f), f.ToString()));
        Assert.True(SettingsMenu.IsToggle(SettingsField.LocalVision));
        Assert.False(SettingsMenu.IsToggle(SettingsField.LocalBackend));
        Assert.Equal(["Local models", "Local backend", "Local context size", "Local GPU layers", "Local vision"], SettingsMenu.TabFields[tab].Select(SettingsMenu.FieldName));
    }

    [Fact]
    public void TheStaticValues_ArePinned()
    {
        var data = new AppSettingsData();
        Assert.Equal(SettingsMenu.LocalModelsDoorLabel, SettingsMenu.FieldValue(SettingsField.LocalModels, data, "C:\\p"));
        Assert.Equal("auto", SettingsMenu.FieldValue(SettingsField.LocalBackend, data, "C:\\p"));
        Assert.Equal("32,768 tokens", SettingsMenu.FieldValue(SettingsField.LocalContextSize, data, "C:\\p"));
        Assert.Equal(SettingsMenu.LocalContextOwnLabel, SettingsMenu.FieldValue(SettingsField.LocalContextSize, new AppSettingsData { LocalContextSize = 0 }, "C:\\p"));
        Assert.Equal("auto", SettingsMenu.FieldValue(SettingsField.LocalGpuLayers, data, "C:\\p"));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.LocalVision, data, "C:\\p"));
        Assert.Equal(LocalLlmText.UrlDisplay, SettingsMenu.FieldValue(SettingsField.LlmUrl, new AppSettingsData { LlmUrl = "http://local-llm.invalid/v1" }, "C:\\p"));
        Assert.Equal(LocalLlmText.UrlDisplay, SettingsMenu.FieldValue(SettingsField.LlmUrl, new AppSettingsData { LlmUrl = "local" }, "C:\\p"));

        var e2b = LocalModelCatalog.Find("gemma-4-e2b")!;
        Assert.Equal("Gemma 4 E2B             [#9A8BB8]UD-Q4_K_XL  installed · 4.2 GB[/]", SettingsMenu.LocalModelLabel(e2b, LocalModelState.Installed));
        Assert.Equal("Remove (4.2 GB)", SettingsMenu.RemoveRow(e2b));
        Assert.Equal("Install (download 4.2 GB + llama.cpp runtime 577 MB)", SettingsMenu.InstallRow(e2b, 577_081_932));
        Assert.Equal("vulkan  [#9A8BB8]any GPU: NVIDIA, AMD, Intel[/]", SettingsMenu.LocalBackendLabel("vulkan"));
    }

    [Fact]
    public void ALocalServerRow_ShowsTheModel_WhereAUrlWouldBe()
    {
        var row = new Llm.LlmServer(LocalEndpoint.BaseUrl, LocalEndpoint.ServerName, new Llm.ProbeResult(false, ["gemma-4-e2b"], "download 4.2 GB"));

        Assert.Equal("Local      " + Theme.ColorMarkup(Theme.Ink, "Gemma 4 E2B") + Theme.DimMarkup("  download 4.2 GB"), SettingsMenu.ServerLabel(row));
        Assert.Equal("LLM servers: Local Gemma 4 E2B", SettingsMenu.ServerListLine([row]));
    }

    [Fact]
    public async Task OnThePane_TheRows_ReadTheDiskAndTheMachine()
    {
        var (menu, pane, _) = LocalPane(new FakeLocalLlm().Installed("gemma-4-e2b"));
        GoTo(SettingsTab.LocalModel);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("1 of 4 installed (4.2 GB)", _console.Output);
        Assert.Contains("auto (cuda: fake driver)", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_UseNow_OnAnInstalledModel_ClosesThePane_AndHandsItOver()
    {
        var (menu, pane, local) = LocalPane(new FakeLocalLlm().Installed("gemma-4-e2b"));
        GoTo(SettingsTab.LocalModel);
        Push(Keys.Enter);                       // Local models: the catalog
        Push(Keys.Down, Keys.Enter);            // Gemma 4 E2B: its page
        Push(Keys.Enter);                       // Use now

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal("gemma-4-e2b", menu.TakePendingLocalModel()!.Id);
        Assert.Null(menu.TakePendingLocalModel());   // taken once
        Assert.Contains(SettingsMenu.UseNowRow, _console.Output);
        Assert.Contains(SettingsMenu.RemoveRow(LocalModelCatalog.Find("gemma-4-e2b")!), _console.Output);
        Assert.Equal("", _settings.Current.LlmUrl);   // the screen saves it, after an install if need be
        Assert.Empty(local.Removes);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_Install_OnAMissingModel_ClosesThePane_AndHandsItOver()
    {
        var (menu, pane, local) = LocalPane(new FakeLocalLlm { RuntimeBytes = 577_081_932 });
        GoTo(SettingsTab.LocalModel);
        Push(Keys.Enter);                       // the catalog, on Gemma 4 E4B QAT
        Push(Keys.Enter);                       // its page
        Push(Keys.Enter);                       // Install

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("gemma-4-e4b-qat", menu.TakePendingLocalModel()!.Id);
        Assert.Contains(SettingsMenu.InstallRow(LocalModelCatalog.Find("gemma-4-e4b-qat")!, 577_081_932), _console.Output);
        Assert.Empty(local.Installs);   // the screen installs it, under the spinner
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_Remove_AsksFirst_ThenDeletes_AndStaysOnTheCatalog()
    {
        var (menu, pane, local) = LocalPane(new FakeLocalLlm().Installed("gemma-4-e2b"));
        GoTo(SettingsTab.LocalModel);
        Push(Keys.Enter);                       // the catalog
        Push(Keys.Down, Keys.Enter);            // Gemma 4 E2B
        Push(Keys.Down, Keys.Enter);            // Remove (4.2 GB)
        Push(Keys.Down, Keys.Enter);            // Yes (the cursor opens on No)
        Push(Keys.Escape, Keys.Escape);         // out of the catalog, then the settings

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["gemma-4-e2b"], local.Removes);
        Assert.Contains(LocalLlmText.RemoveQuestion(LocalModelCatalog.Find("gemma-4-e2b")!), _console.Output);
        Assert.Contains(LocalLlmText.Removed(LocalModelCatalog.Find("gemma-4-e2b")!), _console.Output);
        Assert.Null(menu.TakePendingLocalModel());
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheTypedRows_CheckTheirRanges()
    {
        var (menu, pane, _) = LocalPane();
        GoTo(SettingsTab.LocalModel);
        Push(Keys.Down, Keys.Down, Keys.Enter); // Local context size
        Backspace(10);
        _console.Input.PushText("100");
        Push(Keys.Enter);                       // refused
        Push(Keys.Enter);
        Backspace(10);
        _console.Input.PushText("16384");
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Enter);            // Local GPU layers
        Backspace(10);
        _console.Input.PushText("ALL");
        Push(Keys.Enter);
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal(16_384, _settings.Current.LocalContextSize);
        Assert.Equal("all", _settings.Current.LocalGpuLayers);
        Assert.Contains("Local context size " + LocalContextSize.Error + "; keeping 32768.", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheBackendRow_IsAPicker()
    {
        var (menu, pane, _) = LocalPane();
        GoTo(SettingsTab.LocalModel);
        Push(Keys.Down, Keys.Enter);            // Local backend: the page opens on auto
        Push(Keys.Down, Keys.Down, Keys.Enter); // vulkan
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal("vulkan", _settings.Current.LocalBackend);
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
    public async Task WithNoLocalModelOffered_TheCatalogSaysSo()
    {
        _console.Profile.Width = 240;
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.LocalModel);
        Push(Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.NoLocalModelNotice, _console.Output);
        Assert.Contains(SettingsMenu.LocalModelsDoorLabel, _console.Output);
        pane.Dispose();
    }
}
