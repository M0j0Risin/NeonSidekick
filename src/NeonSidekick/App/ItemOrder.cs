namespace NeonSidekick.App;

/// <summary>
/// The order of a checklist's items (2026-10-12, the user's ask: "reuse this idea" — the home bar picker's move keys — on
/// <c>Show toolbar</c> and <c>Show performance bar</c>): every id of the list's <c>Names</c> in the user's order, checked or not,
/// so an item keeps its place while it is off. Saved apart from the checks (<see cref="Settings.AppSettingsData.ToolbarOrder"/>,
/// <see cref="Settings.AppSettingsData.PerformanceBarOrder"/>); null is <c>Names</c>' own order, the one the bars always had, and
/// what the checklist's <c>⊡ default</c> puts back. A display setting, so an unknown word is dropped without a warning, as the
/// checks' <c>Resolve</c> does. Since 2026-10-13 (the user's ask: any item of the hint bar and the tool bar pinned left or right,
/// the sort free within each side) an order may be split in two groups, <see cref="Sided"/>: the toolbar's path pinned last and
/// the model passing it (2026-10-12) went with that. Later that day (the user's ask: a divider on the home bar and the tool bar)
/// an order may hold dividers, <c>divider:1</c>, <c>divider:2</c>…, each its own id so there can be any number (<see cref="IsDivider"/>);
/// only a list that asks for them keeps them (<c>keepDividers</c>). Pure.
/// </summary>
public static class ItemOrder
{
    /// <summary>The start of a divider's id (2026-10-13): <c>divider:</c> and a number from 1. Pinned.</summary>
    public const string DividerPrefix = "divider:";

    /// <summary>What a divider draws, dim, on the home bar and the tool bar (2026-10-13, the user's pick). Pinned.</summary>
    public const string DividerGlyph = UI.ScreenPane.DividerGlyph;

    /// <summary>Whether <paramref name="id"/> is a divider's: <see cref="DividerPrefix"/> and a whole number from 1, nothing else.</summary>
    public static bool IsDivider(string? id) =>
        id is not null && id.StartsWith(DividerPrefix, StringComparison.Ordinal) && id.Length > DividerPrefix.Length && id.Length <= DividerPrefix.Length + 4
        && id.AsSpan(DividerPrefix.Length).IndexOfAnyExceptInRange('0', '9') < 0 && id[DividerPrefix.Length] != '0';

    /// <summary>A new divider's id among <paramref name="existing"/>: the lowest number not yet used (2026-10-13).</summary>
    public static string NextDivider(IEnumerable<string> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var used = existing.Where(IsDivider).ToHashSet(StringComparer.Ordinal);
        int n = 1;
        while (used.Contains(DividerPrefix + n.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            n++;
        }

        return DividerPrefix + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The full order <paramref name="saved"/> names over <paramref name="names"/>: its known ids (trimmed, any case, the first of
    /// a repeat) in their saved order, then each id it leaves out put in after the one before it in <paramref name="names"/> (an
    /// item a later version adds lands beside its neighbours, not at the end). Null is <paramref name="defaultOrder"/>, else
    /// <paramref name="names"/> as they are. With <paramref name="keepDividers"/> (2026-10-13) a divider's id is known too, in its place.
    /// </summary>
    public static List<string> Resolve(IReadOnlyList<string>? saved, IReadOnlyList<string> names, bool keepDividers = false, IReadOnlyList<string>? defaultOrder = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (saved is null && defaultOrder is not null)
        {
            return [.. defaultOrder];
        }

        var order = new List<string>(names.Count);
        if (saved is not null)
        {
            // A null in a hand-edited list is skipped: this runs on the pane's tick, where a throw repeats.
            foreach (string word in saved.OfType<string>().Select(w => w.Trim().ToLowerInvariant()))
            {
                if ((names.Contains(word, StringComparer.Ordinal) || (keepDividers && IsDivider(word))) && !order.Contains(word, StringComparer.Ordinal))
                {
                    order.Add(word);
                }
            }
        }

        for (int i = 0; i < names.Count; i++)
        {
            if (order.Contains(names[i], StringComparer.Ordinal))
            {
                continue;
            }

            int after = i == 0 ? -1 : order.IndexOf(names[i - 1]);
            order.Insert(after + 1, names[i]);
        }

        return order;
    }

    /// <summary>
    /// What saves for <paramref name="order"/>: null when it is the default order — <paramref name="defaultOrder"/>, else
    /// <paramref name="names"/>' own (so it follows a later default) — else the list.
    /// </summary>
    public static List<string>? Save(IReadOnlyList<string> order, IReadOnlyList<string> names, IReadOnlyList<string>? defaultOrder = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(names);
        return order.SequenceEqual(defaultOrder ?? names, StringComparer.Ordinal) ? null : order.ToList();
    }

    /// <summary>
    /// <paramref name="order"/> with the item at <paramref name="index"/> moved by <paramref name="delta"/> (<see cref="HomeBarItems.Top"/>,
    /// <see cref="HomeBarItems.Bottom"/> or a step, <see cref="HomeBarItems.Move"/>'s), and where it landed.
    /// </summary>
    public static (List<string> Order, int Index) Move(IReadOnlyList<string> order, int index, int delta) => HomeBarItems.Move(order, index, delta);

    /// <summary>
    /// An order in its two groups (2026-10-13, the user's ask): the items pinned left in their order, then the ones pinned right in theirs.
    /// <see cref="All"/> is the order that saves (the left group, then the right one), so each group's order is the saved list's; an index
    /// into it is the one the checklist's rows hand back (its headings aside).
    /// </summary>
    public readonly record struct Sided(IReadOnlyList<string> Left, IReadOnlyList<string> Right)
    {
        /// <summary>The left group, then the right one: the order that saves.</summary>
        public List<string> All => [.. Left, .. Right];

        /// <summary>Whether <paramref name="id"/> is pinned right.</summary>
        public bool IsRight(string id) => Right.Contains(id, StringComparer.Ordinal);
    }

    /// <summary><paramref name="order"/> split by side: the ids in <paramref name="right"/> make the right group, the rest the left, each in <paramref name="order"/>'s order.</summary>
    public static Sided Split(IReadOnlyList<string> order, IReadOnlySet<string> right)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(right);
        return new Sided(order.Where(id => !right.Contains(id)).ToList(), order.Where(right.Contains).ToList());
    }

    /// <summary>
    /// <paramref name="groups"/> with the item at <paramref name="index"/> (into <see cref="Sided.All"/>) moved by <paramref name="delta"/>
    /// within its group (2026-10-13): a step past the group's edge crosses into the other group — a step down off the left group's last
    /// item makes it the right group's first, a step up off the right group's first makes it the left group's last, the item keeping
    /// its index; <see cref="HomeBarItems.Top"/> and <see cref="HomeBarItems.Bottom"/> go to the top or the bottom of its own group.
    /// An index out of range moves nothing.
    /// </summary>
    public static (Sided Groups, int Index) MoveInGroups(Sided groups, int index, int delta)
    {
        var left = groups.Left.ToList();
        var right = groups.Right.ToList();
        if (index < 0 || index >= left.Count + right.Count || delta == 0)
        {
            return (new Sided(left, right), index);
        }

        if (index < left.Count)
        {
            if (index == left.Count - 1 && delta == 1)
            {
                right.Insert(0, left[index]);
                left.RemoveAt(index);
                return (new Sided(left, right), index);
            }

            var (moved, at) = HomeBarItems.Move(left, index, delta);
            return (new Sided(moved, right), at);
        }

        int inRight = index - left.Count;
        if (inRight == 0 && delta == -1)
        {
            left.Add(right[0]);
            right.RemoveAt(0);
            return (new Sided(left, right), index);
        }

        var (movedRight, atRight) = HomeBarItems.Move(right, inRight, delta);
        return (new Sided(left, movedRight), left.Count + atRight);
    }

    /// <summary>
    /// <paramref name="groups"/> with the item at <paramref name="index"/> pinned to the other side (2026-10-13, Shift+←/→): to the right
    /// group's start, or to the left group's end — next to the gap either way — and where it landed. An item already on that side, or
    /// an index out of range, stays.
    /// </summary>
    public static (Sided Groups, int Index) ToSide(Sided groups, int index, bool toRight)
    {
        var left = groups.Left.ToList();
        var right = groups.Right.ToList();
        if (index < 0 || index >= left.Count + right.Count || (index < left.Count) != toRight)
        {
            return (new Sided(left, right), index);
        }

        if (toRight)
        {
            right.Insert(0, left[index]);
            left.RemoveAt(index);
            return (new Sided(left, right), left.Count);
        }

        int at = index - left.Count;
        left.Add(right[at]);
        right.RemoveAt(at);
        return (new Sided(left, right), left.Count - 1);
    }

    /// <summary>
    /// The ids <paramref name="saved"/> pins right over <paramref name="names"/> (2026-10-13): <paramref name="defaults"/> when null, the
    /// known ids (trimmed, any case) otherwise — an empty list pins nothing right. A display setting: an unknown word is dropped.
    /// With <paramref name="keepDividers"/> (2026-10-13) a divider's id is known too.
    /// </summary>
    public static IReadOnlySet<string> ResolveRight(IReadOnlyList<string>? saved, IReadOnlyList<string> names, IReadOnlyList<string> defaults, bool keepDividers = false)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(defaults);
        if (saved is null)
        {
            return defaults.ToHashSet(StringComparer.Ordinal);
        }

        var wanted = saved.OfType<string>().Select(w => w.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var right = names.Where(wanted.Contains).ToHashSet(StringComparer.Ordinal);
        if (keepDividers)
        {
            right.UnionWith(wanted.Where(IsDivider));
        }

        return right;
    }

    /// <summary>
    /// What saves for the right group <paramref name="right"/>: null for exactly <paramref name="defaults"/> (so it follows a later
    /// default), else the ids in <paramref name="names"/> order, then its dividers (2026-10-13) in its own.
    /// </summary>
    public static List<string>? SaveRight(IEnumerable<string> right, IReadOnlyList<string> names, IReadOnlyList<string> defaults)
    {
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(defaults);
        var list = right.ToList();
        var set = list.ToHashSet(StringComparer.Ordinal);
        return set.SetEquals(defaults) ? null : [.. names.Where(set.Contains), .. list.Where(IsDivider)];
    }

    /// <summary>
    /// <paramref name="groups"/> with a new divider (<see cref="NextDivider"/>) right after the item at <paramref name="index"/> (into
    /// <see cref="Sided.All"/>), on its side (2026-10-13, the checklist's <c>+ divider</c>); at the left group's end with no items. Where it landed.
    /// </summary>
    public static (Sided Groups, int Index) AddDivider(Sided groups, int index)
    {
        var left = groups.Left.ToList();
        var right = groups.Right.ToList();
        string divider = NextDivider([.. left, .. right]);
        if (index >= left.Count && index < left.Count + right.Count)
        {
            right.Insert(index - left.Count + 1, divider);
            return (new Sided(left, right), index + 1);
        }

        int at = index < 0 || index >= left.Count ? left.Count : index + 1;
        left.Insert(at, divider);
        return (new Sided(left, right), at);
    }

    /// <summary>
    /// The items of one drawn run with the dividers the user can see (2026-10-13, the shared rule): a divider stands only between two
    /// drawn items — none at either end, none beside another. <paramref name="drawn"/> says which ids draw (a divider never asks). Pure.
    /// </summary>
    public static List<string> ShownDividers(IEnumerable<string> run, Func<string, bool> drawn)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(drawn);
        var shown = new List<string>();
        bool pending = false;
        string? divider = null;
        foreach (string id in run)
        {
            if (IsDivider(id))
            {
                if (shown.Count > 0 && !pending)
                {
                    pending = true;
                    divider = id;
                }

                continue;
            }

            if (!drawn(id))
            {
                continue;
            }

            if (pending)
            {
                shown.Add(divider!);
                pending = false;
            }

            shown.Add(id);
        }

        return shown;
    }
}
