using System.Net;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The session's embedded-model branch (2026-09-29) over <see cref="FakeEmbeddedLlm"/>: the sentinel shown, the live URL used.</summary>
public class LlmSessionEmbeddedTests
{
    private const string LlamaProps = "{\"default_generation_settings\":{\"n_ctx\":32768,\"n_predict\":-1},\"total_slots\":1,\"model_path\":\"/m.gguf\"}";

    private readonly StubHttpMessageHandler _http = new();
    private readonly List<LlmEndpoint> _endpoints = new();
    private readonly FakeEmbeddedLlm _embedded = new();

    private LlmSession Session(IEmbeddedLlm? embedded = null) => new(
        new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        (endpoint, _) =>
        {
            _endpoints.Add(endpoint);
            return new FakeChatClient();
        },
        embedded: embedded ?? _embedded);

    private static AppSettingsData Embedded(string model = "") => new() { LlmUrl = "embedded", LlmModel = model };

    private void ServeProps() => _http.Map("http://127.0.0.1:59999/props", HttpStatusCode.OK, LlamaProps);

    [Fact]
    public async Task AnEmbeddedUrl_StartsTheModel_ShowsTheSentinel_AndPostsToTheLiveUrl()
    {
        _embedded.Installed("gemma-4-e2b");
        ServeProps();
        using var session = Session();
        var labels = new List<string>();

        Assert.True(await session.ConnectAsync(Embedded("gemma-4-e2b"), labels.Add, CancellationToken.None));

        var endpoint = session.Endpoint!;
        Assert.Equal(EmbeddedEndpoint.BaseUrl, endpoint.BaseUrl);
        Assert.Equal(FakeEmbeddedLlm.LiveUrl, endpoint.LiveUrl);
        Assert.Equal("gemma-4-e2b", endpoint.ModelId);
        Assert.Equal(FakeEmbeddedLlm.Key, endpoint.ApiKey);
        Assert.Equal("embedded llama.cpp b11258 cuda on 127.0.0.1:59999", endpoint.Source);
        Assert.Equal("LLM: http://embedded-llm.invalid/v1 model=gemma-4-e2b (embedded llama.cpp b11258 cuda on 127.0.0.1:59999)", LlmSession.ConnectedLine(endpoint));
        Assert.Same(endpoint, _endpoints.Single());
        Assert.NotNull(session.Assistant);
        Assert.Equal(["gemma-4-e2b"], _embedded.Starts);
        Assert.Contains("starting Gemma 4 E2B on llama.cpp…", labels);
        Assert.Equal(new ContextLength(32_768, "n_ctx on /props"), session.ContextLength);
        Assert.All(_http.Requests, r => Assert.Equal("127.0.0.1:59999", r.Uri.Authority));             // the live port, never the sentinel
        Assert.All(_http.Requests, r => Assert.Equal("Bearer " + FakeEmbeddedLlm.Key, r.Authorization));   // the server's own key, not the LLM API key
        Assert.Contains(_http.Requests, r => r.Uri.AbsolutePath == "/props");
        Assert.True(session.EmbeddedServer!.Vision);
    }

    [Fact]
    public async Task AnEmbeddedUrl_WithNoModel_RunsTheFirstInstalled()
    {
        _embedded.Installed("gemma-4-e4b-qat", "gemma-4-e4b-uncensored", "gemma-4-12b");
        using var session = Session();

        Assert.True(await session.ConnectAsync(Embedded(), CancellationToken.None));

        Assert.Equal(["gemma-4-12b"], _embedded.Starts);   // catalog order, not install order
    }

    [Fact]
    public async Task AnEmbeddedModelNotInstalled_ConnectsNothing_AndSaysSo()
    {
        using var session = Session();
        using var log = new LogCapture();

        Assert.False(await session.ConnectAsync(Embedded("gemma-4-e4b-qat"), CancellationToken.None));

        Assert.Null(session.Endpoint);
        Assert.Null(session.Assistant);
        Assert.Empty(_embedded.Starts);
        Assert.Contains(EmbeddedLlmText.NotInstalled(EmbeddedModelCatalog.Find("gemma-4-e4b-qat")!), log.Errors);
    }

    [Fact]
    public async Task AnUnknownOrMissingEmbeddedModel_SaysWhich()
    {
        using var session = Session();
        using var log = new LogCapture();

        Assert.False(await session.ConnectAsync(Embedded("gemma-9"), CancellationToken.None));
        Assert.False(await session.ConnectAsync(Embedded(), CancellationToken.None));

        Assert.Equal([EmbeddedLlmText.UnknownModel("gemma-9"), EmbeddedLlmText.NoneInstalled], log.Errors);
    }

    [Fact]
    public async Task AStartThatFails_LeavesTheSentinelEndpoint_WithNoClient()
    {
        _embedded.Installed("gemma-4-e2b");
        _embedded.StartFailure = "no VRAM";
        using var session = Session();
        using var log = new LogCapture();

        Assert.False(await session.ConnectAsync(Embedded("gemma-4-e2b"), CancellationToken.None));

        Assert.Equal(EmbeddedEndpoint.BaseUrl, session.Endpoint!.BaseUrl);
        Assert.Null(session.Endpoint.LiveUrl);
        Assert.Equal(LlmSession.EmbeddedNotRunningSource, session.Endpoint.Source);
        Assert.Null(session.Assistant);
        Assert.Empty(_endpoints);
        Assert.Contains(EmbeddedLlmText.StartFailed("no VRAM"), log.Errors);
    }

    [Fact]
    public async Task AnEmbeddedUrl_WithNoEmbeddedModelOffered_IsAnError()
    {
        using var session = new LlmSession(new LlmEndpointProbe(new HttpClient(_http)), new ContextLengthProbe(new HttpClient(_http)), (_, _) => new FakeChatClient());
        using var log = new LogCapture();

        Assert.False(await session.ConnectAsync(Embedded("gemma-4-e2b"), CancellationToken.None));

        Assert.Contains(LlmSession.EmbeddedUnavailable, log.Errors);
        Assert.Empty(session.EmbeddedRows());
        Assert.Null(await session.ListModelsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnotherServer_StopsTheEmbeddedOne()
    {
        _embedded.Installed("gemma-4-e2b");
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("qwen"));
        using var session = Session();
        await session.ConnectAsync(Embedded("gemma-4-e2b"), CancellationToken.None);
        Assert.NotNull(_embedded.Running);

        Assert.True(await session.ConnectAsync(new AppSettingsData { LlmUrl = "http://127.0.0.1:1234/v1" }, CancellationToken.None));

        Assert.Null(_embedded.Running);
        Assert.Equal(1, _embedded.Stops);
        Assert.Null(session.EmbeddedServer);

        // A picked endpoint (the /server path) stops it too.
        await session.ConnectAsync(Embedded("gemma-4-e2b"), CancellationToken.None);
        session.Connect(new AppSettingsData(), new LlmEndpoint(new Uri("http://127.0.0.1:1234/v1"), "qwen", "k", "picked"));
        Assert.Null(_embedded.Running);
    }

    [Fact]
    public async Task EachReconnect_AsksTheServiceAgain_WhichKeepsARunningServer()
    {
        _embedded.Installed("gemma-4-e2b");
        using var session = Session();

        await session.ConnectAsync(Embedded("gemma-4-e2b"), CancellationToken.None);
        var high = Embedded("gemma-4-e2b");
        high.LlmReasoning = "high";
        await session.ConnectAsync(high, CancellationToken.None);

        Assert.Equal(["gemma-4-e2b", "gemma-4-e2b"], _embedded.Starts);
        Assert.Equal(0, _embedded.Stops);   // an embedded reconnect never stops the server; the service decides reuse
    }

    [Fact]
    public async Task ListModels_OnAnEmbeddedServer_IsTheInstalledCatalog()
    {
        _embedded.Installed("gemma-4-e2b", "gemma-4-e4b-qat");
        using var session = Session();
        await session.ConnectAsync(Embedded("gemma-4-e2b"), CancellationToken.None);
        int requests = _http.Requests.Count;

        var listed = await session.ListModelsAsync(CancellationToken.None);

        Assert.True(listed!.Value.Exists);
        Assert.Equal(["gemma-4-e2b", "gemma-4-e4b-qat"], listed.Value.ModelIds);
        Assert.Equal("2 embedded models", listed.Value.Detail);
        Assert.Equal(requests, _http.Requests.Count);   // asked of no server
    }

    [Fact]
    public async Task TheServerRows_ListEveryCatalogModel_AfterTheScan_BeforeTheClaudeApi()
    {
        _embedded.Installed("gemma-4-e2b").Partial("gemma-4-e4b-qat", 42);
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("qwen"));
        using var session = Session();

        var servers = await session.ProbeServersAsync(new AppSettingsData(), EmbeddedEndpoint.BaseUrl, CancellationToken.None);

        Assert.Equal(["LM Studio", .. Enumerable.Repeat("Embedded", 11)], servers.Select(s => s.Name));
        Assert.All(servers.Skip(1), s => Assert.Equal(EmbeddedEndpoint.BaseUrl, s.BaseUrl));
        Assert.Equal(EmbeddedModelCatalog.Models.Select(m => m.Id), servers.Skip(1).Select(s => s.Result.ModelIds.Single()));
        Assert.Equal(
            ["download  · 7.5 GB", "download  · 8.8 GB", "download  · 10.9 GB", "download  · 24 GB", "download  · 6.9 GB", "download  · 7.6 GB", "installed · 4.2 GB", "download  · 4.4 GB", "download  · 6.1 GB", "paused    · 5.2 GB · 42%", "download  · 6.4 GB"],
            servers.Skip(1).Select(s => s.Result.Detail));
        Assert.Equal([false, false, false, false, false, false, true, false, false, false, false], servers.Skip(1).Select(s => s.Result.Exists));
        Assert.DoesNotContain(_http.Requests, r => r.Uri.Host == EmbeddedEndpoint.Host);   // the sentinel as the extra URL is never asked
    }

    [Fact]
    public async Task TheServerRows_StandEvenWithTheScanDisabled()
    {
        using var session = Session();

        var servers = await session.DiscoverAsync(new AppSettingsData { LlmScanMode = "disabled" }, CancellationToken.None);

        Assert.Equal(11, servers.Count);
        Assert.All(servers, s => Assert.True(EmbeddedEndpoint.IsEmbedded(s.BaseUrl)));
    }

    // ── /botchat multi ──────────────────────────────────────────────────────

    [Fact]
    public async Task ABotBorrowingAnEmbeddedServer_TakesItsLiveUrl_KeyAndModel()
    {
        _embedded.Installed("gemma-4-e2b");
        using var session = Session();
        await session.ConnectAsync(Embedded("gemma-4-e2b"), CancellationToken.None);

        var (link, problem) = await session.LinkAsync(new AppSettingsData { LlmModel = "something-else" }, CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal(FakeEmbeddedLlm.LiveUrl, link!.Endpoint.LiveUrl);
        Assert.Equal(FakeEmbeddedLlm.Key, link.Endpoint.ApiKey);
        Assert.Equal("gemma-4-e2b", link.Endpoint.ModelId);   // one model on the server
        link.Dispose();
    }

    [Fact]
    public async Task ABotOnTheEmbeddedUrl_SharesTheRunningModel_OrIsRefusedAnother()
    {
        _embedded.Installed("gemma-4-e2b", "gemma-4-e4b-qat");
        using var session = Session();
        await session.ConnectAsync(Embedded("gemma-4-e2b"), CancellationToken.None);

        var (same, _) = await session.LinkAsync(Embedded("gemma-4-e2b"), CancellationToken.None);
        var (none, problem) = await session.LinkAsync(Embedded("gemma-4-e4b-qat"), CancellationToken.None);

        Assert.Equal(FakeEmbeddedLlm.LiveUrl, same!.Endpoint.LiveUrl);
        Assert.Null(none);
        Assert.Equal(EmbeddedLlmText.OneModelAtATime("Gemma 4 E2B"), problem);
        Assert.Single(_embedded.Starts);
        same.Dispose();
    }

    [Fact]
    public async Task ABotOnTheEmbeddedUrl_WithNothingRunning_StartsItsInstalledModel()
    {
        _embedded.Installed("gemma-4-e2b");
        using var session = Session();

        var (link, problem) = await session.LinkAsync(Embedded("gemma-4-e2b"), CancellationToken.None);
        var (missing, why) = await session.LinkAsync(Embedded("gemma-4-e4b-qat"), CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal(FakeEmbeddedLlm.LiveUrl, link!.Endpoint.LiveUrl);
        link.Dispose();
        Assert.Null(missing);
        Assert.Equal(EmbeddedLlmText.OneModelAtATime("Gemma 4 E2B"), why);   // the first bot's start is the one running now
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
