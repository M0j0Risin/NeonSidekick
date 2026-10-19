using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// The web tools' raced connect (2026-10-19, the Mac's dead IPv6 route): the order, a dead address losing to the next, a refusal starting
/// the next at once, every error, the cancel, a late loser closed, and the real attempt over loopback. The dead address is a fake that never
/// answers until cancelled, so no test needs a network without IPv6.
/// </summary>
public class HappyEyeballsTests
{
    private static readonly IPAddress V6a = IPAddress.Parse("2606:4700:10::6814:179a");
    private static readonly IPAddress V6b = IPAddress.Parse("2606:4700:10::ac42:93f3");
    private static readonly IPAddress V4a = IPAddress.Parse("104.20.23.154");
    private static readonly IPAddress V4b = IPAddress.Parse("172.66.147.243");

    /// <summary>A scripted attempt per address: the addresses started in order, and each attempt's token.</summary>
    private sealed class Attempts
    {
        private readonly Dictionary<IPAddress, Func<CancellationToken, Task<Socket>>> _script = new();

        public ConcurrentQueue<IPAddress> Started { get; } = new();

        public ConcurrentDictionary<IPAddress, CancellationToken> Tokens { get; } = new();

        public Attempts On(IPAddress address, Func<CancellationToken, Task<Socket>> attempt)
        {
            _script[address] = attempt;
            return this;
        }

        public Task<Socket> Connect(IPAddress address, int port, CancellationToken cancellationToken)
        {
            Started.Enqueue(address);
            Tokens[address] = cancellationToken;
            return _script[address](cancellationToken);
        }
    }

    private static Socket NewSocket() => new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    /// <summary>A route nowhere: no answer, no refusal, until the attempt is cancelled.</summary>
    private static async Task<Socket> Dead(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new UnreachableException();
    }

    private static Task<Socket> Refused(CancellationToken _) => Task.FromException<Socket>(new SocketException((int)SocketError.ConnectionRefused));

    private static Task<Socket> Answers(Socket socket) => Task.FromResult(socket);

    [Fact]
    public void Interleave_TakesTheFamiliesTurnAbout_TheFirstAsResolved()
    {
        Assert.Equal([V6a, V4a, V6b, V4b], HappyEyeballs.Interleave([V6a, V6b, V4a, V4b]));
        Assert.Equal([V4a, V6a, V4b, V6b], HappyEyeballs.Interleave([V4a, V4b, V6a, V6b]));
        Assert.Equal([V6a, V4a, V6b], HappyEyeballs.Interleave([V6a, V6b, V4a]));
        Assert.Equal([V4a, V4b], HappyEyeballs.Interleave([V4a, V4b]));
        Assert.Empty(HappyEyeballs.Interleave([]));
    }

    [Fact]
    public async Task ADeadFirstAddress_LosesToTheNext_AfterTheAttemptDelay_AndIsCancelled()
    {
        using var won = NewSocket();
        var attempts = new Attempts().On(V6a, Dead).On(V4a, _ => Answers(won));

        var socket = await HappyEyeballs.ConnectAsync([V6a, V6b, V4a], 443, TimeSpan.FromMilliseconds(50), attempts.Connect, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Same(won, socket);
        Assert.Equal([V6a, V4a], attempts.Started);   // turn about: the v4 address second, ahead of the other v6 one
        Assert.True(attempts.Tokens[V6a].IsCancellationRequested, "the dead attempt was left running");
    }

    [Fact]
    public async Task AFirstAddressThatAnswers_StartsNoOther()
    {
        using var won = NewSocket();
        var attempts = new Attempts().On(V6a, _ => Answers(won)).On(V4a, Dead);

        var socket = await HappyEyeballs.ConnectAsync([V6a, V4a], 443, TimeSpan.FromSeconds(30), attempts.Connect, CancellationToken.None);

        Assert.Same(won, socket);
        Assert.Equal([V6a], attempts.Started);
    }

    [Fact]
    public async Task ARefusal_StartsTheNextAtOnce_NotAfterTheDelay()
    {
        using var won = NewSocket();
        var attempts = new Attempts().On(V6a, Refused).On(V4a, _ => Answers(won));
        var watch = Stopwatch.StartNew();

        var socket = await HappyEyeballs.ConnectAsync([V6a, V4a], 443, TimeSpan.FromSeconds(30), attempts.Connect, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Same(won, socket);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");   // the 30 s delay never ran out
        Assert.Equal([V6a, V4a], attempts.Started);
    }

    [Fact]
    public async Task EveryAttemptFailing_ThrowsTheLastOnesError()
    {
        var attempts = new Attempts()
            .On(V6a, Refused)
            .On(V4a, _ => Task.FromException<Socket>(new SocketException((int)SocketError.HostUnreachable)));

        var error = await Assert.ThrowsAsync<SocketException>(() => HappyEyeballs.ConnectAsync([V6a, V4a], 443, TimeSpan.FromSeconds(30), attempts.Connect, CancellationToken.None));

        Assert.Equal(SocketError.HostUnreachable, error.SocketErrorCode);
        Assert.Equal([V6a, V4a], attempts.Started);
    }

    [Fact]
    public async Task NoAddress_IsHostNotFound()
    {
        var error = await Assert.ThrowsAsync<SocketException>(() => HappyEyeballs.ConnectAsync([], 443, HappyEyeballs.AttemptDelay, new Attempts().Connect, CancellationToken.None));

        Assert.Equal(SocketError.HostNotFound, error.SocketErrorCode);
    }

    [Fact]
    public async Task TheCallersCancel_EndsEveryAttempt_AsACancel()
    {
        var attempts = new Attempts().On(V6a, Dead).On(V4a, Dead);
        using var cancel = new CancellationTokenSource();

        var connect = HappyEyeballs.ConnectAsync([V6a, V4a], 443, TimeSpan.FromMilliseconds(20), attempts.Connect, cancel.Token);
        Assert.True(SpinWait.SpinUntil(() => attempts.Started.Count == 2, TimeSpan.FromSeconds(10)), "the second attempt never started");
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.All(attempts.Tokens.Values, token => Assert.True(token.IsCancellationRequested));
    }

    [Fact]
    public async Task ALoserThatConnectsAfterTheWinner_IsClosed()
    {
        using var won = NewSocket();
        var late = NewSocket();
        var lateAnswer = new TaskCompletionSource<Socket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new Attempts().On(V6a, _ => lateAnswer.Task).On(V4a, _ => Answers(won));

        var socket = await HappyEyeballs.ConnectAsync([V6a, V4a], 443, TimeSpan.FromMilliseconds(20), attempts.Connect, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));
        lateAnswer.SetResult(late);   // it connected anyway, after it lost

        Assert.Same(won, socket);
        Assert.True(SpinWait.SpinUntil(() => late.SafeHandle.IsClosed, TimeSpan.FromSeconds(10)), "the late loser was left open");
    }

    [Fact]
    public async Task TheRealAttempt_ConnectsOverLoopback_PastADeadAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptSocketAsync();
        var dead = IPAddress.Parse("::ffff:0.0.0.1");   // stands for the dead route: scripted, never dialled
        Task<Socket> Connect(IPAddress address, int p, CancellationToken ct) => address.Equals(dead) ? Dead(ct) : HappyEyeballs.ConnectOneAsync(address, p, ct);

        using var socket = await HappyEyeballs.ConnectAsync([dead, IPAddress.Loopback], port, TimeSpan.FromMilliseconds(50), Connect, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));
        using var server = await accept.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(socket.Connected);
        Assert.Equal(port, ((IPEndPoint)socket.RemoteEndPoint!).Port);
    }
}
