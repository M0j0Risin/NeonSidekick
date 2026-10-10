using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/settings</c>' Servers and Voice groups (2026-10-10, the user's pick, <c>/tools</c>' Database and Media shape): the six server tabs
/// (Common and Http the LLM tab until later that day) on a second strip under Servers, TTS and STT under Voice; ←/→ walk every tab in the old order, Tab and Shift+Tab step past a group,
/// a group's pickers are titled under it, and a row opened by name lands on its tab inside its group.
/// </summary>
public partial class SettingsMenuTests
{
    [Fact]
    public void TheStrip_AndItsGroups_ArePinned()
    {
        Assert.Equal(["General", "Servers", "Voice", "Sessions", "Botchat", "Advisors"], SettingsMenu.StripTitles);
        Assert.Equal(
            [
                [SettingsTab.General],
                [SettingsTab.Common, SettingsTab.Http, SettingsTab.Embedded, SettingsTab.Docker, SettingsTab.Anthropic, SettingsTab.OpenAI],
                [SettingsTab.Tts, SettingsTab.Stt],
                [SettingsTab.Sessions],
                [SettingsTab.BotChat],
                [SettingsTab.Advisors],
            ],
            SettingsMenu.StripTabs);
        Assert.Equal(Enum.GetValues<SettingsTab>(), SettingsMenu.StripTabs.SelectMany(t => t));   // every tab once, in the arrows' order

        Assert.Equal((1, 1), SettingsMenu.PlaceOf(SettingsTab.Http));   // right after Common (2026-10-10, the user's place)
        Assert.Equal((1, 5), SettingsMenu.PlaceOf(SettingsTab.OpenAI));
        Assert.Equal((2, 1), SettingsMenu.PlaceOf(SettingsTab.Stt));
        Assert.Equal((3, 0), SettingsMenu.PlaceOf(SettingsTab.Sessions));
        Assert.Equal(SettingsTab.Docker, SettingsMenu.TabAt(1, 3));
        Assert.Equal(SettingsTab.Sessions, SettingsMenu.TabAt(3, -1));   // a plain tab's sub (none) is its one tab
        Assert.True(SettingsMenu.IsGroup(1));
        Assert.False(SettingsMenu.IsGroup(0));

        Assert.Equal(SettingsMenu.Title + " › Servers", SettingsMenu.GroupCrumb(SettingsTab.Anthropic));
        Assert.Equal(SettingsMenu.Title + " › Servers", SettingsMenu.GroupCrumb(SettingsTab.Http));
        Assert.Equal(SettingsMenu.Title + " › Voice", SettingsMenu.GroupCrumb(SettingsTab.Tts));
        Assert.Equal(SettingsMenu.Title, SettingsMenu.GroupCrumb(SettingsTab.BotChat));
    }

    [Fact]
    public async Task OnThePane_TheServersTab_ShowsTheSecondStrip_TheArrowsWalkIt_TabStepsPast()
    {
        var (menu, pane) = PaneMenu();
        Push(Keys.Right);                     // Servers, on Common
        Push(Keys.Tab);                       // Voice, on TTS
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Contains("\n" + Head(SettingsTab.Common) + "\n \n" + MenuLayout.Heading("Connection", 120) + "\n▸ LLM URL ", Output);   // whole names: no row starts "Common " (2026-10-10)
        Assert.EndsWith(" │ ↳ Common · Http · Embedded · Docker · Anthropic · OpenAI ", Head(SettingsTab.Common));
        Assert.Contains("\n" + Head(SettingsTab.Tts) + "\n \n▸ Output ", Output);
        Assert.EndsWith(" │ ↳ TTS · STT ", Head(SettingsTab.Tts));
        Assert.DoesNotContain("\n▸ Servers enabled", Output);   // Tab went past Embedded
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ShiftTab_ComesBackToTheServersTabLastShown()
    {
        var (menu, pane) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Right, Keys.Right);   // Common, Http, Embedded, Docker
        Push(Keys.Tab, Keys.ShiftTab);              // Voice, then Servers again — on Docker
        Push(Keys.Enter, Keys.Escape, Keys.Escape); // Docker's first row: the group's crumb on its page

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(GroupBreadcrumb(SettingsTab.Docker, SettingsMenu.FieldName(SettingsMenu.TabFields[(int)SettingsTab.Docker][0])), Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_AServersRowsPicker_IsTitledUnderTheGroup_AndItSaves()
    {
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Http);
        Push(Keys.Enter, Keys.Down, Keys.Enter);   // LLM server scan mode, the Http tab's first row (2026-10-10; the LLM tab's before): remote
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("remote", _settings.Current.LlmScanMode);
        Assert.Contains("\n" + Titled(GroupBreadcrumb(SettingsTab.Http, "LLM server scan mode")) + "\n", Output);
        Assert.DoesNotContain(Breadcrumb("LLM server scan mode"), Output.Replace(GroupBreadcrumb(SettingsTab.Http, "LLM server scan mode"), ""));
        Assert.Equal(SettingsMenu.Title, menu.Root);   // put back after the edit
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ARowOpenedByName_LandsOnItsTabInsideItsGroup()
    {
        var (menu, pane) = PaneMenu();
        Push(Keys.Escape, Keys.Escape);           // the OpenAI API page, then the list

        await menu.ShowAsync(CancellationToken.None, midTurn: false, open: SettingsField.OpenAIApi);

        Assert.Contains("\n" + Titled(GroupBreadcrumb(SettingsTab.OpenAI, "OpenAI API")) + "\n", Output);
        Assert.Contains("\n" + Head(SettingsTab.OpenAI) + "\n", Output);   // the list under it, on OpenAI's rows (the status line says the page kept the value)
        Assert.Contains("\n▸ OpenAI API ", Output);
        pane.Dispose();
    }
}
