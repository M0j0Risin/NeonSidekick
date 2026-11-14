using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Midi;

/// <summary>
/// CoreMIDI as the Mac's MIDI backend calls it (2026-11-12, the MIDI plan), <see cref="Audio.AudioQueueNative"/>'s rules: objects are
/// <c>uint</c> refs, CoreFoundation strings <c>nint</c>s, the read proc an <c>[UnmanagedCallersOnly]</c> static handed over as a function
/// pointer, nothing marshalled by value. <c>MIDIInputPortCreate</c> and its <c>MIDIReadProc</c> are marked "to be deprecated" but are in the
/// current SDK; the replacement takes a block and hands over UMP words. No notify proc is given, so no run loop is needed; the read proc
/// runs on CoreMIDI's own thread. A system framework: nothing ships beside the exe.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class CoreMidiNative
{
    public const string CoreMidi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";

    /// <summary><c>kMIDIServerStartErr</c>: the MIDI server couldn't start (a session without a window server, a CI runner).</summary>
    public const int ServerStartError = -10845;

    /// <summary><c>kMIDINoConnection</c>.</summary>
    public const int NoConnection = -10833;

    [LibraryImport(CoreMidi)]
    public static partial int MIDIClientCreate(nint name, nint notifyProc, nint notifyRefCon, uint* outClient);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIClientDispose(uint client);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIInputPortCreate(uint client, nint portName, delegate* unmanaged[Cdecl]<byte*, nint, nint, void> readProc, nint refCon, uint* outPort);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIPortDispose(uint port);

    [LibraryImport(CoreMidi)]
    public static partial nuint MIDIGetNumberOfSources();

    [LibraryImport(CoreMidi)]
    public static partial uint MIDIGetSource(nuint index);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIPortConnectSource(uint port, uint source, nint connRefCon);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIPortDisconnectSource(uint port, uint source);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIObjectGetStringProperty(uint obj, nint propertyId, nint* value);

    /// <summary>An endpoint's display name (<c>kMIDIPropertyDisplayName</c>, the device and the port), else its name; null when neither reads.</summary>
    public static string? NameOf(uint endpoint)
    {
        foreach (string key in (ReadOnlySpan<string>)["kMIDIPropertyDisplayName", "kMIDIPropertyName"])
        {
            if (Property(key) is not { } property)
            {
                continue;
            }

            nint text = 0;
            if (MIDIObjectGetStringProperty(endpoint, property, &text) == 0 && text != 0)
            {
                try
                {
                    string name = Images.ImageIONative.Text(text).Trim();
                    if (name.Length > 0)
                    {
                        return name;
                    }
                }
                finally
                {
                    Images.ImageIONative.CFRelease(text);
                }
            }
        }

        return null;
    }

    // CoreMIDI's handle, loaded once (2026-11-14, the review's #44: each property read loaded it again), as ImageIONative's handles are;
    // TryLoad rather than Load, so a Mac without it still reads every property as null instead of throwing. 0 when it isn't there.
    private static readonly Lazy<nint> CoreMidiHandle = new(() => NativeLibrary.TryLoad(CoreMidi, out nint library) ? library : 0);

    // A property key: an exported CFStringRef variable, its address the export.
    private static nint? Property(string name)
    {
        nint library = CoreMidiHandle.Value;
        if (library == 0 || !NativeLibrary.TryGetExport(library, name, out nint export))
        {
            return null;
        }

        nint value = *(nint*)export;
        return value == 0 ? null : value;
    }

    /// <summary>
    /// Each packet's bytes in <paramref name="list"/> (a <c>MIDIPacketList</c>): <c>numPackets</c>, then packets of a 64-bit time stamp, a
    /// 16-bit length and the data; on arm64 the list is packed to 4 and each packet starts 4-aligned (<c>MIDIPacketNext</c>).
    /// </summary>
    public static void Packets(byte* list, Action<ReadOnlySpan<byte>> each)
    {
        uint count = *(uint*)list;
        byte* packet = list + 4;
        bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        for (uint i = 0; i < count; i++)
        {
            ushort length = *(ushort*)(packet + 8);
            byte* data = packet + 10;
            each(new ReadOnlySpan<byte>(data, length));
            nuint next = (nuint)(data + length);
            packet = (byte*)(arm ? (next + 3) & ~(nuint)3 : next);
        }
    }
}
