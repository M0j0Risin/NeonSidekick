using System.Net;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// A server that drops (2026-11-19, feature idea #30) over in-process pipe servers: the reconnect at the next call, a drop during a
/// call, a failed reconnect, a changed tool list, the app's own stops, the locks' races, the dispose and the cancel.
/// </summary>
public class McpSessionReconnectTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings;
    private readonly InProcessMcpServers _servers = new();

    public McpSessionReconnectTests()
    {
        _settings = new AppSettings(_dir);
        _settings.Update(d => d.McpServers = true);
    }

    public async ValueTask DisposeAsync()
    {
        await _servers.DisposeAsync();
        _settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private const string One = """{ "mcpServers": { "docker": { "command": "docker" } } }""";
    private const string Two = """{ "mcpServers": { "docker": { "command": "docker" }, "chrome": { "command": "chrome" } } }""";

    private McpSession Session(string? tail = null) => new(_settings, _servers.Transport, TimeProvider.System, tail);

    private void ProfileFile(string json)
    {
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(McpConfigFile.ProfilePath(_settings.ProfileDirectory), json);
    }

    private async Task<McpSession> ConnectedAsync(string json = One, string? tail = null)
    {
        ProfileFile(json);
        var session = Session(tail);
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        return session;
    }

    private static AIFunction Tool(McpSession session, string name) => session.Tools.Single(t => t.Name == name);

    private static async Task<string> CallAsync(AIFunction tool, string text = "x", CancellationToken cancellationToken = default) =>
        (string)(await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { [McpPipeServer.TextArgument] = text }), cancellationToken))!;

    private static string Echo(string text) => UntrustedText.Fenced(McpPipeServer.EchoPrefix + text) + "\n" + UntrustedText.QuoteNote;

    private static McpServerStatus Row(McpSession session, string name = "docker") => session.Servers.Single(s => s.Name == name);

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("never: " + what);
            }

            await Task.Delay(10);
        }
    }

    private async Task DropAsync(McpSession session, string name = "docker")
    {
        await _servers.Drop(name);
        await Until(() => Row(session, name).Dropped is not null, name + " dropped");
    }

    [Fact]
    public void TheModelsTexts_ArePinned()
    {
        Assert.Equal(
            "docker dropped during the call and reconnected; the call wasn't sent again, since it may have changed something: check before calling it again",
            McpText.DroppedNotResentError("docker"));
        Assert.Equal("docker dropped again; /mcp retries it", McpText.DroppedAgainError("docker", McpText.RetryTail));
        Assert.Equal("docker dropped and didn't come back: boom; the next start retries it", McpText.NotBackError("docker", "boom", McpText.HeadlessRetryTail));
        Assert.Equal("docker no longer offers echo", McpText.NoLongerOffersError("docker", "echo"));
        Assert.Equal("docker: the MCP servers were stopped", McpText.StoppedError("docker"));
    }

    [Fact]
    public void TheDropReason_IsTheExitCode_OrTheHttpStatus_NeverTheStderr()
    {
        Assert.Equal("(exit code 1)", McpText.DropReason(new StdioClientCompletionDetails { ExitCode = 1, StandardErrorTail = ["token=secret"] }));
        Assert.Equal("(HTTP 404)", McpText.DropReason(new HttpClientCompletionDetails { HttpStatusCode = HttpStatusCode.NotFound }));
        Assert.Equal("", McpText.DropReason(new ClientCompletionDetails()));
        Assert.Equal("", McpText.DropReason(new StdioClientCompletionDetails { StandardErrorTail = ["token=secret"] }));
        Assert.Equal("", McpText.DropReason(null));
        Assert.Contains("(exit code 143)", McpText.ReconnectedNotice("docker", McpText.DropReason(new StdioClientCompletionDetails { ExitCode = 143 })));
        Assert.Equal("🔌 MCP: docker dropped; reconnected", McpText.ReconnectedNotice("docker", ""));
    }

    [Fact]
    public async Task DroppedBetweenCalls_TheRowSaysSo_AndTheTurnsOldToolReconnectsFirst_ThenRuns()
    {
        await using var session = await ConnectedAsync();
        var echo = Tool(session, "docker__echo");   // the turn's tool, taken before the drop
        Assert.Null(Row(session).Dropped);

        await DropAsync(session);
        Assert.Equal(McpState.Connected, Row(session).State);   // still offered
        Assert.Null(Row(session).Detail);
        Assert.Contains(McpText.StatusDropped, McpRows.StatusText(Row(session)));
        Assert.Contains(echo, session.Tools);

        Assert.Equal(Echo("x"), await CallAsync(echo));
        Assert.Equal(2, _servers.Requested.Count);
        Assert.Null(Row(session).Dropped);
        Assert.Equal([("docker__echo", true)], [(echo.Name, _servers.Server("docker").Calls.Count == 1)]);
        Assert.Equal([(McpText.ReconnectedNotice("docker", ""), false)], session.TakeNotices());
        Assert.Empty(session.TakeNotices());
    }

    [Fact]
    public async Task ADropDuringAReadOnlyCall_IsSentOnceMore_AndAnswers()
    {
        _servers.Tools("docker", (McpPipeServer.DropToolName, "Reads."), ("echo", "Echoes."));
        _servers.ReadOnly("docker", McpPipeServer.DropToolName);
        await using var session = await ConnectedAsync();
        var first = _servers.Server("docker");
        _servers.Steady.Add("docker");   // the server that comes back answers it

        Assert.Equal(Echo("x"), await CallAsync(Tool(session, "docker__drop")));

        Assert.Single(first.Calls);
        Assert.Single(_servers.Server("docker").Calls);
        Assert.Equal(2, _servers.Requested.Count);
        Assert.Equal([(McpText.ReconnectedNotice("docker", ""), false)], session.TakeNotices());
    }

    [Fact]
    public async Task ADropDuringAnotherCall_IsntSentAgain_AndTheCallLeftBehindIsObserved()
    {
        _servers.Tools("docker", (McpPipeServer.DropToolName, "Writes."), ("echo", "Echoes."));
        await using var session = await ConnectedAsync();
        var first = _servers.Server("docker");
        _servers.Steady.Add("docker");
        var unobserved = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> watch = (_, e) => { lock (unobserved) { unobserved.Add(e.Exception); } };
        TaskScheduler.UnobservedTaskException += watch;
        try
        {
            string answer = await CallAsync(Tool(session, "docker__drop"));

            Assert.Equal(McpText.ErrorPrefix + McpText.DroppedNotResentError("docker"), answer);
            Assert.Single(first.Calls);
            Assert.Empty(_servers.Server("docker").Calls);   // never sent twice
            Assert.Equal(Echo("y"), await CallAsync(Tool(session, "docker__echo"), "y"));   // the next call goes to the new client
            Assert.Single(_servers.Server("docker").Calls);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= watch;
        }

        Assert.Empty(unobserved);
    }

    [Fact]
    public async Task AReadOnlyCallThatDropsAgainOnItsRetry_LeavesTheRowFailed_OneReconnect_OneNotice()
    {
        _servers.Tools("docker", (McpPipeServer.DropToolName, "Reads."));
        _servers.ReadOnly("docker", McpPipeServer.DropToolName);
        await using var session = await ConnectedAsync();

        string answer = await CallAsync(Tool(session, "docker__drop"));

        Assert.Equal(McpText.ErrorPrefix + McpText.DroppedAgainError("docker", McpText.RetryTail), answer);
        Assert.Equal(McpState.Failed, Row(session).State);
        Assert.Equal(McpText.DroppedAgainDetail, Row(session).Detail);
        Assert.Empty(session.Tools);
        Assert.Equal(2, _servers.Requested.Count);
        Assert.Equal([(McpText.NotBackNotice("docker", McpText.DroppedAgainDetail, McpText.RetryTail), true)], session.TakeNotices());
    }

    [Fact]
    public async Task AFailedReconnect_LeavesTheRowFailed_ItsDetailFenced_AndALaterCallDoesntTryAgain()
    {
        await using var session = await ConnectedAsync();
        var echo = Tool(session, "docker__echo");
        await DropAsync(session);
        _servers.Failing.Add("docker");

        string answer = await CallAsync(echo);

        string expected = McpText.ErrorPrefix + McpText.NotBackError("docker", UntrustedText.Fenced("no such command: docker"), McpText.RetryTail) + "\n" + UntrustedText.QuoteNote;
        Assert.Equal(expected, answer);
        Assert.Equal(McpState.Failed, Row(session).State);
        Assert.Null(Row(session).Dropped);
        Assert.Empty(session.Tools);
        Assert.Equal(2, _servers.Requested.Count);
        Assert.Equal([(McpText.NotBackNotice("docker", "no such command: docker", McpText.RetryTail), true)], session.TakeNotices());

        Assert.Equal(expected, await CallAsync(echo));   // the same turn's next call: the same text, no other try
        Assert.Equal(2, _servers.Requested.Count);
        Assert.Empty(session.TakeNotices());
    }

    [Fact]
    public async Task Headless_TheTexts_SayTheNextStartRetriesIt()
    {
        await using var session = await ConnectedAsync(tail: McpText.HeadlessRetryTail);
        var echo = Tool(session, "docker__echo");
        await DropAsync(session);
        _servers.Failing.Add("docker");

        string answer = await CallAsync(echo);

        Assert.EndsWith("; " + McpText.HeadlessRetryTail + "\n" + UntrustedText.QuoteNote, answer, StringComparison.Ordinal);
        Assert.DoesNotContain("/mcp", answer, StringComparison.Ordinal);
        var notice = Assert.Single(session.TakeNotices());
        Assert.EndsWith(McpText.HeadlessRetryTail, notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoCallsAtOnce_ToADroppedServer_MakeOneReconnect()
    {
        await using var session = await ConnectedAsync();
        var echo = Tool(session, "docker__echo");
        await DropAsync(session);

        var answers = await Task.WhenAll(CallAsync(echo, "a"), CallAsync(echo, "b"));

        Assert.Equal([Echo("a"), Echo("b")], answers);
        Assert.Equal(2, _servers.Requested.Count);
        Assert.Single(session.TakeNotices());
    }

    [Fact]
    public async Task ACallWhileAReloadHoldsTheGate_WaitsForIt_AndUsesTheReloadedConnection()
    {
        await using var session = await ConnectedAsync();
        var echo = Tool(session, "docker__echo");
        await DropAsync(session);
        var gate = _servers.Held("docker");

        var reload = session.ReloadAsync(_settings.Current, CancellationToken.None);
        await Until(() => _servers.Requested.Count == 2, "the reload's connect");
        var call = CallAsync(echo);
        await Task.Delay(50);
        Assert.False(call.IsCompleted);
        gate.SetResult();

        Assert.Equal((1, 1, 0), await reload);   // a dropped server is started again, counted like a changed one
        Assert.Equal(Echo("x"), await call);
        Assert.Equal(2, _servers.Requested.Count);   // no reconnect of its own
        Assert.Empty(session.TakeNotices());
    }

    [Fact]
    public async Task AReconnect_UsesTheConfigItWasConnectedWith_AndTheNextReloadStillSeesTheEdit()
    {
        await using var session = await ConnectedAsync();
        var echo = Tool(session, "docker__echo");
        ProfileFile("""{ "mcpServers": { "docker": { "command": "docker2" } } }""");
        await DropAsync(session);

        Assert.Equal(Echo("x"), await CallAsync(echo));
        Assert.Equal("docker", _servers.Requested[^1].Config.Command);

        Assert.Equal((1, 1, 0), await session.ReloadAsync(_settings.Current, CancellationToken.None));
        Assert.Equal("docker2", _servers.Requested[^1].Config.Command);
    }

    [Fact]
    public async Task ATool_KeepsItsName_AcrossAReconnect_AndATurnsToolGoneFromTheNewListFailsWithTheText()
    {
        _servers.Tools("docker", ("echo", "Echoes."), ("gone", "Goes."));
        await using var session = await ConnectedAsync();
        var gone = Tool(session, "docker__gone");
        await DropAsync(session);
        _servers.Tools("docker", ("echo", "Echoes."));

        Assert.Equal(McpText.ErrorPrefix + McpText.NoLongerOffersError("docker", "gone"), await CallAsync(gone));
        Assert.Equal(["docker__echo"], session.Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task AChangedList_AppliesFromTheNextTurn_KeepingTheNames_WithOneNotice()
    {
        await using var session = await ConnectedAsync();
        var turn = session.Tools;   // a turn's list, taken at its start
        var server = _servers.Server("docker");

        await server.NotifyToolsChangedAsync([("echo", "Echoes the text back."), ("extra", "More.")]);
        await Until(() => session.Tools.Any(t => t.Name == "docker__extra"), "the new list");
        await session.ListingsSettledAsync();

        Assert.Equal(["docker__echo", "docker__fail"], turn.Select(t => t.Name));
        Assert.Equal(["docker__echo", "docker__extra"], session.Tools.Select(t => t.Name));
        Assert.Equal([(McpText.ToolsChangedNotice("docker", 2), false)], session.TakeNotices());
        Assert.Equal(Echo("z"), await CallAsync(Tool(session, "docker__extra"), "z"));

        Assert.Equal((0, 0, 1), await session.ReloadAsync(_settings.Current, CancellationToken.None));   // mcp.json unchanged: kept, the new list too
        Assert.Equal(["docker__echo", "docker__extra"], session.Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task AnUnchangedList_PublishesNothing_AndGivesNoNotice()
    {
        await using var session = await ConnectedAsync();
        var before = session.Tools;
        var server = _servers.Server("docker");

        await server.NotifyToolsChangedAsync(server.Tools);
        await Until(() => server.Listings == 2, "the listing");
        await session.ListingsSettledAsync();

        Assert.Same(before, session.Tools);
        Assert.Empty(session.TakeNotices());
    }

    [Fact]
    public async Task NoticesInABurst_AreCoalesced_OneListingInFlight_AndOneMoreAfterIt()
    {
        await using var session = await ConnectedAsync();
        var server = _servers.Server("docker");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ListingGate = held.Task;

        await server.NotifyToolsChangedAsync([("echo", "1")]);
        await Until(() => server.Listings == 2, "the first background listing");
        await server.NotifyToolsChangedAsync([("echo", "2")]);
        await server.NotifyToolsChangedAsync([("echo", "3")]);
        Assert.Equal(Echo("x"), await CallAsync(Tool(session, "docker__echo")));   // a round trip: the client has read both notices
        held.SetResult();
        await Until(() => server.Listings >= 3, "the owed listing");
        await session.ListingsSettledAsync();

        Assert.Equal(3, server.Listings);   // the connect's, the first notice's, one for the other two
        Assert.Equal("3", session.Tools.Single().Description);
    }

    [Fact]
    public async Task TheAppsOwnStops_AreNotDrops()
    {
        await using var session = await ConnectedAsync(Two);

        await session.DisconnectServerAsync("chrome");
        ProfileFile("""{ "mcpServers": { "docker": { "command": "docker", "args": ["x"] }, "chrome": { "command": "chrome" } } }""");
        await session.ReloadAsync(_settings.Current, CancellationToken.None);
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        await Task.Delay(100);

        Assert.All(session.Servers, s => Assert.Null(s.Dropped));
        Assert.Empty(session.TakeNotices());
        int requested = _servers.Requested.Count;
        await session.DisposeAsync();
        Assert.Empty(session.TakeNotices());
        Assert.Equal(requested, _servers.Requested.Count);
    }

    [Fact]
    public async Task AListingInFlightAcrossAConnectAll_AppliesNothing()
    {
        await using var session = await ConnectedAsync();
        var server = _servers.Server("docker");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ListingGate = held.Task;
        await server.NotifyToolsChangedAsync([("other", "Other.")]);
        await Until(() => server.Listings == 2, "the background listing");

        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);   // a profile switch's
        held.SetResult();
        await session.ListingsSettledAsync();

        Assert.Equal(["docker__echo", "docker__fail"], session.Tools.Select(t => t.Name));
        Assert.Empty(session.TakeNotices());
    }

    [Fact]
    public async Task AReloadAfterADrop_StartsThatServerAgain_ItsMarkCleared()
    {
        await using var session = await ConnectedAsync();
        var before = _servers.Server("docker");
        await DropAsync(session);

        Assert.Equal((1, 1, 0), await session.ReloadAsync(_settings.Current, CancellationToken.None));

        Assert.NotSame(before, _servers.Server("docker"));
        Assert.Null(Row(session).Dropped);
        Assert.Equal(McpState.Connected, Row(session).State);
    }

    [Fact]
    public async Task AServerThatDropsWhileAnotherStillConnects_KeepsItsMark()
    {
        ProfileFile(Two);
        var gate = _servers.Held("chrome");
        await using var session = Session();

        var wave = session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        await Until(() => Row(session).State == McpState.Connected, "docker connected");
        await DropAsync(session);
        gate.SetResult();
        await wave;

        Assert.Equal(McpState.Connected, Row(session, "chrome").State);
        Assert.NotNull(Row(session).Dropped);
    }

    [Fact]
    public async Task TheCallsOwnCancel_AndAServersErrorAnswers_AreNotDrops()
    {
        _servers.Tools("docker", ("echo", "Echoes."), (McpPipeServer.FailToolName, "Fails."), (McpPipeServer.ThrowToolName, "Throws."));
        await using var session = await ConnectedAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CallAsync(Tool(session, "docker__echo"), cancellationToken: cancelled.Token));
        Assert.StartsWith(McpText.ErrorPrefix, await CallAsync(Tool(session, "docker__fail")), StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<ModelContextProtocol.McpException>(() => CallAsync(Tool(session, "docker__throw")));

        Assert.Single(_servers.Requested);
        Assert.Null(Row(session).Dropped);
        Assert.Empty(session.TakeNotices());
    }

    [Fact]
    public async Task DisposeDuringAReconnectToAHangingServer_ReturnsAtOnce_AndTheCallSaysStopped()
    {
        var session = await ConnectedAsync();
        var echo = Tool(session, "docker__echo");
        await DropAsync(session);
        _servers.Hanging.Add("docker");
        var call = CallAsync(echo);
        await Until(() => _servers.Requested.Count == 2, "the reconnect");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await session.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), stopwatch.Elapsed.ToString());
        Assert.Equal(McpText.ErrorPrefix + McpText.StoppedError("docker"), await call);
        Assert.Empty(session.TakeNotices());
    }

    [Fact]
    public async Task ASecondDispose_DoesntThrow_AndACallAfterIt_SaysStopped()
    {
        var session = await ConnectedAsync();
        var echo = Tool(session, "docker__echo");

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(McpText.ErrorPrefix + McpText.StoppedError("docker"), await CallAsync(echo));
        Assert.Single(_servers.Requested);
    }

    [Fact]
    public async Task TheCallsOwnCancelDuringAReconnect_IsRethrown_TheRowStaysDropped_AndTheNextCallReconnects()
    {
        await using var session = await ConnectedAsync();
        var echo = Tool(session, "docker__echo");
        await DropAsync(session);
        _servers.Hanging.Add("docker");
        using var cts = new CancellationTokenSource();
        var call = CallAsync(echo, cancellationToken: cts.Token);
        await Until(() => _servers.Requested.Count == 2, "the reconnect");

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(McpState.Connected, Row(session).State);
        Assert.NotNull(Row(session).Dropped);
        Assert.Empty(session.TakeNotices());

        _servers.Hanging.Clear();
        Assert.Equal(Echo("x"), await CallAsync(echo));
        Assert.Equal(3, _servers.Requested.Count);
    }

    [Fact]
    public async Task ThroughTheTurnLoopsInvoke_AFailureIsAnErrorResult_NeverAThrow()
    {
        await using var session = await ConnectedAsync();
        var echo = Tool(session, "docker__echo");
        await DropAsync(session);
        _servers.Failing.Add("docker");
        var log = new System.Collections.Concurrent.ConcurrentQueue<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = log.Enqueue;
        DiagnosticLog.Emitted += capture;
        ToolInvocation result;
        try
        {
            var call = new FunctionCallContent("c1", echo.Name, new Dictionary<string, object?> { ["text"] = "x" });
            result = await Assistant.InvokeToolWithDiffAsync([echo], call, CancellationToken.None);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.StartsWith(McpText.ErrorPrefix + "docker dropped and didn't come back: ", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(log, e => e.Message.Contains("threw", StringComparison.Ordinal));
        Assert.Contains(log, e => e.Level == DiagnosticLevel.Info && e.Message == McpText.ReconnectFailedLogLine("docker", "no such command: docker"));
    }
}
