using NeonSidekick.Advisors;
using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// An advisor's Switch to profile reached through <c>/tools advisors</c> (2026-11-22, the toolbar's 🦉 page): the switch ends the page
/// with <see cref="SettingsChanges.Profile"/>, and under a running reply it is refused, the reply put back after the visit.
/// </summary>
public partial class ToolsMenuTests
{
    private void ListOneAdvisor()
    {
        Profiles.Create(_dir, "architect", new AppSettingsData());
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("architect", "architect")]);
    }

    /// <summary>The advisors button, the list's first advisor (under the two add rows), Switch to profile.</summary>
    private void AdvisorsButtonThenSwitch()
    {
        Push(Keys.Char('a'));
        Push(Keys.Down, Keys.Down, Keys.Enter);
        Down(SettingsMenu.SwitchToAdvisorChoice);
        Push(Keys.Enter);
    }

    [Fact]
    public async Task ShowSwitch_Advisors_SwitchToProfile_EndsThePage_WithProfile()
    {
        ListOneAdvisor();
        var (menu, pane, _) = PaneMenu();
        AdvisorsButtonThenSwitch();
        Push(Keys.Char('y'), Keys.Enter);

        var changes = await menu.ShowSwitchAsync(SettingsField.Advisors, CancellationToken.None);

        Assert.Equal(SettingsChanges.Profile, changes);
        Assert.Equal("architect", _settings.ProfileName);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>Under a running reply (the mid-turn <c>/tools advisors</c>), refused with no question; the reply the menu held is put back.</summary>
    [Fact]
    public async Task ShowSwitch_Advisors_UnderAReply_RefusesTheSwitch_AndPutsTheReplyBack()
    {
        ListOneAdvisor();
        var (menu, pane, settings) = PaneMenu();
        AdvisorsButtonThenSwitch();
        Push(Keys.Escape, Keys.Escape);         // the list, the page

        var changes = await menu.ShowSwitchAsync(SettingsField.Advisors, CancellationToken.None, ReplyRun.Held);

        Assert.Equal(SettingsChanges.None, changes);
        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
        Assert.Contains(SettingsMenu.NotWhileReplyRunsNotice, Output);
        Assert.DoesNotContain(SettingsMenu.SwitchToAdvisorQuestion("architect"), Output);
        Assert.False(settings.UnderReply(ReplyRun.Idle).Running);   // idle again after the visit
        pane.Dispose();
    }

    /// <summary><c>/tools</c>' own visit sets the reply for the settings rows it edits and puts it back as it closes.</summary>
    [Fact]
    public async Task Show_UnderAReply_PutsTheMenusReplyBack()
    {
        var (menu, pane, settings) = PaneMenu();
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None, ReplyRun.Held);

        Assert.False(settings.UnderReply(ReplyRun.Idle).Running);
        pane.Dispose();
    }
}
