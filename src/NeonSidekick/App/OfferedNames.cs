using NeonSidekick.Sql;

namespace NeonSidekick.App;

/// <summary>
/// The arithmetic under the checklists that save a list of names (<c>SQL connections offered</c> and its five twins,
/// <c>ComfyUI workflows offered</c>, <c>Botchat ComfyUI limited workflows</c>, <c>Docker server containers</c>, and the botchat's
/// limited skills and tools), 2026-10-04, the user's call. Until that day a saved name the checklist no longer listed was carried
/// along at every save ("it counts again if it comes back"), so it could never be unticked: <c>profile.json</c> kept workflows
/// long deleted and N kept them too. Now the lists read from files (and Docker's, from an engine that answered) drop such a name
/// when their checklist opens (<see cref="Stale"/>), while the two whose listing follows what is running — the botchat's tools
/// (an MCP server not connected) and skills (external skills, the folder's own) — show it as a row of its own the user can untick
/// (<see cref="Gone"/>). Pure.
/// </summary>
public static class OfferedNames
{
    /// <summary>
    /// The names of <paramref name="saved"/> (trimmed, blanks skipped, each once, in saved order) that are neither in
    /// <paramref name="listed"/> nor in <paramref name="present"/> — a name a load could not use but still holds, so a typo
    /// fixed later keeps its tick.
    /// </summary>
    public static IReadOnlyList<string> Stale(IReadOnlyList<string>? saved, IEnumerable<string> listed, IEnumerable<string> present, StringComparer comparer)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(present);
        ArgumentNullException.ThrowIfNull(comparer);
        var known = new HashSet<string>(listed.Select(n => n.Trim()), comparer);
        known.UnionWith(present.Select(n => n.Trim()));
        var seen = new HashSet<string>(comparer);
        var stale = new List<string>();
        foreach (var raw in saved ?? [])
        {
            string name = (raw ?? "").Trim();
            if (name.Length > 0 && !known.Contains(name) && seen.Add(name))
            {
                stale.Add(name);
            }
        }

        return stale;
    }

    /// <summary>
    /// <see cref="Stale"/> over a connections catalog: none at all when a whole file could not be read (its names are unknown, so
    /// nothing may be called gone), and a name a problem names (<see cref="SqlConfigProblem.Name"/>) counts as there.
    /// </summary>
    public static IReadOnlyList<string> StaleConnections(IReadOnlyList<string>? saved, IEnumerable<string> listed, IReadOnlyList<SqlConfigProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return problems.Any(p => p.WholeFile)
            ? []
            : Stale(saved, listed, problems.Select(p => p.Name).OfType<string>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The ticked names the botchat's limited tools and skills show as rows of their own: <see cref="Stale"/> with nothing else present.</summary>
    public static IReadOnlyList<string> Gone(IReadOnlyList<string>? saved, IEnumerable<string> listed, StringComparer comparer) =>
        Stale(saved, listed, [], comparer);

    /// <summary><paramref name="saved"/> less <paramref name="stale"/>: trimmed, blanks dropped, each name once, in saved order.</summary>
    public static List<string> Without(IReadOnlyList<string>? saved, IReadOnlyList<string> stale, StringComparer comparer)
    {
        ArgumentNullException.ThrowIfNull(stale);
        ArgumentNullException.ThrowIfNull(comparer);
        var dropped = new HashSet<string>(stale, comparer);
        var seen = new HashSet<string>(comparer);
        return (saved ?? []).Select(n => (n ?? "").Trim()).Where(n => n.Length > 0 && !dropped.Contains(n) && seen.Add(n)).ToList();
    }
}
