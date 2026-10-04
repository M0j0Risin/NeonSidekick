namespace NeonSidekick.Hotkeys;

/// <summary>One chord's line on <c>/keycheck</c>: its label and meaning as <c>/help</c>'s Keys tab has them, and what the probe said.</summary>
public sealed record KeyCheckRow(string Label, string Meaning, KeyChord Chord, HotkeyProbeResult Result);

/// <summary>A <c>/keycheck</c> run: the rows, the held ones first, and how many are held.</summary>
public sealed record KeyCheckReport(IReadOnlyList<KeyCheckRow> Rows, int Held);

/// <summary>
/// <c>/keycheck</c>'s pure part (2026-10-04): which of the app's keys to ask about, and the answers in order. The keys are
/// <c>/help</c>'s Keys tab (<c>ChatScreen.KeyRows</c>), so a chord added there is checked too: every row whose label is a chord
/// (<see cref="KeyChord.TryParse"/> with a modifier), and the push-to-talk key, a bare key, when its row is there (voice on).
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
            if (KeyChord.TryParse(label, out var chord) && chord.HasModifier)
            {
                chords.Add((label, meaning, chord));
            }
            else if (pushToTalk is { } key && string.Equals(label, key.ToString(), StringComparison.Ordinal))
            {
                chords.Add((label, meaning, new KeyChord(false, false, false, key)));
            }
        }

        return chords;
    }

    /// <summary>Each of <paramref name="rows"/>' chords asked of <paramref name="probe"/>: the held ones first, then the rest, each in its row's order.</summary>
    public static KeyCheckReport Run(IEnumerable<(string Key, string Meaning)> rows, ConsoleKey? pushToTalk, IHotkeyProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var checkedRows = Chords(rows, pushToTalk).Select(c => new KeyCheckRow(c.Label, c.Meaning, c.Chord, probe.Probe(c.Chord))).ToList();
        var ordered = checkedRows.Where(r => r.Result.Status == HotkeyStatus.Held).Concat(checkedRows.Where(r => r.Result.Status != HotkeyStatus.Held)).ToList();
        return new KeyCheckReport(ordered, checkedRows.Count(r => r.Result.Status == HotkeyStatus.Held));
    }
}
