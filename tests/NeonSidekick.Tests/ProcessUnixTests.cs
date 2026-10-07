using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Viewer;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The runner, the registry, the <c>process</c> tool and the <c>/process</c> pane over real <c>zsh</c> children (2026-10-06, the
/// macOS build): the Unix twins of <see cref="ShellRunnerTests"/>, <see cref="ProcessToolTests"/>, <see cref="ProcessMenuTests"/>
/// and <see cref="ProcessWindowTests"/>' registry parts, which run <c>cmd.exe</c> lines and are Windows-only. The same shapes, a
/// Unix shell's words; skipped on Windows.
/// </summary>
public sealed class ProcessUnixTests : IDisposable
{
    private const string Sleeper = "sleep 30";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new();
    private readonly Interpreters _interpreters = new(_ => null);
    private readonly ShellRunner _runner;
    private readonly ProcessRegistry _registry;
    private readonly ProcessTool _tool;
    private readonly CommandGate _gate;
    private readonly Files.WorkingDirectory _files;
    private int _signals;

    public ProcessUnixTests()
    {
        Directory.CreateDirectory(_dir);
        _gate = new CommandGate(() => _settings, new CommandAllowList(() => [], _ => { }), null);
        _runner = new ShellRunner(_time);
        _registry = new ProcessRegistry(_runner, new Random(7), () => Interlocked.Increment(ref _signals));
        _files = new Files.WorkingDirectory(() => _dir, _time);
        _tool = new ProcessTool(_registry, _files, () => _settings, _gate);
    }

    public void Dispose()
    {
        _registry.Dispose();
        GitAccessTests.DeleteTree(_dir);
    }

    private ProcessLaunch Zsh(string command) => ShellCommandLine.For(ShellKind.Zsh, command, _interpreters.Locate(ShellKind.Zsh)!, _dir);

    private ProcessSession Start(string command, bool notify = false) => _registry.Start(Zsh(command), notify);

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs) => new(pairs.ToDictionary(p => p.Name, p => p.Value));

    private async Task<string> Invoke(params (string Name, object? Value)[] pairs) => (string)(await _tool.InvokeAsync(Args(pairs)))!;

    private static async Task Exit(ProcessSession session)
    {
        await session.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(50);   // the registry's watcher runs after the exit
    }

    // ── ShellRunner ─────────────────────────────────────────────────────────

    [UnixFact]
    public async Task Runner_Echo_ExitCode_Stderr_AndUtf8_ComeThrough()
    {
        using var session = _runner.Start(Zsh("echo ü; echo err >&2; exit 3"), "run_000001");
        session.CloseInput();

        Assert.Equal(3, await session.Exited.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(["ü"], session.Output.Lines().Where(l => !l.IsError).Select(l => l.Text));
        Assert.Equal(["err"], session.Output.Lines().Where(l => l.IsError).Select(l => l.Text));
        Assert.Equal("zsh", session.Kind);
        Assert.True(session.Pid > 0);
        Assert.False(session.Killed);
    }

    [UnixFact]
    public async Task Runner_ReadsStdin_UntilItIsClosed()
    {
        using var session = _runner.Start(Zsh("read name; echo hello $name"), "run_000002");
        Assert.True(await session.WriteAsync("world\n", CancellationToken.None));
        session.CloseInput();

        Assert.Equal(0, await session.Exited.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal("hello world", session.Output.Lines()[^1].Text);
        Assert.False(await session.WriteAsync("late\n", CancellationToken.None));
    }

    [UnixFact]
    public async Task Runner_Kill_EndsAWaitingChild_AndDisposeKillsARunningOne()
    {
        using var session = _runner.Start(Zsh(Sleeper), "run_000003");
        session.CloseInput();
        var wait = session.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(11));
        Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(30)));
        session.Kill();
        await session.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(session.Killed);
        Assert.NotEqual(0, session.ExitCode);

        var disposed = _runner.Start(Zsh(Sleeper), "run_000004");
        disposed.Dispose();
        Assert.True(disposed.Killed);
        Assert.NotEqual(0, await disposed.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // ── The process tool ────────────────────────────────────────────────────

    [UnixFact]
    public async Task List_Poll_AndClose_FollowAChild()
    {
        var session = Start("echo one; echo two");
        await Exit(session);

        Assert.Equal("1 process (0 running)\n" + session.Id + "  exit 0    0.0 s     zsh        echo one; echo two", await Invoke(("action", "list")));
        Assert.Equal(session.Id + " exited 0 after 0.0 s (zsh): echo one; echo two — 2 new lines\none\ntwo", await Invoke(("action", "poll"), ("session_id", session.Id[..7])));
        Assert.Equal("closed " + session.Id + " (exit 0, 2 lines forgotten)", await Invoke(("action", "close"), ("session_id", session.Id)));
        Assert.Empty(_registry.List());
    }

    [UnixFact]
    public async Task Log_IsANumberedWindow_AndPollKeepsHeadAndTail()
    {
        const string Lines = "for i in {1..6000}; do echo line $i; done";
        var session = Start(Lines);
        await Exit(session);
        Assert.Equal(session.Id + " lines 5,999-6,000 of 6,000 (exit 0): " + Lines + "\n(lines 1-1,000 are gone: the log keeps the last 5,000)\nline 5999\nline 6000",
            await Invoke(("action", "log"), ("session_id", session.Id), ("limit", 2)));

        _settings.ShellOutputMaxChars = 2000;
        const string Fewer = "for i in {1..400}; do echo line $i; done";
        var capped = Start(Fewer);
        await Exit(capped);
        string poll = await Invoke(("action", "poll"), ("session_id", capped.Id));
        Assert.StartsWith(capped.Id + " exited 0 after 0.0 s (zsh): " + Fewer + " — 400 new lines — output cut\nline 1\n", poll);
        Assert.EndsWith("\nline 400", poll);
    }

    [UnixFact]
    public async Task Wait_Kill_Write_AndSubmit_OverARunningChild_ThePoliceReadingUnixPaths()
    {
        const string ReadLine = "read name; echo hello $name";
        var session = Start(ReadLine);
        Assert.Equal("sent 3 characters to " + session.Id, await Invoke(("action", "write"), ("session_id", session.Id), ("data", "wor")));
        Assert.Equal("sent a line to " + session.Id, await Invoke(("action", "submit"), ("session_id", session.Id), ("data", "ld")));
        Assert.Equal(session.Id + " exited 0 after 0.0 s (zsh): " + ReadLine + " — 1 new line\nhello world", await Invoke(("action", "wait"), ("session_id", session.Id), ("timeout", 30)));

        var typed = Start(ReadLine);
        Assert.Equal("Error: outside the working directory: '/etc' — a command or a script may only name paths under it", await Invoke(("action", "submit"), ("session_id", typed.Id), ("data", "cd /etc")));
        Assert.Equal("Error: outside the working directory: '..' — a command or a script may only name paths under it", await Invoke(("action", "write"), ("session_id", typed.Id), ("data", "cd ..")));
        Assert.Equal("sent a line to " + typed.Id, await Invoke(("action", "submit"), ("session_id", typed.Id), ("data", "sub")));
        Assert.Equal(typed.Id + " exited 0 after 0.0 s (zsh): " + ReadLine + " — 1 new line\nhello sub", await Invoke(("action", "wait"), ("session_id", typed.Id), ("timeout", 30)));
        Assert.Equal(["cd /etc", "cd .."], _gate.Refusals);

        var sleeper = Start(Sleeper);
        var wait = _tool.InvokeAsync(Args(("action", "wait"), ("session_id", sleeper.Id), ("timeout", 5)));
        await Task.Delay(100);
        _time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(sleeper.Id + " still running after 5 s (zsh): " + Sleeper + " — no new output\n(no output)", (string)(await wait.AsTask().WaitAsync(TimeSpan.FromSeconds(30)))!);
        Assert.Matches("^killed " + sleeper.Id + " \\(zsh, pid [0-9]+\\) after 6\\.0 s: sleep 30$", await Invoke(("action", "kill"), ("session_id", sleeper.Id)));
        Assert.True(sleeper.Killed);
    }

    [UnixFact]
    public async Task Notify_AmbiguousIds_Eviction_AndTheRunningCap()
    {
        var a = Start("echo a");
        var loud = Start("echo l; exit 3", notify: true);
        await Exit(a);
        await Exit(loud);
        Assert.Equal("Error: 'proc_' matches 2 processes: " + a.Id + ", " + loud.Id, await Invoke(("action", "poll"), ("session_id", "proc_")));
        Assert.True(_registry.TryTakeAlert(out var alert));
        Assert.Equal(loud.Id + " exited 3 after 0.0 s: echo l; exit 3", ShellText.AlertLine(alert));
        Assert.Equal([loud.Id], _registry.TakeNotes());

        for (int i = 0; i < ProcessRegistry.MaxKept; i++)
        {
            await Exit(Start("exit 0"));
        }

        Assert.DoesNotContain(a, _registry.List());

        var sleepers = new List<ProcessSession>();
        for (int i = 0; i < ProcessRegistry.MaxRunning; i++)
        {
            sleepers.Add(Start(Sleeper));
        }

        Assert.Equal("Error: too many background processes (16); kill or close one", Assert.Throws<ShellStartException>(() => Start("echo no")).Message);
        _registry.Dispose();
        foreach (var sleeper in sleepers)
        {
            Assert.True(sleeper.Killed);
            await sleeper.Exited.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    // ── ProcessRegistry.StopByUser ──────────────────────────────────────────

    [UnixFact]
    public async Task StopByUser_AlertsAndNotes_InTheUsersWords_AndRefusesAnotherRegistrysSession()
    {
        var session = Start(Sleeper);
        Assert.True(_registry.StopByUser(session));
        await Exit(session);

        Assert.True(session.StoppedByUser);
        Assert.True(_registry.TryTakeAlert(out var alert));
        Assert.True(alert.ByUser);
        Assert.Equal(session.Id + " was stopped by you after 0.0 s: " + Sleeper, ShellText.AlertLine(alert));
        Assert.Equal(session.Id + "  stopped by you 0.0 s     zsh        " + Sleeper, ProcessWindowText.Row(session));
        Assert.False(_registry.StopByUser(session));

        using var other = new ProcessRegistry(new ShellRunner(_time), new Random(3), () => { });
        var foreign = other.Start(Zsh(Sleeper), notify: false);
        Assert.False(_registry.StopByUser(foreign));
        Assert.False(foreign.HasExited);
    }

    [UnixFact]
    public async Task StopByUser_AChildGoneWhileItsOutputDrains_IsNotStopped_NorNotified()
    {
        // zsh exits at once while the sleep it left in the background holds the output pipe open (ProcessWindowTests' cmd case).
        var session = Start("sleep 4 & exit 3");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!ParentGone(session.Pid) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(ParentGone(session.Pid));
        Assert.False(session.HasExited);
        Assert.False(_registry.StopByUser(session));
        Assert.False(session.Notify);

        await Exit(session);
        Assert.False(session.StoppedByUser);
        Assert.Equal(3, session.ExitCode);
        Assert.False(_registry.TryTakeAlert(out _));

        static bool ParentGone(int pid)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
    }

    // ── The /process pane ───────────────────────────────────────────────────

    [UnixFact]
    public async Task ThePane_ListsTheRows_TheKillKeyAsksFirst_AndAnEndedOneSaysSo()
    {
        using var console = new TestConsole();
        console.Interactive();
        console.Profile.Width = 120;
        console.Profile.Height = 40;
        var opened = new List<ProcessSession>();
        var done = Start("exit 3");
        await Exit(done);
        var running = Start(Sleeper);
        Assert.Equal("2 processes (1 running)", ProcessMenu.Caption(_registry.List()));

        var pane = new ScreenPane(console, new ScreenGeometry(() => null, () => 100), _time) { Hint = () => "idle" };
        var input = new ScriptedInput();
        var menu = new ProcessMenu(_registry, new TranscriptRenderer(pane), new MenuPane(pane, new KeySource(input, TimeSpan.FromMilliseconds(1))), (session, sink) =>
        {
            opened.Add(session);
            return true;
        });
        pane.Show();
        input.Push(Keys.Char('k'));      // the cursor on the ended one
        input.Push(Keys.Down);
        input.Push(Keys.Char('k'));
        input.Push(Keys.Char('y'));
        input.Push(Keys.Enter);          // Yes
        input.Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("  · " + ProcessMenu.EndedNotice(done), console.Output);
        Assert.Contains(ProcessMenu.KillPrompt(running), console.Output);
        Assert.Contains("  · " + ProcessMenu.StoppingNotice(running.Id), console.Output);
        await running.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(running.StoppedByUser);
        Assert.Empty(opened);
        pane.Dispose();
    }
}
