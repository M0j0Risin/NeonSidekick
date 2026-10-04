using NeonSidekick.App;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>The <c>/mcp</c> pane's rows (2026-09-20), pure over the facts.</summary>
public class McpRowsTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings;
    private readonly InProcessMcpServers _servers = new();

    public McpRowsTests()
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

    /// <summary>A connected session over the profile file <paramref name="json"/>: docker (echo + fail) and chrome (navigate) unless the file says otherwise.</summary>
    private async Task<McpSession> ConnectedAsync(string json, params string[] failing)
    {
        File.WriteAllText(McpConfigFile.ProfilePath(_settings.ProfileDirectory), json);
        _servers.Tools("chrome", ("navigate", "Opens a page."));
        foreach (string name in failing)
        {
            _servers.Failing.Add(name);
        }

        var session = new McpSession(_settings, _servers.Transport);
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        return session;
    }

    private McpFacts Facts(McpSession session, bool enabled = true, bool toolsEnabled = true, IReadOnlyList<string>? disabled = null) =>
        new(session.Servers, session.Problems, enabled, toolsEnabled, ToolsText.DisabledSet(disabled ?? []), session.ProfilePath, session.GlobalPath);

    private const string Two = """{ "mcpServers": { "docker": { "command": "docker", "args": ["mcp", "gateway", "run"] }, "chrome": { "url": "http://localhost:9/mcp" } } }""";

    /// <summary>The markup rendered to plain text at a wide console.</summary>
    private static string Plain(string markup)
    {
        using var console = new TestConsole();
        console.Profile.Width = 400;
        console.Write(new Markup(markup));
        return console.Output.TrimEnd('\n');
    }

    [Fact]
    public void Strings_ArePinned()
    {
        Assert.Equal("🔌 MCP", McpText.Label);
        Assert.Equal(["Servers", "Tools", "Options"], McpText.TabTitles);
        Assert.Equal("🔌 MCP servers is off: switch it on under the Options tab first", McpText.OffNotice);
        Assert.Equal("connected · 14 tools", McpText.StatusConnected(14));
        Assert.Equal("failed: boom", McpText.StatusFailed("boom"));
        Assert.Equal("🔌 docker: on", McpText.ServerFlippedNotice("docker", true));
        Assert.Equal("🔌 docker: off", McpText.ServerFlippedNotice("docker", false));
        Assert.Equal("🔌 connecting docker…", McpText.ConnectingNotice("docker"));
        Assert.Equal("🔌 docker: connected, 3 tools", McpText.ConnectedNotice("docker", 3));
        Assert.Equal("docker failed: boom", McpText.FailedNotice("docker", "boom"));
        Assert.Equal("🔌 docker: stopped", McpText.StoppedNotice("docker"));
        Assert.Equal("🔌 reloaded: 1 added, 0 removed, 2 kept", McpText.ReloadedNotice(1, 0, 2));
        Assert.Equal("🔌 opened the profile's mcp.json", McpText.EditingNotice(profile: true));
        Assert.Equal("🔌 opened the global mcp.json", McpText.EditingNotice(profile: false));
        Assert.Equal(@"could not open C:\x\mcp.json: denied", McpText.EditFailedError(@"C:\x\mcp.json", "denied"));
        Assert.Equal(2, McpMenu.OptionsTab);
        Assert.Equal(40, McpRows.MaxNameWidth);
        Assert.Equal(12, McpRows.MinNameWidth);
        Assert.Equal(5, McpRows.StateWidth);
        Assert.Equal(12, McpRows.NameWidth(["docker"]));
        Assert.Equal(16, McpRows.NameWidth(["docker__echo_1"]));
        Assert.Equal(40, McpRows.NameWidth([new string('n', 60)]));
        Assert.Equal(12, McpRows.NameWidth([]));
    }

    [Fact]
    public async Task ServerRows_OneRowPerServer_ThenTheThreeActionRows()
    {
        await using var session = await ConnectedAsync(Two, "chrome");
        var rows = McpRows.ServerRows(Facts(session));

        Assert.Equal(5, rows.Count);
        Assert.Equal("docker      on   connected · 2 tools  stdio: docker mcp gateway run", Plain(rows[0].Markup));
        Assert.Equal("chrome      on   failed: no such command: http://localhost:9/mcp  http: http://localhost:9/mcp", Plain(rows[1].Markup));
        Assert.Equal(new McpRow.Server("docker"), rows[0].Row);
        Assert.Equal(new McpRow.Server("chrome"), rows[1].Row);
        Assert.Equal("edit profile mcp.json", Plain(rows[2].Markup));
        Assert.Equal(new McpRow.EditProfile(), rows[2].Row);
        Assert.Equal(new McpRow.EditGlobal(), rows[3].Row);
        Assert.Equal("reload", Plain(rows[4].Markup));
        Assert.Equal(new McpRow.Reload(), rows[4].Row);
        Assert.Equal(0, McpRows.FirstServerRow(rows));
        Assert.Contains(Theme.AccentSecondary.ToMarkup(), rows[0].Markup);
    }

    [Fact]
    public async Task ServerRows_OffLines_ShadowedAndGlobal_Problems()
    {
        File.WriteAllText(McpConfigFile.GlobalPath(_settings.StorageDirectory), """{ "mcpServers": { "docker": { "command": "g" }, "gw": { "url": "http://h/mcp" }, "bad": {} } }""");
        _settings.Update(d => d.McpServersDisabled = ["gw"]);
        await using var session = await ConnectedAsync("""{ "mcpServers": { "docker": { "command": "docker" } } }""");
        var rows = McpRows.ServerRows(Facts(session, enabled: false, toolsEnabled: false));

        Assert.Equal(ToolsText.OffLine, Plain(rows[0].Markup));
        Assert.Equal(McpText.OffLine, Plain(rows[1].Markup));
        Assert.Null(rows[0].Row);
        Assert.Null(rows[1].Row);
        Assert.Equal("docker      on   connected · 2 tools  stdio: docker", Plain(rows[2].Markup));
        Assert.Equal("docker      on   shadowed by the profile's (global)", Plain(rows[3].Markup));
        Assert.Equal("gw          off  off (global)  http: http://h/mcp", Plain(rows[4].Markup));
        Assert.Equal(2, McpRows.FirstServerRow(rows));
        // The master switch off: the two edit rows stand, the reload row is left out (later on 2026-09-20).
        Assert.Equal(new McpRow.EditProfile(), rows[5].Row);
        Assert.Equal(new McpRow.EditGlobal(), rows[6].Row);
        Assert.DoesNotContain(rows, r => r.Row is McpRow.Reload);
        Assert.Equal("Skipped:", Plain(rows[7].Markup));
        Assert.EndsWith(" › bad: names neither a command nor a url", Plain(rows[8].Markup));
        Assert.Null(rows[8].Row);
        Assert.Equal(9, rows.Count);
        // Every row dim while the switches are off: the whole row in one dim span, no label colour.
        Assert.DoesNotContain(Theme.AccentSecondary.ToMarkup(), rows[2].Markup);

        // LLM offer tools off alone: the reload row stays (the master switch is on).
        Assert.Contains(McpRows.ServerRows(Facts(session, toolsEnabled: false)), r => r.Row is McpRow.Reload);
    }

    [Fact]
    public async Task ServerRows_NothingConfigured_SaysSo_TheActionRowsStand()
    {
        await using var session = new McpSession(_settings, _servers.Transport);
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        var rows = McpRows.ServerRows(Facts(session));

        Assert.Equal([McpText.NoServersLine, "edit profile mcp.json", "edit global mcp.json", "reload"], rows.Select(r => Plain(r.Markup)));
        Assert.Equal(1, McpRows.FirstServerRow(rows));   // the first row that does something
    }

    [Fact]
    public async Task ToolRows_AHeadingPerConnectedServer_TheDisabledCounted_FlipByPrefixedName()
    {
        await using var session = await ConnectedAsync(Two);
        var rows = McpRows.ToolRows(Facts(session, disabled: ["docker__fail", "read_file"]));

        Assert.Equal("── docker · 1 of 2", Plain(rows[0].Markup));   // a rule heading (2026-10-03), the /tools look
        Assert.Null(rows[0].Tool);
        Assert.True(rows[0].Heading);
        Assert.Equal("docker__echo      on   Echoes the text back.", Plain(rows[1].Markup));
        Assert.Equal("docker__fail      off  Always fails.", Plain(rows[2].Markup));
        Assert.Equal(("", (string?)null, false), rows[3]);   // the gap before the second server
        Assert.Equal("── chrome · 1", Plain(rows[4].Markup));
        Assert.Equal("chrome__navigate  on   Opens a page.", Plain(rows[5].Markup));
        Assert.Equal([0, 4], ToolsText.HeadingRows(rows).Order());
        Assert.Equal(["docker__echo", "docker__fail", "chrome__navigate"], rows.Where(r => r.Tool is not null).Select(r => r.Tool));
        Assert.Equal(1, ToolsText.FirstToolRow(rows));
        Assert.Contains(Theme.SectionHeading.ToMarkup(), rows[0].Markup);
        Assert.DoesNotContain(Theme.AccentSecondary.ToMarkup(), rows[2].Markup);   // off: dim whole
    }

    [Fact]
    public async Task ToolRows_UnderAFilter_KeepTheMatches_AServerWithNoneGoes()
    {
        // 2026-10-03 (the user's ask): /tools' Offered shape — the name or the description, case folded; the heading counts the whole server.
        await using var session = await ConnectedAsync(Two);
        var facts = Facts(session, disabled: ["docker__fail"]);

        var rows = McpRows.ToolRows(facts, "PAGE");   // chrome__navigate's description
        Assert.Equal(["── chrome · 1", "chrome__navigate  on   Opens a page."], rows.Select(r => Plain(r.Markup)));
        Assert.Equal([0], ToolsText.HeadingRows(rows));

        rows = McpRows.ToolRows(facts, "docker__");
        Assert.Equal(["── docker · 1 of 2", "docker__echo      on   Echoes the text back.", "docker__fail      off  Always fails."], rows.Select(r => Plain(r.Markup)));

        Assert.Equal([MenuFilter.NoMatchLine("zz")], McpRows.ToolRows(facts, "zz").Select(r => Plain(r.Markup)));
        Assert.Equal(McpRows.ToolRows(facts), McpRows.ToolRows(facts, ""));
    }

    [Fact]
    public async Task ToolRows_NoneConnected_OrTheSwitchesOff()
    {
        await using var session = await ConnectedAsync(Two, "docker", "chrome");
        Assert.Equal([McpText.NoToolsLine], McpRows.ToolRows(Facts(session)).Select(r => Plain(r.Markup)));

        _servers.Failing.Clear();
        await using var live = await ConnectedAsync(Two);
        var rows = McpRows.ToolRows(Facts(live, enabled: false));
        Assert.Equal(McpText.OffLine, Plain(rows[0].Markup));
        Assert.Equal("── docker · 2", Plain(rows[1].Markup));
        Assert.Contains(Theme.SectionHeading.ToMarkup(), rows[1].Markup);   // the heading keeps its colour with the switch off (the /tools rule, later on 2026-09-20)
        Assert.DoesNotContain(Theme.AccentSecondary.ToMarkup(), rows[2].Markup);
    }

    [Fact]
    public async Task Lines_AreThePlainForm()
    {
        await using var session = await ConnectedAsync(Two, "chrome");
        var facts = Facts(session, disabled: ["docker__fail"]);

        Assert.Equal(
            [
                "docker      on   connected · 2 tools  stdio: docker mcp gateway run",
                "chrome      on   failed: no such command: http://localhost:9/mcp  http: http://localhost:9/mcp",
            ],
            McpRows.ServerLines(facts));
        Assert.Equal(
            [
                "docker (1 of 2)",
                "  docker__echo  on   Echoes the text back.",
                "  docker__fail  off  Always fails.",
            ],
            McpRows.ToolLines(facts));
        Assert.Equal([McpText.NoToolsLine], McpRows.ToolLines(Facts(await ConnectedAsync("""{ "mcpServers": {} }"""))));
    }
}
