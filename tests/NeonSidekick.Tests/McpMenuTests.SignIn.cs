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
}
