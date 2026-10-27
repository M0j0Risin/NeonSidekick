using NeonSidekick.Files;

namespace NeonSidekick.Monitors;

/// <summary>
/// One input as a monitor's tab, <c>/monitor list</c> and <c>monitor_list</c> show it: its code, standard and given names, and its marks
/// (<see cref="ThisPc"/>, later on 2026-10-23: the user's pick of this PC's input).
/// </summary>
public sealed record MonitorInputRow(byte Code, string? Standard, string? Given, bool Hidden, bool Current, bool Away, bool ThisPc = false)
{
    /// <summary>The words for the input: <c>Work laptop (HDMI-1)</c> with a given name, else <c>HDMI-1</c>, else <c>0x1B</c>.</summary>
    public string Words => Given is { } g ? g + " (" + (Standard ?? Mccs.Hex(Code)) + ")" : Standard ?? Mccs.Hex(Code);
}

/// <summary>
/// The three monitor settings as <see cref="MonitorWords.Merge"/> hands them back (2026-10-25, <c>/profile push|pull monitors</c>): new
/// collections, each null when empty, as the settings keep them.
/// </summary>
public sealed record MonitorSettings(Dictionary<string, string>? Names, List<string>? Hidden, List<string>? PcInputs);

/// <summary>What <see cref="MonitorWords.Merge"/> made: the target's new settings, the entries added, those it held already, and those it kept its own of (a name taken by another input, another PC input).</summary>
public sealed record MonitorMerge(MonitorSettings Settings, int Added, int Duplicates, int Kept);

/// <summary>
/// The input names in force (2026-10-23, the monitors plan, <c>Monitor input names</c> and <c>Monitor inputs hidden</c>): saved per profile as
/// <c>&lt;key&gt;/&lt;hex&gt;</c> (<see cref="MonitorKey"/>, two lower-case digits), read from the settings each time the pane, <c>/monitor</c> or a
/// tool builds its words, so a profile switch follows at once. A key not saved falls back to the one saved key of the same model when exactly
/// one connected monitor has it (a cable moved, <see cref="MonitorKey.Saved"/>). Later on 2026-10-23, <c>Monitor PC inputs</c> too: which input of
/// each monitor is this PC's (<see cref="PcInput"/>, <see cref="SetPc"/>), saved the same way, at most one a monitor. Pure.
/// </summary>
public sealed class MonitorWords
{
    /// <summary>A given name's longest.</summary>
    public const int MaxName = 24;

    private readonly IReadOnlyDictionary<string, string> _names;
    private readonly IReadOnlySet<string> _hidden;
    private readonly IReadOnlyDictionary<string, byte> _pc;
    private readonly IReadOnlyList<string?> _connected;

    public MonitorWords(IReadOnlyDictionary<string, string>? names, IReadOnlyList<string>? hidden, IReadOnlyList<MonitorInfo> connected, IReadOnlyList<string>? pcInputs = null)
    {
        ArgumentNullException.ThrowIfNull(connected);
        _names = names ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _hidden = (hidden ?? []).ToHashSet(StringComparer.Ordinal);
        _pc = ParsePc(pcInputs);
        _connected = connected.Select(m => m.Key).ToList();
    }

    /// <summary>No names, nothing hidden, no picks.</summary>
    public static MonitorWords None(IReadOnlyList<MonitorInfo> connected) => new(null, null, connected);

    /// <summary>The words in force for <paramref name="connected"/>: the settings' input names, hidden inputs and this PC's inputs. The one builder every front end uses.</summary>
    public static MonitorWords For(Settings.AppSettingsData effective, IReadOnlyList<MonitorInfo> connected)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return new(effective.MonitorInputNames, effective.MonitorInputsHidden, connected, effective.MonitorPcInputs);
    }

    /// <summary>The input the user picked as this PC's on <paramref name="monitor"/>, or null.</summary>
    public byte? PcInput(MonitorInfo monitor) => SavedKey(monitor) is { } key && _pc.TryGetValue(key, out byte code) ? code : null;

    /// <summary>
    /// <paramref name="pcInputs"/> with <paramref name="key"/>'s pick set to <paramref name="code"/> (null clears it): every other entry for that
    /// key goes, so a hand-edited file comes back to one; null when none is left.
    /// </summary>
    public static List<string>? SetPc(IReadOnlyList<string>? pcInputs, string key, byte? code)
    {
        ArgumentNullException.ThrowIfNull(key);
        var next = (pcInputs ?? []).Where(e => !string.Equals(KeyOf(e), key, StringComparison.Ordinal)).ToList();
        if (code is { } c)
        {
            next.Add(Entry(key, c));
        }

        return next.Count > 0 ? next : null;
    }

    // The saved picks by key: an entry with no '/' or a code that doesn't parse is skipped; the first of two for one key wins.
    private static Dictionary<string, byte> ParsePc(IReadOnlyList<string>? entries)
    {
        var picks = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (string entry in entries ?? [])
        {
            int slash = entry.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && Mccs.TryParseCode(entry[(slash + 1)..], out byte code))
            {
                picks.TryAdd(entry[..slash], code);
            }
        }

        return picks;
    }

    /// <summary>
    /// <c>/profile push|pull monitors [overwrite]</c> (2026-10-25, the user's ask): the source's input names, hidden inputs and PC inputs
    /// (<paramref name="from"/>) over the target's (<paramref name="to"/>), by monitor key. Every entry is read through the parser and
    /// written back with <see cref="Entry"/>, so a hand-edited <c>/0F</c> is <c>/0f</c>, and one that does not parse is dropped. With
    /// <paramref name="overwrite"/> the source's replace all three. Without it, an entry the target holds is a duplicate, and the target keeps
    /// its own name for an input, its own PC input for a monitor, and a name another input of that monitor has (any case, as
    /// <see cref="CleanName"/> holds) or that reads as another input's code. The standard input names need the monitor attached, so they
    /// are not checked here. Pure: new collections every time, each null when empty.
    /// </summary>
    public static MonitorMerge Merge(MonitorSettings from, MonitorSettings to, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var (fromNames, fromHidden, fromPc) = Normalized(from);
        var (toNames, toHidden, toPc) = Normalized(overwrite ? new MonitorSettings(null, null, null) : to);
        int added = 0, duplicates = 0, kept = 0;

        foreach (var (entry, name) in fromNames)
        {
            if (toNames.TryGetValue(entry, out string? held))
            {
                if (string.Equals(held, name, StringComparison.Ordinal))
                {
                    duplicates++;
                }
                else
                {
                    kept++;
                }

                continue;
            }

            string key = KeyOf(entry);
            byte code = Mccs.TryParseCode(entry[(key.Length + 1)..], out byte c) ? c : (byte)0;
            bool taken = toNames.Any(n => n.Key != entry && string.Equals(KeyOf(n.Key), key, StringComparison.Ordinal) && string.Equals(n.Value, name, StringComparison.OrdinalIgnoreCase))
                || (Mccs.TryParseCode(name, out byte hex) && hex != code);
            if (taken)
            {
                kept++;
                continue;
            }

            toNames[entry] = name;
            added++;
        }

        foreach (string entry in fromHidden)
        {
            if (toHidden.Contains(entry, StringComparer.Ordinal))
            {
                duplicates++;
                continue;
            }

            toHidden.Add(entry);
            added++;
        }

        foreach (string entry in fromPc)
        {
            string key = KeyOf(entry);
            if (toPc.FirstOrDefault(e => string.Equals(KeyOf(e), key, StringComparison.Ordinal)) is { } held)
            {
                if (string.Equals(held, entry, StringComparison.Ordinal))
                {
                    duplicates++;
                }
                else
                {
                    kept++;
                }

                continue;
            }

            toPc.Add(entry);
            added++;
        }

        var settings = new MonitorSettings(toNames.Count > 0 ? toNames : null, toHidden.Count > 0 ? toHidden : null, toPc.Count > 0 ? toPc : null);
        return new(settings, added, duplicates, kept);
    }

    /// <summary>
    /// The inputs <paramref name="monitor"/> last reported, from <paramref name="seen"/> (<c>Monitor inputs seen</c>, 2026-10-27, the user's
    /// pick: a tab shows its inputs at once while they are read again), in the order saved; null for none, or for a monitor with no key. The
    /// key found as the names find theirs (<see cref="MonitorKey.Saved"/>: a cable moved still finds its list). Pure.
    /// </summary>
    public static IReadOnlyList<byte>? Remembered(IReadOnlyList<string>? seen, MonitorInfo monitor, IReadOnlyList<MonitorInfo> connected)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(connected);
        if (monitor.Key is not { } key || seen is not { Count: > 0 }
            || MonitorKey.Saved(key, seen.Select(KeyOf), connected.Select(m => m.Key)) is not { } saved)
        {
            return null;
        }

        var codes = new List<byte>();
        foreach (string entry in seen)
        {
            int slash = entry.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && string.Equals(entry[..slash], saved, StringComparison.Ordinal) && Mccs.TryParseCode(entry[(slash + 1)..], out byte code) && !codes.Contains(code))
            {
                codes.Add(code);
            }
        }

        return codes.Count > 0 ? codes : null;
    }

    /// <summary>
    /// <paramref name="seen"/> with <paramref name="monitor"/>'s list set to <paramref name="inputs"/> under its own key: that key's entries, and
    /// those of the key <see cref="Remembered"/> found for it if that is another (a moved cable's old port), replaced. Null when none is left;
    /// <paramref name="seen"/> as is for a monitor with no key. Pure: a new list.
    /// </summary>
    public static List<string>? Remember(IReadOnlyList<string>? seen, MonitorInfo monitor, IReadOnlyList<MonitorInfo> connected, IReadOnlyList<byte> inputs)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(connected);
        ArgumentNullException.ThrowIfNull(inputs);
        if (monitor.Key is not { } key)
        {
            return seen is { Count: > 0 } ? [.. seen] : null;
        }

        string? old = seen is { Count: > 0 } ? MonitorKey.Saved(key, seen.Select(KeyOf), connected.Select(m => m.Key)) : null;
        var next = (seen ?? []).Where(e => !string.Equals(KeyOf(e), key, StringComparison.Ordinal) && !string.Equals(KeyOf(e), old, StringComparison.Ordinal)).ToList();
        next.AddRange(inputs.Distinct().Select(code => Entry(key, code)));
        return next.Count > 0 ? next : null;
    }

    /// <summary>How many entries <paramref name="settings"/> holds once read through the parser (<see cref="Merge"/>'s count of a source).</summary>
    public static int Count(MonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var (names, hidden, pc) = Normalized(settings);
        return names.Count + hidden.Count + pc.Count;
    }

    // Every entry read through the parser and written back with Entry: names trimmed and cut at MaxName (blank ones dropped), hidden
    // entries once each, PC inputs one a monitor (the first wins, as ParsePc reads them).
    private static (Dictionary<string, string> Names, List<string> Hidden, List<string> Pc) Normalized(MonitorSettings settings)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (entry, name) in settings.Names ?? [])
        {
            string clean = (name ?? "").Trim();
            clean = clean.Length > MaxName ? clean[..MaxName].TrimEnd() : clean;
            if (clean.Length > 0 && Normal(entry) is { } normal)
            {
                names.TryAdd(normal, clean);
            }
        }

        var hidden = new List<string>();
        foreach (string entry in settings.Hidden ?? [])
        {
            if (Normal(entry) is { } normal && !hidden.Contains(normal, StringComparer.Ordinal))
            {
                hidden.Add(normal);
            }
        }

        var pc = new List<string>();
        foreach (string entry in settings.PcInputs ?? [])
        {
            if (Normal(entry) is { } normal && !pc.Any(e => string.Equals(KeyOf(e), KeyOf(normal), StringComparison.Ordinal)))
            {
                pc.Add(normal);
            }
        }

        return (names, hidden, pc);
    }

    // An entry as Entry writes it, or null when it has no key or its code does not parse.
    private static string? Normal(string? entry)
    {
        int slash = entry?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        return slash > 0 && Mccs.TryParseCode(entry![(slash + 1)..], out byte code) ? Entry(entry[..slash], code) : null;
    }

    private static string KeyOf(string entry) => entry.IndexOf('/', StringComparison.Ordinal) is int slash and > 0 ? entry[..slash] : entry;

    /// <summary>The saved entry for <paramref name="key"/>'s <paramref name="code"/>: <c>ausaa34-uid4355/0f</c>.</summary>
    public static string Entry(string key, byte code) => key + "/" + code.ToString("x2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The key <paramref name="monitor"/>'s entries are saved under (its own, or a moved cable's), null for a monitor with no key.</summary>
    public string? SavedKey(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (monitor.Key is not { } key)
        {
            return null;
        }

        var saved = _names.Keys.Concat(_hidden).Select(e => e.Split('/')[0]).Concat(_pc.Keys);
        return MonitorKey.Saved(key, saved, _connected) ?? key;
    }

    /// <summary>The name the user gave <paramref name="code"/> on <paramref name="monitor"/>, or null.</summary>
    public string? Given(MonitorInfo monitor, byte code) =>
        SavedKey(monitor) is { } key && _names.TryGetValue(Entry(key, code), out string? name) && name.Trim().Length > 0 ? name.Trim() : null;

    /// <summary>Whether the user hid <paramref name="code"/> on <paramref name="monitor"/>.</summary>
    public bool Hidden(MonitorInfo monitor, byte code) => SavedKey(monitor) is { } key && _hidden.Contains(Entry(key, code));

    /// <summary>The codes given a name on <paramref name="monitor"/>.</summary>
    public IReadOnlyList<byte> Named(MonitorInfo monitor)
    {
        if (SavedKey(monitor) is not { } key)
        {
            return [];
        }

        var codes = new List<byte>();
        foreach (string entry in _names.Keys)
        {
            int slash = entry.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && string.Equals(entry[..slash], key, StringComparison.Ordinal) && Mccs.TryParseCode(entry[(slash + 1)..], out byte code))
            {
                codes.Add(code);
            }
        }

        return codes.Order().ToList();
    }

    /// <summary>The words for <paramref name="code"/> on <paramref name="monitor"/> (<see cref="MonitorInputRow.Words"/>).</summary>
    public string Describe(MonitorInfo monitor, byte code) => new MonitorInputRow(code, Mccs.StandardName(code), Given(monitor, code), false, false, false).Words;

    /// <summary>
    /// The inputs a monitor's tab offers: <paramref name="offered"/> (the reported ones or the common four), every code given a name, and
    /// <paramref name="current"/> whatever its code; hidden ones left out unless current, or kept with <paramref name="all"/> (the
    /// Input names… page); <paramref name="away"/> the input the app switched it away from, marked ↩; the picked one <see cref="MonitorInputRow.ThisPc"/>.
    /// A current of 0x00 is none (no input has it: some monitors answer a read they don't support with zeros), so no <c>0x00</c> row to switch to.
    /// </summary>
    public IReadOnlyList<MonitorInputRow> Rows(MonitorInfo monitor, IReadOnlyList<byte> offered, byte? current, byte? away, bool all = false)
    {
        ArgumentNullException.ThrowIfNull(offered);
        current = MonitorShowing.Usable(current);
        byte? pc = PcInput(monitor);
        var codes = offered.ToList();
        foreach (byte named in Named(monitor).Where(c => !codes.Contains(c)))
        {
            codes.Add(named);
        }

        if (current is { } now && !codes.Contains(now))
        {
            codes.Add(now);
        }

        return codes
            .Select(c => new MonitorInputRow(c, Mccs.StandardName(c), Given(monitor, c), Hidden(monitor, c), c == current, c == away, c == pc))
            .Where(r => all || !r.Hidden || r.Current)
            .ToList();
    }

    /// <summary>
    /// The input <paramref name="text"/> names among <paramref name="rows"/> (a given name, a standard name such as <c>hdmi-1</c>, any case;
    /// a hidden one too), else any code typed as hex (<c>0x1B</c>, <c>1b</c>); null for none.
    /// </summary>
    public static byte? Resolve(IReadOnlyList<MonitorInputRow> rows, string text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        string t = text.Trim();
        if (t.Length == 0)
        {
            return null;
        }

        if (rows.FirstOrDefault(r => r.Given is { } g && string.Equals(g, t, StringComparison.OrdinalIgnoreCase)) is { } given)
        {
            return given.Code;
        }

        if (rows.FirstOrDefault(r => r.Standard is { } s && string.Equals(s, t, StringComparison.OrdinalIgnoreCase)) is { } standard)
        {
            return standard.Code;
        }

        for (int code = 1; code <= 0xFF; code++)
        {
            if (Mccs.StandardName((byte)code) is { } s && string.Equals(s, t, StringComparison.OrdinalIgnoreCase))
            {
                return (byte)code;
            }
        }

        return Mccs.TryParseCode(t, out byte hex) ? hex : null;
    }

    /// <summary>
    /// <paramref name="name"/> as it saves (trimmed, scrubbed, at most <see cref="MaxName"/> characters), or null when it is taken by another
    /// of <paramref name="rows"/>' standard or given names (any case), so a name can never mean two inputs.
    /// </summary>
    public static string? CleanName(string name, byte code, IReadOnlyList<MonitorInputRow> rows)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(rows);
        string clean = NameScrub.Display(name.Trim());
        clean = clean.Length > MaxName ? clean[..MaxName].TrimEnd() : clean;
        bool taken = rows.Any(r => r.Code != code
            && ((r.Given is { } g && string.Equals(g, clean, StringComparison.OrdinalIgnoreCase))
                || (r.Standard is { } s && string.Equals(s, clean, StringComparison.OrdinalIgnoreCase))))
            || (Mccs.TryParseCode(clean, out byte hex) && hex != code);
        return taken ? null : clean;
    }

    /// <summary><paramref name="names"/> with <paramref name="monitor"/>'s <paramref name="code"/> named <paramref name="name"/> (blank clears it); null when none is left.</summary>
    public static Dictionary<string, string>? Rename(IReadOnlyDictionary<string, string>? names, string key, byte code, string? name)
    {
        ArgumentNullException.ThrowIfNull(key);
        var next = names is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(names, StringComparer.Ordinal);
        string entry = Entry(key, code);
        if (string.IsNullOrWhiteSpace(name))
        {
            next.Remove(entry);
        }
        else
        {
            next[entry] = name.Trim();
        }

        return next.Count > 0 ? next : null;
    }

    /// <summary><paramref name="hidden"/> with <paramref name="code"/> hidden or shown again; null when none is left.</summary>
    public static List<string>? Hide(IReadOnlyList<string>? hidden, string key, byte code, bool hide)
    {
        ArgumentNullException.ThrowIfNull(key);
        string entry = Entry(key, code);
        var next = (hidden ?? []).Where(h => !string.Equals(h, entry, StringComparison.Ordinal)).ToList();
        if (hide)
        {
            next.Add(entry);
        }

        return next.Count > 0 ? next : null;
    }
}
