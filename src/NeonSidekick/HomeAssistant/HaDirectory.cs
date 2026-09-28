using System.Text.Json;

namespace NeonSidekick.HomeAssistant;

/// <summary>
/// One entity as <c>/api/states</c> gives it (2026-09-28): the id (<c>light.den_den</c>), the friendly name (the id when it
/// has none), the state, the area the template put it in (null when none), the attributes as they came, and when the state
/// last changed.
/// </summary>
public sealed record HaEntity(string Id, string Name, string State, string? Area, JsonElement Attributes, DateTimeOffset? LastChanged)
{
    /// <summary>The part before the dot: <c>light</c>, <c>scene</c>, <c>media_player</c>.</summary>
    public string Domain => Id.IndexOf('.', StringComparison.Ordinal) is int dot and > 0 ? Id[..dot] : Id;

    /// <summary>Whether the state is one that says nothing is there: <c>unavailable</c> or <c>unknown</c>.</summary>
    public bool Unavailable => State is "unavailable" or "unknown";

    /// <summary>Whether it is a Hue room or zone (<c>is_hue_group</c>) or any light group (<c>entity_id</c> listing members).</summary>
    public bool IsGroup => HaJson.Boolean(Attributes, "is_hue_group") == true || HaJson.Strings(Attributes, "entity_id").Count > 0;
}

/// <summary>An area (room): its id, name and the entities in it, a device's entities included.</summary>
public sealed record HaArea(string Id, string Name, IReadOnlyList<string> EntityIds);

/// <summary>What <see cref="HaSnapshot.Resolve"/> found: the entities, or the <c>Error:</c> sentence naming the candidates.</summary>
public sealed record HaMatch(IReadOnlyList<HaEntity> Entities, string? Error)
{
    public static HaMatch Refused(string error) => new([], error);
}

/// <summary>
/// Every entity and area at one moment (2026-09-28), and the name lookup the tools and <c>/ha</c> share: the user says "the
/// den lights" or "den relax", never <c>light.den_den</c> or <c>scene.den_den_relax</c>, and a small local model guesses ids
/// badly, so the words are matched here and a miss names what there is. Pure over its data.
/// </summary>
public sealed class HaSnapshot
{
    /// <summary>The template that lists the areas as JSON (the REST API has no areas endpoint): id, name, entities.</summary>
    public const string AreasTemplate =
        "[{% for a in areas() %}{\"id\":{{ a|tojson }},\"name\":{{ area_name(a)|tojson }},\"entities\":{{ area_entities(a)|tojson }}}{% if not loop.last %},{% endif %}{% endfor %}]";

    /// <summary>The most candidates an ambiguity or a miss names.</summary>
    public const int MaxCandidates = 12;

    /// <summary>Words a spoken or typed target carries that no name needs: "turn on <b>the</b> lights <b>in the</b> den".</summary>
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal) { "the", "a", "an", "in", "on", "of", "my", "at", "all", "please" };

    /// <summary>Targets that mean every entity of the domains asked for.</summary>
    private static readonly HashSet<string> Everything = new(StringComparer.Ordinal) { "all", "everything", "all lights", "every light", "all the lights", "whole house", "the whole house" };

    /// <summary>Words that name the domain rather than the thing: "den <b>lights</b>", "the <b>tv</b>" — dropped when something is left.</summary>
    private static readonly HashSet<string> DomainWords = new(StringComparer.Ordinal) { "light", "lights", "lamp", "lamps", "scene", "scenes" };

    public HaSnapshot(IReadOnlyList<HaEntity> entities, IReadOnlyList<HaArea> areas, DateTimeOffset taken)
    {
        Entities = entities ?? throw new ArgumentNullException(nameof(entities));
        Areas = areas ?? throw new ArgumentNullException(nameof(areas));
        Taken = taken;
    }

    public IReadOnlyList<HaEntity> Entities { get; }

    public IReadOnlyList<HaArea> Areas { get; }

    /// <summary>When the snapshot was read.</summary>
    public DateTimeOffset Taken { get; }

    /// <summary>The entities of <paramref name="domain"/>, in the server's order.</summary>
    public IReadOnlyList<HaEntity> Of(string domain) => Entities.Where(e => string.Equals(e.Domain, domain, StringComparison.Ordinal)).ToList();

    /// <summary>The entity with id <paramref name="id"/> (any case), or null.</summary>
    public HaEntity? Find(string id) => Entities.FirstOrDefault(e => string.Equals(e.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A snapshot out of <c>/api/states</c>' array and the areas template's (null or unreadable = no areas: the lookup then
    /// goes by names alone). An unreadable states array is null. Pure.
    /// </summary>
    public static HaSnapshot? Parse(string statesJson, string? areasJson, DateTimeOffset taken)
    {
        ArgumentNullException.ThrowIfNull(statesJson);
        var areas = ParseAreas(areasJson);
        var areaOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var area in areas)
        {
            foreach (string id in area.EntityIds)
            {
                areaOf.TryAdd(id, area.Name);
            }
        }

        try
        {
            using var document = JsonDocument.Parse(statesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var entities = new List<HaEntity>(document.RootElement.GetArrayLength());
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (HaJson.String(item, "entity_id") is not { Length: > 0 } id)
                {
                    continue;
                }

                var attributes = item.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object ? a.Clone() : default;
                string name = HaJson.String(attributes, "friendly_name") is { Length: > 0 } friendly ? friendly.Trim() : id;
                entities.Add(new HaEntity(id, name, HaJson.String(item, "state") ?? "", areaOf.GetValueOrDefault(id), attributes, HaJson.Time(item, "last_changed")));
            }

            return new HaSnapshot(entities, areas, taken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The areas template's answer, or none when it is empty or unreadable. Pure.</summary>
    public static IReadOnlyList<HaArea> ParseAreas(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return document.RootElement.EnumerateArray()
                .Where(a => HaJson.String(a, "id") is { Length: > 0 })
                .Select(a => new HaArea(HaJson.String(a, "id")!, HaJson.String(a, "name") ?? HaJson.String(a, "id")!, HaJson.Strings(a, "entities")))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The entities <paramref name="target"/> means among <paramref name="domains"/> (every domain when empty). In order:
    /// an entity id; an exact friendly name; an area's name — its light group when the domains are lights and it has one
    /// named as the area, else every entity of the domains in it; then the names holding every word of the target (filler
    /// such as "the" and domain words such as "lights" left out), a group whose words are exactly the target's preferred.
    /// Several targets separated by commas or " and " are each resolved. No match, or several with nothing to choose between
    /// them, is an <c>Error:</c> sentence naming the candidates, so the model asks or picks. Pure.
    /// </summary>
    public HaMatch Resolve(string target, IReadOnlyList<string> domains)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(domains);
        var parts = SplitTargets(target);
        if (parts.Count == 0)
        {
            return HaMatch.Refused(HaText.NoTarget);
        }

        var found = new List<HaEntity>();
        foreach (string part in parts)
        {
            var one = ResolveOne(part, domains);
            if (one.Error is not null)
            {
                return one;
            }

            foreach (var entity in one.Entities)
            {
                if (!found.Contains(entity))
                {
                    found.Add(entity);
                }
            }
        }

        return new HaMatch(found, null);
    }

    /// <summary>A target list split on commas and the word "and" — "kitchen, den and hallway". Pure.</summary>
    public static IReadOnlyList<string> SplitTargets(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(p => p.Split(" and ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();
    }

    private HaMatch ResolveOne(string target, IReadOnlyList<string> domains)
    {
        var pool = domains.Count == 0 ? Entities : Entities.Where(e => domains.Contains(e.Domain, StringComparer.Ordinal)).ToList();
        string wanted = target.Trim();

        // "all", "all lights", "everything": every available one of the domains but the groups (their members are in it).
        if (domains.Count > 0 && Everything.Contains(Normalize(wanted)))
        {
            var every = pool.Where(e => !e.IsGroup && !e.Unavailable).ToList();
            return every.Count > 0 ? new HaMatch(every, null) : HaMatch.Refused(HaText.NotFound(wanted, domains, []));
        }

        // An id, as the model read it from ha_states.
        if (pool.FirstOrDefault(e => string.Equals(e.Id, wanted, StringComparison.OrdinalIgnoreCase)) is { } byId)
        {
            return new HaMatch([byId], null);
        }

        // A friendly name, whole.
        var named = pool.Where(e => string.Equals(Normalize(e.Name), Normalize(wanted), StringComparison.Ordinal)).ToList();
        if (named.Count > 0)
        {
            return Pick(named, wanted);
        }

        var words = Words(wanted);
        if (words.Count == 0)
        {
            return HaMatch.Refused(HaText.NoTarget);
        }

        // An area: "den", "the living room lights".
        string joined = string.Join(' ', words);
        var area = Areas.FirstOrDefault(a => string.Equals(string.Join(' ', Words(a.Name)), joined, StringComparison.Ordinal)
            || string.Equals(a.Id.Replace('_', ' '), joined, StringComparison.Ordinal));
        if (area is not null)
        {
            var inside = pool.Where(e => area.EntityIds.Contains(e.Id, StringComparer.Ordinal)).ToList();
            var group = inside.FirstOrDefault(e => e.IsGroup && string.Equals(string.Join(' ', Words(e.Name)), string.Join(' ', Words(area.Name)), StringComparison.Ordinal));
            if (group is not null)
            {
                return new HaMatch([group], null);
            }

            if (inside.Count > 0)
            {
                return new HaMatch(inside.Where(e => !e.Unavailable).DefaultIfEmpty(inside[0]).ToList(), null);
            }
        }

        // Every word of the target in the name (or the id).
        var holding = pool.Where(e => Holds(e, words)).ToList();
        if (holding.Count == 0)
        {
            return HaMatch.Refused(HaText.NotFound(wanted, domains, Suggest(pool, words)));
        }

        var exact = holding.Where(e => Words(e.Name).SequenceEqual(words)).ToList();
        if (exact.Count == 1)
        {
            return new HaMatch(exact, null);
        }

        // The closest: the one name with the fewest words besides the target's — "pantry motion" is the motion sensor, not
        // the "… motion sensor enabled" switch; "prep" between "Kitchen Prep 1" and "Kitchen Prep 2" stays a question.
        var fewest = holding.GroupBy(e => Words(e.Name).Count).OrderBy(g => g.Key).First().ToList();
        if (fewest.Count == 1)
        {
            return new HaMatch(fewest, null);
        }

        return Pick(holding, wanted);
    }

    /// <summary>One of several: the only one, else the only group, else the only available one, else the ambiguity.</summary>
    private static HaMatch Pick(IReadOnlyList<HaEntity> candidates, string wanted)
    {
        if (candidates.Count == 1)
        {
            return new HaMatch(candidates, null);
        }

        var groups = candidates.Where(e => e.IsGroup).ToList();
        if (groups.Count == 1)
        {
            return new HaMatch(groups, null);
        }

        var available = candidates.Where(e => !e.Unavailable).ToList();
        if (available.Count == 1)
        {
            return new HaMatch(available, null);
        }

        return HaMatch.Refused(HaText.Ambiguous(wanted, candidates.Take(MaxCandidates).ToList(), candidates.Count));
    }

    private static bool Holds(HaEntity entity, IReadOnlyList<string> words)
    {
        var name = Words(entity.Name);
        var id = Words(entity.Id.Replace('.', ' ').Replace('_', ' '));
        return words.All(w => name.Contains(w, StringComparer.Ordinal) || id.Contains(w, StringComparer.Ordinal));
    }

    /// <summary>The closest names for a miss: those holding any word of the target, else the first few of the pool.</summary>
    private static IReadOnlyList<HaEntity> Suggest(IReadOnlyList<HaEntity> pool, IReadOnlyList<string> words)
    {
        var some = pool.Where(e => Words(e.Name).Any(w => words.Contains(w, StringComparer.Ordinal))).Take(MaxCandidates).ToList();
        return some.Count > 0 ? some : pool.Where(e => !e.Unavailable).Take(MaxCandidates).ToList();
    }

    /// <summary>Lower case, single spaces, no punctuation. Pure.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var chars = text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The words that tell things apart: <see cref="Normalize"/>d, filler out, domain words out while others are left. Pure.</summary>
    public static IReadOnlyList<string> Words(string text)
    {
        var all = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Filler.Contains(w)).ToList();
        var kept = all.Where(w => !DomainWords.Contains(w)).ToList();
        return kept.Count > 0 ? kept : all;
    }
}
