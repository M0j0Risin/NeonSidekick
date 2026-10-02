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
        Assert.Equal("LLM: http://embedded.localhost/v1 model=gemma-4-e2b (embedded llama.cpp b11258 cuda on 127.0.0.1:59999)", LlmSession.ConnectedLine(endpoint));
        Assert.Same(endpoint, _endpoints.Single());
        Assert.NotNull(session.Assistant);
        Assert.Equal(["gemma-4-e2b"], _embedded.Starts);
        Assert.Contains("🦙 starting Gemma 4 E2B", labels);
        Assert.Equal(new ContextLength(32_768, "n_ctx on /props"), session.ContextLength);
        Assert.All(_http.Requests, r => Assert.Equal("127.0.0.1:59999", r.Uri.Authority));             // the live port, never the sentinel
        Assert.All(_http.Requests, r => Assert.Equal("Bearer " + FakeEmbeddedLlm.Key, r.Authorization));   // the server's own key, not the LLM API key
        Assert.Contains(_http.Requests, r => r.Uri.AbsolutePath == "/props");
        Assert.True(session.EmbeddedServer!.Vision);
    }

    /// <summary>
    /// Another model's load (2026-09-30, the user's ask): the old model's name leaves the endpoint, and so the hint row, as the
    /// load begins. A reconnect to the model already running keeps it.
    /// </summary>
    [Fact]
    public async Task AnotherModelsLoad_ClearsTheEndpointFirst_TheSameModelKeepsIt()
    {
        _embedded.Installed("gemma-4-e2b", "gemma-4-12b");
        ServeProps();
        using var session = Session();
        Assert.True(await session.ConnectAsync(Embedded("gemma-4-e2b"), (Action<string>?)null, CancellationToken.None));
        var seen = new List<string?>();
        _embedded.StartGate = _ => { seen.Add(session.Endpoint?.ModelId); return Task.CompletedTask; };

        Assert.True(await session.ConnectAsync(Embedded("gemma-4-12b"), (Action<string>?)null, CancellationToken.None));
        Assert.True(await session.ConnectAsync(Embedded("gemma-4-12b"), (Action<string>?)null, CancellationToken.None));

        Assert.Equal([null, "gemma-4-12b"], seen);
        Assert.Equal("gemma-4-12b", session.Endpoint!.ModelId);
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
        Assert.Empty(session.EmbeddedRows(new AppSettingsData()));
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
    public async Task TheServerRows_ListTheInstalledCatalogModels_AfterTheScan_BeforeTheClaudeApi()
    {
        // The installed models alone (2026-09-29, the user's ask: every catalog model until then, a download or a paused one
        // too): a download starts from the catalog on /settings › Embedded, whose rows carry the same details.
        _embedded.Installed("gemma-4-e2b", "gemma-4-12b").Partial("gemma-4-e4b-qat", 42);
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("qwen"));
        using var session = Session();

        var servers = await session.ProbeServersAsync(new AppSettingsData { LlmScanMode = "local" }, EmbeddedEndpoint.BaseUrl, CancellationToken.None);

        Assert.Equal(["LM Studio", "Embedded", "Embedded"], servers.Select(s => s.Name));
        Assert.All(servers.Skip(1), s => Assert.Equal(EmbeddedEndpoint.BaseUrl, s.BaseUrl));
        Assert.Equal(["gemma-4-12b", "gemma-4-e2b"], servers.Skip(1).Select(s => s.Result.ModelIds.Single()));
        Assert.Equal(["installed · 8 GB", "installed · 4.3 GB"], servers.Skip(1).Select(s => s.Result.Detail));
        Assert.All(servers.Skip(1), s => Assert.True(s.Result.Exists));
        Assert.Equal(
            ["installed · 8 GB", "download  · 9.2 GB", "download  · 11.3 GB", "download  · 24.5 GB", "download  · 7.1 GB", "download  · 7.8 GB",
             "download  · 18.7 GB", "download  · 22.9 GB", "download  · 25 GB", "download  · 15.7 GB", "download  · 18.2 GB", "download  · 18.1 GB", "download  · 20.5 GB", "download  · 24 GB",
             "download  · 20.5 GB", "download  · 23.6 GB", "download  · 18.8 GB", "download  · 20.2 GB",
             "installed · 4.3 GB", "download  · 4.4 GB", "download  · 6.2 GB", "paused    · 5.3 GB · 42%", "download  · 6.4 GB",
             "download  · 19.6 GB", "download  · 25.5 GB",
             "download  · 23.3 GB", "download  · 27.5 GB", "download  · 24.3 GB", "download  · 18.5 GB", "download  · 21.8 GB", "download  · 26.2 GB",
             "download  · 15.8 GB", "download  · 16.1 GB", "download  · 16.5 GB", "download  · 17.3 GB", "download  · 17.8 GB", "download  · 18.5 GB", "download  · 20.6 GB", "download  · 24.1 GB",
             "download  · 18.9 GB", "download  · 21.1 GB"],
            EmbeddedModelCatalog.Models.Select(m => EmbeddedLlmText.ModelDetail(m, _embedded.State(m))));   // each with its MTP drafter since 2026-09-29, Muse Glimmer's DFlash one since 2026-09-30
        Assert.DoesNotContain(_http.Requests, r => r.Uri.Host == EmbeddedEndpoint.Host);   // the sentinel as the extra URL is never asked
    }

    [Fact]
    public async Task TheServerRows_StandEvenWithTheScanDisabled()
    {
        _embedded.Installed("gemma-4-e2b", "gemma-4-e4b-qat");
        using var session = Session();

        var servers = await session.DiscoverAsync(new AppSettingsData { LlmScanMode = "disabled" }, CancellationToken.None);

        Assert.Equal(2, servers.Count);
        Assert.All(servers, s => Assert.True(EmbeddedEndpoint.IsEmbedded(s.BaseUrl)));
    }

    // ── Embedded servers enabled (2026-09-29) ───────────────────────────────────

    [Fact]
    public async Task SwitchedOff_TheRowsAreGone()
    {
        using var session = Session();
        var off = new AppSettingsData { LlmScanMode = "disabled", EmbeddedLlmServer = false };

        Assert.Empty(await session.DiscoverAsync(off, CancellationToken.None));
        Assert.Empty(session.EmbeddedRows(off));
        Assert.Empty(session.EmbeddedRows(new AppSettingsData()));   // on by default, but nothing installed: the installed models alone (2026-09-29)
        _embedded.Installed("gemma-4-e2b");
        Assert.Single(session.EmbeddedRows(new AppSettingsData()));
    }

    [Fact]
    public async Task SwitchedOff_ASavedEmbeddedUrl_StandsForNothing_AndTheRunningServerStops()
    {
        _embedded.Installed("gemma-4-e2b");
        using var session = Session();
        await session.ConnectAsync(Embedded("gemma-4-e2b"), CancellationToken.None);
        Assert.NotNull(_embedded.Running);
        var warnings = new List<string>();
        void Heard(DiagnosticEvent entry)
        {
            if (entry.Level == DiagnosticLevel.Warning)
            {
                warnings.Add(entry.Message);
            }
        }

        var off = Embedded("gemma-4-e2b");
        off.EmbeddedLlmServer = false;
        DiagnosticLog.Emitted += Heard;
        try
        {
            Assert.False(await session.ConnectAsync(off, CancellationToken.None));   // the scan is disabled by default: nothing found
        }
        finally
        {
            DiagnosticLog.Emitted -= Heard;
        }

        Assert.Null(_embedded.Running);
        Assert.Equal(1, _embedded.Stops);
        Assert.Equal(["gemma-4-e2b"], _embedded.Starts);   // never started again
        Assert.Null(session.Endpoint);
        Assert.Contains(EmbeddedLlmText.SwitchedOffWarning, warnings);
        Assert.Null(await session.ListModelsAsync(CancellationToken.None));   // no embedded list to offer
        Assert.True(EmbeddedEndpoint.SwitchedOff(off) && !EmbeddedEndpoint.Chosen(off));
        Assert.True(EmbeddedEndpoint.Chosen(Embedded()) && !EmbeddedEndpoint.SwitchedOff(Embedded()));
    }

    [Fact]
    public async Task SwitchedOff_ABotsEmbeddedLink_IsRefused()
    {
        _embedded.Installed("gemma-4-e2b");
        using var session = Session();
        await session.ConnectAsync(new AppSettingsData { EmbeddedLlmServer = false }, CancellationToken.None);

        var (link, problem) = await session.LinkAsync(Embedded("gemma-4-e2b"), CancellationToken.None);

        Assert.Null(link);
        Assert.Equal(EmbeddedLlmText.SwitchedOffError, problem);
        Assert.Empty(_embedded.Starts);
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
    public async Task ABotOnTheEmbeddedUrl_SharesTheRunningModel_EvenForAnother_UnderParentServer()
    {
        // A bot naming another model uses the running one with a warning (later on 2026-09-29, the user's ask; refused until then).
        _embedded.Installed("gemma-4-e2b", "gemma-4-e4b-qat");
        using var session = Session();
        await session.ConnectAsync(Embedded("gemma-4-e2b"), CancellationToken.None);

        var (same, _) = await session.LinkAsync(Embedded("gemma-4-e2b"), CancellationToken.None);
        var (other, problem) = await session.LinkAsync(Embedded("gemma-4-e4b-qat"), CancellationToken.None);

        Assert.Equal(FakeEmbeddedLlm.LiveUrl, same!.Endpoint.LiveUrl);
        Assert.Null(same.SharedEmbedded);
        Assert.Null(problem);
        Assert.Equal(FakeEmbeddedLlm.LiveUrl, other!.Endpoint.LiveUrl);
        Assert.Equal("gemma-4-e2b", other.Endpoint.ModelId);
        Assert.Equal(("Gemma 4 E4B QAT", "Gemma 4 E2B"), other.SharedEmbedded);
        Assert.Single(_embedded.Starts);
        Assert.Empty(_embedded.ExtraStarts);
        same.Dispose();
        other.Dispose();
    }

    [Fact]
    public async Task ABotOnTheEmbeddedUrl_WithNothingRunning_StartsItsInstalledModel_TheNextShares()
    {
        _embedded.Installed("gemma-4-e2b");
        using var session = Session();

        var (link, problem) = await session.LinkAsync(Embedded("gemma-4-e2b"), CancellationToken.None);
        var (next, why) = await session.LinkAsync(Embedded("gemma-4-e4b-qat"), CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal(FakeEmbeddedLlm.LiveUrl, link!.Endpoint.LiveUrl);
        Assert.Null(why);
        Assert.Equal(("Gemma 4 E4B QAT", "Gemma 4 E2B"), next!.SharedEmbedded);   // the first bot's start is the one running now, even for a model not on disk
        link.Dispose();
        next.Dispose();
    }

    [Fact]
    public async Task ParallelLinks_WithNothingRunning_StartTheFirstBotsModelOnly()
    {
        // The race of before later on 2026-09-29: each link saw nothing running and started its own model, stopping the one
        // before. One at a time now: the first in order starts, the second shares it.
        _embedded.Installed("gemma-4-e2b", "gemma-4-e4b-qat");
        _embedded.StartGate = async _ => await Task.Yield();
        using var session = Session();

        var links = await Task.WhenAll(session.LinkAsync(Embedded("gemma-4-e2b"), CancellationToken.None), session.LinkAsync(Embedded("gemma-4-e4b-qat"), CancellationToken.None));

        Assert.Equal(["gemma-4-e2b"], _embedded.Starts);
        Assert.All(links, l => Assert.Equal("gemma-4-e2b", l.Link!.Endpoint.ModelId));
        Assert.Equal(("Gemma 4 E4B QAT", "Gemma 4 E2B"), links[1].Link!.SharedEmbedded);
        Assert.All(links, l => l.Link!.Dispose());
    }

    [Fact]
    public async Task MultiServer_AnotherModel_GetsAnExtra_TheParentsKept_AndTwoBotsShareIt()
    {
        _embedded.Installed("gemma-4-e2b", "gemma-4-e4b-qat");
        _embedded.StartGate = async _ => await Task.Yield();
        using var session = Session();
        await session.ConnectAsync(Embedded("gemma-4-e2b"), CancellationToken.None);

        var links = await Task.WhenAll(
            session.LinkAsync(Embedded("gemma-4-e4b-qat"), BotEmbeddedMode.MultiServer, CancellationToken.None),
            session.LinkAsync(Embedded("gemma-4-e4b-qat"), BotEmbeddedMode.MultiServer, CancellationToken.None),
            session.LinkAsync(Embedded("gemma-4-e2b"), BotEmbeddedMode.MultiServer, CancellationToken.None));

        Assert.Equal(["gemma-4-e4b-qat"], _embedded.ExtraStarts);   // one extra for the two bots on it
        Assert.Equal(FakeEmbeddedLlm.ExtraUrl(1), links[0].Link!.Endpoint.LiveUrl);
        Assert.Equal(FakeEmbeddedLlm.ExtraUrl(1), links[1].Link!.Endpoint.LiveUrl);
        Assert.Equal("extra-key-1", links[0].Link!.Endpoint.ApiKey);
        Assert.Equal(FakeEmbeddedLlm.LiveUrl, links[2].Link!.Endpoint.LiveUrl);   // the parent's model: the parent's server
        Assert.All(links, l => Assert.Null(l.Link!.SharedEmbedded));
        Assert.Equal("gemma-4-e2b", _embedded.Running!.ModelId);
        Assert.Single(_embedded.Starts);
        Assert.All(links, l => l.Link!.Dispose());
    }

    [Fact]
    public async Task MultiServer_AnExtraThatFails_IsTheBotsProblem_AndTheSwitchOffStopsTheExtras()
    {
        _embedded.Installed("gemma-4-e2b", "gemma-4-e4b-qat", "gemma-4-e4b");
        using var session = Session();
        await session.ConnectAsync(Embedded("gemma-4-e2b"), CancellationToken.None);

        var (good, _) = await session.LinkAsync(Embedded("gemma-4-e4b"), BotEmbeddedMode.MultiServer, CancellationToken.None);
        _embedded.ExtraStartFailure = "out of memory";
        var (none, problem) = await session.LinkAsync(Embedded("gemma-4-e4b-qat"), BotEmbeddedMode.MultiServer, CancellationToken.None);

        Assert.NotNull(good);
        Assert.Null(none);
        Assert.Equal(EmbeddedLlmText.StartFailed("out of memory"), problem);
        Assert.Single(_embedded.Extras);

        await session.ConnectAsync(new AppSettingsData { EmbeddedLlmServer = false }, CancellationToken.None);
        Assert.Empty(_embedded.Extras);
        good!.Dispose();
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
