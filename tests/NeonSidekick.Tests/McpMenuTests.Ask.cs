using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Mcp;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The Servers tab's <c>a</c> (2026-11-20, plan idempotent-mixing-ripple): a server's ask picked, written into its own <c>mcp.json</c> and
/// applied by the reload in place; a typo'd ask mended from its skipped line; the refusals.
/// </summary>
public partial class McpMenuTests
{
    private string ProfileFile => McpConfigFile.ProfilePath(_settings.ProfileDirectory);

    private McpServerStatus Server(string name, McpScope scope = McpScope.Profile) => _session.Servers.Single(s => s.Name == name && s.Scope == scope);

    [Fact]
    public async Task A_PicksAlways_WritesTheProfilesFile_AppliesItInPlace_AndLogs()
    {
        await ConnectedAsync();
        string? heldBefore = _session.AllowKeyFor("docker__echo");
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('a'), Keys.Down, Keys.Enter);   // the cursor starts on writes, the file's
        Push(Keys.Escape);
        var lines = new List<string>();
        Action<DiagnosticEvent> capture = e =>
        {
            lock (lines)
            {
                lines.Add(e.Message);
            }
        };
        DiagnosticLog.Emitted += capture;
        try
        {
            await menu.ShowAsync(CancellationToken.None);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Contains("\"ask\": \"always\"", File.ReadAllText(ProfileFile), StringComparison.Ordinal);
        Assert.Contains(McpText.AskPickerTitle("docker"), Output);
        Assert.Contains(McpText.AskModeMeanings[2], Output);
        Assert.Contains(McpText.AskSetNotice("docker", McpAskMode.Always, global: false, later: false), Output);
        Assert.Equal(McpAskMode.Always, Server("docker").Ask);
        Assert.Equal(McpState.Connected, Server("docker").State);
        Assert.Equal(2, _servers.Requested.Count);   // in place: no reconnect
        // An "Allow for this session" lapses with it: the screen holds allows by this key, which carries the server's whole settings.
        Assert.NotNull(heldBefore);
        Assert.NotEqual(heldBefore, _session.AllowKeyFor("docker__echo"));
        lock (lines)
        {
            Assert.Contains(McpText.AskSetLogLine("docker", McpAskMode.Always, ProfileFile), lines);
        }

        pane.Dispose();
    }

    [Fact]
    public async Task A_OnTheModeItHas_WritesNothing_AndEscWritesNothing()
    {
        await ConnectedAsync();
        byte[] before = File.ReadAllBytes(ProfileFile);
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('a'), Keys.Enter);              // writes, which it has
        Push(Keys.Char('a'), Keys.Down, Keys.Escape);  // back, nothing chosen
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(McpText.AskUnchangedNotice("docker", McpAskMode.Writes), Output);
        Assert.Equal(before, File.ReadAllBytes(ProfileFile));
        Assert.True(Server("docker").Enabled);   // `a` is no flip
        Assert.Empty(_settings.Current.McpServersDisabled);
        pane.Dispose();
    }

    [Fact]
    public async Task A_MidTurn_IsRefused_AndOnAShadowedRow_SaysToSetTheProfilesEntry()
    {
        string global = McpConfigFile.GlobalPath(_settings.StorageDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(global)!);
        File.WriteAllText(global, """{ "mcpServers": { "chrome": { "url": "http://localhost:9/mcp" } } }""");
        await ConnectedAsync();
        byte[] profileBefore = File.ReadAllBytes(ProfileFile);
        byte[] globalBefore = File.ReadAllBytes(global);
        Assert.NotNull(Server("chrome", McpScope.Global).ShadowedBy);

        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('a'), Keys.Escape);
        await menu.ShowAsync(CancellationToken.None, midTurn: true);
        Assert.Contains(SettingsMenu.NotWhileReplyRunsNotice, Output);

        Push(Keys.Down, Keys.Down, Keys.Char('a'), Keys.Escape);   // docker, chrome, chrome (global)
        await menu.ShowAsync(CancellationToken.None);
        Assert.Contains(McpText.AskShadowedNotice("chrome"), Output);
        Assert.DoesNotContain(McpText.AskPickerTitle("chrome"), Output);

        Assert.Equal(profileBefore, File.ReadAllBytes(ProfileFile));
        Assert.Equal(globalBefore, File.ReadAllBytes(global));
        pane.Dispose();
    }

    [Fact]
    public async Task A_WithTheMasterSwitchOff_OnlyWrites_AndSaysItAppliesAtTheNextConnect()
    {
        await ConnectedAsync();
        _settings.Update(d => d.McpServers = false);
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('a'), Keys.Down, Keys.Down, Keys.Enter);   // never
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\"ask\": \"never\"", File.ReadAllText(ProfileFile), StringComparison.Ordinal);
        Assert.Contains(McpText.AskSetNotice("docker", McpAskMode.Never, global: false, later: true), Output);
        Assert.Equal(McpAskMode.Writes, Server("docker").Ask);   // nothing reloaded
        Assert.Equal(2, _servers.Requested.Count);
        pane.Dispose();
    }

    [Fact]
    public async Task ATypoInAServersAsk_IsMendedFromItsSkippedLine_AndTheServerComesBack()
    {
        await ConnectedAsync("""{ "mcpServers": { "docker": { "command": "docker", "args": ["mcp", "gateway", "run"] }, "chrome": { "url": "http://localhost:9/mcp", "ask": "alwayss" } } }""");
        var problem = Assert.Single(_session.Problems);
        Assert.Equal("chrome", problem.Name);
        Assert.Equal(ProfileFile, problem.Path);
        var row = Assert.Single(McpRows.ServerRows(Facts()), r => r.Row is McpRow.BadAsk);
        Assert.Equal(new MenuFooter(McpText.BadAskFixFooter), McpRows.ServerFooter(Facts(), row.Row));

        var (menu, pane, _) = PaneMenu();
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // the two edit rows, reload, the skipped line: Enter does nothing there
        Push(Keys.Char('a'), Keys.Down, Keys.Enter); // always
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\"ask\": \"always\"", File.ReadAllText(ProfileFile), StringComparison.Ordinal);
        Assert.DoesNotContain("alwayss", File.ReadAllText(ProfileFile), StringComparison.Ordinal);
        Assert.Empty(_session.Problems);
        Assert.Equal(McpState.Connected, Server("chrome").State);
        Assert.Equal(McpAskMode.Always, Server("chrome").Ask);
        Assert.DoesNotContain(McpText.OffNotice, Output);
        pane.Dispose();
    }
}
