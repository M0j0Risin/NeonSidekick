// OutageProxy: a loopback TCP forwarder in front of an LLM server that can be taken down and brought back in a second, for checking by
// hand what the app does when its server goes away: a gap review's or a code audit's pass retried once, the error said, the review
// stopped when it stays down (2026-11-17, the gap-audit-reliability plan's hand check: restarting LM Studio or an SGLang container
// reloads the model and takes far longer than the retry's 15 s wait, while this keeps the model loaded behind it).
//
// Going down resets every open connection (RST, as a server that died mid-reply does, not a clean end of stream) and refuses new
// ones, the two failures the app retries. Coming up listens again. The server behind it is never touched.
//
// A .NET 10 file-based app: no csproj, not in the solution, not run by build.ps1. From the repository root:
//
//     dotnet run tools/OutageProxy.cs                       # 127.0.0.1:30001 -> 127.0.0.1:30000 (SGLang's port)
//     dotnet run tools/OutageProxy.cs -- 1240 1234           # listen port, target port (LM Studio's)
//
// Then start the app at it, never through /server: publish\output\NeonSidekick.exe --url http://127.0.0.1:30001/v1
// Type d + Enter to go down, u + Enter to come back up, q + Enter to quit.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

int listenPort = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 30001;
int targetPort = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 30000;
var listenAt = new IPEndPoint(IPAddress.Loopback, listenPort);
var target = new IPEndPoint(IPAddress.Loopback, targetPort);
var open = new ConcurrentDictionary<Socket, Socket>();   // client → upstream
var gate = new object();
TcpListener? listener = null;

GoUp();
Console.WriteLine("d = down, u = up, q = quit");
while (Console.ReadLine() is { } line)
{
    switch (line.Trim().ToLowerInvariant())
    {
        case "d":
            GoDown();
            break;
        case "u":
            GoUp();
            break;
        case "q":
            GoDown();
            return;
        case "":
            break;
        default:
            Console.WriteLine("d = down, u = up, q = quit");
            break;
    }
}

GoDown();

void GoUp()
{
    lock (gate)
    {
        if (listener is not null)
        {
            Console.WriteLine("already up");
            return;
        }

        listener = new TcpListener(listenAt);
        listener.Start();
        var accepting = listener;
        _ = Task.Run(() => AcceptAsync(accepting));
        Console.WriteLine($"UP    {listenAt} -> {target}");
    }
}

void GoDown()
{
    lock (gate)
    {
        listener?.Stop();
        listener = null;
        int dropped = 0;
        foreach (var (client, upstream) in open)
        {
            Reset(client);
            Reset(upstream);
            dropped++;
        }

        open.Clear();
        Console.WriteLine($"DOWN  ({dropped} connection(s) reset; new ones refused)");
    }
}

async Task AcceptAsync(TcpListener accepting)
{
    while (true)
    {
        Socket client;
        try
        {
            client = await accepting.AcceptSocketAsync();
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            return;   // stopped: gone down
        }

        _ = Task.Run(() => ForwardAsync(client));
    }
}

async Task ForwardAsync(Socket client)
{
    var upstream = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    try
    {
        await upstream.ConnectAsync(target);
    }
    catch (SocketException e)
    {
        Console.WriteLine($"  upstream not reachable: {e.SocketErrorCode}");
        Reset(client);
        upstream.Dispose();
        return;
    }

    open[client] = upstream;
    await Task.WhenAny(PipeAsync(client, upstream), PipeAsync(upstream, client));
    if (open.TryRemove(client, out _))
    {
        // One side ended on its own: end the other cleanly too.
        Close(client);
        Close(upstream);
    }
}

static async Task PipeAsync(Socket from, Socket to)
{
    var buffer = new byte[64 * 1024];
    try
    {
        int read;
        while ((read = await from.ReceiveAsync(buffer, SocketFlags.None)) > 0)
        {
            await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None);
        }
    }
    catch (Exception e) when (e is SocketException or ObjectDisposedException)
    {
        // reset or closed: the other pipe ends with it
    }
}

// A close with a TCP reset (linger 0), as a server that went away mid-reply.
static void Reset(Socket socket)
{
    try
    {
        socket.LingerState = new LingerOption(true, 0);
        socket.Close();
    }
    catch (Exception e) when (e is SocketException or ObjectDisposedException)
    {
    }
}

static void Close(Socket socket)
{
    try
    {
        socket.Shutdown(SocketShutdown.Both);
    }
    catch (Exception e) when (e is SocketException or ObjectDisposedException)
    {
    }

    socket.Dispose();
}
