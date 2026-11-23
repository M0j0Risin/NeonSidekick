using NeonSidekick.Advisors;
using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// An advisor's Switch to profile and Change the server on the screen (2026-11-22, the user's asks from UI testing): the switch ends the
/// pane and the screen switches as after <c>/profile &lt;name&gt;</c>; the server's list is the screen's own look-around, under the
/// advisor profile's saved settings (the wiring a menu test's fake would hide).
/// </summary>
public partial class ChatScreenTests
{
    private void ArchitectAdvisor(AppSettingsData? data = null)
    {
        ListPaneScreen();
        Profiles.Create(_settings.StorageDirectory, "architect", data ?? new AppSettingsData());
        _settings.Update(d => d.AdvisorList = [new AdvisorEntry("architect", "architect")]);
    }

    /// <summary>The list's first advisor (under the two add rows), then its <paramref name="action"/> row.</summary>
    private void PushFirstAdvisorAction(int action)
    {
        _console.Input.PushKey(Keys.Down);
        _console.Input.PushKey(Keys.Down);
        _console.Input.PushKey(Keys.Enter);
        for (int i = 0; i < action; i++)
        {
            _console.Input.PushKey(Keys.Down);
        }

        _console.Input.PushKey(Keys.Enter);
    }

    [Fact]
    public async Task Settings_AnAdvisorsSwitchToProfile_SwitchesAsProfileDoes()
    {
        ArchitectAdvisor();
        PushLine("/settings advisor list");
        _console.Input.PushKey(Keys.Enter);     // the row found
        PushFirstAdvisorAction(SettingsMenu.SwitchToAdvisorChoice);
        _console.Input.PushKey(Keys.Char('y'));
        _console.Input.PushKey(Keys.Enter);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal("architect", _settings.ProfileName);
        Assert.Contains("  · " + SettingsMenu.SwitchedNotice("architect"), output);
    }

    /// <summary>The toolbar's 🦉 page (<c>/tools advisors</c>): its advisors button, then the switch, the same way.</summary>
    [Fact]
    public async Task ToolsAdvisors_AnAdvisorsSwitchToProfile_SwitchesAsProfileDoes()
    {
        ArchitectAdvisor();
        PushLine("/tools advisors");
        _console.Input.PushKey(Keys.Char('a'));
        PushFirstAdvisorAction(SettingsMenu.SwitchToAdvisorChoice);
        _console.Input.PushKey(Keys.Char('y'));
        _console.Input.PushKey(Keys.Enter);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal("architect", _settings.ProfileName);
        Assert.Contains("  · " + SettingsMenu.SwitchedNotice("architect"), output);
    }

    /// <summary>A visit to the 🦉 page that switches nothing ends quietly: no usage error after it (the branch's own test).</summary>
    [Fact]
    public async Task ToolsAdvisors_AVisitWithoutASwitch_SaysNothingMore()
    {
        ArchitectAdvisor();
        PushLine("/tools advisors");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.DoesNotContain(ToolsText.UsageErrorFor("advisors"), output);
        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
    }

    /// <summary>Change the server over the screen's probe: the fixture's server is listed and picked, written to the advisor's file alone.</summary>
    [Fact]
    public async Task Settings_AnAdvisorsChangeTheServer_ListsTheScreensLookAround_AndWritesTheAdvisorsFile()
    {
        ArchitectAdvisor(new AppSettingsData { LlmUrl = "http://127.0.0.1:1234/v1", LlmModel = "" });
        string loadedUrl = _settings.Current.LlmUrl;
        PushLine("/settings advisor list");
        _console.Input.PushKey(Keys.Enter);
        PushFirstAdvisorAction(SettingsMenu.ChangeAdvisorServerChoice);
        _console.Input.PushKey(Keys.Enter);     // LM Studio, the advisor's own (the cursor's)
        _console.Input.PushKey(Keys.Enter);     // llama
        _console.Input.PushKey(Keys.Enter);     // reasoning
        _console.Input.PushKey(Keys.Escape);    // the list
        _console.Input.PushKey(Keys.Escape);    // the search
        PushLine("/exit");

        string output = await RunAsync();

        var saved = Profiles.ReadProfileFile(Profiles.ProfileFile(_settings.StorageDirectory, "architect"));
        Assert.Equal(("http://127.0.0.1:1234/v1", "llama"), (saved.LlmUrl, saved.LlmModel));
        Assert.DoesNotContain(SettingsMenu.AdvisorNoServerNotice("architect"), output);
        Assert.Equal(loadedUrl, _settings.Current.LlmUrl);
        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
    }
}
