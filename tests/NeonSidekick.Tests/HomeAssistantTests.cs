using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The Home Assistant integration (2026-09-28): the snapshot and the name lookup over a fixture made from a real Hue + Bravia
/// instance's shapes, the policy, the words, the client and the session over a stub server, the nine tools with the confirm
/// seam, <c>/ha</c>'s engine and argument list, and the turn's offer.
/// </summary>
public sealed class HomeAssistantTests : IDisposable
{
    private const string Server = "http://ha.lan:8123";
    private const string Token = "test-token";

    private static readonly string StatesJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha", "states.json"));
    private static readonly string AreasJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha", "areas.json"));

    private readonly AppSettingsData _settings = new() { HomeAssistantUrl = Server, HomeAssistantToken = Token };
    private readonly StubHttpMessageHandler _stub = new();
    private readonly ManualTimeProvider _time = new();
    private readonly HaSession _ha;
    private readonly List<string> _asked = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public HomeAssistantTests()
    {
        _ha = new HaSession(() => _settings, (url, token) => new HaClient(url, token, new HttpClient(_stub)), _time);
        _stub.Map(Server + "/api/states", HttpStatusCode.OK, StatesJson);
        _stub.Map(Server + "/api/template", HttpStatusCode.OK, AreasJson, "text/plain");
        _stub.Map(Server + "/api/services/", HttpStatusCode.OK, "[]");
        _stub.Map(Server + "/api/config", HttpStatusCode.OK, """{"version":"2026.9.4","location_name":"Home","time_zone":"America/Chicago"}""");
    }

    public void Dispose()
    {
        _ha.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static HaSnapshot Snapshot() => HaSnapshot.Parse(StatesJson, AreasJson, ManualTimeProvider.DefaultUtcNow)!;

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (name, value) in pairs)
        {
            arguments[name] = value;
        }

        return arguments;
    }

    private static async Task<string> Invoke(AIFunction tool, params (string Name, object? Value)[] pairs) =>
        (string)(await tool.InvokeAsync(Args(pairs)))!;

    private Func<string, CancellationToken, Task<bool?>> Answer(bool? yes) => (question, _) =>
    {
        _asked.Add(question);
        return Task.FromResult(yes);
    };

    private IReadOnlyList<RecordedRequest> ServiceCalls() => _stub.Requests.Where(r => r.Uri.AbsolutePath.StartsWith("/api/services/", StringComparison.Ordinal)).ToList();

    // ── the snapshot ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsTheEntities_TheirAreas_AndTheGroups()
    {
        var snapshot = Snapshot();

        Assert.Equal(25, snapshot.Entities.Count);
        Assert.Equal(6, snapshot.Areas.Count);
        var den = snapshot.Find("light.den_den")!;
        Assert.Equal("Den", den.Name);
        Assert.Equal("Den", den.Area);
        Assert.True(den.IsGroup);
        Assert.Equal("light", den.Domain);
        Assert.False(snapshot.Find("light.den_den_corner_lamp")!.IsGroup);
        Assert.Null(snapshot.Find("light.indoor")!.Area);
        Assert.True(snapshot.Find("light.free_1")!.Unavailable);
    }

    [Fact]
    public void Parse_WithoutAreas_StillReadsTheStates_AndBadJsonIsNull()
    {
        var snapshot = HaSnapshot.Parse(StatesJson, "not json", ManualTimeProvider.DefaultUtcNow)!;

        Assert.Empty(snapshot.Areas);
        Assert.Null(snapshot.Find("light.den_den")!.Area);
        Assert.Null(HaSnapshot.Parse("{", null, ManualTimeProvider.DefaultUtcNow));
        Assert.Null(HaSnapshot.Parse("{}", null, ManualTimeProvider.DefaultUtcNow));
    }

    [Theory]
    [InlineData("den", "light.den_den")]
    [InlineData("the den lights", "light.den_den")]
    [InlineData("Den", "light.den_den")]
    [InlineData("den corner lamp", "light.den_den_corner_lamp")]
    [InlineData("LIGHT.KITCHEN_KITCHEN_PREP_1", "light.kitchen_kitchen_prep_1")]
    [InlineData("living room fan", "light.living_room_fan_living_room_fan")]
    [InlineData("living room", "light.living_room_living_room_corner")]
    [InlineData("hallway", "light.hallway_hallway_overhead")]
    [InlineData("indoor", "light.indoor")]
    public void Resolve_Lights_ByRoom_Name_OrId(string target, string id)
    {
        var match = Snapshot().Resolve(target, ["light"]);

        Assert.Null(match.Error);
        Assert.Equal([id], match.Entities.Select(e => e.Id));
    }

    [Fact]
    public void Resolve_SeveralTargets_AndAll()
    {
        var snapshot = Snapshot();

        Assert.Equal(["light.kitchen_kitchen", "light.den_den"], snapshot.Resolve("kitchen and den", ["light"]).Entities.Select(e => e.Id));
        Assert.Equal(["light.kitchen_kitchen", "light.den_den"], snapshot.Resolve("kitchen, den", ["light"]).Entities.Select(e => e.Id));

        var all = snapshot.Resolve("all lights", ["light"]).Entities;
        Assert.DoesNotContain(all, e => e.IsGroup);
        Assert.DoesNotContain(all, e => e.Unavailable);
        Assert.Equal(8, all.Count);
    }

    [Fact]
    public void Resolve_Scenes_ByName_AndAnAmbiguityNamesTheCandidates()
    {
        var snapshot = Snapshot();

        Assert.Equal("scene.den_den_relax", snapshot.Resolve("den relax", ["scene"]).Entities.Single().Id);
        Assert.Equal("scene.den_den_relax", snapshot.Resolve("relax in the den", ["scene"]).Entities.Single().Id);

        var relax = snapshot.Resolve("relax", ["scene"]);
        Assert.Empty(relax.Entities);
        Assert.StartsWith("Error: 'relax' could be 2 things; name one: ", relax.Error);
        Assert.Contains("Den Relax (scene.den_den_relax)", relax.Error);
        Assert.Contains("Kitchen Relax (scene.kitchen_kitchen_relax)", relax.Error);
    }

    [Fact]
    public void Resolve_AMiss_SaysWhatIsNear_AndEmptyIsRefused()
    {
        var snapshot = Snapshot();

        var miss = snapshot.Resolve("garage", ["light"]);
        Assert.StartsWith("Error: nothing named 'garage' among light; some that exist: ", miss.Error);

        var prep = snapshot.Resolve("prep", ["light"]);
        Assert.StartsWith("Error: 'prep' could be 2 things", prep.Error);

        Assert.Equal(HaText.NoTarget, snapshot.Resolve("  ", ["light"]).Error);
        Assert.Equal(HaText.NoTarget, snapshot.Resolve("the", ["light"]).Error);
    }

    [Fact]
    public void Words_DropFiller_AndDomainWords_WhileSomethingIsLeft()
    {
        Assert.Equal(["den"], HaSnapshot.Words("The Den Lights"));
        Assert.Equal(["lights"], HaSnapshot.Words("the lights"));
        Assert.Equal("sensor 1 pantry motion", HaSnapshot.Normalize("Sensor 1 - Pantry Motion"));
    }

    // ── the policy ──────────────────────────────────────────────────────────

    [Fact]
    public void Policy_Ask_RunsTheSafeList_AndAsksForTheRest()
    {
        var effective = new AppSettingsData();

        Assert.Equal(HaPolicy.Ask, effective.HomeAssistantActionPolicy);
        Assert.Equal(HaVerdict.Run, HaPolicy.Judge(effective, "light", "turn_on"));
        Assert.Equal(HaVerdict.Run, HaPolicy.Judge(effective, "light", "toggle"));
        Assert.Equal(HaVerdict.Run, HaPolicy.Judge(effective, "scene", "turn_on"));
        Assert.Equal(HaVerdict.Run, HaPolicy.Judge(effective, "media_player", "select_source"));
        Assert.Equal(HaVerdict.Run, HaPolicy.Judge(effective, "todo", "add_item"));
        Assert.Equal(HaVerdict.Ask, HaPolicy.Judge(effective, "remote", "send_command"));
        Assert.Equal(HaVerdict.Ask, HaPolicy.Judge(effective, "button", "press"));
        Assert.Equal(HaVerdict.Ask, HaPolicy.Judge(effective, "homeassistant", "restart"));
        Assert.Equal(HaVerdict.Ask, HaPolicy.Judge(effective, "scene", "delete"));
    }

    [Fact]
    public void Policy_OffAndAllow_AndReadsRunUnderEvery()
    {
        var off = new AppSettingsData { HomeAssistantActionPolicy = "OFF" };
        var allow = new AppSettingsData { HomeAssistantActionPolicy = "allow" };

        Assert.Equal(HaVerdict.Refuse, HaPolicy.Judge(off, "light", "turn_on"));
        Assert.Equal(HaVerdict.Run, HaPolicy.Judge(off, "todo", "get_items"));
        Assert.Equal(HaVerdict.Run, HaPolicy.Judge(allow, "homeassistant", "restart"));
        Assert.Equal(HaPolicy.Ask, HaPolicy.Resolve("sometimes"));
    }

    [Fact]
    public void Policy_ASafeListOfTheProfile_ReplacesTheDefault_WildcardsTakeADomain()
    {
        var effective = new AppSettingsData { HomeAssistantSafeServices = ["remote.*", "button.press"] };

        Assert.Equal(HaVerdict.Run, HaPolicy.Judge(effective, "remote", "send_command"));
        Assert.Equal(HaVerdict.Run, HaPolicy.Judge(effective, "Button", "Press"));
        Assert.Equal(HaVerdict.Ask, HaPolicy.Judge(effective, "light", "turn_on"));
        Assert.True(HaPolicy.IsSafe("light", "turn_off", null));
    }

    // ── the words ───────────────────────────────────────────────────────────

    [Fact]
    public void StateLine_CarriesWhatMattersForTheDomain()
    {
        var snapshot = Snapshot();

        Assert.Equal("light.den_den · Den · on · 40% · group · in Den", HaText.StateLine(snapshot.Find("light.den_den")!));
        Assert.Equal("light.den_den_corner_lamp · Den Corner Lamp · on · 100% · 2700 K · in Den", HaText.StateLine(snapshot.Find("light.den_den_corner_lamp")!));
        Assert.Equal("media_player.bravia_xr_55a80l · BRAVIA XR-55A80L · on · source HDMI 1 · volume 44% · playing Smart TV", HaText.StateLine(snapshot.Find("media_player.bravia_xr_55a80l")!));
        Assert.Equal("sensor.sensor_1_pantry_temperature · Sensor 1 - Pantry Temperature · 73.22 °F · temperature · in Pantry", HaText.StateLine(snapshot.Find("sensor.sensor_1_pantry_temperature")!));
    }

    [Fact]
    public void Overview_ListsTheRooms_TheTv_TheSensors_AndWhatIsGone()
    {
        var lines = HaText.Overview(Snapshot());

        Assert.Equal("Home Assistant: 3 of 8 lights on", lines[0]);
        Assert.Contains("- Den: 2 of 3 on up to 100% · group light.den_den", lines);
        Assert.Contains("- Kitchen: off · group light.kitchen_kitchen", lines);
        Assert.Contains("- Living Room: off", lines);
        Assert.Contains("Light groups: Indoor (light.indoor, on)", lines);
        Assert.Contains(lines, l => l.StartsWith("Media: media_player.bravia_xr_55a80l", StringComparison.Ordinal));
        Assert.Contains("Temperature: Sensor 1 - Pantry Temperature 73.22 °F", lines);
        Assert.Contains("Motion: Sensor 1 - Pantry Motion clear", lines);
        Assert.Contains("Low batteries: Switch 6 - Kitchen Battery 12%", lines);
        Assert.Contains("To-do: Shopping list (todo.shopping_list) 2 open", lines);
        Assert.Contains("Scenes: 4 (ha_states with domain scene lists them)", lines);
        Assert.Contains("Unavailable lights: Free 1", lines);
    }

    [Fact]
    public void History_IsOldestFirst_InTheZone_WithTheUnit()
    {
        var entity = Snapshot().Find("binary_sensor.sensor_1_pantry_motion")!;
        const string body = """[[{"entity_id":"binary_sensor.sensor_1_pantry_motion","state":"off","attributes":{},"last_changed":"2026-09-11T17:59:48+00:00","last_updated":"2026-09-11T17:59:48+00:00"},{"state":"on","last_changed":"2026-09-11T21:45:11+00:00"},{"state":"off","last_changed":"2026-09-11T21:45:21+00:00"}]]""";

        string text = HaText.History(entity, body, 24, ManualTimeProvider.DefaultZone);

        Assert.Equal("Sensor 1 - Pantry Motion (binary_sensor.sensor_1_pantry_motion): 3 states in the last 24 hours\nFri 10:59 off\nFri 14:45 on\nFri 14:45 off", text);
    }

    [Fact]
    public void TodoItems_OpenFirst_ACompletedOneMarked()
    {
        var list = Snapshot().Find("todo.shopping_list")!;
        const string body = """{"changed_states":[],"service_response":{"todo.shopping_list":{"items":[{"summary":"eggs","uid":"1","status":"completed"},{"summary":"milk","uid":"2","status":"needs_action"}]}}}""";

        Assert.Equal("Shopping list: 1 open item, 1 completed\n- milk\n- eggs (completed)", HaText.TodoItems(list, body));
        Assert.Equal("Error: Home Assistant's answer to todo.get_items could not be read", HaText.TodoItems(list, "{}"));
    }

    [Fact]
    public void AssistAnswer_KindAndWords_AnErrorIsAnError()
    {
        Assert.Equal("Assist (action done): Turned on the lights", HaText.AssistAnswer("""{"response":{"response_type":"action_done","speech":{"plain":{"speech":"Turned on the lights"}}}}"""));
        Assert.Equal("Error: Assist could not do it: Sorry, I am not aware of any area called garage", HaText.AssistAnswer("""{"response":{"response_type":"error","speech":{"plain":{"speech":"Sorry, I am not aware of any area called garage"}}}}"""));
    }

    [Fact]
    public void HttpError_QuotesTheServersMessage_OnOneLine()
    {
        Assert.Equal("Error: Home Assistant answered 400 to /api/services/light/turn_on: extra keys not allowed @ data['bad']",
            HaText.HttpError(400, "/api/services/light/turn_on", """{"message":"extra keys not allowed @ data['bad']"}"""));
        Assert.Equal("Error: Home Assistant answered 404 to /api/states/x: Entity not found.", HaText.HttpError(404, "/api/states/x", "Entity not found.\n"));
    }

    // ── the client ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Client_SendsTheBearerToken_AndTheServicePath()
    {
        using var client = new HaClient(new Uri(Server + "/"), " " + Token + " ", new HttpClient(_stub));

        var reply = await client.CallServiceAsync("todo", "get_items", """{"entity_id":"todo.shopping_list"}""", returnResponse: true, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(reply.Ok);
        var request = _stub.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer " + Token, request.Authorization);
        Assert.Equal(Server + "/api/services/todo/get_items?return_response", request.Uri.AbsoluteUri);
        Assert.Equal("""{"entity_id":"todo.shopping_list"}""", request.Body);
    }

    [Fact]
    public async Task Client_A401_IsTheTokenSentence_AClosedPortIsUnreachable()
    {
        var stub = new StubHttpMessageHandler().Map(Server + "/api/config", HttpStatusCode.Unauthorized, "401: Unauthorized");
        using var client = new HaClient(new Uri(Server), Token, new HttpClient(stub));
        using var closed = new HaClient(new Uri("http://nowhere.lan:8123"), Token, new HttpClient(stub));

        Assert.Equal(HaText.Unauthorized, (await client.ConfigAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).Error);
        Assert.StartsWith("Error: cannot reach Home Assistant at http://nowhere.lan:8123: ", (await closed.ConfigAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Client_ItsOwnTimeoutIsASentence_TheCallersCancellationThrows()
    {
        var stub = new StubHttpMessageHandler().Map(Server, async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        using var client = new HaClient(new Uri(Server), Token, new HttpClient(stub));

        var slow = await client.PingAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.StartsWith("Error: Home Assistant did not answer within ", slow.Error);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PingAsync(TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public void HistoryPath_EscapesEveryPart()
    {
        string path = HaClient.HistoryPath("sensor.x", new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 28, 6, 0, 0, TimeSpan.FromHours(1)));

        Assert.Equal("/api/history/period/2026-09-28T00%3A00%3A00%2B00%3A00?filter_entity_id=sensor.x&end_time=2026-09-28T05%3A00%3A00%2B00%3A00&minimal_response&no_attributes", path);
    }

    [Fact]
    public void ServiceBody_PutsTheIdsFirst_AndRefusesDataThatIsNoObject()
    {
        Assert.Equal("""{"entity_id":["a.b","c.d"],"command":"Home"}""", HaJson.ServiceBody(["a.b", "c.d"], """{"entity_id":"x.y","command":"Home"}""", out _));
        Assert.Equal("""{"command":"Home"}""", HaJson.ServiceBody([], """{"command":"Home"}""", out _));
        Assert.Null(HaJson.ServiceBody([], "[1]", out string? error));
        Assert.Equal(HaText.BadData, error);
    }

    // ── the session ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Session_WithoutUrlOrToken_IsNotConfigured()
    {
        _settings.HomeAssistantToken = "";

        Assert.Null(_ha.Client());
        Assert.Equal(HaText.NotConfigured, (await _ha.SnapshotAsync(CancellationToken.None)).Error);
        Assert.False(ChatScreen.HomeAssistantOffered(_settings));

        _settings.HomeAssistantToken = Token;
        _settings.HomeAssistantUrl = "ftp://ha.lan";
        Assert.Null(HaSession.ServerOf(_settings));
    }

    [Fact]
    public async Task Session_KeepsTheSnapshot_UntilItAges_OrACallChangesSomething()
    {
        await _ha.SnapshotAsync(CancellationToken.None);
        await _ha.SnapshotAsync(CancellationToken.None);
        Assert.Single(_stub.Requests, r => r.Uri.AbsolutePath == "/api/states");

        _time.Advance(HaSession.SnapshotAge + TimeSpan.FromSeconds(1));
        await _ha.SnapshotAsync(CancellationToken.None);
        Assert.Equal(2, _stub.Requests.Count(r => r.Uri.AbsolutePath == "/api/states"));

        await _ha.CallAsync("light", "turn_off", ["light.den_den"], null, CancellationToken.None);
        await _ha.SnapshotAsync(CancellationToken.None);
        Assert.Equal(3, _stub.Requests.Count(r => r.Uri.AbsolutePath == "/api/states"));
    }

    [Fact]
    public void Session_TheTokenIsEncrypted_AndReadBack()
    {
        string stored = HaSession.Protect(" secret ", out string? error);

        Assert.Null(error);
        Assert.StartsWith(Sql.WindowsCredentials.ProtectedPrefix, stored);
        Assert.Equal("secret", HaSession.TokenOf(new AppSettingsData { HomeAssistantToken = stored }));
        Assert.Equal("plain", HaSession.TokenOf(new AppSettingsData { HomeAssistantToken = "plain" }));
        Assert.Null(HaSession.TokenOf(new AppSettingsData { HomeAssistantToken = Sql.WindowsCredentials.ProtectedPrefix + "bm90IGEgYmxvYg==" }));
    }

    [Fact]
    public async Task Session_Test_IsTheServerLine()
    {
        var (ok, text) = await _ha.TestAsync(CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("Home Assistant 2026.9.4 at http://ha.lan:8123 · Home · America/Chicago", text);
    }

    // ── the tools ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Lights_ARoomGoesToItsGroup_WithTheBrightness()
    {
        var tool = new HaLightsTool(_ha, Answer(null));

        string result = await Invoke(tool, ("target", "the den"), ("brightness_pct", 30));

        Assert.Equal("light.turn_on → Den · 30%", result);
        var call = ServiceCalls().Single();
        Assert.Equal("/api/services/light/turn_on", call.Uri.AbsolutePath);
        Assert.Equal("""{"entity_id":"light.den_den","brightness_pct":30}""", call.Body);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Lights_OffDropsTheColour_AndBadArgumentsAreRefused()
    {
        var tool = new HaLightsTool(_ha, Answer(null));

        Assert.Equal("light.turn_off → Kitchen, Den", await Invoke(tool, ("target", "kitchen and den"), ("action", "off"), ("color_name", "red")));
        Assert.Equal("""{"entity_id":["light.kitchen_kitchen","light.den_den"]}""", ServiceCalls().Single().Body);

        Assert.StartsWith("Error: 'dim' is not a valid action", await Invoke(tool, ("target", "den"), ("action", "dim")));
        Assert.StartsWith("Error: '150' is not a whole number from 0 to 100", await Invoke(tool, ("target", "den"), ("brightness_pct", 150)));
        Assert.StartsWith("Error: '900' is not a whole number from 1500 to 9000", await Invoke(tool, ("target", "den"), ("color_temp_kelvin", 900)));
        Assert.Equal(HaText.Missing("target"), await Invoke(tool));
        Assert.Single(ServiceCalls());
    }

    [Fact]
    public void Lights_Data_TemperatureWinsOverAColourName()
    {
        Assert.Equal("""{"brightness_pct":50,"color_temp_kelvin":2700,"transition":2}""", HaLightsTool.Data(50, "warm white", 2700, 2));
        Assert.Equal("""{"color_name":"warmwhite"}""", HaLightsTool.Data(null, "Warm White", null, null));
    }

    [Fact]
    public async Task Lights_UnderPolicyOff_AreRefused_WithoutACall()
    {
        _settings.HomeAssistantActionPolicy = HaPolicy.Off;

        Assert.Equal(HaText.PolicyOff, await Invoke(new HaLightsTool(_ha, Answer(true)), ("target", "den")));
        Assert.Empty(ServiceCalls());
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Scene_ByName_AndAnAmbiguityIsAnError()
    {
        var tool = new HaSceneTool(_ha, Answer(null));

        Assert.Equal("scene.turn_on → Den Relax", await Invoke(tool, ("scene", "den relax")));
        Assert.Equal("""{"entity_id":"scene.den_den_relax"}""", ServiceCalls().Single().Body);
        Assert.StartsWith("Error: 'relax' could be 2 things", await Invoke(tool, ("scene", "relax")));
    }

    [Fact]
    public async Task Media_TheOnlyPlayer_ASourceByItsStart()
    {
        var tool = new HaMediaTool(_ha, Answer(null));

        Assert.Equal("media_player.select_source → BRAVIA XR-55A80L · source HDMI 3 (eARC/ARC)", await Invoke(tool, ("action", "source"), ("source", "hdmi 3")));
        Assert.Equal("""{"entity_id":"media_player.bravia_xr_55a80l","source":"HDMI 3 (eARC/ARC)"}""", ServiceCalls()[^1].Body);

        Assert.Equal("media_player.volume_set → BRAVIA XR-55A80L · volume 25%", await Invoke(tool, ("action", "volume"), ("volume_pct", 25)));
        Assert.Equal("""{"entity_id":"media_player.bravia_xr_55a80l","volume_level":0.25}""", ServiceCalls()[^1].Body);

        Assert.StartsWith("Error: 'hdmi' is not a valid source; use one of: STR-AN1000, HDMI 1", await Invoke(tool, ("action", "source"), ("source", "hdmi")));
        Assert.Equal(HaText.Missing("volume_pct"), await Invoke(tool, ("action", "volume")));
    }

    [Theory]
    [InlineData("HDMI 2", "HDMI 2")]
    [InlineData("hdmi 3", "HDMI 3 (eARC/ARC)")]
    [InlineData("earc", "HDMI 3 (eARC/ARC)")]
    [InlineData("str", "STR-AN1000")]
    [InlineData("hdmi", null)]
    [InlineData("netflix", null)]
    public void MatchSource_Same_Start_OrHolding(string wanted, string? source)
    {
        Assert.Equal(source, HaMediaTool.MatchSource(["STR-AN1000", "HDMI 1", "HDMI 2", "HDMI 3 (eARC/ARC)", "HDMI 4", "Video"], wanted));
    }

    [Fact]
    public async Task Todo_ListIsARead_AddAndCompleteAreCalls()
    {
        _stub.Requests.Clear();
        var stub = new StubHttpMessageHandler()
            .Map(Server + "/api/states", HttpStatusCode.OK, StatesJson)
            .Map(Server + "/api/template", HttpStatusCode.OK, AreasJson, "text/plain")
            .Map(Server + "/api/services/todo/get_items", HttpStatusCode.OK, """{"changed_states":[],"service_response":{"todo.shopping_list":{"items":[{"summary":"milk","status":"needs_action"}]}}}""")
            .Map(Server + "/api/services/", HttpStatusCode.OK, "[]");
        using var ha = new HaSession(() => _settings, (url, token) => new HaClient(url, token, new HttpClient(stub)), _time);
        _settings.HomeAssistantActionPolicy = HaPolicy.Off;
        var tool = new HaTodoTool(ha, Answer(null));

        Assert.Equal("Shopping list: 1 open item\n- milk", await Invoke(tool, ("action", "list")));
        Assert.Equal(HaText.PolicyOff, await Invoke(tool, ("action", "add"), ("item", "eggs")));

        _settings.HomeAssistantActionPolicy = HaPolicy.Ask;
        Assert.Equal("todo.update_item → Shopping list · 'milk'", await Invoke(tool, ("action", "complete"), ("item", "milk")));
        Assert.Equal("""{"entity_id":"todo.shopping_list","item":"milk","status":"completed"}""", stub.Requests[^1].Body);
        Assert.Equal(HaText.Missing("item"), await Invoke(tool, ("action", "remove")));
    }

    [Fact]
    public async Task CallService_OutsideTheSafeList_AsksFirst_NoOneToAskIsANo()
    {
        var data = JsonDocument.Parse("""{"command":"Home"}""").RootElement.Clone();

        Assert.Equal(HaText.NotAsked, await Invoke(new HaCallServiceTool(_ha, null), ("domain", "remote"), ("service", "send_command"), ("entity", "remote.bravia_xr_55a80l"), ("data", data)));
        Assert.Equal(HaText.NotAsked, await Invoke(new HaCallServiceTool(_ha, Answer(null)), ("domain", "remote"), ("service", "send_command"), ("entity", "remote.bravia_xr_55a80l"), ("data", data)));
        Assert.Equal(HaText.Declined, await Invoke(new HaCallServiceTool(_ha, Answer(false)), ("domain", "remote"), ("service", "send_command"), ("entity", "remote.bravia_xr_55a80l"), ("data", data)));
        Assert.Empty(ServiceCalls());

        Assert.Equal("remote.send_command → BRAVIA XR-55A80L", await Invoke(new HaCallServiceTool(_ha, Answer(true)), ("domain", "remote"), ("service", "send_command"), ("entity", "remote.bravia_xr_55a80l"), ("data", data)));
        Assert.Equal("""{"entity_id":"remote.bravia_xr_55a80l","command":"Home"}""", ServiceCalls().Single().Body);
        Assert.Equal("Let the model run remote.send_command on BRAVIA XR-55A80L with {\"command\":\"Home\"} in Home Assistant?", _asked[^1]);
    }

    [Fact]
    public async Task CallService_UnderAllow_RunsWithoutAsking_ADottedDomainIsSplit()
    {
        _settings.HomeAssistantActionPolicy = HaPolicy.Allow;

        Assert.Equal("button.press → BRAVIA XR-55A80L Restart", await Invoke(new HaCallServiceTool(_ha, Answer(false)), ("domain", "button.press"), ("entity", "BRAVIA XR-55A80L Restart")));
        Assert.Empty(_asked);
        Assert.Equal("/api/services/button/press", ServiceCalls().Single().Uri.AbsolutePath);
        Assert.Equal(HaText.BadData, await Invoke(new HaCallServiceTool(_ha, null), ("domain", "light"), ("service", "turn_on"), ("data", "[1]")));
    }

    [Fact]
    public async Task Assist_HandsTheSentenceOn_AndPolicyOffRefusesIt()
    {
        _stub.Map(Server + "/api/conversation/process", HttpStatusCode.OK, """{"response":{"response_type":"action_done","speech":{"plain":{"speech":"Turned off the lights"}}},"conversation_id":"x"}""");
        _settings.HomeAssistantAssistAgent = "conversation.google_ai";
        var tool = new HaAssistTool(_ha);

        Assert.Equal("Assist (action done): Turned off the lights", await Invoke(tool, ("text", "turn off the kitchen")));
        Assert.Equal("""{"text":"turn off the kitchen","agent_id":"conversation.google_ai"}""", _stub.Requests[^1].Body);

        _settings.HomeAssistantActionPolicy = HaPolicy.Off;
        Assert.Equal(HaText.PolicyOff, await Invoke(tool, ("text", "turn off the kitchen")));
        Assert.Equal("Assist (action done): Turned off the lights", await HaAssistTool.AskAsync(_ha, "turn off the kitchen", CancellationToken.None, judged: false));
    }

    [Fact]
    public async Task States_AnIdGivesEveryAttribute_ADomainNarrows()
    {
        var tool = new HaStatesTool(_ha);

        string one = await Invoke(tool, ("query", "media_player.bravia_xr_55a80l"));
        Assert.StartsWith("media_player.bravia_xr_55a80l · BRAVIA XR-55A80L · on", one);
        Assert.Contains("\nsource_list: [\"STR-AN1000\", \"HDMI 1\", \"HDMI 2\", \"HDMI 3 (eARC/ARC)\", \"HDMI 4\", \"Video\"]", one);

        string scenes = await Invoke(tool, ("domain", "scene"), ("area", "den"));
        Assert.Equal("2 entities matching domain scene, area Den\nscene.den_den_relax · Den Relax · unknown · in Den\nscene.den_den_read · Den Read · unknown · in Den", scenes);
    }

    [Fact]
    public async Task History_AsksForTheSpan_AndShowsTheChanges()
    {
        _stub.Map(Server + "/api/history/period/", HttpStatusCode.OK, """[[{"entity_id":"binary_sensor.sensor_1_pantry_motion","state":"on","last_changed":"2026-09-11T20:00:00+00:00"}]]""");
        var tool = new HaHistoryTool(_ha, ManualTimeProvider.DefaultZone);

        string text = await Invoke(tool, ("entity", "pantry motion"), ("hours", 2));

        Assert.Equal("Sensor 1 - Pantry Motion (binary_sensor.sensor_1_pantry_motion): 1 state in the last 2 hours\nFri 13:00 on", text);
        Assert.Equal(HaClient.HistoryPath("binary_sensor.sensor_1_pantry_motion", ManualTimeProvider.DefaultUtcNow.AddHours(-2), ManualTimeProvider.DefaultUtcNow), _stub.Requests[^1].Uri.PathAndQuery);
        Assert.StartsWith("Error: '0' is not a whole number from 1 to 336", await Invoke(tool, ("entity", "pantry motion"), ("hours", 0)));
    }

    [Fact]
    public async Task Overview_IsTheSnapshotsLines()
    {
        string text = await Invoke(new HaOverviewTool(_ha));

        Assert.StartsWith("Home Assistant: 3 of 8 lights on\n- Den: 2 of 3 on", text);
    }

    [Fact]
    public void EveryTool_IsNamed_ClassifiedForPlanMode_AndNotedOnTheTranscript()
    {
        var tools = ChatScreen.HomeAssistantTools(_ha, null);

        Assert.Equal(9, tools.Count);
        Assert.Equal(ChatScreen.HomeAssistantToolNames.Order(StringComparer.Ordinal), tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, t => Assert.True(PlanTools.ReadOnly.Contains(t.Name) ^ PlanTools.Mutating.Contains(t.Name), t.Name));
        Assert.All(tools.Where(t => t is HaOverviewTool or HaStatesTool or HaHistoryTool), t => Assert.Contains(t.Name, PlanTools.ReadOnly));
        Assert.All(tools, t => Assert.StartsWith("ha_", t.Name, StringComparison.Ordinal));
    }

    // ── /ha ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("den 40%", "den", 40)]
    [InlineData("den 40", "den", 40)]
    [InlineData("living room fan", "living room fan", null)]
    [InlineData("den 140", "den 140", null)]
    [InlineData("hdmi 2", "hdmi", 2)]
    public void SplitBrightness_ATrailingNumberIsTheLevel(string text, string target, int? level)
    {
        Assert.Equal((target, level), HaCommand.SplitBrightness(text));
    }

    [Fact]
    public async Task Command_OnWithABrightness_IsTheLightService_NeverJudged()
    {
        _settings.HomeAssistantActionPolicy = HaPolicy.Off;

        var result = await HaCommand.RunAsync(_ha, "on den 40%", CancellationToken.None);

        Assert.False(result.Failed);
        Assert.Equal(["light.turn_on → Den · 40%"], result.Lines);
        Assert.Equal("""{"entity_id":"light.den_den","brightness_pct":40}""", ServiceCalls().Single().Body);
    }

    [Theory]
    [InlineData("toggle kitchen prep 1", "light.toggle → Kitchen Prep 1", """{"entity_id":"light.kitchen_kitchen_prep_1"}""")]
    [InlineData("on Kitchen Prep 1", "light.turn_on → Kitchen Prep 1", """{"entity_id":"light.kitchen_kitchen_prep_1"}""")]
    [InlineData("on kitchen prep 1 40", "light.turn_on → Kitchen Prep 1 · 40%", """{"entity_id":"light.kitchen_kitchen_prep_1","brightness_pct":40}""")]
    [InlineData("on kitchen prep 1 40%", "light.turn_on → Kitchen Prep 1 · 40%", """{"entity_id":"light.kitchen_kitchen_prep_1","brightness_pct":40}""")]
    public async Task Command_ANameEndingInANumber_IsThatName_NotABrightness(string args, string line, string body)
    {
        // 2026-10-02 (the user's report): "/ha toggle Den Piano 1" read the 1 as a brightness and asked which Den Piano; the whole
        // text is tried first now, the split only when it names nothing.
        var result = await HaCommand.RunAsync(_ha, args, CancellationToken.None);

        Assert.False(result.Failed, string.Join("\n", result.Lines));
        Assert.Equal([line], result.Lines);
        Assert.Equal(body, ServiceCalls().Single().Body);
    }

    [Fact]
    public async Task Command_OffOnASwitch_IsTheGenericService()
    {
        var result = await HaCommand.RunAsync(_ha, "off pantry motion sensor enabled", CancellationToken.None);

        Assert.Equal(["homeassistant.turn_off → Sensor 1 - Pantry Motion sensor enabled"], result.Lines);
        Assert.Equal("/api/services/homeassistant/turn_off", ServiceCalls().Single().Uri.AbsolutePath);
    }

    [Fact]
    public async Task Command_Tv_Scene_Status_AndUsage()
    {
        Assert.Equal(["media_player.select_source → BRAVIA XR-55A80L · source HDMI 2"], (await HaCommand.RunAsync(_ha, "tv source hdmi 2", CancellationToken.None)).Lines);
        Assert.Equal(["media_player.volume_mute → BRAVIA XR-55A80L · muted"], (await HaCommand.RunAsync(_ha, "tv mute", CancellationToken.None)).Lines);
        Assert.Equal(["scene.turn_on → Indoor Night"], (await HaCommand.RunAsync(_ha, "scene indoor night", CancellationToken.None)).Lines);

        var status = await HaCommand.RunAsync(_ha, "", CancellationToken.None);
        Assert.Equal("Home Assistant 2026.9.4 at http://ha.lan:8123 · Home · America/Chicago", status.Lines[0]);
        Assert.Equal("Home Assistant: 3 of 8 lights on", status.Lines[1]);

        var usage = await HaCommand.RunAsync(_ha, "dance", CancellationToken.None);
        Assert.True(usage.Failed);
        Assert.Equal([HaText.Usage], usage.Lines);

        var states = await HaCommand.RunAsync(_ha, "states scene", CancellationToken.None);
        Assert.Equal("4 entities matching domain scene", states.Lines[0]);
    }

    [Fact]
    public async Task Command_NotConfigured_SaysSo()
    {
        _settings.HomeAssistantUrl = "";

        var result = await HaCommand.RunAsync(_ha, "on den", CancellationToken.None);

        Assert.True(result.Failed);
        Assert.Equal([HaText.NotConfigured], result.Lines);
    }

    [Fact]
    public void Complete_Verbs_Names_Scenes_AndSources()
    {
        var snapshot = Snapshot();

        Assert.Equal(["scene", "states", "say"], HaCommand.Complete(snapshot, "s").Select(i => i.Text));
        Assert.Contains("on Den", HaCommand.Complete(snapshot, "on d").Select(i => i.Text));
        Assert.Contains("on Den Corner Lamp", HaCommand.Complete(snapshot, "on d").Select(i => i.Text));
        Assert.Equal(["scene Den Relax", "scene Den Read"], HaCommand.Complete(snapshot, "scene den").Select(i => i.Text));
        Assert.Equal(["tv source HDMI 1", "tv source HDMI 2", "tv source HDMI 3 (eARC/ARC)", "tv source HDMI 4"], HaCommand.Complete(snapshot, "tv source h").Select(i => i.Text));
        Assert.Equal(["tv mute"], HaCommand.Complete(null, "tv m").Select(i => i.Text));
        Assert.Empty(HaCommand.Complete(null, "scene de"));
    }

    // ── the turn ────────────────────────────────────────────────────────────

    [Fact]
    public void PrepareTurn_OffersTheGroup_WithItsRule_OnlyWhileEnabled()
    {
        var assistant = new Assistant(new FakeChatClient(), new ConversationHistory(""), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
        var memory = new MemoryStore(_dir);
        string home = Path.Combine(_dir, "profile");
        var tools = ChatScreen.HomeAssistantTools(_ha, null);

        ChatScreen.PrepareTurn(assistant, memory, ChatScreen.MemoryTools(memory), [], new PersonaFile(home), new OperataFile(home), new VocaliaFile(home), memoryEnabled: false, speechOutput: false, homeTools: tools, homeEnabled: true);
        Assert.Contains(HaLightsTool.ToolName, assistant.Tools.Select(t => t.Name));
        Assert.Contains(Assistant.HomeAssistantRule, assistant.History.SystemPrompt);

        ChatScreen.PrepareTurn(assistant, memory, ChatScreen.MemoryTools(memory), [], new PersonaFile(home), new OperataFile(home), new VocaliaFile(home), memoryEnabled: false, speechOutput: false, homeTools: tools, homeEnabled: false);
        Assert.DoesNotContain(HaLightsTool.ToolName, assistant.Tools.Select(t => t.Name));
        Assert.DoesNotContain(Assistant.HomeAssistantRule, assistant.History.SystemPrompt);

        // Every tool switched off on /tools: the group is emptied, and its rule goes with it.
        ChatScreen.PrepareTurn(assistant, memory, ChatScreen.MemoryTools(memory), [], new PersonaFile(home), new OperataFile(home), new VocaliaFile(home), memoryEnabled: false, speechOutput: false, disabledTools: ChatScreen.HomeAssistantToolNames, homeTools: tools, homeEnabled: true);
        Assert.DoesNotContain(Assistant.HomeAssistantRule, assistant.History.SystemPrompt);
    }

    [Fact]
    public void Rule_NamesEveryTool()
    {
        Assert.All(ChatScreen.HomeAssistantToolNames, name => Assert.Contains(name, Assistant.HomeAssistantRule));
    }

    // ── live, read-only ─────────────────────────────────────────────────────

    [LiveHomeAssistantFact]
    public async Task Live_TheSnapshot_TheOverview_AndTheServerLine_ReadWithoutSwitchingAnything()
    {
        var live = new AppSettingsData { HomeAssistantUrl = LiveHomeAssistant.BaseUrl!.AbsoluteUri, HomeAssistantToken = LiveHomeAssistant.Token };
        using var ha = new HaSession(() => live);

        var (snapshot, error) = await ha.SnapshotAsync(CancellationToken.None);
        Assert.Null(error);
        Assert.NotEmpty(snapshot!.Entities);
        Assert.StartsWith("Home Assistant: ", HaText.Overview(snapshot)[0]);

        var (ok, text) = await ha.TestAsync(CancellationToken.None);
        Assert.True(ok, text);
        Assert.StartsWith("Home Assistant ", text);

        // Every entity resolves by its own id; nothing is called.
        Assert.All(snapshot.Entities.Take(20), e => Assert.Equal(e.Id, snapshot.Resolve(e.Id, []).Entities.Single().Id));
    }
}
