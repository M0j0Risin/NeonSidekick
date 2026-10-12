namespace NeonSidekick.Shell;

/// <summary>
/// The prefixes (<see cref="CommandPrefix"/>) the user has allowed (2026-09-21): the session's,
/// kept here for the process (the <see cref="Timers.TimerBoard"/> lifetime — a <c>/clear</c>, a
/// <c>/new</c> or a profile switch keeps them), and the permanent ones, read through
/// <paramref name="permanent"/> from the loaded profile's <c>Shell allowed commands</c> at every
/// judgement and written back through <paramref name="persist"/> with the merged list (sorted
/// ordinal, no duplicates — the <c>ToolsDisabled</c> shape). Thread-safe: the gate judges on the turn
/// task, the pane answers on the watcher's.
/// </summary>
public sealed class CommandAllowList
{
    private readonly object _lock = new();
    private readonly HashSet<string> _session = new(StringComparer.Ordinal);
    private readonly Func<IReadOnlyList<string>> _permanent;
    private readonly Action<IReadOnlyList<string>> _persist;

    /// <param name="permanent">The saved list as it stands now (the effective settings' <c>ShellCommandAllowed</c>).</param>
    /// <param name="persist">Saves the merged list (<see cref="Merge"/>) into the profile; the screen's <c>AppSettings.Update</c>.</param>
    public CommandAllowList(Func<IReadOnlyList<string>> permanent, Action<IReadOnlyList<string>> persist)
    {
        _permanent = permanent ?? throw new ArgumentNullException(nameof(permanent));
        _persist = persist ?? throw new ArgumentNullException(nameof(persist));
    }

    /// <summary>Whether <paramref name="prefix"/> is allowed for the session or for good (case-insensitive: prefixes are lower case, a hand-edited entry may not be).</summary>
    public bool IsAllowed(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        // A command no reading could name (2026-10-12) is never allowed, whatever a hand-edited list holds.
        if (IsNeverAllowed(prefix))
        {
            return false;
        }

        lock (_lock)
        {
            if (_session.Contains(prefix))
            {
                return true;
            }
        }

        return Contains(_permanent(), prefix);
    }

    /// <summary>Whether every prefix is allowed; true for none (a blank command has nothing to judge, and the gate never runs one).</summary>
    public bool AllowsAll(IReadOnlyList<string> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        foreach (string prefix in prefixes)
        {
            if (!IsAllowed(prefix))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Allows the prefixes for the rest of the process.</summary>
    public void AllowSession(IReadOnlyList<string> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        lock (_lock)
        {
            foreach (string prefix in prefixes.Where(p => !IsNeverAllowed(p)))
            {
                _session.Add(prefix.Trim().ToLowerInvariant());
            }
        }
    }

    /// <summary>Allows the prefixes for the session and saves them into the profile.</summary>
    public void AllowPermanently(IReadOnlyList<string> prefixes)
    {
        AllowSession(prefixes);
        _persist(Merge(_permanent(), prefixes));
    }

    /// <summary>The session's prefixes, sorted ordinal, for the list editor and the not-approved sentence.</summary>
    public IReadOnlyList<string> SessionSnapshot()
    {
        lock (_lock)
        {
            var list = _session.ToList();
            list.Sort(StringComparer.Ordinal);
            return list;
        }
    }

    /// <summary>Every allowed prefix, session and permanent, sorted ordinal, no duplicates.</summary>
    public IReadOnlyList<string> Snapshot() => Merge(_permanent(), SessionSnapshot());

    /// <summary>
    /// <see cref="CommandPrefix.Unreadable"/>: never allowed and never added, so a Session or Permanent pick on a line carrying it
    /// allows its other prefixes and leaves the next unreadable substitution to the pane again.
    /// </summary>
    public static bool IsNeverAllowed(string prefix) => string.Equals(prefix.Trim(), CommandPrefix.Unreadable, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the saved list holds <paramref name="prefix"/>, case-insensitive.</summary>
    public static bool Contains(IReadOnlyList<string> saved, string prefix)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(prefix);
        foreach (string entry in saved)
        {
            if (string.Equals(entry.Trim(), prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The saved list with <paramref name="prefixes"/> added, lower case, sorted ordinal, no duplicates — a hand-edited file's doubles go at the first save.</summary>
    public static List<string> Merge(IReadOnlyList<string> saved, IReadOnlyList<string> prefixes)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(prefixes);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in saved)
        {
            string trimmed = entry.Trim().ToLowerInvariant();
            if (trimmed.Length > 0 && !IsNeverAllowed(trimmed))
            {
                set.Add(trimmed);
            }
        }

        foreach (string prefix in prefixes)
        {
            string trimmed = prefix.Trim().ToLowerInvariant();
            if (trimmed.Length > 0 && !IsNeverAllowed(trimmed))
            {
                set.Add(trimmed);
            }
        }

        var list = set.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>The saved list without <paramref name="prefix"/> (case-insensitive), sorted ordinal, no duplicates: the list editor's remove.</summary>
    public static List<string> Without(IReadOnlyList<string> saved, string prefix)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(prefix);
        var list = Merge(saved, []);
        list.RemoveAll(entry => string.Equals(entry, prefix.Trim(), StringComparison.OrdinalIgnoreCase));
        return list;
    }
}
