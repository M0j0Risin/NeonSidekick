using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NeonSidekick.Diagnostics;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>What a configured server is doing.</summary>
public enum McpState
{
    /// <summary>Not started: the master switch, the per-server switch, a shadowing entry or a problem.</summary>
    Off,
    Connecting,
    Connected,
    Failed,
}

/// <summary>One configured server as the pane and the status line see it (a snapshot; the session replaces it whole).</summary>
public sealed record McpServerStatus(string Name, McpScope Scope, McpScope? ShadowedBy, bool Enabled, McpState State, string? Detail, IReadOnlyList<McpTool> Tools, string Transport)
{
    /// <summary>Whether this row can be started at all: not shadowed.</summary>
    public bool Startable => ShadowedBy is null;
}

/// <summary>One connected server's tools, for the <c>/sys</c> and <c>/mcp</c> groups.</summary>
public sealed record McpServerTools(string Name, IReadOnlyList<AIFunction> Tools);

/// <summary>
/// The MCP servers of the loaded profile (2026-09-20): the <see cref="LlmSession"/> shape — one per run,
/// built by <see cref="SidekickApp"/> beside the LLM session, handed to the screen and the headless
/// loop. <see cref="ConnectAllAsync"/> reads the profile's and the home's <c>mcp.json</c>
/// (<see cref="McpCatalog"/>), drops every client it holds and starts every server that is on — the
/// master switch <c>MCP servers</c>, the per-server list <c>McpServersDisabled</c>, not shadowed, no
/// problem — in parallel, each under <c>MCP connect timeout (s)</c>; a failure is a row's detail, never a
/// throw, except the caller's own cancel (Ctrl+C under the spinner), rethrown once after the wave so
/// the watcher sees it. The tools of every connected server, prefixed and de-duplicated
/// (<see cref="McpTool"/>), are what <c>PrepareTurn</c> offers. The transport is the one seam
/// (<see cref="DefaultTransport"/> for the real thing; a test hands in a pipe to an in-process server).
/// Nothing here writes to the console: the screen prints <see cref="StatusLine"/> and <see cref="WarningLines"/>.
/// Disposal drops every client; the SDK ends each stdio child within its shutdown timeout.
/// </summary>
internal sealed class McpSession : IAsyncDisposable, IDisposable
{
    public const string Category = McpConfigFile.Category;

    /// <summary>What the servers are told the client is.</summary>
    public const string ClientName = "NeonSidekick";

    private sealed record Connection(McpClient Client, string Fingerprint, IReadOnlyList<McpTool> Tools);

    private readonly AppSettings _settings;
    private readonly Func<McpServerConfig, string, IClientTransport> _transport;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly object _snapshot = new();
    private IReadOnlyList<McpServerStatus> _servers = [];
    private IReadOnlyList<McpConfigProblem> _problems = [];
    private IReadOnlyList<AIFunction> _tools = [];
    private IReadOnlyList<McpServerTools> _serverTools = [];
    private bool _attempted;

    public McpSession(AppSettings settings, Func<McpServerConfig, string, IClientTransport> transport, TimeProvider? time = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Every configured server as of the last read, in file order (the profile's, then the home's).</summary>
    public IReadOnlyList<McpServerStatus> Servers { get { lock (_snapshot) { return _servers; } } }

    /// <summary>What the last read could not use.</summary>
    public IReadOnlyList<McpConfigProblem> Problems { get { lock (_snapshot) { return _problems; } } }

    /// <summary>Every connected server's tools, flat, in server order: what a turn offers.</summary>
    public IReadOnlyList<AIFunction> Tools { get { lock (_snapshot) { return _tools; } } }

    /// <summary>Every connected server's tools by server: the <c>/sys</c> and <c>/mcp</c> groups.</summary>
    public IReadOnlyList<McpServerTools> ServerTools { get { lock (_snapshot) { return _serverTools; } } }

    /// <summary>Whether either file names a server or has a problem worth showing.</summary>
    public bool Configured => Servers.Count > 0 || Problems.Count > 0;

    /// <summary>Whether the last <see cref="ConnectAllAsync"/> started at least one server (the status line prints only then).</summary>
    public bool Attempted { get { lock (_snapshot) { return _attempted; } } }

    /// <summary>The profile's file and the home's, as the pane's edit rows open them.</summary>
    public string ProfilePath => McpConfigFile.ProfilePath(_settings.ProfileDirectory);
    public string GlobalPath => McpConfigFile.GlobalPath(_settings.StorageDirectory);

    /// <summary>The real transports: a child process for a <c>command</c>, streamable HTTP (SSE fallback) for a <c>url</c>. The HTTP client is the SDK's own.</summary>
    public static IClientTransport DefaultTransport(McpServerConfig config, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.IsHttp)
        {
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(config.Url!.Trim(), UriKind.Absolute),
                Name = name,
                AdditionalHeaders = config.Headers,
            });
        }

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = name,
            Command = config.Command!.Trim(),
            Arguments = config.Args,
            EnvironmentVariables = McpEnvironment(config.Env),
            WorkingDirectory = string.IsNullOrWhiteSpace(config.Cwd) ? null : config.Cwd,
            StandardErrorLines = line => DiagnosticLog.Debug(Category, McpText.StderrLogLine(name, line)),
        });
    }

    /// <summary>
    /// A stdio server's environment over the app's (2026-10-19, the second shell review: a third-party server inherited the app's API keys
    /// and tokens): every <see cref="Shell.ChildEnvironment.IsWithheld"/> name removed, then the server's own <c>env</c> entries on top,
    /// so a key the user hands a server on purpose still reaches it.
    /// </summary>
    public static Dictionary<string, string?> McpEnvironment(IReadOnlyDictionary<string, string>? own)
    {
        var environment = Shell.ChildEnvironment.WithheldForMcp();
        foreach (var (name, value) in own ?? new Dictionary<string, string>())
        {
            environment[name] = value;
        }

        return environment;
    }

    /// <summary>The two files merged as they stand on disk.</summary>
    public McpMerged Read() => McpCatalog.Merge(McpConfigFile.Load(ProfilePath), McpConfigFile.Load(GlobalPath));

    /// <summary>
    /// Drops every client, re-reads both files and starts every server that is on, in parallel; the
    /// master switch off leaves every row <see cref="McpState.Off"/>. <paramref name="phase"/> (may be
    /// null) follows the count. Never throws but for <paramref name="cancellationToken"/>'s own cancel,
    /// which marks every unfinished server <see cref="McpText.Cancelled"/> and is rethrown once after the wave.
    /// </summary>
    public async Task ConnectAllAsync(AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisposeAllAsync().ConfigureAwait(false);
            var merged = Read();
            var disabled = ToolsText.DisabledSet(effective.McpServersDisabled);
            var rows = merged.Entries.Select(e => Initial(e, effective.McpServers, disabled)).ToList();
            Publish(rows, merged.Problems, attempted: rows.Any(r => r.State == McpState.Connecting));
            var pending = rows.Where(r => r.State == McpState.Connecting).ToList();
            if (pending.Count == 0)
            {
                return;
            }

            int done = 0;
            phase?.Invoke(McpText.ConnectingProgress(0, pending.Count));
            var entries = merged.Entries.Where(e => e.Startable).ToDictionary(e => e.Name, StringComparer.Ordinal);   // a shadowed global shares its name with the profile's
            var tasks = pending.Select(async row =>
            {
                var outcome = await ConnectOneAsync(entries[row.Name], effective.McpConnectTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                lock (_snapshot)
                {
                    Replace(rows, outcome);
                    _servers = rows.ToArray();
                    RebuildTools(rows);
                }

                phase?.Invoke(McpText.ConnectingProgress(Interlocked.Increment(ref done), pending.Count));
            }).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts one server by name (a flip on, a retry): its row alone changes. False when no such server is startable.</summary>
    public async Task<bool> ConnectServerAsync(string name, AppSettingsData effective, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var merged = Read();
            var entry = merged.Entries.FirstOrDefault(e => e.Name == name && e.Startable);
            if (entry is null || !effective.McpServers)
            {
                return false;
            }

            await DisposeOneAsync(name).ConfigureAwait(false);
            var rows = Servers.ToList();
            Replace(rows, Initial(entry, enabled: true, ToolsText.DisabledSet([])) with { State = McpState.Connecting });
            Publish(rows, merged.Problems, attempted: true);
            var outcome = await ConnectOneAsync(entry, effective.McpConnectTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            rows = Servers.ToList();
            Replace(rows, outcome);
            Publish(rows, merged.Problems, attempted: true);
            return outcome.State == McpState.Connected;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops one server by name (a flip off): its client dropped, its row <see cref="McpState.Off"/>.</summary>
    public async Task DisconnectServerAsync(string name)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeOneAsync(name).ConfigureAwait(false);
            var rows = Servers.ToList();
            int i = rows.FindIndex(r => r.Name == name);
            if (i >= 0)
            {
                rows[i] = rows[i] with { Enabled = false, State = McpState.Off, Detail = null, Tools = [] };
            }

            Publish(rows, Problems, Attempted);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-reads both files and reconciles: a new or changed server is started, a gone one stopped, an
    /// unchanged connected one kept as it is. Returns the counts for the notice.
    /// </summary>
    public async Task<(int Added, int Removed, int Kept)> ReloadAsync(AppSettingsData effective, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var merged = Read();
            var disabled = ToolsText.DisabledSet(effective.McpServersDisabled);
            var entries = merged.Entries.Where(e => e.Startable).ToDictionary(e => e.Name, StringComparer.Ordinal);   // a shadowed global shares its name with the profile's
            int removed = 0;
            foreach (string name in _connections.Keys.ToList())
            {
                if (!entries.TryGetValue(name, out var entry) || !entry.Startable || entry.Config.Fingerprint() != _connections[name].Fingerprint)
                {
                    await DisposeOneAsync(name).ConfigureAwait(false);
                    removed++;
                }
            }

            var rows = new List<McpServerStatus>();
            var toStart = new List<McpServerEntry>();
            int kept = 0;
            foreach (var entry in merged.Entries)
            {
                if (_connections.TryGetValue(entry.Name, out var live))
                {
                    rows.Add(new McpServerStatus(entry.Name, entry.Scope, null, true, McpState.Connected, null, live.Tools, entry.Config.Describe()));
                    kept++;
                    continue;
                }

                var row = Initial(entry, effective.McpServers, disabled);
                rows.Add(row);
                if (row.State == McpState.Connecting)
                {
                    toStart.Add(entry);
                }
            }

            Publish(rows, merged.Problems, attempted: Attempted || toStart.Count > 0);
            var outcomes = await Task.WhenAll(toStart.Select(e => ConnectOneAsync(e, effective.McpConnectTimeoutSeconds, cancellationToken))).ConfigureAwait(false);
            rows = Servers.ToList();
            foreach (var outcome in outcomes)
            {
                Replace(rows, outcome);
            }

            Publish(rows, merged.Problems, Attempted);
            cancellationToken.ThrowIfCancellationRequested();
            return (toStart.Count, removed, kept);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The status line after a connect, or null when nothing was attempted (no server on): <c>🔌 MCP: 2 servers, 14 tools</c>.</summary>
    public string? StatusLine()
    {
        if (!Attempted)
        {
            return null;
        }

        var connected = Servers.Where(s => s.State == McpState.Connected).ToList();
        return McpText.StatusLine(connected.Count, connected.Sum(s => s.Tools.Count));
    }

    /// <summary>One warning per failed server: <c>🔌 MCP: docker failed: …</c>.</summary>
    public IEnumerable<string> WarningLines() =>
        Servers.Where(s => s.State == McpState.Failed).Select(s => McpText.FailedLine(s.Name, s.Detail ?? ""));

    private static McpServerStatus Initial(McpServerEntry entry, bool enabled, IReadOnlySet<string> disabled)
    {
        bool on = enabled && !disabled.Contains(entry.Name);
        var state = on && entry.Startable ? McpState.Connecting : McpState.Off;
        return new McpServerStatus(entry.Name, entry.Scope, entry.ShadowedBy, !disabled.Contains(entry.Name), state, null, [], entry.Config.Describe());
    }

    private async Task<McpServerStatus> ConnectOneAsync(McpServerEntry entry, int timeoutSeconds, CancellationToken cancellationToken)
    {
        string name = entry.Name;
        var started = _time.GetTimestamp();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        McpClient? client = null;
        try
        {
            var transport = _transport(entry.Config, name);
            var options = new McpClientOptions
            {
                ClientInfo = new Implementation { Name = ClientName, Version = AppVersion() },
                InitializationTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            };
            client = await McpClient.CreateAsync(transport, options, null, budget.Token).ConfigureAwait(false);
            var listed = await client.ListToolsAsync((ModelContextProtocol.RequestOptions?)null, budget.Token).ConfigureAwait(false);
            var names = McpToolName.Unique(listed.Select(t => McpToolName.Prefixed(name, t.Name)).ToList());
            var tools = listed.Select((t, i) => new McpTool(name, client, t, names[i])).ToList();
            var elapsed = _time.GetElapsedTime(started);
            lock (_snapshot)
            {
                _connections[name] = new Connection(client, entry.Config.Fingerprint(), tools);
            }

            DiagnosticLog.Info(Category, McpText.ConnectedLogLine(name, tools.Count, elapsed));
            return new McpServerStatus(name, entry.Scope, null, true, McpState.Connected, null, tools, entry.Config.Describe());
        }
        catch (Exception ex)
        {
            string detail = ex is OperationCanceledException
                ? (cancellationToken.IsCancellationRequested ? McpText.Cancelled : McpText.TimedOut(timeoutSeconds))
                : LogText.Excerpt(ex.Message);
            DiagnosticLog.Info(Category, McpText.FailedLogLine(name, detail));
            if (client is not null)
            {
                await DisposeQuietlyAsync(client).ConfigureAwait(false);
            }

            return new McpServerStatus(name, entry.Scope, null, true, McpState.Failed, detail, [], entry.Config.Describe());
        }
    }

    private static string AppVersion()
    {
        var v = typeof(McpSession).Assembly.GetName().Version;
        return v is null ? "0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private void Publish(IReadOnlyList<McpServerStatus> rows, IReadOnlyList<McpConfigProblem> problems, bool attempted)
    {
        lock (_snapshot)
        {
            _servers = rows.ToArray();
            _problems = problems;
            _attempted = attempted;
            RebuildTools(rows);
        }
    }

    /// <summary>Under the snapshot lock.</summary>
    private void RebuildTools(IReadOnlyList<McpServerStatus> rows)
    {
        var connected = rows.Where(r => r.State == McpState.Connected && r.Tools.Count > 0).ToList();
        _serverTools = connected.Select(r => new McpServerTools(r.Name, r.Tools)).ToArray();
        _tools = connected.SelectMany(r => r.Tools).ToArray();
    }

    private static void Replace(List<McpServerStatus> rows, McpServerStatus row)
    {
        int i = rows.FindIndex(r => r.Name == row.Name);
        if (i >= 0)
        {
            rows[i] = row;
        }
        else
        {
            rows.Add(row);
        }
    }

    private async Task DisposeOneAsync(string name)
    {
        Connection? live;
        lock (_snapshot)
        {
            if (!_connections.Remove(name, out live))
            {
                return;
            }
        }

        await DisposeQuietlyAsync(live.Client).ConfigureAwait(false);
        DiagnosticLog.Info(Category, McpText.StoppedLogLine(name));
    }

    private async Task DisposeAllAsync()
    {
        foreach (string name in _connections.Keys.ToList())
        {
            await DisposeOneAsync(name).ConfigureAwait(false);
        }
    }

    private static async Task DisposeQuietlyAsync(McpClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Debug(Category, "Dispose: " + LogText.Excerpt(ex.Message));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeAllAsync().ConfigureAwait(false);
            Publish([], [], attempted: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The synchronous form for the crash path: blocks until every client is gone.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
