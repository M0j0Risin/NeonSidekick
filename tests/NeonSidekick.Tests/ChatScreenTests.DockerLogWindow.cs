using NeonSidekick.App;
using NeonSidekick.Docker;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// The Docker log window on the screen (2026-10-23, the user's ask): <c>/docker logs &lt;container&gt;</c> hands the window the
/// container and 200 lines (or the number typed), <c>--pane</c> and a screen with no window keep the pane, a Web UI page keeps
/// its list, the reply runs on under it, the <c>/docker</c> pane's logs button opens it too, a refusal or a window that cannot be
/// made is its error line, and the teardown closes it. Named <c>DockerLogWindow_…</c> so the repeat filter catches them with the
/// feed's tests.
/// </summary>
public partial class ChatScreenTests
{
    private const string MariaDbId = "c9e0889008d03fe42ce2ba7be159d07f2392a1c5abe9322dd7acb9bce852b7f3";

    /// <summary>The window's hook, collecting what it was handed.</summary>
    private List<DockerLogSource> DockerLogWindowHook()
    {
        var shown = new List<DockerLogSource>();
        _openDockerLogWindow = source => shown.Add(source);
        return shown;
    }

    [Fact]
    public async Task DockerLogWindow_Logs_OpensTheWindow_With200Lines_OrTheNumberTyped()
    {
        DockerEngine();
        var shown = DockerLogWindowHook();
        PushLine("/docker logs mariadb_dev");
        PushLine("/docker logs maria 30");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal([(MariaDbId, "mariadb_dev", 200), (MariaDbId, "mariadb_dev", 30)], shown.Select(s => (s.Id, s.Name, s.Tail)));
        Assert.Contains(DockerLogWindowText.OpenedNotice("mariadb_dev"), output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task DockerLogWindow_ThePaneWord_KeepsThePane()
    {
        var engine = DockerEngine();
        engine.Map(DockerClient.Host + "/v1.47/containers/" + MariaDbId + "/logs",
            (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(System.Net.HttpStatusCode.OK, [.. SmokeChecks.DockerFrame(1, "mariadb ready for connections\n")], DockerLogStream.MultiplexedType)));
        var shown = DockerLogWindowHook();
        ListPaneScreen();

        string output = await RunListPaneAsync("/docker logs mariadb_dev 5 --pane");

        Assert.Empty(shown);
        OnThePaneOnly(output, "mariadb ready for connections");
    }

    [Fact]
    public async Task DockerLogWindow_AnUnknownContainer_IsAnError_AndAWindowThatCannotBeMade_SaysSo()
    {
        DockerEngine();
        _openDockerLogWindow = _ => throw new InvalidOperationException("no thread");
        PushLine("/docker logs nope");
        PushLine("/docker logs mariadb_dev");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + DockerLogWindowText.WindowFailedError("no thread"), output);
        Assert.DoesNotContain(DockerLogWindowText.OpenedNotice("mariadb_dev"), output);
        Assert.Equal(2, output.Split('\n').Count(l => l.Contains("  ✗ ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DockerLogWindow_TheTeardown_ClosesTheWindow()
    {
        int closed = 0;
        _closeDockerLogWindow = () => { closed++; return true; };
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task DockerLogWindow_UnderAReply_OpensTheWindow_AndTheReplyRunsOn()
    {
        DockerEngine();
        var shown = DockerLogWindowHook();
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                PushLine("/docker logs mariadb_dev");
            }
        });

        string output = await RunAsync();

        Assert.Equal("mariadb_dev", Assert.Single(shown).Name);
        Assert.Contains(DockerLogWindowText.OpenedNotice("mariadb_dev"), output);
        Assert.DoesNotContain(ChatScreen.MidTurnDeferredNotice("/docker"), output);
        Assert.Single(_chat.Requests);
    }

    [Fact]
    public async Task DockerLogWindow_FromThePage_StaysAList()
    {
        DockerEngine().Map(DockerClient.Host + "/v1.47/containers/" + MariaDbId + "/logs",
            (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(System.Net.HttpStatusCode.OK, [.. SmokeChecks.DockerFrame(1, "mariadb ready\n")], DockerLogStream.MultiplexedType)));
        var shown = DockerLogWindowHook();
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        var input = WebSteps(
            _ => SendFromPage("/docker logs mariadb_dev 5"),
            i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Empty(shown);
        Assert.DoesNotContain(DockerLogWindowText.OpenedNotice("mariadb_dev"), output);
    }

    [Fact]
    public async Task DockerLogWindow_ThePanesLogsButton_OpensTheWindow()
    {
        DockerEngine();
        var shown = DockerLogWindowHook();
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        // The list's first row is mariadb_dev; its page reads stop, restart, pause, logs…
        StepsWhenIdle(Line("/docker"), Key(Keys.Enter), Key(Keys.Down), Key(Keys.Down), Key(Keys.Down), Key(Keys.Enter), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(DockerLogWindowText.MenuNote, output);
        Assert.Equal(("mariadb_dev", DockerLogWindowText.DefaultWindowLines), (Assert.Single(shown).Name, shown[0].Tail));
        Assert.Contains(DockerLogWindowText.OpenedNotice("mariadb_dev"), output);
    }
}
