using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

// ── The mcp-ask review's fixes (2026-11-20) over the session's gate ──────────────

public partial class McpSessionAskTests
{
    private static string Prefixed(string tool) => McpToolName.Prefixed("docker", tool);

    private static List<string> AuditLines(List<string> lines, string name)
    {
        lock (lines)
        {
            return lines.Where(l => l.StartsWith(name + ": ", StringComparison.Ordinal)).ToList();
        }
    }

    /// <summary>#121, #15: the audit line says how the call went after the decision: ran, error, not sent, dropped.</summary>
    [Fact]
    public async Task TheAuditLine_SaysTheOutcome_RanErrorNotSentAndDropped()
    {
        _servers.Tools("docker", ("echo", "Echoes."), (McpPipeServer.FailToolName, "Fails."), (McpPipeServer.DropToolName, "Drops."));
        var approver = new McpApprovers.Recording(new McpVerdict.Allowed(McpText.AllowedOnce), new McpVerdict.Allowed(McpText.AllowedOnce), McpVerdict.No);
        var lines = Capture(out var handler);
        try
        {
            await using var session = await ConnectedAsync(One(), approver.Approve);
            await CallAsync(Tool(session, "docker__echo"), "a");
            await CallAsync(Tool(session, Prefixed(McpPipeServer.FailToolName)), "b");
            await CallAsync(Tool(session, "docker__echo"), "c");
            string dropped = await CallAsync(Tool(session, Prefixed(McpPipeServer.DropToolName)), "d");   // allowed once, sent, cut off
            Assert.Equal(McpText.ErrorPrefix + McpText.DroppedNotResentError("docker"), dropped);
        }
        finally
        {
            DiagnosticLog.Emitted -= handler;
        }

        var echo = AuditLines(lines, "docker__echo");
        Assert.Equal(2, echo.Count);
        Assert.StartsWith(McpText.AuditPrefix("docker__echo", McpText.AllowedOnce) + McpText.OutcomeRan + " · ", echo[0]);
        Assert.StartsWith(McpText.AuditPrefix("docker__echo", McpText.DeniedWord) + McpText.OutcomeNotSent + " · ", echo[1]);
        Assert.StartsWith(McpText.AuditPrefix(Prefixed(McpPipeServer.FailToolName), McpText.AllowedOnce) + McpText.OutcomeError + " · ",
            Assert.Single(AuditLines(lines, Prefixed(McpPipeServer.FailToolName))));
        Assert.StartsWith(McpText.AuditPrefix(Prefixed(McpPipeServer.DropToolName), McpText.AllowedOnce) + McpText.OutcomeDropped + " · ",
            Assert.Single(AuditLines(lines, Prefixed(McpPipeServer.DropToolName))));
    }

    /// <summary>#93: an argument named like a secret is redacted by its key, as is a bearer token in any value.</summary>
    [Fact]
    public async Task TheAuditLine_RedactsBySecretKey_AndABearerValue()
    {
        var lines = Capture(out var handler);
        try
        {
            await using var session = await ConnectedAsync(One(), McpApprovers.AllowAll);
            await CallAsync(Tool(session, "docker__echo"), "hi", new Dictionary<string, object?>
            {
                ["token"] = "tok-1234567890",
                ["github_token"] = "ghp_abcdefghijk",
                ["header"] = "Authorization: Bearer sk-abcdefghijkl",
                ["nested"] = new Dictionary<string, object?> { ["apiKey"] = "nested-secret-value" },
            });
        }
        finally
        {
            DiagnosticLog.Emitted -= handler;
        }

        string audit = Assert.Single(AuditLines(lines, "docker__echo"));
        Assert.Contains("\"text\":\"hi\"", audit);
        Assert.Contains(ToolRedaction.Placeholder, audit);
        Assert.DoesNotContain("tok-1234567890", audit);
        Assert.DoesNotContain("ghp_abcdefghijk", audit);
        Assert.DoesNotContain("sk-abcdefghijkl", audit);
        Assert.DoesNotContain("nested-secret-value", audit);
    }

    /// <summary>#23: a stop while the pane waits is a no in the audit (<c>denied, canceled</c>), and the stop goes on to the turn.</summary>
    [Fact]
    public async Task AStopOnThePane_IsAuditedDenied_AndThrows()
    {
        var lines = Capture(out var handler);
        try
        {
            await using var session = await ConnectedAsync(One(), (_, _) => throw new OperationCanceledException());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CallAsync(Tool(session, "docker__echo")));
        }
        finally
        {
            DiagnosticLog.Emitted -= handler;
        }

        Assert.StartsWith(McpText.AuditPrefix("docker__echo", McpText.DeniedWord) + McpText.OutcomeCanceled + " · ", Assert.Single(AuditLines(lines, "docker__echo")));
        Assert.Empty(_servers.Server("docker").Calls);
    }

    /// <summary>#4: a reload that changes the server while the pane is open sends nothing: the yes was to the server as it was.</summary>
    [Fact]
    public async Task ASettingsChangeWhileThePaneWaits_SendsNothing()
    {
        var approver = new McpApprovers.Recording();
        await using var session = await ConnectedAsync(One(), approver.Approve);
        var first = _servers.Server("docker");
        approver.Before = async _ =>
        {
            approver.Before = null;
            ProfileFile("""{ "mcpServers": { "docker": { "command": "docker-two" } } }""");
            await session.ReloadAsync(_settings.Current, CancellationToken.None);
        };

        string answer = await CallAsync(Tool(session, "docker__echo"));

        Assert.Equal(McpText.ErrorPrefix + McpText.ChangedWhileAsking("docker"), answer);
        Assert.Empty(first.Calls);
        Assert.Empty(_servers.Server("docker").Calls);
        Assert.Equal("docker-two", _servers.Requested[^1].Config.Command);
    }

    /// <summary>#3, #51, #116: the ask carries the connection's fingerprint and the tool's definition, so a changed ask or tool asks again.</summary>
    [Fact]
    public async Task TheAsk_CarriesTheFingerprintAndTheDefinition_EachChangedByWhatItKeys()
    {
        var approver = new McpApprovers.Recording();
        await using var session = await ConnectedAsync(One(), approver.Approve);
        await CallAsync(Tool(session, "docker__echo"));

        ProfileFile(One("always"));
        await session.ReloadAsync(_settings.Current, CancellationToken.None);
        await CallAsync(Tool(session, "docker__echo"));

        await _servers.Server("docker").NotifyToolsChangedAsync([("echo", "Echoes, redefined."), (McpPipeServer.FailToolName, "Always fails.")]);
        await Until(() => Tool(session, "docker__echo").Description == "Echoes, redefined.", "the new list");
        await CallAsync(Tool(session, "docker__echo"));

        var asks = approver.Asks;
        Assert.Equal(3, asks.Count);
        Assert.All(asks, a => Assert.NotEqual("", a.Fingerprint));
        Assert.All(asks, a => Assert.NotEqual("", a.Definition));
        Assert.NotEqual(asks[0].Fingerprint, asks[1].Fingerprint);   // the ask changed
        Assert.Equal(asks[0].Definition, asks[1].Definition);
        Assert.Equal(asks[1].Fingerprint, asks[2].Fingerprint);
        Assert.NotEqual(asks[1].Definition, asks[2].Definition);     // the tool changed
        string Key(McpAsk a) => McpSession.McpAllowKey(a.Server, a.ServerTool, a.Fingerprint, a.Definition);
        Assert.Equal(3, asks.Select(Key).Distinct().Count());
        Assert.Equal(Key(asks[2]), session.AllowKeyFor("docker__echo"));
    }

    /// <summary>#87: a list that changes only a tool's hint is applied, so the next call asks.</summary>
    [Fact]
    public async Task AHintOnlyChange_IsApplied_AndTheNextCallAsks()
    {
        _servers.ReadOnly("docker", "echo");
        var approver = new McpApprovers.Recording();
        await using var session = await ConnectedAsync(One(), approver.Approve);
        await CallAsync(Tool(session, "docker__echo"));
        Assert.Empty(approver.Asks);

        var server = _servers.Server("docker");
        await server.NotifyToolsChangedAsync(server.Tools, readOnly: new HashSet<string>(StringComparer.Ordinal));
        await Until(() => session.AskingNames().Contains("docker__echo"), "the hint change applied");

        await CallAsync(Tool(session, "docker__echo"));
        Assert.Equal("docker__echo", Assert.Single(approver.Asks).Name);
    }

    /// <summary>#14: a read-only call cut off by a drop isn't sent again when the server comes back listing the tool as a write.</summary>
    [Fact]
    public async Task ADroppedReadOnlyCall_IsNotResent_WhenTheServerComesBackCallingItAWrite()
    {
        _servers.Tools("docker", (McpPipeServer.DropToolName, "Drops."));
        _servers.ReadOnly("docker", McpPipeServer.DropToolName);
        var approver = new McpApprovers.Recording();
        await using var session = await ConnectedAsync(One(), approver.Approve);
        _servers.Steady.Add("docker");     // the server that comes back answers…
        _servers.ReadOnly("docker");       // …and no longer marks the tool read-only

        string answer = await CallAsync(Tool(session, Prefixed(McpPipeServer.DropToolName)), "y");

        Assert.Equal(McpText.ErrorPrefix + McpText.DroppedNotResentError("docker"), answer);
        Assert.Empty(approver.Asks);
        Assert.Equal(2, _servers.Requested.Count);
        Assert.Empty(_servers.Server("docker").Calls);
    }

    /// <summary>#54: a server reconnected for the call that then doesn't come back after the pane gets the not-back notice alone.</summary>
    [Fact]
    public async Task AServerThatDoesntComeBackAfterThePane_GetsOnlyTheNotBackNotice()
    {
        var approver = new McpApprovers.Recording();
        await using var session = await ConnectedAsync(One(), approver.Approve);
        await _servers.Drop("docker");
        await Until(() => Row(session).Dropped is not null, "dropped");
        approver.Before = async _ =>
        {
            approver.Before = null;
            _servers.Failing.Add("docker");
            await _servers.Drop("docker");
            await Until(() => Row(session).Dropped is not null || Row(session).State != McpState.Connected, "dropped again");
        };

        string answer = await CallAsync(Tool(session, "docker__echo"));

        Assert.StartsWith(McpText.ErrorPrefix, answer);
        var notices = session.TakeNotices();
        Assert.DoesNotContain(notices, n => n.Text == McpText.ReconnectedNotice("docker", ""));
        Assert.Contains(notices, n => n.Error);
    }

    /// <summary>
    /// #72: <c>${VAR}</c> is expanded at connect from the child environment, a default taken; an unset name goes as written with one Warn a
    /// session (a reconnect says nothing more), the server still starts, and the catalog keeps the text as written.
    /// </summary>
    [Fact]
    public async Task AReference_IsExpandedAtConnect_AnUnsetOneWarnsOnce_AndTheServerStillStarts()
    {
        const string unset = "NEON_REF_SURELY_UNSET_9F2C";
        ProfileFile("{ \"mcpServers\": { \"docker\": { \"command\": \"docker\", \"args\": [\"${" + unset + "}\", \"${" + unset + ":-dflt}\", \"${PATH}\"] } } }");
        var lines = Capture(out var handler);
        try
        {
            await using var session = new McpSession(_settings, _servers.Transport, TimeProvider.System) { Approver = McpApprovers.AllowAll };
            await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
            Assert.Equal(McpState.Connected, Row(session).State);
            Assert.Equal(["${" + unset + "}", "dflt", Environment.GetEnvironmentVariable("PATH")!], _servers.Requested[0].Config.Args);

            await _servers.Drop("docker");
            await Until(() => Row(session).Dropped is not null, "dropped");
            await CallAsync(Tool(session, "docker__echo"));   // reconnects first: the references are expanded again
            Assert.Equal(2, _servers.Requested.Count);
            Assert.Equal("${" + unset + "}", Assert.Single(session.Read(seal: false).Entries).Config.Args![0]);
        }
        finally
        {
            DiagnosticLog.Emitted -= handler;
        }

        lock (lines)
        {
            Assert.Single(lines, l => l == McpText.UnsetReferenceLogLine("docker", "args", unset));
        }
    }

    /// <summary>#1: a reload that shadows a connected home server with the profile's keeps one connected row, the profile's.</summary>
    [Fact]
    public async Task AReloadThatShadowsAConnectedHomeServer_KeepsOneConnectedRow()
    {
        string global = McpConfigFile.GlobalPath(_settings.StorageDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(global)!);
        File.WriteAllText(global, One());
        await using var session = new McpSession(_settings, _servers.Transport, TimeProvider.System);
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        Assert.Equal(McpState.Connected, Assert.Single(session.Servers).State);

        ProfileFile("""{ "mcpServers": { "docker": { "command": "docker-profile" } } }""");
        await session.ReloadAsync(_settings.Current, CancellationToken.None);

        var rows = session.Servers.Where(s => s.Name == "docker").ToList();
        Assert.Equal(2, rows.Count);
        var connected = Assert.Single(rows, r => r.State == McpState.Connected);
        Assert.Equal(McpScope.Profile, connected.Scope);
        Assert.NotNull(Assert.Single(rows, r => r.Scope == McpScope.Global).ShadowedBy);
        Assert.Equal("docker-profile", _servers.Requested[^1].Config.Command);
    }
}
