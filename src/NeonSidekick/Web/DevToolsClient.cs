using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;

namespace NeonSidekick.Web;

/// <summary>A DevTools protocol failure: a command the browser answered with an error, a closed connection, a port never opened.</summary>
public sealed class DevToolsException(string message) : Exception(message);

/// <summary>
/// The smallest Chrome DevTools Protocol client the headless browser needs (2026-10-11, the user's ask: wait for a page in real
/// time, so a bot check that counts real seconds can finish and reload). <c>--dump-dom</c> dumps at the load event, or after a
/// <c>--virtual-time-budget</c> of fast-forwarded timers that such a check sees through; the only way to watch the page as it
/// really runs is to ask it. The browser is started with <c>--remote-debugging-port=0</c> and writes the port it chose to
/// <see cref="ActivePortFile"/> in its profile folder; one WebSocket to that loopback endpoint carries commands one at a time,
/// each answer matched by its id and every event in between skipped. JSON written with <see cref="Utf8JsonWriter"/> and read
/// with <see cref="JsonDocument"/>: no serializer context. The port is open only while the run lasts (at most
/// <see cref="HeadlessBrowser.Timeout"/>), on loopback, to a throwaway profile; <c>--remote-debugging-pipe</c> would close even
/// that, but on Windows it needs handles inherited into the child, which <see cref="System.Diagnostics.Process"/> cannot pass.
/// </summary>
public sealed class DevToolsClient : IAsyncDisposable
{
    /// <summary>The file Chromium writes in its profile folder once its DevTools port is open: the port, then the browser's path.</summary>
    public const string ActivePortFile = "DevToolsActivePort";

    /// <summary>The largest message read; a page's DOM rides one, so the cap is generous.</summary>
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    private static readonly TimeSpan PortPollInterval = TimeSpan.FromMilliseconds(100);
    private readonly ClientWebSocket _socket = new();
    private int _nextId;

    /// <summary>The browser endpoint named by <see cref="ActivePortFile"/>'s text (<c>9222\n/devtools/browser/&lt;id&gt;</c>), on 127.0.0.1; null for anything else. Pure; pinned.</summary>
    public static Uri? ParseActivePort(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2
            || !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is <= 0 or > 65535
            || !lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
        {
            return null;
        }

        return new Uri("ws://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + lines[1]);
    }

    /// <summary>Waits for the browser to write <see cref="ActivePortFile"/> under <paramref name="userDataDirectory"/>; throws when <paramref name="exited"/> says it is gone first.</summary>
    public static async Task<Uri> WaitForEndpointAsync(string userDataDirectory, Func<bool> exited, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userDataDirectory);
        ArgumentNullException.ThrowIfNull(exited);
        string file = Path.Combine(userDataDirectory, ActivePortFile);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(file))
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    if (ParseActivePort(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)) is { } endpoint)
                    {
                        return endpoint;
                    }
                }
            }
            catch (IOException)
            {
                // Still being written: the next look reads it whole.
            }

            if (exited())
            {
                throw new DevToolsException("it exited before it opened its DevTools port");
            }

            await Task.Delay(PortPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Opens the WebSocket, with no proxy: the endpoint is loopback, and a system proxy that does not bypass it would swallow the connection.</summary>
    public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _socket.Options.Proxy = null;
        return _socket.ConnectAsync(endpoint, cancellationToken);
    }

    /// <summary>
    /// Sends <paramref name="method"/> with the params <paramref name="writeParams"/> writes (none when null), to the session
    /// <paramref name="sessionId"/> or the browser itself, and returns the answer's <c>result</c>; an <c>error</c> answer throws.
    /// </summary>
    public async Task<JsonElement> SendAsync(string method, Action<Utf8JsonWriter>? writeParams, string? sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        int id = await WriteAsync(method, writeParams, sessionId, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            using var message = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var answered) || answered.ValueKind != JsonValueKind.Number || answered.GetInt32() != id)
            {
                continue;
            }

            if (root.TryGetProperty("error", out var error))
            {
                string why = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString() ?? ""
                    : error.GetRawText();
                throw new DevToolsException(method + ": " + why);
            }

            return root.TryGetProperty("result", out var result) ? result.Clone() : default;
        }
    }

    /// <summary>Sends <paramref name="method"/> to the browser and does not wait for an answer (<c>Browser.close</c>: the connection may go first).</summary>
    public async Task NotifyAsync(string method, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        await WriteAsync(method, null, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> WriteAsync(string method, Action<Utf8JsonWriter>? writeParams, string? sessionId, CancellationToken cancellationToken)
    {
        int id = ++_nextId;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (sessionId is not null)
            {
                writer.WriteString("sessionId", sessionId);
            }

            writer.WriteStartObject("params");
            writeParams?.Invoke(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        await _socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        return id;
    }

    private async Task<JsonDocument> ReceiveAsync(CancellationToken cancellationToken)
    {
        var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var received = await _socket.ReceiveAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    throw new DevToolsException("the browser closed the DevTools connection");
                }

                message.Write(chunk, 0, received.Count);
                if (message.Length > MaxMessageBytes)
                {
                    throw new DevToolsException($"a DevTools message over {MaxMessageBytes / (1024 * 1024)} MB");
                }

                if (received.EndOfMessage)
                {
                    return JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
