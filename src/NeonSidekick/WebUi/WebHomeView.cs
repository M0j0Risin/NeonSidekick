using System.Globalization;
using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Perf;
using NeonSidekick.Settings;
using NeonSidekick.Viewer;
using Spectre.Console;

namespace NeonSidekick.WebUi;

/// <summary>
/// The Home tab's pieces (2026-10-15, the plan's Part D), pure over what the screen hands it: the home bar's items as the tab shows them (the
/// shopping chip left out: the Shopping tab is its own), each a tile built by the bar's own <see cref="HomeChips.Build"/> so its actions, its
/// confirms and its words are the PC's; the tiles' and the rooms' reach (the only entities a sheet or an action may name); Home Assistant's state in
/// a word and a sentence; the rooms (2026-10-16, the scenes' boxes until then); and the cards' figures (the meters in whole percents, so an idle PC's tab does not change every second).
/// </summary>
public static class WebHomeView
{
    /// <summary>
    /// The items the tab shows: the home bar's in its order, or, while the bar is hidden, the last ones it showed; never the shopping list's chip.
    /// </summary>
    public static IReadOnlyList<string> Items(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var items = HomeBarItems.Resolve(effective.HomeBarItems);
        if (items.Count == 0)
        {
            items = HomeBarItems.Resolve(effective.HomeBarLastItems);
        }

        return items.Where(i => !HomeBarItems.TryParse(i, out var kind, out _) || kind is not (HomeItemKind.Shopping or HomeItemKind.Monitors)).ToList();
    }

    /// <summary>
    /// The tiles for <paramref name="items"/> over the live states: the bar's chips, a divider starting the next group (never a tile of its own),
    /// with every action <c>none</c> while <paramref name="controls"/> is off.
    /// </summary>
    public static IReadOnlyList<WebHomeTile> Tiles(
        IReadOnlyList<string> items,
        HaLiveSnapshot live,
        Func<string, HomeChipMark> mark,
        IReadOnlyDictionary<string, string>? aliases,
        IReadOnlyDictionary<string, string>? glyphs,
        bool controls)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(mark);
        bool stale = live.Status != HaLiveStatus.Live;
        var tiles = new List<WebHomeTile>(items.Count);
        int group = 0;
        foreach (string item in items)
        {
            var chip = HomeChips.Build(item, live.Entities, live.Catalog, mark(item), stale, aliases, glyphs);
            if (chip.Divider)
            {
                group += tiles.Count > 0 && tiles[^1].Group == group ? 1 : 0;
                continue;
            }

            var (tone, color) = ToneOf(chip, stale);
            string action = !controls ? "none" : chip.Action switch
            {
                HomeChipAction.Call => "call",
                HomeChipAction.Confirm => "confirm",
                HomeChipAction.Detail => "detail",
                _ => "none",
            };
            string? marked = MarkWord(chip.Mark);
            bool sheet = HomeBarItems.TryParse(item, out var kind, out _) && kind is HomeItemKind.Area or HomeItemKind.Entity;
            tiles.Add(new WebHomeTile(chip.Reference, chip.Glyph, chip.Shown, chip.Value, chip.Note, action, marked, tone, color, group, sheet));
        }

        return tiles;
    }

    /// <summary>
    /// A chip's tone as the page colors it, from the look the bar chose: <c>alert</c> (bold), <c>off</c> (the dim words), <c>stale</c> (the feed not
    /// live, or nothing to do: unavailable, not read yet), <c>plain</c> (a sensor's value in the ink), else <c>on</c>; with the chip's own color for
    /// an on or alert one (a light's tint, a playing player's primary). Pure.
    /// </summary>
    public static (string Tone, string? Color) ToneOf(HomeChip chip, bool stale)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var style = chip.Style;
        if (stale || (chip.Action == HomeChipAction.None && chip.Value == "?"))
        {
            return ("stale", null);
        }

        if ((style.Decoration & Decoration.Bold) != 0)
        {
            return ("alert", "#" + style.Foreground.ToHex());
        }

        if (style.Foreground == UI.Theme.Dim)
        {
            return (chip.Action == HomeChipAction.None ? "stale" : "off", null);
        }

        if (style.Foreground == UI.Theme.Ink)
        {
            return ("plain", null);
        }

        return ("on", "#" + style.Foreground.ToHex());
    }

    /// <summary>
    /// The entities a sheet or an action may name (2026-10-15, the second review: nothing off the home bar): each entity tile's own, and each area
    /// tile's members of the room pane's domains. The areas themselves under their <c>area:</c> reference. Since 2026-10-16 (the user's call in
    /// choosing every room for the tab's Rooms) <paramref name="rooms"/>' too: each box's switch (an area under its <c>area:</c> reference, a
    /// zone's group light by its id) and every device row's entity. Acting still needs <i>Web UI home controls</i> and the actions as they are now.
    /// </summary>
    public static IReadOnlySet<string> Reach(IReadOnlyList<string> items, HaCatalog? catalog, IReadOnlyList<string> roomDomains, IReadOnlyList<WebHomeRoom>? rooms = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(roomDomains);
        var reach = new HashSet<string>(StringComparer.Ordinal);
        foreach (string item in items)
        {
            if (!HomeBarItems.TryParse(item, out var kind, out string id))
            {
                continue;
            }

            if (kind == HomeItemKind.Entity)
            {
                reach.Add(id);
            }
            else if (kind == HomeItemKind.Area)
            {
                reach.Add(item);
                reach.UnionWith(catalog?.MembersOf(id, [.. roomDomains]) ?? []);
            }
        }

        foreach (var room in rooms ?? [])
        {
            if (room.Ref is { } reference && HomeBarItems.TryParse(reference, out var kind, out string id))
            {
                reach.Add(kind == HomeItemKind.Area ? reference : id);
            }

            reach.UnionWith(room.Devices.Select(d => d.Entity));
        }

        return reach;
    }

    /// <summary>
    /// Whether <paramref name="reference"/> (a tile's, a room box's switch, a device row's <c>entity:</c>) is the tab's to read or act on
    /// (2026-10-16): one of the home bar's <paramref name="items"/>, an area in <paramref name="reach"/>, or an entity whose id is. Pure.
    /// </summary>
    public static bool Reaches(string reference, IReadOnlyList<string> items, IReadOnlySet<string> reach)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(reach);
        if (items.Contains(reference, StringComparer.Ordinal))
        {
            return true;
        }

        return HomeBarItems.TryParse(reference, out var kind, out string id)
            && (kind == HomeItemKind.Area ? reach.Contains(reference) : kind == HomeItemKind.Entity && reach.Contains(id));
    }

    /// <summary>Home Assistant's state as the tab names it, and the sentence under it (none while live). Pure.</summary>
    public static (string Status, string? Text) StatusOf(HaLiveSnapshot live, bool configured)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!configured)
        {
            return ("off", WebUiText.HomeNotSetUp);
        }

        return live.Status switch
        {
            HaLiveStatus.Live => ("live", null),
            HaLiveStatus.Connecting => ("connecting", WebUiText.HomeConnecting),
            HaLiveStatus.Retrying => ("retrying", WebUiText.HomeRetrying(live.Error)),
            HaLiveStatus.Unauthorized => ("unauthorized", WebUiText.HomeUnauthorized),
            _ => ("connecting", WebUiText.HomeConnecting),
        };
    }

    /// <summary>
    /// The tab's Rooms (2026-10-16, the user's ask: the Scenes boxes became rooms, each doing what <c>/homebar show &lt;room&gt;</c> does), a box per
    /// room: every area with a member of the room pane's domains (<paramref name="roomDomains"/>, <c>ChatScreen.HomeRoomDomains</c>) or a scene,
    /// each Hue zone (a group light with no area, on a device of its own), and the scenes' device-named and <see cref="HomeBarText.NoArea"/> boxes.
    /// A scene goes by its area (its own, else its device's); with none, by its device's name (Hue hangs each scene on its room's or zone's
    /// device), joining the area of that name when one exists so there are never two "Den" boxes; else under no area. A REST-only catalog has no
    /// devices, so no zones. Each box: its switch (an area's lights, the bar's own <c>area:</c> chip; a zone's group light, <c>light.toggle</c>;
    /// none for a box with no lights), the lights on of all as <see cref="WebUiText.RoomLightsOn"/>, its tone and mark as a tile's, then a row per
    /// device (an area's members in the room pane's domain order, a zone's group's members; light groups left out, the switch covers them), then
    /// its scenes by name, each labeled without its box's name in front. The boxes: the home bar's rooms in the bar's order, the other areas in
    /// <see cref="HaCatalog.Areas"/>' order (by floor, then name), the device boxes by name, no area last. <paramref name="controls"/> is the
    /// page's to disable with; the rows' controls are what the device offers. Pure.
    /// </summary>
    public static IReadOnlyList<WebHomeRoom> Rooms(HaLiveSnapshot live, IReadOnlyList<string> homeBarItems, Func<string, HomeChipMark> mark, IReadOnlyList<string> roomDomains)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(homeBarItems);
        ArgumentNullException.ThrowIfNull(mark);
        ArgumentNullException.ThrowIfNull(roomDomains);
        if (live.Catalog is not { } catalog)
        {
            return [];
        }

        string[] domains = [.. roomDomains];
        bool stale = live.Status != HaLiveStatus.Live;
        var areaByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var area in catalog.Areas)
        {
            areaByName.TryAdd(area.Name, area.Id);
        }

        var boxes = new Dictionary<string, (string Name, List<(string Id, string Name)> Scenes)>(StringComparer.Ordinal);
        (string Name, List<(string Id, string Name)> Scenes) Box(string key, string name) =>
            boxes.TryGetValue(key, out var box) ? box : boxes[key] = (name, []);

        foreach (string id in catalog.EntitiesOf("scene"))
        {
            string name = live.Entities.GetValueOrDefault(id)?.Name ?? catalog.NameOf(id);
            string key;
            string groupName;
            if (catalog.AreaOf(id) is { } areaId && catalog.Area(areaId) is { } area)
            {
                (key, groupName) = (areaId, area.Name);
            }
            else if (catalog.DeviceOf(id) is { Name.Length: > 0 } device)
            {
                (key, groupName) = areaByName.TryGetValue(device.Name, out string? named)
                    ? (named, catalog.Area(named)!.Name)
                    : (DeviceKeyPrefix + device.Id, device.Name);
            }
            else
            {
                (key, groupName) = (NoAreaKey, HomeBarText.NoArea);
            }

            Box(key, groupName).Scenes.Add((id, name));
        }

        foreach (var area in catalog.Areas)
        {
            if (catalog.MembersOf(area.Id, domains).Count > 0)
            {
                Box(area.Id, area.Name);
            }
        }

        // The zones: a group light with no area, on a device whose name is no area's (that area's box already holds its scenes and switch).
        foreach (string id in ZoneLights(catalog))
        {
            if (GroupLight(id, live, catalog) is not null && catalog.DeviceOf(id) is { } device)
            {
                Box(DeviceKeyPrefix + device.Id, device.Name);
            }
        }

        var onBar = homeBarItems
            .Select(i => HomeBarItems.TryParse(i, out var kind, out string areaId) && kind == HomeItemKind.Area ? areaId : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var order = new List<string>(boxes.Count);
        order.AddRange(onBar.Where(boxes.ContainsKey));
        order.AddRange(catalog.Areas.Select(a => a.Id).Where(id => boxes.ContainsKey(id) && !order.Contains(id, StringComparer.Ordinal)));
        order.AddRange(boxes.Keys.Where(k => k.StartsWith(DeviceKeyPrefix, StringComparison.Ordinal)).OrderBy(k => boxes[k].Name, StringComparer.OrdinalIgnoreCase).ThenBy(k => k, StringComparer.Ordinal));
        if (boxes.ContainsKey(NoAreaKey))
        {
            order.Add(NoAreaKey);
        }

        return order.Select(key =>
        {
            var (name, sceneList) = boxes[key];
            bool named = key != NoAreaKey;
            var scenes = sceneList.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => new WebHomeScene(s.Id, s.Name, named ? LabelIn(s.Name, name) : s.Name)).ToList();
            string? reference = null;
            IReadOnlyList<string> members = [];
            int on = 0;
            int total = 0;
            if (catalog.Area(key) is not null)
            {
                members = catalog.MembersOf(key, domains)
                    .Where(m => GroupLight(m, live, catalog) is null)
                    .OrderBy(m => Array.IndexOf(domains, HaCatalog.DomainOf(m)))
                    .ToList();
                var lights = HomeChips.AreaLights(key, live.Entities, catalog);
                (on, total) = (lights.Count(e => e.State == "on"), lights.Count);
                reference = total > 0 ? HomeBarItems.Area(key) : null;
            }
            else if (key.StartsWith(DeviceKeyPrefix, StringComparison.Ordinal)
                && catalog.EntitiesOn(key[DeviceKeyPrefix.Length..], "light").Select(l => (Id: l, Group: GroupLight(l, live, catalog))).FirstOrDefault(l => l.Group is not null) is { Group: { } group } zone)
            {
                members = group.GroupMembers.Where(m => catalog.Visible(m) && GroupLight(m, live, catalog) is null).Distinct(StringComparer.Ordinal).ToList();
                var lit = members.Select(m => live.Entities.GetValueOrDefault(m)).OfType<HaLiveEntity>().Where(e => !e.Unavailable).ToList();
                (on, total) = (lit.Count(e => e.State == "on"), lit.Count);
                reference = HomeBarItems.Entity(zone.Id);
            }

            string? value = null;
            string tone = "off";
            string? color = null;
            string? marked = null;
            bool? switchedOn = null;
            if (reference is not null)
            {
                var chip = HomeChips.Build(reference, live.Entities, catalog, mark(reference), stale);
                (tone, color) = ToneOf(chip, stale);
                marked = MarkWord(chip.Mark);
                value = total > 0 ? WebUiText.RoomLightsOn(on, total) : null;
                switchedOn = HomeBarItems.TryParse(reference, out var kind, out string switchId) && kind == HomeItemKind.Entity
                    ? live.Entities.GetValueOrDefault(switchId)?.State == "on"
                    : on > 0;
            }

            // A device's row drops its box's name in front (2026-10-17, the user's phone: "Living Room Table Lamp Back" in Living Room), as the
            // scenes' labels do; the full name stays its title, its switch's label and its sheet's.
            var devices = members.Select(m => Device(m, live, catalog, mark, stale))
                .Select(d => named && LabelIn(d.Name, name) is var label && label != d.Name ? d with { Short = label } : d)
                .ToList();
            return new WebHomeRoom(key, reference, name, onBar.Contains(key, StringComparer.Ordinal), value, tone, marked, color, switchedOn, devices, scenes);
        }).ToList();
    }

    /// <summary>
    /// The lights that may be a zone's group light (2026-10-16): visible, with no area, on a device whose name is no area's. Whether one is a group
    /// is in its attributes, which the live feed carries only for the ids it is asked for: the screen wants these, and the next read finds the
    /// zones among them (the zone's box shows its scenes alone until then). Pure.
    /// </summary>
    public static IReadOnlyList<string> ZoneLights(HaCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var areaNames = new HashSet<string>(catalog.Areas.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        return catalog.EntitiesOf("light")
            .Where(id => catalog.AreaOf(id) is null && catalog.DeviceOf(id) is { Name.Length: > 0 } device && !areaNames.Contains(device.Name))
            .ToList();
    }

    /// <summary>
    /// <paramref name="id"/> as a light group (a Hue room or zone, any group listing its members), from the feed when it holds it, else from the
    /// catalog's state (so a zone's box and its members show before the feed has its attributes); null when it is none. Pure.
    /// </summary>
    private static HaLiveEntity? GroupLight(string id, HaLiveSnapshot live, HaCatalog catalog)
    {
        if (HaCatalog.DomainOf(id) != "light")
        {
            return null;
        }

        var entity = live.Entities.GetValueOrDefault(id) ?? (catalog.State(id) is { } state ? HaLiveEntity.From(state) : null);
        return entity is { IsGroup: true } ? entity : null;
    }

    /// <summary>
    /// A room box's row (2026-10-16): the device's glyph, name and state (<see cref="DeviceState"/>, a lit light's brightness after it), its chip's
    /// note, tone and mark, and its quick control — the first of its actions (<c>ChatScreen.HomeActions</c>): a <c>switch</c> for a light, switch,
    /// fan or <c>input_boolean</c> (Toggle), a <c>button</c> labeled with it for a player, a cover or a lock (asking where the bar asks), <c>none</c>
    /// for a thermostat (the room pane's Space skips it too) or a device unavailable or not read yet. Pure.
    /// </summary>
    private static WebHomeDevice Device(string id, HaLiveSnapshot live, HaCatalog catalog, Func<string, HomeChipMark> mark, bool stale)
    {
        var entity = live.Entities.GetValueOrDefault(id);
        string reference = HomeBarItems.Entity(id);
        var chip = HomeChips.Build(reference, live.Entities, catalog, mark(reference), stale);
        var (tone, color) = ToneOf(chip, stale);
        string state = DeviceState(entity);
        if (entity is { Domain: "light", State: "on" } && entity.Number("brightness") is { } brightness)
        {
            state += " · " + HomeChips.Percent(brightness / 255 * 100);
        }

        string control = "none";
        bool? on = null;
        string? label = null;
        bool ask = false;
        if (entity is { Unavailable: false, Domain: not "climate" } && ChatScreen.HomeActions(entity) is [var first, ..])
        {
            control = entity.Domain is "light" or "switch" or "input_boolean" or "fan" ? "switch" : "button";
            on = control == "switch" ? entity.State == "on" : null;
            (label, ask) = (first.Label, first.Ask);
        }

        return new WebHomeDevice(id, HomeChips.GlyphFor(id, entity), entity?.Name ?? catalog.NameOf(id), state, chip.Note, control, on, label, ask, MarkWord(chip.Mark), tone, color);
    }

    /// <summary>A device's state in words as a sheet's row and a room's row show it: <c>…</c> not read yet, <c>?</c> unavailable, else the state's word and its unit. Pure.</summary>
    public static string DeviceState(HaLiveEntity? entity) =>
        entity is null ? "…" : entity.Unavailable ? "?" : HomeBarText.StateWord(entity.State) + (entity.Text("unit_of_measurement") is { Length: > 0 } unit ? " " + unit : "");

    /// <summary>A chip's mark as the page names it: <c>pending</c>, <c>failed</c>, or none.</summary>
    private static string? MarkWord(HomeChipMark mark) => mark switch
    {
        HomeChipMark.Pending => "pending",
        HomeChipMark.Failed => "failed",
        _ => null,
    };

    /// <summary>The key of the scenes with no area and no device (2026-10-16).</summary>
    public const string NoAreaKey = "none";

    /// <summary>What a device-named box's key starts with, before the device's id (2026-10-16).</summary>
    public const string DeviceKeyPrefix = "device:";

    /// <summary>A scene's name with its box's in front taken off ("Den Relax" in Den is "Relax"), else the whole name. Pure.</summary>
    public static string LabelIn(string name, string group)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(group);
        if (group.Length == 0 || name.Length <= group.Length + 1 || !name.StartsWith(group, StringComparison.OrdinalIgnoreCase) || name[group.Length] != ' ')
        {
            return name;
        }

        string rest = name[(group.Length + 1)..].Trim();
        return rest.Length > 0 ? rest : name;
    }

    /// <summary>The PC's meters for the card: whole percents, the network's rates as the perf bar words them (trimmed); null with nothing read.</summary>
    public static WebHomePc? Pc(PerfSnapshot reading)
    {
        static int? Whole(double? value) => value is { } v && double.IsFinite(v) ? (int)Math.Round(Math.Clamp(v, 0, 100)) : null;
        var pc = new WebHomePc(Whole(reading.Cpu), Whole(reading.Ram), Whole(reading.Gpu), Whole(reading.Vram),
            reading.NetDown is { } down ? PerfText.Rate(down).Trim() : null, reading.NetUp is { } up ? PerfText.Rate(up).Trim() : null);
        return pc is { Cpu: null, Ram: null, Gpu: null, Vram: null, Down: null, Up: null } ? null : pc;
    }

    /// <summary>
    /// The video playing for the card, in the PC's window or (2026-10-16) the YouTube tab, or null with none open. A video waiting for a YouTube
    /// tab reads <c>waiting</c>, not <c>opening</c>; one with no title yet sends its id (or its playlist) as the title, never an empty line; and
    /// <see cref="WebHomeVideo.Where"/> names the place.
    /// </summary>
    public static WebHomeVideo? Video(VideoSnapshot? snapshot)
    {
        if (snapshot is not { } video)
        {
            return null;
        }

        string state = video.Held is not null ? "waiting" : video.State.ToString().ToLowerInvariant();
        string? title = !string.IsNullOrWhiteSpace(video.Title) ? video.Title
            : video.VideoId is { } id ? id
            : video.PlaylistId is { } list ? "playlist " + list
            : null;
        return new WebHomeVideo(state, title, video.Author, Math.Round(video.Position, 1), Math.Round(video.Duration, 1), video.Volume, video.Muted,
            video.PlaylistEntry is { } entry ? entry + 1 : null, video.PlaylistCount, video.Failure, WebUiText.VideoWhere(video.Place));
    }

    /// <summary>The model's server as the card names it: the embedded model, a Docker container, the Claude Code server, or the host alone (never a key). Pure.</summary>
    public static string ServerOf(Uri? baseUrl)
    {
        if (baseUrl is null)
        {
            return "";
        }

        if (EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(baseUrl))
        {
            return WebUiText.EmbeddedServer;
        }

        if (Docker.DockerEndpoint.ContainerOf(baseUrl) is { } container)
        {
            return WebUiText.DockerServer(container);
        }

        return Claude.ClaudeCliEndpoint.IsClaudeCli(baseUrl) ? WebUiText.ClaudeCodeServer : baseUrl.IsDefaultPort ? baseUrl.Host : baseUrl.Host + ":" + baseUrl.Port.ToString(CultureInfo.InvariantCulture);
    }
}
