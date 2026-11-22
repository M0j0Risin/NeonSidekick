using System.Net;
using System.Net.Sockets;
using NeonSidekick.App;
using NeonSidekick.Mcp;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The Servers tab's <c>s</c> (2026-11-21, feature idea #32): a sign-in on a needs-sign-in row (Enter too), the pane staying open while
/// it waits, Esc calling it off before a second Esc closes, and the rows with no sign-in saying so.
/// </summary>
public partial class McpMenuTests
{
    [Fact]
    public async Task S_OnACommandOrPlainUrlRow_SaysItUsesNoSignIn_AndChangesNothing()
    {
        await ConnectedAsync();
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('s'), Keys.Down, Keys.Char('s'), Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(McpText.NoSignIn("docker"), Output);
        Assert.Contains(McpText.NoSignIn("chrome"), Output);
        Assert.Equal(McpState.Connected, Server("docker").State);
        pane.Dispose();
    }

    [Fact]
    public async Task Enter_OnANeedsSignInRow_StartsTheSignIn_EscCallsItOff_AndASecondEscCloses()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(ProfileFile, "{ \"mcpServers\": { \"stub\": { \"url\": \"" + McpOAuthStub.Endpoint + "\", \"oauth\": { \"redirectPort\": "
            + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + " } } } }");
        using var stub = new McpOAuthStub();
        await using var session = new McpSession(_settings, (config, name, oauth) => McpSession.DefaultTransport(config, name, oauth, stub), _time);
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        Assert.True(session.Servers.Single().NeedsSignIn);

        _console.Profile.Height = 40;
        var screen = new ScreenPane(_console, new ScreenGeometry(() => null), new ManualTimeProvider()) { Hint = () => "idle" };
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var menuPane = new MenuPane(screen, keys);
        var settings = new SettingsMenu(new ConsoleWithInput(screen, keys), _settings, _ => null, new InputLine(screen, keys), new TranscriptRenderer(screen), _speech, menuPane, _ => FakeBrowserPath);
        var menu = new McpMenu(() => new McpFacts(session.Servers, session.Problems, true, true, ToolsText.DisabledSet([]), session.ProfilePath, session.GlobalPath),
            session, _settings, settings, new TranscriptRenderer(screen), menuPane, _opened.Add, () => _settings.Current);
        screen.Show();
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var row = session.Servers.Single();
        Assert.True(row.NeedsSignIn);
        Assert.Null(session.SigningIn);
        Assert.Contains(McpText.SignInCancelledNotice("stub"), Output);
        Assert.Equal(0, stub.CodeExchanges);
        screen.Dispose();
    }

    // ── The review's section 4 (2026-11-22) ──────────────────────────────────

    private string StubConfig()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        string text = "{ \"mcpServers\": { \"stub\": { \"url\": \"" + McpOAuthStub.Endpoint + "\", \"oauth\": { \"redirectPort\": "
            + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + " } } } }";
        File.WriteAllText(ProfileFile, text);
        return text;
    }

    private (McpMenu Menu, ScreenPane Screen) StubMenu(McpSession session, Func<NeonSidekick.Settings.AppSettingsData>? effective = null, Spectre.Console.IAnsiConsoleInput? input = null)
    {
        _console.Profile.Height = 40;
        var screen = new ScreenPane(_console, new ScreenGeometry(() => null), new ManualTimeProvider()) { Hint = () => "idle" };
        var keys = new KeySource(input ?? _console.Input, TimeSpan.FromMilliseconds(1));
        var menuPane = new MenuPane(screen, keys);
        var settings = new SettingsMenu(new ConsoleWithInput(screen, keys), _settings, _ => null, new InputLine(screen, keys), new TranscriptRenderer(screen), _speech, menuPane, _ => FakeBrowserPath);
        var menu = new McpMenu(() => new McpFacts(session.Servers, session.Problems, true, true, ToolsText.DisabledSet(_settings.Current.McpServersDisabled), session.ProfilePath, session.GlobalPath),
            session, _settings, settings, new TranscriptRenderer(screen), menuPane, _opened.Add, effective ?? (() => _settings.Current));
        screen.Show();
        return (menu, screen);
    }

    private async Task SaysAsync(McpSession session, string text)
    {
        var taken = new List<string>();
        for (int i = 0; i < 200 && !Output.Contains(text, StringComparison.Ordinal) && !taken.Contains(text); i++)
        {
            taken.AddRange(session.TakeNotices().Select(n => n.Text));
            await Task.Delay(50);
        }

        var output = Output;
        Assert.True(output.Contains(text, StringComparison.Ordinal) || taken.Contains(text),
            text + "\n--- taken: " + string.Join(" | ", taken) + "\n--- output tail:\n" + output[Math.Max(0, output.Length - 2500)..]);
    }

    [Fact]
    public async Task S_OnASwitchedOffServer_ThatHoldsASignIn_SignsOut()
    {
        StubConfig();
        using var stub = new McpOAuthStub();
        await using var session = new McpSession(_settings, (config, name, oauth) => McpSession.DefaultTransport(config, name, oauth, stub), _time)
        {
            OpenBrowser = url => _ = Task.Run(() => stub.BrowseAsync(url, CancellationToken.None)),
        };
        Assert.Equal(McpState.Connected, (await session.SignInAsync("stub", _settings.Current, CancellationToken.None))!.State);
        _settings.Update(d => d.McpServersDisabled = ["stub"]);
        await session.DisconnectServerAsync("stub");
        Assert.False(session.Servers.Single().Enabled);
        var (menu, screen) = StubMenu(session);
        Push(Keys.Char('s'), Keys.Down, Keys.Enter, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(session.HoldsSignIn("stub"));
        Assert.Contains(McpText.SignedOutNotice("stub"), Output);
        screen.Dispose();
    }

    [Fact]
    public async Task S_OnASwitchedOffServer_ThatCouldSignIn_SaysTurnItOnFirst()
    {
        StubConfig();
        _settings.Update(d => d.McpServersDisabled = ["stub"]);
        using var stub = new McpOAuthStub();
        await using var session = new McpSession(_settings, (config, name, oauth) => McpSession.DefaultTransport(config, name, oauth, stub), _time);
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        var (menu, screen) = StubMenu(session);
        Push(Keys.Char('s'), Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(McpText.SignInTurnOnFirst("stub"), Output);
        Assert.DoesNotContain(McpText.NoSignIn("stub"), Output);
        Assert.Equal(0, stub.Registrations);
        screen.Dispose();
    }

    [Fact]
    public async Task ASignInThatDoesntStart_SaysSo_NeverNothing()
    {
        StubConfig();
        using var stub = new McpOAuthStub();
        await using var session = new McpSession(_settings, (config, name, oauth) => McpSession.DefaultTransport(config, name, oauth, stub), _time);
        await session.ConnectAllAsync(_settings.Current, null, CancellationToken.None);
        Assert.True(session.Servers.Single().NeedsSignIn);

        // MCP turned off between the pane's look and the sign-in's start: the session starts nothing. A scripted input that waits for its
        // next key, never one that runs dry: a dry console ends the pick, which calls off a sign-in still queued (a slow thread pool, the full run).
        var input = new ScriptedInput();
        var (menu, screen) = StubMenu(session, () =>
        {
            var off = NeonSidekick.Settings.AppSettings.Copy(_settings.Current);
            off.McpServers = false;
            return off;
        }, input);
        input.Push(Keys.Char('s'));

        // Esc only once the sign-in has ended: an Esc while it still waits cancels it, which reads as canceled. An Esc that beats the pane's
        // wake closes it with the outcome still queued, which goes to the transcript, never a status line no longer drawn (the full run's case).
        var showing = Task.Run(() => menu.ShowAsync(CancellationToken.None));
        for (int i = 0; i < 400 && menu.SignInTask is not { IsCompleted: true }; i++)
        {
            await Task.Delay(25);
        }

        Assert.True(menu.SignInTask is { IsCompleted: true });
        input.Push(Keys.Escape);
        await showing;

        await SaysAsync(session, McpText.SignInNotNow("stub"));
        Assert.Equal(0, stub.Registrations);
        screen.Dispose();
    }
}
