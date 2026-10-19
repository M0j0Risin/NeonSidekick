using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace NeonSidekick.Web;

/// <summary>
/// The web tools' connect over a host's admitted addresses (2026-10-19, the Mac's pre-CI check: this network's IPv6 was routed nowhere,
/// and <c>web_fetch</c> of example.com sat its whole 30 s on the first v6 address while curl answered over v4 in 0.3 s), as RFC 8305's
/// "Happy Eyeballs" connects: the addresses taken turn about by family, the first family as resolved (<see cref="Interleave"/>); the
/// first attempt started at once, the next after <see cref="AttemptDelay"/> or as soon as one fails, every attempt left running; the
/// first to connect wins and the rest are cancelled, a loser that connects anyway closed. <c>Socket.ConnectAsync</c> over the list
/// tried each address only after the one before had failed, and a dead route never fails before TCP gives up (over a minute on macOS).
/// The judgement is untouched: <see cref="LanPolicy.Admit"/> decides the addresses before any connect.
/// </summary>
public static class HappyEyeballs
{
    /// <summary>How long an attempt runs alone before the next address is tried beside it: RFC 8305's recommended 250 ms.</summary>
    public static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>The order to try <paramref name="addresses"/> in: the first one's family, then the other, turn about, each family in its resolved order.</summary>
    public static IReadOnlyList<IPAddress> Interleave(IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0)
        {
            return [];
        }

        var first = addresses[0].AddressFamily;
        var leading = addresses.Where(a => a.AddressFamily == first).ToList();
        var other = addresses.Where(a => a.AddressFamily != first).ToList();
        var order = new List<IPAddress>(addresses.Count);
        for (int i = 0; i < Math.Max(leading.Count, other.Count); i++)
        {
            if (i < leading.Count)
            {
                order.Add(leading[i]);
            }

            if (i < other.Count)
            {
                order.Add(other[i]);
            }
        }

        return order;
    }

    /// <summary>
    /// The connected socket of the first of <paramref name="addresses"/> to answer, raced as the class says; when every attempt fails, the
    /// last one's error. <paramref name="connect"/> is one attempt (<see cref="ConnectOneAsync"/> in the app, a fake in the tests).
    /// </summary>
    public static async Task<Socket> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, TimeSpan attemptDelay,
        Func<IPAddress, int, CancellationToken, Task<Socket>> connect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connect);
        var order = Interleave(addresses);
        if (order.Count == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempts = new List<Task<Socket>>();
        int next = 0;
        Exception? last = null;
        try
        {
            attempts.Add(connect(order[next++], port, race.Token));
            while (attempts.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(race.Token);
                var delay = next < order.Count ? Task.Delay(attemptDelay, wait.Token) : null;
                var waits = new List<Task>(attempts);
                if (delay is not null)
                {
                    waits.Add(delay);
                }

                var done = await Task.WhenAny(waits).ConfigureAwait(false);
                wait.Cancel();
                if (done == delay)
                {
                    attempts.Add(connect(order[next++], port, race.Token));
                    continue;
                }

                var attempt = (Task<Socket>)done;
                attempts.Remove(attempt);
                if (attempt.IsCompletedSuccessfully)
                {
                    return attempt.Result;
                }

                last = attempt.Exception?.GetBaseException() ?? new OperationCanceledException(cancellationToken);
                if (next < order.Count)
                {
                    attempts.Add(connect(order[next++], port, race.Token));
                }
            }
        }
        finally
        {
            race.Cancel();
            foreach (var loser in attempts)
            {
                _ = loser.ContinueWith(
                    t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            t.Result.Dispose();
                        }
                        else
                        {
                            _ = t.Exception;   // observed: a loser's cancel or refusal is no one's error
                        }
                    },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        ExceptionDispatchInfo.Throw(last!);
        return null!;   // unreachable: Throw never returns
    }

    /// <summary>One attempt: a TCP socket of <paramref name="address"/>'s family connected to it, disposed when the connect fails or is cancelled.</summary>
    public static async Task<Socket> ConnectOneAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
