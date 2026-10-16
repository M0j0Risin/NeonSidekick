using System.Net;
using System.Net.Sockets;

namespace NeonSidekick.Network;

/// <summary>A port's state on one address.</summary>
public enum PortState
{
    Open,
    Refused,
    NoAnswer,
    Unreachable,
}

/// <summary>One port on one address.</summary>
public sealed record PortResult(IPAddress Address, int Port, PortState State);

/// <summary>One hop of a trace: its number, who answered (null for nobody), the round trip, the name when asked for, and whether it was the target.</summary>
public sealed record TraceHop(int Ttl, IPAddress? From, TimeSpan? Rtt, string? Name, bool Reached, bool Unreachable);

/// <summary>
/// The connection checks behind <c>net_port</c> and <c>net_trace</c> (2026-10-15, the admin-tools plan): the TCP connect on every admitted
/// address — the ::1 lesson from <c>LlmEndpointProbe</c>, a server listening on one family alone — and the trace's walk up the TTLs. The
/// connect function is injected, so the tests drive refusals and silences without a network.
/// </summary>
public static class NetworkProbes
{
    /// <summary>The wait for one address.</summary>
    public static readonly TimeSpan PortTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The whole <c>net_port</c> call.</summary>
    public static readonly TimeSpan PortsBudget = TimeSpan.FromSeconds(15);

    /// <summary>The wait for one hop.</summary>
    public static readonly TimeSpan HopTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The whole <c>net_trace</c> call.</summary>
    public static readonly TimeSpan TraceBudget = TimeSpan.FromSeconds(45);

    /// <summary>The wait for one hop's name.</summary>
    public static readonly TimeSpan NameTimeout = TimeSpan.FromSeconds(1);

    /// <summary>A TCP connect, the real one: the socket's error read into a <see cref="PortState"/>.</summary>
    public static async Task<PortState> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(PortTimeout);
        try
        {
            await socket.ConnectAsync(address, port, budget.Token).ConfigureAwait(false);
            return PortState.Open;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PortState.NoAnswer;
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => PortState.Refused,
                SocketError.TimedOut => PortState.NoAnswer,
                _ => PortState.Unreachable,
            };
        }
    }

    /// <summary>Every port on every address, in parallel (16 ports at most, so at most 16 × addresses connects), inside <see cref="PortsBudget"/>.</summary>
    public static async Task<IReadOnlyList<PortResult>> PortsAsync(IReadOnlyList<IPAddress> addresses, IReadOnlyList<int> ports, Func<IPAddress, int, CancellationToken, Task<PortState>> connect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(connect);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(PortsBudget);
        var work = new List<Task<PortResult>>();
        foreach (int port in ports)
        {
            foreach (var address in addresses)
            {
                work.Add(One(address, port));
            }
        }

        return await Task.WhenAll(work).ConfigureAwait(false);

        async Task<PortResult> One(IPAddress address, int port)
        {
            try
            {
                return new PortResult(address, port, await connect(address, port, budget.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new PortResult(address, port, PortState.NoAnswer);
            }
        }
    }

    /// <summary>
    /// The trace to <paramref name="target"/>: TTL 1 up to <paramref name="maxHops"/>, one echo a hop (<see cref="HopTimeout"/>), stopping at the
    /// target's reply or an unreachable, all inside <see cref="TraceBudget"/>; with <paramref name="names"/> each answering hop's reverse name
    /// (<see cref="NameTimeout"/>). Hop addresses are reported as they come, never judged.
    /// </summary>
    public static async Task<IReadOnlyList<TraceHop>> TraceAsync(IPinger pinger, IPAddress target, int maxHops, bool names, Func<IPAddress, CancellationToken, Task<string?>> reverse, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pinger);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(reverse);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TraceBudget);
        var hops = new List<TraceHop>(maxHops);
        try
        {
            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                var hop = await pinger.PingAsync(target, ttl, HopTimeout, budget.Token).ConfigureAwait(false);
                string? name = null;
                if (names && hop.From is { } from)
                {
                    using var nameBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                    nameBudget.CancelAfter(NameTimeout);
                    try
                    {
                        name = await reverse(from, nameBudget.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!budget.IsCancellationRequested)
                    {
                        name = null;
                    }
                }

                bool reached = hop.Status == PingStatus.Reply;
                bool unreachable = hop.Status == PingStatus.Unreachable;
                hops.Add(new TraceHop(ttl, hop.From, hop.Status is PingStatus.TimedOut or PingStatus.Failed ? null : hop.Rtt, name, reached, unreachable));
                if (reached || unreachable)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The budget ran out: the hops so far are the answer.
        }

        return hops;
    }

    /// <summary>The reverse name of <paramref name="address"/> through the system resolver; null when it has none.</summary>
    public static async Task<string?> ReverseAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(entry.HostName) || entry.HostName == address.ToString() ? null : entry.HostName;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
