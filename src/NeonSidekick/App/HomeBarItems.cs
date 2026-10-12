using NeonSidekick.HomeAssistant;

namespace NeonSidekick.App;

/// <summary>What one home bar item names: a whole area (room), or one entity.</summary>
public enum HomeItemKind
{
    Area,
    Entity,
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

        if (text.StartsWith(EntityPrefix, StringComparison.OrdinalIgnoreCase))
        {
            id = text[EntityPrefix.Length..].Trim().ToLowerInvariant();
            return id.IndexOf('.', StringComparison.Ordinal) is > 0 and int dot && dot < id.Length - 1;
        }

        return false;
    }

    /// <summary>A reference in its one spelling (the prefix lower case, an entity id lower case), or null when it is none. Pure.</summary>
    public static string? Normalize(string? reference) =>
        TryParse(reference, out var kind, out string id) ? (kind == HomeItemKind.Area ? Area(id) : Entity(id)) : null;

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
            else if (catalog is not null)
            {
                ids.UnionWith(catalog.MembersOf(id, AreaDomains));
            }
        }

        return ids;
    }

    /// <summary>
    /// The <c>Show home bar</c> row's value: <c>off</c> with nothing on it, else how many items and the first few names
    /// (an area's or an entity's from <paramref name="catalog"/>, the id made readable without one: the settings list has none). Pure.
    /// </summary>
    public static string Value(IReadOnlyList<string>? saved, HaCatalog? catalog)
    {
        var items = Resolve(saved);
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

        return catalog is null ? Readable(id) : catalog.NameOf(id);
    }

    /// <summary>An id as words, for where no names are at hand (the settings row): <c>light.desk_lamp</c> → <c>desk lamp</c>, <c>living_room</c> → <c>living room</c>. Pure.</summary>
    public static string Readable(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        int dot = id.IndexOf('.', StringComparison.Ordinal);
        return (dot >= 0 ? id[(dot + 1)..] : id).Replace('_', ' ');
    }
}
