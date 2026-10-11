using NeonSidekick.Hotkeys;

namespace NeonSidekick.Tests.Fakes;

/// <summary>A hotkey probe over a set of chords another program "holds" (2026-10-04, <c>/keycheck</c>); every other chord is free.</summary>
public sealed class FakeHotkeyProbe : IHotkeyProbe
{
    /// <summary>The chords reported held.</summary>
    public HashSet<KeyChord> Held { get; } = [];

    /// <summary>The chords reported with an error that is neither, and the error.</summary>
    public Dictionary<KeyChord, int> Errors { get; } = [];

    /// <summary>Every chord asked about, in order.</summary>
    public List<KeyChord> Asked { get; } = [];

    /// <summary>A held chord's cause (a Mac's), by chord; <see cref="HotkeyCause.None"/> (Windows') for the rest.</summary>
    public Dictionary<KeyChord, HotkeyCause> Causes { get; } = [];

    /// <summary>Whether the fake speaks a Mac's words (2026-10-11); Windows' by default.</summary>
    public bool Mac { get; set; }

    /// <summary>What <see cref="Begin"/> answers, and how many runs began.</summary>
    public List<string> Notes { get; } = [];

    public int Begun { get; private set; }

    public IReadOnlyList<string> Begin()
    {
        Begun++;
        return Notes;
    }

    public HotkeyProbeResult Probe(KeyChord chord)
    {
        Asked.Add(chord);
        if (Errors.TryGetValue(chord, out int error))
        {
            return new HotkeyProbeResult(HotkeyStatus.Unknown, error);
        }

        return Held.Contains(chord) ? new HotkeyProbeResult(HotkeyStatus.Held, 0, Causes.GetValueOrDefault(chord)) : new HotkeyProbeResult(HotkeyStatus.Free);
    }
}
