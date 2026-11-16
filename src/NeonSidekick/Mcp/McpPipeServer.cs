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
/// <c>isError</c>, one named <see cref="ImageToolName"/> an image block (the SDK carries the base64 text itself as bytes). The handlers are hand-written
/// (<see cref="McpServerOptions.Handlers"/>), never <c>McpServerTool.Create</c> over a delegate — that is
/// the reflection path the AOT budget forbids.
/// </summary>
public sealed class McpPipeServer : IAsyncDisposable
{
    public const string EchoPrefix = "echo: ";
    public const string FailToolName = "fail";
    public const string FailText = "boom";
    public const string ImageToolName = "image";
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
    private McpServer? _server;
    private Task? _run;

    private McpPipeServer(IReadOnlyList<(string Name, string Description)> tools)
    {
        Tools = tools;
    }

    /// <summary>The tools the server lists, in order.</summary>
    public IReadOnlyList<(string Name, string Description)> Tools { get; }

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

    /// <summary>Starts a server listing <paramref name="tools"/>, their schema closed when <paramref name="closed"/>; it runs until disposed.</summary>
    public static McpPipeServer Start(IReadOnlyList<(string Name, string Description)> tools, string serverName = "neon-pipe", bool closed = false)
    {
        var schema = closed ? ClosedSchema : Schema;
        ArgumentNullException.ThrowIfNull(tools);
        var server = new McpPipeServer(tools);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = serverName, Version = "1.0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools = tools.Select(t => new Tool { Name = t.Name, Description = t.Description, InputSchema = schema }).ToList(),
                }),
                CallToolHandler = (request, _) => ValueTask.FromResult(server.Call(request.Params)),
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
