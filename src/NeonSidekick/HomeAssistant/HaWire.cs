using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace NeonSidekick.HomeAssistant;

/// <summary>
/// One WebSocket to Home Assistant as the home bar's live feed sees it (2026-10-12): whole text messages out and in, nothing
/// else. The seam the tests drive a scripted server through (<c>FakeHaServer</c>); <see cref="HaWebSocketWire"/> is the real
/// one. A send and a receive may run at once (one of each), as <see cref="ClientWebSocket"/> allows.
/// </summary>
public interface IHaWire : IAsyncDisposable
{
    /// <summary>Sends one text message.</summary>
    Task SendAsync(string text, CancellationToken cancellationToken);

    /// <summary>The next whole text message, or null once the other side closed. A broken connection throws.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IHaWire"/> over a <see cref="ClientWebSocket"/> (2026-10-12), <see cref="Web.DevToolsClient"/>'s reading
/// (pooled chunks into one message, a cap) with the system's proxy left in place: Home Assistant may be another machine, or
/// Nabu Casa's cloud, where a proxy is the user's business.
/// </summary>
public sealed class HaWebSocketWire : IHaWire
{
    /// <summary>The largest message read: the entity registry of a large home is a few MB, so the cap is generous.</summary>
    public const int MaxMessageBytes = 32 * 1024 * 1024;

    private readonly ClientWebSocket _socket = new();

    private HaWebSocketWire()
    {
    }

    /// <summary>The socket for a server: <c>http</c>→<c>ws</c>, <c>https</c>→<c>wss</c>, <c>/api/websocket</c> after any base path. Pure; pinned.</summary>
    public static Uri SocketUri(Uri server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var builder = new UriBuilder(server)
        {
            Scheme = string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Port = server.IsDefaultPort ? -1 : server.Port,
            Query = "",
            Fragment = "",
        };
        builder.Path = builder.Path.TrimEnd('/') + "/api/websocket";
        return builder.Uri;
    }

    /// <summary>Opens the socket to <paramref name="server"/>'s <see cref="SocketUri"/>.</summary>
    public static async Task<IHaWire> ConnectAsync(Uri server, CancellationToken cancellationToken)
    {
        var wire = new HaWebSocketWire();
        try
        {
            await wire._socket.ConnectAsync(SocketUri(server), cancellationToken).ConfigureAwait(false);
            return wire;
        }
        catch
        {
            wire._socket.Dispose();
            throw;
        }
    }

    public Task SendAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
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
                    return null;
                }

                message.Write(chunk, 0, received.Count);
                if (message.Length > MaxMessageBytes)
                {
                    throw new IOException($"a Home Assistant message over {MaxMessageBytes / (1024 * 1024)} MB");
                }

                if (received.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, quick.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
            {
                // Gone already: disposing is all that is left.
            }
        }

        _socket.Dispose();
    }
}
