using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Midi;

/// <summary>
/// The MIDI inputs over WinMM (2026-11-12, the MIDI plan). On the classic driver stack an input opens for one app at a time
/// (<c>MMSYSERR_ALLOCATED</c> becomes <see cref="MidiException.InUse"/>); under Windows MIDI Services (Windows 11 24H2 and later) the
/// same names are multi-client. The driver's callback only queues the packed <c>MIM_DATA</c> word and sets an event: MIDI callbacks must
/// not call into the system. Each input's own pump thread turns the words into bytes and hands them on, as
/// <see cref="Audio.WinMmAudioCapture"/>'s pump does. Proven by <c>midi:winmm-in</c> and <c>--midi-check</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsMidiSystem : IMidiSystem
{
    private const string Category = "MIDI";

    public IReadOnlyList<MidiInputInfo> Inputs()
    {
        uint count = WinMmMidiNative.midiInGetNumDevs();
        var list = new List<MidiInputInfo>((int)Math.Min(count, 64));
        for (uint i = 0; i < count; i++)
        {
            string name = WinMmMidiNative.NameOf(i) is { Length: > 0 } read ? read : "MIDI input " + (i + 1).ToString(CultureInfo.InvariantCulture);
            list.Add(new MidiInputInfo((int)i, name));
        }

        return list;
    }

    public IMidiInput Open(MidiInputInfo input, Action<ReadOnlySpan<byte>> onBytes)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(onBytes);
        if (input.Index < 0 || (uint)input.Index >= WinMmMidiNative.midiInGetNumDevs()
            || WinMmMidiNative.NameOf((uint)input.Index) is not { } now || !MidiNames.Same(now, input.Name))
        {
            throw new MidiException(MidiText.Gone);
        }

        return WinMmInput.Open(input, onBytes);
    }

    /// <summary>The user's sentence for a WinMM result.</summary>
    internal static MidiException Failure(int result) => result switch
    {
        WinMmMidiNative.MmsyserrAllocated => new MidiException(MidiText.InUse) { InUse = true },
        WinMmMidiNative.MmsyserrBadDeviceId => new MidiException(MidiText.Gone),
        WinMmMidiNative.MmsyserrNoDriver => new MidiException(MidiText.NoDriver),
        _ => new MidiException(MidiText.OpenFailed("MMSYSERR " + result.ToString(CultureInfo.InvariantCulture))),
    };

    private sealed class WinMmInput : IMidiInput
    {
        private readonly Action<ReadOnlySpan<byte>> _onBytes;
        private readonly ConcurrentQueue<uint> _words = new();

        // Every other message the driver sends while the input runs and isn't being closed (2026-11-14, the review's #8): logged at Debug by the
        // pump, never in the callback, so a replug's hand check can tell whether WinMM says anything (MIM_CLOSE, MIM_ERROR) when a device goes.
        private readonly ConcurrentQueue<uint> _unasked = new();
        private readonly AutoResetEvent _ready = new(false);
        private volatile bool _closing;
        private readonly Lock _gate = new();
        private GCHandle _self;
        private IntPtr _device;
        private Thread? _pump;
        private volatile bool _running;
        private bool _disposed;

        private WinMmInput(MidiInputInfo info, Action<ReadOnlySpan<byte>> onBytes)
        {
            Info = info;
            _onBytes = onBytes;
        }

        public MidiInputInfo Info { get; }

        public static WinMmInput Open(MidiInputInfo info, Action<ReadOnlySpan<byte>> onBytes)
        {
            var input = new WinMmInput(info, onBytes);
            input._self = GCHandle.Alloc(input);
            IntPtr device;
            int opened = WinMmMidiNative.midiInOpen(&device, (uint)info.Index, &OnMidi, GCHandle.ToIntPtr(input._self), WinMmMidiNative.CallbackFunction);
            if (opened != WinMmMidiNative.MmsyserrNoError)
            {
                input._self.Free();
                input._ready.Dispose();
                throw Failure(opened);
            }

            input._device = device;
            input._running = true;
            input._pump = new Thread(input.Pump) { IsBackground = true, Name = "MIDI in " + info.Index.ToString(CultureInfo.InvariantCulture) };
            input._pump.Start();
            int started = WinMmMidiNative.midiInStart(device);
            if (started != WinMmMidiNative.MmsyserrNoError)
            {
                input.Dispose();
                throw Failure(started);
            }

            return input;
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

            _closing = true;   // our own close's MIM_CLOSE is asked for, so never logged as the driver's

            // Stop, reset and close: after midiInClose returns the driver calls back no more; then the pump drains and ends.
            if (_device != IntPtr.Zero)
            {
                _ = WinMmMidiNative.midiInStop(_device);
                _ = WinMmMidiNative.midiInReset(_device);
                int closed = WinMmMidiNative.midiInClose(_device);
                if (closed != WinMmMidiNative.MmsyserrNoError)
                {
                    DiagnosticLog.Warn(Category, "midiInClose returned " + closed.ToString(CultureInfo.InvariantCulture) + ".");
                }

                _device = IntPtr.Zero;
            }

            _running = false;
            _ready.Set();
            if (_pump is { } pump && pump != Thread.CurrentThread)
            {
                pump.Join();
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }

            _ready.Dispose();
        }

        private void Pump()
        {
            Span<byte> bytes = stackalloc byte[3];
            while (true)
            {
                _ready.WaitOne();
                while (_unasked.TryDequeue(out uint unasked))
                {
                    DiagnosticLog.Debug(Category, MidiText.UnaskedMessageLogLine(Info.Name, unasked));
                }

                while (_running && _words.TryDequeue(out uint word))
                {
                    bytes[0] = (byte)word;
                    bytes[1] = (byte)(word >> 8);
                    bytes[2] = (byte)(word >> 16);
                    try
                    {
                        _onBytes(bytes[..WinMmMidiNative.ShortLength(bytes[0])]);
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

        /// <summary>The driver's callback: queue and wake, nothing else (no system call but <c>SetEvent</c>); nothing may escape it.</summary>
        [UnmanagedCallersOnly]
        private static void OnMidi(IntPtr device, uint message, IntPtr instance, IntPtr param1, IntPtr param2)
        {
            try
            {
                if (instance == IntPtr.Zero)
                {
                    return;
                }

                if (message is not (WinMmMidiNative.MimData or WinMmMidiNative.MimMoreData))
                {
                    if (GCHandle.FromIntPtr(instance).Target is WinMmInput other && other._running && !other._closing)
                    {
                        other._unasked.Enqueue(message);
                        other._ready.Set();
                    }

                    return;
                }

                if (GCHandle.FromIntPtr(instance).Target is WinMmInput input && input._running)
                {
                    input._words.Enqueue((uint)(nuint)param1);
                    input._ready.Set();
                }
            }
            catch (Exception)
            {
                // Nothing escapes a callback; a lost message is a press missed.
            }
        }
    }
}
