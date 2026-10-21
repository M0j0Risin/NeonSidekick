using NeonSidekick.App;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The Connect a model page with every server off by default (2026-10-21, the user's call): it stands after the no-server line
/// when nothing is offered at all, at the start, after a profile switch and after a bare <c>/server</c>; its embedded row switches
/// <c>Embedded servers enabled</c> on (and back off when nothing was picked), its scan row asks where to look first.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The start with nothing saved and nothing switched on: a fresh profile's settings, the pane, the fake embedded service.</summary>
    private FakeEmbeddedLlm NothingOffered()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmScanMode = "off"; d.LlmUrl = ""; d.EmbeddedLlmServer = false; });
        UsePane();
        return UseEmbedded(new FakeEmbeddedLlm());
    }

    [Fact]
    public async Task Startup_TheEmbeddedRow_SwitchesItOn_AndInstalls()
    {
        var embedded = NothingOffered();
        var model = EmbeddedModelCatalog.Models[0];
        var input = new ScriptedInput();
        input.Push(Keys.Down, Keys.Down, Keys.Enter);     // the page's third row: Download an embedded model
        input.Push(Keys.Enter);                           // the catalog: the first model's page
        input.Push(Keys.Enter);                           // Install: the pane closes, the screen downloads
        PushLine(input, "/exit");

        string output = await RunAsync(input);

        // The page never after the download began (a count of its title would count each cursor move's redraw).
        Assert.DoesNotContain(ChatScreen.ConnectTitle, output[output.IndexOf(BackgroundJobText.DownloadStarted(model), StringComparison.Ordinal)..]);
        Assert.True(_settings.Current.EmbeddedLlmServer);
        Assert.Equal([model.Id], embedded.Installs);
        Assert.Equal([model.Id], embedded.Starts);
        Assert.Equal(model.Id, _settings.Current.LlmModel);
        Assert.True(EmbeddedEndpoint.IsEmbedded(_settings.Current.LlmUrl));
    }

    /// <summary>A download left running behind the line ends the page: its end connects, and the page would stand over it.</summary>
    [Fact]
    public async Task Startup_TheEmbeddedRow_WhileTheDownloadRuns_LeavesThePage()
    {
        var embedded = NothingOffered();
        var release = new TaskCompletionSource();
        bool idle = false;
        embedded.InstallGate = async ct =>
        {
            StepPastTheGrace(() => Volatile.Read(ref idle));
            await release.Task.WaitAsync(ct);
        };
        var input = new ScriptedInput();
        input.Push(Keys.Down, Keys.Down, Keys.Enter);     // Download an embedded model
        input.Push(Keys.Enter, Keys.Enter);               // the first model's page, Install
        input.OnWait = () =>
        {
            if (!Volatile.Read(ref idle))
            {
                Volatile.Write(ref idle, true);
                PushLine(input, "/exit");                 // the input line, not the page again
            }
        };

        try
        {
            string output = await RunAsync(input);

            string started = BackgroundJobText.DownloadStarted(EmbeddedModelCatalog.Models[0]);
            Assert.Contains(started, output);
            Assert.DoesNotContain(ChatScreen.ConnectTitle, output[output.IndexOf(started, StringComparison.Ordinal)..]);   // the line is the user's
            Assert.True(_settings.Current.EmbeddedLlmServer);   // the download needs it
            Assert.Empty(embedded.Starts);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Startup_TheEmbeddedRow_LeftWithNothing_SwitchesItBackOff()
    {
        var embedded = NothingOffered();
        var input = new ScriptedInput();
        input.Push(Keys.Down, Keys.Down, Keys.Enter);     // Download an embedded model: the catalog
        input.Push(Keys.Escape, Keys.Escape);             // out of the catalog and /settings
        input.Push(Keys.Escape);                          // the page again: not now
        PushLine(input, "/exit");

        string output = await RunAsync(input);

        Assert.True(output.LastIndexOf(ChatScreen.ConnectTitle, StringComparison.Ordinal) > output.LastIndexOf(SettingsMenu.Title, StringComparison.Ordinal), output);   // the page again after /settings
        Assert.False(_settings.Current.EmbeddedLlmServer);
        Assert.Empty(embedded.Installs);
    }

    [Fact]
    public async Task Startup_TheScanRow_OpensScanMode_ThenLooks()
    {
        NothingOffered();
        var input = new ScriptedInput();
        input.Push(Keys.Enter);                           // Scan for local servers: the scan mode's list, on off
        input.Push(Keys.Up, Keys.Up, Keys.Up, Keys.Enter);   // local
        input.Push(Keys.Escape);                          // out of /settings: the look, the stub on 1234 answering
        PushLine(input, "/exit");

        string output = await RunAsync(input);

        Assert.Equal("local", _settings.Current.LlmScanMode);
        Assert.NotNull(_session.Endpoint);
        Assert.Equal(1, Count(output, ChatScreen.ConnectTitle));
    }

    [Fact]
    public async Task Startup_TheScanRow_LeftOff_ShowsThePageAgain()
    {
        NothingOffered();
        var input = new ScriptedInput();
        input.Push(Keys.Enter);                           // Scan for local servers: the scan mode's list, on off
        input.Push(Keys.Enter, Keys.Escape);              // off kept, out of /settings: nothing to look through
        input.Push(Keys.Escape);                          // the page again: not now
        PushLine(input, "/exit");

        string output = await RunAsync(input);

        Assert.Equal(2, Count(output, ChatScreen.ConnectTitle));
        Assert.Null(_session.Endpoint);
        Assert.Equal(0, ModelProbes);                     // nothing was looked for
    }

    [Fact]
    public async Task Server_NothingOffered_TheLine_ThenTheConnectPage()
    {
        NothingOffered();
        var input = new ScriptedInput();
        input.Push(Keys.Escape);                          // the start's page: not now
        PushLine(input, "/server");
        input.Push(Keys.Escape);                          // /server's page: not now
        PushLine(input, "/exit");

        string output = await RunAsync(input);

        Assert.Equal(2, Count(output, ChatScreen.ConnectTitle));
        Assert.Equal(2, Count(output, "✗ " + LlmSession.NoServerLine(ScanScope.Disabled)));
    }

    [Fact]
    public async Task ProfileSwitch_ToAProfileWithNothingOffered_OpensTheConnectPage()
    {
        _settings.Update(d => d.TtsOutput = false);
        UsePane();
        UseEmbedded(new FakeEmbeddedLlm());
        Profiles.Create(_dir, "work", new AppSettingsData { TtsOutput = false });   // a fresh profile: no URL, no scan, nothing switched on
        Profiles.Create(_dir, "home", new AppSettingsData { TtsOutput = false, LlmUrl = "http://127.0.0.1:1234/v1" });
        var input = new ScriptedInput();
        PushLine(input, "/profile work");
        input.Push(Keys.Escape);                          // work's page: not now
        PushLine(input, "/profile home");                 // a saved URL: connected, no page
        PushLine(input, "/exit");

        string output = await RunAsync(input);

        Assert.Equal(1, Count(output, ChatScreen.ConnectTitle));
        Assert.Equal("home", _settings.ProfileName);
        Assert.NotNull(_session.Endpoint);
    }

    /// <summary>A profile switched during a row's visit ends the page: that switch offered its own, and the old page's revert never touches the new profile.</summary>
    [Fact]
    public async Task ProfileSwitch_InsideTheEmbeddedRowsVisit_EndsThePage()
    {
        NothingOffered();
        Profiles.Create(_dir, "work", new AppSettingsData { TtsOutput = false });
        var input = new ScriptedInput();
        input.Push(Keys.Down, Keys.Down, Keys.Enter);     // Download an embedded model: the catalog
        input.Push(Keys.Escape);                          // back to /settings, on the Embedded tab
        input.Push(Enumerable.Repeat(Keys.Left, (int)SettingsTab.Embedded).ToArray());   // to the Chat tab, on Profile
        input.Push(Keys.Enter, Keys.Down, Keys.Enter);    // the profile picker, on neon: work (/settings stays open on the new profile)
        input.Push(Keys.Escape);                          // out of /settings: the switch connects work and offers its page
        input.Push(Keys.Escape);                          // work's page: not now
        PushLine(input, "/exit");                         // the input line: the start's page never comes back (it would take this line)

        await RunAsync(input);

        Assert.Equal("work", _settings.ProfileName);
        Assert.False(_settings.Current.EmbeddedLlmServer);
        Assert.False(Profiles.ReadProfileFile(Profiles.ProfileFile(_dir, "work")).EmbeddedLlmServer);
    }
}
