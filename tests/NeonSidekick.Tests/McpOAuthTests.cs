using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Authentication;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The MCP sign-in (2026-11-21, feature idea #32) over <see cref="McpOAuthStub"/>: the SDK's whole OAuth path on the installed version
/// (discovery, registration, PKCE, the code exchange, refresh), the app's loopback listener reached by the stub's browser, the token
/// store's seal and its two-copies rules, and when a sign-in may happen at all. No test binds the default port.
/// </summary>
public sealed class McpOAuthTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings;
    private readonly McpOAuthStub _stub = new();
    private readonly List<string> _log = [];
    private readonly List<string> _opened = [];
    private readonly int _port = FreePort();

    public McpOAuthTests()
    {
        _settings = new AppSettings(_dir);
        _settings.Update(d => d.McpServers = true);
        DiagnosticLog.Emitted += OnLog;
        WriteConfig();
    }

    public ValueTask DisposeAsync()
    {
        DiagnosticLog.Emitted -= OnLog;
        _stub.Dispose();
        _settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    private void OnLog(DiagnosticEvent entry)
    {
        lock (_log)
        {
            _log.Add(entry.Message);
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private string ProfileFile => McpConfigFile.ProfilePath(_settings.ProfileDirectory);

    private void WriteConfig(string? extra = null, int? port = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProfileFile)!);
        string oauth = "\"oauth\": { \"redirectPort\": " + (port ?? _port).ToString(System.Globalization.CultureInfo.InvariantCulture) + " }";
        File.WriteAllText(ProfileFile, "{ \"mcpServers\": { \"stub\": { \"url\": \"" + McpOAuthStub.Endpoint + "\", " + (extra ?? oauth) + " } } }");
    }

    private McpSession Session(bool browse = true, HttpStatusCode[]? statuses = null) => new(_settings, (config, name, oauth) => McpSession.DefaultTransport(config, name, oauth, _stub))
    {
        Approver = McpApprovers.AllowAll,
        OpenBrowser = url =>
        {
            lock (_opened)
            {
                _opened.Add(url);
            }

            if (browse)
            {
                _ = Task.Run(() => _stub.BrowseAsync(url, CancellationToken.None));
            }
        },
    };

    private McpServerStatus Row(McpSession session) => session.Servers.Single(s => s.Name == "stub");

    private async Task<McpServerStatus> SignInAsync(McpSession session, CancellationToken cancellationToken = default) =>
        (await session.SignInAsync("stub", _settings.Current, cancellationToken))!;

    // ── Before a sign-in ─────────────────────────────────────────────────────

    [Fact]
    public async Task AStart_WithNoSignIn_ReadsNeedsSignIn_RegistersNothing_AndOpensNoBrowser()
    {
        await using var session = Session();
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        var row = Row(session);
        Assert.Equal(McpState.Failed, row.State);
        Assert.True(row.NeedsSignIn);
        Assert.Equal(McpText.NeedsSignInLine("stub"), Assert.Single(session.WarningLines()));
        Assert.Equal(McpText.StatusNeedsSignIn + "  " + row.Transport, McpRows.StatusText(row));

        await session.ReloadAsync(_settings.Current, CancellationToken.None);
        Assert.True(Row(session).NeedsSignIn);
        Assert.Equal(0, _stub.Registrations);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task AServerWithItsOwnAuthorizationHeader_ThatAnswers401_IsPlainFailed_NeverASignIn()
    {
        WriteConfig("\"headers\": { \"authorization\": \"Bearer wrong\" }");
        await using var session = Session();
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        var row = Row(session);
        Assert.Equal(McpState.Failed, row.State);
        Assert.False(row.NeedsSignIn);
        Assert.False(session.CanSignIn("stub"));
        Assert.Equal(0, _stub.Registrations);
    }

    // ── The sign-in ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ASignIn_Registers_Exchanges_StoresTheTokensSealed_AndTheNextStartReusesThem()
    {
        await using (var session = Session())
        {
            await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
            var row = await SignInAsync(session);
            Assert.Equal(McpState.Connected, row.State);
            Assert.True(row.SignedIn);
            Assert.Equal(1, _stub.Registrations);
            Assert.Equal(1, _stub.CodeExchanges);
            Assert.True(session.HoldsSignIn("stub"));
            Assert.Single(_opened);
            Assert.StartsWith(McpOAuthStub.Issuer + "/authorize?", _opened[0], StringComparison.Ordinal);
        }

        string file = McpTokenStore.PathFor(_settings.ProfileDirectory);
        string stored = File.ReadAllText(file);
        Assert.DoesNotContain(_stub.Secrets, stored.Contains);
        using (var json = JsonDocument.Parse(stored))
        {
            var entry = json.RootElement.GetProperty(McpTokenStore.Key("stub", McpOAuthStub.Endpoint));
            Assert.Equal(_port, entry.GetProperty("port").GetInt32());
            Assert.True(WindowsCredentials.IsSealed(entry.GetProperty("sealed").GetString()));
        }

        _opened.Clear();
        await using (var again = Session())
        {
            await again.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
            var row = Row(again);
            Assert.Equal(McpState.Connected, row.State);
            Assert.True(row.SignedIn);
        }

        Assert.Equal(1, _stub.Registrations);
        Assert.Empty(_opened);
        lock (_log)
        {
            Assert.DoesNotContain(_log, line => _stub.Secrets.Any(line.Contains));
            Assert.Contains(McpText.SignedInLogLine("stub"), _log);
        }
    }

    [Fact]
    public async Task AnExpiredToken_IsRefreshedQuietly_AndAFailedRefresh_ReadsNeedsSignIn()
    {
        await using (var session = Session())
        {
            await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
            await SignInAsync(session);
        }

        _stub.ExpireAccessTokens();
        await using (var refreshed = Session())
        {
            await refreshed.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
            Assert.Equal(McpState.Connected, Row(refreshed).State);
        }

        Assert.Equal(1, _stub.Refreshes);
        _stub.ExpireAccessTokens();
        _stub.RejectRefresh = true;
        _opened.Clear();
        await using (var refused = Session())
        {
            await refused.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
            Assert.True(Row(refused).NeedsSignIn);
        }

        Assert.Empty(_opened);
        Assert.Equal(1, _stub.Registrations);
    }

    [Fact]
    public async Task ASignOut_ForgetsTheTokens_AndTheRowNeedsASignIn_WithNoReconnect()
    {
        await using var session = Session();
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        await SignInAsync(session);
        Assert.True(await session.SignOutAsync("stub"));
        var row = Row(session);
        Assert.True(row.NeedsSignIn);
        Assert.Empty(row.Tools);
        Assert.False(session.HoldsSignIn("stub"));
        Assert.False(await session.SignOutAsync("stub"));
        Assert.DoesNotContain(McpTokenStore.Key("stub", McpOAuthStub.Endpoint), File.ReadAllText(McpTokenStore.PathFor(_settings.ProfileDirectory)));
    }

    [Fact]
    public async Task TheServiceRefusing_ABusyPort_AndAMissingS256_EachFailTheSignIn_WithTheirReason()
    {
        _stub.DenyAuthorization = true;
        await using (var session = Session())
        {
            var row = await SignInAsync(session);
            Assert.True(row.NeedsSignIn);
            Assert.Contains("access_denied", row.Detail, StringComparison.Ordinal);
        }

        _stub.DenyAuthorization = false;
        var holder = new TcpListener(IPAddress.Loopback, _port);
        holder.Start();
        try
        {
            await using var session = Session();
            var row = await SignInAsync(session);
            Assert.True(row.NeedsSignIn);
            Assert.Contains("redirectPort", row.Detail, StringComparison.Ordinal);
        }
        finally
        {
            holder.Stop();
        }

        _stub.OmitS256 = true;
        await using (var session = Session())
        {
            var row = await SignInAsync(session);
            Assert.True(row.NeedsSignIn);
            Assert.Equal(McpState.Failed, row.State);
        }

        Assert.Equal(0, _stub.CodeExchanges);
    }

    [Fact]
    public async Task AnAddressThatIsntHttps_IsRefused_NeverOpened()
    {
        _stub.AuthorizationEndpointOverride = "file:///C:/Windows/notepad.exe";
        await using var session = Session();
        var row = await SignInAsync(session);
        Assert.True(row.NeedsSignIn);
        Assert.Contains("file", row.Detail, StringComparison.Ordinal);
        Assert.Empty(_opened);
        Assert.False(McpOAuth.MayOpen(new Uri("ms-settings:privacy")));
        Assert.False(McpOAuth.MayOpen(new Uri("http://192.168.1.4/authorize")));
        Assert.True(McpOAuth.MayOpen(new Uri("http://127.0.0.1:9/authorize")));
        Assert.True(McpOAuth.MayOpen(new Uri("https://linear.app/oauth/authorize")));
    }

    [Fact]
    public async Task AStrayRequest_Is404_TheWaitGoesOn_AndACancel_FreesThePortForTheNextSignIn()
    {
        await using var session = Session(browse: false);
        using var cancel = new CancellationTokenSource();
        var waiting = SignInAsync(session, cancel.Token);
        await WaitForAsync(() => _opened.Count == 1);
        Assert.Equal(McpText.StatusSigningIn + "  " + Row(session).Transport, McpRows.StatusText(Row(session)));
        using (var browser = new HttpClient())
        {
            var favicon = await browser.GetAsync(new Uri("http://127.0.0.1:" + _port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/favicon.ico"));
            Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);
            var bare = await browser.GetAsync(new Uri("http://127.0.0.1:" + _port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/callback"));
            Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
        }

        Assert.False(waiting.IsCompleted);
        cancel.Cancel();
        var row = await waiting;
        Assert.True(row.NeedsSignIn);
        Assert.Equal(McpText.SignInCancelledDetail, row.Detail);

        // The same port again: the listener was let go.
        string url = _opened[0];
        _opened.Clear();
        var second = SignInAsync(session);
        await WaitForAsync(() => _opened.Count == 1);
        Assert.NotEqual(url, _opened[0]);
        Assert.Equal(HttpStatusCode.OK, await _stub.BrowseAsync(_opened[0], CancellationToken.None));
        Assert.Equal(McpState.Connected, (await second).State);
    }

    [Fact]
    public async Task AReload_WhileASignInWaits_CallsItOff()
    {
        await using var session = Session(browse: false);
        var waiting = SignInAsync(session);
        await WaitForAsync(() => _opened.Count == 1);
        await session.ReloadAsync(_settings.Current, CancellationToken.None);
        var row = await waiting;
        Assert.Equal(McpText.SignInCancelledDetail, row.Detail);
        Assert.Null(session.SigningIn);
    }

    [Fact]
    public async Task AChangedRedirectPort_TakesTheOldSignInForNone_SoTheNextSignInRegistersAgain()
    {
        await using (var session = Session())
        {
            await SignInAsync(session);
        }

        int other = FreePort();
        WriteConfig(port: other);
        await using (var moved = Session())
        {
            await moved.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
            Assert.True(Row(moved).NeedsSignIn);
            Assert.Equal(McpState.Connected, (await SignInAsync(moved)).State);
        }

        Assert.Equal(2, _stub.Registrations);
    }

    [Fact]
    public async Task ARemovedServersSignIn_IsPruned_ButNeverWhileTheFileIsBroken_OrTheEntryHasAProblem()
    {
        await using (var session = Session())
        {
            await SignInAsync(session);
        }

        string tokens = McpTokenStore.PathFor(_settings.ProfileDirectory);
        string key = McpTokenStore.Key("stub", McpOAuthStub.Endpoint);
        File.WriteAllText(ProfileFile, "{ \"mcpServers\": { ");   // broken JSON
        await using (var session = Session())
        {
            await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        }

        Assert.Contains(key, File.ReadAllText(tokens));
        File.WriteAllText(ProfileFile, "{ \"mcpServers\": { \"\": { \"command\": \"x\" } } }");   // a blank name
        await using (var session = Session())
        {
            await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        }

        Assert.Contains(key, File.ReadAllText(tokens));
        File.WriteAllText(ProfileFile, "{ \"mcpServers\": { \"stub\": { \"url\": \"" + McpOAuthStub.Endpoint + "\", \"ask\": \"sometimes\" } } }");   // its own problem
        await using (var session = Session())
        {
            await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        }

        Assert.Contains(key, File.ReadAllText(tokens));
        File.WriteAllText(ProfileFile, "{ \"mcpServers\": { } }");
        await using (var session = Session())
        {
            await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        }

        Assert.DoesNotContain(key, File.ReadAllText(tokens));
        lock (_log)
        {
            Assert.Contains(McpText.TokensPrunedLogLine("stub"), _log);
        }
    }

    // ── The store ────────────────────────────────────────────────────────────

    private static TokenContainer Tokens(string access) => new() { TokenType = "Bearer", AccessToken = access, RefreshToken = "r-" + access, ObtainedAt = DateTimeOffset.UnixEpoch, ClientId = "c" };

    private string StorePath => Path.Combine(_dir, "configs", McpTokenStore.FileName);

    [Fact]
    public void ASealThatFails_KeepsTheTokensForThisRunOnly_NothingWritten()
    {
        var store = new McpTokenStore(StorePath, seal: _ => CredentialResult.Failed("the Keychain is locked"));
        store.Store("s", "https://x", 1, Tokens("a1"));
        Assert.Equal("a1", store.Get("s", "https://x", 1)!.AccessToken);
        Assert.False(File.Exists(StorePath));
        Assert.Null(new McpTokenStore(StorePath).Get("s", "https://x", 1));
        lock (_log)
        {
            Assert.Contains(McpText.TokensSealFailedLogLine("s", "the Keychain is locked"), _log);
        }
    }

    [Fact]
    public void AnEntryThatWontUnseal_OrWontParse_OrAFileOfGarbage_ReadsAsNoSignIn()
    {
        var store = new McpTokenStore(StorePath);
        store.Store("s", "https://x", 1, Tokens("a1"));
        Assert.Null(new McpTokenStore(StorePath, unseal: _ => CredentialResult.Failed("another user's")).Get("s", "https://x", 1));

        var missing = new McpTokenStore(StorePath, seal: v => CredentialResult.Ok(v), unseal: v => CredentialResult.Ok(v));
        File.WriteAllText(StorePath, "{ \"s|https://x\": { \"server\": \"s\", \"url\": \"https://x\", \"port\": 1, \"sealed\": \"{\\\"tokenType\\\":\\\"Bearer\\\"}\" } }");
        Assert.Null(missing.Get("s", "https://x", 1));
        File.WriteAllText(StorePath, "not json at all");
        var garbage = new McpTokenStore(StorePath);
        Assert.Null(garbage.Get("s", "https://x", 1));
        garbage.Store("s", "https://x", 1, Tokens("a2"));
        Assert.Equal("a2", new McpTokenStore(StorePath).Get("s", "https://x", 1)!.AccessToken);
    }

    [Fact]
    public void TwoCopies_OverOneFile_EachSeeTheOthersRefresh_AndKeepTheOthersEntries()
    {
        var first = new McpTokenStore(StorePath);
        var second = new McpTokenStore(StorePath);
        first.Store("s", "https://x", 1, Tokens("a1"));
        Assert.Equal("a1", second.Get("s", "https://x", 1)!.AccessToken);
        second.Store("t", "https://y", 1, Tokens("b1"));
        File.SetLastWriteTimeUtc(StorePath, DateTime.UtcNow.AddSeconds(5));
        first.Store("s", "https://x", 1, Tokens("a2"));
        File.SetLastWriteTimeUtc(StorePath, DateTime.UtcNow.AddSeconds(10));
        Assert.Equal("a2", second.Get("s", "https://x", 1)!.AccessToken);
        Assert.Equal("b1", first.Get("t", "https://y", 1)!.AccessToken);
    }

    [Fact]
    public void ABusyFile_KeepsTheNewTokensInMemory_AndWritesThemAtTheNextStore()
    {
        var saved = ConnectionsFileEdit.CrossProcessWait;
        ConnectionsFileEdit.CrossProcessWait = TimeSpan.FromMilliseconds(100);
        try
        {
            var store = new McpTokenStore(StorePath);
            store.Store("s", "https://x", 1, Tokens("a1"));
            using (new FileStream(StorePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                store.Store("s", "https://x", 1, Tokens("a2"));
            }

            Assert.Equal("a2", store.Get("s", "https://x", 1)!.AccessToken);
            Assert.Equal("a1", new McpTokenStore(StorePath).Get("s", "https://x", 1)!.AccessToken);
            store.Store("s", "https://x", 1, Tokens("a3"));
            Assert.Equal("a3", new McpTokenStore(StorePath).Get("s", "https://x", 1)!.AccessToken);
        }
        finally
        {
            ConnectionsFileEdit.CrossProcessWait = saved;
        }
    }

    [Fact]
    public void Hold_ReleasedOnItsOwnThread_LetsTheNextEditIn()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        using (ConnectionsFileEdit.Hold(StorePath))
        {
            Assert.True(File.Exists(StorePath + ".lock"));
        }

        Assert.False(File.Exists(StorePath + ".lock"));
        using (ConnectionsFileEdit.Hold(StorePath, TimeSpan.Zero))
        {
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(20);
        }
    }
}
