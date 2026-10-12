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
    public static List<string> Resolve(IReadOnlyList<string>? saved, IReadOnlyList<string> names, string? pinnedLast = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        var known = names.Where(n => n != pinnedLast).ToList();
        var order = new List<string>(names.Count);
        if (saved is not null)
        {
            // A null in a hand-edited list is skipped: this runs on the pane's tick, where a throw repeats.
            foreach (string word in saved.OfType<string>().Select(w => w.Trim().ToLowerInvariant()))
            {
                if (known.Contains(word, StringComparer.Ordinal) && !order.Contains(word, StringComparer.Ordinal))
                {
                    order.Add(word);
                }
            }
        }

        for (int i = 0; i < known.Count; i++)
        {
            if (order.Contains(known[i], StringComparer.Ordinal))
            {
                continue;
            }

            int after = i == 0 ? -1 : order.IndexOf(known[i - 1]);
            order.Insert(after + 1, known[i]);
        }

        if (pinnedLast is not null && names.Contains(pinnedLast, StringComparer.Ordinal))
        {
            order.Add(pinnedLast);
        }

        return order;
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
    public static (List<string> Order, int Index) Move(IReadOnlyList<string> order, int index, int delta, string? pinnedLast = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        bool pinned = pinnedLast is not null && order.Count > 0 && order[^1] == pinnedLast;
        if (!pinned)
        {
            return HomeBarItems.Move(order, index, delta);
        }

        if (index == order.Count - 1)
        {
            return (order.ToList(), index);
        }

        var (moved, at) = HomeBarItems.Move(order.Take(order.Count - 1).ToList(), index, delta);
        moved.Add(pinnedLast!);
        return (moved, at);
    }
}
