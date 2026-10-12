using System.Text.Json;

namespace NeonSidekick.HomeAssistant;

/// <summary>What a message from Home Assistant's WebSocket is (<see cref="HaProtocol.Classify"/>).</summary>
public enum HaMessageKind
{
    Other,
    AuthRequired,
    AuthOk,
    AuthInvalid,
    Result,
    Event,
    Pong,
}

/// <summary>
/// One service call (2026-10-12, the home bar's): the domain and service, what it acts on — entity ids, an area, or both —
/// and its data as a JSON object's text (null for none).
/// </summary>
public sealed record HaServiceCall(string Domain, string Service, IReadOnlyList<string> EntityIds, string? AreaId = null, string? Data = null)
{
    /// <summary>A call on one entity.</summary>
    public static HaServiceCall On(string domain, string service, string entityId, string? data = null) => new(domain, service, [entityId], null, data);

    /// <summary><c>domain.service</c>, for the log.</summary>
    public string Name => Domain + "." + Service;
}

/// <summary>
/// The WebSocket API's messages the home bar sends and reads (2026-10-12): the auth handshake, <c>subscribe_entities</c>
/// (the compressed state stream the frontend itself uses: <c>a</c> adds, <c>c</c> changes, <c>r</c> removes — read by
/// <see cref="HaEntityDiff"/>), <c>call_service</c>, the registries' lists and <c>ping</c>. Written with
/// <see cref="HaJson.Object"/>; read over <see cref="JsonElement"/>, no serializer. Pure; pinned.
/// </summary>
public static class HaProtocol
{
    /// <summary>The registry lists the picker and the area chips need, in the order they are asked.</summary>
    public const string FloorList = "config/floor_registry/list";

    public const string AreaList = "config/area_registry/list";

    public const string DeviceList = "config/device_registry/list";

    public const string EntityList = "config/entity_registry/list";

    public const string LabelList = "config/label_registry/list";

    public const string GetStates = "get_states";

    public const string Ping = "ping";

    /// <summary>The first message after <c>auth_required</c>: the token.</summary>
    public static string Auth(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return HaJson.Object(w =>
        {
            w.WriteString("type", "auth");
            w.WriteString("access_token", token);
        });
    }

    /// <summary>A command with no fields but its id and type: <c>get_states</c>, <c>ping</c>, a registry's list.</summary>
    public static string Command(int id, string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return HaJson.Object(w =>
        {
            w.WriteNumber("id", id);
            w.WriteString("type", type);
        });
    }

    /// <summary><c>subscribe_entities</c> for <paramref name="entityIds"/> (never empty: an empty list would mean every entity).</summary>
    public static string SubscribeEntities(int id, IEnumerable<string> entityIds)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        return HaJson.Object(w =>
        {
            w.WriteNumber("id", id);
            w.WriteString("type", "subscribe_entities");
            w.WriteStartArray("entity_ids");
            foreach (string entity in entityIds)
            {
                w.WriteStringValue(entity);
            }

            w.WriteEndArray();
        });
    }

    /// <summary><c>unsubscribe_events</c> for the subscription made by message <paramref name="subscription"/>.</summary>
    public static string Unsubscribe(int id, int subscription) => HaJson.Object(w =>
    {
        w.WriteNumber("id", id);
        w.WriteString("type", "unsubscribe_events");
        w.WriteNumber("subscription", subscription);
    });

    /// <summary>
    /// <c>call_service</c>: <c>target</c> holds the entity ids and the area, <c>service_data</c> every member of the call's
    /// data. Null with <paramref name="error"/> when the data is not a JSON object.
    /// </summary>
    public static string? CallService(int id, HaServiceCall call, out string? error)
    {
        ArgumentNullException.ThrowIfNull(call);
        error = null;
        JsonDocument? data = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(call.Data))
            {
                try
                {
                    data = JsonDocument.Parse(call.Data);
                }
                catch (JsonException)
                {
                    error = HaText.BadData;
                    return null;
                }

                if (data.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = HaText.BadData;
                    return null;
                }
            }

            var members = data?.RootElement;
            return HaJson.Object(w =>
            {
                w.WriteNumber("id", id);
                w.WriteString("type", "call_service");
                w.WriteString("domain", call.Domain);
                w.WriteString("service", call.Service);
                w.WriteStartObject("target");
                if (call.EntityIds.Count > 0)
                {
                    HaJson.WriteEntityIds(w, call.EntityIds);
                }

                if (!string.IsNullOrEmpty(call.AreaId))
                {
                    w.WriteString("area_id", call.AreaId);
                }

                w.WriteEndObject();
                if (members is { } m)
                {
                    w.WritePropertyName("service_data");
                    m.WriteTo(w);
                }
            });
        }
        finally
        {
            data?.Dispose();
        }
    }

    /// <summary>What <paramref name="message"/> is, by its <c>type</c>.</summary>
    public static HaMessageKind Classify(JsonElement message) => HaJson.String(message, "type") switch
    {
        "auth_required" => HaMessageKind.AuthRequired,
        "auth_ok" => HaMessageKind.AuthOk,
        "auth_invalid" => HaMessageKind.AuthInvalid,
        "result" => HaMessageKind.Result,
        "event" => HaMessageKind.Event,
        "pong" => HaMessageKind.Pong,
        _ => HaMessageKind.Other,
    };

    /// <summary>The message's <c>id</c>, or null.</summary>
    public static int? IdOf(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out int value) ? value : null;

    /// <summary>Whether a <c>result</c> says <c>success</c>.</summary>
    public static bool Succeeded(JsonElement result) => HaJson.Boolean(result, "success") == true;

    /// <summary>A failed <c>result</c>'s words: its error's message (and code), as an <c>Error:</c> sentence.</summary>
    public static string ErrorOf(JsonElement result, string what)
    {
        var error = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out var e) ? e : default;
        string message = HaJson.String(error, "message") ?? HaJson.String(error, "code") ?? "";
        return HomeBarText.LiveRefused(what, message);
    }

    /// <summary>The <c>auth_invalid</c>'s message, or "".</summary>
    public static string AuthMessage(JsonElement message) => HaJson.String(message, "message") ?? "";

    /// <summary>A Unix time in seconds (the compressed stream's <c>lc</c>/<c>lu</c>) as a time; null when it is no number.</summary>
    public static DateTimeOffset? EpochSeconds(JsonElement element, string name)
    {
        if (HaJson.Number(element, name) is not { } seconds || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return null;
        }

        long milliseconds = (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        return DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(milliseconds, DateTimeOffset.MinValue.ToUnixTimeMilliseconds(), DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()));
    }
}
