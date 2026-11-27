using NeonSidekick.Advisors;
using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// An advisor's off switch on the Advisor list (2026-11-27, the user's ask): Space on its row, or Turn off / Turn on on its page; the off
/// ones under an Off rule, dim, in the same columns; the setting button's value.
/// </summary>
public partial class SettingsMenuTests
{
    private void TwoAdvisors()
    {
        Profiles.Create(_dir, "dba", new AppSettingsData());
        Profiles.Create(_dir, "ops", new AppSettingsData());
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("dba", "architect"), new AdvisorEntry("ops", "operations", "runbooks")]);
    }

    [Fact]
    public async Task OnThePane_SpaceOnAnAdvisor_MovesItUnderTheOffRule_AndSpaceAgainBringsItBack()
    {
        TwoAdvisors();
        var (menu, pane) = PaneMenu();
        OpenAdvisorList();
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Char(' '));   // ops (under the two add rows and dba): off, without opening its page
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(_settings.Current.AdvisorList!.Single(e => e.Profile == "ops").Off);
        Assert.False(_settings.Current.AdvisorList!.Single(e => e.Profile == "dba").Off);
        Assert.Contains(SettingsMenu.AdvisorOffNotice("ops"), Output);
        Assert.Contains("─ " + SettingsMenu.AdvisorOffHeading + " ─", Output);
        Assert.Contains(SettingsMenu.AdvisorOffBand, Output);   // the cursor followed ops to its place: its band
        Assert.DoesNotContain(SettingsMenu.AdvisorEntryRows[0], Output);   // no page opened
        pane.Dispose();

        var (again, againPane) = PaneMenu();
        OpenAdvisorList();
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Char(' '));   // ops, still last, under the Off rule
        Push(Keys.Escape, Keys.Escape);
        await again.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.AdvisorList!.Single(e => e.Profile == "ops").Off);
        Assert.Contains(SettingsMenu.AdvisorOnNotice("ops"), Output);
        againPane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ThePagesTurnOffRow_FlipsIt_AndAnOffPageReadsTurnOn()
    {
        TwoAdvisors();
        var (menu, pane) = PaneMenu();
        OpenAdvisorList();
        Push(Keys.Down, Keys.Down, Keys.Enter);                  // dba: its page
        Push([.. Enumerable.Repeat(Keys.Down, 7), Keys.Enter]);  // Turn off, under the switch
        Push(Keys.Enter);                                         // dba again, now last: its page
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(_settings.Current.AdvisorList!.Single(e => e.Profile == "dba").Off);
        Assert.Contains(SettingsMenu.AdvisorOffNotice("dba"), Output);
        Assert.Contains("dba" + SettingsMenu.AdvisorOffCrumbMark, Output);
        Assert.Contains(SettingsMenu.TurnOnAdvisorRow, Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_DeleteOnAnOffAdvisor_StillAsksToRemove()
    {
        TwoAdvisors();
        _settings.Update(d => d.AdvisorList = AdvisorRoster.WithOff(d.AdvisorList, "ops", true));
        var (menu, pane) = PaneMenu();
        OpenAdvisorList();
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Delete);      // ops, under the Off rule
        Push(Keys.Char('y'), Keys.Enter);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["dba"], _settings.Current.AdvisorList!.Select(e => e.Profile));
        Assert.Contains(SettingsMenu.RemoveAdvisorQuestion("ops"), Output);
        pane.Dispose();
    }

    [Fact]
    public void AnOffRow_KeepsItsAvatar_DimsItsName_AndPrintsNoMarkup()
    {
        var off = new AdvisorEntry("ops", "operations", Off: true);
        string row = SettingsMenu.AdvisorRow(off, exists: true, width: 6, avatar: "🦉");
        Assert.StartsWith(Markup.Escape("🦉 "), row, StringComparison.Ordinal);
        Assert.Contains(Theme.DimMarkup(AdvisorText.PadCells("ops", 6)), row, StringComparison.Ordinal);

        var console = new TestConsole().Width(80);
        console.Markup(row);
        Assert.DoesNotContain("[", console.Output, StringComparison.Ordinal);
        Assert.Contains("🦉 ops", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdvisorListButton_CountsTheOnesOn()
    {
        Assert.Equal("none", SettingsMenu.SettingButtonValue(SettingsField.AdvisorList, new AppSettingsData(), _dir));
        var two = new AppSettingsData { AdvisorList = [new AdvisorEntry("dba"), new AdvisorEntry("ops", Off: true), new AdvisorEntry("ux")] };
        Assert.Equal("2 of 3", SettingsMenu.SettingButtonValue(SettingsField.AdvisorList, two, _dir));
        var none = new AppSettingsData { AdvisorList = [new AdvisorEntry("dba", Off: true)] };
        Assert.Equal("0 of 1", SettingsMenu.SettingButtonValue(SettingsField.AdvisorList, none, _dir));
        Assert.Contains("Space = on/off", SettingsMenu.AdvisorListKeys, StringComparison.Ordinal);
        Assert.Contains("Space = on/off", SettingsMenu.AdvisorListCloseKeys, StringComparison.Ordinal);
    }
}
