using System.Text.RegularExpressions;
using NeonSidekick.Diagnostics;

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
///
/// <para>Since 2026-11-24 (the session log review: a profile's list held <c>$null</c>, <c>exit=$code</c>, <c>if</c> and <c>code:python</c>) it holds
/// only what reads as a command: <see cref="IsNotACommand"/>'s shapes are never held or saved, a script (<c>code:&lt;language&gt;</c>) is held for the
/// session but never saved (<see cref="Holdable"/>), and <see cref="Tidy"/> drops both from a saved list as the profile loads.</para>
/// </summary>
public sealed partial class CommandAllowList
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
        // A command no reading could name (2026-10-12) is never allowed, whatever a hand-edited list holds; nor, since 2026-11-24, a shape
        // no reader makes ($null, exit=$code, if), which a hand edit could still save.
        if (IsNeverAllowed(prefix) || IsNotACommand(prefix))
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

        // A script is held for the session only (2026-11-24, the user's call): a saved code:* entry, hand-edited or from before, allows nothing.
        return !IsScript(prefix) && Contains(_permanent(), prefix);
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

    /// <summary>Allows the prefixes for the rest of the process; returns those it held (<see cref="Holdable"/>), for the log line.</summary>
    public IReadOnlyList<string> AllowSession(IReadOnlyList<string> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        var kept = Holdable(prefixes, forSave: false);
        lock (_lock)
        {
            foreach (string prefix in kept)
            {
                _session.Add(prefix);
            }
        }

        return kept;
    }

    /// <summary>
    /// Allows the prefixes for the session and saves them into the profile. The session's copy keeps the prefix allowed while the saved
    /// list catches up; each one it adds is remembered as a copy (<see cref="_savedCopies"/>), so a later drop from the saved list
    /// forgets it too (2026-11-23). <see cref="_persist"/> runs outside the lock: the store's change event calls back into
    /// <see cref="ForgetDropped"/>. Returns the prefixes it saved (<see cref="Holdable"/> for a save: never a script), for the log line.
    /// </summary>
    public IReadOnlyList<string> AllowPermanently(IReadOnlyList<string> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        var kept = Holdable(prefixes, forSave: true);
        lock (_lock)
        {
            foreach (string key in kept)
            {
                if (_session.Add(key))
                {
                    _savedCopies.Add(key);
                }
            }
        }

        _persist(Merge(_permanent(), kept));
        return kept;
    }

    /// <summary>
    /// The saved list tidied (2026-11-24, the session log review), once as the profile loads (the screen's <c>BindProfile</c>, the headless start):
    /// <see cref="Merge"/>'s reading saved when it differs, which drops scripts and entries that aren't commands and writes <c>%</c>, <c>?</c> and a
    /// backslash path verb the way the reader now does. Each dropped entry is named in one log line. A hand edit later is caught by
    /// <see cref="IsAllowed"/>, which never matches a dropped shape.
    /// </summary>
    public void Tidy()
    {
        var saved = _permanent();
        var tidied = Merge(saved, []);
        if (tidied.SequenceEqual(saved, StringComparer.Ordinal))
        {
            return;
        }

        var dropped = saved.Select(e => e.Trim()).Where(e => e.Length > 0 && !Keeps(Normalized(e), forSave: true)).Distinct(StringComparer.Ordinal).ToList();
        _persist(tidied);
        if (dropped.Count > 0)
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.TidiedLogLine(dropped));
        }
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

    /// <summary>
    /// Every allowed prefix, session and permanent, sorted ordinal, no duplicates: the saved list as <see cref="Merge"/> reads it plus the session's
    /// as it stands (2026-11-24), so a script allowed for the session still shows while a saved <c>code:*</c> does not.
    /// </summary>
    public IReadOnlyList<string> Snapshot()
    {
        var all = new SortedSet<string>(Merge(_permanent(), []), StringComparer.Ordinal);
        all.UnionWith(SessionSnapshot());
        return all.ToList();
    }

    /// <summary>
    /// <see cref="CommandPrefix.Unreadable"/> and, since 2026-11-24, <see cref="CommandPrefix.NoCommand"/>: never allowed and never added, so a
    /// Session or Permanent pick on a line carrying one allows its other prefixes and leaves the rest to the pane again.
    /// </summary>
    public static bool IsNeverAllowed(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string trimmed = prefix.Trim();
        return string.Equals(trimmed, CommandPrefix.Unreadable, StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, CommandPrefix.NoCommand, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A shape no reader makes (2026-11-24, the session log review: <c>$null</c>, <c>exit=$code</c>, <c>"EXIT=$LASTEXITCODE"</c> and <c>if</c> were
    /// saved): any <c>$</c>, <c>=</c> or quote, a number alone (<c>1</c>; <c>7z</c> is a command), a lone PowerShell keyword. Never held, saved or
    /// matched. A <c>:</c> is no sign: <c>[system.io.file]::readalltext</c> and <c>c:/tools/x</c> are real prefixes.
    /// </summary>
    public static bool IsNotACommand(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string trimmed = prefix.Trim();
        return trimmed.IndexOfAny(['$', '=', '"', '\'', '`']) >= 0 || NumberAlonePattern().IsMatch(trimmed) || CommandPrefix.Keywords.Contains(trimmed);
    }

    // A number alone: 1, 1.5, 0x1f, -2.
    [GeneratedRegex(@"^[+-]?(?:0x[0-9a-f]+|\d+(?:\.\d+)?|\.\d+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NumberAlonePattern();

    /// <summary>Whether <paramref name="prefix"/> is a script's pseudo-prefix, <c>code:&lt;language&gt;</c> (<see cref="ShellText.ScriptPrefix"/>).</summary>
    public static bool IsScript(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return prefix.Trim().StartsWith(ShellText.ScriptPrefix(""), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The prefixes a Session (<paramref name="forSave"/> false) or Always pick holds (2026-11-24): trimmed, lower case, no duplicates, never
    /// <see cref="IsNeverAllowed"/> or <see cref="IsNotACommand"/>, and for a save never a script. The pane's rows, the Web page's buttons, the
    /// notice and <see cref="AllowSession"/>/<see cref="AllowPermanently"/> all read this one rule, so they agree.
    /// </summary>
    public static IReadOnlyList<string> Holdable(IReadOnlyList<string> prefixes, bool forSave)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        var kept = new List<string>();
        foreach (string prefix in prefixes)
        {
            string key = prefix.Trim().ToLowerInvariant();
            if (Keeps(key, forSave) && !kept.Contains(key, StringComparer.Ordinal))
            {
                kept.Add(key);
            }
        }

        return kept;
    }

    private static bool Keeps(string key, bool forSave) => key.Length > 0 && !IsNeverAllowed(key) && !IsNotACommand(key) && !(forSave && IsScript(key));

    /// <summary>
    /// A saved entry as the reader now writes it (2026-11-24): trimmed, lower case; PowerShell's <c>%</c> and <c>foreach</c> (the alias) as
    /// <c>foreach-object</c>, <c>?</c> as <c>where-object</c>; a two-word entry whose second word holds a <c>\</c> (the old lower-case path verb,
    /// <c>dotnet bin\debug\x.dll</c>) with that word read as a path (<see cref="CommandPrefix.Program"/>), so an allow already given keeps working.
    /// </summary>
    internal static string Normalized(string entry)
    {
        string key = entry.Trim().ToLowerInvariant();
        switch (key)
        {
            case "%":
            case "foreach":
                return "foreach-object";
            case "?":
                return "where-object";
        }

        int space = key.IndexOf(' ');
        if (space > 0 && key.IndexOf(' ', space + 1) < 0 && key.IndexOf('\\', space) > space)
        {
            return key[..space] + " " + CommandPrefix.Program(key[(space + 1)..]);
        }

        return key;
    }

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

    /// <summary>
    /// The saved list with <paramref name="prefixes"/> added, lower case, sorted ordinal, no duplicates — a hand-edited file's doubles go at the first save.
    /// Since 2026-11-24 each entry is <see cref="Normalized"/>, and scripts and entries that aren't commands are left out (<see cref="Holdable"/>'s rule
    /// for a save), so a save, a <c>/profile push|pull allowed</c> and the list views never carry one.
    /// </summary>
    public static List<string> Merge(IReadOnlyList<string> saved, IReadOnlyList<string> prefixes)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(prefixes);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in saved.Concat(prefixes))
        {
            string key = Normalized(entry);
            if (Keeps(key, forSave: true))
            {
                set.Add(key);
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
