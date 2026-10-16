using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace NeonSidekick.Network;

/// <summary>
/// The echo on Windows (2026-10-15, the admin-tools plan): .NET's <see cref="Ping"/>, which calls <c>IcmpSendEcho2</c> in-process — no raw
/// socket, no elevation, no process. A TTL that runs out comes back as <see cref="IPStatus.TtlExpired"/> with the router's address, so a
/// trace works. The round trip is timed here (Windows reports 0 for an expired TTL). Never used off Windows: <see cref="IcmpSocketPinger"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPinger : IPinger
{
    private static readonly byte[] Payload = "NeonSidekick ping"u8.ToArray();

    public bool CanTrace => true;

    public async Task<PingHop> PingAsync(IPAddress address, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        using var ping = new Ping();
        long started = Stopwatch.GetTimestamp();
        PingReply reply;
        try
        {
            reply = await ping.SendPingAsync(address, timeout, Payload, new PingOptions(ttl, dontFragment: false), cancellationToken).ConfigureAwait(false);
        }
        catch (PingException ex)
        {
            return new PingHop(PingStatus.Failed, null, null, ex.InnerException?.Message ?? ex.Message);
        }

        var rtt = Stopwatch.GetElapsedTime(started);
        IPAddress? from = reply.Address is { } a && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any) ? a : null;
        return reply.Status switch
        {
            IPStatus.Success => new PingHop(PingStatus.Reply, from ?? address, reply.RoundtripTime > 0 ? TimeSpan.FromMilliseconds(reply.RoundtripTime) : rtt),
            IPStatus.TtlExpired or IPStatus.TimeExceeded or IPStatus.TtlReassemblyTimeExceeded => new PingHop(PingStatus.TtlExpired, from, rtt),
            IPStatus.TimedOut => new PingHop(PingStatus.TimedOut, null, null),
            IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationUnreachable
                or IPStatus.DestinationPortUnreachable or IPStatus.DestinationProtocolUnreachable or IPStatus.DestinationProhibited
                or IPStatus.DestinationScopeMismatch => new PingHop(PingStatus.Unreachable, from, rtt, reply.Status.ToString()),
            _ => new PingHop(PingStatus.Failed, from, null, reply.Status.ToString()),
        };
    }
}
