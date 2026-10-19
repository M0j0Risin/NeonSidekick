using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Shell.Bridge;

/// <summary>What the bridge asks the host to do with one request: the tool's text, or an <c>Error:</c> sentence.</summary>
public delegate Task<string> BridgeDispatch(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken);

/// <summary>
/// The way an <c>execute_code</c> script calls the app's tools (2026-09-21): a loopback TCP listener
/// on <c>127.0.0.1</c> and an ephemeral port, alive for one run, speaking one JSON line each way per
/// connection — <c>{"token","tool","arguments"}</c> in, <c>{"result"}</c> or <c>{"error"}</c> out — with a
/// fresh 32-byte token in the child's environment, so nothing else on the machine can talk to it
/// during the run. One connection per call keeps every client module trivial (Python's
/// <c>socket</c>, Node's <c>net</c>, PowerShell's <c>TcpClient</c>: no third-party package). Calls
/// are dispatched as they arrive, in parallel, under the run's token; <see cref="Calls"/> counts
/// them and the one over <paramref name="maxCalls"/> is answered with the limit sentence. Rejected
/// on the way: named pipes (Python needs pywin32 for them), a file RPC (polling), stdio (the
/// script's own output lives there).
/// </summary>
public sealed class BridgeServer : IAsyncDisposable
{
    /// <summary>The wire's writer: the source-generated context, the text left readable (a quote or an apostrophe in a result is not escaped as \u00xx — the client decodes JSON either way, the log reads it raw).</summary>
    private static readonly BridgeJsonContext Wire = new(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private readonly TcpListener _listener;
    private readonly BridgeDispatch _dispatch;
    private readonly int _maxCalls;
    private readonly int _maxRequestBytes;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private int _calls;

    private BridgeServer(TcpListener listener, string token, BridgeDispatch dispatch, int maxCalls, int maxRequestBytes)
    {
        _listener = listener;
        Token = token;
        _dispatch = dispatch;
        _maxCalls = maxCalls;
        _maxRequestBytes = maxRequestBytes;
        _slots = new SemaphoreSlim(Math.Clamp(maxCalls + 4, 8, MaxConnections), Math.Clamp(maxCalls + 4, 8, MaxConnections));
        _accepting = AcceptAsync();
    }

    /// <summary>The run's secret, hex; the child gets it as <see cref="TokenVariable"/>.</summary>
    public string Token { get; }

    /// <summary><c>127.0.0.1:port</c>; the child gets it as <see cref="AddressVariable"/>.</summary>
    public string Address => "127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>How many requests were dispatched (the refused ones over the cap not counted).</summary>
    public int Calls => Volatile.Read(ref _calls);

    public const string AddressVariable = "NEONSIDEKICK_BRIDGE_ADDRESS";
    public const string TokenVariable = "NEONSIDEKICK_BRIDGE_TOKEN";

    /// <summary>
    /// The longest request line read (2026-10-12, the shell review: a script that sent one line without its newline grew the
    /// app's memory as long as it wrote); room for a large <c>write_file</c>. Past it the call is answered
    /// <see cref="ShellText.RequestTooLarge"/> and the connection closed. Pinned.
    /// </summary>
    public const int MaxRequestBytes = 32 * 1024 * 1024;

    /// <summary>
    /// The most connections served at once (2026-10-19, the code review): the per-request <see cref="MaxRequestBytes"/> cap bounds one
    /// connection, but nothing bounded how many a local process could open and hold during a run, so the app could keep N × 32 MB. The
    /// slots are <c>maxCalls + 4</c>, clamped here, so legitimate parallel calls run unthrottled while a flood past the cap waits for a
    /// slot and is dropped on <see cref="ReadTimeout"/>. Pinned.
    /// </summary>
    public const int MaxConnections = 64;

    /// <summary>
    /// How long one connection has to deliver its request line (2026-10-19, the code review): the token is only checked once the whole
    /// line is read, so a connection that opens and sends slowly or never held its slot and its buffer for the whole run. Past this it is
    /// closed, freeing both. Pinned.
    /// </summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Binds the listener and starts accepting.</summary>
    public static BridgeServer Start(BridgeDispatch dispatch, int maxCalls) => Start(dispatch, maxCalls, MaxRequestBytes);

    /// <summary><see cref="Start(BridgeDispatch, int)"/> with another request cap: the tests'.</summary>
    internal static BridgeServer Start(BridgeDispatch dispatch, int maxCalls, int maxRequestBytes)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCalls, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRequestBytes, 1);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return new BridgeServer(listener, token, dispatch, maxCalls, maxRequestBytes);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            // A slot per in-flight connection (2026-10-19): a flood past the cap waits here rather than buffering, and a connection that
            // never frees its slot within the read timeout is dropped below, so the held memory and the open count both stay bounded.
            bool got;
            try
            {
                got = await _slots.WaitAsync(ReadTimeout, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                client.Dispose();
                return;
            }

            if (!got)
            {
                client.Dispose();
                continue;
            }

            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            deadline.CancelAfter(ReadTimeout);
            try
            {
                using var stream = client.GetStream();
                // The read is bounded by both the byte cap and the time: a connection that opens and sends nothing holds nothing for long.
                var response = await ReadLineAsync(stream, _maxRequestBytes, deadline.Token).ConfigureAwait(false) is { } line
                    ? await AnswerAsync(line, _stop.Token).ConfigureAwait(false)
                    : new BridgeResponse { Error = ShellText.RequestTooLarge(_maxRequestBytes) };
                byte[] bytes = Encoding.UTF8.GetBytes(Serialize(response) + "\n");
                await stream.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
                await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The client went, the read timed out, or the run ended: nothing to answer to.
            }
            finally
            {
                // The serve tasks are not awaited on dispose, so the slot's release can race the semaphore's own dispose.
                try
                {
                    _slots.Release();
                }
                catch (ObjectDisposedException)
                {
                    // The run ended under us; the port is closing anyway.
                }
            }
        }
    }

    /// <summary>The response as the line the client reads (no newline).</summary>
    public static string Serialize(BridgeResponse response) => JsonSerializer.Serialize(response, Wire.BridgeResponse);

    /// <summary>One request's answer, pure over the line: the token checked, the JSON read, the cap applied, the tool dispatched.</summary>
    public async Task<BridgeResponse> AnswerAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        BridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, BridgeJsonContext.Default.BridgeRequest);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Tool))
        {
            return new BridgeResponse { Error = ShellText.BadRequest };
        }

        if (!string.Equals(request.Token, Token, StringComparison.Ordinal))
        {
            return new BridgeResponse { Error = ShellText.BadToken };
        }

        if (Interlocked.Increment(ref _calls) > _maxCalls)
        {
            Interlocked.Decrement(ref _calls);
            return new BridgeResponse { Error = ShellText.ToolCallLimit(_maxCalls) };
        }

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (request.Arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in request.Arguments.EnumerateObject())
            {
                arguments[property.Name] = property.Value.Clone();
            }
        }

        string result;
        try
        {
            result = await _dispatch(request.Tool.Trim(), arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = "Error: " + request.Tool.Trim() + " failed: " + ex.Message;
        }

        DiagnosticLog.Debug(ShellKinds.Category, ShellText.BridgeLogLine(request.Tool.Trim(), result));
        return result.StartsWith("Error:", StringComparison.Ordinal) ? new BridgeResponse { Error = result } : new BridgeResponse { Result = result };
    }

    /// <summary>The request line, its newline off; null once it passes <paramref name="maxBytes"/>.</summary>
    private static async Task<string?> ReadLineAsync(NetworkStream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            int newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            int take = newline < 0 ? read : newline;
            if (buffer.Length + take > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, take);
            if (newline >= 0)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Stops accepting, cancels the calls in flight and closes the port.</summary>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _accepting.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The accept loop's own exit.
        }

        _slots.Dispose();
        _stop.Dispose();
    }
}
