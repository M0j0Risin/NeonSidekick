using NeonSidekick.Emojis;
using NeonSidekick.HomeAssistant;

namespace NeonSidekick.App;

/// <summary>What one home bar item names: a whole area (room), one entity, or the app's own shopping list (later on 2026-10-12).</summary>
public enum HomeItemKind
{
    Area,
    Entity,
    Shopping,
    Divider,   // 2026-10-13, the user's ask: a dim │ between chips, any number of them (divider:1, divider:2…)
}

/// <summary>What <c>/homebar</c> saves (<see cref="HomeBarItems.Toggle"/>): the items shown (empty: hidden), the ones a later show brings back, and whether there is nothing to show yet (the picker opens instead).</summary>
public sealed record HomeToggle(List<string> Items, List<string>? LastItems, bool NeedsPicker = false);

/// <summary>
/// The home bar's items (2026-10-12, the user's ask: a bar of Home Assistant rooms and devices under the perf bar, the user
/// choosing what is on it and in what order): one reference per chip, <c>area:&lt;area_id&gt;</c> or
/// <c>entity:&lt;entity_id&gt;</c>, saved in <see cref="Settings.AppSettingsData.HomeBarItems"/>. The perf bar's shape with one
/// difference: <b>the saved order is the bar's order</b>, which the picker's move keys change (<see cref="Move"/>). Null or
/// empty is no bar: there is no default, a home is the user's own. <see cref="Resolve"/> is the one place the saved list
/// becomes the items; a display setting, so a malformed reference is dropped without a warning (the pane's tick reads it).
/// </summary>
public static class HomeBarItems
{
    public const string AreaPrefix = "area:";

    public const string EntityPrefix = "entity:";

    public const string ShoppingPrefix = "shopping:";

    /// <summary>
    /// The shopping list's item (later on 2026-10-12, the user's ask): 🛒 and the lines still to get, the app's own list rather than
    /// Home Assistant's, so it needs no Home Assistant. It has no name to change (<see cref="IsNameless"/>); its emoji can be.
    /// </summary>
    public const string Shopping = ShoppingPrefix + "list";

    /// <summary>The domains an area chip counts and switches: its lights.</summary>
    public static readonly string[] AreaDomains = ["light"];

    /// <summary>The reference for an area.</summary>
    public static string Area(string areaId) => AreaPrefix + areaId;

    /// <summary>The reference for an entity.</summary>
    public static string Entity(string entityId) => EntityPrefix + entityId;

    /// <summary>
    /// A reference's kind and id: <c>area:kitchen</c>, <c>entity:light.desk</c> (the prefix any case, the id trimmed; an
    /// entity id needs its dot). False for anything else. Pure.
    /// </summary>
    public static bool TryParse(string? reference, out HomeItemKind kind, out string id)
    {
        kind = HomeItemKind.Entity;
        id = "";
        string text = reference?.Trim() ?? "";
        if (text.StartsWith(AreaPrefix, StringComparison.OrdinalIgnoreCase))
        {
            kind = HomeItemKind.Area;
            id = text[AreaPrefix.Length..].Trim();
            return id.Length > 0;
        }

        if (text.StartsWith(ItemOrder.DividerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            kind = HomeItemKind.Divider;
            id = text[ItemOrder.DividerPrefix.Length..].Trim();
            return ItemOrder.IsDivider(ItemOrder.DividerPrefix + id);
        }

        if (text.StartsWith(ShoppingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            kind = HomeItemKind.Shopping;
            id = text[ShoppingPrefix.Length..].Trim().ToLowerInvariant();
            return id == "list";
        }

        if (text.StartsWith(EntityPrefix, StringComparison.OrdinalIgnoreCase))
        {
            id = text[EntityPrefix.Length..].Trim().ToLowerInvariant();
            return id.IndexOf('.', StringComparison.Ordinal) is > 0 and int dot && dot < id.Length - 1;
        }

        return false;
    }

    /// <summary>A reference in its one spelling (the prefix lower case, an entity id lower case), or null when it is none. Pure.</summary>
    public static string? Normalize(string? reference) =>
        TryParse(reference, out var kind, out string id) ? kind switch { HomeItemKind.Area => Area(id), HomeItemKind.Shopping => Shopping, HomeItemKind.Divider => ItemOrder.DividerPrefix + id, _ => Entity(id) } : null;

    /// <summary>An item with no name to change (later on 2026-10-12, the user's call): the shopping list's 🛒 shows its count alone; a divider (2026-10-13) none at all. Pure.</summary>
    public static bool IsNameless(string reference) => TryParse(reference, out var kind, out _) && kind is HomeItemKind.Shopping or HomeItemKind.Divider;

    /// <summary>Whether <paramref name="reference"/> is a divider's (2026-10-13): a dim │, no chip, no name, no emoji. Pure.</summary>
    public static bool IsDivider(string reference) => TryParse(reference, out var kind, out _) && kind == HomeItemKind.Divider;

    /// <summary><paramref name="items"/> with a new divider at the end (2026-10-13, the bar tab's <c>+ Add divider</c>; <see cref="ItemOrder.NextDivider"/>). Pure.</summary>
    public static List<string> AddDivider(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = Resolve(items).ToList();
        list.Add(ItemOrder.NextDivider(list));
        return list;
    }

    /// <summary>The items <paramref name="saved"/> names, in its order, each once; malformed ones and nulls skipped. Pure.</summary>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string>? saved)
    {
        if (saved is null || saved.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<string>(saved.Count);
        foreach (string? one in saved)
        {
            if (Normalize(one) is { } reference && seen.Add(reference))
            {
                items.Add(reference);
            }
        }

        return items;
    }

    /// <summary>Whether <paramref name="items"/> holds <paramref name="reference"/>.</summary>
    public static bool Contains(IReadOnlyList<string> items, string reference) => Normalize(reference) is { } r && items.Contains(r, StringComparer.Ordinal);

    /// <summary><paramref name="items"/> with <paramref name="reference"/> added at the end, or taken out when it is there. Pure.</summary>
    public static List<string> Flip(IReadOnlyList<string> items, string reference)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = Resolve(items).ToList();
        if (Normalize(reference) is not { } r)
        {
            return list;
        }

        if (!list.Remove(r))
        {
            list.Add(r);
        }

        return list;
    }

    /// <summary>To the top (<see cref="int.MinValue"/>), to the bottom (<see cref="int.MaxValue"/>), or so many places down (positive) or up (negative).</summary>
    public const int Top = int.MinValue;

    public const int Bottom = int.MaxValue;

    /// <summary>
    /// <paramref name="items"/> with the one at <paramref name="index"/> moved by <paramref name="delta"/> (<see cref="Top"/>,
    /// <see cref="Bottom"/>, or a step clamped to the ends), and where it landed. An index out of range moves nothing. Pure.
    /// </summary>
    public static (List<string> Items, int Index) Move(IReadOnlyList<string> items, int index, int delta)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        if (index < 0 || index >= list.Count)
        {
            return (list, index);
        }

        int target = delta switch
        {
            Top => 0,
            Bottom => list.Count - 1,
            _ => (int)Math.Clamp((long)index + delta, 0, list.Count - 1),
        };
        string moved = list[index];
        list.RemoveAt(index);
        list.Insert(target, moved);
        return (list, target);
    }

    /// <summary><paramref name="items"/> without the one at <paramref name="index"/> (out of range: unchanged). Pure.</summary>
    public static List<string> Remove(IReadOnlyList<string> items, int index)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        if (index >= 0 && index < list.Count)
        {
            list.RemoveAt(index);
        }

        return list;
    }

    /// <summary>
    /// What <c>/homebar</c> saves, pure (<see cref="PerfBarMode.Toggle"/>'s shape): bare, the bar hidden while it shows (its
    /// items kept in <paramref name="last"/>), else shown again with the last ones; <c>on</c> shows, <c>off</c> hides; null
    /// for any other word, the usage error. Showing with nothing ever picked is <see cref="HomeToggle.NeedsPicker"/>.
    /// </summary>
    public static HomeToggle? Toggle(string args, IReadOnlyList<string>? items, IReadOnlyList<string>? last)
    {
        ArgumentNullException.ThrowIfNull(args);
        var shown = Resolve(items);
        string word = args.Trim();
        if (word.Length == 0)
        {
            return shown.Count > 0 ? Hide() : Show();
        }

        if (string.Equals(word, HomeBarText.OnWord, StringComparison.OrdinalIgnoreCase))
        {
            return Show();
        }

        return string.Equals(word, HomeBarText.OffWord, StringComparison.OrdinalIgnoreCase) ? Hide() : null;

        HomeToggle Hide() => new([], shown.Count > 0 ? shown.ToList() : last?.ToList());

        HomeToggle Show()
        {
            if (shown.Count > 0)
            {
                return new(shown.ToList(), last?.ToList());
            }

            var back = Resolve(last);
            return back.Count > 0 ? new(back.ToList(), back.ToList()) : new([], last?.ToList(), NeedsPicker: true);
        }
    }

    /// <summary>
    /// The entity ids the bar keeps current for <paramref name="items"/>: each entity, and each area's lights
    /// (<see cref="AreaDomains"/>, from the registries; none until they are read). Pure.
    /// </summary>
    public static IReadOnlySet<string> EntityIds(IReadOnlyList<string> items, HaCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(items);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string item in items)
        {
            if (!TryParse(item, out var kind, out string id))
            {
                continue;
            }

            if (kind == HomeItemKind.Entity)
            {
                ids.Add(id);
            }
            else if (kind == HomeItemKind.Area && catalog is not null)
            {
                ids.UnionWith(catalog.MembersOf(id, AreaDomains));
            }
        }

        return ids;
    }

    /// <summary>
    /// The <c>Home bar</c> row's value: <c>off</c> with nothing on it, else how many items and the first few names
    /// (an area's or an entity's from <paramref name="catalog"/>, the id made readable without one: the settings list has none). Pure.
    /// </summary>
    public static string Value(IReadOnlyList<string>? saved, HaCatalog? catalog)
    {
        var items = Resolve(saved).Where(i => !IsDivider(i)).ToList();   // a divider is no item to count (2026-10-13)
        return items.Count == 0 ? HomeBarText.NoItems : HomeBarText.ItemsValue(items.Select(i => NameOf(i, catalog)).ToList());
    }

    /// <summary>An item's name: the area's, the entity's, else its id made readable (<see cref="Readable"/>). Pure.</summary>
    public static string NameOf(string reference, HaCatalog? catalog)
    {
        if (!TryParse(reference, out var kind, out string id))
        {
            return reference;
        }

        if (kind == HomeItemKind.Area)
        {
            return catalog?.Area(id)?.Name ?? Readable(id);
        }

        if (kind == HomeItemKind.Shopping)
        {
            return HomeBarText.ShoppingName;
        }

        if (kind == HomeItemKind.Divider)
        {
            return HomeBarText.DividerName;
        }

        return catalog is null ? Readable(id) : catalog.NameOf(id);
    }

    /// <summary>
    /// The alias the chip of <paramref name="reference"/> shows instead of its name (2026-10-12, the user's ask: "Den PC Monitor"
    /// as "PC"; <see cref="Settings.AppSettingsData.HomeBarAliases"/>), trimmed; empty for the emoji alone (later on 2026-10-12, the
    /// user's ask: no name and no value, a room's 🏠 without its 2/5); null for none. Pure.
    /// </summary>
    public static string? AliasOf(IReadOnlyDictionary<string, string>? aliases, string reference) =>
        aliases is not null && !IsNameless(reference) && Normalize(reference) is { } r && aliases.TryGetValue(r, out string? alias) && alias is not null ? alias.Trim() : null;

    /// <summary>
    /// <paramref name="aliases"/> with <paramref name="reference"/>'s alias set to <paramref name="alias"/> (blank: the emoji alone,
    /// later on 2026-10-12; until then blank took it off), or taken out when it is null or the item's own <paramref name="name"/>
    /// (any case); null when none is left, as the setting's default. A malformed reference, or a nameless one
    /// (<see cref="IsNameless"/>), changes nothing. Pure.
    /// </summary>
    public static Dictionary<string, string>? Rename(IReadOnlyDictionary<string, string>? aliases, string reference, string? alias, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var next = aliases is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(aliases, StringComparer.Ordinal);
        if (!IsNameless(reference) && Normalize(reference) is { } r)
        {
            string? text = alias?.Trim();
            if (text is null || string.Equals(text, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                next.Remove(r);
            }
            else
            {
                next[r] = text;
            }
        }

        return next.Count > 0 ? next : null;
    }

    /// <summary>
    /// The emoji of the user's own the chip of <paramref name="reference"/> shows in place of its glyph (later on 2026-10-12, the
    /// user's ask; <see cref="Settings.AppSettingsData.HomeBarGlyphs"/>): one emoji (<see cref="AvatarRules.Normalize"/>, a
    /// hand-edited word ignored); null for none. Pure.
    /// </summary>
    public static string? GlyphOf(IReadOnlyDictionary<string, string>? glyphs, string reference) =>
        glyphs is not null && Normalize(reference) is { } r && glyphs.TryGetValue(r, out string? glyph) && AvatarRules.Normalize(glyph) is { Length: > 0 } emoji ? emoji : null;

    /// <summary>
    /// <paramref name="glyphs"/> with <paramref name="reference"/>'s emoji set to <paramref name="emoji"/>, or taken out when it is
    /// blank, not one emoji, or the item's own <paramref name="defaultGlyph"/>; null when none is left, as the setting's default
    /// (<see cref="Rename"/>'s shape). A malformed reference changes nothing. Pure.
    /// </summary>
    public static Dictionary<string, string>? SetGlyph(IReadOnlyDictionary<string, string>? glyphs, string reference, string? emoji, string defaultGlyph)
    {
        ArgumentNullException.ThrowIfNull(defaultGlyph);
        var next = glyphs is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(glyphs, StringComparer.Ordinal);
        if (Normalize(reference) is { } r)
        {
            string text = AvatarRules.Normalize(emoji);
            if (text.Length == 0 || string.Equals(text, defaultGlyph.Trim(), StringComparison.Ordinal))
            {
                next.Remove(r);
            }
            else
            {
                next[r] = text;
            }
        }

        return next.Count > 0 ? next : null;
    }

    /// <summary>
    /// The item of <paramref name="items"/> that <paramref name="item"/> names (<c>/homebar name</c>, <c>tap</c>, <c>show</c>): its
    /// reference, its alias, its name or its bare id, in that order, any case; null for none. Pure.
    /// </summary>
    public static string? Find(IReadOnlyList<string> items, string item, HaCatalog? catalog, IReadOnlyDictionary<string, string>? aliases)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(item);
        string text = item.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        items = items.Where(i => !IsDivider(i)).ToList();   // a divider is never the one named (2026-10-13)
        if (Normalize(text) is { } reference && items.Contains(reference, StringComparer.Ordinal))
        {
            return reference;
        }

        return items.FirstOrDefault(i => AliasOf(aliases, i) is { Length: > 0 } alias && string.Equals(alias, text, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault(i => string.Equals(NameOf(i, catalog), text, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault(i => TryParse(i, out _, out string id) && string.Equals(id, text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An id as words, for where no names are at hand (the settings row): <c>light.desk_lamp</c> → <c>desk lamp</c>, <c>living_room</c> → <c>living room</c>. Pure.</summary>
    public static string Readable(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        int dot = id.IndexOf('.', StringComparison.Ordinal);
        return (dot >= 0 ? id[(dot + 1)..] : id).Replace('_', ' ');
    }
}
