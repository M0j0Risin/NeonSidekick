using NeonSidekick.Midi;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// <see cref="IMidiSystem"/> for the tests (2026-11-12, the MIDI plan): a list of inputs the test edits (plug one in, pull one out), each
/// opened as the real backends would, an input another app holds (<see cref="Held"/>) refused as in use, and <see cref="Send"/> handing an
/// open input's callback its bytes inline on the test's thread (the real backends' pump thread is theirs alone), so a board test needs no
/// waits. Thread-safe: a screen's board opens on the pool.
/// </summary>
internal sealed class FakeMidiSystem : IMidiSystem
{
    private readonly Lock _gate = new();
    private readonly List<string> _inputs = [];
    private readonly List<FakeInput> _open = [];

    public FakeMidiSystem(params string[] inputs) => _inputs.AddRange(inputs);

    /// <summary>The names another app holds: opening one throws <see cref="MidiException.InUse"/>.</summary>
    public HashSet<string> Held { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every open, by name, in order.</summary>
    public List<string> Opened { get; } = [];

    /// <summary>Every close, by name, in order.</summary>
    public List<string> Closed { get; } = [];

    /// <summary>How many times the inputs were listed.</summary>
    public int Listed { get; private set; }

    public void Plug(string name)
    {
        lock (_gate)
        {
            _inputs.Add(name);
        }
    }

    public void Unplug(string name)
    {
        lock (_gate)
        {
            _inputs.Remove(name);
        }
    }

    /// <summary>Whether an input named <paramref name="name"/> (its listed spelling) is open now.</summary>
    public bool IsOpen(string name)
    {
        lock (_gate)
        {
            return _open.Any(o => string.Equals(o.Info.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// <paramref name="bytes"/> from the open input named <paramref name="name"/> (the <paramref name="occurrence"/>th of that name), handed to its
    /// callback now; false when it isn't open.
    /// </summary>
    public bool Send(string name, byte[] bytes, int occurrence = 0)
    {
        FakeInput? input;
        lock (_gate)
        {
            input = _open.Where(o => string.Equals(o.Info.Name, name, StringComparison.OrdinalIgnoreCase)).OrderBy(o => o.Info.Index).Skip(occurrence).FirstOrDefault();
        }

        if (input is null)
        {
            return false;
        }

        input.OnBytes(bytes);
        return true;
    }

    public IReadOnlyList<MidiInputInfo> Inputs()
    {
        lock (_gate)
        {
            Listed++;
            return _inputs.Select((name, i) => new MidiInputInfo(i, name)).ToList();
        }
    }

    public IMidiInput Open(MidiInputInfo input, Action<ReadOnlySpan<byte>> onBytes)
    {
        lock (_gate)
        {
            if (input.Index < 0 || input.Index >= _inputs.Count || !string.Equals(_inputs[input.Index], input.Name, StringComparison.Ordinal))
            {
                throw new MidiException(MidiText.Gone);
            }

            if (Held.Contains(input.Name))
            {
                throw new MidiException(MidiText.InUse) { InUse = true };
            }

            if (_open.Any(o => o.Info.Index == input.Index))
            {
                throw new MidiException(MidiText.InUse) { InUse = true };   // the classic stack: one open at a time
            }

            var opened = new FakeInput(this, input, onBytes);
            _open.Add(opened);
            Opened.Add(input.Name);
            return opened;
        }
    }

    private void Close(FakeInput input)
    {
        lock (_gate)
        {
            if (_open.Remove(input))
            {
                Closed.Add(input.Info.Name);
            }
        }
    }

    private sealed class FakeInput(FakeMidiSystem system, MidiInputInfo info, Action<ReadOnlySpan<byte>> onBytes) : IMidiInput
    {
        public MidiInputInfo Info { get; } = info;

        public void OnBytes(byte[] bytes) => onBytes(bytes);

        public void Dispose() => system.Close(this);
    }
}
