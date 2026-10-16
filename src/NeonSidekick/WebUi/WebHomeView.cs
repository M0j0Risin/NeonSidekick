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
/// confirms and its words are the PC's; the tiles' reach (the only entities a sheet or an action may name); Home Assistant's state in a word and a
/// sentence; the scenes; and the cards' figures (the meters in whole percents, so an idle PC's tab does not change every second).
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

        return items.Where(i => !HomeBarItems.TryParse(i, out var kind, out _) || kind != HomeItemKind.Shopping).ToList();
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
            string? marked = chip.Mark switch
            {
                HomeChipMark.Pending => "pending",
                HomeChipMark.Failed => "failed",
                _ => null,
            };
            bool sheet = HomeBarItems.TryParse(item, out var kind, out _) && kind is HomeItemKind.Area or HomeItemKind.Entity;
            tiles.Add(new WebHomeTile(chip.Reference, chip.Glyph, chip.Label is { Length: > 0 } label ? label : chip.Name, chip.Value, chip.Note, action, marked, tone, color, group, sheet));
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
    /// tile's members of the room pane's domains. The areas themselves under their <c>area:</c> reference.
    /// </summary>
    public static IReadOnlySet<string> Reach(IReadOnlyList<string> items, HaCatalog? catalog, IReadOnlyList<string> roomDomains)
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

        return reach;
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
    /// Every scene the catalog knows in a box per room or zone (2026-10-16, the user's ask: one long wrap of buttons was hard to scan), each named
    /// as the live states name it (else the registry). A scene goes by its area (its own, else its device's); with none, by its device's name
    /// (Hue hangs each scene on its room's or zone's device, and a zone spanning rooms usually has no area), joining the area of that name when
    /// one exists so there are never two "Den" boxes; else under <see cref="HomeBarText.NoArea"/>. A REST-only catalog has no devices. The
    /// boxes: the home bar's rooms (<paramref name="homeBarItems"/>' <c>area:</c> entries) in the bar's order, the other areas in
    /// <see cref="HaCatalog.Areas"/>' order (by floor, then name: the PC's picker's), the device-named ones by name, no area last; the scenes in
    /// each by name, each labeled without its box's name in front ("Den Relax" in Den reads "Relax"). Pure.
    /// </summary>
    public static IReadOnlyList<WebHomeSceneGroup> SceneGroups(HaLiveSnapshot live, IReadOnlyList<string> homeBarItems)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(homeBarItems);
        if (live.Catalog is not { } catalog)
        {
            return [];
        }

        var areaByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var area in catalog.Areas)
        {
            areaByName.TryAdd(area.Name, area.Id);
        }

        var members = new Dictionary<string, (string Name, List<(string Id, string Name)> Scenes)>(StringComparer.Ordinal);
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

            (members.TryGetValue(key, out var group) ? group : members[key] = (groupName, [])).Scenes.Add((id, name));
        }

        var onBar = homeBarItems
            .Select(i => HomeBarItems.TryParse(i, out var kind, out string areaId) && kind == HomeItemKind.Area ? areaId : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var order = new List<string>(members.Count);
        order.AddRange(onBar.Where(members.ContainsKey));
        order.AddRange(catalog.Areas.Select(a => a.Id).Where(id => members.ContainsKey(id) && !order.Contains(id, StringComparer.Ordinal)));
        order.AddRange(members.Keys.Where(k => k.StartsWith(DeviceKeyPrefix, StringComparison.Ordinal)).OrderBy(k => members[k].Name, StringComparer.OrdinalIgnoreCase).ThenBy(k => k, StringComparer.Ordinal));
        if (members.ContainsKey(NoAreaKey))
        {
            order.Add(NoAreaKey);
        }

        return order.Select(key =>
        {
            var (name, scenes) = members[key];
            bool named = key != NoAreaKey;
            return new WebHomeSceneGroup(key, name, onBar.Contains(key, StringComparer.Ordinal),
                scenes.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id, StringComparer.Ordinal)
                    .Select(s => new WebHomeScene(s.Id, s.Name, named ? LabelIn(s.Name, name) : s.Name)).ToList());
        }).ToList();
    }

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
