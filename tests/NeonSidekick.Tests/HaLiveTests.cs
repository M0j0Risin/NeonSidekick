using System.Text.Json;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The home bar's live feed (2026-10-12): <see cref="HaLive"/> over a scripted Home Assistant (<see cref="FakeHaServer"/>) —
/// the sign-in, the registries, the subscription kept to the wanted set, changes as they arrive, calls over the socket, a
/// refused token, a dropped connection and its retry, the linger after the bar hides, the picker's catalog — and the protocol's
/// pure pieces under it.
/// </summary>
public sealed class HaLiveTests : IDisposable
{
    private const string Server = "http://ha.test:8123";

    private readonly AppSettingsData _settings = new() { HomeAssistantUrl = Server, HomeAssistantToken = "secret" };
    private readonly ManualTimeProvider _time = new();
    private readonly FakeHaServer _ha = new();
    private readonly HaSession _session;
    private readonly HaLive _live;

    public HaLiveTests()
    {
        _ha.Floor("ground", "Ground", 0)
            .Area("den", "Den", "ground")
            .Area("kitchen", "Kitchen", "ground")
            .Entity("light.den_lamp", "on", """{"friendly_name":"Den Lamp","brightness":128,"rgb_color":[255,0,0]}""", area: "den")
            .Entity("light.den_spot", "off", """{"friendly_name":"Den Spot"}""", area: "den")
            .Entity("switch.kettle", "off", """{"friendly_name":"Kettle"}""", area: "kitchen")
            .Entity("lock.front", "locked", """{"friendly_name":"Front Door"}""");
        _session = new HaSession(() => _settings, time: _time);
        _live = new HaLive(_session, _ha.Connect, _time);
    }

    public void Dispose()
    {
        _live.Dispose();
        _session.Dispose();
    }

    /// <summary>Polls <paramref name="done"/> on the real clock (the feed runs on its own task), moving the manual one by <paramref name="step"/> each look.</summary>
    private async Task WaitUntil(Func<bool> done, TimeSpan? step = null, string what = "the condition")
    {
        var give = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < give, "Timed out waiting for " + what);
            if (step is { } s)
            {
                _time.Advance(s);
            }

            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Want_SignsIn_ReadsTheRegistries_AndSubscribesToTheWantedEntitiesAlone()
    {
        _live.Want(["light.den_lamp", "switch.kettle"]);

        await WaitUntil(() => _live.Snapshot.Entities.Count == 2, what: "the entities");

        var snapshot = _live.Snapshot;
        Assert.Equal(HaLiveStatus.Live, snapshot.Status);
        Assert.Equal("on", snapshot.Entities["light.den_lamp"].State);
        Assert.Equal("Den Lamp", snapshot.Entities["light.den_lamp"].Name);
        Assert.Equal(128, snapshot.Entities["light.den_lamp"].Number("brightness"));
        Assert.Equal(["light.den_lamp", "switch.kettle"], _ha.Subscribed.Order(StringComparer.Ordinal));
        Assert.NotNull(snapshot.Catalog);
        Assert.Equal(["Den", "Kitchen"], snapshot.Catalog!.Areas.Select(a => a.Name));
        Assert.Equal(["light.den_lamp", "light.den_spot"], snapshot.Catalog.MembersOf("den", "light"));
        // The sign-in carries the token; the label list this server lacks reads as none, not as no catalog.
        Assert.Contains(_ha.Received, m => m.Contains("\"access_token\":\"secret\"", StringComparison.Ordinal));
        Assert.Empty(snapshot.Catalog.Labels);
    }

    [Fact]
    public async Task AChange_ArrivesAsItHappens_AndMovesTheVersion()
    {
        _live.Want(["switch.kettle"]);
        await WaitUntil(() => _live.Snapshot.Entities.ContainsKey("switch.kettle"));
        long before = _live.Snapshot.Version;

        _ha.Change("switch.kettle", "on");

        await WaitUntil(() => _live.Snapshot.Entities["switch.kettle"].State == "on", what: "the change");
        Assert.True(_live.Snapshot.Version > before);
        Assert.Equal("Kettle", _live.Snapshot.Entities["switch.kettle"].Name);   // the attributes kept
    }

    [Fact]
    public async Task ANewWantedSet_SubscribesBeforeItUnsubscribes_AndDropsWhatIsNoLongerWanted()
    {
        _live.Want(["switch.kettle"]);
        await WaitUntil(() => _live.Snapshot.Entities.ContainsKey("switch.kettle"));

        _live.Want(["lock.front"]);

        await WaitUntil(() => _live.Snapshot.Entities.ContainsKey("lock.front") && !_live.Snapshot.Entities.ContainsKey("switch.kettle"), what: "the new set");
        var types = _ha.Received.Select(m => JsonDocument.Parse(m).RootElement.GetProperty("type").GetString()).Where(t => t is "subscribe_entities" or "unsubscribe_events").ToList();
        Assert.Equal(["subscribe_entities", "subscribe_entities", "unsubscribe_events"], types);
        Assert.Equal(1, _ha.Connections);
    }

    [Fact]
    public async Task CallAsync_RidesTheSocket_AndTheChangeComesBack()
    {
        _live.Want(["light.den_spot"]);
        await WaitUntil(() => _live.Snapshot.Fresh && _live.Snapshot.Entities.ContainsKey("light.den_spot"));

        var reply = await _live.CallAsync(HaServiceCall.On("light", "toggle", "light.den_spot"), CancellationToken.None);

        Assert.True(reply.Ok, reply.Error);
        var (service, target, data) = Assert.Single(_ha.Calls);
        Assert.Equal("light.toggle", service);
        Assert.Equal("""{"entity_id":"light.den_spot"}""", target);
        Assert.Null(data);
        await WaitUntil(() => _live.Snapshot.Entities["light.den_spot"].State == "on");
    }

    [Fact]
    public async Task CallAsync_AnAreaAndData_GoAsTargetAndServiceData()
    {
        _live.Want([]);
        await WaitUntil(() => _live.Snapshot.Fresh);

        var reply = await _live.CallAsync(new HaServiceCall("light", "turn_on", [], "den", """{"brightness_pct":40}"""), CancellationToken.None);

        Assert.True(reply.Ok, reply.Error);
        var (service, target, data) = Assert.Single(_ha.Calls);
        Assert.Equal("light.turn_on", service);
        Assert.Equal("""{"area_id":"den"}""", target);
        Assert.Equal("""{"brightness_pct":40}""", data);
        Assert.Equal("on", _ha.StateOf("light.den_spot"));
    }

    [Fact]
    public async Task ARefusedToken_IsUnauthorized_AndNeverRetried_UntilTheTokenChanges()
    {
        _ha.RefuseAuth = true;
        _live.Want(["switch.kettle"]);

        await WaitUntil(() => _live.Snapshot.Status == HaLiveStatus.Unauthorized, what: "the refusal");
        for (int i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(5);
        }

        Assert.Equal(1, _ha.Connections);
        Assert.StartsWith(HaText.Unauthorized, _live.Snapshot.Error);

        _ha.RefuseAuth = false;
        _settings.HomeAssistantToken = "secret";   // the same token typed again is no change: still refused
        _ha.Token = "fresh";
        _settings.HomeAssistantToken = "fresh";
        _live.Nudge();

        await WaitUntil(() => _live.Snapshot.Status == HaLiveStatus.Live, what: "the new token");
        Assert.Equal(2, _ha.Connections);
    }

    [Fact]
    public async Task ADroppedConnection_Retries_AfterTheFirstWait_AndKeepsTheLastStatesMeanwhile()
    {
        _live.Want(["switch.kettle"]);
        await WaitUntil(() => _live.Snapshot.Entities.ContainsKey("switch.kettle"));

        _ha.Drop();

        await WaitUntil(() => _live.Snapshot.Status == HaLiveStatus.Retrying, what: "the retry state");
        Assert.True(_live.Snapshot.Entities.ContainsKey("switch.kettle"));   // stale, shown dim, not gone
        await WaitUntil(() => _live.Snapshot.Status == HaLiveStatus.Live, TimeSpan.FromMilliseconds(250), "the reconnect");
        Assert.Equal(2, _ha.Connections);
    }

    [Fact]
    public async Task AnUnreachableServer_BacksOff_OneTwoFour()
    {
        _ha.Unreachable = true;
        _live.Want(["switch.kettle"]);
        await WaitUntil(() => _live.Snapshot.Status == HaLiveStatus.Retrying);

        Assert.Equal(TimeSpan.FromSeconds(1), HaLive.Backoff[0]);
        Assert.Equal(TimeSpan.FromSeconds(30), HaLive.Backoff[^1]);
        Assert.StartsWith("Error: the Home Assistant connection failed", _live.Snapshot.Error);

        _ha.Unreachable = false;
        await WaitUntil(() => _live.Snapshot.Status == HaLiveStatus.Live, TimeSpan.FromMilliseconds(500), "the reconnect");
    }

    [Fact]
    public async Task Hidden_TheConnectionLingers_ThenCloses()
    {
        _live.Want(["switch.kettle"]);
        await WaitUntil(() => _live.Snapshot.Fresh);

        _live.Want(null);
        _time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.True(_ha.Open);   // a quick hide and show keeps it

        await WaitUntil(() => !_ha.Open, TimeSpan.FromSeconds(1), "the close");
        await WaitUntil(() => _live.Snapshot.Status == HaLiveStatus.Off);
        Assert.Empty(_live.Snapshot.Entities);
    }

    [Fact]
    public async Task AStalledPong_Reconnects()
    {
        _live.Want(["switch.kettle"]);
        await WaitUntil(() => _live.Snapshot.Fresh);
        _ha.StallPong = true;

        await WaitUntil(() => _ha.Connections == 2, TimeSpan.FromSeconds(1), "the reconnect after the missing pong");
        Assert.Contains(_ha.Received, m => m.Contains("\"type\":\"ping\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotConfigured_IsOff_WithTheSentence_AndNoConnection()
    {
        _settings.HomeAssistantUrl = "";
        _live.Want(["switch.kettle"]);

        await WaitUntil(() => _live.Snapshot.Error == HaText.NotConfigured);
        Assert.Equal(HaLiveStatus.Off, _live.Snapshot.Status);
        Assert.Equal(0, _ha.Connections);
    }

    [Fact]
    public async Task CatalogAsync_ReadsTheRegistriesAndEveryState_OverASocketOfItsOwn()
    {
        var (catalog, error) = await _live.CatalogAsync(CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(catalog);
        Assert.Equal(4, catalog!.States.Count);
        Assert.Equal("Front Door", catalog.NameOf("lock.front"));
        Assert.Equal("kitchen", catalog.AreaOf("switch.kettle"));
        Assert.Equal("Ground", catalog.Floor(catalog.Area("den")!.FloorId)!.Name);
        Assert.False(_ha.Open);   // closed again
    }

    [Fact]
    public async Task ACallSentButUnanswered_IsNotSentAgain()
    {
        // The review's finding (2026-10-12): a toggle that timed out after it went was posted again over REST — twice is no toggle.
        _live.Want(["light.den_spot"]);
        await WaitUntil(() => _live.Snapshot.Fresh);
        _ha.SilentCalls = true;

        var pending = _live.CallAsync(HaServiceCall.On("light", "toggle", "light.den_spot"), CancellationToken.None);
        await WaitUntil(() => pending.IsCompleted, TimeSpan.FromSeconds(1), "the call's timeout");

        var reply = await pending;
        Assert.False(reply.Ok);
        Assert.Equal(HomeBarText.Unconfirmed("light.toggle"), reply.Error);
        Assert.Single(_ha.Calls);
        Assert.Equal("on", _ha.StateOf("light.den_spot"));   // it ran once
    }

    [Fact]
    public async Task AConnectionThatSignsInThenFails_StillBacksOff()
    {
        // The review's finding (2026-10-12): the count reset at every sign-in, so a server refusing the subscription was asked every second.
        _ha.RefuseSubscribe = true;
        _live.Want(["switch.kettle"]);
        var step = TimeSpan.FromMilliseconds(250);
        await WaitUntil(() => _ha.Connections == 2, step, "the first retry");
        var second = _time.GetUtcNow();
        await WaitUntil(() => _ha.Connections == 3, step, "the second retry");
        var third = _time.GetUtcNow();
        await WaitUntil(() => _ha.Connections == 4, step, "the third retry");

        Assert.True(_time.GetUtcNow() - third > third - second, "the waits grow");
        Assert.True(third - second >= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Want_AfterDispose_StartsNothing()
    {
        _live.Dispose();
        _live.Want(["switch.kettle"]);

        Assert.Null(_live.Loop);
        Assert.Equal(0, _ha.Connections);
    }

    [Fact]
    public async Task ThePickersCatalog_BecomesTheBars()
    {
        // The review's finding (2026-10-12): a room added in Home Assistant reached the picker but not the bar until a reconnect.
        _live.Want([]);
        await WaitUntil(() => _live.Snapshot.Fresh);
        _ha.Area("hall", "Hall").Entity("light.hall", "on", "{}", area: "hall");
        Assert.Null(_live.Snapshot.Catalog!.Area("hall"));

        var (catalog, _) = await _live.CatalogAsync(CancellationToken.None);

        Assert.NotNull(catalog!.Area("hall"));
        Assert.Same(catalog, _live.Snapshot.Catalog);
        Assert.Equal(["light.hall"], _live.Snapshot.Catalog!.MembersOf("hall", "light"));
    }

    [Fact]
    public void SocketUri_IsTheWebSocketBesideTheApi()
    {
        Assert.Equal("ws://ha.test:8123/api/websocket", HaWebSocketWire.SocketUri(new Uri("http://ha.test:8123")).AbsoluteUri);
        Assert.Equal("wss://home.example/api/websocket", HaWebSocketWire.SocketUri(new Uri("https://home.example/")).AbsoluteUri);
        Assert.Equal("ws://box:8123/ha/api/websocket", HaWebSocketWire.SocketUri(new Uri("http://box:8123/ha/")).AbsoluteUri);
    }

    [Fact]
    public void WithArea_PutsTheAreaInTheRestBody()
    {
        Assert.Null(HaLive.WithArea(HaServiceCall.On("light", "toggle", "light.x")));
        Assert.Equal("""{"area_id":"den","brightness_pct":40}""", HaLive.WithArea(new HaServiceCall("light", "turn_on", [], "den", """{"brightness_pct":40}""")));
    }

    [Fact]
    public void Protocol_WritesTheMessages()
    {
        Assert.Equal("""{"type":"auth","access_token":"t"}""", HaProtocol.Auth("t"));
        Assert.Equal("""{"id":3,"type":"ping"}""", HaProtocol.Command(3, HaProtocol.Ping));
        Assert.Equal("""{"id":4,"type":"subscribe_entities","entity_ids":["a.b","c.d"]}""", HaProtocol.SubscribeEntities(4, ["a.b", "c.d"]));
        Assert.Equal("""{"id":5,"type":"unsubscribe_events","subscription":4}""", HaProtocol.Unsubscribe(5, 4));
        Assert.Equal("""{"id":6,"type":"call_service","domain":"lock","service":"unlock","target":{"entity_id":"lock.front"}}""", HaProtocol.CallService(6, HaServiceCall.On("lock", "unlock", "lock.front"), out _));
        Assert.Null(HaProtocol.CallService(7, HaServiceCall.On("light", "turn_on", "light.x", "[1]"), out string? error));
        Assert.Equal(HaText.BadData, error);
    }

    [Theory]
    [InlineData("""{"type":"auth_required"}""", HaMessageKind.AuthRequired)]
    [InlineData("""{"type":"auth_ok"}""", HaMessageKind.AuthOk)]
    [InlineData("""{"type":"auth_invalid","message":"x"}""", HaMessageKind.AuthInvalid)]
    [InlineData("""{"id":1,"type":"result","success":true}""", HaMessageKind.Result)]
    [InlineData("""{"id":1,"type":"event","event":{}}""", HaMessageKind.Event)]
    [InlineData("""{"id":1,"type":"pong"}""", HaMessageKind.Pong)]
    [InlineData("""{"type":"what"}""", HaMessageKind.Other)]
    public void Classify_ReadsTheType(string json, HaMessageKind kind)
    {
        using var message = JsonDocument.Parse(json);
        Assert.Equal(kind, HaProtocol.Classify(message.RootElement));
    }

    [Fact]
    public void Diff_AddsChangesAndRemoves()
    {
        var entities = new Dictionary<string, HaLiveEntity>(StringComparer.Ordinal);
        Apply("""{"a":{"light.x":{"s":"on","a":{"brightness":10,"friendly_name":"X","effect":"none"},"lu":1700000000.5}}}""");
        Assert.Equal("on", entities["light.x"].State);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000500), entities["light.x"].LastChanged);

        Apply("""{"c":{"light.x":{"+":{"s":"off","a":{"brightness":20},"lc":1700000001},"-":{"a":["effect"]}}}}""");
        var x = entities["light.x"];
        Assert.Equal("off", x.State);
        Assert.Equal(20, x.Number("brightness"));
        Assert.Equal("X", x.Name);
        Assert.False(x.Attributes.ContainsKey("effect"));

        Assert.False(HaEntityDiff.Apply(entities, Parse("""{"c":{"light.unknown":{"+":{"s":"on"}}}}""")));   // a change before its add
        Apply("""{"r":["light.x"]}""");
        Assert.Empty(entities);

        void Apply(string json) => Assert.True(HaEntityDiff.Apply(entities, Parse(json)));
    }

    [Fact]
    public void Catalog_AnEntityWithoutAnAreaOfItsOwn_IsInItsDevicesArea_AndHiddenOnesAreNotOffered()
    {
        var catalog = HaCatalog.Parse(
            Parse("""[{"floor_id":"up","name":"Upstairs","level":1},{"floor_id":"down","name":"Downstairs","level":0}]"""),
            Parse("""[{"area_id":"bed","name":"Bedroom","floor_id":"up"},{"area_id":"hall","name":"Hall","floor_id":"down"}]"""),
            Parse("""[{"id":"dev1","name":"Hue bulb","name_by_user":"Bed lamp","area_id":"bed"}]"""),
            Parse("""[{"entity_id":"light.bed","device_id":"dev1","area_id":null,"disabled_by":null,"hidden_by":null,"entity_category":null,"labels":["night"]},{"entity_id":"light.hall","device_id":null,"area_id":"hall","disabled_by":null,"hidden_by":"user","entity_category":null,"labels":[]},{"entity_id":"sensor.bed_rssi","device_id":"dev1","area_id":null,"disabled_by":null,"hidden_by":null,"entity_category":"diagnostic","labels":[]}]"""),
            Parse("""[{"label_id":"night","name":"Night"}]"""),
            default);

        Assert.Equal("bed", catalog.AreaOf("light.bed"));
        Assert.Equal("bed", catalog.AreaOf("sensor.bed_rssi"));
        Assert.Equal(["light.bed"], catalog.MembersOf("bed", "light"));
        Assert.Empty(catalog.MembersOf("hall", "light"));   // hidden
        Assert.False(catalog.Visible("sensor.bed_rssi"));
        Assert.Equal(["Night"], catalog.LabelsOf("light.bed"));
        Assert.Equal(["Hall", "Bedroom"], catalog.Areas.Select(a => a.Name));   // by floor level, then name
        Assert.Equal(["Downstairs", "Upstairs"], catalog.OrderedFloors.Select(f => f.Name));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
