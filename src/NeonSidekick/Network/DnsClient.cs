using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NeonSidekick.Network;

/// <summary>What one lookup came to: the reply and the server that gave it, or the sentence saying why not.</summary>
public sealed record DnsOutcome(DnsMessage? Reply, IPEndPoint? Server, string? Error, bool OverTcp = false);

/// <summary>
/// <c>net_dns</c>'s own client (2026-10-15, the admin-tools plan) for every type but a plain A/AAAA asked of the system: UDP first from a fresh
/// ephemeral socket, the query's id a random 16 bits from <see cref="RandomNumberGenerator"/>; a reply is taken only when it comes from the
/// server's address and port, carries the id, echoes the question (name case ignored, type, class) and has the QR bit set — anything else is
/// dropped and the wait goes on. 3 s per server, 2 tries, then the next server; a truncated reply is asked again once over TCP (5 s).
/// </summary>
public static class DnsClient
{
    public static readonly TimeSpan UdpTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(5);
    public const int Tries = 2;

    /// <summary>The DNS port.</summary>
    public const int Port = 53;

    /// <summary>
    /// The system's resolvers: every up adapter's DNS addresses, IPv4 first, the old site-local IPv6 placeholders (<c>fec0:0:0:ffff::</c>)
    /// left out, each once.
    /// </summary>
    public static IReadOnlyList<IPEndPoint> SystemServers()
    {
        var found = new List<IPAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var address in adapter.GetIPProperties().DnsAddresses)
                {
                    if (address.IsIPv6SiteLocal || found.Contains(address))
                    {
                        continue;
                    }

                    found.Add(address);
                }
            }
        }
        catch (NetworkInformationException)
        {
            return [];
        }

        return found.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).Select(a => new IPEndPoint(a, Port)).ToList();
    }

    /// <summary>Asks each of <paramref name="servers"/> in turn until one answers; the last failure's sentence when none does.</summary>
    public static async Task<DnsOutcome> QueryAsync(IReadOnlyList<IPEndPoint> servers, string name, ushort type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(name);
        if (servers.Count == 0)
        {
            return new DnsOutcome(null, null, NetworkText.NoDnsServers);
        }

        try
        {
            // The name checked once before any server is asked.
            _ = DnsWire.EncodeName(name);
        }
        catch (ArgumentException)
        {
            return new DnsOutcome(null, null, NetworkText.BadName(name));
        }

        DnsOutcome last = new(null, null, NetworkText.DnsNoAnswer(servers[0].Address.ToString()));
        foreach (var server in servers)
        {
            last = await AskAsync(server, name, type, cancellationToken).ConfigureAwait(false);
            if (last.Reply is not null)
            {
                return last;
            }
        }

        return last;
    }

    /// <summary>One server: UDP with <see cref="Tries"/> tries, TCP once when the reply is truncated.</summary>
    public static async Task<DnsOutcome> AskAsync(IPEndPoint server, string name, ushort type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        for (int attempt = 0; attempt < Tries; attempt++)
        {
            ushort id = (ushort)RandomNumberGenerator.GetInt32(0, 0x10000);
            var reply = await UdpAsync(server, id, name, type, cancellationToken).ConfigureAwait(false);
            if (reply is null)
            {
                continue;
            }

            if (reply.Truncated)
            {
                ushort tcpId = (ushort)RandomNumberGenerator.GetInt32(0, 0x10000);
                var whole = await TcpAsync(server, tcpId, name, type, cancellationToken).ConfigureAwait(false);
                return whole is null ? new DnsOutcome(reply, server, null) : new DnsOutcome(whole, server, null, OverTcp: true);
            }

            return new DnsOutcome(reply, server, null);
        }

        return new DnsOutcome(null, server, NetworkText.DnsNoAnswer(server.Address.ToString()));
    }

    /// <summary>Whether <paramref name="reply"/> answers our query: the id, the QR bit, the question echoed (name case ignored, type, class).</summary>
    public static bool Answers(DnsMessage reply, ushort id, string name, ushort type) =>
        reply.Id == id && reply.IsResponse && reply.QuestionType == type && reply.QuestionClass == DnsWire.ClassIn
        && string.Equals(reply.QuestionName.TrimEnd('.'), name.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static async Task<DnsMessage?> UdpAsync(IPEndPoint server, ushort id, string name, ushort type, CancellationToken cancellationToken)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(UdpTimeout);
        var query = DnsWire.Query(id, name, type);
        var buffer = new byte[4096];
        try
        {
            await socket.SendToAsync(query, SocketFlags.None, server, budget.Token).ConfigureAwait(false);
            var any = new IPEndPoint(server.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
            while (true)
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, budget.Token).ConfigureAwait(false);
                if (received.RemoteEndPoint is not IPEndPoint from || !from.Address.Equals(server.Address) || from.Port != server.Port)
                {
                    continue;
                }

                DnsMessage reply;
                try
                {
                    reply = DnsWire.Parse(buffer.AsSpan(0, received.ReceivedBytes));
                }
                catch (DnsFormatException)
                {
                    continue;
                }

                if (Answers(reply, id, name, type))
                {
                    return reply;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static async Task<DnsMessage?> TcpAsync(IPEndPoint server, ushort id, string name, ushort type, CancellationToken cancellationToken)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TcpTimeout);
        try
        {
            await socket.ConnectAsync(server, budget.Token).ConfigureAwait(false);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var query = DnsWire.Query(id, name, type);
            var framed = new byte[query.Length + 2];
            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
            query.CopyTo(framed, 2);
            await stream.WriteAsync(framed, budget.Token).ConfigureAwait(false);
            var lengthBytes = new byte[2];
            await stream.ReadExactlyAsync(lengthBytes, budget.Token).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, budget.Token).ConfigureAwait(false);
            var reply = DnsWire.Parse(body);
            return Answers(reply, id, name, type) ? reply : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or DnsFormatException)
        {
            return null;
        }
    }
}
