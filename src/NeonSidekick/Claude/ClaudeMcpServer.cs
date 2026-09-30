using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Claude;

/// <summary>
/// A tool call the Claude CLI server's model made, as the MCP server took it (2026-09-30): the <c>tool_use</c> id the CLI
/// put in the call's <c>_meta</c> (the stream's own id for the same call, so the two meet exactly; null from a CLI that
/// sends none), the tool's name as the app knows it, and the arguments.
/// </summary>
public sealed record ClaudeToolCall(string? ToolUseId, string Name, IReadOnlyDictionary<string, JsonElement> Arguments);

/// <summary>What answers a <see cref="ClaudeToolCall"/>: the result's text, the pictures that go with it, and whether it is an error.</summary>
public sealed record ClaudeToolAnswer(string Text, IReadOnlyList<DataContent> Images, bool IsError);

/// <summary>
/// The MCP server the Claude CLI server's model reaches the app's tools through (2026-09-30). It listens on a loopback
/// port for the one relay the CLI starts (<see cref="McpRelay"/>, this app's own executable in its relay mode, whose stdio
/// is the CLI's MCP pipe): the relay's first line is <see cref="Token"/>, a key minted per server (32 random bytes, hex),
/// and a connection that sends anything else is closed. From there the SDK's own server runs over the socket
/// (<see cref="StreamServerTransport"/>, as <see cref="Mcp.McpPipeServer"/> runs over pipes), with hand-written handlers —
/// never <c>McpServerTool.Create</c> over a delegate, the reflection path the AOT budget forbids.
///
/// <para>The list is <paramref name="tools"/> as it stands when the CLI asks (at its start): the host sets it per launch.
/// A call is handed to <paramref name="onCall"/>, which does not run the tool itself: it waits for the app's own turn loop
/// to run it (<see cref="ClaudeCliChatClient"/>), so approvals, <c>ask_user</c> and the transcript are the ones every
/// other server's calls get. A CLI's <c>notifications/cancelled</c> (an interrupt) cancels the handler's token.</para>
/// </summary>
public sealed class ClaudeMcpServer : IAsyncDisposable
{
    /// <summary>The longest tool name the Messages API takes, <c>mcp__neon__</c> included; a longer one is left off the list (logged).</summary>
    public const int MaxToolNameLength = 64;

    private readonly TcpListener _listener;
    private readonly Func<IReadOnlyList<AIFunction>> _tools;
    private readonly Func<ClaudeToolCall, CancellationToken, Task<ClaudeToolAnswer>> _onCall;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private readonly Task _accept;

    private ClaudeMcpServer(Func<IReadOnlyList<AIFunction>> tools, Func<ClaudeToolCall, CancellationToken, Task<ClaudeToolAnswer>> onCall)
    {
        _tools = tools;
        _onCall = onCall;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Address = "127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _accept = AcceptAsync();
    }

    /// <summary>Starts listening; the server runs until disposed.</summary>
    public static ClaudeMcpServer Start(Func<IReadOnlyList<AIFunction>> tools, Func<ClaudeToolCall, CancellationToken, Task<ClaudeToolAnswer>> onCall)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(onCall);
        return new ClaudeMcpServer(tools, onCall);
    }

    /// <summary><c>127.0.0.1:port</c>, what the relay connects to.</summary>
    public string Address { get; }

    /// <summary>The key the relay sends first.</summary>
    public string Token { get; }

    /// <summary>The tools as the CLI lists them: <paramref name="tools"/>'s schema and words, each name within <see cref="MaxToolNameLength"/> with the prefix.</summary>
    public static IList<Tool> ListOf(IReadOnlyList<AIFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var listed = new List<Tool>(tools.Count);
        foreach (var tool in tools)
        {
            if (ClaudeArguments.McpToolPrefix.Length + tool.Name.Length > MaxToolNameLength)
            {
                DiagnosticLog.Warn(ClaudeText.Category, ClaudeCliText.ToolNameTooLongLog(tool.Name));
                continue;
            }

            listed.Add(new Tool { Name = tool.Name, Description = tool.Description, InputSchema = tool.JsonSchema });
        }

        return listed;
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

            lock (_connections)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(ServeAsync(client));
            }
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                if (!await ReadTokenAsync(stream).ConfigureAwait(false))
                {
                    DiagnosticLog.Warn(ClaudeText.Category, ClaudeCliText.RelayRefusedLog);
                    return;
                }

                DiagnosticLog.Debug(ClaudeText.Category, ClaudeCliText.RelayConnectedLog);
                var options = new McpServerOptions
                {
                    ServerInfo = new Implementation { Name = ClaudeArguments.McpServerName, Version = "1.0" },
                    Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                    Handlers = new McpServerHandlers
                    {
                        ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = ListOf(_tools()) }),
                        CallToolHandler = (request, cancellationToken) => new ValueTask<CallToolResult>(CallAsync(request.Params, cancellationToken)),
                    },
                };
                var transport = new StreamServerTransport(stream, stream, ClaudeArguments.McpServerName);
                await using var server = McpServer.Create(transport, options);
                await server.RunAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The relay went with its CLI, or the server is stopping.
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn(ClaudeText.Category, ClaudeCliText.McpServerFailedLog(ex.Message));
            }
        }
    }

    /// <summary>The first line, byte by byte (nothing of the MCP traffic behind it is read), compared in constant time.</summary>
    private async Task<bool> ReadTokenAsync(NetworkStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var line = new List<byte>(Token.Length + 1);
        var one = new byte[1];
        try
        {
            while (line.Count <= Token.Length + 1)
            {
                if (await stream.ReadAsync(one, timeout.Token).ConfigureAwait(false) == 0)
                {
                    return false;
                }

                if (one[0] == (byte)'\n')
                {
                    if (line.Count > 0 && line[^1] == (byte)'\r')
                    {
                        line.RemoveAt(line.Count - 1);
                    }

                    return CryptographicOperations.FixedTimeEquals(line.ToArray(), Encoding.ASCII.GetBytes(Token));
                }

                line.Add(one[0]);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return false;
    }

    private async Task<CallToolResult> CallAsync(CallToolRequestParams? request, CancellationToken cancellationToken)
    {
        string name = request?.Name ?? "";
        var arguments = request?.Arguments is { } given
            ? new Dictionary<string, JsonElement>(given, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var call = new ClaudeToolCall(ToolUseIdOf(request), name, arguments);
        ClaudeToolAnswer answer;
        try
        {
            answer = await _onCall(call, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            answer = new ClaudeToolAnswer(ClaudeCliText.CallCancelled, [], IsError: true);
        }

        var content = new List<ContentBlock> { new TextContentBlock { Text = answer.Text } };
        foreach (var image in answer.Images)
        {
            content.Add(new ImageContentBlock { Data = Encoding.ASCII.GetBytes(Convert.ToBase64String(image.Data.Span)), MimeType = image.MediaType });
        }

        return new CallToolResult { Content = content, IsError = answer.IsError };
    }

    /// <summary>The key in a call's <c>_meta</c> that carries the stream's <c>tool_use</c> id (Claude Code 2.1.285). Pinned.</summary>
    public const string ToolUseIdMetaKey = "claudecode/toolUseId";

    private static string? ToolUseIdOf(CallToolRequestParams? request)
    {
        try
        {
            return request?.Meta?[ToolUseIdMetaKey]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        Task[] running;
        lock (_connections)
        {
            running = [.. _connections, _accept];
        }

        try
        {
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }

        _stop.Dispose();
    }
}
