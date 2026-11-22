using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Authentication;
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
    /// The first of <paramref name="servers"/> listing a tool named <paramref name="name"/> (the model's prefixed name), with that tool; null
    /// for none (2026-11-20, the mcp-ask review's #119). Each caller passes its own rows: prefixed names are unique within one server only
    /// (<c>my.srv</c> and <c>my_srv</c> both make <c>my_srv__echo</c>), so which rows are searched decides the pick. Pure.
    /// </summary>
    public static (McpServerStatus Server, McpTool Tool)? FindTool(IEnumerable<McpServerStatus> servers, string name)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(name);
        foreach (var server in servers)
        {
            foreach (var tool in server.Tools)
            {
                if (string.Equals(tool.Name, name, StringComparison.Ordinal))
                {
                    return (server, tool);
                }
            }
        }

        return null;
    }

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

    /// <summary>
    /// How many characters of notes the connected server sends (2026-11-19, feature idea #59): null for none; of notes that were cut
    /// (<see cref="NotesRaw"/> set), the characters of their own text kept. The row never carries the text.
    /// </summary>
    public int? NotesLength { get; init; }

    /// <summary>The notes' whole length when they were cut (2026-11-20, the mcp-ask review's #43): the footer's <c>2000 of 5000</c>. Null when they weren't.</summary>
    public int? NotesRaw { get; init; }

    /// <summary>
    /// Whether the servers' notes together pass the total with every server's tools offered, so this one's are left out (2026-11-20, #43): the
    /// footer's <c>left out</c>. A turn that offers fewer tools may fit them.
    /// </summary>
    public bool NotesLeftOut { get; init; }

    /// <summary>Whether the server's notes are kept: false for <c>"instructions": false</c> in its entry.</summary>
    public bool NotesOn { get; init; } = true;

    /// <summary>
    /// Whether <see cref="Detail"/> is the app's own words (2026-11-20, the mcp-ask review's #118): a cancel, a timeout, a drop on the
    /// retried call, a value that can't be decrypted. A detail that isn't (a connect exception's message, maybe the server's) is fenced
    /// when the model reads it.
    /// </summary>
    public bool DetailIsApps { get; init; }

    /// <summary>
    /// Whether a <see cref="McpState.Failed"/> row is a server that wants a sign-in (2026-11-21, feature idea #32): a flag, not a state, so the
    /// switches over <see cref="McpState"/> keep treating it as failed (a retry, a call's not-back refusal) and only the words change.
    /// </summary>
    public bool NeedsSignIn { get; init; }

    /// <summary>Whether a <see cref="McpState.Connecting"/> row is a requested sign-in waiting for the browser.</summary>
    public bool SigningIn { get; init; }

    /// <summary>Whether a connected row's server holds a sign-in (the footer's "signed in", and <c>s</c> signs out).</summary>
    public bool SignedIn { get; init; }
}

/// <summary>
/// What a server's config becomes on the wire (2026-11-21, feature idea #32): <paramref name="oauth"/> is the sign-in's options, attached
/// only for a server holding a sign-in or one being signed in to; null otherwise.
/// </summary>
public delegate IClientTransport McpTransportFactory(McpServerConfig config, string name, ClientOAuthOptions? oauth);

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
internal sealed class McpSession : IAsyncDisposable, IDisposable, Mcp.IMcpResources
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
    /// reload), its tools, and <see cref="Dropped"/> once its session ended without the app stopping it. <see cref="Notes"/> (2026-11-19,
    /// feature idea #59) is the server's own text as the handshake gave it, scrubbed and cut (<see cref="McpNotes.Cut"/>), or null for none:
    /// read at each connect, so a reconnect refreshes it. Kept whatever the entry says (2026-11-20), so a reload that turns
    /// <c>"instructions"</c> back on, which updates the connection in place, has them; <see cref="Instructions"/> is what the prompt gets.
    /// </summary>
    private sealed record Connection(McpClient Client, string Fingerprint, IReadOnlyList<McpTool> Tools, McpServerEntry Entry, int TimeoutSeconds, Listing Listing, McpCutNotes? Notes)
    {
        public string? Dropped { get; init; }

        /// <summary>Connected with tokens that opened, or by a sign-in (2026-11-22, the review's #91): the footer's "signed in" on every row made from it.</summary>
        public bool SignedIn { get; init; }

        /// <summary>The notes the prompt carries: <see cref="Notes"/>' text, or null for none or an entry that opts out.</summary>
        public string? Instructions => Entry.Config.InstructionsOn ? Notes?.Text : null;

        /// <summary>Marked dropped, or its session already over (the watcher not run yet).</summary>
        public bool Dead => Dropped is not null || Client.Completion.IsCompleted;
    }

    /// <summary>
    /// What a call has to go on: the server's live connection and the tool as its current list has it, or the error the call returns
    /// instead. <see cref="NotBack"/> marks a refusal for a server that didn't come back (2026-11-20, the mcp-ask review's #54): its own
    /// notice says so, and a held "reconnected" notice would contradict it.
    /// </summary>
    private readonly record struct Ready(Connection? Live, McpCallOutcome? Refusal, string? Reconnected, bool NotBack = false, McpTool? Tool = null);

    /// <summary>What one send came to: the server's answer, or a drop with what the client knew about it.</summary>
    private readonly record struct Sent(CallToolResult? Result, ClientCompletionDetails? Details, bool SignInNeeded = false);

    private readonly AppSettings _settings;
    private readonly McpTransportFactory _transport;
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
    private IReadOnlySet<string> _askingNames = new HashSet<string>(StringComparer.Ordinal);
    private HashSet<string> _notesLeftOut = new(StringComparer.Ordinal);

    /// <summary>
    /// The keys of the lines said once per session (2026-11-20, the mcp-ask review's #7: once per process before, so a second session never
    /// said them): a server's notes cut or left out, an unset <c>${NAME}</c>. Its own lock.
    /// </summary>
    private readonly HashSet<string> _loggedOnce = new(StringComparer.Ordinal);

    /// <summary>Lines owed while the snapshot lock was held, written once it is let go (<see cref="FlushLogs"/>): the session never logs under it.</summary>
    private readonly ConcurrentQueue<(string Line, bool Warn)> _owedLogs = new();
    private bool _attempted;
    private bool _disposed;

    /// <param name="retryTail">How the texts of a server that didn't come back end (2026-11-19): <see cref="McpText.RetryTail"/> by default,
    /// <see cref="McpText.HeadlessRetryTail"/> from headless, which has no <c>/mcp</c>.</param>
    public McpSession(AppSettings settings, Func<McpServerConfig, string, IClientTransport> transport, TimeProvider? time = null, string? retryTail = null)
        : this(settings, Wrap(transport), time, retryTail)
    {
    }

    /// <summary>The same over a transport that takes the sign-in's options (2026-11-21): the app's <see cref="DefaultTransport(McpServerConfig, string, ClientOAuthOptions?)"/>, or a test's over the OAuth stub.</summary>
    public McpSession(AppSettings settings, McpTransportFactory transport, TimeProvider? time = null, string? retryTail = null)
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

    /// <summary>
    /// The names of the offered tools that would ask under their server's <c>ask</c>: what an unattended Ralph pass, and a screen with no
    /// pane, leave out. Worked out when the rows are published (2026-11-20, the mcp-ask review's #69), not at every turn.
    /// </summary>
    public IReadOnlySet<string> AskingNames()
    {
        lock (_snapshot)
        {
            return _askingNames;
        }
    }

    /// <summary>
    /// A session allow's key (2026-11-20, the mcp-ask review's #3, #51, #116): the server's name as the catalog holds it, the tool's as the
    /// server names it, the server's config fingerprint and the tool's <see cref="McpTool.DefinitionKey"/> as approved, joined with
    /// <c>\0</c>. A reload to a new command or url, a changed <c>ask</c> or a redefined tool makes a new key, so it asks again. Pure.
    /// </summary>
    public static string McpAllowKey(string server, string serverTool, string fingerprint, string definition) =>
        string.Join('\0', server, serverTool, fingerprint, definition);

    /// <summary>
    /// <see cref="McpAllowKey"/> for the offered tool named <paramref name="prefixedName"/> (the model's name), from its live connection and
    /// current listing, or null when no connected server offers it: what the help's live line and <c>/mcp</c> look a session allow up by.
    /// </summary>
    public string? AllowKeyFor(string prefixedName)
    {
        ArgumentNullException.ThrowIfNull(prefixedName);
        lock (_snapshot)
        {
            foreach (var live in _connections.Values)
            {
                foreach (var tool in live.Tools)
                {
                    if (string.Equals(tool.Name, prefixedName, StringComparison.Ordinal))
                    {
                        return McpAllowKey(live.Entry.Name, tool.ServerToolName, live.Fingerprint, tool.DefinitionKey);
                    }
                }
            }
        }

        return null;
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

    private static McpTransportFactory Wrap(Func<McpServerConfig, string, IClientTransport> transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return (config, name, _) => transport(config, name);
    }

    /// <summary>The real transports with no sign-in: a child process for a <c>command</c>, streamable HTTP (SSE fallback) for a <c>url</c>.</summary>
    public static IClientTransport DefaultTransport(McpServerConfig config, string name) => DefaultTransport(config, name, null);

    /// <summary>
    /// The real transports: a child process for a <c>command</c>, streamable HTTP (SSE fallback) for a <c>url</c>, with the sign-in's
    /// <paramref name="oauth"/> when given (2026-11-21, feature idea #32). <paramref name="handler"/> (a test's and the smoke's OAuth stub)
    /// goes under a client of its own; without it the client is a plain <see cref="HttpClient"/>, the very one the SDK would make itself.
    /// </summary>
    public static IClientTransport DefaultTransport(McpServerConfig config, string name, ClientOAuthOptions? oauth, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.IsHttp)
        {
            var options = new HttpClientTransportOptions
            {
                Endpoint = new Uri(config.Url!.Trim(), UriKind.Absolute),
                Name = name,
                AdditionalHeaders = config.Headers,
                OAuth = oauth,
            };
            // A passed handler is the caller's (2026-11-22, the review's #136): the smoke's shared stub outlives each transport, so the client never disposes it.
            return new HttpClientTransport(options, handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false), null, ownsHttpClient: true);
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

    /// <summary>
    /// The two files merged as they stand on disk. With <paramref name="seal"/> (the default: connect, reload) their plain tokens are
    /// encrypted first; without it (2026-11-20, the mcp-ask review's #84: a read that only looks, such as whether any server is set up)
    /// nothing is written or logged.
    /// </summary>
    public McpMerged Read(bool seal = true) => McpCatalog.Merge(McpConfigFile.Load(ProfilePath, seal), McpConfigFile.Load(GlobalPath, seal));

    /// <summary>Both files read from the disk at the next <see cref="Read"/> (2026-11-20, #17): a connect and a reload never trust the cache's stamp.</summary>
    private void ForgetCachedFiles()
    {
        Computers.ConfigCache.Invalidate(ProfilePath);
        Computers.ConfigCache.Invalidate(GlobalPath);
    }

    /// <summary>
    /// Drops every client, re-reads both files and starts every server that is on, in parallel; the
    /// master switch off leaves every row <see cref="McpState.Off"/>. <paramref name="phase"/> (may be
    /// null) follows the count. Never throws but for <paramref name="cancellationToken"/>'s own cancel,
    /// which marks every unfinished server <see cref="McpText.Cancelled"/> and is rethrown once after the wave.
    /// </summary>
    public async Task ConnectAllAsync(AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        CancelSignIn();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisposeAllAsync().ConfigureAwait(false);
            ForgetCachedFiles();
            var merged = Read();
            PruneTokens(merged);
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
    /// when its config didn't change, counted like a changed one: kept, its row would hide a dead client. Since 2026-11-20 (the mcp-ask
    /// review's #16, #115) "changed" means a change to how it connects (<see cref="McpSecrets.SameConnection"/>): a new <c>ask</c> or
    /// <c>instructions</c>, or a token typed again and sealed anew, updates the kept connection in place, and a session allow keyed to the
    /// old config asks again.
    /// </summary>
    public async Task<(int Added, int Removed, int Kept)> ReloadAsync(AppSettingsData effective, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        CancelSignIn();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ForgetCachedFiles();
            var merged = Read();
            PruneTokens(merged);
            var disabled = ToolsText.DisabledSet(effective.McpServersDisabled);
            var entries = merged.Entries.Where(e => e.Startable).ToDictionary(e => e.Name, StringComparer.Ordinal);   // a shadowed global shares its name with the profile's
            List<(string Name, Connection Live)> held;
            lock (_snapshot)
            {
                // Read under the snapshot lock (2026-11-19): the watcher and the background listing write the connections without the gate.
                held = _connections.Select(c => (c.Key, c.Value)).ToList();
            }

            // Judged outside the lock: SameConnection may decrypt.
            var stale = held
                .Where(c => !entries.TryGetValue(c.Name, out var entry) || c.Live.Dead || !McpSecrets.SameConnection(c.Live.Entry.Config, entry.Config))
                .Select(c => c.Name)
                .ToList();

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
                    // Only the startable entry keeps the connection (2026-11-20, the mcp-ask review's #1): a shadowed global of the same name
                    // falls through to its own Off row, not a second connected one.
                    if (entry.Startable && _connections.TryGetValue(entry.Name, out var live))
                    {
                        string fingerprint = entry.Config.Fingerprint();
                        if (fingerprint != live.Fingerprint)
                        {
                            live = live with { Entry = entry, Fingerprint = fingerprint };
                            _connections[entry.Name] = live;
                        }

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

            FlushLogs();
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
        Servers.Where(s => s.State == McpState.Failed).Select(s => s.NeedsSignIn
            ? (_retryTail == McpText.HeadlessRetryTail ? McpText.NeedsSignInHeadlessLine(s.Name) : McpText.NeedsSignInLine(s.Name))   // headless has no /mcp (2026-11-22, the review's #30)
            : McpText.FailedLine(s.Name, s.Detail ?? ""));

    private static McpServerStatus Initial(McpServerEntry entry, bool enabled, IReadOnlySet<string> disabled)
    {
        bool on = enabled && !disabled.Contains(entry.Name);
        var state = on && entry.Startable ? McpState.Connecting : McpState.Off;
        return new McpServerStatus(entry.Name, entry.Scope, entry.ShadowedBy, !disabled.Contains(entry.Name), state, null, [], entry.Config.Describe());
    }

    /// <summary>
    /// A connection made but not installed yet (2026-11-22, the review's #17): the client, its tools and facts, built without touching the
    /// session's state, so a sign-in that ends after a reload, a second sign-in or the dispose can be thrown away without a watcher ever
    /// having started on it.
    /// </summary>
    private sealed record Made(Connection Connection, bool SignIn, TimeSpan Elapsed);

    /// <summary>Whether a requested sign-in's browser step may still run (2026-11-22, the review's #36): only while its connect is being made.</summary>
    private sealed class SignInArm
    {
        private volatile bool _on = true;

        public bool On => _on;

        public void Off() => _on = false;
    }

    /// <summary>One server connected and installed: the normal start, a reload, a reconnect.</summary>
    private async Task<McpServerStatus> ConnectOneAsync(McpServerEntry entry, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var (row, made) = await MakeConnectionAsync(entry, timeoutSeconds, cancellationToken, signIn: false).ConfigureAwait(false);
        return made is null ? row : Install(made);
    }

    /// <summary>
    /// The connect itself: the client made, its tools listed, nothing stored. Null <c>Made</c> with the failed row (needs sign-in, timed
    /// out, cancelled, the server's error). A requested sign-in (<paramref name="signIn"/>) waits on the browser first: its budget is that wait
    /// plus <paramref name="timeoutSeconds"/>, cut back to the connect's alone once the browser step returns (#31); the connection keeps
    /// <paramref name="timeoutSeconds"/> for its reconnects (#3).
    /// </summary>
    private async Task<(McpServerStatus Row, Made? Made)> MakeConnectionAsync(McpServerEntry entry, int timeoutSeconds, CancellationToken cancellationToken, bool signIn)
    {
        string name = entry.Name;
        long started = 0;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        McpClient? client = null;
        McpServerConfig? connecting = null;
        bool signedIn = false;
        int budgetSeconds = signIn ? McpOAuth.SignInTimeoutSeconds + timeoutSeconds : timeoutSeconds;
        var arm = new SignInArm();
        try
        {
            // A stored value decrypted only here (2026-11-19, feature idea #29): the transport gets the plain copy, while the entry, the
            // fingerprint and the row keep the stored config. A value that can't be read here fails the server; nothing starts. Inside the
            // try since 2026-11-20 (the mcp-ask review's #98), so an unexpected throw is a failed row, and before the budget starts: a
            // Keychain prompt waits on the user, not on the server (DPAPI never prompts).
            var revealed = McpSecrets.Reveal(entry.Config);
            if (revealed.Error is { } unreadable)
            {
                DiagnosticLog.Info(Category, McpText.FailedLogLine(name, unreadable));
                return (new McpServerStatus(name, entry.Scope, null, true, McpState.Failed, unreadable, [], entry.Config.Describe()) { DetailIsApps = true }, null);
            }

            // ${NAME} filled from the child environment (2026-11-20, the mcp-ask review's #72), on a copy: the cached config keeps its text.
            var (config, unset) = McpReferences.Expand(revealed.Config!, Shell.ChildEnvironment.ForReference);
            foreach (var reference in unset)
            {
                LogOnce("unset\0" + name + "\0" + reference.Key + "\0" + reference.Name, McpText.UnsetReferenceLogLine(name, reference.Key, reference.Name), warn: true);
            }

            connecting = config;
            var oauth = OAuthFor(name, config, signIn, arm, () =>
            {
                try
                {
                    budget.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                }
                catch (ObjectDisposedException)
                {
                    // The connect is over.
                }
            });
            signedIn = oauth is not null && !signIn;
            started = _time.GetTimestamp();
            budget.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));
            var transport = _transport(config, name, oauth);
            var options = new McpClientOptions
            {
                ClientInfo = new Implementation { Name = ClientName, Version = AppVersion() },
                InitializationTimeout = TimeSpan.FromSeconds(budgetSeconds),
            };
            if (signIn)
            {
                // A sign-in's browser step runs inside the SDK's first request, its server/discover probe (2026-11-22, found by the review fixes'
                // late-callback test): the probe gives up at 5 s by default, and the initialize fallback then asked for a second authorization,
                // a second browser tab, the first tab's callback refused as a stray. So a sign-in's probe has the connect's whole budget.
                options.DiscoverProbeTimeout = Timeout.InfiniteTimeSpan;
            }

            client = await McpClient.CreateAsync(transport, options, null, budget.Token).ConfigureAwait(false);
            // The browser step only while the connect is made (#36): a refresh that fails later reads needs sign-in, never a browser on its own.
            arm.Off();
            // The server's notes (2026-11-19, feature idea #59) only through the SDK's property, so a later spec that moves them keeps working.
            var notes = McpNotes.Cut(name, client.ServerInstructions, entry.Config.InstructionsOn ? (key, line) => LogOnce(key, line) : null);
            var listed = await client.ListToolsAsync((ModelContextProtocol.RequestOptions?)null, budget.Token).ConfigureAwait(false);
            var tools = BuildTools(name, client, listed);
            var elapsed = _time.GetElapsedTime(started);
            var connection = new Connection(client, entry.Config.Fingerprint(), tools, entry, timeoutSeconds, new Listing(), notes)
            {
                // Carried by the connection (2026-11-22, the review's #91), so a reload's row keeps the footer's "signed in".
                SignedIn = signedIn || HoldsSignIn(name, config),
            };
            client = null;   // the connection's now: the catch below never disposes it
            return (new McpServerStatus(name, entry.Scope, null, true, McpState.Connected, null, tools, entry.Config.Describe()), new Made(connection, signIn, elapsed));
        }
        catch (Exception ex)
        {
            arm.Off();
            bool cancelled = ex is OperationCanceledException;
            if (client is not null)
            {
                await DisposeQuietlyAsync(client).ConfigureAwait(false);
            }

            // A server that wants a sign-in (2026-11-21, feature idea #32): a 401 with no sign-in attached, or the SDK falling through to a new
            // authorization nobody asked for. Never "failed" at every start; nothing registered with the service.
            if (!signIn && !cancelled && connecting is { SignInPossible: true } && (McpOAuth.IsUnauthorized(ex) || McpOAuth.IsSignInNeeded(ex)))
            {
                DiagnosticLog.Info(Category, McpText.NeedsSignInLogLine(name));
                FlushLogs();
                return (NeedsSignInRow(entry), null);
            }

            string detail = cancelled
                ? (cancellationToken.IsCancellationRequested ? McpText.Cancelled : McpText.TimedOut(budgetSeconds))
                : LogText.Excerpt(ex.Message);
            if (signIn)
            {
                DiagnosticLog.Info(Category, cancellationToken.IsCancellationRequested ? McpText.SignInCancelledLogLine(name) : McpText.SignInFailedLogLine(name, detail));
                FlushLogs();
                // A failed or cancelled sign-in leaves the server needing one: the row says so, the notice says why. The detail is the app's
                // only for the app's own failure (2026-11-22, the review's #137); an SDK exception's message may carry the server's text.
                return (new McpServerStatus(name, entry.Scope, null, true, McpState.Failed, cancellationToken.IsCancellationRequested ? McpText.SignInCancelledDetail : detail, [], entry.Config.Describe())
                {
                    DetailIsApps = cancelled || McpOAuth.IsSignInFailed(ex),
                    NeedsSignIn = connecting is { SignInPossible: true },
                }, null);
            }

            DiagnosticLog.Info(Category, McpText.FailedLogLine(name, detail));
            FlushLogs();
            return (new McpServerStatus(name, entry.Scope, null, true, McpState.Failed, detail, [], entry.Config.Describe()) { DetailIsApps = cancelled }, null);
        }
    }

    /// <summary>
    /// A made connection put in place: stored, then its watcher and its tool-list handler (the watcher only after the store, 2026-11-19: a
    /// client that died since <c>CreateAsync</c> finds itself current, and its finished <c>Completion</c> runs the watcher at once, so the row
    /// reads dropped rather than plain connected), its log lines, and its connected row with the connection's facts.
    /// </summary>
    private McpServerStatus Install(Made made)
    {
        var connection = made.Connection;
        var entry = connection.Entry;
        string name = entry.Name;
        var connected = connection.Client;
        lock (_snapshot)
        {
            _connections[name] = connection;
        }

        _ = WatchAsync(name, connected);
        _ = connected.RegisterNotificationHandler(NotificationMethods.ToolListChangedNotification, (_, _) =>
        {
            ToolsChanged(name, connected);
            return ValueTask.CompletedTask;
        });
        DiagnosticLog.Info(Category, McpText.ConnectedLogLine(name, connection.Tools.Count, made.Elapsed));
        if (made.SignIn && connection.SignedIn)
        {
            DiagnosticLog.Info(Category, McpText.SignedInLogLine(name));
        }

        FlushLogs();
        // With the connection's facts (2026-11-20, the mcp-ask review's #68): a caller that publishes this row as it is shows them too.
        return ConnectedFacts(new McpServerStatus(name, entry.Scope, null, true, McpState.Connected, null, connection.Tools, entry.Config.Describe()), connection);
    }

    /// <summary>
    /// The one needs-sign-in row (2026-11-22, the review's #36): a start's 401, a call's refresh that failed mid-session, a sign-out's. Its
    /// detail is the app's own.
    /// </summary>
    private static McpServerStatus NeedsSignInRow(McpServerEntry entry) =>
        new(entry.Name, entry.Scope, null, true, McpState.Failed, McpText.NeedsSignInDetail, [], entry.Config.Describe())
        {
            DetailIsApps = true,
            NeedsSignIn = true,
        };

    /// <summary>A server's listed tools as the model sees them: prefixed, de-duplicated, each calling through the session. One helper for a connect and a changed list, so a tool keeps its name.</summary>
    private List<McpTool> BuildTools(string name, McpClient client, IList<McpClientTool> listed)
    {
        var names = McpToolName.Unique(listed.Select(t => McpToolName.Prefixed(name, t.Name)).ToList());
        return listed.Select((t, i) => new McpTool(name, client, t, names[i], CallAsync, RenderContextFor)).ToList();
    }

    private static string AppVersion()
    {
        var v = typeof(McpSession).Assembly.GetName().Version;
        return v is null ? "0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    // ── Sign-in (2026-11-21, feature idea #32) ─────────────────────────────

    private McpTokenStore? _tokens;
    private readonly object _tokensGate = new();
    private CancellationTokenSource? _signIn;
    private string? _signingIn;

    /// <summary>Opens an address in the user's browser (the screen's shell open); null prints it alone (headless never signs in).</summary>
    public Action<string>? OpenBrowser { get; set; }

    /// <summary>Where a requested sign-in's address line goes (the <c>/mcp</c> pane's); null = the session's notice queue.</summary>
    public Action<string, bool>? SignInNotice { get; set; }

    /// <summary>Queues a notice for the host to print (<see cref="TakeNotices"/>), as the session's own are.</summary>
    public void QueueNotice(string text, bool error) => Notice(text, error);

    /// <summary>Raised with a server's name when a call or a read meets a server that needs a sign-in: headless counts it as a failure.</summary>
    public Action<string>? SignInNeededCall { get; set; }

    /// <summary>The store over a profile's token file; a test's store with a failing seal. Null = the real one.</summary>
    public Func<string, McpTokenStore>? TokenStoreFor { get; set; }

    /// <summary>The profile's sign-ins (<c>&lt;profile&gt;/configs/mcp-tokens.json</c>), built again when the profile changes.</summary>
    /// <remarks>
    /// Built under a lock (2026-11-22, the review's #58), so two threads never build two stores over one file. On a profile switch the old
    /// store's pending sign-ins are written to its own file (#6), after the lock is let go, so a lookup never waits on that write.
    /// </remarks>
    public McpTokenStore Tokens
    {
        get
        {
            string path = McpTokenStore.PathFor(_settings.ProfileDirectory);
            McpTokenStore store;
            McpTokenStore? left = null;
            lock (_tokensGate)
            {
                var current = _tokens;
                if (current is not null && string.Equals(current.FilePath, path, StringComparison.Ordinal))
                {
                    return current;
                }

                left = current;
                store = TokenStoreFor?.Invoke(path) ?? new McpTokenStore(path);
                _tokens = store;
            }

            left?.Flush();
            return store;
        }
    }

    /// <summary>The server a requested sign-in is waiting on, or null.</summary>
    public string? SigningIn { get { lock (_snapshot) { return _signingIn; } } }

    /// <summary>The address a server's tokens are kept under: its <c>url</c> once <c>${NAME}</c> is filled in.</summary>
    private static string SignInUrl(McpServerConfig config) =>
        McpReferences.Expand(config, Shell.ChildEnvironment.ForReference).Config.Url?.Trim() ?? "";

    /// <summary>Whether a sign-in can serve server <paramref name="name"/> as configured now: a <c>url</c> server without its own Authorization header.</summary>
    public bool CanSignIn(string name) => Entry(name)?.Config.SignInPossible == true;

    /// <summary>Whether server <paramref name="name"/> holds a sign-in for its address (whatever its port): what <c>s</c> signs out of.</summary>
    public bool HoldsSignIn(string name) => Entry(name) is { Config.SignInPossible: true } entry && Tokens.HasAny(name, SignInUrl(entry.Config));

    private bool HoldsSignIn(string name, McpServerConfig expanded) =>
        expanded.SignInPossible && Tokens.Has(name, expanded.Url!.Trim(), expanded.RedirectPort);

    private McpServerEntry? Entry(string name) => Read(seal: false).Entries.FirstOrDefault(e => e.Startable && e.Name == name);

    /// <summary>
    /// The sign-in's options for <paramref name="name"/> (2026-11-21), or null for none: attached only when the store holds tokens for this
    /// address and port, or for a requested sign-in. Otherwise a server not signed in yet would register a fresh client with the service at
    /// every start and reload, the SDK registering before it asks for the browser. The kept client id and secret go back in, so a refresh
    /// or a sign-in again reuses the registered app; the entry's own <c>oauth</c> wins over them.
    /// </summary>
    private ClientOAuthOptions? OAuthFor(string name, McpServerConfig config, bool signIn, SignInArm arm, Action browserDone)
    {
        if (!config.SignInPossible)
        {
            return null;
        }

        var store = Tokens;
        string url = config.Url!.Trim();
        int port = config.RedirectPort;
        var kept = store.Get(name, url, port);
        if (kept is null && !signIn)
        {
            return null;
        }

        var own = config.OAuth;
        return new ClientOAuthOptions
        {
            RedirectUri = McpOAuth.RedirectUri(port),
            ClientId = own?.ClientId ?? kept?.ClientId,
            ClientSecret = own?.ClientId is not null ? own.ClientSecret : kept?.ClientSecret,
            Scopes = own?.Scopes,
            DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = McpOAuth.ClientName },
            TokenCache = store.CacheFor(name, url, port),
            // Always set (the SDK's default reads the console, which the TUI owns). Unasked, a fall-through to a new authorization is a
            // needs-sign-in row, never a browser opening on its own.
            // A requested sign-in's browser step only while its connect is made (2026-11-22, the review's #36): the transport keeps these options,
            // and a refresh that fails mid-session must read needs sign-in, never open the browser on its own. Once the browser step returns,
            // the connect's own timeout takes over the budget (#31).
            AuthorizationCallbackHandler = signIn
                ? async (context, token) =>
                {
                    if (!arm.On)
                    {
                        throw new McpSignInNeeded();
                    }

                    var result = await McpSignIn.WaitAsync(name, context.AuthorizationUri!, context.RedirectUri!, OpenBrowser, SignInNotice ?? Notice, token).ConfigureAwait(false);
                    browserDone();
                    return result;
                }
                : (_, _) => Task.FromException<AuthorizationResult?>(new McpSignInNeeded()),
        };
    }

    /// <summary>
    /// Removes the sign-ins of servers no longer in the catalog (a start and a reload): never while either file has a problem with no name
    /// (a broken file lists no servers, and a typo mustn't sign out every server; a blank name can't be matched either), and a server whose
    /// entry has a problem of its own keeps its sign-in until the entry is fixed or removed.
    /// </summary>
    private void PruneTokens(McpMerged merged)
    {
        if (merged.Problems.Any(p => p.Name is null))
        {
            return;
        }

        try
        {
            var store = Tokens;
            if (!File.Exists(store.FilePath))
            {
                return;
            }

            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in merged.Entries.Where(e => e.Config.IsHttp))
            {
                keep.Add(McpTokenStore.Key(entry.Name, SignInUrl(entry.Config)));
            }

            // A url that expands ${NAME} (2026-11-22, the review's #5) may read otherwise in another environment (a headless run's, a
            // terminal's): its server's sign-ins are kept by name, never pruned for an address this run happens not to produce.
            var keepServers = merged.Entries.Where(e => e.Config.IsHttp && (e.Config.Url ?? "").Contains("${", StringComparison.Ordinal))
                .Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            var broken = merged.Problems.Select(p => p.Name!).ToHashSet(StringComparer.Ordinal);
            store.Prune(keep, keepServers, broken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Debug(Category, LogText.Excerpt(ex.Message));
        }
    }

    /// <summary>Calls off a requested sign-in (a reload, the wave, the dispose, a second sign-in's Esc); its listener stops with it.</summary>
    public void CancelSignIn()
    {
        CancellationTokenSource? waiting;
        lock (_snapshot)
        {
            waiting = _signIn;
        }

        try
        {
            waiting?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ended meanwhile.
        }
    }

    /// <summary>
    /// A requested sign-in to server <paramref name="name"/> (2026-11-21, <c>s</c> or Enter on its needs-sign-in row): its connection started
    /// again with the sign-in's options and a browser wait of <see cref="McpOAuth.SignInTimeoutSeconds"/>, the row reading signing in
    /// meanwhile. One at a time: null when another waits, the server isn't startable, or the master switch is off. A cancel (Esc, a reload)
    /// is a needs-sign-in row with the cancelled detail, never a throw.
    /// </summary>
    public async Task<McpServerStatus?> SignInAsync(string name, AppSettingsData effective, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        using var mine = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        lock (_snapshot)
        {
            if (_signIn is not null || _disposed)
            {
                return null;
            }

            _signIn = mine;
            _signingIn = name;
        }

        try
        {
            // The session's lock only to read the entry and show the row, then again to swap (2026-11-22, the review's #17): never across the
            // browser wait, so a flip, a reload or another server's connect goes on meanwhile.
            McpServerEntry entry;
            IReadOnlyList<McpConfigProblem> problems;
            try
            {
                await _gate.WaitAsync(mine.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            try
            {
                var merged = Read();
                var disabled = ToolsText.DisabledSet(effective.McpServersDisabled);
                // A server switched off since the pane offered the sign-in (2026-11-22, the review's #48) isn't started by it.
                if (merged.Entries.FirstOrDefault(e => e.Name == name && e.Startable) is not { } found || !effective.McpServers || disabled.Contains(name))
                {
                    return null;
                }

                entry = found;
                problems = merged.Problems;
                PublishRow(Initial(entry, effective.McpServers, disabled) with { State = McpState.Connecting, SigningIn = true }, problems, attempted: true);
            }
            finally
            {
                _gate.Release();
            }

            var (row, made) = await MakeConnectionAsync(entry, effective.McpConnectTimeoutSeconds, mine.Token, signIn: true).ConfigureAwait(false);
            McpClient? loser = null;
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                bool still;
                lock (_snapshot)
                {
                    still = ReferenceEquals(_signIn, mine) && !_disposed;
                }

                bool same = Entry(name) is { } now && string.Equals(now.Config.Fingerprint(), entry.Config.Fingerprint(), StringComparison.Ordinal);
                if (made is not null)
                {
                    if (still && same)
                    {
                        await DisposeOneAsync(name).ConfigureAwait(false);
                        var installed = Install(made);
                        PublishRow(installed, problems, attempted: true);
                        return installed;
                    }

                    // Lost the swap (a reload, the dispose, a changed entry): its tokens stay stored for the next connect; the row is whatever
                    // won.
                    loser = made.Connection.Client;
                    return Servers.FirstOrDefault(r => r.Name == name);
                }

                // A failed or cancelled sign-in leaves a live connection as it was (2026-11-22, the review's #83).
                Connection? live;
                lock (_snapshot)
                {
                    live = _connections.GetValueOrDefault(name);
                }

                if (live is { Dead: false })
                {
                    if (still)
                    {
                        PublishRow(ConnectedFacts(new McpServerStatus(name, entry.Scope, null, true, McpState.Connected, null, live.Tools, entry.Config.Describe()) { Dropped = live.Dropped }, live), problems, attempted: true);
                    }

                    return row;
                }

                if (still && same)
                {
                    PublishRow(row, problems, attempted: true);
                }

                return row;
            }
            finally
            {
                _gate.Release();
                if (loser is not null)
                {
                    await DisposeQuietlyAsync(loser).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            EndSignIn(mine);
        }
    }

    private void EndSignIn(CancellationTokenSource mine)
    {
        lock (_snapshot)
        {
            if (ReferenceEquals(_signIn, mine))
            {
                _signIn = null;
                _signingIn = null;
            }
        }
    }

    /// <summary>
    /// Signs out of server <paramref name="name"/>: its tokens forgotten on this computer (no revocation is sent: not every service offers
    /// one), its client dropped, its row needs sign-in with no reconnect attempt. <see cref="SignOutResult.NotSignedIn"/> when it held no
    /// sign-in; <see cref="SignOutResult.Busy"/> when the token file couldn't be written (2026-11-22, the review's #7): the sign-in is still
    /// kept, the client gone, and the row says so, so <c>s</c> tries again.
    /// </summary>
    public async Task<SignOutResult> SignOutAsync(string name)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Entry(name) is not { Config.SignInPossible: true } entry)
            {
                return SignOutResult.NotSignedIn;
            }

            string url = SignInUrl(entry.Config);
            if (!Tokens.HasAny(name, url))
            {
                return SignOutResult.NotSignedIn;
            }

            // The client first: one still live could refresh and store its tokens again after the forget.
            await DisposeOneAsync(name).ConfigureAwait(false);
            var forgot = Tokens.Forget(name, url);
            var row = Servers.FirstOrDefault(r => r.Name == name);
            if (forgot == SignOutResult.Busy)
            {
                if (row is not null)
                {
                    PublishRow(row with { State = McpState.Failed, Detail = McpText.SignOutBusy, DetailIsApps = true, NeedsSignIn = false, Tools = [], Dropped = null });
                }

                FlushLogs();
                return SignOutResult.Busy;
            }

            if (row is not null)
            {
                PublishRow(row with { State = McpState.Failed, Detail = McpText.NeedsSignInDetail, DetailIsApps = true, NeedsSignIn = true, SignedIn = false, Tools = [], Dropped = null });
            }

            DiagnosticLog.Info(Category, McpText.SignedOutLogLine(name));
            FlushLogs();
            return forgot;
        }
        finally
        {
            _gate.Release();
        }
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

        FlushLogs();
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
    /// <para>A call that asks, or one that may change things and doesn't (<c>"ask": "never"</c>), is audited with one line once it's over
    /// (2026-11-20, the mcp-ask review's #121 and #15): the decision and what came of it, <c>ran</c>, <c>error</c>, <c>not sent</c>,
    /// <c>dropped, may have changed something</c>, <c>canceled</c> or <c>failed: …</c>, written in a <c>finally</c> so a throw is audited too.</para>
    /// </summary>
    private async ValueTask<McpCallOutcome> CallAsync(McpTool tool, CallToolRequestParams request, CancellationToken cancellationToken)
    {
        string name = tool.ServerName;
        var ready = await ResolveAsync(name, tool, null, cancellationToken).ConfigureAwait(false);
        if (ready.Refusal is { } refused)
        {
            return refused;
        }

        // The reconnected notice waits for the call (2026-11-19): a server that drops on it again gives the one "didn't come back" notice.
        string? reconnected = ready.Reconnected;
        var live = ready.Live!;
        var sending = ready.Tool!;

        // The ask (2026-11-19, feature idea #28): judged on the tool as the current list describes it, right before the send.
        bool asks = McpAskRule.Asks(live.Entry.Config.AskMode, sending);
        if (!asks && sending.ReadOnly)
        {
            return (await SendAndRecoverAsync(name, tool, live, sending, request, reconnected, asked: false, cancellationToken).ConfigureAwait(false)).Outcome;
        }

        // Only the audited calls copy the arguments and write their JSON, once (2026-11-20, the mcp-ask review's #10).
        var arguments = (IReadOnlyDictionary<string, System.Text.Json.JsonElement>?)request.Arguments?.ToDictionary(StringComparer.Ordinal)
            ?? new Dictionary<string, System.Text.Json.JsonElement>();
        string json = McpText.ArgumentsJson(arguments, indented: false, redact: true);
        string audited = sending.Name;
        string decision = asks ? McpText.DeniedWord : McpText.RanUnasked;
        string outcome = McpText.OutcomeNotSent;
        bool decided = !asks;
        try
        {
            if (asks)
            {
                var ask = new McpAsk(name, sending.ServerToolName, sending.Name, arguments, McpAskRule.Note(sending))
                {
                    Fingerprint = live.Fingerprint,
                    Definition = sending.DefinitionKey,
                };
                var verdict = Approver is { } approver ? await approver(ask, cancellationToken).ConfigureAwait(false) : McpVerdict.Nobody;
                decided = true;
                switch (verdict)
                {
                    case McpVerdict.Allowed(var how):
                        decision = how;
                        break;
                    case McpVerdict.NotAsked(var tail):
                        decision = McpText.NotAskedWord;
                        Reconnected(name, reconnected);
                        return McpCallOutcome.Failed(McpText.NotAskedError(sending.Name, tail));
                    default:
                        Reconnected(name, reconnected);
                        return McpCallOutcome.Failed(McpText.DeniedError(sending.Name));
                }

                // The pane may have waited: the server can have been switched off, removed, dropped or changed meanwhile. A dropped one
                // reconnects first (nothing was sent, so that's safe for any tool) and the call goes out without asking again: the user said
                // yes to this call. One whose config changed is refused (2026-11-20, #4): the yes was to the server as it was.
                var after = await ResolveAsync(name, tool, live.Fingerprint, cancellationToken).ConfigureAwait(false);
                if (after.Refusal is { } refusedAfter)
                {
                    if (!after.NotBack)
                    {
                        Reconnected(name, reconnected);
                    }

                    return refusedAfter;
                }

                reconnected = after.Reconnected ?? reconnected;
                live = after.Live!;
                sending = after.Tool!;
            }

            var sent = await SendAndRecoverAsync(name, tool, live, sending, request, reconnected, asked: asks, cancellationToken).ConfigureAwait(false);
            outcome = sent.Word;
            return sent.Outcome;
        }
        catch (OperationCanceledException)
        {
            outcome = McpText.OutcomeCanceled;
            if (!decided)
            {
                // A stop on the pane (2026-11-20, #23) is a no, and the reconnect the call waited on is still said.
                decision = McpText.DeniedWord;
                Reconnected(name, reconnected);
            }

            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            outcome = McpText.OutcomeFailed(ex.GetType().Name);
            throw;
        }
        finally
        {
            DiagnosticLog.Info(Category, McpText.AuditLogLine(audited, decision, outcome, json));
        }
    }

    // ── Resources (2026-11-21, feature idea #33: mcp_read) ──────────────────

    /// <summary>
    /// The connected servers that declare resources (<c>ServerCapabilities.Resources</c>), in the catalog's order: what <c>mcp_read</c> is
    /// offered for. A dropped one stays listed, since a read reconnects it as a call does.
    /// </summary>
    public IReadOnlyList<string> ResourceServers
    {
        get
        {
            lock (_snapshot)
            {
                return _servers
                    .Where(s => s.State == McpState.Connected && _connections.TryGetValue(s.Name, out var live) && live.Client.ServerCapabilities?.Resources is not null)
                    .Select(s => s.Name)
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Whether the host offers <c>mcp_read</c> at all (2026-11-21): <c>MCP servers enabled</c> on and the tool not switched off. Set by the
    /// screen and headless; null reads as not offered, so a result's links never point to a tool the model doesn't have.
    /// </summary>
    public Func<bool>? ReadToolOffered { get; set; }

    /// <summary>Whether a result from <paramref name="server"/> may point to <c>mcp_read</c> (<see cref="McpRenderContext.ReadOffered"/>).</summary>
    private bool ReadOffered(string server) => ReadToolOffered?.Invoke() == true && ResourceServers.Contains(server, StringComparer.Ordinal);

    /// <summary>The conversation's MCP picture numbers (2026-11-22, the review's #60): reset with the allows, seeded past a resumed history's.</summary>
    private readonly McpPictureCounter _pictures = new();

    /// <summary>
    /// Whether the model is shown pictures (2026-11-22, the review's #131): set by the screen and headless from <c>LlmSession.SeesPictures</c>;
    /// null reads as seen.
    /// </summary>
    public Func<bool>? PicturesSeen { get; set; }

    /// <summary>A new conversation's picture numbers, from 1; or, for a restored one, past the highest its history's MCP pictures hold.</summary>
    public void ResetPictures(IEnumerable<Microsoft.Extensions.AI.ChatMessage>? restored = null) =>
        _pictures.Reset(restored is null ? 0 : McpPictureCounter.Highest(restored.SelectMany(m => m.Contents).Select(Llm.ConversationHistory.McpLabel).OfType<string>()));

    /// <summary>A tool's result context: its name labels its pictures, numbered by the conversation (#60), and whether they're seen (#131).</summary>
    private McpRenderContext RenderContextFor(McpTool tool) =>
        new(tool.Name, ReadOffered(tool.ServerName)) { Pictures = _pictures, PicturesSeen = PicturesSeen?.Invoke() ?? true };

    /// <inheritdoc />
    public McpRenderContext ReadContext() =>
        McpRenderContext.Plain(McpReadToolName) with { Pictures = _pictures, PicturesSeen = PicturesSeen?.Invoke() ?? true };

    /// <summary>What a turn offers of MCP: every connected server's tools, then <c>mcp_read</c> while a connected server declares resources.</summary>
    public IReadOnlyList<AIFunction> TurnTools()
    {
        var read = ReadTools();
        return read.Count == 0 ? Tools : [.. Tools, .. read];
    }

    /// <summary><c>mcp_read</c> for the servers that declare resources now, or nothing while none does: the turn's and <c>/usage</c>' one tool.</summary>
    public IReadOnlyList<AIFunction> ReadTools()
    {
        var servers = ResourceServers;
        return servers.Count == 0 ? [] : [new Mcp.McpReadTool(this, servers)];
    }

    /// <summary>
    /// The server's resources and resource templates, every page up to <paramref name="max"/> of each (the tool shows that many), with
    /// whether more were left. A refusal is the call's text.
    /// </summary>
    public async Task<McpResourceAnswer> ListResourcesAsync(string server, int max, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        var arguments = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        var (listing, refusal) = await ResourceAsync(server, McpText.ResourcesListMethod, arguments, async (client, token) =>
        {
            var (resources, more) = await PagesAsync<Resource>(async cursor =>
            {
                var page = await client.ListResourcesAsync(new ListResourcesRequestParams { Cursor = cursor }, token).ConfigureAwait(false);
                return (page.Resources, page.NextCursor);
            }, max).ConfigureAwait(false);
            IReadOnlyList<ResourceTemplate> templates = [];
            try
            {
                (var listed, bool moreTemplates) = await PagesAsync<ResourceTemplate>(async cursor =>
                {
                    var page = await client.ListResourceTemplatesAsync(new ListResourceTemplatesRequestParams { Cursor = cursor }, token).ConfigureAwait(false);
                    return (page.ResourceTemplates, page.NextCursor);
                }, max).ConfigureAwait(false);
                templates = listed;
                more |= moreTemplates;
            }
            catch (ModelContextProtocol.McpProtocolException ex)
            {
                // Templates are optional: a server with resources may not answer resources/templates/list at all (MethodNotFound), and one
                // that answers it with an error (2026-11-22, the review's #57) keeps its resources listed; the error goes to the log.
                if (ex.ErrorCode != ModelContextProtocol.McpErrorCode.MethodNotFound)
                {
                    DiagnosticLog.Info(Category, McpText.TemplatesFailedLogLine(server, LogText.Excerpt(ex.Message)));
                }
            }

            return new McpResourceList(resources.Take(max).ToList(), templates.Take(max).ToList(), more);
        }, cancellationToken).ConfigureAwait(false);
        return new McpResourceAnswer(listing, null, refusal);
    }

    /// <summary>The most pages one list reads (2026-11-22, the review's #8).</summary>
    internal const int MaxListPages = 10;

    /// <summary>
    /// A paged list read up to <paramref name="max"/> items (2026-11-22, the review's #8: a server that repeated a cursor, or sent empty pages
    /// with one, kept the loop going for ever): it stops at <see cref="MaxListPages"/> pages, a cursor seen before, or an empty page that
    /// still has one. <c>More</c> when it stopped with a cursor left, or past <paramref name="max"/>.
    /// </summary>
    internal static async Task<(List<T> Items, bool More)> PagesAsync<T>(Func<string?, Task<(IList<T>? Items, string? Next)>> page, int max)
    {
        var items = new List<T>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (int pages = 1; ; pages++)
        {
            var (got, next) = await page(cursor).ConfigureAwait(false);
            items.AddRange(got ?? []);
            if (next is null)
            {
                return (items, items.Count > max);
            }

            if (items.Count >= max || pages >= MaxListPages || got is not { Count: > 0 } || !seen.Add(next))
            {
                return (items, true);
            }

            cursor = next;
        }
    }

    /// <summary>The resource at <paramref name="uri"/> as the server reads it. A refusal is the call's text.</summary>
    public async Task<McpResourceAnswer> ReadResourceAsync(string server, string uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(uri);
        var arguments = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["uri"] = McpText.JsonString(uri),
        };
        var (read, refusal) = await ResourceAsync(server, McpText.ResourcesReadMethod, arguments,
            (client, token) => client.ReadResourceAsync(new ReadResourceRequestParams { Uri = uri }, token).AsTask(), cancellationToken).ConfigureAwait(false);
        return new McpResourceAnswer(null, read, refusal);
    }

    /// <summary>
    /// One resource request, the twin of <see cref="CallAsync"/> for a read: the server resolved (<see cref="ResolveResourcesAsync"/>), asked
    /// under <c>"ask": "always"</c> alone (<see cref="McpAskRule.AsksRead"/>, the <see cref="McpAsk"/>'s server tool the protocol's
    /// <paramref name="method"/> and its name <c>mcp_read</c>, so a session allow covers the server's reads until its config changes), sent,
    /// and sent once more after a drop, since a read changes nothing: when it was asked already or the server as it came back wouldn't
    /// ask, and the server still offers resources. An asked request is audited as a call is. A server's own error is thrown, as a call's.
    /// </summary>
    private async Task<(T? Value, McpCallOutcome? Refusal)> ResourceAsync<T>(string name, string method, IReadOnlyDictionary<string, System.Text.Json.JsonElement> arguments, Func<McpClient, CancellationToken, Task<T>> send, CancellationToken cancellationToken)
        where T : class
    {
        var ready = await ResolveResourcesAsync(name, method, null, cancellationToken).ConfigureAwait(false);
        if (ready.Refusal is { } refused)
        {
            return (null, refused);
        }

        string? reconnected = ready.Reconnected;
        var live = ready.Live!;
        bool asks = McpAskRule.AsksRead(live.Entry.Config.AskMode);
        if (!asks)
        {
            return await SendResourceAndRecoverAsync(name, method, live, send, reconnected, asked: false, cancellationToken).ConfigureAwait(false);
        }

        string json = McpText.ArgumentsJson(arguments, indented: false, redact: true);
        string decision = McpText.DeniedWord;
        string outcome = McpText.OutcomeNotSent;
        bool decided = false;
        try
        {
            var ask = new McpAsk(name, method, McpReadToolName, arguments, McpChangeNote.None)
            {
                Fingerprint = live.Fingerprint,
                Definition = McpReadToolName,
                AllowTool = McpReadToolName,
            };
            var verdict = Approver is { } approver ? await approver(ask, cancellationToken).ConfigureAwait(false) : McpVerdict.Nobody;
            decided = true;
            switch (verdict)
            {
                case McpVerdict.Allowed(var how):
                    decision = how;
                    break;
                case McpVerdict.NotAsked(var tail):
                    decision = McpText.NotAskedWord;
                    Reconnected(name, reconnected);
                    return (null, McpCallOutcome.Failed(McpText.NotAskedError(McpReadToolName, tail)));
                default:
                    Reconnected(name, reconnected);
                    return (null, McpCallOutcome.Failed(McpText.DeniedError(McpReadToolName)));
            }

            var after = await ResolveResourcesAsync(name, method, live.Fingerprint, cancellationToken).ConfigureAwait(false);
            if (after.Refusal is { } refusedAfter)
            {
                if (!after.NotBack)
                {
                    Reconnected(name, reconnected);
                }

                return (null, refusedAfter);
            }

            reconnected = after.Reconnected ?? reconnected;
            var sent = await SendResourceAndRecoverAsync(name, method, after.Live!, send, reconnected, asked: true, cancellationToken).ConfigureAwait(false);
            outcome = sent.Refusal is null ? McpText.OutcomeRan : McpText.OutcomeDropped;
            return sent;
        }
        catch (OperationCanceledException)
        {
            outcome = McpText.OutcomeCanceled;
            if (!decided)
            {
                Reconnected(name, reconnected);
            }

            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            outcome = McpText.OutcomeFailed(ex.GetType().Name);
            throw;
        }
        finally
        {
            DiagnosticLog.Info(Category, McpText.AuditLogLine(McpReadToolName + " " + name + " " + method, decision, outcome, json));
        }
    }

    /// <summary><c>mcp_read</c>'s name, as the session's asks and audit lines give it (the tool's own constant, kept here so the session needn't build it).</summary>
    private const string McpReadToolName = Mcp.McpReadTool.ToolName;

    /// <summary>
    /// <see cref="ResolveAsync"/>'s sibling for a resource request: <see cref="Usable"/>, a reconnect of a dead one, the fingerprint check under
    /// <paramref name="asked"/>, then that the live server still declares resources, in place of <see cref="Offered"/>.
    /// </summary>
    private async Task<Ready> ResolveResourcesAsync(string name, string method, string? asked, CancellationToken cancellationToken)
    {
        var ready = Usable(name, method);
        if (ready.Refusal is null && (ready.Live is null || ready.Live.Dead))
        {
            ready = await ReconnectForCallAsync(name, method, ready.Live?.Client, null, cancellationToken).ConfigureAwait(false);
        }

        if (ready.Refusal is not null)
        {
            return ready;
        }

        var live = ready.Live!;
        if (asked is not null && !string.Equals(live.Fingerprint, asked, StringComparison.Ordinal))
        {
            Reconnected(name, ready.Reconnected);
            return new Ready(null, McpCallOutcome.Failed(McpText.ChangedWhileAsking(name)), null);
        }

        if (live.Client.ServerCapabilities?.Resources is null)
        {
            Reconnected(name, ready.Reconnected);
            return new Ready(null, McpCallOutcome.Failed(McpText.NoResourcesError(name, ResourceServers)), null);
        }

        return ready;
    }

    /// <summary><see cref="SendAndRecoverAsync"/>'s twin for a read: a drop reconnects once and sends again, as for a read-only tool.</summary>
    private async Task<(T? Value, McpCallOutcome? Refusal)> SendResourceAndRecoverAsync<T>(string name, string method, Connection live, Func<McpClient, CancellationToken, Task<T>> send, string? reconnected, bool asked, CancellationToken cancellationToken)
        where T : class
    {
        var first = await SendAsync(live, send, cancellationToken).ConfigureAwait(false);
        if (first.Answered)
        {
            Reconnected(name, reconnected);
            return (first.Value, null);
        }

        if (first.SignInNeeded)
        {
            return (null, await SignInLostAsync(name, live).ConfigureAwait(false));
        }

        if (reconnected is not null)
        {
            return (null, await DroppedAgainAsync(name, live, first.Details).ConfigureAwait(false));
        }

        var again = await ReconnectForCallAsync(name, method, live.Client, first.Details, cancellationToken).ConfigureAwait(false);
        if (again.Refusal is { } gone)
        {
            return (null, gone);
        }

        var back = again.Live!;
        if (back.Client.ServerCapabilities?.Resources is null)
        {
            Reconnected(name, again.Reconnected);
            return (null, McpCallOutcome.Failed(McpText.NoResourcesError(name, ResourceServers)));
        }

        if (!asked && McpAskRule.AsksRead(back.Entry.Config.AskMode))
        {
            Reconnected(name, again.Reconnected);
            return (null, McpCallOutcome.Failed(McpText.DroppedNotResentError(name)));
        }

        var second = await SendAsync(back, send, cancellationToken).ConfigureAwait(false);
        if (second.Answered)
        {
            Reconnected(name, again.Reconnected);
            return (second.Value, null);
        }

        if (second.SignInNeeded)
        {
            return (null, await SignInLostAsync(name, back).ConfigureAwait(false));
        }

        return (null, await DroppedAgainAsync(name, back, second.Details).ConfigureAwait(false));
    }

    /// <summary>
    /// The server's connection and the tool as its current list has it (2026-11-20, the mcp-ask review's #40: the one path before the ask
    /// and after it): <see cref="Usable"/>, a reconnect of a dead one, then <see cref="Offered"/>. With <paramref name="asked"/> (the
    /// fingerprint the pane asked about) a connection whose config changed meanwhile is refused (#4). A refusal after a reconnect says that
    /// reconnect itself; a success leaves it to the call.
    /// </summary>
    private async Task<Ready> ResolveAsync(string name, McpTool tool, string? asked, CancellationToken cancellationToken)
    {
        var ready = Usable(name, tool.ServerToolName);
        if (ready.Refusal is null && (ready.Live is null || ready.Live.Dead))
        {
            ready = await ReconnectForCallAsync(name, tool.ServerToolName, ready.Live?.Client, null, cancellationToken).ConfigureAwait(false);
        }

        if (ready.Refusal is not null)
        {
            return ready;
        }

        var live = ready.Live!;
        if (asked is not null && !string.Equals(live.Fingerprint, asked, StringComparison.Ordinal))
        {
            Reconnected(name, ready.Reconnected);
            return new Ready(null, McpCallOutcome.Failed(McpText.ChangedWhileAsking(name)), null);
        }

        if (Offered(live, tool) is not { } offered)
        {
            Reconnected(name, ready.Reconnected);
            return new Ready(null, McpCallOutcome.Failed(McpText.NoLongerOffersError(name, tool.ServerToolName)), null);
        }

        return ready with { Tool = offered };
    }

    /// <summary>
    /// The send and what a drop makes of it, with the audit's word for each end: <c>ran</c> or <c>error</c> for an answer, <c>dropped, may
    /// have changed something</c> for every end after a first send that got none, whatever came next. After a drop the call goes again only
    /// when the reconnected listing still marks the tool read-only, and it was asked already or its server's <c>ask</c> wouldn't ask now
    /// (2026-11-20, the mcp-ask review's #14): the resend is judged on the server as it came back.
    /// </summary>
    private async Task<(McpCallOutcome Outcome, string Word)> SendAndRecoverAsync(string name, McpTool tool, Connection live, McpTool sending, CallToolRequestParams request, string? reconnected, bool asked, CancellationToken cancellationToken)
    {
        var first = await SendAsync(live, request, cancellationToken).ConfigureAwait(false);
        if (first.Result is { } answer)
        {
            Reconnected(name, reconnected);
            return (McpCallOutcome.Answered(answer), RanWord(answer));
        }

        if (first.SignInNeeded)
        {
            return (await SignInLostAsync(name, live).ConfigureAwait(false), McpText.OutcomeNotSent);
        }

        if (reconnected is not null)
        {
            // Dropped again right after its reconnect: no second one for this call.
            return (await DroppedAgainAsync(name, live, first.Details).ConfigureAwait(false), McpText.OutcomeDropped);
        }

        var again = await ReconnectForCallAsync(name, tool.ServerToolName, live.Client, first.Details, cancellationToken).ConfigureAwait(false);
        if (again.Refusal is { } gone)
        {
            return (gone, McpText.OutcomeDropped);
        }

        var back = again.Live!;
        if (!sending.ReadOnly)
        {
            Reconnected(name, again.Reconnected);
            return (McpCallOutcome.Failed(McpText.DroppedNotResentError(name)), McpText.OutcomeDropped);
        }

        if (Offered(back, tool) is not { } fresh)
        {
            Reconnected(name, again.Reconnected);
            return (McpCallOutcome.Failed(McpText.NoLongerOffersError(name, tool.ServerToolName)), McpText.OutcomeDropped);
        }

        if (!fresh.ReadOnly || (!asked && McpAskRule.Asks(back.Entry.Config.AskMode, fresh)))
        {
            Reconnected(name, again.Reconnected);
            return (McpCallOutcome.Failed(McpText.DroppedNotResentError(name)), McpText.OutcomeDropped);
        }

        var second = await SendAsync(back, request, cancellationToken).ConfigureAwait(false);
        if (second.Result is { } retried)
        {
            Reconnected(name, again.Reconnected);
            return (McpCallOutcome.Answered(retried), McpText.OutcomeDropped);
        }

        if (second.SignInNeeded)
        {
            return (await SignInLostAsync(name, back).ConfigureAwait(false), McpText.OutcomeDropped);
        }

        return (await DroppedAgainAsync(name, back, second.Details).ConfigureAwait(false), McpText.OutcomeDropped);
    }

    /// <summary>An answer's audit word: <c>error</c> for an <c>isError</c> result, else <c>ran</c>.</summary>
    private static string RanWord(CallToolResult answer) => answer.IsError == true ? McpText.OutcomeError : McpText.OutcomeRan;

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
                { State: McpState.Failed } row => new Ready(null, NotBack(row), null, NotBack: true),
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
                return new Ready(null, NotBack(outcome), null, NotBack: true);
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

        FlushLogs();
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
        var failed = new McpServerStatus(name, live.Entry.Scope, null, true, McpState.Failed, McpText.DroppedAgainDetail, [], live.Entry.Config.Describe()) { DetailIsApps = true };
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

        FlushLogs();
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
        var sent = await SendAsync(live, (client, token) => client.CallToolAsync(request, token).AsTask(), cancellationToken).ConfigureAwait(false);
        return new Sent(sent.Value, sent.Details, sent.SignInNeeded);
    }

    /// <summary>
    /// A server whose sign-in ran out mid-session (2026-11-22, the review's #36: its refresh failed, or it answered 401): this connection
    /// dropped (taken out first, so its watcher stays quiet), the one needs-sign-in row published, and the call's needs-sign-in refusal, which
    /// also tells headless (<see cref="SignInNeededCall"/>). The browser never opens on its own.
    /// </summary>
    private async Task<McpCallOutcome> SignInLostAsync(string name, Connection live)
    {
        bool mine;
        lock (_snapshot)
        {
            mine = _connections.TryGetValue(name, out var current) && ReferenceEquals(current, live);
            if (mine)
            {
                _connections.Remove(name);
            }
        }

        var row = NeedsSignInRow(live.Entry);
        if (mine)
        {
            await DisposeQuietlyAsync(live.Client).ConfigureAwait(false);
            DiagnosticLog.Info(Category, McpText.NeedsSignInLogLine(name));
            PublishRow(row);
            FlushLogs();
        }

        return NotBack(row);
    }

    /// <summary>
    /// Any one request sent as <see cref="SendAsync(Connection, CallToolRequestParams, CancellationToken)"/> sends a call (2026-11-21: a
    /// resource read shares it): <see cref="SentOf{T}.Answered"/> with the value, or a drop with the client's details.
    /// </summary>
    private static async Task<SentOf<T>> SendAsync<T>(Connection live, Func<McpClient, CancellationToken, Task<T>> send, CancellationToken cancellationToken)
        where T : class
    {
        var completion = live.Client.Completion;
        var call = send(live.Client, cancellationToken);
        var first = await Task.WhenAny(call, completion).ConfigureAwait(false);
        if (!ReferenceEquals(first, call) && !call.IsCompleted)
        {
            _ = call.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new SentOf<T>(null, completion.Result, false);
        }

        try
        {
            return new SentOf<T>(await call.ConfigureAwait(false), null, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && live.Entry.Config.SignInPossible && (McpOAuth.IsSignInNeeded(ex) || McpOAuth.IsUnauthorized(ex)))
        {
            // A sign-in that ran out mid-session (2026-11-22, the review's #36): the caller drops the connection and asks for a sign-in.
            return new SentOf<T>(null, null, false) { SignInNeeded = true };
        }
        catch (ClientTransportClosedException closed)
        {
            return new SentOf<T>(null, closed.Details, false);
        }
        catch (IOException) when (!completion.IsCompleted)
        {
            // The client's read loop can fault the call ("The server shut down unexpectedly") a moment before it sets its completion
            // (2026-11-22, a flake of McpSessionReconnectTests under the full run): a drop, once the completion follows, never the call's error.
            if (await Task.WhenAny(completion, Task.Delay(DropSettle, CancellationToken.None)).ConfigureAwait(false) == completion)
            {
                return new SentOf<T>(null, completion.Result, false);
            }

            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && completion.IsCompleted)
        {
            return new SentOf<T>(null, completion.Result, false);
        }
    }

    /// <summary>How long a call that faulted on its transport waits for the client's completion to follow (real time: the client sets it at once).</summary>
    private static readonly TimeSpan DropSettle = TimeSpan.FromSeconds(2);

    /// <summary>What one send of any request came to: the value, or a drop with what the client knew about it.</summary>
    private readonly record struct SentOf<T>(T? Value, ClientCompletionDetails? Details, bool Answered)
        where T : class
    {
        /// <summary>The server wants a sign-in again (#36): neither an answer nor a drop to reconnect from.</summary>
        public bool SignInNeeded { get; init; }
    }

    /// <summary>
    /// The text for a server that didn't come back. Its detail can be the server's own (a connect exception's message, such as a JSON-RPC
    /// error from its <c>initialize</c>), so it is fenced on its own; the app's own details (a timeout, a cancel, a drop on the retry, a value
    /// that can't be decrypted) aren't. Told apart by where the detail came from (2026-11-20, the mcp-ask review's #118), not by its words:
    /// a server's message that began like the app's would have gone unfenced.
    /// </summary>
    private McpCallOutcome NotBack(McpServerStatus row)
    {
        if (row.NeedsSignIn)
        {
            // A server that wants a sign-in (2026-11-21): the way to it, never a retry tail; headless counts it as a failure.
            SignInNeededCall?.Invoke(row.Name);
            return McpCallOutcome.Failed(McpText.SignInNeededError(row.Name, _retryTail == McpText.HeadlessRetryTail ? McpText.SignInHeadlessTail : McpText.SignInTail));
        }

        string detail = row.Detail ?? "";
        return row.DetailIsApps
            ? McpCallOutcome.Failed(McpText.NotBackError(row.Name, detail, _retryTail))
            : McpCallOutcome.Failed(McpText.NotBackError(row.Name, Llm.Tools.UntrustedText.Fenced(detail), _retryTail), fenced: true);
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

        FlushLogs();
        DiagnosticLog.Info(Category, McpText.ToolsChangedLogLine(name, count));
        Notice(McpText.ToolsChangedNotice(name, count), error: false);
    }

    /// <summary>
    /// Whether a new listing says what the old one did, tool by tool in order, by <see cref="McpTool.DefinitionKeyOf"/> (2026-11-20, the
    /// mcp-ask review's #87: the hints too, so a tool that stops saying it's read-only is applied, and asks).
    /// </summary>
    private static bool SameTools(IReadOnlyList<McpTool> tools, IList<McpClientTool> listed)
    {
        if (tools.Count != listed.Count)
        {
            return false;
        }

        for (int i = 0; i < tools.Count; i++)
        {
            if (!string.Equals(tools[i].DefinitionKey, McpTool.DefinitionKeyOf(listed[i].ProtocolTool), StringComparison.Ordinal))
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
            RebuildTools();
        }

        FlushLogs();
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

            RebuildTools();
        }

        FlushLogs();
    }

    /// <summary>
    /// Under the snapshot lock: everything worked out from <c>_servers</c> as just written. The tools, the notes, the names that would ask
    /// (2026-11-20, #69), and each row's notes-left-out mark (#43): a server whose mark turns on is said once per session, by a line owed
    /// until the lock is let go.
    /// </summary>
    private void RebuildTools()
    {
        var rows = _servers;
        var connected = rows.Where(r => r.State == McpState.Connected && r.Tools.Count > 0).ToList();
        _serverTools = connected.Select(r => new McpServerTools(r.Name, r.Tools)).ToArray();
        _tools = connected.SelectMany(r => r.Tools).ToArray();
        _notes = rows
            .Where(r => r.State == McpState.Connected && r.Startable)
            .Select(r => _connections.TryGetValue(r.Name, out var live) && !string.IsNullOrWhiteSpace(live.Instructions) ? new McpServerNote(r.Name, live.Instructions) : null)
            .OfType<McpServerNote>()
            .ToArray();
        _askingNames = rows
            .Where(r => r.State == McpState.Connected)
            .SelectMany(r => r.Tools.Where(t => McpAskRule.Asks(r.Ask, t)).Select(t => t.Name))
            .ToHashSet(StringComparer.Ordinal);
        // mcp_read asks too while a resource server asks always (2026-11-22, the review's #38): an unattended pass or a screen with no pane
        // leaves it out, never offered to be refused.
        if (rows.Any(r => r.State == McpState.Connected && McpAskRule.AsksRead(r.Ask) && _connections.TryGetValue(r.Name, out var held) && held.Client.ServerCapabilities?.Resources is not null))
        {
            ((HashSet<string>)_askingNames).Add(McpReadToolName);
        }

        var leftOut = McpNotes.LeftOut(_notes, _tools).ToHashSet(StringComparer.Ordinal);
        if (rows.Any(r => r.NotesLeftOut != (r.State == McpState.Connected && r.Startable && leftOut.Contains(r.Name))))
        {
            _servers = rows.Select(r => r with { NotesLeftOut = r.State == McpState.Connected && r.Startable && leftOut.Contains(r.Name) }).ToArray();
        }

        foreach (var note in _notes)
        {
            if (leftOut.Contains(note.Server) && !_notesLeftOut.Contains(note.Server))
            {
                LogOnce("total\0" + note.Server + "\0" + note.Text, McpText.InstructionsLeftOutLogLine(note.Server));
            }
        }

        _notesLeftOut = leftOut;
    }

    /// <summary>
    /// Writes <paramref name="line"/> once per session for <paramref name="key"/> (#7): at once, or, under the snapshot lock, owed until it
    /// is let go (<see cref="FlushLogs"/>).
    /// </summary>
    private void LogOnce(string key, string line, bool warn = false)
    {
        lock (_loggedOnce)
        {
            if (!_loggedOnce.Add(key))
            {
                return;
            }
        }

        _owedLogs.Enqueue((line, warn));
        FlushLogs();
    }

    /// <summary>Writes the owed lines, unless this thread still holds the snapshot lock (a nested publish): the outer caller flushes after it.</summary>
    private void FlushLogs()
    {
        if (Monitor.IsEntered(_snapshot))
        {
            return;
        }

        while (_owedLogs.TryDequeue(out var owed))
        {
            if (owed.Warn)
            {
                DiagnosticLog.Warn(Category, owed.Line);
            }
            else
            {
                DiagnosticLog.Info(Category, owed.Line);
            }
        }
    }

    /// <summary>
    /// A connected row with the facts only its live connection knows (2026-11-19): the server's <c>ask</c> and its notes' length. One
    /// helper for a connect, a reload's kept connection (a new row that would otherwise reset both) and every published row.
    /// </summary>
    private static McpServerStatus ConnectedFacts(McpServerStatus row, Connection live) => row with
    {
        SignedIn = live.SignedIn,
        Ask = live.Entry.Config.AskMode,
        NotesLength = live.Instructions is null ? null : live.Notes?.Shown,
        NotesRaw = live.Instructions is null ? null : live.Notes?.Raw,
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
        CancelSignIn();
        _stop.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_snapshot)
            {
                _disposed = true;
            }

            await DisposeAllAsync().ConfigureAwait(false);
            // A sign-in a busy file kept in memory (2026-11-22, the review's #6), written now the clients that could refresh it are gone.
            McpTokenStore? tokens;
            lock (_tokensGate)
            {
                tokens = _tokens;
            }

            tokens?.Flush();
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
