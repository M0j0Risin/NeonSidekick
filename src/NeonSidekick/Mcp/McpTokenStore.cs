using System.Text.Json;
using ModelContextProtocol.Authentication;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sql;

namespace NeonSidekick.Mcp;

/// <summary>One server's sign-in in <c>mcp-tokens.json</c>: who it is, the redirect port its app registered with, and the sealed tokens.</summary>
public sealed class McpTokenEntry
{
    public string Server { get; set; } = "";
    public string Url { get; set; } = "";
    public int Port { get; set; }
    public string Sealed { get; set; } = "";
}

/// <summary>What a sign-out came to (2026-11-22, the review's #7): a busy file is said, never read as "held no sign-in".</summary>
public enum SignOutResult
{
    NotSignedIn,
    SignedOut,
    Busy,
}

/// <summary>
/// The MCP sign-ins of one profile (2026-11-21, feature idea #32): <c>&lt;profile&gt;/configs/mcp-tokens.json</c>, one entry per
/// <c>&lt;server&gt;|&lt;url&gt;</c>, so a changed <c>url</c> never reuses a token. Each value is the SDK's <see cref="TokenContainer"/> as JSON,
/// sealed for this Windows user (DPAPI) or the Keychain; tokens are never written unsealed. A seal that fails keeps them for this run only.
/// <para>
/// Two copies of the app on one profile (the TUI open, a headless run): many services hand out a new refresh token on each refresh, so a
/// read checks the file's stamp (write time and length) and re-reads when it changed, and a write re-reads under
/// <see cref="ConnectionsFileEdit.Hold"/> and replaces its own entry alone. A busy file (past the hold's wait) keeps the new tokens in
/// memory, sealed, and every later write carries them (and <see cref="Flush"/> at the end); a refresh never fails over the file. Nothing
/// here ever logs a token, a code or a secret.
/// </para>
/// <para>
/// The review's fixes (2026-11-22): a read that fails on IO keeps what was read before (#120), one that won't parse is moved to
/// <c>.bad</c> and the store starts fresh (#19); a write seals and writes outside the store's lock, so a lookup never waits on the file
/// (#73); "signed in" means tokens that open (#112); an entry is found by its own server and address as well as its key (#117).
/// Lock order: the hold, then <c>_gate</c>; nothing takes the hold while holding <c>_gate</c>.
/// </para>
/// </summary>
public sealed class McpTokenStore
{
    /// <summary>The file's name under the profile's <c>configs</c>.</summary>
    public const string FileName = "mcp-tokens.json";

    /// <summary>The suffix a file that won't parse is moved to (2026-11-22, the review's #19), beside it in <c>configs</c>.</summary>
    public const string BadSuffix = ".bad";

    private readonly string _path;
    private readonly Func<string, CredentialResult> _seal;
    private readonly Func<string, CredentialResult> _unseal;
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Sealed, TokenContainer? Tokens)> _opened = new(StringComparer.Ordinal);
    private readonly HashSet<string> _saidUnreadable = new(StringComparer.Ordinal);
    private Dictionary<string, McpTokenEntry> _entries = new(StringComparer.Ordinal);
    private FileStamp? _stamp;
    private bool _loaded;

    /// <summary>
    /// Tokens not in the file yet: a busy file's (<see cref="Sealed"/> set, carried by every later write and by <see cref="Flush"/>), or a
    /// seal that failed (null, kept for this run only by design: nothing is written unsealed).
    /// </summary>
    private sealed record Pending(string Server, string Url, int Port, TokenContainer Tokens, string? Sealed);

    /// <summary>The file as last read (2026-11-22, the review's #104): its write time and length, so two writes in one tick still differ.</summary>
    private readonly record struct FileStamp(DateTime Written, long Length);

    private enum ReadKind
    {
        Ok,
        Unreadable,
        Corrupt,
    }

    /// <param name="path">The file's full path (<see cref="PathFor"/>).</param>
    /// <param name="seal">Encrypts a JSON text (<see cref="WindowsCredentials.Protect"/> by default; a test passes one that fails).</param>
    /// <param name="unseal">Decrypts one (<see cref="WindowsCredentials.Unprotect"/> by default).</param>
    public McpTokenStore(string path, Func<string, CredentialResult>? seal = null, Func<string, CredentialResult>? unseal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _seal = seal ?? WindowsCredentials.Protect;
        _unseal = unseal ?? WindowsCredentials.Unprotect;
    }

    /// <summary>The file of the profile in <paramref name="profileDirectory"/>.</summary>
    public static string PathFor(string profileDirectory) => Path.Combine(Settings.Profiles.ConfigsDirectory(profileDirectory), FileName);

    /// <summary>The file's path.</summary>
    public string FilePath => _path;

    /// <summary>
    /// The entry's key: the server's name and the address it connects to, after <c>${NAME}</c> expansion. A name holding <c>|</c> or <c>\</c>
    /// has both escaped (2026-11-22, the review's #117), so <c>a|b</c> at <c>c</c> and <c>a</c> at <c>b|c</c> never share a key; any other
    /// name keeps the key it had.
    /// </summary>
    public static string Key(string server, string url)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(url);
        string name = server.Contains('|', StringComparison.Ordinal) || server.Contains('\\', StringComparison.Ordinal)
            ? server.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal)
            : server;
        return name + "|" + url;
    }

    /// <summary>
    /// Whether this server holds a sign-in registered for <paramref name="port"/> that opens here: what decides that its connects attach OAuth
    /// at all, and the footer's "signed in" (2026-11-22, the review's #112: an entry that won't unseal is no sign-in). A sign-in registered for
    /// another port counts as none (its app is bound to the old redirect), so the next sign-in registers again.
    /// </summary>
    public bool Has(string server, string url, int port) => Get(server, url, port) is not null;

    /// <summary>Whether any sign-in is kept for this server and address, whatever its port or whether it opens: what a sign-out forgets.</summary>
    public bool HasAny(string server, string url)
    {
        Refresh();
        lock (_gate)
        {
            string key = Key(server, url);
            return Mine(_pending.GetValueOrDefault(key), server, url) || Mine(_entries.GetValueOrDefault(key), server, url);
        }
    }

    /// <summary>The tokens kept for this server, opened, or null: none, another port's, or an entry that won't unseal or parse here.</summary>
    public TokenContainer? Get(string server, string url, int port)
    {
        Refresh();
        lock (_gate)
        {
            string key = Key(server, url);
            if (_pending.TryGetValue(key, out var waiting) && Mine(waiting, server, url))
            {
                // A pending sign-in answers for its own port alone (2026-11-22, the review's #9), as a stored one does.
                return waiting.Port == port ? waiting.Tokens : null;
            }

            if (!_entries.TryGetValue(key, out var entry) || !Mine(entry, server, url))
            {
                return null;
            }

            if (entry.Port != port)
            {
                if (_saidUnreadable.Add("port\0" + key))
                {
                    DiagnosticLog.Info(McpConfigFile.Category, McpText.RegisteredAgainLogLine(server, entry.Port, port));
                }

                return null;
            }

            if (_opened.TryGetValue(key, out var opened) && opened.Sealed == entry.Sealed)
            {
                return opened.Tokens;
            }

            var tokens = Open(entry);
            _opened[key] = (entry.Sealed, tokens);
            return tokens;
        }
    }

    /// <summary>
    /// Keeps <paramref name="tokens"/> for this server: sealed, then written in place of its own entry under the file's hold, both outside the
    /// store's lock (2026-11-22, the review's #73). A seal that fails or a busy file keeps them in memory, with a Warn naming the server;
    /// nothing is written unsealed.
    /// </summary>
    public void Store(string server, string url, int port, TokenContainer tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        string key = Key(server, url);
        string json = JsonSerializer.Serialize(tokens, McpJsonContext.Default.TokenContainer);
        var sealedResult = _seal(json);
        if (sealedResult.Error is { } sealError)
        {
            lock (_gate)
            {
                _pending[key] = new Pending(server, url, port, tokens, null);
            }

            DiagnosticLog.Warn(McpConfigFile.Category, McpText.TokensSealFailedLogLine(server, sealError));
            return;
        }

        var pending = new Pending(server, url, port, tokens, sealedResult.Value!);
        lock (_gate)
        {
            _pending[key] = pending;
            _opened[key] = (pending.Sealed!, tokens);
        }

        try
        {
            Write(_ => { });   // the pending entry rides in with every other one
            lock (_gate)
            {
                _saidUnreadable.Remove(key);
                _saidUnreadable.Remove("port\0" + key);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(McpConfigFile.Category, McpText.TokensFileBusyLogLine(server, LogText.Excerpt(ex.Message)));
        }
    }

    /// <summary>
    /// Writes any sign-in a busy file kept in memory (2026-11-22, the review's #6): at the session's end and before a profile switch leaves this
    /// store behind. A file still busy is a log line; a seal that failed is never written.
    /// </summary>
    public void Flush()
    {
        string? server;
        lock (_gate)
        {
            server = _pending.Values.FirstOrDefault(p => p.Sealed is not null)?.Server;
        }

        if (server is null)
        {
            return;
        }

        try
        {
            Write(_ => { });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(McpConfigFile.Category, McpText.TokensFileBusyLogLine(server, LogText.Excerpt(ex.Message)));
        }
    }

    /// <summary>
    /// Forgets this server's sign-in (a sign-out): its entry removed from the file and from memory. <see cref="SignOutResult.NotSignedIn"/>
    /// when none was kept, <see cref="SignOutResult.Busy"/> when the file couldn't be written (the entry stays, so the sign-out can be tried again).
    /// </summary>
    public SignOutResult Forget(string server, string url)
    {
        string key = Key(server, url);
        Refresh();
        lock (_gate)
        {
            bool held = Mine(_pending.GetValueOrDefault(key), server, url) || Mine(_entries.GetValueOrDefault(key), server, url);
            if (!held)
            {
                return SignOutResult.NotSignedIn;
            }

            _pending.Remove(key);
            _opened.Remove(key);
        }

        try
        {
            Write(entries => entries.Remove(key));
            return SignOutResult.SignedOut;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(McpConfigFile.Category, McpText.TokensFileBusyLogLine(server, LogText.Excerpt(ex.Message)));
            return SignOutResult.Busy;
        }
    }

    /// <summary>
    /// Removes every entry, kept or pending, that isn't kept: its key in <paramref name="keep"/>, its server in <paramref name="keepServers"/>
    /// (2026-11-22, the review's #5: a url holding <c>${NAME}</c> may expand otherwise in another environment, so such a server's sign-ins are kept
    /// by name), or its server in <paramref name="broken"/> (an entry with a problem of its own keeps its sign-in until it is fixed). The caller
    /// decides when it may: never while <c>mcp.json</c> has a problem with no name. A log line per server, never a token.
    /// </summary>
    public void Prune(IReadOnlySet<string> keep, IReadOnlySet<string>? keepServers = null, IReadOnlySet<string>? broken = null)
    {
        ArgumentNullException.ThrowIfNull(keep);
        keepServers ??= new HashSet<string>(StringComparer.Ordinal);
        broken ??= new HashSet<string>(StringComparer.Ordinal);
        bool Kept(string key, string server) => keep.Contains(key) || keepServers.Contains(server) || broken.Contains(server);
        Refresh();
        List<string> gone;
        lock (_gate)
        {
            gone = _entries.Where(e => !Kept(e.Key, e.Value.Server)).Select(e => e.Value.Server).ToList();
            foreach (var key in _pending.Where(p => !Kept(p.Key, p.Value.Server)).Select(p => p.Key).ToList())
            {
                _pending.Remove(key);
            }
        }

        if (gone.Count == 0)
        {
            return;
        }

        try
        {
            Write(entries =>
            {
                foreach (var key in entries.Where(e => !Kept(e.Key, e.Value.Server)).Select(e => e.Key).ToList())
                {
                    entries.Remove(key);
                }
            });
            foreach (var server in gone)
            {
                DiagnosticLog.Info(McpConfigFile.Category, McpText.TokensPrunedLogLine(server));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Debug(McpConfigFile.Category, McpText.TokensFileBusyLogLine(gone[0], LogText.Excerpt(ex.Message)));
        }
    }

    /// <summary>The token cache the SDK reads and refreshes through, for one server at one port.</summary>
    public ITokenCache CacheFor(string server, string url, int port) => new Cache(this, server, url, port);

    private sealed class Cache(McpTokenStore store, string server, string url, int port) : ITokenCache
    {
        public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            // Synchronous inside the hold (its locks are thread-affine): no await here.
            store.Store(server, url, port, tokens);
            return ValueTask.CompletedTask;
        }

        public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(store.Get(server, url, port));
    }

    /// <summary>An entry or a pending sign-in that is this server's at this address, not another's under a colliding key (#117).</summary>
    private static bool Mine(McpTokenEntry? entry, string server, string url) =>
        entry is not null && string.Equals(entry.Server, server, StringComparison.Ordinal) && string.Equals(entry.Url, url, StringComparison.Ordinal);

    private static bool Mine(Pending? pending, string server, string url) =>
        pending is not null && string.Equals(pending.Server, server, StringComparison.Ordinal) && string.Equals(pending.Url, url, StringComparison.Ordinal);

    // ── The file ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The read before a lookup: the file again when its stamp changed. One that won't parse reads as empty and is then moved aside outside
    /// <c>_gate</c> (the hold is never taken under it), with no wait: a busy file is moved by the next write instead.
    /// </summary>
    private void Refresh()
    {
        bool corrupt;
        lock (_gate)
        {
            corrupt = RefreshLocked();
        }

        if (!corrupt)
        {
            return;
        }

        try
        {
            using (ConnectionsFileEdit.Hold(_path, TimeSpan.Zero))
            {
                var (entries, kind, detail) = TryRead();
                if (kind == ReadKind.Corrupt)
                {
                    MoveAsideHeld(detail);
                }

                var stamp = Stamp();
                lock (_gate)
                {
                    _entries = kind == ReadKind.Ok ? entries! : new Dictionary<string, McpTokenEntry>(StringComparer.Ordinal);
                    _stamp = stamp;
                    _loaded = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Debug(McpConfigFile.Category, McpText.TokensFileUnreadableLogLine(LogText.Excerpt(ex.Message)));
        }
    }

    /// <summary>
    /// Re-reads the file when its stamp changed since the last read: one stat a request. A read that fails on IO keeps the entries read before and
    /// leaves the stamp, so the next request tries again (#120). True for a file that won't parse: read as empty until it is moved aside.
    /// </summary>
    private bool RefreshLocked()
    {
        var stamp = Stamp();
        if (_loaded && stamp == _stamp)
        {
            return false;
        }

        var (entries, kind, _) = TryRead();
        switch (kind)
        {
            case ReadKind.Ok:
                _entries = entries!;
                _stamp = stamp;
                _loaded = true;
                return false;
            case ReadKind.Corrupt:
                _entries = new Dictionary<string, McpTokenEntry>(StringComparer.Ordinal);
                _stamp = stamp;
                _loaded = true;
                return true;
            default:
                return false;
        }
    }

    private FileStamp? Stamp()
    {
        try
        {
            var info = new FileInfo(_path);
            return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The file, strictly (2026-11-22, the review's #19 and #120): its entries (none for no file), <see cref="ReadKind.Unreadable"/> for an IO
    /// failure, <see cref="ReadKind.Corrupt"/> for text that won't parse. The unreadable line is said once until a good read (#133).
    /// </summary>
    private (Dictionary<string, McpTokenEntry>? Entries, ReadKind Kind, string Detail) TryRead()
    {
        try
        {
            var entries = new Dictionary<string, McpTokenEntry>(StringComparer.Ordinal);
            if (File.Exists(_path))
            {
                var read = JsonSerializer.Deserialize(File.ReadAllText(_path), McpJsonContext.Default.DictionaryStringMcpTokenEntry);
                foreach (var (key, entry) in read ?? [])
                {
                    if (entry is not null)
                    {
                        entries[key] = entry;
                    }
                }
            }

            lock (_gate)
            {
                _saidUnreadable.Remove("\0file");
            }

            return (entries, ReadKind.Ok, "");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (null, ReadKind.Corrupt, LogText.Excerpt(ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string detail = LogText.Excerpt(ex.Message);
            bool say;
            lock (_gate)
            {
                say = _saidUnreadable.Add("\0file");
            }

            if (say)
            {
                DiagnosticLog.Warn(McpConfigFile.Category, McpText.TokensFileUnreadableLogLine(detail));
            }

            return (null, ReadKind.Unreadable, detail);
        }
    }

    /// <summary>A file that won't parse moved to <c>.bad</c> (the house pattern), the caller holding the hold. Throws when it can't be moved.</summary>
    private void MoveAsideHeld(string detail)
    {
        File.Move(_path, _path + BadSuffix, overwrite: true);
        DiagnosticLog.Warn(McpConfigFile.Category, McpText.TokensFileMovedAsideLogLine(detail));
    }

    /// <summary>
    /// One read-modify-write under the hold, outside <c>_gate</c>: the file read again strictly inside it (an IO failure throws, so nothing
    /// read is lost; one that won't parse is moved aside first), every pending sealed entry merged in (#6), <paramref name="change"/> applied,
    /// written atomically; then the in-memory state set under <c>_gate</c>, each merged entry no longer pending unless a newer one replaced it.
    /// </summary>
    private void Write(Action<Dictionary<string, McpTokenEntry>> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        using (ConnectionsFileEdit.Hold(_path))
        {
            var (read, kind, detail) = TryRead();
            if (kind == ReadKind.Unreadable)
            {
                throw new IOException(detail);
            }

            if (kind == ReadKind.Corrupt)
            {
                MoveAsideHeld(detail);
            }

            var entries = read ?? new Dictionary<string, McpTokenEntry>(StringComparer.Ordinal);
            List<KeyValuePair<string, Pending>> merged;
            lock (_gate)
            {
                merged = _pending.Where(p => p.Value.Sealed is not null).ToList();
            }

            foreach (var (key, pending) in merged)
            {
                entries[key] = new McpTokenEntry { Server = pending.Server, Url = pending.Url, Port = pending.Port, Sealed = pending.Sealed! };
            }

            change(entries);
            string json = JsonSerializer.Serialize(entries, McpJsonContext.Default.DictionaryStringMcpTokenEntry);
            Files.WorkingDirectory.WriteAtomically(_path, System.Text.Encoding.UTF8.GetBytes(json));
            var stamp = Stamp();
            lock (_gate)
            {
                foreach (var (key, pending) in merged)
                {
                    if (_pending.TryGetValue(key, out var now) && ReferenceEquals(now, pending))
                    {
                        _pending.Remove(key);
                    }
                }

                _entries = entries;
                _stamp = stamp;
                _loaded = true;
            }
        }
    }

    /// <summary>An entry's tokens, or null with one log line naming the server: one that won't unseal here, or won't parse.</summary>
    private TokenContainer? Open(McpTokenEntry entry)
    {
        var plain = _unseal(entry.Sealed);
        if (plain.Error is { } error)
        {
            SayUnreadable(entry, error);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(plain.Value!, McpJsonContext.Default.TokenContainer);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            SayUnreadable(entry, LogText.Excerpt(ex.Message));
            return null;
        }
    }

    private void SayUnreadable(McpTokenEntry entry, string detail)
    {
        if (_saidUnreadable.Add(Key(entry.Server, entry.Url)))
        {
            DiagnosticLog.Info(McpConfigFile.Category, McpText.TokensUnreadableLogLine(entry.Server, detail));
        }
    }
}
