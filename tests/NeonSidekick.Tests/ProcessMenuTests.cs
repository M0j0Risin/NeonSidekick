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
        Assert.Equal("⚡ Process", ProcessMenu.Title);
        Assert.Equal("Enter = open window · k = kill · r / e = running / ended · ESC = close", ProcessMenu.Keys);
        Assert.Equal("✖  kill", ProcessMenu.KillButton);
        Assert.Equal('k', ProcessMenu.KillKey);
        Assert.Equal("● running", ProcessMenu.RunningButton);
        Assert.Equal('r', ProcessMenu.RunningKey);
        Assert.Equal("○ ended", ProcessMenu.EndedButton);
        Assert.Equal('e', ProcessMenu.EndedKey);
        Assert.Equal("No running processes.", ProcessMenu.NoMatchLine(running: true));
        Assert.Equal("No ended processes.", ProcessMenu.NoMatchLine(running: false));
        Assert.Equal(ChatScreen.KeptNotice, ProcessMenu.KeptNotice);
        Assert.Equal("(⚡ stopping proc_3f2a1b)", ProcessMenu.StoppingNotice("proc_3f2a1b"));
    }

    [WindowsFact]
    public async Task TheRows_AreTheListsRows_AnEndedOneDim_UnderTheCount()
    {
        var done = Start("exit 0");
        await done.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        var running = Start("ping -n 30 127.0.0.1 >nul");

        Assert.Equal(Spectre.Console.Markup.Escape(ProcessWindowText.Row(running)), ProcessMenu.RowMarkup(running));
        Assert.Equal(Theme.DimMarkup(ProcessWindowText.Row(done)), ProcessMenu.RowMarkup(done));
        Assert.Equal("2 processes (1 running)", ProcessMenu.Caption(_registry.List()));
        Assert.Equal("⚡ Stop " + running.Id + " (" + ProcessWindowText.Label(running.Label) + ")?", ProcessMenu.KillPrompt(running));
        Assert.Equal("(⚡ " + done.Id + " has ended: exited 0)", ProcessMenu.EndedNotice(done));
    }

    [Fact]
    public void TheFilterButtons_AreARadioPair_AfterKill()
    {
        // 2026-10-11, the user's ask: running and ended, one lit at a time, the lit one pressed again goes dark.
        Assert.Equal([ProcessMenu.KillButton, ProcessMenu.RunningButton, ProcessMenu.EndedButton], ProcessMenu.ButtonsFor(null).Select(b => b.Title));
        Assert.DoesNotContain(ProcessMenu.ButtonsFor(null), b => b.On);
        Assert.Equal([false, true, false], ProcessMenu.ButtonsFor(true).Select(b => b.On));
        Assert.Equal([false, false, true], ProcessMenu.ButtonsFor(false).Select(b => b.On));

        Assert.True(ProcessMenu.Press(null, ProcessMenu.RunningIndex));
        Assert.Null(ProcessMenu.Press(true, ProcessMenu.RunningIndex));
        Assert.False(ProcessMenu.Press(true, ProcessMenu.EndedIndex));
        Assert.Null(ProcessMenu.Press(false, ProcessMenu.EndedIndex));
        Assert.True(ProcessMenu.Press(false, ProcessMenu.RunningIndex));
        Assert.True(ProcessMenu.Press(true, 0));   // kill is no filter
    }

    [WindowsFact]
    public async Task Matches_KeepsTheRunningOrTheEnded_OrEvery()
    {
        var done = Start("exit 0");
        await done.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        var running = Start("ping -n 30 127.0.0.1 >nul");

        Assert.True(ProcessMenu.Matches(null, done));
        Assert.True(ProcessMenu.Matches(null, running));
        Assert.True(ProcessMenu.Matches(true, running));
        Assert.False(ProcessMenu.Matches(true, done));
        Assert.True(ProcessMenu.Matches(false, done));
        Assert.False(ProcessMenu.Matches(false, running));
    }

    [WindowsFact]
    public async Task TheFilterKeys_ThinTheRows_AndTheLitOnePressedAgainShowsAll()
    {
        var done = Start("exit 0");
        await done.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(50);   // the registry's watcher runs after the exit
        var running = Start("ping -n 30 127.0.0.1 >nul");
        var (menu, pane, input) = PaneMenu();
        input.Push(Keys.Char('r'));
        input.Push(Keys.Enter);          // the one running row
        input.Push(Keys.Char('e'));
        input.Push(Keys.Enter);          // the one ended row
        input.Push(Keys.Char('e'));
        input.Push(Keys.Down);
        input.Push(Keys.Enter);          // every row again: the second is the running one
        input.Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([running, done, running], _opened);
        Assert.Contains("2 processes (1 running)", _console.Output);   // the count stays the whole list's
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [WindowsFact]
    public async Task AFilterLeavingNothing_ShowsTheNoMatchRow_EnterOpensNothing_ThePaneStays()
    {
        var done = Start("exit 0");
        await done.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(50);
        var (menu, pane, input) = PaneMenu();
        input.Push(Keys.Char('r'));
        input.Push(Keys.Enter);          // the no-match row
        input.Push(Keys.Char('k'));      // nothing to stop
        input.Push(Keys.Char('r'));
        input.Push(Keys.Enter);          // every row again
        input.Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(ProcessMenu.NoMatchLine(running: true), _console.Output);
        Assert.DoesNotContain(ProcessMenu.EndedNotice(done), _console.Output);
        Assert.Equal([done], _opened);
        pane.Dispose();
    }

    [WindowsFact]
    public async Task CursorAfter_FollowsTheProcess_WhenTheRowsShift_AndKeepsTheRowOnceItIsGone()
    {
        // The registry's eviction drops the oldest from the front: the cursor stays on the process, not the row (2026-10-05, the code review).
        var a = Start("exit 0");
        var b = Start("exit 0");
        var c = Start("exit 0");
        await Task.WhenAll(a.Exited, b.Exited, c.Exited).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, ProcessMenu.CursorAfter([b, c], b, 1));   // a went: b moved up a row
        Assert.Equal(1, ProcessMenu.CursorAfter([a, c], b, 1));   // b went: the row it stood on
        Assert.Equal(1, ProcessMenu.CursorAfter([a, c], b, 5));   // in range
        Assert.Equal(0, ProcessMenu.CursorAfter([], b, 3));
    }

    [WindowsFact]
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

    [WindowsFact]
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

    [WindowsFact]
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

    [WindowsFact]
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
    public async Task WithNone_ThePaneOpens_OnTheNoneYetLine()
    {
        // The pane opens with nothing started too (2026-10-07, the consistency pass), the line its one dim row.
        var (menu, pane, input) = PaneMenu();
        input.Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("▸ " + ProcessWindowText.NoneYet + "\n", _console.Output);
        Assert.Contains(ProcessMenu.Title, _console.Output);
        Assert.Contains("\n" + MenuPane.EmptyKeys + "\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }
}
