using NeonSidekick.App;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The fit marks in the pickers (2026-11-16, plan lucid-roaming-lantern #43): the catalog's rows marked with the legend as the caption, S
/// stopping at <c>size: fits</c> to keep the ones that fit; with no GPU read no mark, the caption saying why and S skipping the stop;
/// <c>/server</c>'s embedded rows marked with the legend, the typed filter's caption over it while text is typed; a row's mark pinned.
/// </summary>
public partial class SettingsMenuTests
{
    private const long TwelveGiB = 12L << 30;

    [Fact]
    public void AMarkedRow_LeadsItsCapabilityColumns_WithTheMark()
    {
        var e2b = EmbeddedModelCatalog.Find("gemma-4-e2b")!;
        var state = new EmbeddedModelState(EmbeddedModelStateKind.Absent);
        Assert.Equal(SettingsMenu.EmbeddedModelLabel(e2b, state, 0), SettingsMenu.EmbeddedModelLabel(e2b, state, 0, fit: null));
        Assert.EndsWith("  ✅" + Marks, SettingsMenu.EmbeddedModelLabel(e2b, state, 0, fit: FitMark.Fits), StringComparison.Ordinal);
        Assert.EndsWith("  ✗ " + Marks, SettingsMenu.EmbeddedModelLabel(e2b, state, 0, fit: FitMark.WontFit), StringComparison.Ordinal);

        var row = new LlmServer(EmbeddedEndpoint.BaseUrl, EmbeddedEndpoint.ServerName, new ProbeResult(false, ["gemma-4-e2b"], "installed"));
        Assert.EndsWith("installed[/]  🐢" + Marks, SettingsMenu.ServerLabel(row, 0, 0, FitMark.PartlyCpu), StringComparison.Ordinal);
        var lm = new LlmServer(new Uri("http://127.0.0.1:1234/v1"), "LM Studio", new ProbeResult(true, ["m"], "1 chat model"));
        Assert.Equal(SettingsMenu.ServerLabel(lm), SettingsMenu.ServerLabel(lm, 0, 0, FitMark.Fits));   // a network server has no columns
    }

    [Fact]
    public async Task OnThePane_TheCatalog_MarksEachModel_AndSizeFits_KeepsTheOnesThatFit()
    {
        var big = EmbeddedModelCatalog.Find("gemma-4-31b")!;
        var small = EmbeddedModelCatalog.Find("gemma-4-e2b")!;
        var (menu, pane, _) = EmbeddedPane(new FakeEmbeddedLlm { Catalog = [big, small], Gpu = TwelveGiB });
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Enter);            // the catalog: 31B (✗ under Embedded VRAM only, the default), then E2B (✅)
        Push(Keys.Char('s'), Keys.Home);        // size: fits: E2B alone
        Push(Keys.Enter);                       // its page
        Push(Keys.Enter);                       // Install

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(small.Id, menu.TakePendingEmbeddedModel()!.Id);
        Assert.Contains("an estimate for this GPU's 12 GB", Output);
        Assert.Contains(" size: fits · show: all · ", Output);
        Assert.Contains("  ✗   ⚡", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_WithNoGpuRead_TheCatalogSaysWhy_AndSizeSkipsFits()
    {
        var (menu, pane, _) = EmbeddedPane(new FakeEmbeddedLlm { Catalog = [EmbeddedModelCatalog.Find("gemma-4-e2b")!] });
        GoTo(SettingsTab.Embedded);
        Push(Keys.Down, Keys.Enter);            // the catalog
        Push(Keys.Char('s'));                   // straight to 8GB
        Push(Keys.Home, Keys.Enter, Keys.Enter);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(EmbeddedLlmText.FitNoneCaption(FitNone.NoRead), Output);
        Assert.Contains(" size: 8GB · show: all · ", Output);
        Assert.DoesNotContain("size: fits", Output);
        Assert.DoesNotContain("✅", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task Server_OnThePane_TheEmbeddedRowsAreMarked_TheLegendGivingWayToATypedFilter()
    {
        _console.Profile.Width = 240;
        var (menu, pane) = PaneMenu();
        menu.EmbeddedLlm = new FakeEmbeddedLlm { Gpu = TwelveGiB };
        LlmServer Embedded(string id) => new(EmbeddedEndpoint.BaseUrl, EmbeddedEndpoint.ServerName, new ProbeResult(true, [id], "installed"));
        var servers = new[] { Server(1234, "LM Studio", "lm"), Embedded("gemma-4-31b"), Embedded("gemma-4-e2b") };

        Push(Keys.Char('s'));                   // size: fits: the 31B goes, LM Studio stays
        Push(Keys.Down, Keys.Enter);            // the second row left: E2B
        Assert.Same(servers[2], await menu.PickServerAsync(servers, null, SettingsMenu.ServerTitle, CancellationToken.None));
        Assert.Contains("an estimate for this GPU's 12 GB", Output);
        Assert.Contains(" size: fits · ⇉ drafter · ", Output);

        _console.Input.PushText("e2b");         // typed: the filter's caption in the legend's place
        Push(Keys.Enter);
        Assert.Same(servers[2], await menu.PickServerAsync(servers, null, SettingsMenu.ServerTitle, CancellationToken.None));
        Assert.Contains(MenuFilter.Caption("e2b", 1, 3), Output);
        pane.Dispose();
    }

    [Fact]
    public async Task Server_OnThePane_WithNoMark_ShowsNoLegend()
    {
        _console.Profile.Width = 240;
        var (menu, pane) = PaneMenu();
        menu.EmbeddedLlm = new FakeEmbeddedLlm { Gpu = TwelveGiB, Choice = new BackendChoice(LlamaBackend.Cpu, "no GPU") };
        var servers = new[] { Server(1234, "LM Studio", "lm"), new LlmServer(EmbeddedEndpoint.BaseUrl, EmbeddedEndpoint.ServerName, new ProbeResult(true, ["gemma-4-e2b"], "installed")) };

        Push(Keys.Char('s'), Keys.Enter);       // the CPU backend: no fits stop, 8GB
        Assert.Same(servers[0], await menu.PickServerAsync(servers, null, SettingsMenu.ServerTitle, CancellationToken.None));
        Assert.DoesNotContain("an estimate for this GPU", Output);
        Assert.DoesNotContain("No fit marks", Output);
        Assert.Contains(" size: 8GB · ", Output);
        pane.Dispose();
    }
}
