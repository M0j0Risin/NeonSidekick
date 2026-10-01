using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The MCP session (2026-09-20) over in-process pipe servers: the wave, the rows, the tools, the flips, the reload, the disposal.</summary>
public class McpSessionTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings;
    private readonly InProcessMcpServers _servers = new();
    private readonly ManualTimeProvider _time = new();

    public McpSessionTests()
    {
        _settings = new AppSettings(_dir);
        _settings.Update(d => d.McpServers = true);   // off by default since 2026-09-21; these tests are about the connections
    }

    public async ValueTask DisposeAsync()
    {
        await _servers.DisposeAsync();
        _settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private McpSession Session() => new(_settings, _servers.Transport, _time);

    private void ProfileFile(string json) => File.WriteAllText(McpConfigFile.ProfilePath(_settings.ProfileDirectory), json);

    private void GlobalFile(string json) => File.WriteAllText(McpConfigFile.GlobalPath(_settings.StorageDirectory), json);

    private const string Two = """{ "mcpServers": { "docker": { "command": "docker", "args": ["mcp", "gateway", "run"] }, "chrome": { "url": "http://localhost:9/mcp" } } }""";

    [Fact]
    public void Strings_ArePinned()
    {
        Assert.Equal("Mcp", McpSession.Category);
        Assert.Equal("NeonSidekick", McpSession.ClientName);
        Assert.Equal("🔌 MCP: 2 servers, 14 tools", McpText.StatusLine(2, 14));
        Assert.Equal("🔌 MCP: 1 server, 1 tool", McpText.StatusLine(1, 1));
        Assert.Equal("🔌 MCP: no server connected", McpText.StatusLine(0, 0));
        Assert.Equal("🔌 MCP: docker failed: timed out after 5 s", McpText.FailedLine("docker", McpText.TimedOut(5)));
        Assert.Equal("connecting MCP servers (1 of 3)", McpText.ConnectingProgress(1, 3));
        Assert.Equal(SpeechSession.ConnectCancelledDetail, McpText.Cancelled);
        Assert.Equal("Connected docker: 3 tools in 12 ms", McpText.ConnectedLogLine("docker", 3, TimeSpan.FromMilliseconds(12.7)));
        Assert.Equal("Failed docker: boom", McpText.FailedLogLine("docker", "boom"));
        Assert.Equal("Stopped docker", McpText.StoppedLogLine("docker"));
        Assert.Equal("Could not read x: y", McpText.ConfigProblemLogLine("x", "y"));
        Assert.Equal("docker stderr: warn", McpText.StderrLogLine("docker", "warn"));
    }

    [Fact]
    public async Task NothingConfigured_ConnectsNothing_NoStatusLine()
    {
        await using var session = Session();
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);

        Assert.Empty(session.Servers);
        Assert.Empty(session.Problems);
        Assert.Empty(session.Tools);
        Assert.False(session.Configured);
        Assert.False(session.Attempted);
        Assert.Null(session.StatusLine());
        Assert.Empty(session.WarningLines());
        Assert.Empty(_servers.Requested);
    }

    [Fact]
    public async Task TwoServers_ConnectInParallel_TheToolsPrefixed_TheStatusLineCounts()
    {
        ProfileFile(Two);
        _servers.Tools("chrome", ("navigate", "Opens a page."));
        var labels = new List<string>();
        await using var session = Session();

        await session.ConnectAllAsync(_settings.Current, labels.Add, CancellationToken.None);

        Assert.Equal(["docker", "chrome"], session.Servers.Select(s => s.Name));
        Assert.All(session.Servers, s => Assert.Equal(McpState.Connected, s.State));
        Assert.Equal(["docker__echo", "docker__fail", "chrome__navigate"], session.Tools.Select(t => t.Name));
        Assert.Equal(["docker", "chrome"], session.ServerTools.Select(g => g.Name));
        Assert.Equal("🔌 MCP: 2 servers, 3 tools", session.StatusLine());
        Assert.Empty(session.WarningLines());
        Assert.True(session.Attempted);
        Assert.Equal("connecting MCP servers (0 of 2)", labels[0]);
        Assert.Equal("connecting MCP servers (2 of 2)", labels[^1]);
        Assert.Equal(3, labels.Count);
        Assert.Equal("stdio: docker mcp gateway run", session.Servers[0].Transport);
        Assert.Equal("http: http://localhost:9/mcp", session.Servers[1].Transport);
        Assert.Equal([McpScope.Profile, McpScope.Profile], session.Servers.Select(s => s.Scope));
    }

    [Fact]
    public async Task AFailingServer_IsARow_AndAWarning_TheOtherStands()
    {
        ProfileFile(Two);
        _servers.Failing.Add("chrome");
        // A concurrent queue (2026-10-01, a flake): the servers connect at once and a subscriber runs on the producing thread,
        // so two lines could land together and a List lost one ("Failed chrome" missing, one run in five).
        var log = new System.Collections.Concurrent.ConcurrentQueue<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == McpSession.Category) log.Enqueue(e); };
        DiagnosticLog.Emitted += capture;
        await using var session = Session();
        try
        {
            await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var chrome = session.Servers[1];
        Assert.Equal(McpState.Failed, chrome.State);
        Assert.Equal("no such command: http://localhost:9/mcp", chrome.Detail);
        Assert.Equal(McpState.Connected, session.Servers[0].State);
        Assert.Equal("🔌 MCP: 1 server, 2 tools", session.StatusLine());
        Assert.Equal(["🔌 MCP: chrome failed: no such command: http://localhost:9/mcp"], session.WarningLines());
        Assert.Contains(log, e => e.Level == DiagnosticLevel.Info && e.Message.StartsWith("Connected docker: 2 tools in ", StringComparison.Ordinal));
        Assert.Contains(log, e => e.Level == DiagnosticLevel.Info && e.Message == "Failed chrome: no such command: http://localhost:9/mcp");
    }

    [Fact]
    public async Task TheTimeout_MarksAHangingServer_AndTheOthersAreUnaffected()
    {
        ProfileFile(Two);
        _servers.Hanging.Add("docker");
        _settings.Update(d => d.McpConnectTimeoutSeconds = 5);   // the setting's floor; the hang is cut by the per-server budget
        await using var session = Session();
        var effective = AppSettings.Copy(_settings.Current);
        effective.McpConnectTimeoutSeconds = 1;   // below the floor on purpose: the session takes what it is handed

        await session.ConnectAllAsync(effective, null, CancellationToken.None);

        Assert.Equal(McpState.Failed, session.Servers[0].State);
        Assert.Equal("timed out after 1 s", session.Servers[0].Detail);
        Assert.Equal(McpState.Connected, session.Servers[1].State);
    }

    [Fact]
    public async Task TheCallersCancel_MarksTheUnfinished_AndIsRethrownOnce()
    {
        ProfileFile(Two);
        _servers.Hanging.Add("docker");
        await using var session = Session();
        using var cts = new CancellationTokenSource();
        var wave = session.ConnectAllAsync(_settings.Current, null, cts.Token);
        cts.CancelAfter(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wave);

        Assert.Equal(McpState.Failed, session.Servers[0].State);
        Assert.Equal("cancelled", session.Servers[0].Detail);
        Assert.Equal(["🔌 MCP: docker failed: cancelled"], session.WarningLines());
    }

    [Fact]
    public async Task MasterOff_ConnectsNothing_TheRowsOff_TheConfigStillListed()
    {
        ProfileFile(Two);
        _settings.Update(d => d.McpServers = false);
        await using var session = Session();

        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);

        Assert.Equal(2, session.Servers.Count);
        Assert.All(session.Servers, s => Assert.Equal(McpState.Off, s.State));
        Assert.All(session.Servers, s => Assert.True(s.Enabled));   // the per-server switch is its own
        Assert.Empty(session.Tools);
        Assert.False(session.Attempted);
        Assert.Null(session.StatusLine());
        Assert.True(session.Configured);
        Assert.Empty(_servers.Requested);
    }

    [Fact]
    public async Task ADisabledName_IsOff_AndAShadowedGlobalIsNeverStarted()
    {
        ProfileFile("""{ "mcpServers": { "docker": { "command": "p-docker" } } }""");
        GlobalFile("""{ "mcpServers": { "docker": { "command": "g-docker" }, "chrome": { "command": "chrome" } } }""");
        _settings.Update(d => d.McpServersDisabled = ["chrome"]);
        await using var session = Session();

        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);

        Assert.Equal(["docker", "docker", "chrome"], session.Servers.Select(s => s.Name));
        Assert.Equal([McpState.Connected, McpState.Off, McpState.Off], session.Servers.Select(s => s.State));
        Assert.Equal([McpScope.Profile, McpScope.Global, McpScope.Global], session.Servers.Select(s => s.Scope));
        Assert.Equal([null, McpScope.Profile, null], session.Servers.Select(s => s.ShadowedBy));
        Assert.Equal([true, true, false], session.Servers.Select(s => s.Enabled));
        Assert.Equal([("docker", "p-docker")], _servers.Requested.Select(r => (r.Name, r.Config.Command)));
        Assert.Equal("🔌 MCP: 1 server, 2 tools", session.StatusLine());
    }

    [Fact]
    public async Task AProblemInAFile_IsListed_TheGoodServerConnects()
    {
        ProfileFile("""{ "mcpServers": { "docker": { "command": "docker" }, "bad": {} } }""");
        await using var session = Session();

        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);

        Assert.Equal(["docker"], session.Servers.Select(s => s.Name));
        var problem = Assert.Single(session.Problems);
        Assert.EndsWith(" › bad", problem.Source);
        Assert.Equal(McpText.NeitherCommandNorUrl, problem.Reason);
    }

    [Fact]
    public async Task ConnectServer_DisconnectServer_FlipOneRowAlone()
    {
        ProfileFile(Two);
        _settings.Update(d => d.McpServersDisabled = ["chrome"]);
        await using var session = Session();
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        Assert.Equal([McpState.Connected, McpState.Off], session.Servers.Select(s => s.State));

        _settings.Update(d => d.McpServersDisabled = []);
        Assert.True(await session.ConnectServerAsync("chrome", _settings.Current, CancellationToken.None));
        Assert.Equal([McpState.Connected, McpState.Connected], session.Servers.Select(s => s.State));
        Assert.Equal(["docker__echo", "docker__fail", "chrome__echo", "chrome__fail"], session.Tools.Select(t => t.Name));

        await session.DisconnectServerAsync("docker");
        Assert.Equal([McpState.Off, McpState.Connected], session.Servers.Select(s => s.State));
        Assert.False(session.Servers[0].Enabled);
        Assert.Equal(["chrome__echo", "chrome__fail"], session.Tools.Select(t => t.Name));

        Assert.False(await session.ConnectServerAsync("nobody", _settings.Current, CancellationToken.None));
        await session.DisconnectServerAsync("nobody");   // nothing to do, nothing thrown
        Assert.Equal("🔌 MCP: 1 server, 2 tools", session.StatusLine());
    }

    [Fact]
    public async Task ConnectServer_OnAFailedRow_IsTheRetry()
    {
        ProfileFile(Two);
        _servers.Failing.Add("chrome");
        await using var session = Session();
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        Assert.Equal(McpState.Failed, session.Servers[1].State);

        _servers.Failing.Clear();
        Assert.True(await session.ConnectServerAsync("chrome", _settings.Current, CancellationToken.None));
        Assert.Equal(McpState.Connected, session.Servers[1].State);
        Assert.Equal(4, session.Tools.Count);
    }

    [Fact]
    public async Task Reload_StartsTheNewAndChanged_StopsTheGone_KeepsTheSame()
    {
        ProfileFile(Two);
        await using var session = Session();
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        var dockerBefore = _servers.Server("docker");

        ProfileFile("""{ "mcpServers": { "docker": { "command": "docker", "args": ["mcp", "gateway", "run"] }, "chrome": { "url": "http://localhost:10/mcp" }, "fs": { "command": "fs" } } }""");
        var (added, removed, kept) = await session.ReloadAsync(_settings.Current, CancellationToken.None);

        Assert.Equal((2, 1, 1), (added, removed, kept));   // chrome changed (stopped, started again), fs new, docker kept
        Assert.Equal(["docker", "chrome", "fs"], session.Servers.Select(s => s.Name));
        Assert.All(session.Servers, s => Assert.Equal(McpState.Connected, s.State));
        Assert.Same(dockerBefore, _servers.Server("docker"));   // never restarted
        Assert.Equal(["docker", "chrome", "chrome", "fs"], _servers.Requested.Select(r => r.Name));

        ProfileFile("""{ "mcpServers": { "fs": { "command": "fs" } } }""");
        Assert.Equal((0, 2, 1), await session.ReloadAsync(_settings.Current, CancellationToken.None));
        Assert.Equal(["fs"], session.Servers.Select(s => s.Name));
        Assert.Equal(["fs__echo", "fs__fail"], session.Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task ConnectAll_Again_DropsEveryClientFirst()
    {
        ProfileFile(Two);
        await using var session = Session();
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);

        Assert.Equal(["docker", "chrome", "docker", "chrome"], _servers.Requested.Select(r => r.Name));
        Assert.Equal(4, session.Tools.Count);
    }

    [Fact]
    public async Task Dispose_DropsEverything()
    {
        ProfileFile(Two);
        var session = Session();
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        Assert.Equal(4, session.Tools.Count);

        await session.DisposeAsync();

        Assert.Empty(session.Tools);
        Assert.Empty(session.Servers);
        Assert.Null(session.StatusLine());
        session.Dispose();   // the sync form, twice is fine
    }

    [Fact]
    public async Task DefaultTransport_BuildsTheStdioAndHttpTransports_FromTheConfig()
    {
        var stdio = McpSession.DefaultTransport(new McpServerConfig { Command = " docker ", Args = ["mcp"], Env = new() { ["A"] = "1" }, Cwd = @"C:\w" }, "docker");
        Assert.IsType<ModelContextProtocol.Client.StdioClientTransport>(stdio);
        Assert.Equal("docker", stdio.Name);
        var http = McpSession.DefaultTransport(new McpServerConfig { Url = "http://localhost:8811/mcp", Headers = new() { ["Authorization"] = "Bearer x" } }, "gateway");
        Assert.IsType<ModelContextProtocol.Client.HttpClientTransport>(http);
        Assert.Equal("gateway", http.Name);
        await Task.CompletedTask;
    }
}
