using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using NeonSidekick.HomeAssistant;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A scripted Home Assistant WebSocket API (2026-10-12, the home bar's feed): <see cref="Connect"/> is the <see cref="HaLive"/>
/// seam. It keeps entities (a state and an attributes object each), answers the sign-in (<see cref="Token"/>, or
/// <c>auth_invalid</c>), the five registry lists, <c>get_states</c>, <c>subscribe_entities</c> (an <c>a</c> event of the ids
/// asked for, then a <c>c</c> event for each change), <c>unsubscribe_events</c>, <c>ping</c> (unless <see cref="StallPong"/>),
/// and <c>call_service</c> — recorded in <see cref="Calls"/> and played on the entities as a real server would: on, off and
/// toggle for lights, switches and fans (an area target: its lights), lock and unlock, open and close for a cover, play and
/// pause for a player. <see cref="Drop"/> closes the connection from the server's side.
/// </summary>
public sealed class FakeHaServer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string State, string Attributes)> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _areaOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _deviceOf = new(StringComparer.Ordinal);
    private readonly List<(string Id, string Name, string? Area)> _devices = [];
    private readonly List<(string Id, string Name, string? Floor)> _areas = [];
    private readonly List<(string Id, string Name, int Level)> _floors = [];
    private readonly List<string> _received = [];
    private Wire? _wire;

    /// <summary>The token the sign-in accepts.</summary>
    public string Token { get; set; } = "secret";

    /// <summary>Every sign-in is refused.</summary>
    public bool RefuseAuth { get; set; }

    /// <summary>A connection attempt throws, as a closed port does.</summary>
    public bool Unreachable { get; set; }

    /// <summary>Pings are never answered.</summary>
    public bool StallPong { get; set; }

    /// <summary>Service calls are recorded and played, but never answered.</summary>
    public bool SilentCalls { get; set; }

    /// <summary>A call is answered and recorded but moves no state (2026-10-15: a mark that would clear on the state's change stays to be read).</summary>
    public bool HoldStates { get; set; }

    /// <summary><c>subscribe_entities</c> is refused, as a server too old for it would.</summary>
    public bool RefuseSubscribe { get; set; }

    /// <summary>How many connections were opened.</summary>
    public int Connections { get; private set; }

    /// <summary>The service calls, as <c>domain.service</c> with their target and data's raw JSON.</summary>
    public List<(string Service, string Target, string? Data)> Calls { get; } = [];

    /// <summary>Every message the client sent, in order.</summary>
    public IReadOnlyList<string> Received
    {
        get
        {
            lock (_gate)
            {
                return _received.ToList();
            }
        }
    }

    /// <summary>The entity ids the last <c>subscribe_entities</c> asked for (empty before any).</summary>
    public IReadOnlyList<string> Subscribed { get; private set; } = [];

    /// <summary>Whether a connection is open now.</summary>
    public bool Open
    {
        get
        {
            lock (_gate)
            {
                return _wire is { Closed: false };
            }
        }
    }

    /// <summary>
    /// Adds an entity: its state and its attributes as a JSON object's text; in <paramref name="area"/> when given, on <paramref name="device"/>
    /// when given (2026-10-16, a Hue zone's group light).
    /// </summary>
    public FakeHaServer Entity(string id, string state, string attributes = "{}", string? area = null, string? device = null)
    {
        lock (_gate)
        {
            _states[id] = (state, attributes);
            if (area is not null)
            {
                _areaOf[id] = area;
            }

            if (device is not null)
            {
                _deviceOf[id] = device;
            }
        }

        return this;
    }

    /// <summary>Adds a device (2026-10-16), in <paramref name="area"/> when given.</summary>
    public FakeHaServer Device(string id, string name, string? area = null)
    {
        lock (_gate)
        {
            _devices.Add((id, name, area));
        }

        return this;
    }

    /// <summary>Adds an area, on <paramref name="floor"/> when given.</summary>
    public FakeHaServer Area(string id, string name, string? floor = null)
    {
        lock (_gate)
        {
            _areas.Add((id, name, floor));
        }

        return this;
    }

    /// <summary>Adds a floor.</summary>
    public FakeHaServer Floor(string id, string name, int level)
    {
        lock (_gate)
        {
            _floors.Add((id, name, level));
        }

        return this;
    }

    /// <summary>An entity's state now.</summary>
    public string StateOf(string id)
    {
        lock (_gate)
        {
            return _states[id].State;
        }
    }

    /// <summary>The seam: opens a connection that starts with <c>auth_required</c>.</summary>
    public Task<IHaWire> Connect(Uri server, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Unreachable)
            {
                throw new IOException("connection refused");
            }

            Connections++;
            _wire = new Wire(this);
            _wire.Push("""{"type":"auth_required","ha_version":"2026.10.0"}""");
            return Task.FromResult<IHaWire>(_wire);
        }
    }

    /// <summary>Changes an entity as Home Assistant would, and tells a subscriber.</summary>
    public void Change(string id, string state, string? attributes = null)
    {
        lock (_gate)
        {
            var (_, kept) = _states[id];
            _states[id] = (state, attributes ?? kept);
            PushChange(id);
        }
    }

    /// <summary>Closes the connection from the server's side.</summary>
    public void Drop()
    {
        lock (_gate)
        {
            _wire?.Close();
        }
    }

    // Under _gate: a 'c' event for id to the open subscription.
    private void PushChange(string id)
    {
        if (_wire is not { Closed: false } wire || wire.Subscription is not { } sub || !wire.Ids.Contains(id))
        {
            return;
        }

        var (state, attributes) = _states[id];
        wire.Push("{\"id\":" + sub.ToString(CultureInfo.InvariantCulture) + ",\"type\":\"event\",\"event\":{\"c\":{\"" + id + "\":{\"+\":{\"s\":\"" + state + "\",\"a\":" + attributes + ",\"lc\":1760000000.5}}}}}");
    }

    private void Handle(Wire wire, string text)
    {
        lock (_gate)
        {
            _received.Add(text);
            using var message = JsonDocument.Parse(text);
            var root = message.RootElement;
            string type = root.GetProperty("type").GetString()!;
            int id = root.TryGetProperty("id", out var idElement) ? idElement.GetInt32() : 0;
            switch (type)
            {
                case "auth":
                    wire.Push(!RefuseAuth && root.GetProperty("access_token").GetString() == Token
                        ? """{"type":"auth_ok","ha_version":"2026.10.0"}"""
                        : """{"type":"auth_invalid","message":"Invalid access token or password"}""");
                    break;
                case "ping":
                    if (!StallPong)
                    {
                        wire.Push("{\"id\":" + Num(id) + ",\"type\":\"pong\"}");
                    }

                    break;
                case "subscribe_entities" when RefuseSubscribe:
                    wire.Push("{\"id\":" + Num(id) + ",\"type\":\"result\",\"success\":false,\"error\":{\"code\":\"unknown_command\",\"message\":\"Unknown command.\"}}");
                    break;
                case "subscribe_entities":
                    var ids = root.GetProperty("entity_ids").EnumerateArray().Select(e => e.GetString()!).ToList();
                    wire.Subscription = id;
                    wire.Ids = ids.ToHashSet(StringComparer.Ordinal);
                    Subscribed = ids;
                    Result(wire, id, "null");
                    var added = ids.Where(_states.ContainsKey).Select(e => "\"" + e + "\":{\"s\":\"" + _states[e].State + "\",\"a\":" + _states[e].Attributes + ",\"lu\":1760000000.25}");
                    wire.Push("{\"id\":" + Num(id) + ",\"type\":\"event\",\"event\":{\"a\":{" + string.Join(",", added) + "}}}");
                    break;
                case "unsubscribe_events":
                    if (wire.Subscription == root.GetProperty("subscription").GetInt32())
                    {
                        wire.Subscription = null;
                    }

                    Result(wire, id, "null");
                    break;
                case "get_states":
                    Result(wire, id, "[" + string.Join(",", _states.Select(s => "{\"entity_id\":\"" + s.Key + "\",\"state\":\"" + s.Value.State + "\",\"attributes\":" + s.Value.Attributes + "}")) + "]");
                    break;
                case "config/floor_registry/list":
                    Result(wire, id, "[" + string.Join(",", _floors.Select(f => "{\"floor_id\":\"" + f.Id + "\",\"name\":\"" + f.Name + "\",\"level\":" + Num(f.Level) + "}")) + "]");
                    break;
                case "config/area_registry/list":
                    Result(wire, id, "[" + string.Join(",", _areas.Select(a => "{\"area_id\":\"" + a.Id + "\",\"name\":\"" + a.Name + "\",\"floor_id\":" + (a.Floor is null ? "null" : "\"" + a.Floor + "\"") + "}")) + "]");
                    break;
                case "config/device_registry/list":
                    Result(wire, id, "[" + string.Join(",", _devices.Select(d => "{\"id\":\"" + d.Id + "\",\"name\":\"" + d.Name + "\",\"name_by_user\":null,\"area_id\":" + (d.Area is null ? "null" : "\"" + d.Area + "\"") + "}")) + "]");
                    break;
                case "config/entity_registry/list":
                    Result(wire, id, "[" + string.Join(",", _states.Keys.Select(e => "{\"entity_id\":\"" + e + "\",\"area_id\":" + (_areaOf.TryGetValue(e, out var a) ? "\"" + a + "\"" : "null") + ",\"device_id\":" + (_deviceOf.TryGetValue(e, out var dev) ? "\"" + dev + "\"" : "null") + ",\"disabled_by\":null,\"hidden_by\":null,\"entity_category\":null,\"labels\":[]}")) + "]");
                    break;
                case "config/label_registry/list":
                    wire.Push("{\"id\":" + Num(id) + ",\"type\":\"result\",\"success\":false,\"error\":{\"code\":\"unknown_command\",\"message\":\"Unknown command.\"}}");
                    break;
                case "call_service":
                    string domain = root.GetProperty("domain").GetString()!;
                    string service = root.GetProperty("service").GetString()!;
                    var target = root.GetProperty("target");
                    Calls.Add((domain + "." + service, target.GetRawText(), root.TryGetProperty("service_data", out var data) ? data.GetRawText() : null));
                    if (!SilentCalls)
                    {
                        Result(wire, id, "{\"context\":{\"id\":\"x\"}}");
                    }

                    foreach (string entity in HoldStates ? [] : Targets(target))
                    {
                        Play(entity, service);
                    }

                    break;
                default:
                    wire.Push("{\"id\":" + Num(id) + ",\"type\":\"result\",\"success\":false,\"error\":{\"code\":\"unknown_command\",\"message\":\"Unknown command.\"}}");
                    break;
            }
        }
    }

    // Under _gate: the entities a call's target names — its ids, and an area's lights.
    private IEnumerable<string> Targets(JsonElement target)
    {
        if (target.TryGetProperty("entity_id", out var ids))
        {
            foreach (string id in ids.ValueKind == JsonValueKind.Array ? ids.EnumerateArray().Select(e => e.GetString()!) : [ids.GetString()!])
            {
                yield return id;
            }
        }

        if (target.TryGetProperty("area_id", out var area))
        {
            foreach (string id in _areaOf.Where(p => p.Value == area.GetString() && p.Key.StartsWith("light.", StringComparison.Ordinal)).Select(p => p.Key).ToList())
            {
                yield return id;
            }
        }
    }

    // Under _gate: a service's effect on one entity, told to the subscriber.
    private void Play(string id, string service)
    {
        if (!_states.TryGetValue(id, out var kept))
        {
            return;
        }

        string? next = service switch
        {
            "turn_on" => "on",
            "turn_off" => "off",
            "toggle" => kept.State == "on" ? "off" : "on",
            "lock" => "locked",
            "unlock" => "unlocked",
            "open_cover" => "open",
            "close_cover" => "closed",
            "media_play_pause" => kept.State == "playing" ? "paused" : "playing",
            _ => null,
        };
        if (next is not null && next != kept.State)
        {
            _states[id] = (next, kept.Attributes);
            PushChange(id);
        }
    }

    private static void Result(Wire wire, int id, string result) =>
        wire.Push("{\"id\":" + Num(id) + ",\"type\":\"result\",\"success\":true,\"result\":" + result + "}");

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class Wire(FakeHaServer server) : IHaWire
    {
        private readonly Channel<string?> _out = Channel.CreateUnbounded<string?>();

        public int? Subscription { get; set; }

        public HashSet<string> Ids { get; set; } = new(StringComparer.Ordinal);

        public bool Closed { get; private set; }

        public void Push(string text) => _out.Writer.TryWrite(text);

        public void Close()
        {
            Closed = true;
            _out.Writer.TryWrite(null);
        }

        public Task SendAsync(string text, CancellationToken cancellationToken)
        {
            if (Closed)
            {
                throw new IOException("the connection is closed");
            }

            server.Handle(this, text);
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (Closed && !_out.Reader.TryPeek(out _))
            {
                return null;
            }

            return await _out.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            Closed = true;
            _out.Writer.TryWrite(null);
            return ValueTask.CompletedTask;
        }
    }
}
