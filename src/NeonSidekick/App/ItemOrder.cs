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
    /// An order in its groups (2026-10-13, the user's ask): the items pinned left in their order, then the ones pinned right in theirs.
    /// Later that day (the user's ask: the home bar pinned left, center or right) a third group between the two where a list asks for
    /// one, <see cref="Center"/>; null is none, the hint bar's and the tool bar's. <see cref="All"/> is the order that saves (the left
    /// group, the center, the right one), so each group's order is the saved list's; an index into it is the one the checklist's rows
    /// hand back (its headings aside).
    /// </summary>
    public readonly record struct Sided(IReadOnlyList<string> Left, IReadOnlyList<string> Right, IReadOnlyList<string>? Center = null)
    {
        /// <summary>Whether the order has a center group (the home bar's, later on 2026-10-13).</summary>
        public bool HasCenter => Center is not null;

        /// <summary>The left group, the center, then the right one: the order that saves.</summary>
        public List<string> All => [.. Left, .. Center ?? [], .. Right];

        /// <summary>The groups left to right: two, or three with a center.</summary>
        public IReadOnlyList<IReadOnlyList<string>> Groups => Center is { } center ? [Left, center, Right] : [Left, Right];

        /// <summary>Whether <paramref name="id"/> is pinned right.</summary>
        public bool IsRight(string id) => Right.Contains(id, StringComparer.Ordinal);

        /// <summary>Whether <paramref name="id"/> is pinned center.</summary>
        public bool IsCenter(string id) => Center?.Contains(id, StringComparer.Ordinal) == true;

        /// <summary>Whether <paramref name="other"/> holds the same groups, each in the same order.</summary>
        public bool SameAs(Sided other) =>
            Groups.Count == other.Groups.Count && Groups.Zip(other.Groups).All(pair => pair.First.SequenceEqual(pair.Second, StringComparer.Ordinal));
    }

    /// <summary>
    /// <paramref name="groups"/> with only the ids <paramref name="shown"/> keeps, each group in its order (2026-10-22, the user's ask: a
    /// Mac's Tool bar checklist leaves out 🧩 and 📅, <see cref="ToolbarItems.IsHere(string, bool)"/>). <see cref="Reinsert"/> puts the rest back. Pure.
    /// </summary>
    public static Sided Only(Sided groups, Func<string, bool> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        return new Sided(groups.Left.Where(shown).ToList(), groups.Right.Where(shown).ToList(), groups.Center?.Where(shown).ToList());
    }

    /// <summary>
    /// <paramref name="after"/> (an edit of <see cref="Only"/>'s view of <paramref name="before"/>) with every id <paramref name="shown"/> left
    /// out put back (2026-10-22): on the group it was in, right after the id it followed there (first in the group with none), so a
    /// hidden item never moves and an order put back as it was saves as it was. Pure.
    /// </summary>
    public static Sided Reinsert(Sided before, Sided after, Func<string, bool> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        var groups = before.Groups.Zip(after.Groups, (was, now) =>
        {
            var result = now.ToList();
            int front = 0;            // where a hidden id with nothing before it goes: after the ones already put there
            string? anchor = null;    // the last id of this group, before the edit, that the result holds
            foreach (string id in was)
            {
                if (!shown(id))
                {
                    int at = anchor is null ? front++ : result.IndexOf(anchor) + 1;
                    result.Insert(at, id);
                    anchor = id;
                }
                else if (result.Contains(id, StringComparer.Ordinal))
                {
                    anchor = id;
                }
            }

            return (IReadOnlyList<string>)result;
        }).ToList();
        return after.HasCenter ? new Sided(groups[0], groups[2], groups[1]) : new Sided(groups[0], groups[1]);
    }

    /// <summary><paramref name="order"/> split by side: the ids in <paramref name="right"/> make the right group, the rest the left, each in <paramref name="order"/>'s order.</summary>
    public static Sided Split(IReadOnlyList<string> order, IReadOnlySet<string> right)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(right);
        return new Sided(order.Where(id => !right.Contains(id)).ToList(), order.Where(right.Contains).ToList());
    }

    /// <summary>
    /// <paramref name="order"/> split in three (later on 2026-10-13, the home bar): the ids in <paramref name="left"/> the left group, the
    /// ids in <paramref name="right"/> (and not in <paramref name="left"/>) the right one, the rest the center, each in <paramref name="order"/>'s order.
    /// </summary>
    public static Sided SplitCentered(IReadOnlyList<string> order, IReadOnlySet<string> left, IReadOnlySet<string> right)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new Sided(
            order.Where(left.Contains).ToList(),
            order.Where(id => !left.Contains(id) && right.Contains(id)).ToList(),
            order.Where(id => !left.Contains(id) && !right.Contains(id)).ToList());
    }

    /// <summary>
    /// <paramref name="groups"/> with the item at <paramref name="index"/> (into <see cref="Sided.All"/>) moved by <paramref name="delta"/>
    /// within its group (2026-10-13): a step past the group's edge crosses into the next group — a step down off a group's last item
    /// makes it the next group's first, a step up off a group's first makes it the one before's last, the item keeping its index (left,
    /// center, right, later that day); <see cref="HomeBarItems.Top"/> and <see cref="HomeBarItems.Bottom"/> go to the top or the bottom
    /// of its own group. An index out of range moves nothing.
    /// </summary>
    public static (Sided Groups, int Index) MoveInGroups(Sided groups, int index, int delta)
    {
        var lists = Lists(groups);
        if (delta == 0 || !Locate(lists, index, out int group, out int at))
        {
            return (Of(lists), index);
        }

        var list = lists[group];
        if (at == list.Count - 1 && delta == 1 && group < lists.Count - 1)
        {
            lists[group + 1].Insert(0, list[at]);
            list.RemoveAt(at);
            return (Of(lists), index);
        }

        if (at == 0 && delta == -1 && group > 0)
        {
            lists[group - 1].Add(list[0]);
            list.RemoveAt(0);
            return (Of(lists), index);
        }

        var (moved, landed) = HomeBarItems.Move(list, at, delta);
        lists[group] = moved;
        return (Of(lists), Before(lists, group) + landed);
    }

    /// <summary>
    /// <paramref name="groups"/> with the item at <paramref name="index"/> pinned one group over (2026-10-13, Shift+←/→): to the next
    /// group's start, or to the group before's end — next to the gap either way — and where it landed. With a center (later that day)
    /// left goes to center and center to right, and back. An item already in the outermost group that way, or an index out of range, stays.
    /// </summary>
    public static (Sided Groups, int Index) ToSide(Sided groups, int index, bool toRight)
    {
        var lists = Lists(groups);
        if (!Locate(lists, index, out int group, out int at))
        {
            return (Of(lists), index);
        }

        int target = group + (toRight ? 1 : -1);
        if (target < 0 || target >= lists.Count)
        {
            return (Of(lists), index);
        }

        string id = lists[group][at];
        lists[group].RemoveAt(at);
        if (toRight)
        {
            lists[target].Insert(0, id);
            return (Of(lists), Before(lists, target));
        }

        lists[target].Add(id);
        return (Of(lists), Before(lists, target) + lists[target].Count - 1);
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
    /// <see cref="Sided.All"/>), in its group (2026-10-13, the checklist's <c>+ divider</c>); with no item there, at the end of the center
    /// where there is one (later that day), else of the left group. Where it landed.
    /// </summary>
    public static (Sided Groups, int Index) AddDivider(Sided groups, int index)
    {
        var lists = Lists(groups);
        string divider = NextDivider(groups.All);
        if (Locate(lists, index, out int group, out int at))
        {
            lists[group].Insert(at + 1, divider);
            return (Of(lists), index + 1);
        }

        int home = groups.HasCenter ? 1 : 0;
        lists[home].Add(divider);
        return (Of(lists), Before(lists, home) + lists[home].Count - 1);
    }

    // The groups as lists to change, left to right.
    private static List<List<string>> Lists(Sided groups) => groups.Groups.Select(g => g.ToList()).ToList();

    // The groups back as a Sided: three lists are left, center, right.
    private static Sided Of(List<List<string>> lists) => lists.Count == 3 ? new Sided(lists[0], lists[2], lists[1]) : new Sided(lists[0], lists[1]);

    // The group an index into the whole order falls in, and its place there; false out of range.
    private static bool Locate(List<List<string>> lists, int index, out int group, out int at)
    {
        at = index;
        group = 0;
        if (index < 0)
        {
            return false;
        }

        for (; group < lists.Count; group++)
        {
            if (at < lists[group].Count)
            {
                return true;
            }

            at -= lists[group].Count;
        }

        return false;
    }

    // How many items the groups before group hold.
    private static int Before(List<List<string>> lists, int group) => lists.Take(group).Sum(l => l.Count);

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
