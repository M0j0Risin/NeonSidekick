using NeonSidekick.Files;

namespace NeonSidekick.Monitors;

/// <summary>One input as a monitor's tab, <c>/monitor list</c> and <c>monitor_list</c> show it: its code, standard and given names, and its marks.</summary>
public sealed record MonitorInputRow(byte Code, string? Standard, string? Given, bool Hidden, bool Current, bool Away)
{
    /// <summary>The words for the input: <c>Work laptop (HDMI-1)</c> with a given name, else <c>HDMI-1</c>, else <c>0x1B</c>.</summary>
    public string Words => Given is { } g ? g + " (" + (Standard ?? Mccs.Hex(Code)) + ")" : Standard ?? Mccs.Hex(Code);
}

/// <summary>
/// The input names in force (2026-10-23, the monitors plan, <c>Monitor input names</c> and <c>Monitor inputs hidden</c>): saved per profile as
/// <c>&lt;key&gt;/&lt;hex&gt;</c> (<see cref="MonitorKey"/>, two lower-case digits), read from the settings each time the pane, <c>/monitor</c> or a
/// tool builds its words, so a profile switch follows at once. A key not saved falls back to the one saved key of the same model when exactly
/// one connected monitor has it (a cable moved, <see cref="MonitorKey.Saved"/>). Pure.
/// </summary>
public sealed class MonitorWords
{
    /// <summary>A given name's longest.</summary>
    public const int MaxName = 24;

    private readonly IReadOnlyDictionary<string, string> _names;
    private readonly IReadOnlySet<string> _hidden;
    private readonly IReadOnlyList<string?> _connected;

    public MonitorWords(IReadOnlyDictionary<string, string>? names, IReadOnlyList<string>? hidden, IReadOnlyList<MonitorInfo> connected)
    {
        ArgumentNullException.ThrowIfNull(connected);
        _names = names ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _hidden = (hidden ?? []).ToHashSet(StringComparer.Ordinal);
        _connected = connected.Select(m => m.Key).ToList();
    }

    /// <summary>No names, nothing hidden.</summary>
    public static MonitorWords None(IReadOnlyList<MonitorInfo> connected) => new(null, null, connected);

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

        var saved = _names.Keys.Concat(_hidden).Select(e => e.Split('/')[0]);
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
    /// Input names… page); <paramref name="away"/> the input the app switched it away from, marked ↩.
    /// </summary>
    public IReadOnlyList<MonitorInputRow> Rows(MonitorInfo monitor, IReadOnlyList<byte> offered, byte? current, byte? away, bool all = false)
    {
        ArgumentNullException.ThrowIfNull(offered);
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
            .Select(c => new MonitorInputRow(c, Mccs.StandardName(c), Given(monitor, c), Hidden(monitor, c), c == current, c == away))
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
