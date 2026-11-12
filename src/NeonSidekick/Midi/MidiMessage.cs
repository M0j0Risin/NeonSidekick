namespace NeonSidekick.Midi;

/// <summary>
/// The MIDI messages a button can be learned from (2026-11-12, the MIDI plan): a Note On (a key or pad), a Control Change (a pedal or
/// button that sends a CC, momentary or latching) and a Program Change (a footswitch's patch select). Note Off, pitch bend, aftertouch,
/// SysEx and the system messages are never learned.
/// </summary>
public enum MidiKind
{
    Note,
    ControlChange,
    ProgramChange,
}

/// <summary>
/// One channel message as <see cref="MidiParser"/> decoded it: <paramref name="Channel"/> 1–16, <paramref name="Number"/> the note, the
/// controller or the program (0–127), <paramref name="Value"/> the velocity or the controller's value (a Program Change's is 0).
/// </summary>
public readonly record struct MidiMessage(MidiKind Kind, int Channel, int Number, int Value);

/// <summary>
/// What a binding is keyed by: the input's name (as <see cref="MidiNames.Normalize"/> leaves it), its <paramref name="Occurrence"/> among
/// inputs of that same name (0 for the first: two identical pedals share a name), and the message's kind, channel and number. The value
/// never takes part: a CC's press is its edge (<see cref="MidiEdge"/>).
/// </summary>
public sealed record MidiTrigger(string Device, int Occurrence, MidiKind Kind, int Channel, int Number)
{
    /// <summary>Whether <paramref name="other"/> is the same button on the same device, the device's name compared as <see cref="MidiNames.Same"/> does.</summary>
    public bool SameButton(MidiTrigger other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Kind == other.Kind && Channel == other.Channel && Number == other.Number && Occurrence == other.Occurrence && MidiNames.Same(Device, other.Device);
    }
}

/// <summary>An input as the system lists it: <paramref name="Index"/> its place in that listing (the backend's handle to open it by), <paramref name="Name"/> its name.</summary>
public sealed record MidiInputInfo(int Index, string Name);

/// <summary>Device names (2026-11-12): how two spellings of one input are matched.</summary>
public static class MidiNames
{
    /// <summary>
    /// <paramref name="name"/> trimmed, with the <c>2- </c> prefix Windows puts before the second of a kind (and later ones) taken off, so a
    /// pedal plugged into another port, or listed after an identical one, keeps its bindings. Pure.
    /// </summary>
    public static string Normalize(string? name)
    {
        string text = (name ?? "").Trim();
        int dash = text.IndexOf("- ", StringComparison.Ordinal);
        if (dash > 0 && dash <= 3 && text[..dash].All(char.IsAsciiDigit))
        {
            text = text[(dash + 2)..].TrimStart();
        }

        return text;
    }

    /// <summary>Whether two names are one input's: <see cref="Normalize"/>d, case ignored. Pure.</summary>
    public static bool Same(string? a, string? b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Each of <paramref name="inputs"/>' occurrence among the inputs of its name: 0 for the first of a name, 1 for the second, in the
    /// order listed. Pure.
    /// </summary>
    public static IReadOnlyList<int> Occurrences(IReadOnlyList<MidiInputInfo> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new int[inputs.Count];
        for (int i = 0; i < inputs.Count; i++)
        {
            string key = Normalize(inputs[i].Name);
            seen.TryGetValue(key, out int count);
            result[i] = count;
            seen[key] = count + 1;
        }

        return result;
    }
}

/// <summary>
/// Raw MIDI bytes into <see cref="MidiMessage"/>s (2026-11-12), one parser per input: a Windows <c>MIM_DATA</c> word and a Mac packet
/// both come through here, so one rule serves both. Running status is followed (a packet may hold several messages with the status sent
/// once); realtime bytes (0xF8–0xFF: clock, start, stop, active sensing) are skipped wherever they fall, even between a message's data
/// bytes, and leave the running status alone; SysEx (0xF0 … 0xF7) and the other system common messages are skipped whole and end the
/// running status, as the MIDI spec says. A Note On with velocity 0 is a Note Off and is dropped with the Note Offs; pitch bend and
/// aftertouch are read past. Not thread-safe: each input's pump feeds its own.
/// </summary>
public sealed class MidiParser
{
    private int _status;      // the running status (0x80–0xEF), 0 for none
    private bool _skipping;   // inside SysEx or a system common message's data
    private int _first = -1;  // the first data byte of a two-byte message, -1 for none yet

    /// <summary>The messages in <paramref name="bytes"/>, added to <paramref name="into"/> in order; a message cut across two calls is completed by the second.</summary>
    public void Feed(ReadOnlySpan<byte> bytes, List<MidiMessage> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        foreach (byte b in bytes)
        {
            if (b >= 0xF8)
            {
                continue;   // realtime: anywhere, nothing else changes
            }

            if (b >= 0xF0)
            {
                // SysEx and the system common messages: skipped to the next status byte, the running status ended. 0xF7 (end of SysEx) and
                // 0xF6 (tune request) have no data, which the skip handles the same.
                _status = 0;
                _first = -1;
                _skipping = b != 0xF7 && b != 0xF6;
                continue;
            }

            if (b >= 0x80)
            {
                _status = b;
                _first = -1;
                _skipping = false;
                continue;
            }

            if (_skipping || _status == 0)
            {
                continue;
            }

            int type = _status & 0xF0;
            int channel = (_status & 0x0F) + 1;
            if (type is 0xC0 or 0xD0)
            {
                if (type == 0xC0)
                {
                    into.Add(new MidiMessage(MidiKind.ProgramChange, channel, b, 0));
                }

                continue;   // one data byte; the running status stays for the next
            }

            if (_first < 0)
            {
                _first = b;
                continue;
            }

            int first = _first;
            _first = -1;
            switch (type)
            {
                case 0x90 when b > 0:
                    into.Add(new MidiMessage(MidiKind.Note, channel, first, b));
                    break;
                case 0xB0:
                    into.Add(new MidiMessage(MidiKind.ControlChange, channel, first, b));
                    break;
            }
        }
    }
}

/// <summary>
/// When a message is a press (2026-11-12, the MIDI plan): a Note On and a Program Change always; a Control Change only on its rising
/// edge, its value going from below <see cref="Threshold"/> to it or above, so a momentary pedal (127 down, 0 up) fires once, a latching
/// one on each "on", and a knob swept past halfway once. A CC never seen counts as off. A pedal with its polarity inverted (0 down, 127
/// up) fires as it is let go, still once a press. Not thread-safe: the board's pump calls it.
/// </summary>
public sealed class MidiEdge
{
    /// <summary>The value a CC must reach to be "on".</summary>
    public const int Threshold = 64;

    private readonly Dictionary<(string Input, int Channel, int Number), bool> _on = [];

    /// <summary>Whether <paramref name="message"/> from the input <paramref name="input"/> (any stable key for it) is a press.</summary>
    public bool Fires(string input, MidiMessage message)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (message.Kind != MidiKind.ControlChange)
        {
            return true;
        }

        var key = (input, message.Channel, message.Number);
        bool on = message.Value >= Threshold;
        bool was = _on.TryGetValue(key, out bool known) && known;
        _on[key] = on;
        return on && !was;
    }

    /// <summary>Every CC back to off: an input closed and opened again starts fresh.</summary>
    public void Forget(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        foreach (var key in _on.Keys.Where(k => string.Equals(k.Input, input, StringComparison.Ordinal)).ToList())
        {
            _on.Remove(key);
        }
    }
}
