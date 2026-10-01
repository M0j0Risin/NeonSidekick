using NeonSidekick.App;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>The <c>/mcp</c> pane (2026-09-20) over in-process pipe servers: the server flips that connect and disconnect, the tool flips, the edit rows, the reload, the Options tab, the refusals.</summary>
public class McpMenuTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly AppSettings _settings;
    private readonly ManualTimeProvider _time = new();
    private readonly FakeSynthesizer _synth = new();
    private readonly SpeechSession _speech;
    private readonly InProcessMcpServers _servers = new();
    private readonly McpSession _session;
    private readonly List<string> _opened = [];

    public McpMenuTests()
    {
        _console.Profile.Width = 100;
        _settings = new AppSettings(_dir);
        _settings.Update(d => { d.TtsOutput = true; d.TtsSource = "http"; d.ToolsDisabled = []; d.McpServers = true; });
        _speech = new SpeechSession(_ => _synth, _ => new FakeAudioPlayback(), new ModelStore(Path.Combine(_dir, "models"), new HttpClient(new StubHttpMessageHandler())));
        _servers.Tools("chrome", ("navigate", "Opens a page."));
        _session = new McpSession(_settings, _servers.Transport, _time);
    }

    private const string FakeBrowserPath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

    private const string Two = """{ "mcpServers": { "docker": { "command": "docker", "args": ["mcp", "gateway", "run"] }, "chrome": { "url": "http://localhost:9/mcp" } } }""";

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        await _servers.DisposeAsync();
        _speech.Dispose();
        _settings.Dispose();
        _console.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>The profile file written and the session connected, so the pane opens on live rows.</summary>
    private async Task ConnectedAsync(string json = Two)
    {
        File.WriteAllText(McpConfigFile.ProfilePath(_settings.ProfileDirectory), json);
        await _session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
    }

    private McpFacts Facts()
    {
        var effective = _settings.Current;
        return new McpFacts(_session.Servers, _session.Problems, effective.McpServers, effective.LlmOfferTools, ToolsText.DisabledSet(effective.ToolsDisabled), _session.ProfilePath, _session.GlobalPath);
    }

    private void Push(params ConsoleKeyInfo[] keys)
    {
        foreach (var key in keys)
        {
            _console.Input.PushKey(key);
        }
    }

    private (McpMenu Menu, ScreenPane Pane, SettingsMenu Settings) PaneMenu()
    {
        _console.Profile.Height = 40;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null), new ManualTimeProvider()) { Hint = () => "idle" };
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menuPane = new MenuPane(pane, keys);
        var settings = new SettingsMenu(new ConsoleWithInput(pane, keys), _settings, _ => null, new InputLine(pane, keys), new TranscriptRenderer(pane), _speech, menuPane, _ => FakeBrowserPath);
        var menu = new McpMenu(Facts, _session, _settings, settings, new TranscriptRenderer(pane), menuPane, _opened.Add, () => _settings.Current);
        pane.Show();
        return (menu, pane, settings);
    }

    private static string Rule(int width) => new(ScreenPane.RuleGlyph, width);

    /// <summary>A tab's rows, then the blank rows that hold every tab at its pane's tallest tab's height (2026-10-01), then the rule under the list.</summary>
    private void AssertTabEnds(string rows, int width) =>
        Assert.Matches(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(rows) + "(?: \n)*" + System.Text.RegularExpressions.Regex.Escape(Rule(width))), _console.Output);

    private string Titled(string row) => row + new string(' ', _console.Profile.Width - 2 - TextCells.Width(row)) + ScreenPane.CloseGlyph;

    /// <summary>The strip as the pane prints it. Pinned.</summary>
    private const string Strip = McpText.Label + "   Servers    Tools    Options ";

    [Fact]
    public async Task OnThePane_OpensOnTheFirstServer_TheThreeTabsOnTheStrip()
    {
        await ConnectedAsync();
        var (menu, pane, settings) = PaneMenu();
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Contains("\n" + Titled(Strip) + "\n \n▸ docker      on   connected · 2 tools  stdio: docker mcp gateway run\n  chrome      on   connected · 1 tool  http: http://localhost:9/mcp\n  edit profile mcp.json\n  edit global mcp.json\n  reload\n" + Rule(100), _console.Output);
        Assert.Contains(McpText.ServersKeys, _console.Output);
        Assert.Equal(SettingsMenu.Title, settings.Root);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_SpaceOnAServer_FlipsItOff_AndDisconnects_EnterFlipsItOn_AndConnects()
    {
        await ConnectedAsync();
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char(' '));                      // docker off
        Push(Keys.Enter);                          // docker on again
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Empty(_settings.Current.McpServersDisabled);
        // The status lines stack under the strip: the flip, then what the connect or disconnect did.
        Assert.Contains("\n" + Titled(Strip) + "\n  · 🔌 docker: off\n  · 🔌 docker: stopped\n▸ docker      off  off  stdio: docker mcp gateway run\n", _console.Output);
        Assert.Contains("\n" + Titled(Strip) + "\n  · 🔌 docker: on\n  · 🔌 connecting docker…\n  · 🔌 docker: connected, 2 tools\n▸ docker      on   connected · 2 tools  stdio: docker mcp gateway run\n", _console.Output);
        Assert.Equal(["docker", "chrome", "docker"], _servers.Requested.Select(r => r.Name));
        Assert.Equal(3, _session.Tools.Count);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_AFlipOff_IsSaved_AndStaysOffAtTheNextConnect()
    {
        await ConnectedAsync();
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Down, Keys.Enter);               // chrome off
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["chrome"], _settings.Current.McpServersDisabled);
        Assert.Contains("  · 🔌 chrome: stopped\n", _console.Output);
        await _session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        Assert.Equal([McpState.Connected, McpState.Off], _session.Servers.Select(s => s.State));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_EnterOnAFailedServer_Retries()
    {
        _servers.Failing.Add("chrome");
        await ConnectedAsync();
        Assert.Equal(McpState.Failed, _session.Servers[1].State);
        _servers.Failing.Clear();
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Down, Keys.Enter);               // chrome: retry, nothing saved
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Empty(_settings.Current.McpServersDisabled);
        Assert.Contains("  chrome      on   failed: no such command: http://localhost:9/mcp  http: http://localhost:9/mcp\n", _console.Output);
        Assert.Contains("  · 🔌 chrome: connected, 1 tool\n", _console.Output);
        Assert.Equal(McpState.Connected, _session.Servers[1].State);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_AFailedConnect_IsAnErrorOnTheStatusLine()
    {
        await ConnectedAsync();
        _settings.Update(d => d.McpServersDisabled = ["chrome"]);
        await _session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        _servers.Failing.Add("chrome");
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Down, Keys.Enter);               // chrome on: the connect fails
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Empty(_settings.Current.McpServersDisabled);
        Assert.Contains("  · 🔌 chrome: on\n  · 🔌 connecting chrome…\n  ✗ chrome failed: no such command: http://localhost:9/mcp\n  docker      on   connected · 2 tools  stdio: docker mcp gateway run\n▸ chrome      on   failed: no such command: http://localhost:9/mcp", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_MasterOff_EnterOrSpaceOnAServer_AnswersTheNotice_SavesNothing_NoReloadRow()
    {
        // Later on 2026-09-20 (the user's ask): with the master switch off the server rows are inert — the
        // Options tab is the way in — the reload row is left out, and the edit rows still work.
        await ConnectedAsync();
        _settings.Update(d => { d.McpServers = false; d.McpServersDisabled = ["docker"]; });
        await _session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Enter);                          // docker: the notice, nothing saved
        Push(Keys.Char(' '));                      // Space the same
        Push(Keys.Down, Keys.Down, Keys.Enter);    // past chrome: edit profile mcp.json still opens
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["docker"], _settings.Current.McpServersDisabled);
        Assert.Contains("  · " + McpText.OffNotice + "\n  " + McpText.OffLine + "\n▸ docker      off  off  stdio: docker mcp gateway run\n  chrome      on   off  http: http://localhost:9/mcp\n  edit profile mcp.json\n  edit global mcp.json\n" + Rule(100), _console.Output);
        Assert.Contains("  · " + McpText.OffNotice + "\n  " + McpText.OffLine + "\n  docker      off  off  stdio: docker mcp gateway run\n", _console.Output);   // Space's answer, then the cursor moved on under it
        Assert.DoesNotContain("docker: on", _console.Output);
        Assert.DoesNotContain("connecting docker", _console.Output);
        Assert.DoesNotContain("\n  reload\n", _console.Output);
        Assert.Equal([_session.ProfilePath], _opened);
        Assert.Equal(McpState.Off, _session.Servers[0].State);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheEditRows_MakeTheFileWhenMissing_AndOpenIt()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Enter);                          // nothing configured: the cursor opens on edit profile
        Push(Keys.Down, Keys.Enter);               // edit global
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([_session.ProfilePath, _session.GlobalPath], _opened);
        Assert.Equal(McpConfigFile.EmptyText, File.ReadAllText(_session.ProfilePath));
        Assert.Equal(McpConfigFile.EmptyText, File.ReadAllText(_session.GlobalPath));
        Assert.Contains("  · 🔌 opened the profile's mcp.json\n", _console.Output);
        Assert.Contains("  · 🔌 opened the global mcp.json\n  " + McpText.NoServersLine + "\n  edit profile mcp.json\n▸ edit global mcp.json\n  reload\n", _console.Output);
        Assert.Contains(McpText.NoServersLine, _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_Reload_ReadsTheFileAgain()
    {
        await ConnectedAsync();
        var (menu, pane, _) = PaneMenu();
        File.WriteAllText(McpConfigFile.ProfilePath(_settings.ProfileDirectory), """{ "mcpServers": { "docker": { "command": "docker", "args": ["mcp", "gateway", "run"] }, "fs": { "command": "fs" } } }""");
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // reload
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("  · 🔌 reloaded: 1 added, 1 removed, 1 kept\n", _console.Output);
        Assert.Equal(["docker", "fs"], _session.Servers.Select(s => s.Name));
        Assert.Contains("  fs          on   connected · 2 tools  stdio: fs\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheToolsTab_FlipsAToolIntoToolsDisabled_ByItsPrefixedName()
    {
        await ConnectedAsync();
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Right);                          // Tools: the cursor on docker__echo
        Push(Keys.Down, Keys.Enter);               // docker__fail off
        Push(Keys.Char(' '));                      // on again
        Push(Keys.Enter);                          // off again
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["docker__fail"], _settings.Current.ToolsDisabled);
        Assert.Contains("\n" + Titled(Strip) + "\n  · 🔌 docker__fail: off\n  docker (1 of 2)\n  docker__echo      on   Echoes the text back.\n▸ docker__fail      off  Always fails.\n  chrome (1)\n  chrome__navigate  on   Opens a page.\n" + Rule(100), _console.Output);
        Assert.Contains("  · 🔌 docker__fail: on\n  docker (2)\n", _console.Output);
        Assert.Contains(ToolsText.OfferedKeys, _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheOptionsTab_TheMasterSwitch_ReturnsTheFlag_TheTimeoutTypes()
    {
        await ConnectedAsync();
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Right, Keys.Right);              // Options
        Push(Keys.Enter, Keys.Down, Keys.Enter);   // MCP servers: the page, off picked
        Push(Keys.Down, Keys.Enter);               // MCP connect timeout (s): typed over the 30 in the slot
        Push(Keys.Backspace, Keys.Backspace);
        foreach (char c in "45")
        {
            Push(Keys.Char(c));
        }

        Push(Keys.Enter);
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Mcp, await menu.ShowAsync(CancellationToken.None));

        Assert.False(_settings.Current.McpServers);
        Assert.Equal(45, _settings.Current.McpConnectTimeoutSeconds);
        AssertTabEnds("\n" + Titled(Strip) + "\n \n▸ MCP servers              on\n  MCP connect timeout (s)  30\n", 100);
        Assert.Contains(McpText.Label + " › MCP servers", _console.Output);
        Assert.Contains("no MCP server is started; the pane still lists the config", _console.Output);
        Assert.Contains("  · MCP servers: off\n", _console.Output);
        Assert.Contains("  · MCP connect timeout (s): 45\n", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheTimeoutAlone_ReturnsNoFlag_AndABadValueIsRefused()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Right, Keys.Right, Keys.Down, Keys.Enter);   // the timeout
        Push(Keys.Backspace, Keys.Backspace, Keys.Char('2'), Keys.Enter);          // below the floor
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Equal(30, _settings.Current.McpConnectTimeoutSeconds);
        Assert.Contains("MCP connect timeout (s) must be 5 to 300 seconds; keeping 30.", _console.Output);
        Assert.Equal("must be 5 to 300 seconds", SettingsMenu.McpConnectTimeoutRangeError);
        pane.Dispose();
    }

    [Fact]
    public async Task MidTurn_AServerFlipAndTheReloadAreRefused_AToolFlipAndTheEditRowsWork_TheMasterSwitchRefused()
    {
        await ConnectedAsync();
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Enter);                          // docker: refused
        Push(Keys.Down, Keys.Down, Keys.Enter);    // edit profile: fine
        Push(Keys.Down, Keys.Down, Keys.Enter);    // reload: refused
        Push(Keys.Right, Keys.Enter);              // docker__echo off: fine
        Push(Keys.Right, Keys.Enter);              // MCP servers: refused
        Push(Keys.Down, Keys.Enter, Keys.Backspace, Keys.Backspace, Keys.Char('9'), Keys.Char('0'), Keys.Enter);   // the timeout: fine
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None, midTurn: true));

        Assert.Empty(_settings.Current.McpServersDisabled);
        Assert.Equal(["docker__echo"], _settings.Current.ToolsDisabled);
        Assert.True(_settings.Current.McpServers);
        Assert.Equal(90, _settings.Current.McpConnectTimeoutSeconds);
        Assert.Equal([_session.ProfilePath], _opened);
        Assert.Contains("  · " + SettingsMenu.NotWhileReplyRunsNotice + "\n▸ docker", _console.Output);
        Assert.Contains("  · " + SettingsMenu.NotWhileReplyRunsNotice + "\n  docker", _console.Output);   // reload's refusal, the cursor on the reload row
        Assert.Contains("  · " + SettingsMenu.NotWhileReplyRunsNotice + "\n▸ MCP servers", _console.Output);
        Assert.Equal(3, _session.Tools.Count);   // nothing stopped
        pane.Dispose();
    }

    [Fact]
    public async Task WithoutThePane_PrintsTheThreeTabsAsLines_ToTheTranscript()
    {
        await ConnectedAsync();
        _settings.Update(d => d.ToolsDisabled = ["docker__fail"]);
        var pane = new ScreenPane(_console, geometry: null, _time);
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menuPane = new MenuPane(pane, keys);
        var settings = new SettingsMenu(_console, _settings, _ => null, new InputLine(_console, keys), new TranscriptRenderer(_console), _speech, menuPane, _ => FakeBrowserPath);
        var menu = new McpMenu(Facts, _session, _settings, settings, new TranscriptRenderer(_console), menuPane, _opened.Add, () => _settings.Current);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Contains("  · Servers\n  ·   docker      on   connected · 2 tools  stdio: docker mcp gateway run\n  ·   chrome      on   connected · 1 tool  http: http://localhost:9/mcp\n  · Tools\n  ·   docker (1 of 2)\n  ·     docker__echo      on   Echoes the text back.\n  ·     docker__fail      off  Always fails.\n  ·   chrome (1)\n  ·     chrome__navigate  on   Opens a page.\n  · Options\n  ·   MCP servers: on\n  ·   MCP connect timeout (s): 30\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }
}
