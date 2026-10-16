using System.Globalization;
using NeonSidekick.HomeAssistant;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>What a double-click on a chip does.</summary>
public enum HomeChipAction
{
    /// <summary>Nothing (unavailable, not read yet).</summary>
    None,

    /// <summary>Calls <see cref="HomeChip.Call"/> at once.</summary>
    Call,

    /// <summary>Asks Yes/No on the pane, then calls <see cref="HomeChip.Call"/> (an unlock, a garage opening, a disarm).</summary>
    Confirm,

    /// <summary>Opens the chip's pane.</summary>
    Detail,
}

/// <summary>A chip's mark after a click: none, waiting for the state to change, or the change that never came.</summary>
public enum HomeChipMark
{
    None,
    Pending,
    Failed,
}

/// <summary>
/// One chip as the bar draws it and a click reads it: its reference, glyph, name (whole, and the short one a narrow bar uses),
/// value, look (a slab — a coloured background — or plain text), what a double-click does (and the call it makes), and the
/// one-line note a single click shows. <see cref="Label"/> is the user's alias for it (2026-10-12): what the bar draws in place
/// of <see cref="Name"/>, which the note, the pane and the calls' outcomes keep.
/// </summary>
public sealed record HomeChip(
    string Reference,
    string Glyph,
    string Name,
    string Short,
    string Value,
    Style Style,
    HomeChipAction Action,
    HaServiceCall? Call,
    string Note,
    HomeChipMark Mark = HomeChipMark.None,
    string? Label = null,
    bool Bare = false,
    bool Divider = false);

/// <summary>
/// The home bar's chips, kind by kind (2026-10-12, the user's ask; the looks and the clicks agreed in the plan): what each
/// shows, its colour, and what a double-click does — a light, switch, fan or <c>input_boolean</c> toggles; an area switches
/// its lights (off when any is on); a media player plays and pauses (on when off); a scene or script runs; a cover closes at
/// once and opens at once unless it is a garage, gate or door (then the pane asks); a lock locks at once and unlocks only
/// after a yes; an alarm disarms only after a yes; sensors and climate open their pane. Every chip sits on the
/// page and its words carry the state: on is <see cref="Lit"/> (a light's own colour), off is <see cref="Off"/> (dim), an
/// alert (an open garage, a leak, a jammed lock, a triggered alarm) is <see cref="Alert"/> (bold amber or red). The emoji
/// keeps its colour either way. Unavailable is dim with <c>?</c>; while the
/// feed is not live every chip is dim (stale). Glyphs are emoji by default (no VS16 needed), two cells each. Pure.
/// </summary>
public static class HomeChips
{
    /// <summary>The glyphs, pinned at two cells each by the tests.</summary>
    public const string AreaGlyph = "🏠";
    public const string LightGlyph = "💡";
    public const string SwitchGlyph = "🔌";
    public const string ToggleGlyph = "🔘";
    public const string FanGlyph = "🌀";
    public const string SensorGlyph = "📈";
    public const string HumidityGlyph = "💧";
    public const string BatteryGlyph = "🔋";
    public const string PowerGlyph = "⚡";
    public const string LightLevelGlyph = "🔆";
    public const string DoorGlyph = "🚪";
    public const string WindowGlyph = "🪟";
    public const string GarageGlyph = "🚗";
    public const string GateGlyph = "🚧";
    public const string MotionGlyph = "🚶";
    public const string SmokeGlyph = "🔥";
    public const string HeatGlyph = "🔥";
    public const string CoolGlyph = "🧊";
    public const string IdleGlyph = "💤";
    public const string TvGlyph = "📺";
    public const string SpeakerGlyph = "🔊";
    public const string MediaGlyph = "🎵";
    public const string SceneGlyph = "🎬";
    public const string ScriptGlyph = "📜";
    public const string LockedGlyph = "🔒";
    public const string UnlockedGlyph = "🔓";
    public const string AlarmGlyph = "🚨";
    public const string ZoneGlyph = "📍";

    /// <summary>The shopping list's chip (later on 2026-10-12): the toolbar's cart.</summary>
    public const string ShoppingGlyph = ChatScreen.ShoppingToolGlyph;

    /// <summary>Every glyph, for the width test.</summary>
    public static readonly string[] Glyphs =
    [
        AreaGlyph, LightGlyph, SwitchGlyph, ToggleGlyph, FanGlyph, SensorGlyph, HumidityGlyph, BatteryGlyph, PowerGlyph, LightLevelGlyph,
        DoorGlyph, WindowGlyph, GarageGlyph, GateGlyph, MotionGlyph, SmokeGlyph, CoolGlyph, IdleGlyph, TvGlyph, SpeakerGlyph,
        MediaGlyph, SceneGlyph, ScriptGlyph, LockedGlyph, UnlockedGlyph, AlarmGlyph, ZoneGlyph, ShoppingGlyph,
    ];

    /// <summary>The short name's widest, in cells.</summary>
    public const int ShortCells = 10;

    /// <summary>A media title's widest, in cells.</summary>
    public const int TitleCells = 18;

    /// <summary>The cover classes whose opening asks first: a way into the house.</summary>
    public static readonly string[] GuardedCovers = ["garage", "gate", "door"];

    /// <summary>
    /// The chip for <paramref name="reference"/> from the live entities and the registries; <paramref name="stale"/> while the
    /// feed is not live. An item whose entity has not arrived yet is a dim chip with its catalog name and no action. An alias in
    /// <paramref name="aliases"/> (<see cref="HomeBarItems.AliasOf"/>) is the chip's <see cref="HomeChip.Label"/> and its short name;
    /// an emoji in <paramref name="glyphs"/> (<see cref="HomeBarItems.GlyphOf"/>, later on 2026-10-12) its <see cref="HomeChip.Glyph"/>,
    /// whatever the state. The shopping list's (<see cref="HomeBarItems.Shopping"/>) counts <paramref name="shoppingOpen"/>
    /// (<see cref="ShoppingChip"/>) and is never stale: Home Assistant's feed is not its own.
    /// </summary>
    public static HomeChip Build(string reference, IReadOnlyDictionary<string, HaLiveEntity> entities, HaCatalog? catalog, HomeChipMark mark, bool stale, IReadOnlyDictionary<string, string>? aliases = null, IReadOnlyDictionary<string, string>? glyphs = null, int? shoppingOpen = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(entities);
        if (!HomeBarItems.TryParse(reference, out var kind, out string id))
        {
            return Waiting(reference, ToggleGlyph, reference, mark);
        }

        if (kind == HomeItemKind.Divider)
        {
            // A dim │ (2026-10-13, the user's ask): no name, no value, no action, no span (UI.HomeBar.Build).
            return new HomeChip(reference, UI.ScreenPane.DividerGlyph, HomeBarText.DividerName, "", "", Theme.TabSeparator, HomeChipAction.None, null, "", Divider: true);
        }

        if (kind == HomeItemKind.Shopping)
        {
            var cart = ShoppingChip(HomeBarItems.Shopping, shoppingOpen) with { Mark = mark };
            return HomeBarItems.GlyphOf(glyphs, reference) is { } own ? cart with { Glyph = own } : cart;
        }

        var chip = kind == HomeItemKind.Area
            ? AreaChip(reference, id, entities, catalog)
            : entities.TryGetValue(id, out var entity) ? EntityChip(reference, entity, catalog) : Waiting(reference, GlyphFor(id, null), catalog?.NameOf(id) ?? id, mark);
        chip = chip with { Mark = mark };
        if (HomeBarItems.AliasOf(aliases, reference) is { } alias)
        {
            // An empty alias (later on 2026-10-12, the user's ask): the emoji alone, no name and no value.
            chip = alias.Length == 0 ? chip with { Label = "", Short = "", Bare = true } : chip with { Label = alias, Short = Clip(alias, ShortCells) };
        }

        if (HomeBarItems.GlyphOf(glyphs, reference) is { } glyph)
        {
            chip = chip with { Glyph = glyph };
        }

        return stale ? chip with { Style = Theme.DimText } : chip;
    }

    /// <summary>
    /// The glyph <paramref name="reference"/>'s chip gets without an emoji of the user's own (later on 2026-10-12): a room's
    /// <see cref="AreaGlyph"/>, else its kind's (<see cref="GlyphFor"/>) from <paramref name="entity"/>'s state (a TV's 📺 is in its
    /// device class), or from the catalog's state when none is given. Pure.
    /// </summary>
    public static string DefaultGlyph(string reference, HaCatalog? catalog, HaLiveEntity? entity = null)
    {
        if (!HomeBarItems.TryParse(reference, out var kind, out string id))
        {
            return ToggleGlyph;
        }

        return kind == HomeItemKind.Area ? AreaGlyph : kind == HomeItemKind.Shopping ? ShoppingGlyph : GlyphFor(id, entity ?? (catalog?.State(id) is { } state ? HaLiveEntity.From(state) : null));
    }

    /// <summary>
    /// The shopping list's chip (later on 2026-10-12, the user's ask): <c>🛒 3</c>, no name (an empty <see cref="HomeChip.Label"/> and
    /// short name, so <see cref="UI.HomeBar.ChipText"/> draws none), lit while anything is left to get and <see cref="Off"/> at
    /// <c>🛒 0</c>, which stays on the bar; <c>?</c> and dim while the list is off (<paramref name="open"/> null). A double-click or a
    /// right-click opens the list's pane (<see cref="HomeChipAction.Detail"/>). Pure.
    /// </summary>
    public static HomeChip ShoppingChip(string reference, int? open)
    {
        string name = HomeBarText.ShoppingName;
        if (open is not { } count)
        {
            return new HomeChip(reference, ShoppingGlyph, name, "", "?", Theme.DimText, HomeChipAction.Detail, null, HomeBarText.ShoppingNote(null), Label: "");
        }

        return new HomeChip(reference, ShoppingGlyph, name, "", count.ToString(CultureInfo.InvariantCulture), count > 0 ? Lit(Theme.Secondary) : Off,
            HomeChipAction.Detail, null, HomeBarText.ShoppingNote(count), Label: "");
    }

    private static HomeChip Waiting(string reference, string glyph, string name, HomeChipMark mark) =>
        new(reference, glyph, name, ShortOf(name, null), "", Theme.DimText, HomeChipAction.None, null, HomeBarText.NotYet(name), mark);

    /// <summary>
    /// The lights an area's chip counts and switches (2026-10-16, shared with the Web UI's rooms): its <see cref="HomeBarItems.AreaDomains"/>
    /// members the feed holds, groups and the unavailable left out. Pure.
    /// </summary>
    public static IReadOnlyList<HaLiveEntity> AreaLights(string areaId, IReadOnlyDictionary<string, HaLiveEntity> entities, HaCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(areaId);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.MembersOf(areaId, HomeBarItems.AreaDomains)
            .Select(id => entities.GetValueOrDefault(id))
            .OfType<HaLiveEntity>()
            .Where(e => !e.IsGroup && !e.Unavailable)
            .ToList();
    }

    private static HomeChip AreaChip(string reference, string areaId, IReadOnlyDictionary<string, HaLiveEntity> entities, HaCatalog? catalog)
    {
        string name = catalog?.Area(areaId)?.Name ?? areaId;
        if (catalog is null)
        {
            return Waiting(reference, AreaGlyph, name, HomeChipMark.None);
        }

        var lights = AreaLights(areaId, entities, catalog);
        int on = lights.Count(e => e.State == "on");
        string value = lights.Count == 0 ? "" : HomeBarText.Count(on, lights.Count);
        var call = new HaServiceCall("light", on > 0 ? "turn_off" : "turn_on", [], areaId);
        var style = on > 0 ? Lit(AverageTint(lights.Where(e => e.State == "on")) ?? Theme.Highlight) : Off;
        var action = lights.Count == 0 ? HomeChipAction.Detail : HomeChipAction.Call;
        string note = lights.Count == 0 ? HomeBarText.AreaNoLights(name) : HomeBarText.AreaNote(name, on, lights.Count);
        return new HomeChip(reference, AreaGlyph, name, ShortOf(name, null), value, style, action, action == HomeChipAction.Call ? call : null, note);
    }

    private static HomeChip EntityChip(string reference, HaLiveEntity entity, HaCatalog? catalog)
    {
        string name = entity.Name;
        string? area = catalog?.AreaOf(entity.Id) is { } areaId ? catalog.Area(areaId)?.Name : null;
        string shortName = ShortOf(name, area);
        string glyph = GlyphFor(entity.Id, entity);
        if (entity.Unavailable)
        {
            return new HomeChip(reference, glyph, name, shortName, "?", Theme.DimText, HomeChipAction.None, null, HomeBarText.Unavailable(name));
        }

        string state = entity.State;
        string id = entity.Id;
        switch (entity.Domain)
        {
            case "light":
            {
                // No brightness on the chip (2026-10-12, the user's call: it made the chips' widths change; the slab's colour says
                // it is on). The click's note still gives it.
                bool on = state == "on";
                string brightness = on && entity.Number("brightness") is { } b ? Percent(b / 255 * 100) : "";
                var style = on ? Lit(LightTint(entity) ?? Theme.Highlight) : Off;
                return Toggle(reference, glyph, name, shortName, "", style, HaServiceCall.On("light", "toggle", id), on) with { Note = HomeBarText.ToggleNote(name, on, brightness) };
            }

            case "switch" or "input_boolean":
            {
                bool on = state == "on";
                return Toggle(reference, glyph, name, shortName, "", on ? Lit(Theme.Secondary) : Off, HaServiceCall.On(entity.Domain, "toggle", id), on);
            }

            case "fan":
            {
                bool on = state == "on";
                string value = on && entity.Number("percentage") is { } p ? Percent(p) : "";
                return Toggle(reference, glyph, name, shortName, value, on ? Lit(Theme.Secondary) : Off, HaServiceCall.On("fan", "toggle", id), on);
            }

            case "media_player":
            {
                bool off = state is "off" or "standby";
                bool playing = state == "playing";
                // No title on the chip (2026-10-12, the user's call: an idle BRAVIA's "Smart TV" read as part of its renamed
                // name, and a title changed the chip's width); the click's note still gives it.
                string title = entity.Text("media_title") is { Length: > 0 } t ? t.Trim() : "";
                string value = off ? "" : playing ? HomeBarText.PlayingMark : state == "paused" ? HomeBarText.PausedMark : "";
                var call = off ? HaServiceCall.On("media_player", "turn_on", id) : HaServiceCall.On("media_player", "media_play_pause", id);
                var style = off ? Off : Lit(playing ? Theme.Primary : Theme.Tertiary);
                string note = HomeBarText.MediaNote(name, state, title, off);
                return new HomeChip(reference, glyph, name, shortName, value.Trim(), style, HomeChipAction.Call, call, note);
            }

            case "scene":
                return new HomeChip(reference, glyph, name, shortName, "", Off, HomeChipAction.Call, HaServiceCall.On("scene", "turn_on", id), HomeBarText.SceneNote(name));

            case "script":
            {
                bool running = state == "on";
                var call = HaServiceCall.On("script", running ? "turn_off" : "turn_on", id);
                return new HomeChip(reference, glyph, name, shortName, running ? HomeBarText.PlayingMark : "", running ? Lit(Theme.Secondary) : Off, HomeChipAction.Call, call, HomeBarText.ScriptNote(name, running));
            }

            case "cover":
            {
                bool open = state is "open" or "opening";
                string value = entity.Number("current_position") is { } position ? Percent(position) : HomeBarText.StateWord(state);
                bool guarded = GuardedCovers.Contains(entity.Text("device_class") ?? "", StringComparer.Ordinal);
                var call = HaServiceCall.On("cover", open ? "close_cover" : "open_cover", id);
                var action = !open && guarded ? HomeChipAction.Confirm : HomeChipAction.Call;
                var style = open ? guarded ? Alert(Theme.Warn) : Lit(Theme.Secondary) : Off;
                return new HomeChip(reference, glyph, name, shortName, value, style, action, call, HomeBarText.CoverNote(name, state, open));
            }

            case "lock":
            {
                switch (state)
                {
                    case "locked":
                        return new HomeChip(reference, glyph, name, shortName, "", Off, HomeChipAction.Confirm, HaServiceCall.On("lock", "unlock", id), HomeBarText.LockNote(name, state));
                    case "unlocked" or "open":
                        return new HomeChip(reference, glyph, name, shortName, "", Alert(Theme.Warn), HomeChipAction.Call, HaServiceCall.On("lock", "lock", id), HomeBarText.LockNote(name, state));
                    default:
                        var bad = state == "jammed" ? Alert(Theme.Bad) : Off;
                        return new HomeChip(reference, glyph, name, shortName, HomeBarText.StateWord(state), bad, HomeChipAction.Detail, null, HomeBarText.LockNote(name, state));
                }
            }

            case "alarm_control_panel":
            {
                bool armed = state.StartsWith("armed", StringComparison.Ordinal) || state is "triggered" or "arming" or "pending";
                string value = HomeBarText.AlarmWord(state);
                var style = state == "triggered" ? Alert(Theme.Bad) : armed ? Lit(Theme.Good) : Off;

                // A panel that wants a code cannot be disarmed with a click: its pane says so.
                bool needsCode = entity.Attributes.TryGetValue("code_format", out var format) && format.ValueKind == System.Text.Json.JsonValueKind.String;
                var action = armed && !needsCode ? HomeChipAction.Confirm : HomeChipAction.Detail;
                var call = armed && !needsCode ? HaServiceCall.On("alarm_control_panel", "alarm_disarm", id) : null;
                return new HomeChip(reference, glyph, name, shortName, value, style, action, call, HomeBarText.AlarmNote(name, value, armed, needsCode));
            }

            case "binary_sensor":
            {
                string deviceClass = entity.Text("device_class") ?? "";
                bool on = state == "on";
                bool alert = on && deviceClass is "door" or "window" or "garage_door" or "opening" or "moisture" or "smoke" or "gas" or "carbon_monoxide" or "problem" or "safety" or "tamper";
                string value = HomeBarText.BinaryWord(deviceClass, on);
                var style = alert ? Alert(deviceClass is "moisture" or "smoke" or "gas" or "carbon_monoxide" or "safety" ? Theme.Bad : Theme.Warn) : new Style(Theme.Ink);
                return new HomeChip(reference, glyph, name, shortName, value, style, HomeChipAction.Detail, null, HomeBarText.ValueNote(name, value));
            }

            case "sensor":
            {
                string value = SensorValue(entity);
                string deviceClass = entity.Text("device_class") ?? "";
                var style = deviceClass == "battery" && double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out double level) && level <= 20
                    ? new Style(level <= 10 ? Theme.Bad : Theme.Warn)
                    : new Style(Theme.Ink);
                return new HomeChip(reference, glyph, name, shortName, value, style, HomeChipAction.Detail, null, HomeBarText.ValueNote(name, value));
            }

            case "zone":
            {
                // A zone's state is how many people Home Assistant places in it (2026-10-12, the Zones tab): the count, lit while anyone is there.
                int people = int.TryParse(state, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Math.Max(0, n) : 0;
                string value = people.ToString(CultureInfo.InvariantCulture);
                return new HomeChip(reference, glyph, name, shortName, value, people > 0 ? new Style(Theme.Ink) : Off, HomeChipAction.Detail, null, HomeBarText.ZoneNote(name, people));
            }

            case "climate":
            {
                string action = entity.Text("hvac_action") ?? "";
                string current = entity.Number("current_temperature") is { } c ? Number(c) : "";
                string target = entity.Number("temperature") is { } t ? Number(t) : "";
                string value = HomeBarText.ClimateValue(current, target);
                var style = action == "heating" ? Lit(Theme.Warm) : action == "cooling" ? Lit(Theme.Secondary) : state == "off" ? Off : new Style(Theme.Ink);
                return new HomeChip(reference, glyph, name, shortName, value, style, HomeChipAction.Detail, null, HomeBarText.ClimateNote(name, state, action, value));
            }

            default:
            {
                bool on = state == "on";
                string value = state is "on" or "off" ? "" : Clip(HomeBarText.StateWord(state), TitleCells);
                return new HomeChip(reference, glyph, name, shortName, value, on ? Lit(Theme.Secondary) : state == "off" ? Off : new Style(Theme.Ink), HomeChipAction.Detail, null, HomeBarText.ValueNote(name, state));
            }
        }
    }

    private static HomeChip Toggle(string reference, string glyph, string name, string shortName, string value, Style style, HaServiceCall call, bool on) =>
        new(reference, glyph, name, shortName, value, style, HomeChipAction.Call, call, HomeBarText.ToggleNote(name, on, value));

    /// <summary>The glyph for an entity: by domain, then by device class. Pure.</summary>
    public static string GlyphFor(string entityId, HaLiveEntity? entity)
    {
        string domain = HaCatalog.DomainOf(entityId);
        string deviceClass = entity?.Text("device_class") ?? "";
        return domain switch
        {
            "light" => LightGlyph,
            "switch" => SwitchGlyph,
            "input_boolean" => ToggleGlyph,
            "fan" => FanGlyph,
            "scene" => SceneGlyph,
            "script" => ScriptGlyph,
            "lock" => entity?.State == "locked" ? LockedGlyph : entity is null ? LockedGlyph : UnlockedGlyph,
            "alarm_control_panel" => AlarmGlyph,
            "zone" => ZoneGlyph,
            "media_player" => deviceClass == "tv" ? TvGlyph : deviceClass is "speaker" or "receiver" ? SpeakerGlyph : MediaGlyph,
            "climate" => entity?.Text("hvac_action") switch { "heating" => HeatGlyph, "cooling" => CoolGlyph, _ => IdleGlyph },
            "cover" => deviceClass switch { "garage" => GarageGlyph, "gate" => GateGlyph, "door" => DoorGlyph, _ => WindowGlyph },
            "binary_sensor" => deviceClass switch
            {
                "door" or "opening" => DoorGlyph,
                "window" => WindowGlyph,
                "garage_door" => GarageGlyph,
                "moisture" => HumidityGlyph,
                "motion" or "occupancy" or "presence" => MotionGlyph,
                "smoke" or "gas" or "heat" => SmokeGlyph,
                "battery" => BatteryGlyph,
                "lock" => LockedGlyph,
                _ => ToggleGlyph,
            },
            "sensor" => deviceClass switch
            {
                "humidity" or "moisture" => HumidityGlyph,
                "battery" => BatteryGlyph,
                "power" or "energy" or "current" or "voltage" => PowerGlyph,
                "illuminance" => LightLevelGlyph,
                _ => SensorGlyph,
            },
            _ => ToggleGlyph,
        };
    }

    /// <summary>
    /// A light's colour, a lit chip's (<see cref="Lit"/>): its <c>rgb_color</c>, else its <c>hs_color</c>, else its colour temperature
    /// (<c>color_temp_kelvin</c>) as a white's warmth; null when it gives none (a dimmer: the theme's warm highlight). Pure.
    /// </summary>
    public static Color? LightTint(HaLiveEntity light)
    {
        ArgumentNullException.ThrowIfNull(light);
        if (light.Numbers("rgb_color") is { Length: 3 } rgb)
        {
            return new Color(Byte(rgb[0]), Byte(rgb[1]), Byte(rgb[2]));
        }

        if (light.Numbers("hs_color") is { Length: 2 } hs)
        {
            return FromHs(hs[0], hs[1]);
        }

        return light.Number("color_temp_kelvin") is { } kelvin ? FromKelvin(kelvin) : null;
    }

    /// <summary>
    /// An on chip (2026-10-12, the user's call, twice that day: first the full slabs of a warm bulb's orange or a theme's
    /// violet were overpowering, then the quiet fills that replaced them were still too much): <paramref name="color"/> as the
    /// words on the page, no fill, lifted by <see cref="Readable"/> when too dark to read. Pure for one theme.
    /// </summary>
    public static Style Lit(Color color) => new(Readable(color));

    /// <summary>
    /// An off chip (2026-10-12, with <see cref="Lit"/>): the theme's dim words on the page. Its emoji stays in full colour —
    /// Windows Terminal ignores faint and grey on a colour emoji — so the words alone say it is off.
    /// </summary>
    public static Style Off => new(Theme.Dim);

    /// <summary>
    /// An alert's chip (an open garage, gate or door, an unlocked or jammed lock, a triggered alarm, a leak or smoke):
    /// <paramref name="color"/> bold on the page, so with no fills it still stands out from a lit chip (2026-10-12; a full
    /// slab until then).
    /// </summary>
    public static Style Alert(Color color) => new(color, decoration: Decoration.Bold);

    /// <summary>The luminance below which a lit chip's colour is lifted (<see cref="Readable"/>): a deep blue scene vanished on the page.</summary>
    public const double ReadableFloor = 0.35;

    /// <summary>
    /// <paramref name="color"/>, or, when its <see cref="Luminance"/> is under <see cref="ReadableFloor"/>, the first tenth of
    /// the way toward <see cref="Theme.Ink"/> that reads (the ink itself at worst): the light's hue kept, its words legible on
    /// a dark page. Pure for one theme.
    /// </summary>
    public static Color Readable(Color color)
    {
        for (int tenths = 0; tenths < 10; tenths++)
        {
            var lifted = Theme.Mix(color, Theme.Ink, tenths / 10.0);
            if (Luminance(lifted) >= ReadableFloor)
            {
                return lifted;
            }
        }

        return Theme.Ink;
    }

    /// <summary>Relative luminance, 0 to 1 (Rec. 709 weights on the raw channels). Pure.</summary>
    public static double Luminance(Color color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;

    /// <summary>A hue (0–360) and saturation (0–100) at full value as a colour. Pure.</summary>
    public static Color FromHs(double hue, double saturation)
    {
        double h = ((hue % 360) + 360) % 360 / 60;
        double s = Math.Clamp(saturation, 0, 100) / 100;
        double c = s;
        double x = c * (1 - Math.Abs(h % 2 - 1));
        double m = 1 - c;
        (double r, double g, double b) = (int)h switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return new Color(Byte((r + m) * 255), Byte((g + m) * 255), Byte((b + m) * 255));
    }

    /// <summary>A colour temperature (1500–9000 K) as a white's tint: Tanner Helland's fit, clamped. Pure.</summary>
    public static Color FromKelvin(double kelvin)
    {
        double t = Math.Clamp(kelvin, 1000, 40000) / 100;
        double r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
        double g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
        double b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        return new Color(Byte(r), Byte(g), Byte(b));
    }

    private static Color? AverageTint(IEnumerable<HaLiveEntity> lights)
    {
        var tints = lights.Select(LightTint).OfType<Color>().ToList();
        if (tints.Count == 0)
        {
            return null;
        }

        return new Color(Byte(tints.Average(c => c.R)), Byte(tints.Average(c => c.G)), Byte(tints.Average(c => c.B)));
    }

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>A sensor's value: the state rounded (a whole number when it is one, else one decimal) and its unit. Pure.</summary>
    public static string SensorValue(HaLiveEntity sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        string unit = sensor.Text("unit_of_measurement") ?? "";
        string value = double.TryParse(sensor.State, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            ? Number(number)
            : Clip(HomeBarText.StateWord(sensor.State), TitleCells);
        return unit.Length == 0 ? value : value + (unit is "%" or "°C" or "°F" or "°" ? "" : " ") + unit;
    }

    /// <summary>A number as the bar writes it: whole when it is, else one decimal. Pure.</summary>
    public static string Number(double value)
    {
        double rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return rounded == Math.Floor(rounded) ? ((long)rounded).ToString(CultureInfo.InvariantCulture) : rounded.ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>A share as a whole percent. Pure.</summary>
    public static string Percent(double value) => Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 100).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// The short name a narrow bar uses: the name without its area's name ahead of it ("Kitchen Lamp" in the kitchen is
    /// "Lamp"), cut to <see cref="ShortCells"/>. Pure.
    /// </summary>
    public static string ShortOf(string name, string? area)
    {
        ArgumentNullException.ThrowIfNull(name);
        string trimmed = name.Trim();
        if (area is { Length: > 0 } && trimmed.Length > area.Length + 1 && trimmed.StartsWith(area + " ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[(area.Length + 1)..].Trim();
        }

        return Clip(trimmed, ShortCells);
    }

    /// <summary><paramref name="text"/> cut to <paramref name="cells"/> cells with an ellipsis. Pure.</summary>
    public static string Clip(string text, int cells)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (TextCells.Width(text) <= cells)
        {
            return text;
        }

        var kept = new System.Text.StringBuilder();
        int used = 0;
        var walker = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (walker.MoveNext())
        {
            string element = walker.GetTextElement();
            int width = TextCells.Width(element);
            if (used + width > cells - 1)
            {
                break;
            }

            kept.Append(element);
            used += width;
        }

        return kept.ToString().TrimEnd() + "…";
    }
}
