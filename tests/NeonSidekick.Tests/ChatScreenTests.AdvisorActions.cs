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

    /// <summary>
    /// Change the model over the session's listing (2026-11-22): the advisor's own server is asked, so its <c>llama</c> is listed beside
    /// the saved <c>other</c> (the cursor's); Up, Enter writes it to the advisor's file alone.
    /// </summary>
    [Fact]
    public async Task Settings_AnAdvisorsChangeTheModel_ListsItsServersModels_AndWritesTheAdvisorsFile()
    {
        ArchitectAdvisor(new AppSettingsData { LlmUrl = "http://127.0.0.1:1234/v1", LlmModel = "other", LlmReasoning = "low" });
        string loadedModel = _settings.Current.LlmModel;
        PushLine("/settings advisor list");
        _console.Input.PushKey(Keys.Enter);
        PushFirstAdvisorAction(SettingsMenu.ChangeAdvisorModelChoice);
        _console.Input.PushKey(Keys.Up);        // llama, above other
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);    // the list
        _console.Input.PushKey(Keys.Escape);    // the search
        PushLine("/exit");

        string output = await RunAsync();

        var saved = Profiles.ReadProfileFile(Profiles.ProfileFile(_settings.StorageDirectory, "architect"));
        Assert.Equal(("http://127.0.0.1:1234/v1", "llama", "low"), (saved.LlmUrl, saved.LlmModel, saved.LlmReasoning));
        Assert.DoesNotContain("(no models for architect", output);
        Assert.Equal(loadedModel, _settings.Current.LlmModel);
        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
    }

    [Fact]
    public async Task Settings_AnAdvisorsChangeTheReasoning_WritesTheAdvisorsFile_AndLeavesYoursAlone()
    {
        ArchitectAdvisor(new AppSettingsData { LlmUrl = "http://127.0.0.1:1234/v1", LlmModel = "llama", LlmReasoning = "low" });
        string loaded = _settings.Current.LlmReasoning;
        PushLine("/settings advisor list");
        _console.Input.PushKey(Keys.Enter);
        PushFirstAdvisorAction(SettingsMenu.ChangeAdvisorReasoningChoice);
        _console.Input.PushKey(Keys.Down);      // medium, under low
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        await RunAsync();

        var saved = Profiles.ReadProfileFile(Profiles.ProfileFile(_settings.StorageDirectory, "architect"));
        Assert.Equal(("llama", "medium"), (saved.LlmModel, saved.LlmReasoning));
        Assert.Equal(loaded, _settings.Current.LlmReasoning);
    }

    /// <summary>The rows name the model a borrowing advisor answers on, the session's <c>llama</c> (the screen's <c>AdvisorLinkedModel</c> line).</summary>
    [Fact]
    public async Task Settings_ABorrowingAdvisorsRow_NamesTheSessionsModel()
    {
        ArchitectAdvisor(new AppSettingsData());
        PushLine("/settings advisor list");
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("· " + AdvisorServer.Scan + " · llama", output);
        Assert.DoesNotContain("· " + AdvisorServer.Scan + " · " + AdvisorServer.DefaultModel, output);
    }

    // ── /advisor list (2026-11-23, the user's ask) ──────────────────────────

    /// <summary><c>/advisor list</c> opens the Advisor list alone: its rows drawn, its hint saying Esc closes, and one Esc back to the line.</summary>
    [Fact]
    public async Task AdvisorList_OpensTheList_AndOneEscClosesIt()
    {
        ArchitectAdvisor(new AppSettingsData { LlmUrl = "http://127.0.0.1:1234/v1", LlmModel = "llama", LlmReasoning = "low" });
        PushLine("/advisor list");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SettingsMenu.AddAdvisorRow, output);
        Assert.Contains("· http · llama ◔", output);   // the architect's row, its disc
        Assert.Contains(SettingsMenu.AdvisorListCloseKeys, output);
        Assert.DoesNotContain(AdvisorText.UsageError, output);
        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// The hint resets between visits (the plan's pass 37): <c>/advisor list</c> says close, then <c>/settings advisor list</c> + Enter says back,
    /// and its first Esc leaves the search open (a second closes it).
    /// </summary>
    [Fact]
    public async Task AdvisorList_ThenTheSettingsSearchRoute_TheHintSaysBackAgain()
    {
        ArchitectAdvisor();
        PushLine("/advisor list");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/settings advisor list");
        _console.Input.PushKey(Keys.Enter);     // the row found
        _console.Input.PushKey(Keys.Escape);    // the list: back to the search
        _console.Input.PushKey(Keys.Escape);    // the search
        PushLine("/exit");

        string output = await RunAsync();

        int close = output.IndexOf(SettingsMenu.AdvisorListCloseKeys, StringComparison.Ordinal);
        Assert.True(close >= 0);
        Assert.Contains(SettingsMenu.AdvisorListKeys, output[close..]);
        Assert.DoesNotContain(SettingsMenu.AdvisorListKeys, output[..close]);
    }

    /// <summary>An action from <c>/advisor list</c> runs end to end: Change the reasoning writes the advisor's file, its row's disc follows.</summary>
    [Fact]
    public async Task AdvisorList_ChangeTheReasoning_WritesTheAdvisorsFile()
    {
        ArchitectAdvisor(new AppSettingsData { LlmUrl = "http://127.0.0.1:1234/v1", LlmModel = "llama", LlmReasoning = "low" });
        PushLine("/advisor list");
        PushFirstAdvisorAction(SettingsMenu.ChangeAdvisorReasoningChoice);
        _console.Input.PushKey(Keys.Down);      // medium, under low
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);    // one Esc: the pane
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal("medium", Profiles.ReadProfileFile(Profiles.ProfileFile(_settings.StorageDirectory, "architect")).LlmReasoning);
        Assert.Contains("· http · llama ◑", output);
        Assert.Equal(Profiles.DefaultName, _settings.ProfileName);
    }

    /// <summary>Switch to profile from <c>/advisor list</c> switches as <c>/profile &lt;name&gt;</c> does (the screen applies the menu's changes).</summary>
    [Fact]
    public async Task AdvisorList_SwitchToProfile_SwitchesAsProfileDoes()
    {
        ArchitectAdvisor();
        PushLine("/advisor list");
        PushFirstAdvisorAction(SettingsMenu.SwitchToAdvisorChoice);
        _console.Input.PushKey(Keys.Char('y'));
        _console.Input.PushKey(Keys.Enter);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal("architect", _settings.ProfileName);
        Assert.Contains("  · " + SettingsMenu.SwitchedNotice("architect"), output);
    }

    /// <summary>With no advisors it still opens, on the add rows (how the first is made), and says nothing of none listed.</summary>
    [Fact]
    public async Task AdvisorList_WithNoAdvisors_OpensOnTheAddRows()
    {
        ListPaneScreen();
        PushLine("/advisor list");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SettingsMenu.AddAdvisorRow, output);
        Assert.Contains(SettingsMenu.NewAdvisorRow, output);
        Assert.DoesNotContain(AdvisorText.NoAdvisorsNotice, output);
    }

    /// <summary>Under a reply <c>/advisor list</c> is a pane, as <c>/settings</c> is; every other <c>/advisor</c> keeps its class.</summary>
    [Fact]
    public void MidTurn_AdvisorPolicy_ListIsAPane_TheRestUnchanged()
    {
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Advisor, "list"));
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Advisor, " LIST "));
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicyUnderLoad(SlashCommand.Advisor, "list"));
        Assert.Equal(ChatScreen.MidTurnPolicy(SlashCommand.Advisor, hasArgs: true), ChatScreen.MidTurnPolicy(SlashCommand.Advisor, "dba why?"));
        Assert.Equal(ChatScreen.MidTurnPolicy(SlashCommand.Advisor, hasArgs: true), ChatScreen.MidTurnPolicy(SlashCommand.Advisor, "list why?"));
        Assert.Equal(ChatScreen.MidTurnPolicy(SlashCommand.Advisor, hasArgs: true), ChatScreen.MidTurnPolicy(SlashCommand.Advisor, "templates"));
        Assert.Equal(ChatScreen.MidTurnPolicy(SlashCommand.Advisor, hasArgs: false), ChatScreen.MidTurnPolicy(SlashCommand.Advisor, ""));
        Assert.NotEqual(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Advisor, "dba why?"));
    }

    /// <summary><c>/advisor list</c> typed under a reply opens the list over it; one Esc closes it, and the reply ends whole.</summary>
    [Fact]
    public async Task MidTurn_AdvisorList_OpensOverTheReply()
    {
        ArchitectAdvisor();
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                PushLine("/advisor list");
            }
            else if (i == 2)
            {
                Scripted().Push(Keys.Escape);
            }
        });

        string output = await RunAsync();

        Assert.Contains(SettingsMenu.AddAdvisorRow, output);
        Assert.Contains(SettingsMenu.AdvisorListCloseKeys, output);
        Assert.DoesNotContain(ChatScreen.MidTurnDeferredNotice("/advisor"), output);
        Assert.Contains("One two three.", output);
        Assert.Single(_chat.Requests);
    }
}
