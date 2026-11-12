using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Midi;

/// <summary>
/// The MIDI inputs over CoreMIDI (2026-11-12, the MIDI plan). Sources are shared: another app hears the same pedal. One client for the
/// process, made at the first open (so a start with MIDI off never starts the MIDI server); one input port per open input, its read proc
/// keyed by a number rather than a <see cref="GCHandle"/>, so a packet that arrives as the port goes finds nothing instead of a freed
/// handle. The read proc copies each packet's bytes and wakes the input's pump, which hands them on (a packet may hold several messages;
/// <see cref="MidiParser"/> splits them). Proven by <c>midi:coremidi</c> and <c>--midi-check</c> on the Mac.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacMidiSystem : IMidiSystem
{
    private const string Category = "MIDI";

    private static readonly ConcurrentDictionary<long, MacInput> s_inputs = new();
    private static long s_next;

    private readonly Lock _gate = new();
    private uint _client;

    public IReadOnlyList<MidiInputInfo> Inputs()
    {
        nuint count = CoreMidiNative.MIDIGetNumberOfSources();
        var list = new List<MidiInputInfo>((int)Math.Min(count, 64));
        for (nuint i = 0; i < count; i++)
        {
            uint source = CoreMidiNative.MIDIGetSource(i);
            string name = source != 0 && CoreMidiNative.NameOf(source) is { } read ? read : "MIDI input " + ((ulong)i + 1).ToString(CultureInfo.InvariantCulture);
            list.Add(new MidiInputInfo((int)i, name));
        }

        return list;
    }

    public IMidiInput Open(MidiInputInfo input, Action<ReadOnlySpan<byte>> onBytes)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(onBytes);
        if (input.Index < 0 || (nuint)input.Index >= CoreMidiNative.MIDIGetNumberOfSources())
        {
            throw new MidiException(MidiText.Gone);
        }

        uint source = CoreMidiNative.MIDIGetSource((nuint)input.Index);
        if (source == 0 || CoreMidiNative.NameOf(source) is not { } now || !MidiNames.Same(now, input.Name))
        {
            throw new MidiException(MidiText.Gone);
        }

        uint client = Client();
        long id = Interlocked.Increment(ref s_next);
        var opened = new MacInput(input, onBytes, id);
        s_inputs[id] = opened;
        nint name = 0;
        try
        {
            name = CfString("NeonSidekick MIDI in");
            uint port;
            int created = CoreMidiNative.MIDIInputPortCreate(client, name, &OnPackets, (nint)id, &port);
            if (created != 0)
            {
                throw new MidiException(MidiText.OpenFailed("OSStatus " + created.ToString(CultureInfo.InvariantCulture)));
            }

            opened.Port = port;
            int connected = CoreMidiNative.MIDIPortConnectSource(port, source, 0);
            if (connected != 0)
            {
                throw new MidiException(MidiText.OpenFailed("OSStatus " + connected.ToString(CultureInfo.InvariantCulture)));
            }

            opened.Source = source;
            opened.Start();
            return opened;
        }
        catch
        {
            opened.Dispose();
            throw;
        }
        finally
        {
            if (name != 0)
            {
                Images.ImageIONative.CFRelease(name);
            }
        }
    }

    /// <summary>The process's client, made once; a failure is the user's sentence (the server couldn't start, as on a runner).</summary>
    private uint Client()
    {
        lock (_gate)
        {
            if (_client != 0)
            {
                return _client;
            }

            nint name = CfString("NeonSidekick");
            try
            {
                uint client;
                int status = CoreMidiNative.MIDIClientCreate(name, 0, 0, &client);
                if (status != 0)
                {
                    throw new MidiException(MidiText.NoMidiServer(status));
                }

                _client = client;
                return client;
            }
            finally
            {
                Images.ImageIONative.CFRelease(name);
            }
        }
    }

    private static nint CfString(string text)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* p = bytes)
        {
            return Images.ImageIONative.CFStringCreateWithCString(0, p, Images.ImageIONative.Utf8);
        }
    }

    /// <summary>The read proc, on CoreMIDI's thread: copy each packet and wake the pump; nothing may escape it.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPackets(byte* list, nint readRefCon, nint sourceRefCon)
    {
        try
        {
            if (list != null && s_inputs.TryGetValue(readRefCon, out var input) && input.Running)
            {
                CoreMidiNative.Packets(list, input.Queue);
                input.Wake();
            }
        }
        catch (Exception)
        {
            // A lost packet is a press missed.
        }
    }

    private sealed class MacInput(MidiInputInfo info, Action<ReadOnlySpan<byte>> onBytes, long id) : IMidiInput
    {
        private readonly ConcurrentQueue<byte[]> _packets = new();
        private readonly AutoResetEvent _ready = new(false);
        private readonly Lock _gate = new();
        private Thread? _pump;
        private volatile bool _running;
        private bool _disposed;

        public MidiInputInfo Info { get; } = info;

        public uint Port { get; set; }

        public uint Source { get; set; }

        public bool Running => _running;

        public void Queue(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > 0)
            {
                _packets.Enqueue(bytes.ToArray());
            }
        }

        public void Wake() => _ready.Set();

        public void Start()
        {
            _running = true;
            _pump = new Thread(Pump) { IsBackground = true, Name = "MIDI in " + Info.Index.ToString(CultureInfo.InvariantCulture) };
            _pump.Start();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _running = false;
            s_inputs.TryRemove(id, out _);
            if (Port != 0)
            {
                if (Source != 0)
                {
                    _ = CoreMidiNative.MIDIPortDisconnectSource(Port, Source);
                }

                int disposed = CoreMidiNative.MIDIPortDispose(Port);
                if (disposed != 0)
                {
                    DiagnosticLog.Warn(Category, "MIDIPortDispose returned " + disposed.ToString(CultureInfo.InvariantCulture) + ".");
                }
            }

            _ready.Set();
            if (_pump is { } pump && pump != Thread.CurrentThread)
            {
                pump.Join();
            }

            _ready.Dispose();
        }

        private void Pump()
        {
            while (true)
            {
                _ready.WaitOne();
                while (_running && _packets.TryDequeue(out byte[]? bytes))
                {
                    try
                    {
                        onBytes(bytes);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Error(Category, "A MIDI message handler failed: " + ex.Message, ex);
                    }
                }

                if (!_running)
                {
                    return;
                }
            }
        }
    }
}
