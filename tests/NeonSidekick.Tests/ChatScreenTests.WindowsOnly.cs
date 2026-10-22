using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Services;
using NeonSidekick.Tasks;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The screen's side of the families only Windows has (2026-10-22, the user's ask): off Windows the toolbar draws no 🧩 or 📅, <c>/tools</c>'
/// Offered tab lists no Services or Tasks group, and <c>/tools services</c> is the usage error. The toolbar through
/// <see cref="ChatScreen.ToolbarItemHere"/>, so Windows draws the Mac's strip too; the rest reads the system.
/// </summary>
public partial class ChatScreenTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheToolbar_WithTheMacsAnswer_DrawsNoServicesOrTasks_EveryOtherSwitchStays(bool mac)
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolbarItems = [.. ToolbarItems.Names.Where(id => id != ToolbarItems.Model)]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);
        _toolbarHere = mac ? id => ToolbarItems.IsHere(id, windows: false) : _ => true;
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(!mac, output.Contains(ServiceText.Glyph, StringComparison.Ordinal));
        Assert.Equal(!mac, output.Contains(TaskText.Glyph, StringComparison.Ordinal));
        Assert.Contains(ToolbarItems.Glyph(ToolbarItems.Directory), output);
        Assert.Contains(ToolbarItems.Glyph(ToolbarItems.Network), output);
    }

    /// <summary>On Windows the Offered tab lists the services tools (their switch off by default), as before.</summary>
    [WindowsFact]
    public async Task TheOfferedTab_OnWindows_ListsTheServicesTools()
    {
        string output = await OfferedFiltered("service_");

        Assert.Contains(ServiceListTool.ToolName, output);
    }

    /// <summary>On a Mac the Offered tab has no Services group (2026-10-22): a filter for its tools finds none (Tasks goes the same way, one list).</summary>
    [MacFact]
    public async Task TheOfferedTab_OnAMac_HasNoServicesGroup()
    {
        string output = await OfferedFiltered("service_");

        Assert.DoesNotContain(ServiceListTool.ToolName, output);
    }

    [MacFact]
    public async Task ToolsServices_OnAMac_IsTheUsageError()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Width = 300;   // the usage line on one row
        PushLine("/tools services");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + ToolsText.SwitchUsageError, output);
        Assert.DoesNotContain("services", ToolsText.SwitchUsageError);
    }

    /// <summary><c>/tools</c> on the pane, its Offered tab narrowed to <paramref name="filter"/>, then closed.</summary>
    private async Task<string> OfferedFiltered(string filter)
    {
        _settings.Update(d => { d.TtsOutput = false; d.MenuMaxHeight = "full"; });
        _console.Profile.Height = 60;
        _console.Profile.Width = 200;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/tools");
        _console.Input.PushText(filter);
        _console.Input.PushKey(Keys.Escape);   // the filter cleared
        _console.Input.PushKey(Keys.Escape);   // the pane closed
        PushLine("/exit");
        return await RunAsync();
    }
}
