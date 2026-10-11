namespace NeonSidekick.Hotkeys;

/// <summary>One chord's line on <c>/keycheck</c>: its label and meaning as <c>/help</c>'s Keys tab has them, and what the probe said.</summary>
public sealed record KeyCheckRow(string Label, string Meaning, KeyChord Chord, HotkeyProbeResult Result);

/// <summary>
/// A <c>/keycheck</c> run: the rows, the held ones first, and how many are held; and since 2026-10-11 whether the probe speaks a Mac's
/// words and the lines it had to add (the terminal's Option key).
/// </summary>
public sealed record KeyCheckReport(IReadOnlyList<KeyCheckRow> Rows, int Held, bool Mac = false, IReadOnlyList<string>? Notes = null);

/// <summary>
/// <c>/keycheck</c>'s pure part (2026-10-04): which of the app's keys to ask about, and the answers in order. The keys are
/// <c>/help</c>'s Keys tab (<c>ChatScreen.KeyRows</c>), so a chord added there is checked too: every row whose label is a chord
/// (<see cref="KeyChord.TryParse"/> with a modifier), and the push-to-talk key, a bare key, when its row is there (voice on).
/// A row that names two keys (<c>Ctrl+Left / Right</c>, a Mac's <c>Ctrl+V / Alt+V</c>, Windows' <c>Ctrl+Backspace / Delete</c>) is
/// two chords since 2026-10-11 (the Mac's check found Mission Control on Ctrl+Left/Right, which the label had kept out until then):
/// a later part that is a chord of its own stands as it is, a bare key takes the first part's modifiers.
/// </summary>
public static class KeyCheck
{
    /// <summary>The chords among <paramref name="rows"/>, in their order; <paramref name="pushToTalk"/>'s row as a bare key when given.</summary>
    public static IReadOnlyList<(string Label, string Meaning, KeyChord Chord)> Chords(IEnumerable<(string Key, string Meaning)> rows, ConsoleKey? pushToTalk)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var chords = new List<(string, string, KeyChord)>();
        foreach (var (label, meaning) in rows)
        {
            if (pushToTalk is { } key && string.Equals(label, key.ToString(), StringComparison.Ordinal))
            {
                chords.Add((label, meaning, new KeyChord(false, false, false, key)));
                continue;
            }

            foreach (var (part, chord) in Parts(label))
            {
                chords.Add((part, meaning, chord));
            }
        }

        return chords;
    }

    /// <summary>
    /// The chords one label names, each with its own label: <c>Ctrl+Alt+M</c> alone; <c>Ctrl+Left / Right</c> as <c>Ctrl+Left</c> and
    /// <c>Ctrl+Right</c>; <c>Ctrl+V / Alt+V</c> as both as written. Nothing for a label whose first part is no chord with a modifier
    /// (<c>Up / Down</c>, <c>PgUp / PgDn</c>); a later part that is neither a chord nor a key is left out.
    /// </summary>
    private static IEnumerable<(string Label, KeyChord Chord)> Parts(string label)
    {
        string[] parts = label.Split(" / ", StringSplitOptions.TrimEntries);
        if (!KeyChord.TryParse(parts[0], out var first) || !first.HasModifier)
        {
            yield break;
        }

        yield return (parts[0], first);
        string prefix = parts[0][..(parts[0].LastIndexOf('+') + 1)];
        foreach (string part in parts.Skip(1))
        {
            if (KeyChord.TryParse(part, out var own) && own.HasModifier)
            {
                yield return (part, own);
            }
            else if (KeyChord.TryParse(prefix + part, out var shared))
            {
                yield return (prefix + part, shared);
            }
        }
    }

    /// <summary>Each of <paramref name="rows"/>' chords asked of <paramref name="probe"/>: the held ones first, then the rest, each in its row's order.</summary>
    public static KeyCheckReport Run(IEnumerable<(string Key, string Meaning)> rows, ConsoleKey? pushToTalk, IHotkeyProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var chords = Chords(rows, pushToTalk);
        var notes = probe.Begin();
        var checkedRows = chords.Select(c => new KeyCheckRow(c.Label, c.Meaning, c.Chord, probe.Probe(c.Chord))).ToList();
        var ordered = checkedRows.Where(r => r.Result.Status == HotkeyStatus.Held).Concat(checkedRows.Where(r => r.Result.Status != HotkeyStatus.Held)).ToList();
        return new KeyCheckReport(ordered, checkedRows.Count(r => r.Result.Status == HotkeyStatus.Held), probe.Mac, notes);
    }
}
