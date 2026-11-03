using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NeonSidekick.Network;

/// <summary>
/// A lookup asked of a Mac's own resolver, mDNSResponder, through dns_sd's <c>DNSServiceQueryRecord</c> (2026-11-03, a VPN domain's report:
/// .NET reads a Mac's DNS servers from <c>/etc/resolv.conf</c>, which lists neither a VPN's per-domain resolvers nor any scoped one, so the
/// DC lookup asked the public servers and found none). The daemon follows every resolver <c>scutil --dns</c> shows, as the system's own
/// name lookups do. One lookup is one service reference, started, read and deallocated on one pool thread (the header warns that a
/// deallocation while another thread is in <c>DNSServiceProcessResult</c> crashes): its socket is polled every <see cref="PollMillis"/>
/// so Esc and <see cref="DnsClient.QueryBudget"/> end the wait, and <c>DNSServiceProcessResult</c> runs only when the socket is readable,
/// calling <see cref="Reply"/> on this thread for each record. What the callbacks mean is <see cref="DnsServiceReplies"/>' (pure, tested on
/// every OS). No process-start site, no shipped library: libSystem's. Excluded from coverage with the other native layers; proven by the
/// smoke's <c>net:dnssd</c> on the published exe and the Mac's <c>MacDnsServiceTests</c>.
/// </summary>
[SupportedOSPlatform("macos")]
public static unsafe partial class MacDnsService
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const ushort ClassIn = 1;
    private const short PollIn = 0x1;   // POLLIN
    private const int Interrupted = 4;   // EINTR

    /// <summary>How long one poll of the daemon's socket waits before the token and the budget are looked at again.</summary>
    public const int PollMillis = 100;

    /// <summary>
    /// <paramref name="name"/>'s <paramref name="type"/> records from the system resolver; an outcome with <see cref="DnsOutcome.Error"/> when
    /// the lookup fails; null when the daemon could not be asked at all (it is not running, or its connection is gone), so the caller can ask
    /// the servers it knows instead.
    /// </summary>
    public static Task<DnsOutcome?> QueryAsync(string name, ushort type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Task.Run(() => Query(name.Trim().TrimEnd('.'), type, DnsClient.QueryBudget, cancellationToken), cancellationToken);
    }

    /// <summary>Whether a start's <paramref name="code"/> says the daemon cannot be reached, rather than that the question was bad.</summary>
    public static bool Unreachable(int code) => code is DnsServiceReplies.ServiceNotRunning or DnsServiceReplies.DefunctConnection;

    private static DnsOutcome? Query(string name, ushort type, TimeSpan budget, CancellationToken cancellationToken)
    {
        var replies = new DnsServiceReplies(name, type);
        var handle = GCHandle.Alloc(replies);
        IntPtr service = IntPtr.Zero;
        try
        {
            int started;
            fixed (byte* fullname = Encoding.UTF8.GetBytes(name + "\0"))
            {
                started = QueryRecord(&service, DnsServiceReplies.ReturnIntermediates | DnsServiceReplies.TimeoutFlag, 0, fullname, type, ClassIn,
                    &Reply, GCHandle.ToIntPtr(handle));
            }

            if (started != DnsServiceReplies.NoError || service == IntPtr.Zero)
            {
                return Unreachable(started) ? null : new DnsOutcome(null, null, NetworkText.DnsServiceFailed(started), Resolver: NetworkText.SystemResolver);
            }

            int fd = SockFd(service);
            long deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;
            while (!replies.Done && !cancellationToken.IsCancellationRequested && Environment.TickCount64 < deadline)
            {
                var poll = new PollFd { Fd = fd, Events = PollIn };
                int ready = Poll(&poll, 1, PollMillis);
                if (ready < 0 && Marshal.GetLastPInvokeError() != Interrupted)
                {
                    break;
                }

                if (ready > 0 && (poll.Revents & PollIn) != 0)
                {
                    int processed = ProcessResult(service);
                    if (processed != DnsServiceReplies.NoError)
                    {
                        return new DnsOutcome(null, null, NetworkText.DnsServiceFailed(processed), Resolver: NetworkText.SystemResolver);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return replies.Outcome();
        }
        finally
        {
            if (service != IntPtr.Zero)
            {
                Deallocate(service);
            }

            handle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Reply(IntPtr service, uint flags, uint interfaceIndex, int error, byte* fullname, ushort rrtype, ushort rrclass, ushort rdlen,
        byte* rdata, uint ttl, IntPtr context)
    {
        // Nothing may throw out of a callback the daemon's library makes: an exception there ends a NativeAOT process.
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not DnsServiceReplies replies)
            {
                return;
            }

            string name = fullname is null ? "" : Marshal.PtrToStringUTF8((IntPtr)fullname) ?? "";
            var data = rdata is null ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(rdata, rdlen);
            replies.Add(flags, error, name, rrtype, data, ttl);
        }
        catch (Exception)
        {
            // A record this reader cannot take is left out; the lookup goes on.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport(LibSystem, EntryPoint = "DNSServiceQueryRecord")]
    private static partial int QueryRecord(IntPtr* service, uint flags, uint interfaceIndex, byte* fullname, ushort rrtype, ushort rrclass,
        delegate* unmanaged[Cdecl]<IntPtr, uint, uint, int, byte*, ushort, ushort, ushort, byte*, uint, IntPtr, void> callback, IntPtr context);

    [LibraryImport(LibSystem, EntryPoint = "DNSServiceRefSockFD")]
    private static partial int SockFd(IntPtr service);

    [LibraryImport(LibSystem, EntryPoint = "DNSServiceProcessResult")]
    private static partial int ProcessResult(IntPtr service);

    [LibraryImport(LibSystem, EntryPoint = "DNSServiceRefDeallocate")]
    private static partial void Deallocate(IntPtr service);

    [LibraryImport(LibSystem, EntryPoint = "poll", SetLastError = true)]
    private static partial int Poll(PollFd* fds, uint count, int timeoutMillis);
}
