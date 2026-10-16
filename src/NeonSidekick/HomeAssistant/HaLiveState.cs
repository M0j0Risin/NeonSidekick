using System.Text.Json;

namespace NeonSidekick.HomeAssistant;

/// <summary>
/// One entity as the live stream keeps it (2026-10-12): the id, the state, the attributes (each value cloned out of its
/// message, so the documents can go), and when the state last changed. <see cref="HaEntity"/>'s cousin; the REST snapshot's
/// attributes are one element, these are merged member by member as changes arrive.
/// </summary>
public sealed record HaLiveEntity(string Id, string State, IReadOnlyDictionary<string, JsonElement> Attributes, DateTimeOffset? LastChanged)
{
    /// <summary>The part before the dot.</summary>
    public string Domain => Id.IndexOf('.', StringComparison.Ordinal) is int dot and > 0 ? Id[..dot] : Id;

    /// <summary>The friendly name, else the id.</summary>
    public string Name => Text("friendly_name") is { Length: > 0 } name ? name.Trim() : Id;

    /// <summary>Whether the state says nothing is there: <c>unavailable</c> or <c>unknown</c>.</summary>
    public bool Unavailable => State is "unavailable" or "unknown";

    /// <summary>A string attribute, or null.</summary>
    public string? Text(string name) => Attributes.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A number attribute, or null.</summary>
    public double? Number(string name) => Attributes.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) ? number : null;

    /// <summary>A boolean attribute, or null.</summary>
    public bool? Flag(string name) => Attributes.TryGetValue(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>An array attribute of numbers (<c>rgb_color</c>, <c>hs_color</c>), or null when absent or another shape.</summary>
    public double[]? Numbers(string name)
    {
        if (!Attributes.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var numbers = new List<double>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double n))
            {
                return null;
            }

            numbers.Add(n);
        }

        return numbers.ToArray();
    }

    /// <summary>
    /// The live shape of a REST snapshot's <paramref name="entity"/> (2026-10-12): the home bar's picker has the catalog's states,
    /// not the feed's, and its glyphs read the same attributes the chips do.
    /// </summary>
    public static HaLiveEntity From(HaEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (entity.Attributes.ValueKind == JsonValueKind.Object)
        {
            foreach (var attribute in entity.Attributes.EnumerateObject())
            {
                attributes[attribute.Name] = attribute.Value.Clone();
            }
        }

        return new HaLiveEntity(entity.Id, entity.State, attributes, entity.LastChanged);
    }

    /// <summary>Whether it is a light group (a Hue room or zone, or any group listing its members).</summary>
    public bool IsGroup => Flag("is_hue_group") == true || (Attributes.TryGetValue("entity_id", out var members) && members.ValueKind == JsonValueKind.Array);

    /// <summary>A group's members as its <c>entity_id</c> attribute lists them (2026-10-16, the Web UI's zones), in its order; empty for none.</summary>
    public IReadOnlyList<string> GroupMembers =>
        Attributes.TryGetValue("entity_id", out var members) && members.ValueKind == JsonValueKind.Array
            ? members.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
}

/// <summary>
/// The compressed state stream's messages applied to the entities kept (2026-10-12): <c>a</c> adds (or replaces) whole
/// entities — <c>s</c> the state, <c>a</c> the attributes, <c>lc</c> the last change (when absent, <c>lu</c>, the last
/// update, which then equals it); <c>c</c> changes them — its <c>+</c> sets the state, merges attributes and moves the times,
/// its <c>-</c> lists attributes gone; <c>r</c> removes ids. A change for an entity not kept is skipped (its <c>a</c> comes
/// first). Pure: the dictionary is the caller's, changed in place.
/// </summary>
public static class HaEntityDiff
{
    /// <summary>Applies one event (the <c>event</c> member of a <c>subscribe_entities</c> message); whether anything changed.</summary>
    public static bool Apply(Dictionary<string, HaLiveEntity> entities, JsonElement change)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (change.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        bool changed = false;
        if (change.TryGetProperty("a", out var added) && added.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in added.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (item.Value.TryGetProperty("a", out var a) && a.ValueKind == JsonValueKind.Object)
                {
                    foreach (var attribute in a.EnumerateObject())
                    {
                        attributes[attribute.Name] = attribute.Value.Clone();
                    }
                }

                var time = HaProtocol.EpochSeconds(item.Value, "lc") ?? HaProtocol.EpochSeconds(item.Value, "lu");
                entities[item.Name] = new HaLiveEntity(item.Name, HaJson.String(item.Value, "s") ?? "", attributes, time);
                changed = true;
            }
        }

        if (change.TryGetProperty("c", out var changes) && changes.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in changes.EnumerateObject())
            {
                if (!entities.TryGetValue(item.Name, out var kept) || item.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string state = kept.State;
                var time = kept.LastChanged;
                Dictionary<string, JsonElement>? attributes = null;
                if (item.Value.TryGetProperty("+", out var plus) && plus.ValueKind == JsonValueKind.Object)
                {
                    if (HaJson.String(plus, "s") is { } s)
                    {
                        state = s;
                    }

                    time = HaProtocol.EpochSeconds(plus, "lc") ?? time;
                    if (plus.TryGetProperty("a", out var a) && a.ValueKind == JsonValueKind.Object)
                    {
                        attributes = new Dictionary<string, JsonElement>(kept.Attributes, StringComparer.Ordinal);
                        foreach (var attribute in a.EnumerateObject())
                        {
                            attributes[attribute.Name] = attribute.Value.Clone();
                        }
                    }
                }

                if (item.Value.TryGetProperty("-", out var minus) && minus.ValueKind == JsonValueKind.Object
                    && minus.TryGetProperty("a", out var gone) && gone.ValueKind == JsonValueKind.Array)
                {
                    attributes ??= new Dictionary<string, JsonElement>(kept.Attributes, StringComparer.Ordinal);
                    foreach (var name in gone.EnumerateArray())
                    {
                        if (name.ValueKind == JsonValueKind.String)
                        {
                            attributes.Remove(name.GetString()!);
                        }
                    }
                }

                entities[item.Name] = kept with { State = state, LastChanged = time, Attributes = (IReadOnlyDictionary<string, JsonElement>?)attributes ?? kept.Attributes };
                changed = true;
            }
        }

        if (change.TryGetProperty("r", out var removed) && removed.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in removed.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String && entities.Remove(id.GetString()!))
                {
                    changed = true;
                }
            }
        }

        return changed;
    }
}

/// <summary>Where the live feed stands.</summary>
public enum HaLiveStatus
{
    /// <summary>Not wanted (the bar is hidden), or no URL and token set.</summary>
    Off,

    /// <summary>Opening the socket and signing in.</summary>
    Connecting,

    /// <summary>Signed in; the states stream.</summary>
    Live,

    /// <summary>The connection failed or dropped; trying again after a wait.</summary>
    Retrying,

    /// <summary>The token was refused; no retry until the URL or the token changes.</summary>
    Unauthorized,
}

/// <summary>
/// The live feed at one moment (2026-10-12), immutable: its status, the entities subscribed to, the registries (null until
/// read), a version that moves with every change (the bar's cache key) and the last failure's sentence.
/// </summary>
public sealed record HaLiveSnapshot(HaLiveStatus Status, IReadOnlyDictionary<string, HaLiveEntity> Entities, HaCatalog? Catalog, long Version, string? Error)
{
    /// <summary>Nothing yet.</summary>
    public static readonly HaLiveSnapshot Empty = new(HaLiveStatus.Off, new Dictionary<string, HaLiveEntity>(StringComparer.Ordinal), null, 0, null);

    /// <summary>Whether the states are being kept current.</summary>
    public bool Fresh => Status == HaLiveStatus.Live;
}
