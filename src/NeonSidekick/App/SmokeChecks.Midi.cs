using System.Globalization;
using NeonSidekick.Midi;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>midi:winmm-in</c> (2026-11-12, the MIDI plan, <see cref="ProbeWinMmIn"/>'s shape): the MIDI-in imports bound in the published binary
    /// (<c>midiInGetNumDevs</c>) and, when an input exists, the first one's caps read. Never opens one: a pedal another app holds stays its.
    /// Zero devices (a runner) passes, as do <c>MMSYSERR_NODRIVER</c> and <c>BADDEVICEID</c>; whether bytes arrive through the callback is
    /// <c>--midi-check</c>'s job. Skipped off Windows.
    /// </summary>
    public static SmokeCheck ProbeMidiWinMm()
    {
        const string name = "midi:winmm-in";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        try
        {
            uint devices = WinMmMidiNative.midiInGetNumDevs();
            if (devices == 0)
            {
                return new SmokeCheck(name, true, "winmm bound; no MIDI input (0 devices)");
            }

            return WinMmMidiNative.NameOf(0) is { } first
                ? new SmokeCheck(name, true, devices.ToString(CultureInfo.InvariantCulture) + " MIDI input(s); the first is \"" + first + "\"")
                : new SmokeCheck(name, true, devices.ToString(CultureInfo.InvariantCulture) + " MIDI input(s) listed, the first's caps unread");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>midi:coremidi</c> (2026-11-12, the MIDI plan): CoreMIDI bound by its install name in the published binary, a client made and
    /// disposed, the sources counted and the first one's name read. A MIDI server that can't start (a runner with no window server) passes
    /// with a note, as <c>keys:carbon</c> skips there; any other failure fails. Skipped off a Mac.
    /// </summary>
    public static unsafe SmokeCheck ProbeMidiCoreMidi()
    {
        const string name = "midi:coremidi";
        if (!OperatingSystem.IsMacOS())
        {
            return new SmokeCheck(name, true, "skipped: not macOS");
        }

        try
        {
            byte[] label = "NeonSidekick smoke\0"u8.ToArray();
            nint text;
            fixed (byte* p = label)
            {
                text = Images.ImageIONative.CFStringCreateWithCString(0, p, Images.ImageIONative.Utf8);
            }

            uint client;
            int status;
            try
            {
                status = CoreMidiNative.MIDIClientCreate(text, 0, 0, &client);
            }
            finally
            {
                Images.ImageIONative.CFRelease(text);
            }

            if (status == CoreMidiNative.ServerStartError)
            {
                return new SmokeCheck(name, true, "CoreMIDI bound; the MIDI server didn't start here (no window server)");
            }

            if (status != 0)
            {
                return new SmokeCheck(name, false, "MIDIClientCreate returned " + status.ToString(CultureInfo.InvariantCulture));
            }

            _ = CoreMidiNative.MIDIClientDispose(client);
            nuint sources = CoreMidiNative.MIDIGetNumberOfSources();
            string? first = sources > 0 ? CoreMidiNative.NameOf(CoreMidiNative.MIDIGetSource(0)) : null;
            return new SmokeCheck(name, true, "a client made and disposed; " + ((ulong)sources).ToString(CultureInfo.InvariantCulture) + " source(s)"
                + (first is null ? "" : "; the first is \"" + first + "\""));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
