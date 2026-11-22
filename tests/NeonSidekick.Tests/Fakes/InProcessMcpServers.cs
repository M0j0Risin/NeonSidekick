using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NeonSidekick.Mcp;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The MCP transport seam for the tests (2026-09-20): one in-process <see cref="McpPipeServer"/> per configured
/// server name, handed to <c>McpSession</c> / <c>SidekickApp</c> as the factory — the real client runs over a
/// pipe and no child process starts. A name in <see cref="Hanging"/> gets a transport whose connect never
/// completes (the timeout's test); one in <see cref="Failing"/> a transport that throws (a command that is not there). Since 2026-11-19
/// (feature idea #30): <see cref="Drop"/> ends a running server (its next connect starts a fresh one), <see cref="ReadOnly"/> marks
/// tools <c>readOnlyHint</c>, and <see cref="Held"/> holds one connect until the test lets it go.
/// </summary>
public sealed class InProcessMcpServers : IAsyncDisposable
{
    private readonly Dictionary<string, McpPipeServer> _servers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<(string Name, string Description)>> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlySet<string>> _readOnly = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, bool>> _destructive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _instructions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Uri, string Name, string? Text)[]> _resources = new(StringComparer.Ordinal);

    /// <summary>The default tool list of a server not given its own: an echo and the fail tool.</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> DefaultTools = [("echo", "Echoes the text back."), (McpPipeServer.FailToolName, "Always fails.")];

    /// <summary>Names whose connect never completes.</summary>
    public HashSet<string> Hanging { get; } = new(StringComparer.Ordinal);

    /// <summary>Names whose transport throws at once.</summary>
    public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

    /// <summary>Names whose next servers' <see cref="McpPipeServer.DropToolName"/> tool echoes instead of dropping (2026-11-19: the one that came back).</summary>
    public HashSet<string> Steady { get; } = new(StringComparer.Ordinal);

    /// <summary>Every transport the factory was asked for, in order: the name and the config it came with.</summary>
    public List<(string Name, McpServerConfig Config)> Requested { get; } = [];

    /// <summary>What a named server lists (set before the connect).</summary>
    public void Tools(string name, params (string Name, string Description)[] tools) => _tools[name] = tools;

    /// <summary>The server behind <paramref name="name"/> after a connect, for its <c>Calls</c>.</summary>
    public McpPipeServer Server(string name) => _servers[name];

    /// <summary>Which of a named server's tools are marked <c>readOnlyHint: true</c> (set before the connect; kept for the reconnects).</summary>
    public void ReadOnly(string name, params string[] tools) => _readOnly[name] = new HashSet<string>(tools, StringComparer.Ordinal);

    /// <summary>A named server's handshake instructions (2026-11-19, feature idea #59; set before the connect, kept for the reconnects).</summary>
    public void Instructions(string name, string? text) => _instructions[name] = text;

    /// <summary>The resources <paramref name="name"/>'s next server publishes (2026-11-21, mcp_read); none set, it declares no resources.</summary>
    public void Resources(string name, params (string Uri, string Name, string? Text)[] resources) => _resources[name] = resources;

    /// <summary>Leaves <paramref name="name"/>'s next server without resources again (a server that came back without them).</summary>
    public void NoResources(string name) => _resources.Remove(name);

    /// <summary>The servers whose <c>resources/templates/list</c> answers with an error.</summary>
    public HashSet<string> TemplatesFail { get; } = new(StringComparer.Ordinal);

    /// <summary>A named server's tool's <c>destructiveHint</c> (2026-11-19, feature idea #28; set before the connect).</summary>
    public void Destructive(string name, string tool, bool destructive)
    {
        if (!_destructive.TryGetValue(name, out var map))
        {
            _destructive[name] = map = new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        map[tool] = destructive;
    }

    /// <summary>Ends the running server behind <paramref name="name"/>, as a crash would: its client sees the transport close.</summary>
    public async Task Drop(string name) => await _servers[name].DisposeAsync();

    /// <summary>
    /// Holds the next connect of <paramref name="name"/>: its pipe server starts as usual, but the transport's connect waits for the
    /// returned gate (or the connect's own cancel) first, so a test can hold one server's connect while another finishes.
    /// </summary>
    public TaskCompletionSource Held(string name)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _held[name] = gate;
        return gate;
    }

    /// <summary>The factory for the seam.</summary>
    public IClientTransport Transport(McpServerConfig config, string name)
    {
        Requested.Add((name, config));
        if (Failing.Contains(name))
        {
            throw new InvalidOperationException("no such command: " + (config.Command ?? config.Url));
        }

        if (Hanging.Contains(name))
        {
            return new HangingTransport(name);
        }

        if (_servers.TryGetValue(name, out var old))
        {
            _ = old.DisposeAsync();
        }

        var server = McpPipeServer.Start(_tools.TryGetValue(name, out var tools) ? tools : DefaultTools, name, new McpPipeServerOptions
        {
            ReadOnly = _readOnly.GetValueOrDefault(name),
            Drops = !Steady.Contains(name),
            Destructive = _destructive.GetValueOrDefault(name),
            Instructions = _instructions.GetValueOrDefault(name),
            Resources = _resources.GetValueOrDefault(name),
            TemplatesFail = TemplatesFail.Contains(name),
        });
        _servers[name] = server;
        if (_held.Remove(name, out var gate))
        {
            return new HeldTransport(server.ClientTransport, gate.Task);
        }

        return server.ClientTransport;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers.Values)
        {
            await server.DisposeAsync();
        }

        _servers.Clear();
    }

    private sealed class HeldTransport(IClientTransport inner, Task gate) : IClientTransport
    {
        public string Name => inner.Name;

        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            return await inner.ConnectAsync(cancellationToken);
        }
    }

    private sealed class HangingTransport(string name) : IClientTransport
    {
        public string Name => name;

        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
