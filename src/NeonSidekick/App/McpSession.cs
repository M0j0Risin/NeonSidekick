using System.Collections.Concurrent;
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

    /// <summary>
    /// Why a connected server's client is gone (2026-11-19, feature idea #30): <c>(exit code 1)</c>, <c>(HTTP 404)</c>, or empty when
    /// that isn't known; null while it's live. The row stays <see cref="McpState.Connected"/>, its tools still offered, and the next call
    /// reconnects first. Apart from <see cref="Detail"/>, the <see cref="McpState.Failed"/> reason. An init property, not a positional one,
    /// so the rows the tests build don't change.
    /// </summary>
    public string? Dropped { get; init; }

    /// <summary>
    /// The server's <c>ask</c> (2026-11-19, feature idea #28), from its live connection: the <c>/mcp</c> footer, the Tools tab's tag and
    /// <c>neon_help</c> read it, since the published rows are all a static reader has. <see cref="McpAskMode.Writes"/> on a row that isn't
    /// connected; an init property, so the tests' rows don't change.
    /// </summary>
    public McpAskMode Ask { get; init; }

    /// <summary>How many characters of notes the connected server sends (2026-11-19, feature idea #59): null for none. The row never carries the text.</summary>
    public int? NotesLength { get; init; }

    /// <summary>Whether the server's notes are kept: false for <c>"instructions": false</c> in its entry.</summary>
    public bool NotesOn { get; init; } = true;
}

/// <summary>One connected server's tools, for the <c>/usage</c> and <c>/mcp</c> groups.</summary>
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
/// <remarks>
/// Since 2026-11-19 (feature idea #30) a server that drops reconnects once, at its next call: every tool calls through
/// <see cref="CallAsync"/> by server name, so even the turn's tools reach the new client, and a call the drop cut off is sent
/// again only when the server marks it read-only. A changed tool list (<c>notifications/tools/list_changed</c>) is listed again in the
/// background and applies from the next turn. What the user should know is queued (<see cref="TakeNotices"/>,
/// <see cref="NoticeQueued"/>) for the host to print. Two locks: <c>_gate</c> for every connect (the wave, a flip, a reload, a
/// reconnect) and the snapshot lock for every read and write of the connections and rows, since the drop watcher and the background
/// listing run on the SDK's threads and never take the gate.
/// </remarks>
internal sealed class McpSession : IAsyncDisposable, IDisposable
{
    public const string Category = McpConfigFile.Category;

    /// <summary>What the servers are told the client is.</summary>
    public const string ClientName = "NeonSidekick";

    /// <summary>A background tool listing's state (2026-11-19), shared by every <c>with</c> copy of its connection: one in flight, one more owed.</summary>
    private sealed class Listing
    {
        public Task? Running;
        public bool Again;
    }

    /// <summary>
    /// One live client: the entry and the timeout it was started with (a reconnect reuses both, so an <c>mcp.json</c> edit waits for a
    /// reload), its tools, and <see cref="Dropped"/> once its session ended without the app stopping it. <see cref="Instructions"/> (2026-11-19,
    /// feature idea #59) is the server's own text as the handshake gave it, cut at <see cref="McpText.NotesPerServer"/>, or null for none
    /// or an entry that opts out: read at each connect, so a reconnect refreshes it.
    /// </summary>
    private sealed record Connection(McpClient Client, string Fingerprint, IReadOnlyList<McpTool> Tools, McpServerEntry Entry, int TimeoutSeconds, Listing Listing, string? Instructions)
    {
        public string? Dropped { get; init; }

        /// <summary>Marked dropped, or its session already over (the watcher not run yet).</summary>
        public bool Dead => Dropped is not null || Client.Completion.IsCompleted;
    }

    /// <summary>What a call has to go on: the server's live connection, or the error the call returns instead.</summary>
    private readonly record struct Ready(Connection? Live, McpCallOutcome? Refusal, string? Reconnected);

    /// <summary>What one send came to: the server's answer, or a drop with what the client knew about it.</summary>
    private readonly record struct Sent(CallToolResult? Result, ClientCompletionDetails? Details);

    private readonly AppSettings _settings;
    private readonly Func<McpServerConfig, string, IClientTransport> _transport;
    private readonly TimeProvider _time;
    private readonly string _retryTail;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Text, bool Error)> _notices = new();
    private readonly List<Task> _listings = [];
    private readonly object _snapshot = new();
    private IReadOnlyList<McpServerStatus> _servers = [];
    private IReadOnlyList<McpConfigProblem> _problems = [];
    private IReadOnlyList<AIFunction> _tools = [];
    private IReadOnlyList<McpServerTools> _serverTools = [];
    private IReadOnlyList<McpServerNote> _notes = [];
    private bool _attempted;
    private bool _disposed;

    /// <param name="retryTail">How the texts of a server that didn't come back end (2026-11-19): <see cref="McpText.RetryTail"/> by default,
    /// <see cref="McpText.HeadlessRetryTail"/> from headless, which has no <c>/mcp</c>.</param>
    public McpSession(AppSettings settings, Func<McpServerConfig, string, IClientTransport> transport, TimeProvider? time = null, string? retryTail = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _time = time ?? TimeProvider.System;
        _retryTail = retryTail ?? McpText.RetryTail;
    }

    /// <summary>Every configured server as of the last read, in file order (the profile's, then the home's).</summary>
    public IReadOnlyList<McpServerStatus> Servers { get { lock (_snapshot) { return _servers; } } }

    /// <summary>What the last read could not use.</summary>
    public IReadOnlyList<McpConfigProblem> Problems { get { lock (_snapshot) { return _problems; } } }

    /// <summary>Every connected server's tools, flat, in server order: what a turn offers.</summary>
    public IReadOnlyList<AIFunction> Tools { get { lock (_snapshot) { return _tools; } } }

    /// <summary>Every connected server's tools by server: the <c>/usage</c> and <c>/mcp</c> groups.</summary>
    public IReadOnlyList<McpServerTools> ServerTools { get { lock (_snapshot) { return _serverTools; } } }

    /// <summary>
    /// Every connected server's notes (2026-11-19, feature idea #59), in the catalog's order, for the servers that don't opt out and send a
    /// non-blank text. The session publishes them all; a turn keeps the servers with a tool in it (<see cref="McpNotes.For"/>).
    /// </summary>
    public IReadOnlyList<McpServerNote> ServerInstructions { get { lock (_snapshot) { return _notes; } } }

    /// <summary>
    /// Who answers a call that asks (2026-11-19, feature idea #28): the screen's pane, or headless's <c>--allow</c> judge. Null means
    /// nobody can be asked, so such a call is refused: the safe default needs no wiring. Read once per call that asks.
    /// </summary>
    public Func<McpAsk, CancellationToken, Task<McpVerdict>>? Approver { get; set; }

    /// <summary>The names of the offered tools that would ask under their server's <c>ask</c>: what an unattended Ralph pass leaves out.</summary>
    public IReadOnlySet<string> AskingNames()
    {
        lock (_snapshot)
        {
            return _servers
                .Where(r => r.State == McpState.Connected)
                .SelectMany(r => r.Tools.Where(t => McpAskRule.Asks(r.Ask, t)).Select(t => t.Name))
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>Whether either file names a server or has a problem worth showing.</summary>
    public bool Configured => Servers.Count > 0 || Problems.Count > 0;

    /// <summary>Whether the last <see cref="ConnectAllAsync"/> started at least one server (the status line prints only then).</summary>
    public bool Attempted { get { lock (_snapshot) { return _attempted; } } }

    /// <summary>The profile's file and the home's, as the pane's edit rows open them.</summary>
    public string ProfilePath => McpConfigFile.ProfilePath(_settings.ProfileDirectory);
    public string GlobalPath => McpConfigFile.GlobalPath(_settings.StorageDirectory);

    /// <summary>
    /// Raised when a notice is queued (2026-11-19), on the producing thread (a call's, the SDK's): the handler only nudges the host's
    /// loop, never blocks. The host takes them with <see cref="TakeNotices"/>.
    /// </summary>
    public event Action? NoticeQueued;

    /// <summary>The notices queued since the last take, oldest first: a reconnect, a server that didn't come back (an error), a changed list.</summary>
    public IReadOnlyList<(string Text, bool Error)> TakeNotices()
    {
        var taken = new List<(string Text, bool Error)>();
        while (_notices.TryDequeue(out var notice))
        {
            taken.Add(notice);
        }

        return taken;
    }

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
                // Row by row on the rows as they stand (2026-11-19): a server that connected and dropped while another still connects keeps its mark.
                PublishRow(outcome);
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
            PublishRow(Initial(entry, enabled: true, ToolsText.DisabledSet([])) with { State = McpState.Connecting }, merged.Problems, attempted: true);
            var outcome = await ConnectOneAsync(entry, effective.McpConnectTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            PublishRow(outcome, merged.Problems, attempted: true);
            return outcome.State == McpState.Connected;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reconnects a dropped server by name (2026-11-19, Enter on its <c>/mcp</c> row), from the config it was connected with, as a call
    /// would: nothing saved, no notice queued (the pane says how it went). The outcome row, or null when the server has no connection to
    /// start again from or the session was stopped. The caller's cancel is rethrown and leaves the row dropped.
    /// </summary>
    public async Task<McpServerStatus?> ReconnectAsync(string name, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            Connection? live;
            lock (_snapshot)
            {
                if (_disposed || !_connections.TryGetValue(name, out live))
                {
                    return null;
                }
            }

            return await ReconnectLockedAsync(live, null, quiet: true, cancellationToken, linked.Token).ConfigureAwait(false);
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
            var row = Servers.FirstOrDefault(r => r.Name == name);
            if (row is not null)
            {
                PublishRow(row with { Enabled = false, State = McpState.Off, Detail = null, Tools = [] });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-reads both files and reconciles: a new or changed server is started, a gone one stopped, an
    /// unchanged connected one kept as it is. Returns the counts for the notice. Since 2026-11-19 a dropped one is started again even
    /// when its config didn't change, counted like a changed one: kept, its row would hide a dead client.
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
            List<string> stale;
            lock (_snapshot)
            {
                // Read under the snapshot lock (2026-11-19): the watcher and the background listing write the connections without the gate.
                stale = _connections
                    .Where(c => !entries.TryGetValue(c.Key, out var entry) || !entry.Startable || entry.Config.Fingerprint() != c.Value.Fingerprint || c.Value.Dead)
                    .Select(c => c.Key)
                    .ToList();
            }

            foreach (string name in stale)
            {
                await DisposeOneAsync(name).ConfigureAwait(false);
            }

            var toStart = new List<McpServerEntry>();
            int kept = 0;
            lock (_snapshot)
            {
                var rows = new List<McpServerStatus>();
                foreach (var entry in merged.Entries)
                {
                    if (_connections.TryGetValue(entry.Name, out var live))
                    {
                        rows.Add(ConnectedFacts(new McpServerStatus(entry.Name, entry.Scope, null, true, McpState.Connected, null, live.Tools, entry.Config.Describe()) { Dropped = live.Dropped }, live));
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

                Publish(rows, merged.Problems, attempted: _attempted || toStart.Count > 0);
            }

            var outcomes = await Task.WhenAll(toStart.Select(e => ConnectOneAsync(e, effective.McpConnectTimeoutSeconds, cancellationToken))).ConfigureAwait(false);
            foreach (var outcome in outcomes)
            {
                PublishRow(outcome);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return (toStart.Count, stale.Count, kept);
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
        // A stored value decrypted only here (2026-11-19, feature idea #29): the transport gets the plain copy, while the entry, the
        // fingerprint and the row keep the stored config. A value that can't be read here fails the server; nothing starts.
        var revealed = McpSecrets.Reveal(entry.Config);
        if (revealed.Error is { } unreadable)
        {
            DiagnosticLog.Info(Category, McpText.FailedLogLine(name, unreadable));
            return new McpServerStatus(name, entry.Scope, null, true, McpState.Failed, unreadable, [], entry.Config.Describe());
        }

        McpClient? client = null;
        try
        {
            var transport = _transport(revealed.Config!, name);
            var options = new McpClientOptions
            {
                ClientInfo = new Implementation { Name = ClientName, Version = AppVersion() },
                InitializationTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            };
            client = await McpClient.CreateAsync(transport, options, null, budget.Token).ConfigureAwait(false);
            // The server's notes (2026-11-19, feature idea #59) only through the SDK's property, so a later spec that moves them keeps working.
            string? instructions = entry.Config.InstructionsOn ? McpNotes.Cut(name, client.ServerInstructions) : null;
            var listed = await client.ListToolsAsync((ModelContextProtocol.RequestOptions?)null, budget.Token).ConfigureAwait(false);
            var tools = BuildTools(name, client, listed);
            var elapsed = _time.GetElapsedTime(started);
            var connected = client;
            lock (_snapshot)
            {
                _connections[name] = new Connection(connected, entry.Config.Fingerprint(), tools, entry, timeoutSeconds, new Listing(), instructions);
            }

            // The watcher only after the store (2026-11-19): a client that died since CreateAsync finds itself current, and its finished
            // Completion runs the watcher at once, so the row reads dropped rather than plain connected. No test can hit that window on purpose.
            _ = WatchAsync(name, connected);
            _ = connected.RegisterNotificationHandler(NotificationMethods.ToolListChangedNotification, (_, _) =>
            {
                ToolsChanged(name, connected);
                return ValueTask.CompletedTask;
            });
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

    /// <summary>A server's listed tools as the model sees them: prefixed, de-duplicated, each calling through the session. One helper for a connect and a changed list, so a tool keeps its name.</summary>
    private List<McpTool> BuildTools(string name, McpClient client, IList<McpClientTool> listed)
    {
        var names = McpToolName.Unique(listed.Select(t => McpToolName.Prefixed(name, t.Name)).ToList());
        return listed.Select((t, i) => new McpTool(name, client, t, names[i], CallAsync)).ToList();
    }

    private static string AppVersion()
    {
        var v = typeof(McpSession).Assembly.GetName().Version;
        return v is null ? "0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    // ── Drops ───────────────────────────────────────────────────────────────

    /// <summary>Awaits the client's end (it always completes successfully) and marks the connection dropped if it is still the current one.</summary>
    private async Task WatchAsync(string name, McpClient client)
    {
        try
        {
            var details = await client.Completion.ConfigureAwait(false);
            MarkDropped(name, client, details);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Debug(Category, "Watch: " + LogText.Excerpt(ex.Message));
        }
    }

    /// <summary>
    /// Marks the server's connection dropped when <paramref name="client"/> is still its current one and not marked yet, and shows it on
    /// the row. Never takes the gate: it runs on the SDK's thread while a connect may hold it. The app's own stops take the connection
    /// out before disposing it, so they never land here. The stderr tail goes to a Debug line alone: it can carry tokens.
    /// </summary>
    private void MarkDropped(string name, McpClient client, ClientCompletionDetails? details)
    {
        string reason = McpText.DropReason(details);
        lock (_snapshot)
        {
            if (_disposed || !_connections.TryGetValue(name, out var live) || !ReferenceEquals(live.Client, client) || live.Dropped is not null)
            {
                return;
            }

            _connections[name] = live with { Dropped = reason };
            if (Row(name) is { } row)
            {
                PublishRow(row);
            }
        }

        DiagnosticLog.Info(Category, McpText.DroppedLogLine(name, reason));
        if (details is StdioClientCompletionDetails { StandardErrorTail: { Count: > 0 } tail })
        {
            DiagnosticLog.Debug(Category, McpText.DroppedStderrLogLine(name, string.Join(" | ", tail)));
        }
    }

    /// <summary>
    /// A session-built tool's call (2026-11-19, feature idea #30), by server name: a dropped server is reconnected first (safe for any
    /// tool, nothing was sent); a drop during the call reconnects once and sends the call again only for a read-only tool; a server that
    /// drops on that retry, or on the call right after its reconnect, is taken as crashing on it and goes <see cref="McpState.Failed"/>.
    /// Every one of those is a returned <c>Error:</c> result. The turn's own cancel is rethrown; any other exception from a server whose
    /// session is still alive is an answer and is thrown as before.
    /// </summary>
    private async ValueTask<McpCallOutcome> CallAsync(McpTool tool, CallToolRequestParams request, CancellationToken cancellationToken)
    {
        string name = tool.ServerName;
        string toolName = tool.ServerToolName;
        var ready = Usable(name, toolName);
        if (ready.Refusal is null && (ready.Live is null || ready.Live.Dead))
        {
            ready = await ReconnectForCallAsync(name, toolName, ready.Live?.Client, null, cancellationToken).ConfigureAwait(false);
        }

        if (ready.Refusal is { } refused)
        {
            return refused;
        }

        // The reconnected notice waits for the call (2026-11-19): a server that drops on it again gives the one "didn't come back" notice.
        string? reconnected = ready.Reconnected;
        var live = ready.Live!;
        if (Offered(live, tool) is not { } sending)
        {
            Reconnected(name, reconnected);
            return McpCallOutcome.Failed(McpText.NoLongerOffersError(name, tool.ServerToolName));
        }

        // The ask (2026-11-19, feature idea #28): judged on the tool as the current list describes it, right before the send.
        var mode = live.Entry.Config.AskMode;
        var arguments = (IReadOnlyDictionary<string, System.Text.Json.JsonElement>?)request.Arguments?.ToDictionary(StringComparer.Ordinal);
        if (McpAskRule.Asks(mode, sending))
        {
            var ask = new McpAsk(name, toolName, sending.Name, arguments ?? new Dictionary<string, System.Text.Json.JsonElement>(), McpAskRule.Note(sending));
            var verdict = Approver is { } approver ? await approver(ask, cancellationToken).ConfigureAwait(false) : McpVerdict.Nobody;
            switch (verdict)
            {
                case McpVerdict.Allowed(var how):
                    DiagnosticLog.Info(Category, McpText.AuditLogLine(sending.Name, how, arguments));
                    break;
                case McpVerdict.NotAsked(var tail):
                    DiagnosticLog.Info(Category, McpText.AuditLogLine(sending.Name, McpText.NotAskedWord, arguments));
                    Reconnected(name, reconnected);
                    return McpCallOutcome.Failed(McpText.NotAskedError(sending.Name, tail));
                default:
                    DiagnosticLog.Info(Category, McpText.AuditLogLine(sending.Name, McpText.DeniedWord, arguments));
                    Reconnected(name, reconnected);
                    return McpCallOutcome.Failed(McpText.DeniedError(sending.Name));
            }

            // The pane may have waited: the server can have been switched off, removed, or dropped meanwhile. A dropped one reconnects first
            // (nothing was sent, so that's safe for any tool) and the call goes out without asking again: the user said yes to this call.
            var after = Usable(name, toolName);
            if (after.Refusal is null && (after.Live is null || after.Live.Dead))
            {
                after = await ReconnectForCallAsync(name, toolName, after.Live?.Client, null, cancellationToken).ConfigureAwait(false);
            }

            if (after.Refusal is { } refusedAfter)
            {
                Reconnected(name, reconnected);
                return refusedAfter;
            }

            reconnected = after.Reconnected ?? reconnected;
            live = after.Live!;
            if (Offered(live, tool) is not { } still)
            {
                Reconnected(name, reconnected);
                return McpCallOutcome.Failed(McpText.NoLongerOffersError(name, tool.ServerToolName));
            }

            sending = still;
        }
        else if (!sending.ReadOnly)
        {
            DiagnosticLog.Info(Category, McpText.AuditLogLine(sending.Name, McpText.RanUnasked, arguments));
        }

        var first = await SendAsync(live, request, cancellationToken).ConfigureAwait(false);
        if (first.Result is { } answer)
        {
            Reconnected(name, reconnected);
            return McpCallOutcome.Answered(answer);
        }

        if (reconnected is not null)
        {
            // Dropped again right after its reconnect: no second one for this call.
            return await DroppedAgainAsync(name, live, first.Details).ConfigureAwait(false);
        }

        var again = await ReconnectForCallAsync(name, toolName, live.Client, first.Details, cancellationToken).ConfigureAwait(false);
        if (again.Refusal is { } gone)
        {
            return gone;
        }

        var back = again.Live!;
        if (!sending.ReadOnly)
        {
            Reconnected(name, again.Reconnected);
            return McpCallOutcome.Failed(McpText.DroppedNotResentError(name));
        }

        if (Offered(back, tool) is null)
        {
            Reconnected(name, again.Reconnected);
            return McpCallOutcome.Failed(McpText.NoLongerOffersError(name, tool.ServerToolName));
        }

        var second = await SendAsync(back, request, cancellationToken).ConfigureAwait(false);
        if (second.Result is { } retried)
        {
            Reconnected(name, again.Reconnected);
            return McpCallOutcome.Answered(retried);
        }

        return await DroppedAgainAsync(name, back, second.Details).ConfigureAwait(false);
    }

    /// <summary>The tool as the server's current list has it, by its name on the server, or null when the list no longer holds it.</summary>
    private static McpTool? Offered(Connection live, McpTool tool) =>
        live.Tools.FirstOrDefault(t => string.Equals(t.ServerToolName, tool.ServerToolName, StringComparison.Ordinal));

    private void Reconnected(string name, string? reason)
    {
        if (reason is not null)
        {
            Notice(McpText.ReconnectedNotice(name, reason), error: false);
        }
    }

    /// <summary>The server's connection as it stands, without the gate: live (maybe dead, for the caller to reconnect), or the call's refusal.</summary>
    private Ready Usable(string name, string tool)
    {
        lock (_snapshot)
        {
            if (_disposed)
            {
                return new Ready(null, McpCallOutcome.Failed(McpText.StoppedError(name)), null);
            }

            if (_connections.TryGetValue(name, out var live))
            {
                return new Ready(live, null, null);
            }

            return Row(name) switch
            {
                { State: McpState.Failed } row => new Ready(null, NotBack(name, row.Detail ?? ""), null),
                { State: McpState.Connecting } => new Ready(null, null, null),   // a connect holds the gate: wait for it
                _ => new Ready(null, McpCallOutcome.Failed(McpText.NoLongerOffersError(name, tool)), null),
            };
        }
    }

    /// <summary>
    /// Under the gate: the server's connection once whatever held the gate is done. A live one other than <paramref name="dead"/> (another
    /// call's reconnect, a reload) is used as it is; a dead one is reconnected once. The session's stop is the stopped text; the call's
    /// own cancel is rethrown.
    /// </summary>
    private async Task<Ready> ReconnectForCallAsync(string name, string tool, McpClient? dead, ClientCompletionDetails? details, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Ready(null, McpCallOutcome.Failed(McpText.StoppedError(name)), null);
        }

        try
        {
            var now = Usable(name, tool);
            if (now.Refusal is not null)
            {
                return now;
            }

            if (now.Live is not { } live)
            {
                return new Ready(null, McpCallOutcome.Failed(McpText.NoLongerOffersError(name, tool)), null);
            }

            if (!ReferenceEquals(live.Client, dead) && !live.Dead)
            {
                return now;
            }

            string reason = live.Dropped ?? McpText.DropReason(details ?? (live.Client.Completion.IsCompleted ? live.Client.Completion.Result : null));
            var outcome = await ReconnectLockedAsync(live, details, quiet: false, cancellationToken, linked.Token).ConfigureAwait(false);
            if (outcome is null)
            {
                return new Ready(null, McpCallOutcome.Failed(McpText.StoppedError(name)), null);
            }

            if (outcome.State != McpState.Connected)
            {
                return new Ready(null, NotBack(name, outcome.Detail ?? ""), null);
            }

            lock (_snapshot)
            {
                return _connections.TryGetValue(name, out var fresh)
                    ? new Ready(fresh, null, reason)
                    : new Ready(null, McpCallOutcome.Failed(McpText.StoppedError(name)), null);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Under the gate: starts <paramref name="dead"/>'s server again from the entry and timeout it was connected with, through the same
    /// transport (a stdio server gets the withheld environment as at the start). <c>ConnectOneAsync</c> never throws on a cancel, so the
    /// tokens are read here: the call's cancel is rethrown and leaves the row dropped, the dead connection kept for the next call or Enter
    /// to start from; the session's stop is null with nothing published; only a real failure or a timeout publishes
    /// <see cref="McpState.Failed"/>, its tools gone from the next turn.
    /// </summary>
    private async Task<McpServerStatus?> ReconnectLockedAsync(Connection dead, ClientCompletionDetails? details, bool quiet, CancellationToken call, CancellationToken linked)
    {
        string name = dead.Entry.Name;
        MarkDropped(name, dead.Client, details ?? (dead.Client.Completion.IsCompleted ? dead.Client.Completion.Result : null));
        await DisposeQuietlyAsync(dead.Client).ConfigureAwait(false);
        var outcome = await ConnectOneAsync(dead.Entry, dead.TimeoutSeconds, linked).ConfigureAwait(false);
        if (outcome.State == McpState.Connected)
        {
            PublishRow(outcome);
            DiagnosticLog.Info(Category, McpText.ReconnectedLogLine(name, outcome.Tools.Count));
            return outcome;
        }

        call.ThrowIfCancellationRequested();
        if (_stop.IsCancellationRequested)
        {
            return null;
        }

        lock (_snapshot)
        {
            if (_connections.TryGetValue(name, out var current) && ReferenceEquals(current.Client, dead.Client))
            {
                _connections.Remove(name);
            }

            PublishRow(outcome);
        }

        DiagnosticLog.Info(Category, McpText.ReconnectFailedLogLine(name, outcome.Detail ?? ""));
        if (!quiet)
        {
            Notice(McpText.NotBackNotice(name, outcome.Detail ?? "", _retryTail), error: true);
        }

        return outcome;
    }

    /// <summary>
    /// A server that dropped on the call right after its reconnect: taken as crashing on it, so no further reconnect. Its connection goes
    /// and its row is <see cref="McpState.Failed"/> (<see cref="McpText.DroppedAgainDetail"/>), with one notice; only <c>/mcp</c> retries it.
    /// </summary>
    private async Task<McpCallOutcome> DroppedAgainAsync(string name, Connection live, ClientCompletionDetails? details)
    {
        var failed = new McpServerStatus(name, live.Entry.Scope, null, true, McpState.Failed, McpText.DroppedAgainDetail, [], live.Entry.Config.Describe());
        MarkDropped(name, live.Client, details);
        bool ours;
        lock (_snapshot)
        {
            ours = !_disposed && _connections.TryGetValue(name, out var current) && ReferenceEquals(current.Client, live.Client);
            if (ours)
            {
                _connections.Remove(name);
                PublishRow(failed);
            }
        }

        if (ours)
        {
            await DisposeQuietlyAsync(live.Client).ConfigureAwait(false);
            DiagnosticLog.Info(Category, McpText.ReconnectFailedLogLine(name, McpText.DroppedAgainDetail));
            Notice(McpText.NotBackNotice(name, McpText.DroppedAgainDetail, _retryTail), error: true);
        }

        return McpCallOutcome.Failed(McpText.DroppedAgainError(name, _retryTail));
    }

    /// <summary>
    /// Sends the call, awaited together with the client's end so a dead transport never hangs it. A drop is the client's session having
    /// ended: first, during the call, or as the call's <see cref="ClientTransportClosedException"/> (thrown as the transport closes, its
    /// details on it). The call left behind is observed, since it faults later as the dead client is disposed. The call's own cancel is
    /// rethrown; any other exception while the session lives is the server's answer and is thrown.
    /// </summary>
    private static async Task<Sent> SendAsync(Connection live, CallToolRequestParams request, CancellationToken cancellationToken)
    {
        var completion = live.Client.Completion;
        var call = live.Client.CallToolAsync(request, cancellationToken).AsTask();
        var first = await Task.WhenAny(call, completion).ConfigureAwait(false);
        if (!ReferenceEquals(first, call) && !call.IsCompleted)
        {
            _ = call.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new Sent(null, completion.Result);
        }

        try
        {
            return new Sent(await call.ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ClientTransportClosedException closed)
        {
            return new Sent(null, closed.Details);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && completion.IsCompleted)
        {
            return new Sent(null, completion.Result);
        }
    }

    /// <summary>
    /// The text for a server that didn't come back. Its detail can be the server's own (a connect exception's message, such as a JSON-RPC
    /// error from its <c>initialize</c>), so it is fenced on its own; the app's own details (a timeout, a cancel, a drop on the retry) aren't.
    /// </summary>
    private McpCallOutcome NotBack(string name, string detail)
    {
        bool own = detail == McpText.Cancelled || detail == McpText.DroppedAgainDetail || detail.StartsWith(McpText.TimedOutPrefix, StringComparison.Ordinal);
        return own
            ? McpCallOutcome.Failed(McpText.NotBackError(name, detail, _retryTail))
            : McpCallOutcome.Failed(McpText.NotBackError(name, Llm.Tools.UntrustedText.Fenced(detail), _retryTail), fenced: true);
    }

    private void Notice(string text, bool error)
    {
        _notices.Enqueue((text, error));
        NoticeQueued?.Invoke();
    }

    // ── A changed tool list ─────────────────────────────────────────────────

    /// <summary>
    /// The SDK's <c>notifications/tools/list_changed</c> handler: returns at once (it runs on the SDK's message loop). One listing in flight
    /// per connection, and one more after it when another notice came meanwhile; it runs on a task the session awaits at dispose.
    /// </summary>
    private void ToolsChanged(string name, McpClient client)
    {
        lock (_snapshot)
        {
            if (_disposed || !IsCurrent(name, client, out var live))
            {
                return;
            }

            if (live.Listing.Running is not null)
            {
                live.Listing.Again = true;
                return;
            }

            var listing = live.Listing;
            var task = Task.Run(() => ListAgainAsync(name, client, listing));
            listing.Running = task;
            _listings.RemoveAll(t => t.IsCompleted);
            _listings.Add(task);
        }
    }

    /// <summary>For the tests: returns once no background listing is in flight.</summary>
    internal async Task ListingsSettledAsync()
    {
        while (true)
        {
            Task[] running;
            lock (_snapshot)
            {
                running = _listings.Where(t => !t.IsCompleted).ToArray();
            }

            if (running.Length == 0)
            {
                return;
            }

            await Task.WhenAll(running).ConfigureAwait(false);
        }
    }

    private async Task ListAgainAsync(string name, McpClient client, Listing listing)
    {
        while (true)
        {
            try
            {
                var listed = await client.ListToolsAsync((ModelContextProtocol.RequestOptions?)null, _stop.Token).ConfigureAwait(false);
                ApplyListing(name, client, listed);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (!_stop.IsCancellationRequested)
                {
                    DiagnosticLog.Info(Category, McpText.ListingFailedLogLine(name, LogText.Excerpt(ex.Message)));
                }
            }

            lock (_snapshot)
            {
                if (!listing.Again || _stop.IsCancellationRequested || !IsCurrent(name, client, out _))
                {
                    listing.Running = null;
                    return;
                }

                listing.Again = false;
            }
        }
    }

    /// <summary>
    /// A new listing for the next turn, applied only while its client is still the server's current one (compared by reference: the
    /// record is swapped with <c>with</c>). A list equal to the old one (names, descriptions and schemas as raw JSON) publishes nothing
    /// and gives no notice. The turn running now keeps the tools it took at its start.
    /// </summary>
    private void ApplyListing(string name, McpClient client, IList<McpClientTool> listed)
    {
        int count;
        lock (_snapshot)
        {
            if (_disposed || !IsCurrent(name, client, out var live) || SameTools(live.Tools, listed))
            {
                return;
            }

            var tools = BuildTools(name, client, listed);
            _connections[name] = live with { Tools = tools };
            if (Row(name) is { } row)
            {
                PublishRow(row);
            }

            count = tools.Count;
        }

        DiagnosticLog.Info(Category, McpText.ToolsChangedLogLine(name, count));
        Notice(McpText.ToolsChangedNotice(name, count), error: false);
    }

    private static bool SameTools(IReadOnlyList<McpTool> tools, IList<McpClientTool> listed)
    {
        if (tools.Count != listed.Count)
        {
            return false;
        }

        for (int i = 0; i < tools.Count; i++)
        {
            var was = tools[i].ProtocolTool;
            var now = listed[i].ProtocolTool;
            if (!string.Equals(was.Name, now.Name, StringComparison.Ordinal)
                || !string.Equals(was.Description, now.Description, StringComparison.Ordinal)
                || !string.Equals(was.InputSchema.GetRawText(), now.InputSchema.GetRawText(), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // ── The rows ────────────────────────────────────────────────────────────

    /// <summary>Under the snapshot lock: whether <paramref name="client"/> is the server's current connection's.</summary>
    private bool IsCurrent(string name, McpClient client, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Connection? live) =>
        _connections.TryGetValue(name, out live) && ReferenceEquals(live.Client, client);

    /// <summary>Under the snapshot lock: the server's row (the startable one, a shadowed global sharing its name).</summary>
    private McpServerStatus? Row(string name) => _servers.FirstOrDefault(r => r.Name == name && r.Startable);

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

    /// <summary>
    /// Replaces one row in the rows as they stand, read and written under one snapshot lock (2026-11-19), so no path publishes from a
    /// copy taken earlier and writes over a drop the watcher marked meanwhile. A connected row takes its tools and <c>Dropped</c> from the
    /// server's current connection; any other row has no drop.
    /// </summary>
    private void PublishRow(McpServerStatus row, IReadOnlyList<McpConfigProblem>? problems = null, bool? attempted = null)
    {
        lock (_snapshot)
        {
            row = row.State == McpState.Connected && _connections.TryGetValue(row.Name, out var live)
                ? ConnectedFacts(row with { Tools = live.Tools, Dropped = live.Dropped }, live)
                : row with { Dropped = null };
            var rows = _servers.ToList();
            Replace(rows, row);
            _servers = rows.ToArray();
            if (problems is not null)
            {
                _problems = problems;
            }

            if (attempted is { } tried)
            {
                _attempted = tried;
            }

            RebuildTools(rows);
        }
    }

    /// <summary>Under the snapshot lock.</summary>
    private void RebuildTools(IReadOnlyList<McpServerStatus> rows)
    {
        var connected = rows.Where(r => r.State == McpState.Connected && r.Tools.Count > 0).ToList();
        _serverTools = connected.Select(r => new McpServerTools(r.Name, r.Tools)).ToArray();
        _tools = connected.SelectMany(r => r.Tools).ToArray();
        _notes = rows
            .Where(r => r.State == McpState.Connected && r.Startable)
            .Select(r => _connections.TryGetValue(r.Name, out var live) && !string.IsNullOrWhiteSpace(live.Instructions) ? new McpServerNote(r.Name, live.Instructions) : null)
            .OfType<McpServerNote>()
            .ToArray();
    }

    /// <summary>
    /// A connected row with the facts only its live connection knows (2026-11-19): the server's <c>ask</c> and its notes' length. One
    /// helper for a connect, a reload's kept connection (a new row that would otherwise reset both) and every published row.
    /// </summary>
    private static McpServerStatus ConnectedFacts(McpServerStatus row, Connection live) => row with
    {
        Ask = live.Entry.Config.AskMode,
        NotesLength = string.IsNullOrWhiteSpace(live.Instructions) ? null : live.Instructions.Length,
        NotesOn = live.Entry.Config.InstructionsOn,
    };

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

    // ── Stops ───────────────────────────────────────────────────────────────

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
        List<string> names;
        lock (_snapshot)
        {
            names = _connections.Keys.ToList();
        }

        foreach (string name in names)
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

    /// <summary>
    /// Drops every client. The session's own token is cancelled first (2026-11-19), so a reconnect stuck on a server that won't start
    /// ends at once rather than at its timeout and its call returns the stopped text; it is never disposed, so a second call (headless
    /// makes one) finds nothing to stop. The background listings are awaited last.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_snapshot)
            {
                _disposed = true;
            }

            await DisposeAllAsync().ConfigureAwait(false);
            Publish([], [], attempted: false);
            Task[] listings;
            lock (_snapshot)
            {
                listings = _listings.ToArray();
                _listings.Clear();
            }

            await Task.WhenAll(listings).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The synchronous form for the crash path: blocks until every client is gone.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
