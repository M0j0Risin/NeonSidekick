using System.Net;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.LocalLlm;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The local model on the screen (2026-09-29): its <c>/server</c> rows, the install before the switch, <c>/server local</c>, the startup picker.</summary>
public partial class ChatScreenTests
{
    /// <summary>The fixture's session swapped for one over <paramref name="local"/>, its server's <c>/props</c> answered on the stub.</summary>
    private FakeLocalLlm UseLocal(FakeLocalLlm local)
    {
        _session.Dispose();
        _session = new LlmSession(new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)), new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)), (endpoint, _) => { _endpoints.Add(endpoint); return _chat; }, _time, local: local);
        _http.Map("http://127.0.0.1:59999/props", HttpStatusCode.OK, "{\"default_generation_settings\":{\"n_ctx\":32768}}");
        return local;
    }

    private const string LocalConnectedE2b = "LLM: http://local-llm.invalid/v1 model=gemma-4-e2b (local llama.cpp b11258 cuda on 127.0.0.1:59999)";

    [Fact]
    public async Task Server_ListsTheLocalModels_AndPickingAnInstalledOne_SavesItAndStartsIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        var local = UseLocal(new FakeLocalLlm().Installed("gemma-4-e2b"));
        PushLine("/server");
        _console.Input.PushKey(Keys.Down);      // Local · Gemma 4 E4B QAT
        _console.Input.PushKey(Keys.Down);      // Local · Gemma 4 E2B
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);    // keep the reasoning
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SettingsMenu.ServerTitle, output);
        Assert.Contains("Local      Gemma 4 E4B QAT  download 5.2 GB", output);
        Assert.Contains("Local      Gemma 4 E2B  installed · 4.2 GB", output);
        Assert.Contains("  · 🖥️ LLM URL: " + LocalLlmText.UrlDisplay, output);
        Assert.Contains(SettingsMenu.ReasoningTitle, output);
        Assert.DoesNotContain(SettingsMenu.ModelTitle, output);   // a local row is one model: no model step
        Assert.Equal("http://local-llm.invalid/v1", _settings.Current.LlmUrl);
        Assert.Equal("gemma-4-e2b", _settings.Current.LlmModel);
        Assert.Equal(["gemma-4-e2b"], local.Starts);
        Assert.Empty(local.Installs);
        Assert.Contains(LocalConnectedE2b, output);
        Assert.Equal(FakeLocalLlm.LiveUrl, _endpoints[^1].LiveUrl);
    }

    [Fact]
    public async Task Server_PickingALocalModelNotInstalled_DownloadsItFirst_ThenStartsIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        var local = UseLocal(new FakeLocalLlm());
        PushLine("/server");
        _console.Input.PushKey(Keys.Down);      // Local · Gemma 4 E4B QAT
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);    // keep the reasoning
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["gemma-4-e4b-qat"], local.Installs);
        Assert.Equal(["gemma-4-e4b-qat"], local.Starts);
        Assert.Contains(NoticeGlyphs.Llm + LocalLlmText.Installed(LocalModelCatalog.Find("gemma-4-e4b-qat")!), output);
        Assert.Equal("gemma-4-e4b-qat", _settings.Current.LlmModel);
    }

    [Fact]
    public async Task Server_ALocalDownloadCancelled_SavesNothing_AndSaysItIsPaused()
    {
        _settings.Update(d => d.TtsOutput = false);
        var input = Scripted();
        var local = UseLocal(new FakeLocalLlm());
        local.InstallGate = async ct =>
        {
            input.Push(Keys.CtrlC);
            await Task.Delay(Timeout.Infinite, ct);
        };
        StepsWhenIdle(Line("/server"), Key(Keys.Down), Key(Keys.Enter), Line("/exit"));   // Down: Local · Gemma 4 E4B QAT

        string output = await RunAsync();

        Assert.Contains("· " + ChatScreen.ConnectCancelledNotice(NoticeGlyphs.Llm), output);
        Assert.Contains("· " + LocalLlmText.PausedNotice, output);
        Assert.Equal("http://127.0.0.1:1234/v1", _settings.Current.LlmUrl);   // nothing saved before the model is there
        Assert.Empty(local.Starts);
    }

    [Fact]
    public async Task Server_Local_ListsTheLocalModelsAlone()
    {
        _settings.Update(d => d.TtsOutput = false);
        var local = UseLocal(new FakeLocalLlm().Installed("gemma-4-e2b", "gemma-4-e4b-qat"));
        PushLine("/server local");
        _console.Input.PushKey(Keys.Enter);     // the first local row: Gemma 4 E4B QAT
        _console.Input.PushKey(Keys.Escape);    // keep the reasoning
        PushLine("/exit");

        string output = await RunAsync();

        Assert.DoesNotContain("LM Studio  http://127.0.0.1:1234/v1", output);
        Assert.Equal(["gemma-4-e4b-qat"], local.Starts);
        Assert.Equal(1, ModelProbes);   // the startup connect alone: /server local asked no server
    }

    [Fact]
    public async Task Startup_OnALocalUrl_StartsTheSavedModel()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = "local"; d.LlmModel = "gemma-4-e2b"; });
        var local = UseLocal(new FakeLocalLlm().Installed("gemma-4-e2b"));
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["gemma-4-e2b"], local.Starts);
        Assert.Equal(0, ModelProbes);   // no scan, no /v1/models
        Assert.DoesNotContain("LLM: ", output);   // quiet under the banner: the settings name the endpoint
        Assert.NotNull(_session.Assistant);
    }

    [Fact]
    public async Task Startup_OnALocalModelNotInstalled_OffersTheInstall_AndNoKeepsItOff()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = "local"; d.LlmModel = "gemma-4-e4b-qat"; });
        var local = UseLocal(new FakeLocalLlm { RuntimeBytes = 577_081_932 });
        _console.Input.PushKey(Keys.Enter);     // No (the cursor opens on No)
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(LocalLlmText.InstallQuestion(LocalModelCatalog.Find("gemma-4-e4b-qat")!, 577_081_932), output);
        Assert.Empty(local.Installs);
        Assert.Empty(local.Starts);
        Assert.Contains(LocalLlmText.NotInstalled(LocalModelCatalog.Find("gemma-4-e4b-qat")!), output);
        Assert.Null(_session.Assistant);
    }

    [Fact]
    public async Task Startup_Escape_OnTheServerPicker_NeverStartsALocalModel()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = ""; });
        var local = UseLocal(new FakeLocalLlm().Installed("gemma-4-e2b"));
        _console.Input.PushKey(Keys.Escape);    // the startup picker: the first server that answered
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SettingsMenu.StartupServerTitle, output);
        Assert.Empty(local.Starts);
        Assert.Contains("LLM: http://127.0.0.1:1234/v1 model=llama (probed http://127.0.0.1:1234/v1)", output);
    }

    [Fact]
    public async Task ASwitchAway_StopsTheLocalServer()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = "local"; d.LlmModel = "gemma-4-e2b"; });
        var local = UseLocal(new FakeLocalLlm().Installed("gemma-4-e2b"));
        PushLine("/server http://127.0.0.1:1234");
        _console.Input.PushKey(Keys.Enter);     // llama
        _console.Input.PushKey(Keys.Escape);    // keep the reasoning
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(1, local.Stops);
        Assert.Null(local.Running);
        Assert.Equal("http://127.0.0.1:1234/v1", _settings.Current.LlmUrl);
    }
}
