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

    public HotkeyProbeResult Probe(KeyChord chord)
    {
        Asked.Add(chord);
        if (Errors.TryGetValue(chord, out int error))
        {
            return new HotkeyProbeResult(HotkeyStatus.Unknown, error);
        }

        return new HotkeyProbeResult(Held.Contains(chord) ? HotkeyStatus.Held : HotkeyStatus.Free);
    }
}
