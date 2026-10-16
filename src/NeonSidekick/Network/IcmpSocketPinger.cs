using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NeonSidekick.Network;

/// <summary>
/// The echo off Windows (2026-10-15, the admin-tools plan): our own ICMPv4 echo over an unprivileged <c>SOCK_DGRAM</c>/<c>IPPROTO_ICMP</c>
/// socket. Required, not a nicety: dotnet/runtime's <c>Ping.Unix.cs</c> uses a raw socket only when <c>RawSocketPermissions.CanUseRawSockets</c>
/// says so (it probes <c>SocketType.Raw</c> alone) and otherwise starts <c>/sbin/ping</c> (<c>SendWithPingUtility</c>) — a process start, which
/// this app does not make. macOS's <c>icmp(4)</c> lets a datagram ICMP socket send echo, timestamp and mask requests without root and says it
/// can receive Time Exceeded, which a trace needs; that last part is still to be proven on a Mac (<c>.notes/mac_todo.md</c>): if it fails,
/// <see cref="CanTrace"/> goes false and <c>net_trace</c> is left out there. IPv4 only in v1. The reply is read with or without its IP header
/// (<see cref="IcmpPacket.Parse"/>). Excluded from coverage: proven by the smoke's <c>net:icmp</c> on a Mac.
/// </summary>
public sealed class IcmpSocketPinger : IPinger
{
    private static readonly byte[] Payload = "NeonSidekick ping"u8.ToArray();
    private static int s_sequence;

    public bool CanTrace => true;

    public async Task<PingHop> PingAsync(IPAddress address, int ttl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        address = HostRules.Canonical(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return new PingHop(PingStatus.Failed, null, null, NetworkText.NoIpv6Ping);
        }

        ushort id = (ushort)RandomNumberGenerator.GetInt32(1, 0xFFFF);
        ushort sequence = (ushort)(Interlocked.Increment(ref s_sequence) & 0xFFFF);
        byte[] packet = IcmpPacket.Echo(id, sequence, Payload);
        Socket socket;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Icmp);
        }
        catch (SocketException ex)
        {
            return new PingHop(PingStatus.Failed, null, null, ex.Message);
        }

        using (socket)
        {
            socket.Ttl = (short)Math.Clamp(ttl, 1, 255);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            long started = Stopwatch.GetTimestamp();
            try
            {
                await socket.SendToAsync(packet, SocketFlags.None, new IPEndPoint(address, 0), budget.Token).ConfigureAwait(false);
                var buffer = new byte[1500];
                while (true)
                {
                    var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), budget.Token).ConfigureAwait(false);
                    var (kind, _) = IcmpPacket.Parse(buffer.AsSpan(0, received.ReceivedBytes), id, sequence);
                    var from = (received.RemoteEndPoint as IPEndPoint)?.Address;
                    var rtt = Stopwatch.GetElapsedTime(started);
                    switch (kind)
                    {
                        case IcmpReplyKind.EchoReply:
                            return new PingHop(PingStatus.Reply, from ?? address, rtt);
                        case IcmpReplyKind.TimeExceeded:
                            return new PingHop(PingStatus.TtlExpired, from, rtt);
                        case IcmpReplyKind.Unreachable:
                            return new PingHop(PingStatus.Unreachable, from, rtt);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new PingHop(PingStatus.TimedOut, null, null);
            }
            catch (SocketException ex)
            {
                return new PingHop(PingStatus.Failed, null, null, ex.Message);
            }
        }
    }
}
