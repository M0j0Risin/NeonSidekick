namespace NeonSidekick.Midi;

/// <summary>
/// This computer's MIDI inputs (2026-11-12, the MIDI plan): <see cref="WindowsMidiSystem"/> over WinMM on Windows, <see cref="MacMidiSystem"/>
/// over CoreMIDI on a Mac, <c>FakeMidiSystem</c> in the tests (<c>Program</c> passes null nowhere else; with none, <c>/midi</c> says MIDI
/// isn't available here). <see cref="Inputs"/> lists them in the system's order; <see cref="Open"/> starts one, its bytes handed to
/// <c>onBytes</c> on the backend's own pump thread (never the system's callback thread), a message at a time on Windows, a packet at a
/// time on a Mac — <see cref="MidiParser"/> splits either. Every call may throw <see cref="MidiException"/>, whose message is the user's sentence.
/// </summary>
public interface IMidiSystem
{
    /// <summary>The inputs connected now, in the system's order.</summary>
    IReadOnlyList<MidiInputInfo> Inputs();

    /// <summary>
    /// <paramref name="input"/> opened and started: <paramref name="onBytes"/> gets its bytes until the returned handle is disposed (after
    /// which no call comes). Throws <see cref="MidiException"/> when it is in use by another app, gone, or failed to open.
    /// </summary>
    IMidiInput Open(MidiInputInfo input, Action<ReadOnlySpan<byte>> onBytes);
}

/// <summary>An open input (2026-11-12): disposing it stops and closes it; no bytes come after <see cref="IDisposable.Dispose"/> returns.</summary>
public interface IMidiInput : IDisposable
{
    /// <summary>The input as it was opened.</summary>
    MidiInputInfo Info { get; }
}

/// <summary>A MIDI failure whose message is the sentence the user reads (<see cref="MidiText"/>'s).</summary>
public sealed class MidiException : Exception
{
    public MidiException()
    {
    }

    public MidiException(string message)
        : base(message)
    {
    }

    public MidiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The input is held by another app (WinMM's <c>MMSYSERR_ALLOCATED</c>): the board lists it as in use instead of failing.</summary>
    public bool InUse { get; init; }
}
