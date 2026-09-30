using System.Net;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class LlmSessionTests
{
    /// <summary>A blank URL under the <c>local</c> scan, which finds the stub: the default until 2026-09-29, when it became <c>disabled</c>.</summary>
    private static AppSettingsData Scanning => new() { LlmScanMode = "local" };

    private readonly StubHttpMessageHandler _http = new();
    private readonly List<FakeChatClient> _clients = new();
    private readonly List<LlmEndpoint> _endpoints = new();

    private LlmSession Session() => new(
        new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        (endpoint, _) =>
        {
            _endpoints.Add(endpoint);
            var client = new FakeChatClient();
            _clients.Add(client);
            return client;
        });

    private void ServerOn1234(params string[] models) =>
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson(models));

    [Fact]
    public async Task Connect_FindsTheServer_AndBuildsAnAssistant()
    {
        ServerOn1234("llama");
        using var session = Session();

        Assert.True(await session.ConnectAsync(Scanning, CancellationToken.None));

        Assert.NotNull(session.Assistant);
        Assert.Equal("llama", session.Endpoint!.ModelId);
        Assert.Equal("LLM: http://127.0.0.1:1234/v1 model=llama (probed http://127.0.0.1:1234/v1)", LlmSession.ConnectedLine(session.Endpoint));
        Assert.Equal(LlmTimeouts.Default, session.Timeouts);
        Assert.Equal(ReasoningEffort.None, session.Assistant.Reasoning);   // the compiled default is "none"
    }

    [Fact]
    public async Task Connect_HandsTheReasoningLevelToTheAssistant()
    {
        ServerOn1234("llama");
        using var session = Session();

        Assert.True(await session.ConnectAsync(new AppSettingsData { LlmReasoning = "xhigh", LlmScanMode = "local" }, CancellationToken.None));

        Assert.Equal(ReasoningEffort.ExtraHigh, session.Assistant!.Reasoning);
    }

    [Fact]
    public async Task Connect_NoServer_ReturnsFalse_WithNoEndpoint()
    {
        using var session = Session();
        Assert.False(await session.ConnectAsync(Scanning, CancellationToken.None));
        Assert.Null(session.Endpoint);
        Assert.Null(session.Assistant);
        Assert.Equal("LLM: no server found on 127.0.0.1 ports 1234, 8000, 30000, 8080, 11434, 8888; set NEONSIDEKICK_LLM_URL.", LlmSession.NoServerLine(ScanScope.Local));
        Assert.Equal("LLM: no server found on the local network (ports 1234, 8000, 30000, 8080, 11434, 8888); set NEONSIDEKICK_LLM_URL.", LlmSession.NoServerLine(ScanScope.Remote));
        Assert.Equal("LLM: no server found on 127.0.0.1 or the local network (ports 1234, 8000, 30000, 8080, 11434, 8888); set NEONSIDEKICK_LLM_URL.", LlmSession.NoServerLine(ScanScope.Both));
        Assert.Equal("LLM: no URL is set and LLM scan mode is disabled; set NEONSIDEKICK_LLM_URL, or the URL or the scan mode in /settings.", LlmSession.NoServerLine(ScanScope.Disabled));
    }

    [Fact]
    public async Task Reconnect_DisposesTheOldClient_AndKeepsTheHistory()
    {
        ServerOn1234("a", "b");
        using var session = Session();
        await session.ConnectAsync(Scanning, CancellationToken.None);
        session.History.AddUser("remember me");

        Assert.True(await session.ConnectAsync(new AppSettingsData { LlmModel = "b", LlmScanMode = "local" }, CancellationToken.None));

        Assert.True(_clients[0].Disposed);
        Assert.False(_clients[1].Disposed);
        Assert.Equal("b", _endpoints[1].ModelId);
        Assert.Single(session.History.Messages);
        Assert.Same(session.History, session.Assistant!.History);
    }

    [Fact]
    public async Task Connect_FactoryThrows_IsAnError_EndpointStaysKnown()
    {
        ServerOn1234("llama");
        using var session = new LlmSession(
            new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
            new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
            (_, _) => throw new InvalidOperationException("no client"));
        var seen = new List<string>();
        void Capture(NeonSidekick.Diagnostics.DiagnosticEvent e) => seen.Add($"[{e.Category}] {e.Message}");
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += Capture;
        bool echo = NeonSidekick.Diagnostics.DiagnosticLog.EchoToConsole;
        NeonSidekick.Diagnostics.DiagnosticLog.EchoToConsole = false;
        try
        {
            Assert.False(await session.ConnectAsync(Scanning, CancellationToken.None));
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= Capture;
            NeonSidekick.Diagnostics.DiagnosticLog.EchoToConsole = echo;
        }

        Assert.NotNull(session.Endpoint);
        Assert.Null(session.Assistant);
        Assert.Contains("[App] Could not create the chat client: InvalidOperationException: no client", seen);
    }

    [Fact]
    public async Task ListModels_UsesTheConnectedEndpoint_OrTheConfiguredUrl_OrNothing()
    {
        using var session = Session();
        Assert.Null(await session.ListModelsAsync(CancellationToken.None));

        ServerOn1234("x", "y");
        await session.ConnectAsync(Scanning, CancellationToken.None);
        var listed = await session.ListModelsAsync(CancellationToken.None);
        Assert.Equal(new[] { "x", "y" }, listed!.Value.ModelIds);

        // A configured URL that does not answer still gives something to ask.
        using var other = Session();
        await other.ConnectAsync(new AppSettingsData { LlmUrl = "http://127.0.0.1:9" }, CancellationToken.None);
        var dead = await other.ListModelsAsync(CancellationToken.None);
        Assert.False(dead!.Value.Exists);
    }

    [Fact]
    public async Task Discover_ListsTheServers_DropsTheClient_AndConnectLandsOne()
    {
        ServerOn1234("llama");
        _http.Map("http://127.0.0.1:11434/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("phi"));
        using var session = Session();
        await session.ConnectAsync(Scanning, CancellationToken.None);

        var effective = new AppSettingsData { LlmApiKey = "k", LlmRequestTimeoutSeconds = 5, LlmTurnTimeoutSeconds = 50, LlmScanMode = "local" };
        var servers = await session.DiscoverAsync(effective, CancellationToken.None);

        Assert.Equal(new[] { "LM Studio", "Ollama" }, servers.Select(s => s.Name));
        Assert.Null(session.Endpoint);
        Assert.Null(session.Assistant);
        Assert.True(_clients[0].Disposed);
        Assert.Equal(5, session.Timeouts.Request.TotalSeconds);

        Assert.True(session.Connect(effective, LlmEndpointProbe.Endpoint(servers[1], "k", null, configured: true)));
        Assert.Equal("LLM: http://127.0.0.1:11434/v1 model=phi (first listed)", LlmSession.ConnectedLine(session.Endpoint!));
        Assert.Equal(2, _endpoints.Count);
        Assert.Equal("k", _endpoints[1].ApiKey);
        Assert.NotNull(session.Assistant);

        // The endpoint in use is what /model lists from now on.
        Assert.Equal(new[] { "phi" }, (await session.ListModelsAsync(CancellationToken.None))!.Value.ModelIds);
    }

    [Fact]
    public async Task ProbeServers_KeepsTheSession_AndAsksTheExtraUrl()
    {
        ServerOn1234("llama");
        _http.Map("http://127.0.0.1:7777/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("odd"));
        using var session = Session();
        await session.ConnectAsync(new AppSettingsData { LlmUrl = "http://127.0.0.1:7777" }, CancellationToken.None);

        var servers = await session.ProbeServersAsync(Scanning, session.Endpoint!.BaseUrl, CancellationToken.None);

        Assert.Equal(new[] { "http://127.0.0.1:1234/v1", "http://127.0.0.1:7777/v1" }, servers.Select(s => s.BaseUrl.AbsoluteUri));
        Assert.Equal("127.0.0.1:7777", servers[1].Name);
        Assert.NotNull(session.Assistant);
        Assert.False(_clients.Single().Disposed);

        var one = await session.ProbeServerAsync(new Uri("http://127.0.0.1:9"), new AppSettingsData(), CancellationToken.None);
        Assert.False(one.Result.Exists);
        Assert.Equal("http://127.0.0.1:9/v1", one.BaseUrl.AbsoluteUri);
    }

    private const string ModelsWithWindow = "{\"object\":\"list\",\"data\":[{\"id\":\"llama\",\"object\":\"model\",\"max_model_len\":32768}]}";

    [Fact]
    public async Task Connect_AsksForTheContextWindow_AndDropsItOnDisconnect()
    {
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, ModelsWithWindow);
        using var session = Session();
        Assert.Null(session.ContextLength);

        Assert.True(await session.ConnectAsync(Scanning, CancellationToken.None));
        Assert.Equal(new ContextLength(32_768, ContextLengthProbe.MaxModelLenSource), session.ContextLength);
        // The list published it: no native tier was asked.
        Assert.All(_http.Requests, r => Assert.EndsWith("/v1/models", r.Uri.AbsolutePath));

        // The discovery half drops the client and with it the window; the connect half brings both back —
        // the synchronous one too, the window riding on the endpoint.
        var servers = await session.DiscoverAsync(Scanning, CancellationToken.None);
        Assert.Null(session.ContextLength);
        Assert.True(session.Connect(new AppSettingsData(), LlmEndpointProbe.Endpoint(servers[0], null, null, configured: false)));
        Assert.Equal(32_768, session.ContextLength!.Value.Tokens);
    }

    [Fact]
    public async Task Connect_ListSaysNothing_AsksTheNativeTiers_OrLeavesItUnknown()
    {
        ServerOn1234("llama");
        using var session = Session();
        Assert.True(await session.ConnectAsync(Scanning, CancellationToken.None));
        Assert.Null(session.ContextLength);
        Assert.Equal(
            new[] { "/api/v0/models", "/props", "/api/ps", "/api/show" },
            _http.Requests.Where(r => r.Uri.Port == 1234 && !r.Uri.AbsolutePath.EndsWith("/v1/models")).Select(r => r.Uri.AbsolutePath));

        // LM Studio's native list answers: the window, with its source.
        _http.Map("http://127.0.0.1:1234/api/v0/models", HttpStatusCode.OK, "{\"data\":[{\"id\":\"llama\",\"loaded_context_length\":4096}]}");
        Assert.True(await session.ConnectAsync(Scanning, CancellationToken.None));
        Assert.Equal(new ContextLength(4_096, ContextLengthProbe.LmStudioLoadedSource), session.ContextLength);
    }

    [Fact]
    public async Task Connect_AConfiguredWindow_WinsAndAsksNothing()
    {
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, ModelsWithWindow);
        using var session = Session();

        Assert.True(await session.ConnectAsync(new AppSettingsData { LlmContextLength = 8_192, LlmScanMode = "local" }, CancellationToken.None));

        Assert.Equal(ContextLength.Configured(8_192), session.ContextLength);
        // Discovery's own /v1/models once; the window probe would have asked it a second time.
        Assert.Equal(1, _http.Requests.Count(r => r.Uri.AbsoluteUri == "http://127.0.0.1:1234/v1/models"));
    }

    [Fact]
    public async Task Dispose_DisposesTheClient()
    {
        ServerOn1234("llama");
        var session = Session();
        await session.ConnectAsync(Scanning, CancellationToken.None);
        session.Dispose();
        Assert.True(_clients.Single().Disposed);
    }

    // ── The background reflection (Reflection (auto-learn), 2026-09-17) ───────────

    private static SkillLearnResult Result(SkillLearnOutcome outcome) => new(outcome, null, "", TokenUsage.Zero, 0);

    private static async Task<SkillLearnResult> WaitForCancel(Assistant _, CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token);
            return Result(SkillLearnOutcome.Nothing);
        }
        catch (OperationCanceledException)
        {
            return Result(SkillLearnOutcome.Cancelled);
        }
    }

    [Fact]
    public async Task StartLearning_RunsOneJobAtATime_OverTheAssistant()
    {
        ServerOn1234("llama");
        using var session = Session();
        await session.ConnectAsync(Scanning, CancellationToken.None);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assistant? seen = null;

        var job = session.StartLearning(async (assistant, token) =>
        {
            seen = assistant;
            await gate.Task.WaitAsync(token);
            return Result(SkillLearnOutcome.Nothing);
        }, CancellationToken.None);

        Assert.NotNull(job);
        Assert.True(session.IsLearning);
        Assert.Same(job, session.Learning);
        Assert.Null(session.StartLearning((_, _) => throw new InvalidOperationException("never"), CancellationToken.None));   // one at a time

        gate.SetResult();
        var result = await job!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SkillLearnOutcome.Nothing, result.Outcome);
        Assert.Same(session.Assistant, seen);
        Assert.False(session.IsLearning);
        Assert.NotNull(session.StartLearning((_, _) => Task.FromResult(Result(SkillLearnOutcome.Nothing)), CancellationToken.None));
    }

    [Fact]
    public async Task StartTitling_IsItsOwnSlot_BesideTheReflection_AndAReconnectCancelsIt()
    {
        ServerOn1234("llama");
        using var session = Session();
        await session.ConnectAsync(Scanning, CancellationToken.None);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelled = false;

        var job = session.StartTitling(async (assistant, token) =>
        {
            try
            {
                await gate.Task.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
        }, CancellationToken.None);

        Assert.NotNull(job);
        Assert.True(session.IsTitling);
        Assert.Same(job, session.Titling);
        Assert.Null(session.StartTitling((_, _) => throw new InvalidOperationException("never"), CancellationToken.None));   // one at a time
        // The reflection's slot is another: a title request never blocks a reflection.
        Assert.NotNull(session.StartLearning((_, _) => Task.FromResult(Result(SkillLearnOutcome.Nothing)), CancellationToken.None));

        await session.ConnectAsync(Scanning, CancellationToken.None);   // Disconnect first: the title job is cancelled
        await job!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled);
        Assert.False(session.IsTitling);
    }

    [Fact]
    public void StartTitling_WithNoAssistant_IsNull()
    {
        using var session = Session();
        Assert.Null(session.StartTitling((_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.False(session.IsTitling);
    }

    [Fact]
    public void StartLearning_WithNoAssistant_IsNull()
    {
        using var session = Session();
        Assert.Null(session.StartLearning((_, _) => Task.FromResult(Result(SkillLearnOutcome.Nothing)), CancellationToken.None));
        Assert.False(session.IsLearning);
    }

    [Fact]
    public async Task AReconnect_CancelsTheRunningJob_BeforeTheClientGoes()
    {
        ServerOn1234("llama");
        using var session = Session();
        await session.ConnectAsync(Scanning, CancellationToken.None);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seen = default;

        var job = session.StartLearning((_, token) =>
        {
            seen = token;
            started.SetResult();
            return WaitForCancel(null!, token);
        }, CancellationToken.None);

        await started.Task;
        await session.ConnectAsync(Scanning, CancellationToken.None);

        // The token went first (Disconnect cancels before it disposes), the old client after; the new one is untouched.
        Assert.True(seen.IsCancellationRequested);
        var result = await job!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SkillLearnOutcome.Cancelled, result.Outcome);
        Assert.True(_clients[0].Disposed);
        Assert.False(_clients[1].Disposed);
        Assert.False(session.IsLearning);
    }

    [Fact]
    public async Task CancelLearning_AndTheAppToken_EndTheJob()
    {
        ServerOn1234("llama");
        using var session = Session();
        await session.ConnectAsync(Scanning, CancellationToken.None);
        using var app = new CancellationTokenSource();

        var first = session.StartLearning(WaitForCancel, app.Token)!;
        session.CancelLearning();
        Assert.Equal(SkillLearnOutcome.Cancelled, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        session.CancelLearning();   // nothing running: harmless

        var second = session.StartLearning(WaitForCancel, app.Token)!;
        app.Cancel();
        Assert.Equal(SkillLearnOutcome.Cancelled, (await second.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
    }
    [Fact]
    public void ConnectLogLines_ArePinned()
    {
        var endpoint = new LlmEndpoint(new Uri("http://127.0.0.1:1234/v1/"), "qwen3", "", "probed");
        Assert.Equal("Connected: " + LlmSession.ConnectedLine(endpoint), LlmSession.ConnectedLogLine(endpoint));
        Assert.Equal("Disconnected from http://127.0.0.1:1234/v1/ model=qwen3", LlmSession.DisconnectedLogLine(endpoint));
    }
}
