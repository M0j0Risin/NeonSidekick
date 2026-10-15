using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.EventLog;

/// <summary>
/// The event logs through <c>wevtapi.dll</c> in-process (2026-10-15, the event-log plan): no process-start site, no shipped DLL, no new
/// package. <see cref="Query"/> (<c>.Query.cs</c>) opens a reverse query and hands its events over 32 at a time, halving the batch down to
/// one on <c>RPC_S_INVALID_BOUND</c> (1734, a large PowerShell 4104 over RPC); <see cref="Channels"/> and <see cref="Info"/>
/// (<c>.Channels.cs</c>) list and describe the logs; a remote target's session (<c>.Session.cs</c>) is opened per call with
/// <c>EvtRpcLogin</c> and closed with it.
///
/// <para><b>Buffers.</b> One unmanaged buffer per purpose per read, grown in place on 122 (<c>EvtRender</c> counts bytes,
/// <c>EvtFormatMessage</c> and <c>EvtNextChannelPath</c> characters); nothing is <c>stackalloc</c>ed in a loop, and every string is
/// copied out before the buffer is used again.</para>
///
/// <para><b>Cancel.</b> The token's <c>EvtCancel</c> is registered on the query handle only for a local or file read — the local session
/// is NULL, and cancelling it would cancel every local read in the process — and on a remote read's own session as well, so a call stuck
/// in <c>EvtQuery</c> against a host that does not answer ends. Registrations are disposed before any <c>EvtClose</c>; one
/// <c>finally</c> closes the events, the render contexts, the query, the publishers and the session, in that order.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsEventLogSystem : IEventLogSystem
{
    /// <summary>An unmanaged buffer grown in place; freed on dispose.</summary>
    internal sealed unsafe class NativeBuffer : IDisposable
    {
        public NativeBuffer(int size)
        {
            Size = Math.Max(16, size);
            Pointer = (byte*)NativeMemory.Alloc((nuint)Size);
        }

        public byte* Pointer { get; private set; }

        public int Size { get; private set; }

        /// <summary>At least <paramref name="needed"/> bytes; the contents are not kept.</summary>
        public void Ensure(int needed)
        {
            if (needed <= Size)
            {
                return;
            }

            NativeMemory.Free(Pointer);
            Size = Math.Max(needed, Size * 2);
            Pointer = (byte*)NativeMemory.Alloc((nuint)Size);
        }

        public void Dispose()
        {
            if (Pointer != null)
            {
                NativeMemory.Free(Pointer);
                Pointer = null;
            }
        }
    }

    /// <summary>Closes <paramref name="handle"/> when it is one.</summary>
    internal static void Close(nint handle)
    {
        if (handle != 0)
        {
            EventLogNative.EvtClose(handle);
        }
    }

    /// <summary>
    /// Copies <paramref name="channel"/>'s events matching <paramref name="xpath"/> to <paramref name="target"/> (an <c>.evtx</c>), this PC's
    /// own reads: the smoke's <c>eventlog:evtx</c> and the tests' fixture. Throws <see cref="EventLogException"/> on a refusal.
    /// </summary>
    public static void Export(string channel, string xpath, string target)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(target);
        if (!EventLogNative.EvtExportLog(0, channel, xpath, target, EventLogNative.QueryChannelPath))
        {
            int code = EventLogNative.LastError;
            throw new EventLogException(code, "EvtExportLog failed with " + code.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static EventLogException Failure(string call)
    {
        int code = EventLogNative.LastError;
        return new EventLogException(code, call + " failed with " + code.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
