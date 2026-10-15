using System.Text.Json;

namespace NeonSidekick.HomeAssistant;

/// <summary>A floor: its id, name and level (null when none is set; the picker orders by it).</summary>
public sealed record HaFloor(string Id, string Name, int? Level);

/// <summary>An area (room) as the registry keeps it: id, name, and the floor it is on (null when none).</summary>
public sealed record HaAreaEntry(string Id, string Name, string? FloorId);

/// <summary>A device: id, the name the user gave it (else its own), and its area.</summary>
public sealed record HaDevice(string Id, string Name, string? AreaId);

/// <summary>
/// An entity as the registry keeps it: its id, device, own area (which overrides the device's), registry name (null =
/// the device's or the integration's), whether it is disabled or hidden, its category (<c>config</c>, <c>diagnostic</c>, or
/// null for a primary entity) and labels.
/// </summary>
public sealed record HaRegistryEntity(string Id, string? DeviceId, string? AreaId, string? Name, bool Disabled, bool Hidden, string? Category, IReadOnlyList<string> Labels);

/// <summary>A label: id and name.</summary>
public sealed record HaLabel(string Id, string Name);

/// <summary>
/// The registries and the states at one moment (2026-10-12, the home bar's): which entities exist, what each is called, the
/// area each is in (its own, else its device's), the floors the areas are on and the labels. Read over the WebSocket API
/// (<see cref="HaProtocol"/>'s lists); <see cref="FromSnapshot"/> makes the lesser one the REST snapshot allows — areas
/// from the template, no floors, devices or labels. Pure over its data.
/// </summary>
public sealed class HaCatalog
{
    private readonly Dictionary<string, HaAreaEntry> _areas;
    private readonly Dictionary<string, HaFloor> _floors;
    private readonly Dictionary<string, HaDevice> _devices;
    private readonly Dictionary<string, HaRegistryEntity> _registry;
    private readonly Dictionary<string, HaEntity> _states;
    private readonly Dictionary<string, string> _templateAreaOf;

    // Each area's visible entities, by id: built once (the review's finding, 2026-10-12: a scan of the whole registry per room
    // chip per rebuild ran inside the pane's lock).
    private readonly Dictionary<string, List<string>> _members = new(StringComparer.Ordinal);

    public HaCatalog(
        IReadOnlyList<HaFloor> floors,
        IReadOnlyList<HaAreaEntry> areas,
        IReadOnlyList<HaDevice> devices,
        IReadOnlyList<HaRegistryEntity> entities,
        IReadOnlyList<HaLabel> labels,
        IReadOnlyList<HaEntity> states,
        IReadOnlyDictionary<string, string>? areaOf = null)
    {
        Floors = floors ?? throw new ArgumentNullException(nameof(floors));
        Labels = labels ?? throw new ArgumentNullException(nameof(labels));
        States = states ?? throw new ArgumentNullException(nameof(states));
        _floors = Index(floors, f => f.Id);
        _areas = Index(areas ?? throw new ArgumentNullException(nameof(areas)), a => a.Id);
        _devices = Index(devices ?? throw new ArgumentNullException(nameof(devices)), d => d.Id);
        _registry = Index(entities ?? throw new ArgumentNullException(nameof(entities)), e => e.Id);
        _states = Index(states, s => s.Id);
        _templateAreaOf = areaOf is null ? new(StringComparer.Ordinal) : new(areaOf, StringComparer.Ordinal);

        // Floors by level (none last), then name; areas by their floor's place, then name.
        var floorOrder = floors.OrderBy(f => f.Level ?? int.MaxValue).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select((f, i) => (f.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
        OrderedFloors = floors.OrderBy(f => floorOrder[f.Id]).ToList();
        Areas = areas.OrderBy(a => a.FloorId is { } f && floorOrder.TryGetValue(f, out int at) ? at : int.MaxValue)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

        IEnumerable<string> ids = _registry.Count > 0 ? _registry.Keys : _states.Keys.Concat(_templateAreaOf.Keys).Distinct(StringComparer.Ordinal);
        foreach (string id in ids.Order(StringComparer.Ordinal))
        {
            if (AreaOf(id) is { } area && Visible(id))
            {
                (_members.TryGetValue(area, out var list) ? list : _members[area] = []).Add(id);
            }
        }
    }

    /// <summary>The floors as listed.</summary>
    public IReadOnlyList<HaFloor> Floors { get; }

    /// <summary>The floors by level (none last), then name.</summary>
    public IReadOnlyList<HaFloor> OrderedFloors { get; }

    /// <summary>The areas by floor, then name.</summary>
    public IReadOnlyList<HaAreaEntry> Areas { get; }

    public IReadOnlyList<HaLabel> Labels { get; }

    /// <summary>Every entity's state, as <c>get_states</c> gave them (empty when the catalog was read without).</summary>
    public IReadOnlyList<HaEntity> States { get; }

    /// <summary>Whether the registries were read (false for one made out of the REST snapshot).</summary>
    public bool HasRegistry => _registry.Count > 0;

    /// <summary>An area by id, or null.</summary>
    public HaAreaEntry? Area(string id) => _areas.GetValueOrDefault(id);

    /// <summary>A floor by id, or null.</summary>
    public HaFloor? Floor(string? id) => id is null ? null : _floors.GetValueOrDefault(id);

    /// <summary>An entity's state, or null.</summary>
    public HaEntity? State(string id) => _states.GetValueOrDefault(id);

    /// <summary>An entity's registry entry, or null.</summary>
    public HaRegistryEntity? Entry(string id) => _registry.GetValueOrDefault(id);

    /// <summary>The area <paramref name="entityId"/> is in: its own, else its device's; null when none.</summary>
    public string? AreaOf(string entityId)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        if (_registry.TryGetValue(entityId, out var entry))
        {
            if (entry.AreaId is { Length: > 0 } own)
            {
                return own;
            }

            return entry.DeviceId is { } device && _devices.TryGetValue(device, out var d) ? d.AreaId : null;
        }

        return _templateAreaOf.GetValueOrDefault(entityId);
    }

    /// <summary>
    /// Whether the entity is one to offer: not disabled, not hidden, and no config or diagnostic category. Without the
    /// registry, everything is.
    /// </summary>
    public bool Visible(string entityId) => !_registry.TryGetValue(entityId, out var entry) || (!entry.Disabled && !entry.Hidden && entry.Category is null);

    /// <summary>Whether the entity is disabled (so it has no state to show).</summary>
    public bool Disabled(string entityId) => _registry.TryGetValue(entityId, out var entry) && entry.Disabled;

    /// <summary>The ids in area <paramref name="areaId"/> of the <paramref name="domains"/> (every domain when empty), visible ones, by id.</summary>
    public IReadOnlyList<string> MembersOf(string areaId, params string[] domains)
    {
        ArgumentNullException.ThrowIfNull(areaId);
        if (!_members.TryGetValue(areaId, out var members))
        {
            return [];
        }

        return domains.Length == 0 ? members : members.Where(id => domains.Contains(DomainOf(id), StringComparer.Ordinal)).ToList();
    }

    /// <summary>
    /// Every visible entity of <paramref name="domain"/> the catalog knows (2026-10-15, the Web UI's scenes): the registry's, or the states' without
    /// one, by id.
    /// </summary>
    public IReadOnlyList<string> EntitiesOf(string domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        IEnumerable<string> ids = _registry.Count > 0 ? _registry.Keys : _states.Keys;
        return ids.Where(id => string.Equals(DomainOf(id), domain, StringComparison.Ordinal) && Visible(id)).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>The name to show for an entity: the state's friendly name, else the registry's, else the id.</summary>
    public string NameOf(string entityId)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        if (_states.TryGetValue(entityId, out var state) && !string.Equals(state.Name, state.Id, StringComparison.Ordinal))
        {
            return state.Name;
        }

        return _registry.TryGetValue(entityId, out var entry) && entry.Name is { Length: > 0 } name ? name : entityId;
    }

    /// <summary>The labels' names on an entity, in the registry's order.</summary>
    public IReadOnlyList<string> LabelsOf(string entityId)
    {
        if (!_registry.TryGetValue(entityId, out var entry) || entry.Labels.Count == 0)
        {
            return [];
        }

        return entry.Labels.Select(id => Labels.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.Ordinal))?.Name ?? id).ToList();
    }

    /// <summary>The part of an id before the dot. Pure.</summary>
    public static string DomainOf(string entityId) => entityId.IndexOf('.', StringComparison.Ordinal) is int dot and > 0 ? entityId[..dot] : entityId;

    /// <summary>
    /// The catalog out of the five registry lists' <c>result</c> arrays and <c>get_states</c>' (any may be default: read as
    /// none). Pure.
    /// </summary>
    public static HaCatalog Parse(JsonElement floors, JsonElement areas, JsonElement devices, JsonElement entities, JsonElement labels, JsonElement states)
    {
        var floorList = Items(floors).Where(f => HaJson.String(f, "floor_id") is { Length: > 0 })
            .Select(f => new HaFloor(HaJson.String(f, "floor_id")!, HaJson.String(f, "name") ?? HaJson.String(f, "floor_id")!, HaJson.Number(f, "level") is { } level ? (int)level : null))
            .ToList();
        var areaList = Items(areas).Where(a => HaJson.String(a, "area_id") is { Length: > 0 })
            .Select(a => new HaAreaEntry(HaJson.String(a, "area_id")!, HaJson.String(a, "name") ?? HaJson.String(a, "area_id")!, Blank(HaJson.String(a, "floor_id"))))
            .ToList();
        var deviceList = Items(devices).Where(d => HaJson.String(d, "id") is { Length: > 0 })
            .Select(d => new HaDevice(HaJson.String(d, "id")!, Blank(HaJson.String(d, "name_by_user")) ?? HaJson.String(d, "name") ?? HaJson.String(d, "id")!, Blank(HaJson.String(d, "area_id"))))
            .ToList();
        var entityList = Items(entities).Where(e => HaJson.String(e, "entity_id") is { Length: > 0 })
            .Select(e => new HaRegistryEntity(
                HaJson.String(e, "entity_id")!,
                Blank(HaJson.String(e, "device_id")),
                Blank(HaJson.String(e, "area_id")),
                Blank(HaJson.String(e, "name")) ?? Blank(HaJson.String(e, "original_name")),
                HaJson.String(e, "disabled_by") is not null,
                HaJson.String(e, "hidden_by") is not null,
                Blank(HaJson.String(e, "entity_category")),
                HaJson.Strings(e, "labels")))
            .ToList();
        var labelList = Items(labels).Where(l => HaJson.String(l, "label_id") is { Length: > 0 })
            .Select(l => new HaLabel(HaJson.String(l, "label_id")!, HaJson.String(l, "name") ?? HaJson.String(l, "label_id")!))
            .ToList();
        var stateList = states.ValueKind == JsonValueKind.Array
            ? HaSnapshot.Parse(states.GetRawText(), null, DateTimeOffset.MinValue)?.Entities ?? []
            : [];
        return new HaCatalog(floorList, areaList, deviceList, entityList, labelList, stateList);
    }

    /// <summary>The lesser catalog the REST snapshot allows: its areas (no floors) and states; no registry. Pure.</summary>
    public static HaCatalog FromSnapshot(HaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var areaOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var area in snapshot.Areas)
        {
            foreach (string id in area.EntityIds)
            {
                areaOf.TryAdd(id, area.Id);
            }
        }

        return new HaCatalog([], snapshot.Areas.Select(a => new HaAreaEntry(a.Id, a.Name, null)).ToList(), [], [], [], snapshot.Entities, areaOf);
    }

    private static IEnumerable<JsonElement> Items(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object) : [];

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var index = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            index.TryAdd(key(item), item);
        }

        return index;
    }
}
