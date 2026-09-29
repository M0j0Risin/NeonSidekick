using System.Net;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.LocalLlm;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The session's local-model branch (2026-09-29) over <see cref="FakeLocalLlm"/>: the sentinel shown, the live URL used.</summary>
public class LlmSessionLocalTests
{
    private const string LlamaProps = "{\"default_generation_settings\":{\"n_ctx\":32768,\"n_predict\":-1},\"total_slots\":1,\"model_path\":\"/m.gguf\"}";

    private readonly StubHttpMessageHandler _http = new();
    private readonly List<LlmEndpoint> _endpoints = new();
    private readonly FakeLocalLlm _local = new();

    private LlmSession Session(ILocalLlm? local = null) => new(
        new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        (endpoint, _) =>
        {
            _endpoints.Add(endpoint);
            return new FakeChatClient();
        },
        local: local ?? _local);

    private static AppSettingsData Local(string model = "") => new() { LlmUrl = "local", LlmModel = model };

    private void ServeProps() => _http.Map("http://127.0.0.1:59999/props", HttpStatusCode.OK, LlamaProps);

    [Fact]
    public async Task ALocalUrl_StartsTheModel_ShowsTheSentinel_AndPostsToTheLiveUrl()
    {
        _local.Installed("gemma-4-e2b");
        ServeProps();
        using var session = Session();
        var labels = new List<string>();

        Assert.True(await session.ConnectAsync(Local("gemma-4-e2b"), labels.Add, CancellationToken.None));

        var endpoint = session.Endpoint!;
        Assert.Equal(LocalEndpoint.BaseUrl, endpoint.BaseUrl);
        Assert.Equal(FakeLocalLlm.LiveUrl, endpoint.LiveUrl);
        Assert.Equal("gemma-4-e2b", endpoint.ModelId);
        Assert.Equal(FakeLocalLlm.Key, endpoint.ApiKey);
        Assert.Equal("local llama.cpp b11258 cuda on 127.0.0.1:59999", endpoint.Source);
        Assert.Equal("LLM: http://local-llm.invalid/v1 model=gemma-4-e2b (local llama.cpp b11258 cuda on 127.0.0.1:59999)", LlmSession.ConnectedLine(endpoint));
        Assert.Same(endpoint, _endpoints.Single());
        Assert.NotNull(session.Assistant);
        Assert.Equal(["gemma-4-e2b"], _local.Starts);
        Assert.Contains("starting Gemma 4 E2B on llama.cpp…", labels);
        Assert.Equal(new ContextLength(32_768, "n_ctx on /props"), session.ContextLength);
        Assert.All(_http.Requests, r => Assert.Equal("127.0.0.1:59999", r.Uri.Authority));             // the live port, never the sentinel
        Assert.All(_http.Requests, r => Assert.Equal("Bearer " + FakeLocalLlm.Key, r.Authorization));   // the server's own key, not the LLM API key
        Assert.Contains(_http.Requests, r => r.Uri.AbsolutePath == "/props");
        Assert.True(session.LocalServer!.Vision);
    }

    [Fact]
    public async Task ALocalUrl_WithNoModel_RunsTheFirstInstalled()
    {
        _local.Installed("gemma-4-e4b-uncensored", "gemma-4-e2b");
        using var session = Session();

        Assert.True(await session.ConnectAsync(Local(), CancellationToken.None));

        Assert.Equal(["gemma-4-e2b"], _local.Starts);   // catalog order, not install order
    }

    [Fact]
    public async Task ALocalModelNotInstalled_ConnectsNothing_AndSaysSo()
    {
        using var session = Session();
        using var log = new LogCapture();

        Assert.False(await session.ConnectAsync(Local("gemma-4-e4b-qat"), CancellationToken.None));

        Assert.Null(session.Endpoint);
        Assert.Null(session.Assistant);
        Assert.Empty(_local.Starts);
        Assert.Contains(LocalLlmText.NotInstalled(LocalModelCatalog.Find("gemma-4-e4b-qat")!), log.Errors);
    }

    [Fact]
    public async Task AnUnknownOrMissingLocalModel_SaysWhich()
    {
        using var session = Session();
        using var log = new LogCapture();

        Assert.False(await session.ConnectAsync(Local("gemma-9"), CancellationToken.None));
        Assert.False(await session.ConnectAsync(Local(), CancellationToken.None));

        Assert.Equal([LocalLlmText.UnknownModel("gemma-9"), LocalLlmText.NoneInstalled], log.Errors);
    }

    [Fact]
    public async Task AStartThatFails_LeavesTheSentinelEndpoint_WithNoClient()
    {
        _local.Installed("gemma-4-e2b");
        _local.StartFailure = "no VRAM";
        using var session = Session();
        using var log = new LogCapture();

        Assert.False(await session.ConnectAsync(Local("gemma-4-e2b"), CancellationToken.None));

        Assert.Equal(LocalEndpoint.BaseUrl, session.Endpoint!.BaseUrl);
        Assert.Null(session.Endpoint.LiveUrl);
        Assert.Equal(LlmSession.LocalNotRunningSource, session.Endpoint.Source);
        Assert.Null(session.Assistant);
        Assert.Empty(_endpoints);
        Assert.Contains(LocalLlmText.StartFailed("no VRAM"), log.Errors);
    }

    [Fact]
    public async Task ALocalUrl_WithNoLocalModelOffered_IsAnError()
    {
        using var session = new LlmSession(new LlmEndpointProbe(new HttpClient(_http)), new ContextLengthProbe(new HttpClient(_http)), (_, _) => new FakeChatClient());
        using var log = new LogCapture();

        Assert.False(await session.ConnectAsync(Local("gemma-4-e2b"), CancellationToken.None));

        Assert.Contains(LlmSession.LocalUnavailable, log.Errors);
        Assert.Empty(session.LocalRows());
        Assert.Null(await session.ListModelsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnotherServer_StopsTheLocalOne()
    {
        _local.Installed("gemma-4-e2b");
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("qwen"));
        using var session = Session();
        await session.ConnectAsync(Local("gemma-4-e2b"), CancellationToken.None);
        Assert.NotNull(_local.Running);

        Assert.True(await session.ConnectAsync(new AppSettingsData { LlmUrl = "http://127.0.0.1:1234/v1" }, CancellationToken.None));

        Assert.Null(_local.Running);
        Assert.Equal(1, _local.Stops);
        Assert.Null(session.LocalServer);

        // A picked endpoint (the /server path) stops it too.
        await session.ConnectAsync(Local("gemma-4-e2b"), CancellationToken.None);
        session.Connect(new AppSettingsData(), new LlmEndpoint(new Uri("http://127.0.0.1:1234/v1"), "qwen", "k", "picked"));
        Assert.Null(_local.Running);
    }

    [Fact]
    public async Task EachReconnect_AsksTheServiceAgain_WhichKeepsARunningServer()
    {
        _local.Installed("gemma-4-e2b");
        using var session = Session();

        await session.ConnectAsync(Local("gemma-4-e2b"), CancellationToken.None);
        var high = Local("gemma-4-e2b");
        high.LlmReasoning = "high";
        await session.ConnectAsync(high, CancellationToken.None);

        Assert.Equal(["gemma-4-e2b", "gemma-4-e2b"], _local.Starts);
        Assert.Equal(0, _local.Stops);   // a local reconnect never stops the server; the service decides reuse
    }

    [Fact]
    public async Task ListModels_OnALocalServer_IsTheInstalledCatalog()
    {
        _local.Installed("gemma-4-e2b", "gemma-4-e4b-qat");
        using var session = Session();
        await session.ConnectAsync(Local("gemma-4-e2b"), CancellationToken.None);
        int requests = _http.Requests.Count;

        var listed = await session.ListModelsAsync(CancellationToken.None);

        Assert.True(listed!.Value.Exists);
        Assert.Equal(["gemma-4-e4b-qat", "gemma-4-e2b"], listed.Value.ModelIds);
        Assert.Equal("2 local models", listed.Value.Detail);
        Assert.Equal(requests, _http.Requests.Count);   // asked of no server
    }

    [Fact]
    public async Task TheServerRows_ListEveryCatalogModel_AfterTheScan_BeforeTheClaudeApi()
    {
        _local.Installed("gemma-4-e2b").Partial("gemma-4-e4b-qat", 42);
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("qwen"));
        using var session = Session();

        var servers = await session.ProbeServersAsync(new AppSettingsData(), LocalEndpoint.BaseUrl, CancellationToken.None);

        Assert.Equal(["LM Studio", "Local", "Local", "Local", "Local"], servers.Select(s => s.Name));
        Assert.All(servers.Skip(1), s => Assert.Equal(LocalEndpoint.BaseUrl, s.BaseUrl));
        Assert.Equal(["gemma-4-e4b-qat", "gemma-4-e2b", "gemma-4-e4b-uncensored", "gemma-4-e2b-uncensored"], servers.Skip(1).Select(s => s.Result.ModelIds.Single()));
        Assert.Equal(["paused 42% · 5.2 GB", "installed · 4.2 GB", "download 6.4 GB", "download 4.4 GB"], servers.Skip(1).Select(s => s.Result.Detail));
        Assert.Equal([false, true, false, false], servers.Skip(1).Select(s => s.Result.Exists));
        Assert.DoesNotContain(_http.Requests, r => r.Uri.Host == LocalEndpoint.Host);   // the sentinel as the extra URL is never asked
    }

    [Fact]
    public async Task TheServerRows_StandEvenWithTheScanDisabled()
    {
        using var session = Session();

        var servers = await session.DiscoverAsync(new AppSettingsData { LlmScanMode = "disabled" }, CancellationToken.None);

        Assert.Equal(4, servers.Count);
        Assert.All(servers, s => Assert.True(LocalEndpoint.IsLocal(s.BaseUrl)));
    }

    // ── /botchat multi ──────────────────────────────────────────────────────

    [Fact]
    public async Task ABotBorrowingALocalServer_TakesItsLiveUrl_KeyAndModel()
    {
        _local.Installed("gemma-4-e2b");
        using var session = Session();
        await session.ConnectAsync(Local("gemma-4-e2b"), CancellationToken.None);

        var (link, problem) = await session.LinkAsync(new AppSettingsData { LlmModel = "something-else" }, CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal(FakeLocalLlm.LiveUrl, link!.Endpoint.LiveUrl);
        Assert.Equal(FakeLocalLlm.Key, link.Endpoint.ApiKey);
        Assert.Equal("gemma-4-e2b", link.Endpoint.ModelId);   // one model on the server
        link.Dispose();
    }

    [Fact]
    public async Task ABotOnTheLocalUrl_SharesTheRunningModel_OrIsRefusedAnother()
    {
        _local.Installed("gemma-4-e2b", "gemma-4-e4b-qat");
        using var session = Session();
        await session.ConnectAsync(Local("gemma-4-e2b"), CancellationToken.None);

        var (same, _) = await session.LinkAsync(Local("gemma-4-e2b"), CancellationToken.None);
        var (none, problem) = await session.LinkAsync(Local("gemma-4-e4b-qat"), CancellationToken.None);

        Assert.Equal(FakeLocalLlm.LiveUrl, same!.Endpoint.LiveUrl);
        Assert.Null(none);
        Assert.Equal(LocalLlmText.OneModelAtATime("Gemma 4 E2B"), problem);
        Assert.Single(_local.Starts);
        same.Dispose();
    }

    [Fact]
    public async Task ABotOnTheLocalUrl_WithNothingRunning_StartsItsInstalledModel()
    {
        _local.Installed("gemma-4-e2b");
        using var session = Session();

        var (link, problem) = await session.LinkAsync(Local("gemma-4-e2b"), CancellationToken.None);
        var (missing, why) = await session.LinkAsync(Local("gemma-4-e4b-qat"), CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal(FakeLocalLlm.LiveUrl, link!.Endpoint.LiveUrl);
        link.Dispose();
        Assert.Null(missing);
        Assert.Equal(LocalLlmText.OneModelAtATime("Gemma 4 E2B"), why);   // the first bot's start is the one running now
    }

    /// <summary>The Error lines logged while it lives.</summary>
    private sealed class LogCapture : IDisposable
    {
        public List<string> Errors { get; } = new();

        public LogCapture() => DiagnosticLog.Emitted += OnEmitted;

        private void OnEmitted(DiagnosticEvent entry)
        {
            if (entry.Level == DiagnosticLevel.Error)
            {
                Errors.Add(entry.Message);
            }
        }

        public void Dispose() => DiagnosticLog.Emitted -= OnEmitted;
    }
}
