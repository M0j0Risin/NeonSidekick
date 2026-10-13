namespace NeonSidekick.App;

/// <summary>
/// The order of a checklist's items (2026-10-12, the user's ask: "reuse this idea" — the home bar picker's move keys — on
/// <c>Show toolbar</c> and <c>Show performance bar</c>): every id of the list's <c>Names</c> in the user's order, checked or not,
/// so an item keeps its place while it is off. Saved apart from the checks (<see cref="Settings.AppSettingsData.ToolbarOrder"/>,
/// <see cref="Settings.AppSettingsData.PerformanceBarOrder"/>); null is <c>Names</c>' own order, the one the bars always had, and
/// what the checklist's <c>⊡ default</c> puts back. A display setting, so an unknown word is dropped without a warning, as the
/// checks' <c>Resolve</c> does. Pure.
/// </summary>
public static class ItemOrder
{
    /// <summary>
    /// The full order <paramref name="saved"/> names over <paramref name="names"/>: its known ids (trimmed, any case, the first of
    /// a repeat) in their saved order, then each id it leaves out put in after the one before it in <paramref name="names"/> (an
    /// item a later version adds lands beside its neighbours, not at the end), <paramref name="pinnedLast"/> always last (the
    /// toolbar's path, drawn at the row's right whatever its place). Null is <paramref name="names"/> as they are.
    /// </summary>
    public static List<string> Resolve(IReadOnlyList<string>? saved, IReadOnlyList<string> names, string? pinnedLast = null) =>
        Resolve(saved, names, pinnedLast, []);

    /// <summary>
    /// <see cref="Resolve(IReadOnlyList{string}?, IReadOnlyList{string}, string?)"/> with <paramref name="passers"/> (2026-10-12, the
    /// user's ask: the toolbar's Model name moves freely, after the path by default): ids that may stand after
    /// <paramref name="pinned"/>, every other id kept ahead of it; the passers after it keep their order there. An order saved
    /// before a passer existed puts it after its neighbour as any new id, so one after the pinned id in <paramref name="names"/> lands after it.
    /// </summary>
    public static List<string> Resolve(IReadOnlyList<string>? saved, IReadOnlyList<string> names, string? pinned, IReadOnlyCollection<string> passers)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(passers);
        var order = new List<string>(names.Count);
        if (saved is not null)
        {
            // A null in a hand-edited list is skipped: this runs on the pane's tick, where a throw repeats.
            foreach (string word in saved.OfType<string>().Select(w => w.Trim().ToLowerInvariant()))
            {
                if (names.Contains(word, StringComparer.Ordinal) && !order.Contains(word, StringComparer.Ordinal))
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

        int at = pinned is null ? -1 : order.IndexOf(pinned);
        if (at < 0)
        {
            return order;
        }

        // Only the passers stand after the pinned id: anything else saved after it goes back ahead of it, in its order.
        var behind = order.Skip(at + 1).ToList();
        return [.. order.Take(at), .. behind.Where(id => !passers.Contains(id)), pinned!, .. behind.Where(passers.Contains)];
    }

    /// <summary>What saves for <paramref name="order"/>: null when it is <paramref name="names"/>' own order (so it follows a later default), else the list.</summary>
    public static List<string>? Save(IReadOnlyList<string> order, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(names);
        return order.SequenceEqual(names, StringComparer.Ordinal) ? null : order.ToList();
    }

    /// <summary>
    /// <paramref name="order"/> with the item at <paramref name="index"/> moved by <paramref name="delta"/> (<see cref="HomeBarItems.Top"/>,
    /// <see cref="HomeBarItems.Bottom"/> or a step, <see cref="HomeBarItems.Move"/>'s), and where it landed; <paramref name="pinnedLast"/>
    /// never moves and nothing moves past it.
    /// </summary>
    public static (List<string> Order, int Index) Move(IReadOnlyList<string> order, int index, int delta, string? pinnedLast = null) =>
        Move(order, index, delta, pinnedLast, []);

    /// <summary>
    /// <see cref="Move(IReadOnlyList{string}, int, int, string?)"/> with <paramref name="passers"/> (2026-10-12): a passer moves over
    /// the whole list, stepping over <paramref name="pinned"/> as over any item; anything else moves among the ids ahead of the pinned
    /// one alone; the pinned id itself never moves.
    /// </summary>
    public static (List<string> Order, int Index) Move(IReadOnlyList<string> order, int index, int delta, string? pinned, IReadOnlyCollection<string> passers)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(passers);
        int at = pinned is null ? -1 : IndexOf(order, pinned);
        if (at < 0 || (index >= 0 && index < order.Count && passers.Contains(order[index])))
        {
            return HomeBarItems.Move(order, index, delta);
        }

        if (index >= at)
        {
            return (order.ToList(), index);   // the pinned id, or a passer's place: nothing ahead of the pin to move
        }

        var (moved, landed) = HomeBarItems.Move(order.Take(at).ToList(), index, delta);
        moved.AddRange(order.Skip(at));
        return (moved, landed);
    }

    private static int IndexOf(IReadOnlyList<string> order, string id)
    {
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i] == id)
            {
                return i;
            }
        }

        return -1;
    }
}
