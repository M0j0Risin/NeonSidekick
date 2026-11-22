using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Authentication;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;
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
    private volatile bool _browseOn = true;

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

    private McpSession Session(bool browse = true, HttpStatusCode[]? statuses = null, string? tail = null) => new(_settings, (config, name, oauth) => McpSession.DefaultTransport(config, name, oauth, _stub), retryTail: tail)
    {
        Approver = McpApprovers.AllowAll,
        OpenBrowser = url =>
        {
            lock (_opened)
            {
                _opened.Add(url);
            }

            if (browse && _browseOn)
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
        Assert.Equal(SignOutResult.SignedOut, await session.SignOutAsync("stub"));
        var row = Row(session);
        Assert.True(row.NeedsSignIn);
        Assert.Empty(row.Tools);
        Assert.False(session.HoldsSignIn("stub"));
        Assert.Equal(SignOutResult.NotSignedIn, await session.SignOutAsync("stub"));
        Assert.DoesNotContain(McpTokenStore.Key("stub", McpOAuthStub.Endpoint), File.ReadAllText(McpTokenStore.PathFor(_settings.ProfileDirectory)));
    }

    [Fact]
    public async Task TheServiceRefusing_ABusyPort_AndAMissingS256_EachFailTheSignIn_WithTheirReason()
    {
        // The app's own failures are the app's text; the SDK's may carry the server's (2026-11-22, the review's #137, #138).
        _stub.DenyAuthorization = true;
        _stub.DenialDescription = "The user\nsaid no";
        await using (var session = Session())
        {
            var row = await SignInAsync(session);
            Assert.True(row.NeedsSignIn);
            Assert.Contains("access_denied", row.Detail, StringComparison.Ordinal);
            Assert.Contains("The user said no", row.Detail, StringComparison.Ordinal);
            Assert.True(row.DetailIsApps);
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
            Assert.True(row.DetailIsApps);
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
            Assert.False(row.DetailIsApps);
        }

        Assert.Equal(0, _stub.CodeExchanges);
    }

    // ── While a sign-in waits, and after (2026-11-22, the review's section 2) ──

    [Fact]
    public async Task WhileASignInWaits_AnotherServersConnect_GoesOn()
    {
        string port = _port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(ProfileFile, "{ \"mcpServers\": { \"stub\": { \"url\": \"" + McpOAuthStub.Endpoint + "\", \"oauth\": { \"redirectPort\": " + port
            + " } }, \"other\": { \"url\": \"" + McpOAuthStub.Endpoint + "\", \"oauth\": { \"redirectPort\": " + FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture) + " } } } }");
        await using var session = Session(browse: false);
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        var waiting = SignInAsync(session);
        await WaitForAsync(() => _opened.Count == 1);
        await session.ConnectServerAsync("other", _settings.Current, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(session.Servers.Single(s => s.Name == "other").NeedsSignIn);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(HttpStatusCode.OK, await _stub.BrowseAsync(_opened[0], CancellationToken.None));
        Assert.Equal(McpState.Connected, (await waiting).State);
    }

    [Fact]
    public async Task ACancelledSignIn_OnAConnectedServer_KeepsItsConnection()
    {
        await using var session = Session();
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        Assert.Equal(McpState.Connected, (await SignInAsync(session)).State);

        // Tokens that no longer open, so the second sign-in goes to the browser; it's called off there.
        _stub.ExpireAccessTokens();
        _stub.RejectRefresh = true;
        _browseOn = false;
        using var cancel = new CancellationTokenSource();
        var again = SignInAsync(session, cancel.Token);
        await WaitForAsync(() => _opened.Count == 2);
        cancel.Cancel();
        await again;
        var row = Row(session);
        Assert.Equal(McpState.Connected, row.State);
        Assert.False(row.NeedsSignIn);
        Assert.True(row.SignedIn);
        Assert.NotEmpty(session.Tools);
    }

    [Fact]
    public async Task ACallbackPastTheConnectTimeout_StillConnects()
    {
        _settings.Update(d => d.McpConnectTimeoutSeconds = AppSettingsData.MinMcpConnectTimeout);
        await using var session = Session(browse: false);
        var waiting = SignInAsync(session);
        await WaitForAsync(() => _opened.Count == 1);
        await Task.Delay(TimeSpan.FromSeconds(AppSettingsData.MinMcpConnectTimeout + 1));
        Assert.False(waiting.IsCompleted);
        Assert.Single(_opened);   // the SDK's 5 s discover probe never asks for a second tab
        Assert.Equal(HttpStatusCode.OK, await _stub.BrowseAsync(_opened[0], CancellationToken.None));
        Assert.Equal(McpState.Connected, (await waiting).State);
    }

    [Fact]
    public async Task ARefreshThatFailsMidSession_AsksForASignIn_AndNeverOpensTheBrowser()
    {
        await using var session = Session();
        var asked = new List<string>();
        session.SignInNeededCall = name =>
        {
            lock (asked)
            {
                asked.Add(name);
            }
        };
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        await SignInAsync(session);
        var tool = session.Tools.Single(t => t.Name.EndsWith(McpOAuthStub.ToolName, StringComparison.Ordinal));
        _stub.ExpireAccessTokens();
        _stub.RejectRefresh = true;
        string answer = (string)(await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["text"] = "x" }), CancellationToken.None))!;
        Assert.Contains(McpText.SignInNeededError("stub", McpText.SignInTail), answer, StringComparison.Ordinal);
        var row = Row(session);
        Assert.True(row.NeedsSignIn);
        Assert.Equal(McpState.Failed, row.State);
        Assert.Equal(["stub"], asked);
        Assert.Single(_opened);
    }

    [Fact]
    public async Task TheSignedInFooter_SurvivesAReload()
    {
        await using var session = Session();
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        await SignInAsync(session);
        await session.ReloadAsync(_settings.Current, CancellationToken.None);
        var row = Row(session);
        Assert.Equal(McpState.Connected, row.State);
        Assert.True(row.SignedIn);
    }

    [Fact]
    public async Task AServerWithAKeyHeader_ThatAnswers401_IsPlainFailed_NeverASignIn()
    {
        WriteConfig("\"headers\": { \"X-Api-Key\": \"wrong\" }");
        await using var session = Session();
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        var row = Row(session);
        Assert.Equal(McpState.Failed, row.State);
        Assert.False(row.NeedsSignIn);
        Assert.False(session.CanSignIn("stub"));
        Assert.Equal(0, _stub.Registrations);
    }

    [Fact]
    public async Task Headless_TheNeedsSignInWarning_SaysWhereToSignIn_WithNoMcpPane()
    {
        await using var session = Session(tail: McpText.HeadlessRetryTail);
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        string line = Assert.Single(session.WarningLines());
        Assert.Equal(McpText.NeedsSignInHeadlessLine("stub"), line);
        Assert.DoesNotContain("/mcp", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ASignInNeeded_OrFailed_IsFoundInsideWrappers()
    {
        Assert.True(McpOAuth.IsSignInNeeded(new AggregateException(new HttpRequestException("x", new McpSignInNeeded()))));
        Assert.True(McpOAuth.IsSignInFailed(new InvalidOperationException("x", new AggregateException(new McpSignInFailed("y")))));
        Assert.False(McpOAuth.IsSignInFailed(new InvalidOperationException("the server's text")));
        Assert.False(McpOAuth.IsSignInNeeded(new AggregateException()));
    }

    // ── The loopback listener (2026-11-22, the review's section 3) ───────────

    private static Uri Authorize(string state) => new("https://auth.stub.test/authorize?client_id=c&state=" + state);

    private Uri Redirect => McpOAuth.RedirectUri(_port);

    private Uri Callback(string query) => new("http://127.0.0.1:" + _port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/callback?" + query);

    [Fact]
    public async Task TheListener_404sACallbackWithoutItsState_AndAnswersItsOwn_WithTheNeutralPage()
    {
        var wait = McpSignIn.WaitAsync("stub", Authorize("s1"), Redirect, null, null, CancellationToken.None);
        using var browser = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(Callback("error=access_denied"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(Callback("code=c0&state=other"))).StatusCode);
        Assert.False(wait.IsCompleted);
        var answer = await browser.GetAsync(Callback("code=c1&state=s1"));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(McpText.SignInReceivedPage("stub"), await answer.Content.ReadAsStringAsync());
        Assert.Equal("c1", (await wait).Code);
    }

    [Fact]
    public async Task AnIdleConnection_NeverHoldsTheCallbackUp()
    {
        var wait = McpSignIn.WaitAsync("stub", Authorize("s1"), Redirect, null, null, CancellationToken.None);
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, _port);
        using var browser = new HttpClient();
        var clock = Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(Callback("code=c1&state=s1"))).StatusCode);
        Assert.Equal("c1", (await wait).Code);
        Assert.True(clock.Elapsed < McpSignIn.RequestTimeout - TimeSpan.FromSeconds(2), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task ARequestHeldAtTheBudgetsEnd_IsTheTimedOutText()
    {
        McpSignIn.Budget = TimeSpan.FromSeconds(1);
        try
        {
            var wait = McpSignIn.WaitAsync("stub", Authorize("s1"), Redirect, null, null, CancellationToken.None);
            using var held = new TcpClient();
            await held.ConnectAsync(IPAddress.Loopback, _port);
            await held.GetStream().WriteAsync("GET /callback?code=c1&state=s1 HTTP/1.1\r\n"u8.ToArray());
            var failed = await Assert.ThrowsAsync<McpSignInFailed>(() => wait);
            Assert.Equal(McpText.SignInTimedOut(McpOAuth.SignInTimeoutSeconds), failed.Message);
        }
        finally
        {
            McpSignIn.Budget = TimeSpan.FromSeconds(McpOAuth.SignInTimeoutSeconds);
        }
    }

    [Fact]
    public async Task AnOpenerThatThrowsAnything_PrintsTheAddress_AndTheWaitGoesOn()
    {
        var notices = new List<(string Text, bool Error)>();
        var wait = McpSignIn.WaitAsync("stub", Authorize("s1"), Redirect, _ => throw new UnauthorizedAccessException("denied"), (text, error) => notices.Add((text, error)),
            CancellationToken.None);
        var (said, isError) = Assert.Single(notices);
        Assert.True(isError);
        Assert.Equal(McpText.SignInBrowserFailed("stub", Authorize("s1").AbsoluteUri, "denied"), said);
        using var browser = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(Callback("code=c1&state=s1"))).StatusCode);
        Assert.Equal("c1", (await wait).Code);
    }

    [Fact]
    public async Task ARefusal_CarriesTheServicesDescription_OnOneLine()
    {
        var wait = McpSignIn.WaitAsync("stub", Authorize("s1"), Redirect, null, null, CancellationToken.None);
        using var browser = new HttpClient();
        var page = await browser.GetAsync(Callback("error=access_denied&error_description=" + Uri.EscapeDataString("The user\nsaid no") + "&state=s1"));
        Assert.Equal(McpText.SignInErrorPage("stub"), await page.Content.ReadAsStringAsync());
        var failed = await Assert.ThrowsAsync<McpSignInFailed>(() => wait);
        Assert.Equal(McpText.SignInRefused(UntrustedText.Fenced("access_denied"), UntrustedText.Fenced(McpText.OneLine("The user\nsaid no"))), failed.Message);
        Assert.DoesNotContain('\n', McpText.OneLine("The user\nsaid no"));
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
        // Moved aside, never written over (2026-11-22, the review's #19): the old bytes kept beside a fresh file.
        Assert.Equal("not json at all", File.ReadAllText(StorePath + McpTokenStore.BadSuffix));
        garbage.Store("s", "https://x", 1, Tokens("a2"));
        Assert.Equal("a2", new McpTokenStore(StorePath).Get("s", "https://x", 1)!.AccessToken);
        lock (_log)
        {
            Assert.Contains(_log, l => l.StartsWith("mcp-tokens.json won't parse (", StringComparison.Ordinal) && l.Contains(".bad", StringComparison.Ordinal));
        }
    }

    /// <summary>The review's #112: an entry that won't unseal here is no sign-in (Has), yet still one to sign out of (HasAny).</summary>
    [Fact]
    public void AnEntryThatWontUnseal_IsNoSignIn_ButCanBeSignedOutOf()
    {
        new McpTokenStore(StorePath).Store("s", "https://x", 1, Tokens("a1"));
        var other = new McpTokenStore(StorePath, unseal: _ => CredentialResult.Failed("another user's"));
        Assert.False(other.Has("s", "https://x", 1));
        Assert.True(other.HasAny("s", "https://x"));
        Assert.Equal(SignOutResult.SignedOut, other.Forget("s", "https://x"));
        Assert.Equal(SignOutResult.NotSignedIn, other.Forget("s", "https://x"));
    }

    /// <summary>The review's #6 and #9: a busy file's tokens wait in memory for their own port alone, and the next write or a flush carries them.</summary>
    [Fact]
    public void ABusyFilesTokens_AnswerForTheirOwnPort_AndAFlushWritesThem()
    {
        var saved = ConnectionsFileEdit.CrossProcessWait;
        ConnectionsFileEdit.CrossProcessWait = TimeSpan.FromMilliseconds(100);
        try
        {
            var store = new McpTokenStore(StorePath);
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            using (new FileStream(StorePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                store.Store("s", "https://x", 1, Tokens("a1"));
            }

            Assert.Equal("a1", store.Get("s", "https://x", 1)!.AccessToken);
            Assert.Null(store.Get("s", "https://x", 2));   // another port's: none
            Assert.False(File.Exists(StorePath));
            store.Flush();
            Assert.Equal("a1", new McpTokenStore(StorePath).Get("s", "https://x", 1)!.AccessToken);

            // Another server's write carries a pending sign-in too.
            using (new FileStream(StorePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                store.Store("s", "https://x", 1, Tokens("a2"));
            }

            store.Store("t", "https://y", 1, Tokens("b1"));
            Assert.Equal("a2", new McpTokenStore(StorePath).Get("s", "https://x", 1)!.AccessToken);
        }
        finally
        {
            ConnectionsFileEdit.CrossProcessWait = saved;
        }
    }

    /// <summary>The review's #7: a sign-out the file is too busy for says so, and the sign-in stays to be tried again.</summary>
    [Fact]
    public void ASignOut_AgainstABusyFile_IsBusy_AndKeepsTheSignIn()
    {
        var saved = ConnectionsFileEdit.CrossProcessWait;
        ConnectionsFileEdit.CrossProcessWait = TimeSpan.FromMilliseconds(100);
        try
        {
            var store = new McpTokenStore(StorePath);
            store.Store("s", "https://x", 1, Tokens("a1"));
            using (new FileStream(StorePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal(SignOutResult.Busy, store.Forget("s", "https://x"));
            }

            Assert.True(store.HasAny("s", "https://x"));
            Assert.Equal(SignOutResult.SignedOut, store.Forget("s", "https://x"));
        }
        finally
        {
            ConnectionsFileEdit.CrossProcessWait = saved;
        }
    }

    /// <summary>The review's #73: a lookup never waits on a write held up by the file's lock.</summary>
    [Fact]
    public async Task ALookup_ReturnsWhileAStoreWaitsOnTheFile()
    {
        var saved = ConnectionsFileEdit.CrossProcessWait;
        ConnectionsFileEdit.CrossProcessWait = TimeSpan.FromSeconds(3);
        try
        {
            var store = new McpTokenStore(StorePath);
            store.Store("s", "https://x", 1, Tokens("a1"));
            Task storing;
            using (new FileStream(StorePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                storing = Task.Run(() => store.Store("t", "https://y", 1, Tokens("b1")));
                await Task.Delay(200);
                var looked = Task.Run(() => store.Has("s", "https://x", 1));
                Assert.Same(looked, await Task.WhenAny(looked, Task.Delay(TimeSpan.FromSeconds(2))));
                Assert.True(await looked);
                Assert.False(storing.IsCompleted);
            }

            await storing;
            Assert.Equal("b1", new McpTokenStore(StorePath).Get("t", "https://y", 1)!.AccessToken);
        }
        finally
        {
            ConnectionsFileEdit.CrossProcessWait = saved;
        }
    }

    /// <summary>The review's #104: a change that keeps the write time but not the length is read again.</summary>
    [Fact]
    public void AChangeAtTheSameWriteTime_ButAnotherLength_IsReadAgain()
    {
        var reader = new McpTokenStore(StorePath);
        new McpTokenStore(StorePath).Store("s", "https://x", 1, Tokens("a1"));
        var when = new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(StorePath, when);
        Assert.Null(reader.Get("t", "https://y", 1));
        new McpTokenStore(StorePath).Store("t", "https://y", 1, Tokens("b1"));
        File.SetLastWriteTimeUtc(StorePath, when);
        Assert.Equal("b1", reader.Get("t", "https://y", 1)!.AccessToken);
    }

    /// <summary>The review's #117: two servers whose name and address would join to the same key each keep their own sign-in.</summary>
    [Fact]
    public void TwoServers_WhoseKeysWouldCollide_EachFindTheirOwn()
    {
        Assert.NotEqual(McpTokenStore.Key("a|b", "c"), McpTokenStore.Key("a", "b|c"));
        Assert.NotEqual(McpTokenStore.Key("a\\", "b|c"), McpTokenStore.Key("a\\|b", "c"));
        Assert.Equal("plain|https://x", McpTokenStore.Key("plain", "https://x"));   // an ordinary name keeps the key it had
        var store = new McpTokenStore(StorePath);
        store.Store("a|b", "c", 1, Tokens("one"));
        store.Store("a", "b|c", 1, Tokens("two"));
        var read = new McpTokenStore(StorePath);
        Assert.Equal("one", read.Get("a|b", "c", 1)!.AccessToken);
        Assert.Equal("two", read.Get("a", "b|c", 1)!.AccessToken);
    }

    /// <summary>The review's #5 and #9: a server whose url expands <c>${NAME}</c> keeps its sign-ins by name, and a prune reaches pending ones too.</summary>
    [Fact]
    public void APrune_KeepsAServerKeptByName_AndDropsAPendingSignInThatIsntKept()
    {
        var store = new McpTokenStore(StorePath);
        store.Store("env", "https://a.example", 1, Tokens("e1"));
        store.Store("gone", "https://b.example", 1, Tokens("g1"));
        var pending = new McpTokenStore(StorePath, seal: _ => CredentialResult.Failed("locked"));
        pending.Store("gone", "https://b.example", 2, Tokens("g2"));

        store.Prune(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "env" });
        pending.Prune(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "env" });
        var read = new McpTokenStore(StorePath);
        Assert.Equal("e1", read.Get("env", "https://a.example", 1)!.AccessToken);
        Assert.False(read.HasAny("gone", "https://b.example"));
        Assert.False(pending.HasAny("gone", "https://b.example"));
    }

    /// <summary>The review's #120 and #133: a read that fails on IO keeps what was read before, and says so again after a good read between.</summary>
    [WindowsFact]
    public void ALockedRead_KeepsTheSignInsReadBefore_AndIsSaidAgainAfterAGoodRead()
    {
        var reader = new McpTokenStore(StorePath);
        new McpTokenStore(StorePath).Store("s", "https://x", 1, Tokens("a1"));
        Assert.Equal("a1", reader.Get("s", "https://x", 1)!.AccessToken);
        int Said()
        {
            lock (_log)
            {
                return _log.Count(l => l.StartsWith("mcp-tokens.json can't be read", StringComparison.Ordinal));
            }
        }

        for (int round = 1; round <= 2; round++)
        {
            new McpTokenStore(StorePath).Store("t" + round, "https://y", 1, Tokens("b" + round));
            using (new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Equal("a1", reader.Get("s", "https://x", 1)!.AccessToken);   // kept, never wiped
                Assert.Equal(round, Said());
            }

            Assert.Equal("b" + round, reader.Get("t" + round, "https://y", 1)!.AccessToken);   // the good read between
        }
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
