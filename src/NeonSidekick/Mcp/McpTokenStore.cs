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

/// <summary>
/// The MCP sign-ins of one profile (2026-11-21, feature idea #32): <c>&lt;profile&gt;/configs/mcp-tokens.json</c>, one entry per
/// <c>&lt;server&gt;|&lt;url&gt;</c>, so a changed <c>url</c> never reuses a token. Each value is the SDK's <see cref="TokenContainer"/> as JSON,
/// sealed for this Windows user (DPAPI) or the Keychain; tokens are never written unsealed. A seal that fails keeps them for this run only.
/// <para>
/// Two copies of the app on one profile (the TUI open, a headless run): many services hand out a new refresh token on each refresh, so a
/// read checks the file's write time and re-reads when it changed, and a write re-reads under <see cref="ConnectionsFileEdit.Hold"/> and
/// replaces its own entry alone. A busy file (past the hold's wait) keeps the new tokens in memory and writes them at the next store;
/// a refresh never fails over the file. Nothing here ever logs a token, a code or a secret.
/// </para>
/// </summary>
public sealed class McpTokenStore
{
    /// <summary>The file's name under the profile's <c>configs</c>.</summary>
    public const string FileName = "mcp-tokens.json";

    private readonly string _path;
    private readonly Func<string, CredentialResult> _seal;
    private readonly Func<string, CredentialResult> _unseal;
    private readonly object _gate = new();
    private readonly Dictionary<string, TokenContainer> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Sealed, TokenContainer? Tokens)> _opened = new(StringComparer.Ordinal);
    private readonly HashSet<string> _saidUnreadable = new(StringComparer.Ordinal);
    private Dictionary<string, McpTokenEntry> _entries = new(StringComparer.Ordinal);
    private DateTime? _stamp;
    private bool _loaded;

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

    /// <summary>The entry's key: the server's name and the address it connects to, after <c>${NAME}</c> expansion.</summary>
    public static string Key(string server, string url) => server + "|" + url;

    /// <summary>
    /// Whether this server holds a sign-in registered for <paramref name="port"/>: what decides that its connects attach OAuth at all, and
    /// the footer's "signed in". A sign-in registered for another port counts as none (its app is bound to the old redirect), so the next
    /// sign-in registers again.
    /// </summary>
    public bool Has(string server, string url, int port)
    {
        lock (_gate)
        {
            string key = Key(server, url);
            if (_pending.ContainsKey(key))
            {
                return true;
            }

            RefreshLocked();
            return _entries.TryGetValue(key, out var entry) && entry.Port == port;
        }
    }

    /// <summary>Whether any sign-in is kept for this server and address, whatever its port (a sign-out forgets it).</summary>
    public bool HasAny(string server, string url)
    {
        lock (_gate)
        {
            string key = Key(server, url);
            RefreshLocked();
            return _pending.ContainsKey(key) || _entries.ContainsKey(key);
        }
    }

    /// <summary>The tokens kept for this server, opened, or null: none, another port's, or an entry that won't unseal or parse here.</summary>
    public TokenContainer? Get(string server, string url, int port)
    {
        lock (_gate)
        {
            string key = Key(server, url);
            if (_pending.TryGetValue(key, out var waiting))
            {
                return waiting;
            }

            RefreshLocked();
            if (!_entries.TryGetValue(key, out var entry))
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
    /// Keeps <paramref name="tokens"/> for this server: sealed, then written in place of its own entry under the file's hold. A seal that
    /// fails or a busy file keeps them in memory, with a Warn naming the server; nothing is written unsealed.
    /// </summary>
    public void Store(string server, string url, int port, TokenContainer tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        string key = Key(server, url);
        string json = JsonSerializer.Serialize(tokens, McpJsonContext.Default.TokenContainer);
        var sealedResult = _seal(json);
        lock (_gate)
        {
            if (sealedResult.Error is { } sealError)
            {
                _pending[key] = tokens;
                DiagnosticLog.Warn(McpConfigFile.Category, McpText.TokensSealFailedLogLine(server, sealError));
                return;
            }

            var entry = new McpTokenEntry { Server = server, Url = url, Port = port, Sealed = sealedResult.Value! };
            try
            {
                Write(entries => entries[key] = entry);
                _pending.Remove(key);
                _opened[key] = (entry.Sealed, tokens);
                _saidUnreadable.Remove(key);
                _saidUnreadable.Remove("port\0" + key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _pending[key] = tokens;
                DiagnosticLog.Warn(McpConfigFile.Category, McpText.TokensFileBusyLogLine(server, LogText.Excerpt(ex.Message)));
            }
        }
    }

    /// <summary>Forgets this server's sign-in (a sign-out): its entry removed from the file and from memory. False when the file was busy.</summary>
    public bool Forget(string server, string url)
    {
        string key = Key(server, url);
        lock (_gate)
        {
            _pending.Remove(key);
            _opened.Remove(key);
            try
            {
                Write(entries => entries.Remove(key));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn(McpConfigFile.Category, McpText.TokensFileBusyLogLine(server, LogText.Excerpt(ex.Message)));
                return false;
            }
        }
    }

    /// <summary>
    /// Removes every entry whose key isn't in <paramref name="keep"/>: a deleted or re-pointed server's sign-in (a catalog load), but never one
    /// of a server named in <paramref name="broken"/> (an entry with a problem of its own keeps its sign-in until it is fixed). The caller
    /// decides when it may: never while <c>mcp.json</c> has a problem with no name. A log line per server, never a token.
    /// </summary>
    public void Prune(IReadOnlySet<string> keep, IReadOnlySet<string>? broken = null)
    {
        ArgumentNullException.ThrowIfNull(keep);
        broken ??= new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            RefreshLocked();
            bool Kept(string key, McpTokenEntry entry) => keep.Contains(key) || broken.Contains(entry.Server);
            var gone = _entries.Where(e => !Kept(e.Key, e.Value)).Select(e => e.Value.Server).ToList();
            foreach (var key in _pending.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                _pending.Remove(key);
            }

            if (gone.Count == 0)
            {
                return;
            }

            try
            {
                Write(entries =>
                {
                    foreach (var key in entries.Where(e => !Kept(e.Key, e.Value)).Select(e => e.Key).ToList())
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

    // ── The file ─────────────────────────────────────────────────────────────

    /// <summary>Re-reads the file when its write time changed since the last read: one stat a request.</summary>
    private void RefreshLocked()
    {
        DateTime? stamp = Stamp();
        if (_loaded && stamp == _stamp)
        {
            return;
        }

        _entries = ReadFile();
        _stamp = stamp;
        _loaded = true;
    }

    private DateTime? Stamp()
    {
        try
        {
            return File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Dictionary<string, McpTokenEntry> ReadFile()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, McpTokenEntry>(StringComparer.Ordinal);
            }

            var read = JsonSerializer.Deserialize(File.ReadAllText(_path), McpJsonContext.Default.DictionaryStringMcpTokenEntry);
            var entries = new Dictionary<string, McpTokenEntry>(StringComparer.Ordinal);
            foreach (var (key, entry) in read ?? [])
            {
                if (entry is not null)
                {
                    entries[key] = entry;
                }
            }

            return entries;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (_saidUnreadable.Add("\0file"))
            {
                DiagnosticLog.Warn(McpConfigFile.Category, McpText.TokensFileUnreadableLogLine(LogText.Excerpt(ex.Message)));
            }

            return new Dictionary<string, McpTokenEntry>(StringComparer.Ordinal);
        }
    }

    /// <summary>One read-modify-write under the hold: the file read again inside it, <paramref name="change"/> applied, written atomically.</summary>
    private void Write(Action<Dictionary<string, McpTokenEntry>> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        using (ConnectionsFileEdit.Hold(_path))
        {
            var entries = ReadFile();
            change(entries);
            string json = JsonSerializer.Serialize(entries, McpJsonContext.Default.DictionaryStringMcpTokenEntry);
            Files.WorkingDirectory.WriteAtomically(_path, System.Text.Encoding.UTF8.GetBytes(json));
            _entries = entries;
            _stamp = Stamp();
            _loaded = true;
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
