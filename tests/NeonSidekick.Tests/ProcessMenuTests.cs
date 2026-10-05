using NeonSidekick.App;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Viewer;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The bare <c>/process</c> pane (2026-10-05, the user's ask): the rows, Enter or a double-click handing the highlighted session to
/// the window's opener, the kill button (a click or <c>k</c>) asking first and stopping through the registry, an ended one refused
/// with its state. Real <c>cmd.exe</c> children, as <see cref="ProcessWindowTests"/> has.
/// </summary>
public sealed class ProcessMenuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly TestConsole _console = new();
    private readonly ManualTimeProvider _time = new();
    private readonly Interpreters _interpreters = new(_ => null);
    private readonly ProcessRegistry _registry;
    private readonly List<ProcessSession> _opened = [];

    public ProcessMenuTests()
    {
        Directory.CreateDirectory(_dir);
        _console.Interactive();
        _console.Profile.Width = 120;
        _console.Profile.Height = 40;
        _registry = new ProcessRegistry(new ShellRunner(_time), new Random(13), () => { });
    }

    public void Dispose()
    {
        _registry.Dispose();
        _console.Dispose();
        GitAccessTests.DeleteTree(_dir);
    }

    private ProcessSession Start(string command) =>
        _registry.Start(ShellCommandLine.For(ShellKind.Cmd, command, _interpreters.Locate(ShellKind.Cmd)!, _dir), notify: false);

    /// <summary>The menu over a pane whose cursor row the geometry reports (the click's frame) and a scripted source that carries clicks and keys.</summary>
    private (ProcessMenu Menu, ScreenPane Pane, ScriptedInput Input) PaneMenu(int cursorTop = 100)
    {
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => cursorTop), _time) { Hint = () => "idle" };
        var input = new ScriptedInput();
        var keys = new KeySource(input, TimeSpan.FromMilliseconds(1));
        var menu = new ProcessMenu(_registry, new TranscriptRenderer(pane), new MenuPane(pane, keys), (session, sink) =>
        {
            _opened.Add(session);
            sink.Notice(ProcessWindowText.OpenedNotice(session.Id));
            return true;
        });
        pane.Show();
        return (menu, pane, input);
    }

    [Fact]
    public void Strings_ArePinned()
    {
        Assert.Equal("⏳ Process", ProcessMenu.Title);
        Assert.Equal("Enter = open window · k = kill · ESC = close", ProcessMenu.Keys);
        Assert.Equal("✖ kill", ProcessMenu.KillButton);
        Assert.Equal('k', ProcessMenu.KillKey);
        Assert.Equal(ChatScreen.KeptNotice, ProcessMenu.KeptNotice);
        Assert.Equal("(⏳ stopping proc_3f2a1b)", ProcessMenu.StoppingNotice("proc_3f2a1b"));
    }

    [Fact]
    public async Task TheRows_AreTheListsRows_AnEndedOneDim_UnderTheCount()
    {
        var done = Start("exit 0");
        await done.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        var running = Start("ping -n 30 127.0.0.1 >nul");

        Assert.Equal(Spectre.Console.Markup.Escape(ProcessWindowText.Row(running)), ProcessMenu.RowMarkup(running));
        Assert.Equal(Theme.DimMarkup(ProcessWindowText.Row(done)), ProcessMenu.RowMarkup(done));
        Assert.Equal("2 processes (1 running)", ProcessMenu.Caption(_registry.List()));
        Assert.Equal("⏳ Stop " + running.Id + " (" + ProcessWindowText.Label(running.Label) + ")?", ProcessMenu.KillPrompt(running));
        Assert.Equal("(⏳ " + done.Id + " has ended: exited 0)", ProcessMenu.EndedNotice(done));
    }

    [Fact]
    public async Task Enter_OpensTheHighlightedOne_ThePaneStaysOpen()
    {
        var first = Start("ping -n 30 127.0.0.1 >nul");
        var second = Start("ping -n 31 127.0.0.1 >nul");
        var (menu, pane, input) = PaneMenu();
        input.Push(Keys.Down);
        input.Push(Keys.Enter);
        input.Push(Keys.Up);
        input.Push(Keys.Enter);
        input.Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([second, first], _opened);
        Assert.Contains(ProcessMenu.Title, _console.Output);
        Assert.Contains("2 processes (2 running)", _console.Output);
        Assert.Contains("  · " + ProcessWindowText.OpenedNotice(second.Id), _console.Output);   // on the status line
        Assert.False(pane.OverlayOpen);
        Assert.False(first.HasExited || second.HasExited);
        pane.Dispose();
    }

    [Fact]
    public async Task ADoubleClickOnARow_OpensIt_AsEnterWould()
    {
        Start("ping -n 30 127.0.0.1 >nul");
        var second = Start("ping -n 31 127.0.0.1 >nul");
        var (menu, pane, input) = PaneMenu();
        input.Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);   // once to find the second row's line on the screen

        string[] lines = _console.Output.Split('\n');
        int top = Array.FindLastIndex(lines, l => l.Contains(ProcessMenu.Title, StringComparison.Ordinal));
        int row = Array.FindIndex(lines, top, l => l.Contains(second.Id, StringComparison.Ordinal)) - top;
        Assert.True(row > 0);

        input.PushClick(4, 100 + row);
        input.PushClick(6, 100 + row);
        input.Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([second], _opened);
        pane.Dispose();
    }

    [Fact]
    public async Task TheKillKey_AsksFirst_NoKeepsIt_YesStopsIt()
    {
        var running = Start("ping -n 30 127.0.0.1 >nul");
        var (menu, pane, input) = PaneMenu();
        input.Push(Keys.Char('k'));
        input.Push(Keys.Enter);          // the cursor on No
        input.Push(Keys.Char('k'));
        input.Push(Keys.Char('y'));
        input.Push(Keys.Enter);          // Yes
        input.Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(ProcessMenu.KillPrompt(running), _console.Output);
        Assert.Contains("  · " + ProcessMenu.KeptNotice, _console.Output);
        Assert.Contains("  · " + ProcessMenu.StoppingNotice(running.Id), _console.Output);
        await running.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(running.StoppedByUser);
        Assert.Empty(_opened);
        pane.Dispose();
    }

    [Fact]
    public async Task TheKillButton_OnAnEndedOne_SaysSo_AndAsksNothing()
    {
        var done = Start("exit 3");
        await done.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(50);   // the registry's watcher runs after the exit
        var (menu, pane, input) = PaneMenu();
        input.Push(Keys.Char('k'));
        input.Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("  · " + ProcessMenu.EndedNotice(done), _console.Output);
        Assert.DoesNotContain(ProcessMenu.KillPrompt(done), _console.Output);
        Assert.False(done.StoppedByUser);
        pane.Dispose();
    }

    [Fact]
    public async Task WithNone_ItIsTheNoneYetLine()
    {
        var (menu, pane, _) = PaneMenu();

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(ProcessWindowText.NoneYet, _console.Output);
        Assert.DoesNotContain(ProcessMenu.Title, _console.Output);
        pane.Dispose();
    }
}
