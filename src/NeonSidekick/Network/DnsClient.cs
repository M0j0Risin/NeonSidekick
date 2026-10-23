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
/// dropped and the wait goes on. 3 s per server, 2 tries, then the next server (after a SERVFAIL or REFUSED too); a truncated reply is asked again once over TCP (5 s).
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
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }

        foreach (var adapter in adapters)
        {
            // Each adapter read alone (2026-10-23, the admin review's U9: one that threw discarded the servers already found).
            try
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
            catch (NetworkInformationException)
            {
                // That adapter's servers unknown; the others stand.
            }
        }

        return found.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).Select(a => new IPEndPoint(a, Port)).ToList();
    }

    /// <summary>
    /// Asks each of <paramref name="servers"/> in turn until one answers (a server's failure or refusal moving on to the next); the first such
    /// refusal, else the last failure's sentence, when none does.
    /// </summary>
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

        // A server that failed or refused (SERVFAIL, REFUSED, FORMERR, NOTIMP) is a reason to ask the next, as a resolver does (2026-10-19,
        // the network review: a VPN's resolver first that refuses public names ended the lookup); its reply is kept and given when no
        // server answers better. NXDOMAIN and no error are answers.
        DnsOutcome last = new(null, null, NetworkText.DnsNoAnswer(servers[0].Address.ToString()));
        DnsOutcome? failed = null;

        // One limit over the whole lookup (2026-10-23, the admin review's I5: 2 × 3 s per resolver in turn, ten dead ones about a minute).
        using var whole = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        whole.CancelAfter(QueryBudget);
        foreach (var server in servers)
        {
            if (whole.IsCancellationRequested)
            {
                return failed ?? new DnsOutcome(null, null, NetworkText.DnsTookTooLong);
            }

            try
            {
                last = await AskAsync(server, name, type, whole.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return failed ?? new DnsOutcome(null, null, NetworkText.DnsTookTooLong);
            }

            if (last.Reply is { } reply)
            {
                if (!ServerFailed(reply.Rcode))
                {
                    return last;
                }

                failed ??= last;
            }
        }

        return failed ?? last;
    }

    /// <summary>Whether <paramref name="rcode"/> says the server could not or would not answer (FORMERR, SERVFAIL, NOTIMP, REFUSED), not that the name has none.</summary>
    public static bool ServerFailed(int rcode) => rcode is 1 or 2 or 4 or 5;

    /// <summary>The longest a whole lookup takes over every server (I5).</summary>
    public static readonly TimeSpan QueryBudget = TimeSpan.FromSeconds(10);

    /// <summary>One server: UDP with <see cref="Tries"/> tries, TCP once when the reply is truncated; after a FORMERR, once more without EDNS (H7).</summary>
    public static async Task<DnsOutcome> AskAsync(IPEndPoint server, string name, ushort type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        bool edns = true;
        for (int attempt = 0; attempt < Tries; attempt++)
        {
            ushort id = (ushort)RandomNumberGenerator.GetInt32(0, 0x10000);
            var reply = await UdpAsync(server, id, name, type, cancellationToken, edns).ConfigureAwait(false);
            if (reply is null)
            {
                continue;
            }

            if (reply.Rcode == 1 && edns)
            {
                // A server that does not take EDNS says FORMERR (RFC 6891 §6.2.2): asked again plainly, that answer is the server's.
                edns = false;
                ushort plainId = (ushort)RandomNumberGenerator.GetInt32(0, 0x10000);
                reply = await UdpAsync(server, plainId, name, type, cancellationToken, edns: false).ConfigureAwait(false) ?? reply;
            }

            if (reply.Truncated)
            {
                ushort tcpId = (ushort)RandomNumberGenerator.GetInt32(0, 0x10000);
                // Over TCP as plainly as the reply was asked (2026-10-23, the branch review of DP7: EDNS again drew the FORMERR back).
                var whole = await TcpAsync(server, tcpId, name, type, cancellationToken, edns).ConfigureAwait(false);
                return whole is null ? new DnsOutcome(reply, server, null) : new DnsOutcome(whole, server, null, OverTcp: true);
            }

            return new DnsOutcome(reply, server, null);
        }

        return new DnsOutcome(null, server, NetworkText.DnsNoAnswer(server.Address.ToString()));
    }

    /// <summary>
    /// Whether <paramref name="reply"/> answers our query: the id, the QR bit, the question echoed (name case ignored, type, class) — or, for a
    /// server's refusal (FORMERR, SERVFAIL, NOTIMP, REFUSED), no question at all, which no rule requires it to echo (2026-10-23, the admin
    /// review's H7: those were dropped and read as no answer).
    /// </summary>
    public static bool Answers(DnsMessage reply, ushort id, string name, ushort type) =>
        reply.Id == id && reply.IsResponse
        && ((reply.QuestionType == type && reply.QuestionClass == DnsWire.ClassIn && string.Equals(reply.QuestionName.TrimEnd('.'), name.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            || (reply.QuestionName.Length == 0 && reply.QuestionType == 0 && ServerFailed(reply.Rcode)));

    private static async Task<DnsMessage?> UdpAsync(IPEndPoint server, ushort id, string name, ushort type, CancellationToken cancellationToken, bool edns = true)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(UdpTimeout);
        var query = DnsWire.Query(id, name, type, edns);
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

    private static async Task<DnsMessage?> TcpAsync(IPEndPoint server, ushort id, string name, ushort type, CancellationToken cancellationToken, bool edns = true)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TcpTimeout);
        try
        {
            await socket.ConnectAsync(server, budget.Token).ConfigureAwait(false);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var query = DnsWire.Query(id, name, type, edns);
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
