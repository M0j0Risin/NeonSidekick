using System.Net;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The embedded model's idle unload in the session (2026-11-16, plan lucid-roaming-lantern #44) over <see cref="FakeEmbeddedLlm"/> and a
/// <see cref="ManualTimeProvider"/>: the due after the minutes and the unload that keeps the model for the reload; never with a request in flight
/// (two links on one count), a load running, the screen busy, or the setting off (off mid-countdown disposes the timer); a parked model stopped
/// alone; a crash marked for the main model only; a disconnect retiring the count so a request it cut off arms nothing.
/// </summary>
public class LlmSessionIdleUnloadTests
{
    private static readonly TimeSpan Twenty = TimeSpan.FromMinutes(20);

    private readonly StubHttpMessageHandler _http = new();
    private readonly FakeEmbeddedLlm _embedded = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<FakeChatClient> _clients = new();
    private TimeSpan _minutes = Twenty;
    private bool _busy;
    private int _dues;

    public LlmSessionIdleUnloadTests()
    {
        _embedded.Installed("gemma-4-e2b", "gemma-4-12b");
        _http.Map("http://127.0.0.1:59999/props", HttpStatusCode.OK, "{\"default_generation_settings\":{\"n_ctx\":32768}}");
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("qwen"));
    }

    private LlmSession Session()
    {
        var session = new LlmSession(
            new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
            new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
            (_, _) =>
            {
                var client = new FakeChatClient();
                _clients.Add(client);
                return client;
            },
            time: _time,
            embedded: _embedded);
        session.UseIdleUnload(() => _minutes, () => _busy, () => Interlocked.Increment(ref _dues));
        return session;
    }

    private static AppSettingsData Embedded(string model = "gemma-4-e2b", bool oneLocal = true) =>
        new() { LlmUrl = "embedded", LlmModel = model, EmbeddedLlmServer = true, LlmOneLocalModel = oneLocal };

    /// <summary>A borrowed bot link to the main server, its request held open after its first update.</summary>
    private async Task<IAsyncEnumerator<ChatResponseUpdate>> HeldRequestAsync(LlmSession session)
    {
        var (link, problem) = await session.LinkAsync(new AppSettingsData(), CancellationToken.None);
        Assert.Null(problem);
        _clients[^1].EnqueueText("one", "two");
        var stream = link!.Client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        return stream;
    }

    [Fact]
    public async Task AfterTheMinutes_TheDueComes_AndTheUnloadKeepsTheModelForTheReload()
    {
        using var session = Session();
        Assert.True(await session.ConnectAsync(Embedded(), CancellationToken.None));

        _time.Advance(Twenty - TimeSpan.FromSeconds(1));
        Assert.Equal(0, _dues);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _dues);
        Assert.True(session.UnloadPending);

        Assert.Equal(LlmSession.UnloadDue.Idle, session.TakeUnloadDue());
        var done = session.UnloadIfIdle()!;
        Assert.Equal(("gemma-4-e2b", Twenty, (string?)null), (done.Model.Id, done.Idle, done.ParkedBehind));
        Assert.Null(_embedded.Running);
        Assert.Null(session.Assistant);
        Assert.Null(session.Endpoint);
        Assert.Equal("gemma-4-e2b", session.IdleUnloaded!.Model.Id);
        Assert.Equal(("gemma-4-e2b", EmbeddedEndpoint.BaseUrl, true), (session.ShownModelId, session.ShownBaseUrl, session.ShownUnloaded));
        Assert.Equal(0, _time.TimerCount);
        Assert.Equal("🦙 Unloaded Gemma 4 E2B after 20 min idle; the next message or command loads it again", EmbeddedLlmText.IdleUnloadedNotice(done.Model, done.Idle, null));

        // The reload's connect clears the mark; a failed one puts it back.
        var kept = session.IdleUnloaded;
        Assert.True(await session.ConnectAsync(Embedded(), CancellationToken.None));
        Assert.Null(session.IdleUnloaded);
        Assert.False(session.ShownUnloaded);
        session.RestoreIdleUnload(kept);
        Assert.Null(session.IdleUnloaded);   // a model is connected: nothing put back
    }

    [Fact]
    public async Task ARequestInFlight_HoldsIt_TwoLinksOnOneCount()
    {
        using var session = Session();
        await session.ConnectAsync(Embedded(), CancellationToken.None);
        var first = await HeldRequestAsync(session);
        var second = await HeldRequestAsync(session);

        await first.DisposeAsync();            // the first ends; the second is still in flight
        _time.Advance(Twenty * 3);
        Assert.Equal(0, _dues);

        await second.DisposeAsync();           // the last out arms the full minutes from now
        _time.Advance(Twenty - TimeSpan.FromSeconds(1));
        Assert.Equal(0, _dues);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _dues);

        // A request's start drops an idle due: the turn is use.
        var third = await HeldRequestAsync(session);
        Assert.False(session.UnloadPending);
        await third.DisposeAsync();
    }

    [Fact]
    public async Task ALoadRunning_OrTheScreenBusy_ReArmsTheFullMinutes()
    {
        using var session = Session();
        await session.ConnectAsync(Embedded(), CancellationToken.None);
        var gate = new TaskCompletionSource();
        _embedded.StartGate = _ => gate.Task;
        var load = _embedded.StartAsync(EmbeddedModelCatalog.Find("gemma-4-12b")!, Embedded("gemma-4-12b"), null, CancellationToken.None);
        Assert.True(_embedded.Starting);

        _time.Advance(Twenty);
        Assert.Equal((0, 1), (_dues, _time.TimerCount));   // re-armed, not fired
        gate.SetResult();
        await load;

        _busy = true;
        _time.Advance(Twenty);
        Assert.Equal((0, 1), (_dues, _time.TimerCount));
        _busy = false;
        _time.Advance(Twenty);
        Assert.Equal(1, _dues);

        // The drain checks again: busy by then, it re-arms and unloads nothing.
        _busy = true;
        Assert.Equal(LlmSession.UnloadDue.Idle, session.TakeUnloadDue());
        Assert.Null(session.UnloadIfIdle());
        Assert.NotNull(_embedded.Running);
        Assert.Equal(1, _time.TimerCount);
    }

    [Fact]
    public async Task Off_NeverArms_AndTurnedOffMidCountdown_DisposesTheTimer()
    {
        _minutes = TimeSpan.Zero;
        using var session = Session();
        await session.ConnectAsync(Embedded(), CancellationToken.None);
        Assert.Equal(0, _time.TimerCount);
        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, _dues);

        // On at 20, the last use a while ago: re-armed for what is left of the minutes, not from now.
        _minutes = Twenty;
        _time.Advance(TimeSpan.FromMinutes(5));
        session.IdleFromNow();
        _time.Advance(TimeSpan.FromMinutes(15));
        _minutes = TimeSpan.FromMinutes(25);
        session.RearmIdle();
        _time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, _dues);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _dues);

        // Off with a due waiting: the due dropped, no timer, still loaded.
        _minutes = TimeSpan.Zero;
        session.RearmIdle();
        Assert.False(session.UnloadPending);
        Assert.Equal(0, _time.TimerCount);
        Assert.NotNull(_embedded.Running);
    }

    [Fact]
    public async Task AParkedModel_IsStoppedAlone_ItsServerInUseKept()
    {
        using var session = Session();
        await session.ConnectAsync(Embedded(oneLocal: false), CancellationToken.None);
        var lm = new AppSettingsData { LlmUrl = "http://127.0.0.1:1234/v1", EmbeddedLlmServer = true, LlmOneLocalModel = false };
        Assert.True(await session.ConnectAsync(lm, CancellationToken.None));
        Assert.True(session.EmbeddedParked);

        _time.Advance(Twenty);
        Assert.Equal(LlmSession.UnloadDue.Idle, session.TakeUnloadDue());
        var done = session.UnloadIfIdle()!;
        Assert.Equal("LM Studio", done.ParkedBehind);
        Assert.Null(_embedded.Running);
        Assert.NotNull(session.Assistant);
        Assert.Equal(new Uri("http://127.0.0.1:1234/v1"), session.Endpoint!.BaseUrl);
        Assert.Null(session.IdleUnloaded);
        Assert.Equal("🦙 Unloaded Gemma 4 E2B, parked behind LM Studio, after 20 min idle", EmbeddedLlmText.IdleUnloadedNotice(done.Model, done.Idle, done.ParkedBehind));
    }

    [Fact]
    public async Task ACrash_MarksTheMainModel_NeverAParkedOne()
    {
        _minutes = TimeSpan.Zero;   // taken whatever the setting says
        using var session = Session();
        await session.ConnectAsync(Embedded(), CancellationToken.None);
        Assert.True(_embedded.RestartsAfterCrash);

        _embedded.Crash();
        Assert.Equal(1, _dues);
        Assert.Equal(LlmSession.UnloadDue.Crashed, session.TakeUnloadDue());
        Assert.True(session.MarkCrashed());
        Assert.Equal("gemma-4-e2b", session.IdleUnloaded!.Model.Id);
        Assert.Null(session.Assistant);

        await session.ConnectAsync(Embedded(oneLocal: false), CancellationToken.None);
        await session.ConnectAsync(new AppSettingsData { LlmUrl = "http://127.0.0.1:1234/v1", EmbeddedLlmServer = true, LlmOneLocalModel = false }, CancellationToken.None);
        Assert.True(session.EmbeddedParked);
        _embedded.Crash();
        Assert.Equal(1, _dues);
        Assert.False(session.UnloadPending);
    }

    [Fact]
    public async Task ADisconnect_RetiresTheCount_SoARequestItCutOffArmsNothing()
    {
        using var session = Session();
        await session.ConnectAsync(Embedded(), CancellationToken.None);
        var held = await HeldRequestAsync(session);

        // The kill switch mid-stream, then the stream's end.
        session.KillEmbedded();
        session.EmbeddedUnloaded();
        session.ForgetIdleUnload();
        await held.DisposeAsync();
        Assert.Equal(0, _time.TimerCount);
        _time.Advance(Twenty * 2);
        Assert.Equal(0, _dues);
        Assert.Null(session.IdleUnloaded);
    }

    [Fact]
    public async Task AConnect_ClearsADueWaiting_ForTheNewModel()
    {
        using var session = Session();
        await session.ConnectAsync(Embedded(), CancellationToken.None);
        _time.Advance(Twenty);
        Assert.True(session.UnloadPending);

        await session.ConnectAsync(Embedded("gemma-4-12b"), CancellationToken.None);
        Assert.False(session.UnloadPending);
        Assert.Equal(LlmSession.UnloadDue.None, session.TakeUnloadDue());
        Assert.Equal("gemma-4-12b", _embedded.Running!.ModelId);
    }
}
