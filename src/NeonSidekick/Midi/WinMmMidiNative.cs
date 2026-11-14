using System.Runtime.InteropServices;

namespace NeonSidekick.Midi;

/// <summary>
/// The WinMM MIDI-in imports (2026-11-12, the MIDI plan), <see cref="Audio.WinMmNative"/>'s rules: source-generated
/// <c>[LibraryImport]</c>s over blittable structs only, so nothing is marshalled. The callback is a function pointer
/// (<c>CALLBACK_FUNCTION</c>: midiIn has no event callback), an <c>[UnmanagedCallersOnly]</c> static in <see cref="WindowsMidiSystem"/>.
/// No long (SysEx) buffer is ever queued, so <c>MIM_LONGDATA</c> never comes and a close never fails with <c>MIDIERR_STILLPLAYING</c>.
/// </summary>
internal static unsafe partial class WinMmMidiNative
{
    public const int CallbackFunction = 0x00030000;

    /// <summary>A short message: param1's low three bytes are the status and the two data bytes.</summary>
    public const uint MimData = 0x3C3;

    /// <summary>A short message the driver held back while the app was slow (only with <c>MIDI_IO_STATUS</c>; read the same).</summary>
    public const uint MimMoreData = 0x3CC;

    public const uint MimError = 0x3C5;

    public const int MmsyserrNoError = 0;
    public const int MmsyserrBadDeviceId = 2;
    public const int MmsyserrAllocated = 4;
    public const int MmsyserrNoDriver = 6;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MidiInCapsW
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        public fixed char szPname[32];
        public uint dwSupport;
    }

    [LibraryImport("winmm.dll")]
    public static partial uint midiInGetNumDevs();

    [LibraryImport("winmm.dll", EntryPoint = "midiInGetDevCapsW")]
    public static partial int midiInGetDevCaps(nuint uDeviceID, MidiInCapsW* pmic, uint cbmic);

    [LibraryImport("winmm.dll")]
    public static partial int midiInOpen(IntPtr* phmi, uint uDeviceID, delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr, void> dwCallback, IntPtr dwInstance, uint fdwOpen);

    [LibraryImport("winmm.dll")]
    public static partial int midiInStart(IntPtr hmi);

    [LibraryImport("winmm.dll")]
    public static partial int midiInStop(IntPtr hmi);

    [LibraryImport("winmm.dll")]
    public static partial int midiInReset(IntPtr hmi);

    [LibraryImport("winmm.dll")]
    public static partial int midiInClose(IntPtr hmi);

    /// <summary>Input <paramref name="index"/>'s name, or null when its caps can't be read.</summary>
    public static string? NameOf(uint index)
    {
        MidiInCapsW caps;
        if (midiInGetDevCaps(index, &caps, (uint)sizeof(MidiInCapsW)) != MmsyserrNoError)
        {
            return null;
        }

        // Cut at the first NUL (2026-11-14, the review's #2): a driver may leave junk after it, which TrimEnd('\0') kept.
        return Platform.FixedText.Read(caps.szPname, 32).Trim();
    }

    /// <summary>
    /// How many bytes a short message with <paramref name="status"/> holds (a <c>MIM_DATA</c> word always carries three; a Program Change's
    /// third is junk, which would read as a second message under running status).
    /// </summary>
    public static int ShortLength(byte status) => status switch
    {
        >= 0x80 and < 0xC0 => 3,
        >= 0xC0 and < 0xE0 => 2,
        >= 0xE0 and < 0xF0 => 3,
        0xF1 or 0xF3 => 2,
        0xF2 => 3,
        _ => 1,
    };
}
