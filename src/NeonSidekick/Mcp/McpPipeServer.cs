using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Mcp;

/// <summary>
/// An MCP server in this process over a pair of pipes (2026-09-20): the smoke probe's proof that the
/// SDK's JSON path survives ILC and the tests' stand-in for a real server, so <see cref="McpTool"/> and
/// the session run over the real <see cref="McpClient"/> with no child process. Every tool echoes its
/// <c>text</c> argument (<see cref="EchoPrefix"/>); a tool named <see cref="FailToolName"/> answers
/// <c>isError</c>, one named <see cref="ImageToolName"/> an image block (the SDK carries the base64 text itself as bytes), and since 2026-11-19
/// (feature idea #30) one named <see cref="DropToolName"/> ends the server mid-call; <see cref="NotifyToolsChangedAsync"/> swaps the list
/// and says so, and a tool in <c>readOnly</c> is marked <c>readOnlyHint</c>. The handlers are hand-written
/// (<see cref="McpServerOptions.Handlers"/>), never <c>McpServerTool.Create</c> over a delegate — that is
/// the reflection path the AOT budget forbids.
/// </summary>
public sealed class McpPipeServer : IAsyncDisposable
{
    public const string EchoPrefix = "echo: ";
    public const string FailToolName = "fail";
    public const string FailText = "boom";
    public const string ImageToolName = "image";

    /// <summary>A tool that ends the server mid-call (2026-11-19): it closes both pipes and never answers. Acts only when a test lists it.</summary>
    public const string DropToolName = "drop";

    /// <summary>A tool whose handler throws (2026-11-19): the server answers a JSON-RPC error, an answer from a live server, not a drop.</summary>
    public const string ThrowToolName = "throw";
    public const string TextArgument = "text";

    private static readonly JsonElement Schema = ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "description": "What to echo back." }
          }
        }
        """);

    /// <summary>
    /// <see cref="Schema"/> closed with <c>additionalProperties: false</c> (2026-11-15, the tool-arguments review's #2): the argument check
    /// judges a server's tool only then, so a test of that path asks for it.
    /// </summary>
    private static readonly JsonElement ClosedSchema = ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "description": "What to echo back." }
          },
          "additionalProperties": false
        }
        """);

    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<(string Name, string ArgumentsJson)> _calls = [];
    private readonly IReadOnlySet<string> _readOnly;
    private readonly object _toolsLock = new();
    private IReadOnlyList<(string Name, string Description)> _tools;
    private int _listings;
    private int _disposed;
    private McpServer? _server;
    private Task? _run;

    private McpPipeServer(IReadOnlyList<(string Name, string Description)> tools, IReadOnlySet<string> readOnly)
    {
        _tools = tools;
        _readOnly = readOnly;
    }

    /// <summary>The tools the server lists, in order (swapped whole by <see cref="NotifyToolsChangedAsync"/>).</summary>
    public IReadOnlyList<(string Name, string Description)> Tools { get { lock (_toolsLock) { return _tools; } } }

    /// <summary>
    /// When set, every <c>tools/list</c> waits for it before answering (2026-11-19): a test holds a background listing in flight. The
    /// count goes up as the request comes in.
    /// </summary>
    public Task? ListingGate { get; set; }

    /// <summary>How many <c>tools/list</c> requests the server answered (2026-11-19): a client's first listing and every one after.</summary>
    public int Listings => Volatile.Read(ref _listings);

    /// <summary>Every call the server took: the tool's name and its arguments as JSON.</summary>
    public IReadOnlyList<(string Name, string ArgumentsJson)> Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <summary>The client side of the pipes: what <c>McpClient.CreateAsync</c> takes. One client per server.</summary>
    public IClientTransport ClientTransport => new StreamClientTransport(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream());

    /// <summary>
    /// Starts a server listing <paramref name="tools"/>, their schema closed when <paramref name="closed"/>, those named in
    /// <paramref name="readOnly"/> marked <c>readOnlyHint: true</c>; it runs until disposed. Without <paramref name="drops"/> the
    /// <see cref="DropToolName"/> tool echoes like any other (the server that came back).
    /// </summary>
    public static McpPipeServer Start(IReadOnlyList<(string Name, string Description)> tools, string serverName = "neon-pipe", bool closed = false, IReadOnlySet<string>? readOnly = null, bool drops = true)
    {
        var schema = closed ? ClosedSchema : Schema;
        ArgumentNullException.ThrowIfNull(tools);
        var server = new McpPipeServer(tools, readOnly ?? new HashSet<string>(StringComparer.Ordinal));
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = serverName, Version = "1.0" },
            // ListChanged (2026-11-19): a server that sends notifications/tools/list_changed declares it, as a real one must.
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = async (_, cancellationToken) =>
                {
                    Interlocked.Increment(ref server._listings);
                    if (server.ListingGate is { } gate)
                    {
                        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }

                    return new ListToolsResult
                    {
                        Tools = server.Tools.Select(t => new Tool
                        {
                            Name = t.Name,
                            Description = t.Description,
                            InputSchema = schema,
                            Annotations = server._readOnly.Contains(t.Name) ? new ToolAnnotations { ReadOnlyHint = true } : null,
                        }).ToList(),
                    };
                },
                CallToolHandler = (request, cancellationToken) => request.Params?.Name switch
                {
                    DropToolName when drops => server.DropAsync(request.Params, cancellationToken),
                    ThrowToolName => throw new ModelContextProtocol.McpProtocolException("the tool threw", ModelContextProtocol.McpErrorCode.InternalError),
                    _ => ValueTask.FromResult(server.Call(request.Params)),
                },
            },
        };
        var transport = new StreamServerTransport(server._clientToServer.Reader.AsStream(), server._serverToClient.Writer.AsStream(), serverName);
        server._server = McpServer.Create(transport, options);
        server._run = server._server.RunAsync(server._stop.Token);
        return server;
    }

    private CallToolResult Call(CallToolRequestParams? request)
    {
        string name = request?.Name ?? "";
        string text = request?.Arguments is { } args && args.TryGetValue(TextArgument, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : "";
        lock (_calls)
        {
            _calls.Add((name, ArgumentsJson(request?.Arguments)));
        }

        return name switch
        {
            FailToolName => new CallToolResult { Content = [new TextContentBlock { Text = FailText }], IsError = true },
            ImageToolName => new CallToolResult { Content = [new ImageContentBlock { Data = System.Text.Encoding.ASCII.GetBytes("iVBORw0KGgo="), MimeType = "image/png" }] },
            _ => new CallToolResult { Content = [new TextContentBlock { Text = EchoPrefix + text }] },
        };
    }

    /// <summary>
    /// The <see cref="DropToolName"/> call: noted like any other, then both pipes closed and no answer, so the client sees its transport
    /// end with the call still pending (a server that crashed on it).
    /// </summary>
    private async ValueTask<CallToolResult> DropAsync(CallToolRequestParams request, CancellationToken cancellationToken)
    {
        lock (_calls)
        {
            _calls.Add((request.Name, ArgumentsJson(request.Arguments)));
        }

        _serverToClient.Writer.Complete();
        _clientToServer.Writer.Complete();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>Swaps the tool list and sends <c>notifications/tools/list_changed</c> (2026-11-19), with no parameters to serialize (AOT).</summary>
    public async Task NotifyToolsChangedAsync(IReadOnlyList<(string Name, string Description)> tools, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tools);
        lock (_toolsLock)
        {
            _tools = tools;
        }

        await _server!.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The arguments as one JSON object, written by hand (no context needed for a dictionary of elements).</summary>
    private static string ArgumentsJson(IDictionary<string, JsonElement>? arguments)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in arguments ?? new Dictionary<string, JsonElement>())
            {
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public async ValueTask DisposeAsync()
    {
        // Once (2026-11-19): a test ends a server to drop it, and the next connect's factory disposes the one it replaces.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        _clientToServer.Writer.Complete();
        _serverToClient.Writer.Complete();
        if (_server is { } server)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }

        if (_run is { } run)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }
}
