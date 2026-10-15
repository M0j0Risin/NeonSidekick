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

    /// <summary>Every scene the catalog knows, named as the live states name them (else the registry), sorted by area then name. Pure.</summary>
    public static IReadOnlyList<WebHomeScene> Scenes(HaLiveSnapshot live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Catalog is not { } catalog)
        {
            return [];
        }

        return catalog.EntitiesOf("scene")
            .Select(id => (Id: id, Name: live.Entities.GetValueOrDefault(id)?.Name ?? catalog.NameOf(id), Area: catalog.AreaOf(id) is { } area ? catalog.Area(area)?.Name ?? "" : ""))
            .OrderBy(s => s.Area.Length == 0 ? 1 : 0)
            .ThenBy(s => s.Area, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new WebHomeScene(s.Id, s.Name))
            .ToList();
    }

    /// <summary>The PC's meters for the card: whole percents, the network's rates as the perf bar words them (trimmed); null with nothing read.</summary>
    public static WebHomePc? Pc(PerfSnapshot reading)
    {
        static int? Whole(double? value) => value is { } v && double.IsFinite(v) ? (int)Math.Round(Math.Clamp(v, 0, 100)) : null;
        var pc = new WebHomePc(Whole(reading.Cpu), Whole(reading.Ram), Whole(reading.Gpu), Whole(reading.Vram),
            reading.NetDown is { } down ? PerfText.Rate(down).Trim() : null, reading.NetUp is { } up ? PerfText.Rate(up).Trim() : null);
        return pc is { Cpu: null, Ram: null, Gpu: null, Vram: null, Down: null, Up: null } ? null : pc;
    }

    /// <summary>The PC's YouTube window for the card, or null with none open.</summary>
    public static WebHomeVideo? Video(VideoSnapshot? snapshot)
    {
        if (snapshot is not { } video)
        {
            return null;
        }

        string state = video.State.ToString().ToLowerInvariant();
        return new WebHomeVideo(state, video.Title, video.Author, Math.Round(video.Position, 1), Math.Round(video.Duration, 1), video.Volume, video.Muted,
            video.PlaylistEntry is { } entry ? entry + 1 : null, video.PlaylistCount, video.Failure);
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
