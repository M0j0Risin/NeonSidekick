using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NeonSidekick.HomeAssistant;

/// <summary>
/// The Home Assistant words (2026-09-28): the tools' results and failures, the overview and state lines, the history,
/// the confirm question, the transcript's note and the <c>/ha</c> lines. Every failure starts <c>Error:</c>; a result's
/// first line is its header, which is the transcript's note (<see cref="Note"/>). Pure; pinned by tests.
/// </summary>
public static class HaText
{
    /// <summary>The log category.</summary>
    public const string Category = "HomeAssistant";

    // ─── failures ───────────────────────────────────────────────────────────────

    /// <summary>No URL or no readable token. Pinned.</summary>
    public const string NotConfigured = "Error: Home Assistant is not set up; set Home Assistant URL and Home Assistant API key on the Home Assistant tab of /tools";

    /// <summary>A 401: the token was refused. Pinned.</summary>
    public const string Unauthorized = "Error: Home Assistant refused the access token (401); make a new long-lived access token in your Home Assistant profile (Security) and set it on the Home Assistant tab of /tools";

    /// <summary>A target that is empty once the filler is gone. Pinned.</summary>
    public const string NoTarget = "Error: say which device, room or scene (a name from ha_states, or an entity id)";

    /// <summary><c>data</c> that is not a JSON object. Pinned.</summary>
    public const string BadData = "Error: \"data\" must be a JSON object, such as {\"brightness_pct\": 40}";

    /// <summary>The policy is <c>off</c>. Pinned.</summary>
    public const string PolicyOff = "Error: Home Assistant action policy is off, so the model may only read states; the user can switch things with /ha";

    /// <summary>The user said no on the pane. Pinned.</summary>
    public const string Declined = "Error: the user declined this Home Assistant action; do not retry it unless they ask";

    /// <summary>The call needed the user's yes and nothing could ask (headless, no pane). Pinned.</summary>
    public const string NotAsked = "Error: this Home Assistant action needs the user's yes and nobody could be asked; the user can run it with /ha, or allow it with Home Assistant action policy";

    /// <summary>No answer in time.</summary>
    public static string NoAnswer(string timeout) => $"Error: Home Assistant did not answer within {timeout}";

    /// <summary>The server could not be reached.</summary>
    public static string Unreachable(string baseUrl, string detail) => $"Error: cannot reach Home Assistant at {baseUrl}: {detail.Trim()}";

    /// <summary>A non-2xx answer other than a 401: the status, the endpoint and the server's own words, cut short.</summary>
    public static string HttpError(int status, string endpoint, string body)
    {
        string said = Clip(Single(ServerMessage(body)), 300);
        return $"Error: Home Assistant answered {status.ToString(CultureInfo.InvariantCulture)} to {endpoint}" + (said.Length > 0 ? ": " + said : "");
    }

    /// <summary>An answer that could not be read.</summary>
    public static string BadAnswer(string endpoint) => $"Error: Home Assistant's answer to {endpoint} could not be read";

    /// <summary>A target that matched nothing, with what is near.</summary>
    public static string NotFound(string target, IReadOnlyList<string> domains, IReadOnlyList<HaEntity> near)
    {
        ArgumentNullException.ThrowIfNull(target);
        string among = domains.Count == 0 ? "" : " among " + string.Join(", ", domains);
        string tail = near.Count == 0 ? "" : "; some there are: " + string.Join(", ", near.Select(Label));
        return $"Error: nothing named '{target.Trim()}'{among}" + tail;
    }

    /// <summary>A target that matched several with nothing to choose between them.</summary>
    public static string Ambiguous(string target, IReadOnlyList<HaEntity> shown, int total)
    {
        ArgumentNullException.ThrowIfNull(shown);
        string more = total > shown.Count ? $" (and {(total - shown.Count).ToString(CultureInfo.InvariantCulture)} more)" : "";
        return $"Error: '{target.Trim()}' could be {total.ToString(CultureInfo.InvariantCulture)} things; name one: " + string.Join(", ", shown.Select(Label)) + more;
    }

    /// <summary>An argument outside what the tool takes: <paramref name="choices"/> names what it does.</summary>
    public static string BadChoice(string argument, string given, IEnumerable<string> choices) =>
        $"Error: '{given.Trim()}' is not a valid {argument}; use one of: " + string.Join(", ", choices);

    /// <summary>A number argument out of its range.</summary>
    public static string OutOfRange(string argument, string given, int min, int max) =>
        $"Error: '{given.Trim()}' is not a whole number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)} for '{argument}'";

    /// <summary>A required argument left out.</summary>
    public static string Missing(string argument) => $"Error: give \"{argument}\"";

    /// <summary>No entity of a domain the tool needs (no media player, no to-do list).</summary>
    public static string NoneOf(string what) => $"Error: Home Assistant has no {what}";

    // ─── the confirm pane ───────────────────────────────────────────────────────

    /// <summary>The question on the pane before an asked call: the service and what it acts on. Pinned.</summary>
    public static string ConfirmQuestion(string domain, string service, string target, string? data)
    {
        string on = target.Length > 0 ? " on " + target : "";
        string with = string.IsNullOrWhiteSpace(data) ? "" : " with " + Clip(Single(data), 120);
        return $"Let the model run {domain}.{service}{on}{with} in Home Assistant?";
    }

    // ─── results ────────────────────────────────────────────────────────────────

    /// <summary>The transcript's one-line note for a result: its first line.</summary>
    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int newline = result.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? result : result[..newline];
    }

    /// <summary>A service call done: <c>light.turn_off → Den, Kitchen</c>, with what was set.</summary>
    public static string Done(string domain, string service, IReadOnlyList<HaEntity> targets, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        string on = targets.Count == 0 ? "" : " → " + Names(targets);
        string with = string.IsNullOrWhiteSpace(detail) ? "" : " · " + detail.Trim();
        return $"{domain}.{service}{on}{with}";
    }

    /// <summary>A light call's settings in words: <c>40% · 2700 K · red · over 2 s</c>; empty for none.</summary>
    public static string LightDetail(int? brightness, string? color, int? kelvin, double? transition)
    {
        var parts = new List<string>(4);
        if (brightness is { } b)
        {
            parts.Add(b.ToString(CultureInfo.InvariantCulture) + "%");
        }

        if (kelvin is { } k)
        {
            parts.Add(k.ToString(CultureInfo.InvariantCulture) + " K");
        }

        if (!string.IsNullOrWhiteSpace(color))
        {
            parts.Add(color.Trim());
        }

        if (transition is { } t)
        {
            parts.Add("over " + t.ToString("0.#", CultureInfo.InvariantCulture) + " s");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The names of entities, joined; a long list cut with a count.</summary>
    public static string Names(IReadOnlyList<HaEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        const int shown = 6;
        string head = string.Join(", ", entities.Take(shown).Select(e => e.Name));
        return entities.Count > shown ? head + $" and {(entities.Count - shown).ToString(CultureInfo.InvariantCulture)} more" : head;
    }

    /// <summary>An entity as a candidate: <c>Den (light.den_den)</c>.</summary>
    public static string Label(HaEntity entity) => $"{entity.Name} ({entity.Id})";

    /// <summary>A service's own answer (<c>?return_response</c>) or the changed states, for the generic tool: the header and the body cut short.</summary>
    public static string ServiceAnswer(string header, string body)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(body);
        string text = body.Trim();
        if (text.Length == 0 || text == "[]")
        {
            return header;
        }

        return header + "\n" + Clip(text, 2000);
    }

    /// <summary>Assist's answer: its kind and what it said.</summary>
    public static string AssistAnswer(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            using var document = JsonDocument.Parse(body);
            var response = document.RootElement.TryGetProperty("response", out var r) ? r : default;
            string kind = HaJson.String(response, "response_type") ?? "";
            string speech = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("speech", out var s)
                && s.TryGetProperty("plain", out var plain) ? HaJson.String(plain, "speech") ?? "" : "";
            string said = speech.Trim().Length > 0 ? speech.Trim() : "(no words)";
            return kind == "error" ? "Error: Assist could not do it: " + said : "Assist (" + (kind.Length > 0 ? kind.Replace('_', ' ') : "answer") + "): " + said;
        }
        catch (JsonException)
        {
            return BadAnswer("/api/conversation/process");
        }
    }

    /// <summary>The log line of a service call: the service and the ids.</summary>
    public static string CallLog(string domain, string service, IReadOnlyList<string> ids) =>
        $"{domain}.{service} on {(ids.Count == 0 ? "(no entity)" : string.Join(", ", ids))}";

    // ─── states ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// One entity on one line: <c>light.den_den · Den · on · 80% · 2700 K · group · Den</c> — the id, the name, the state and
    /// what matters for its domain, then the area.
    /// </summary>
    public static string StateLine(HaEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var parts = new List<string> { entity.Id, entity.Name, StateOf(entity) };
        parts.AddRange(Details(entity));
        if (entity.Area is { } area)
        {
            parts.Add("in " + area);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The state with its unit, when it has one: <c>75.0 °F</c>.</summary>
    public static string StateOf(HaEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        string unit = HaJson.String(entity.Attributes, "unit_of_measurement") ?? "";
        return unit.Length > 0 && !entity.Unavailable ? entity.State + (unit == "%" ? "%" : " " + unit) : entity.State;
    }

    /// <summary>The attributes worth a word, by domain.</summary>
    public static IEnumerable<string> Details(HaEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var a = entity.Attributes;
        switch (entity.Domain)
        {
            case "light":
                if (Percent(HaJson.Number(a, "brightness")) is { } brightness && entity.State == "on")
                {
                    yield return brightness;
                }

                if (HaJson.Number(a, "color_temp_kelvin") is { } kelvin && entity.State == "on" && HaJson.String(a, "color_mode") == "color_temp")
                {
                    yield return ((int)kelvin).ToString(CultureInfo.InvariantCulture) + " K";
                }

                if (entity.IsGroup)
                {
                    yield return "group";
                }

                break;
            case "media_player":
                if (HaJson.String(a, "source") is { Length: > 0 } source)
                {
                    yield return "source " + source;
                }

                if (HaJson.Number(a, "volume_level") is { } volume)
                {
                    yield return "volume " + Math.Round(volume * 100).ToString(CultureInfo.InvariantCulture) + "%";
                }

                if (HaJson.Boolean(a, "is_volume_muted") == true)
                {
                    yield return "muted";
                }

                if (HaJson.String(a, "media_title") is { Length: > 0 } title)
                {
                    yield return "playing " + title;
                }

                break;
            case "binary_sensor":
            case "sensor":
                if (HaJson.String(a, "device_class") is { Length: > 0 } kind)
                {
                    yield return kind;
                }

                break;
            case "todo":
                yield return "items";
                break;
        }
    }

    /// <summary>
    /// Every attribute of one entity, a line each, lists cut short — what <c>ha_states</c> gives for a single id. The
    /// source list and the effects come whole enough to choose from.
    /// </summary>
    public static string Full(HaEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var sb = new StringBuilder();
        sb.Append(StateLine(entity));
        if (entity.LastChanged is { } changed)
        {
            sb.Append("\nlast changed: ").Append(changed.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        }

        if (entity.Attributes.ValueKind == JsonValueKind.Object)
        {
            foreach (var attribute in entity.Attributes.EnumerateObject())
            {
                sb.Append('\n').Append(attribute.Name).Append(": ").Append(Clip(Single(attribute.Value.ValueKind == JsonValueKind.String ? attribute.Value.GetString() ?? "" : attribute.Value.GetRawText()), 400));
            }
        }

        return sb.ToString();
    }

    /// <summary>The <c>ha_states</c> list: a header with the count and the filter, then a line per entity.</summary>
    public static string States(IReadOnlyList<HaEntity> entities, int total, string filter)
    {
        ArgumentNullException.ThrowIfNull(entities);
        string what = filter.Length > 0 ? " matching " + filter : "";
        string shown = total > entities.Count ? $" (first {entities.Count.ToString(CultureInfo.InvariantCulture)}; narrow with query, domain or area)" : "";
        var sb = new StringBuilder(Count(total, "entity", "entities") + what + shown);
        foreach (var entity in entities)
        {
            sb.Append('\n').Append(StateLine(entity));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The overview (<c>ha_overview</c> and <c>/ha</c>): the lights on by area with each room's group, the media players,
    /// the temperatures, the motion, the low batteries, the to-do lists, how many scenes and what is unavailable.
    /// </summary>
    public static IReadOnlyList<string> Overview(HaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var lines = new List<string>();
        var lights = snapshot.Of("light").Where(e => !e.IsGroup).ToList();
        var on = lights.Where(e => e.State == "on").ToList();
        lines.Add($"Home Assistant: {on.Count.ToString(CultureInfo.InvariantCulture)} of {Count(lights.Count(e => !e.Unavailable), "light")} on");

        var groups = snapshot.Of("light").Where(e => e.IsGroup).ToList();
        foreach (var area in snapshot.Areas)
        {
            var inside = lights.Where(e => area.EntityIds.Contains(e.Id, StringComparer.Ordinal) && !e.Unavailable).ToList();
            if (inside.Count == 0)
            {
                continue;
            }

            int lit = inside.Count(e => e.State == "on");
            var group = groups.FirstOrDefault(g => area.EntityIds.Contains(g.Id, StringComparer.Ordinal));
            string state = lit == 0 ? "off" : $"{lit.ToString(CultureInfo.InvariantCulture)} of {inside.Count.ToString(CultureInfo.InvariantCulture)} on";
            string brightest = lit == 0 ? "" : inside.Where(e => e.State == "on").Select(e => HaJson.Number(e.Attributes, "brightness")).Max() is { } top ? " up to " + Percent(top) : "";
            lines.Add($"- {area.Name}: {state}{brightest}" + (group is null ? "" : $" · group {group.Id}"));
        }

        var loose = on.Where(e => e.Area is null).ToList();
        if (loose.Count > 0)
        {
            lines.Add("- no area: " + Names(loose) + " on");
        }

        var wide = groups.Where(g => g.Area is null).ToList();
        if (wide.Count > 0)
        {
            lines.Add("Light groups: " + string.Join(", ", wide.Select(g => $"{g.Name} ({g.Id}, {g.State})")));
        }

        foreach (var player in snapshot.Of("media_player"))
        {
            lines.Add("Media: " + StateLine(player));
        }

        var temperatures = snapshot.Of("sensor").Where(e => HaJson.String(e.Attributes, "device_class") == "temperature" && !e.Unavailable).ToList();
        if (temperatures.Count > 0)
        {
            lines.Add("Temperature: " + string.Join("; ", temperatures.Select(e => $"{e.Name} {StateOf(e)}")));
        }

        var motion = snapshot.Of("binary_sensor").Where(e => HaJson.String(e.Attributes, "device_class") is "motion" or "occupancy" && !e.Unavailable).ToList();
        if (motion.Count > 0)
        {
            lines.Add("Motion: " + string.Join("; ", motion.Select(e => $"{e.Name} {(e.State == "on" ? "detected" : "clear")}")));
        }

        var low = snapshot.Of("sensor").Where(e => HaJson.String(e.Attributes, "device_class") == "battery"
            && double.TryParse(e.State, NumberStyles.Float, CultureInfo.InvariantCulture, out double level) && level < 20).ToList();
        if (low.Count > 0)
        {
            lines.Add("Low batteries: " + string.Join("; ", low.Select(e => $"{e.Name} {StateOf(e)}")));
        }

        foreach (var list in snapshot.Of("todo"))
        {
            lines.Add($"To-do: {list.Name} ({list.Id}) {list.State} open");
        }

        int scenes = snapshot.Of("scene").Count;
        if (scenes > 0)
        {
            lines.Add($"Scenes: {scenes.ToString(CultureInfo.InvariantCulture)} (ha_states with domain scene lists them)");
        }

        var gone = lights.Where(e => e.Unavailable).ToList();
        if (gone.Count > 0)
        {
            lines.Add("Unavailable lights: " + Names(gone));
        }

        return lines;
    }

    /// <summary><c>/ha</c>'s status head: the server, its version and place, from <c>/api/config</c>.</summary>
    public static string Server(string baseUrl, string configJson)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        try
        {
            using var document = JsonDocument.Parse(configJson);
            var root = document.RootElement;
            string version = HaJson.String(root, "version") ?? "?";
            string place = HaJson.String(root, "location_name") ?? "";
            string zone = HaJson.String(root, "time_zone") ?? "";
            return $"Home Assistant {version} at {baseUrl}" + (place.Length > 0 ? " · " + place : "") + (zone.Length > 0 ? " · " + zone : "");
        }
        catch (JsonException)
        {
            return $"Home Assistant at {baseUrl}";
        }
    }

    /// <summary>A to-do list's items from <c>todo.get_items</c>' answer: open ones first, a completed one marked.</summary>
    public static string TodoItems(HaEntity list, string body)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var answer = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("service_response", out var sr) ? sr : root;
            if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty(list.Id, out var entry) || !entry.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return BadAnswer("todo.get_items");
            }

            var all = items.EnumerateArray().Select(i => (Summary: HaJson.String(i, "summary") ?? "", Done: HaJson.String(i, "status") == "completed")).ToList();
            var sb = new StringBuilder($"{list.Name}: {Count(all.Count(i => !i.Done), "open item")}" + (all.Any(i => i.Done) ? $", {all.Count(i => i.Done).ToString(CultureInfo.InvariantCulture)} completed" : ""));
            foreach (var (summary, done) in all.OrderBy(i => i.Done))
            {
                sb.Append("\n- ").Append(summary).Append(done ? " (completed)" : "");
            }

            return sb.ToString();
        }
        catch (JsonException)
        {
            return BadAnswer("todo.get_items");
        }
    }

    /// <summary>
    /// An entity's history: the header with the span and the count, then <c>time state</c> a line each, oldest first, in
    /// <paramref name="zone"/>'s time; a long run keeps its newest <paramref name="max"/>.
    /// </summary>
    public static string History(HaEntity entity, string body, int hours, TimeZoneInfo zone, int max = 60)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(zone);
        try
        {
            using var document = JsonDocument.Parse(body);
            var changes = new List<(DateTimeOffset When, string State)>();
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var series in document.RootElement.EnumerateArray())
                {
                    if (series.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var point in series.EnumerateArray())
                    {
                        if ((HaJson.Time(point, "last_changed") ?? HaJson.Time(point, "last_updated")) is { } when && HaJson.String(point, "state") is { } state)
                        {
                            changes.Add((when, state));
                        }
                    }
                }
            }

            string unit = HaJson.String(entity.Attributes, "unit_of_measurement") is { Length: > 0 } u ? (u == "%" ? "%" : " " + u) : "";
            var sb = new StringBuilder($"{entity.Name} ({entity.Id}): {Count(changes.Count, "state")} in the last {Count(hours, "hour")}");
            var kept = changes.OrderBy(c => c.When).ToList();
            if (kept.Count > max)
            {
                sb.Append($" (the newest {max.ToString(CultureInfo.InvariantCulture)} shown)");
                kept = kept.Skip(kept.Count - max).ToList();
            }

            foreach (var (when, state) in kept)
            {
                var local = TimeZoneInfo.ConvertTime(when, zone);
                sb.Append('\n').Append(local.ToString("ddd HH:mm", CultureInfo.InvariantCulture)).Append(' ').Append(state).Append(state is "unavailable" or "unknown" ? "" : unit);
            }

            return sb.ToString();
        }
        catch (JsonException)
        {
            return BadAnswer("/api/history/period");
        }
    }

    // ─── /ha ────────────────────────────────────────────────────────────────────

    /// <summary>The spinner's label while <c>/ha</c> waits on the server. Pinned.</summary>
    public const string Working = "Asking Home Assistant";

    /// <summary><c>/ha</c>'s usage line. Pinned.</summary>
    public const string Usage =
        "usage: /ha · /ha on|off|toggle <name> [brightness%] · /ha scene <name> · /ha tv on|off|mute|unmute|up|down|vol <0-100>|source <name> · /ha states [filter] · /ha say <sentence>";

    /// <summary>The <c>/ha</c> verbs, in the completion's order.</summary>
    public static readonly string[] Verbs = ["on", "off", "toggle", "scene", "tv", "states", "say"];

    /// <summary>The <c>/ha tv</c> actions, in the completion's order.</summary>
    public static readonly string[] TvActions = ["on", "off", "mute", "unmute", "up", "down", "vol", "source"];

    // ─── shared ─────────────────────────────────────────────────────────────────

    /// <summary><paramref name="n"/> and the noun, plural past one.</summary>
    public static string Count(int n, string singular, string? plural = null) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : plural ?? singular + "s");

    /// <summary>A 0–255 brightness as a percentage, or null.</summary>
    public static string? Percent(double? brightness) =>
        brightness is { } b ? Math.Round(b / 255 * 100).ToString(CultureInfo.InvariantCulture) + "%" : null;

    /// <summary>The <c>message</c> of a JSON error body, else the body.</summary>
    private static string ServerMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return HaJson.String(document.RootElement, "message") ?? body;
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static string Single(string text) => string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
