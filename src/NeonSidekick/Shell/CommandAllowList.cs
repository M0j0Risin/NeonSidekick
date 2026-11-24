namespace NeonSidekick.Shell;

/// <summary>
/// The prefixes (<see cref="CommandPrefix"/>) the user has allowed (2026-09-21): the session's,
/// kept here for the process (the <see cref="Timers.TimerBoard"/> lifetime — a <c>/clear</c> or a
/// <c>/new</c> keeps them; a profile switch, a reset or a pull into the loaded profile clears them, 2026-11-23, the user's ask, and a
/// prefix dropped from the saved list loses the copy <i>Allow always</i> made, <see cref="ForgetDropped"/>), and the permanent ones, read through
/// <paramref name="permanent"/> from the loaded profile's <c>Shell allowed commands</c> at every
/// judgement and written back through <paramref name="persist"/> with the merged list (sorted
/// ordinal, no duplicates — the <c>ToolsDisabled</c> shape). Thread-safe: the gate judges on the turn
/// task, the pane answers on the watcher's.
/// </summary>
public sealed class CommandAllowList
{
    private readonly object _lock = new();
    private readonly HashSet<string> _session = new(StringComparer.Ordinal);

    /// <summary>
    /// The prefixes <see cref="AllowPermanently"/> put into <see cref="_session"/> that were not there already (2026-11-23): the copies a
    /// drop from the saved list takes back out (<see cref="ForgetDropped"/>). A prefix allowed for the session first is no copy.
    /// </summary>
    private readonly HashSet<string> _savedCopies = new(StringComparer.Ordinal);
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

    /// <summary>
    /// Allows the prefixes for the session and saves them into the profile. The session's copy keeps the prefix allowed while the saved
    /// list catches up; each one it adds is remembered as a copy (<see cref="_savedCopies"/>), so a later drop from the saved list
    /// forgets it too (2026-11-23). <see cref="_persist"/> runs outside the lock: the store's change event calls back into
    /// <see cref="ForgetDropped"/>.
    /// </summary>
    public void AllowPermanently(IReadOnlyList<string> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        lock (_lock)
        {
            foreach (string prefix in prefixes.Where(p => !IsNeverAllowed(p)))
            {
                string key = prefix.Trim().ToLowerInvariant();
                if (key.Length > 0 && _session.Add(key))
                {
                    _savedCopies.Add(key);
                }
            }
        }

        _persist(Merge(_permanent(), prefixes));
    }

    /// <summary>
    /// The session's prefixes not on the saved list (case-insensitive, as <see cref="IsAllowed"/> compares), sorted ordinal: the
    /// allowed-commands pane's This session group (2026-11-23, the user's ask). A prefix allowed for the session and saved since shows
    /// under the saved ones alone.
    /// </summary>
    public IReadOnlyList<string> SessionOnly()
    {
        var saved = _permanent();
        return SessionSnapshot().Where(prefix => !Contains(saved, prefix)).ToList();
    }

    /// <summary>
    /// Forgets <paramref name="prefix"/> for the session (2026-11-23, the pane's Enter on a This session row, and its remove of a saved
    /// one): the next command it starts asks again unless the saved list holds it. Trimmed and lower-cased, as <see cref="AllowSession"/>
    /// adds. Not <c>ForgetSession</c>: the screen's <c>ForgetSession</c> forgets the conversation.
    /// </summary>
    public void ForgetSessionAllow(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string key = prefix.Trim().ToLowerInvariant();
        lock (_lock)
        {
            _session.Remove(key);
            _savedCopies.Remove(key);
        }
    }

    /// <summary>Forgets every session allow (2026-11-23, the user's ask: a profile switch, a reset or a pull into the loaded profile).</summary>
    public void ClearSessionAllows()
    {
        lock (_lock)
        {
            _session.Clear();
            _savedCopies.Clear();
        }
    }

    /// <summary>
    /// After a change to the saved list (2026-11-23): each copy <see cref="AllowPermanently"/> made that <paramref name="saved"/> no
    /// longer holds leaves the session too, so a prefix removed, reset or overwritten by a pull asks again. A prefix allowed for the
    /// session before it was saved stays. Nothing to do while no copy is held.
    /// </summary>
    public void ForgetDropped(IReadOnlyList<string> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        lock (_lock)
        {
            if (_savedCopies.Count == 0)
            {
                return;
            }

            foreach (string copy in _savedCopies.Where(copy => !Contains(saved, copy)).ToList())
            {
                _session.Remove(copy);
                _savedCopies.Remove(copy);
            }
        }
    }

    /// <summary>The session's prefixes, sorted ordinal, for the list editor (<see cref="SessionOnly"/>) and the not-approved sentence.</summary>
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
