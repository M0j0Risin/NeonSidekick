using NeonSidekick.Diagnostics;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// The process window's parts that need no window (2026-10-05, <c>/process &lt;id&gt;</c>): the output buffer's event and copy,
/// the feed's numbering, levels, title and kill key, the kill arm, the registry's stop by the user and its words, and the log
/// window's own feed unchanged. Real <c>cmd.exe</c> children where a session is needed, as <see cref="ProcessToolTests"/> has.
/// </summary>
public sealed class ProcessWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly Interpreters _interpreters = new(_ => null);
    private readonly ProcessRegistry _registry;
    private int _signals;

    public ProcessWindowTests()
    {
        Directory.CreateDirectory(_dir);
        _registry = new ProcessRegistry(new ShellRunner(_time), new Random(11), () => Interlocked.Increment(ref _signals));
    }

    public void Dispose()
    {
        _registry.Dispose();
        GitAccessTests.DeleteTree(_dir);
    }

    private ProcessSession Start(string command, bool notify = false) =>
        _registry.Start(ShellCommandLine.For(ShellKind.Cmd, command, _interpreters.Locate(ShellKind.Cmd)!, _dir), notify);

    private static async Task Exit(ProcessSession session)
    {
        await session.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(50);   // the registry's watcher runs after the exit
    }

    // ── OutputBuffer ────────────────────────────────────────────────────────

    [Fact]
    public void OutputBuffer_Appended_IsRaisedOncePerLine_OutsideTheLock()
    {
        var buffer = new OutputBuffer();
        int raised = 0;
        bool readElsewhere = true;
        buffer.Appended += () =>
        {
            raised++;
            // Another thread reads the buffer while the event runs: under the lock this would wait for good.
            readElsewhere &= Task.Run(() => buffer.TotalLines).Wait(TimeSpan.FromSeconds(5));
        };

        buffer.Append("one", isError: false);
        buffer.Append("two", isError: true);

        Assert.Equal(2, raised);
        Assert.True(readElsewhere);
    }

    [Fact]
    public void OutputBuffer_CopyFrom_NumbersFromTheOldestKept()
    {
        var buffer = new OutputBuffer(maxLines: 3);
        foreach (string text in new[] { "a", "b", "c", "d", "e" })
        {
            buffer.Append(text, isError: false);
        }

        var lines = new List<OutputLine>();
        Assert.Equal(3, buffer.CopyFrom(0, lines));   // lines 1 and 2 are gone
        Assert.Equal(["c", "d", "e"], lines.Select(l => l.Text));

        lines.Clear();
        Assert.Equal(3, buffer.CopyFrom(5, lines));
        Assert.Equal(["e"], lines.Select(l => l.Text));

        lines.Clear();
        Assert.Equal(3, buffer.CopyFrom(6, lines));
        Assert.Empty(lines);

        Assert.Equal(1, new OutputBuffer().CopyFrom(0, lines));   // nothing written: the next number
    }

    // ── ProcessFeed ─────────────────────────────────────────────────────────

    [Fact]
    public void Feed_NumbersLinesByTheBuffer_StderrAsAWarning_AndTheStateDropsFromTheFront()
    {
        var buffer = new OutputBuffer(maxLines: 3);
        buffer.Append("out 1", isError: false);
        buffer.Append("err 2", isError: true);
        var scratch = new List<OutputLine>();
        var into = new List<LogLine>();

        long first = ProcessFeed.Copy(buffer, 0, scratch, into);

        Assert.Equal(1, first);
        Assert.Equal([new LogLine(1, DiagnosticLevel.Info, "out 1"), new LogLine(2, DiagnosticLevel.Warning, "err 2")], into);

        var state = new LogViewState();
        state.Resize(80, 10);
        state.Append(into, first);
        Assert.Equal(3, state.NextSeq);

        foreach (string text in new[] { "out 3", "out 4", "out 5" })
        {
            buffer.Append(text, isError: false);
        }

        into.Clear();
        first = ProcessFeed.Copy(buffer, state.NextSeq, scratch, into);
        Assert.Equal(3, first);                                     // 1 and 2 went from the buffer
        Assert.Equal([3L, 4L, 5L], into.Select(l => l.Seq));
        state.Append(into, first);
        Assert.Equal(3, state.LineCount);                           // and from the window's state with them
        Assert.Equal(6, state.NextSeq);
    }

    [WindowsFact]
    public async Task Feed_RaisesOnALine_AndAtTheExit_AndItsTitleFollowsTheState()
    {
        var session = Start("ping -n 30 127.0.0.1 >nul");
        using var feed = new ProcessFeed(session, _ => { }, _time);
        int raised = 0;
        feed.Appended += () => Interlocked.Increment(ref raised);

        Assert.Equal(session.Id + " · ping -n 30 127.0.0.1 >nul — running", feed.Title(following: true));
        Assert.Equal(session.Id + " · ping -n 30 127.0.0.1 >nul — running (paused: Ctrl+E follows)", feed.Title(following: false));
        Assert.Equal("No output yet.", feed.Empty);

        session.Output.Append("a line", isError: false);   // as a pump would
        Assert.Equal(1, Volatile.Read(ref raised));

        session.Kill();
        await Exit(session);
        Assert.True(Volatile.Read(ref raised) >= 2);
        Assert.Equal(session.Id + " · ping -n 30 127.0.0.1 >nul — killed", feed.Title(following: true));

        feed.Dispose();
        int before = Volatile.Read(ref raised);
        session.Output.Append("after", isError: false);
        Assert.Equal(before, Volatile.Read(ref raised));   // disposed: no more news
    }

    [WindowsFact]
    public async Task Feed_TitleSaysExitedN_OrStoppedByYou()
    {
        var done = Start("exit /b 4");
        await Exit(done);
        using (var feed = new ProcessFeed(done, _ => { }, _time))
        {
            Assert.Equal(done.Id + " · exit /b 4 — exited 4", feed.Title(following: true));
        }

        var stopped = Start("ping -n 30 127.0.0.1 >nul");
        using var stoppedFeed = new ProcessFeed(stopped, _ => { }, _time);
        Assert.True(_registry.StopByUser(stopped));
        await Exit(stopped);
        Assert.Equal(stopped.Id + " · ping -n 30 127.0.0.1 >nul — stopped by you", stoppedFeed.Title(following: true));
    }

    [WindowsFact]
    public async Task Feed_CtrlKTwice_Stops_TheFirstArmsTheTitle_AndItLapses()
    {
        var session = Start("ping -n 30 127.0.0.1 >nul");
        var stops = new List<ProcessSession>();
        using var feed = new ProcessFeed(session, stops.Add, _time);
        int raised = 0;
        feed.Appended += () => raised++;

        Assert.False(feed.Key(ProcessFeed.VkK, control: false, repeat: false));   // a plain K is not the feed's
        Assert.False(feed.Key(0x4C, control: true, repeat: false));               // nor Ctrl+L

        Assert.True(feed.Key(ProcessFeed.VkK, control: true, repeat: false));
        Assert.Equal("Press Ctrl+K again to stop " + session.Id, feed.Title(following: true));
        Assert.Empty(stops);

        _time.Advance(TimeSpan.FromSeconds(4));                     // the window lapses: the title goes back, and the window is told
        Assert.True(raised >= 1);
        Assert.Equal(session.Id + " · ping -n 30 127.0.0.1 >nul — running", feed.Title(following: true));

        Assert.True(feed.Key(ProcessFeed.VkK, control: true, repeat: false));     // armed again
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(feed.Key(ProcessFeed.VkK, control: true, repeat: false));     // and fired
        Assert.Equal([session], stops);

        session.Kill();
        await Exit(session);
        Assert.False(feed.Key(ProcessFeed.VkK, control: true, repeat: false));    // an exited process: the key goes on to the chat
        Assert.False(feed.Key(ProcessFeed.VkK, control: true, repeat: true));
        Assert.Single(stops);
        Assert.DoesNotContain("Press Ctrl+K", feed.Title(following: true), StringComparison.Ordinal);
    }

    [WindowsFact]
    public void Feed_HeldCtrlK_IsOnePress_TheRepeatTakenButNeverFiring()
    {
        var session = Start("ping -n 30 127.0.0.1 >nul");
        var stops = new List<ProcessSession>();
        using var feed = new ProcessFeed(session, stops.Add, _time);
        try
        {
            Assert.True(feed.Key(ProcessFeed.VkK, control: true, repeat: false));    // armed
            _time.Advance(TimeSpan.FromMilliseconds(500));
            Assert.True(feed.Key(ProcessFeed.VkK, control: true, repeat: true));     // the keyboard's repeat: taken, never the second
            Assert.True(feed.Key(ProcessFeed.VkK, control: true, repeat: true));
            Assert.Empty(stops);
            Assert.Equal("Press Ctrl+K again to stop " + session.Id, feed.Title(following: true));   // still armed

            Assert.True(feed.Key(ProcessFeed.VkK, control: true, repeat: false));    // a fresh press fires
            Assert.Equal([session], stops);
        }
        finally
        {
            session.Kill();
        }
    }

    [WindowsFact]
    public void Feed_DisposedTwice_IsHarmless()
    {
        var session = Start("ping -n 30 127.0.0.1 >nul");
        var feed = new ProcessFeed(session, _ => { }, _time);
        feed.Dispose();
        feed.Dispose();
        session.Kill();
    }

    [Fact]
    public void DiagnosticFeed_IsTheLogWindowAsBefore()
    {
        using var buffer = new DiagnosticBuffer(4);
        using var feed = new DiagnosticFeed(buffer);
        int raised = 0;
        feed.Appended += () => raised++;

        buffer.Add(new DiagnosticEvent(DateTime.UnixEpoch, DiagnosticLevel.Warning, "Test", "hello", null));
        var lines = new List<LogLine>();

        Assert.Equal(0, feed.CopySince(0, lines));
        Assert.Single(lines);
        Assert.Equal(1, raised);
        Assert.Equal(LogViewText.Title, feed.Title(following: true));
        Assert.Equal(LogViewText.PausedTitle, feed.Title(following: false));
        Assert.Equal(LogViewText.Empty, feed.Empty);
        Assert.False(feed.Key(ProcessFeed.VkK, control: true, repeat: false));
    }

    // ── ProcessKillArm ──────────────────────────────────────────────────────

    [Fact]
    public void KillArm_OnePressArms_ASecondWithinTheWindowFires_ALateOneStartsOver()
    {
        var arm = new ProcessKillArm();
        var t = ManualTimeProvider.DefaultUtcNow;

        Assert.Equal(KillPress.Armed, arm.Press(t, exited: false));
        Assert.True(arm.IsArmed(t + TimeSpan.FromSeconds(2.9)));
        Assert.Equal(KillPress.Fire, arm.Press(t + TimeSpan.FromSeconds(2.9), exited: false));
        Assert.False(arm.IsArmed(t + TimeSpan.FromSeconds(3)));     // fired: disarmed

        Assert.Equal(KillPress.Armed, arm.Press(t + TimeSpan.FromSeconds(10), exited: false));
        Assert.False(arm.IsArmed(t + TimeSpan.FromSeconds(13)));    // exactly the window: lapsed
        Assert.Equal(KillPress.Armed, arm.Press(t + TimeSpan.FromSeconds(13), exited: false));   // so it arms again
        Assert.Equal(ProcessKillArm.Window, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void KillArm_AnExitedProcess_IgnoresThePress_AndDisarms()
    {
        var arm = new ProcessKillArm();
        var t = ManualTimeProvider.DefaultUtcNow;

        Assert.Equal(KillPress.Armed, arm.Press(t, exited: false));
        Assert.Equal(KillPress.Ignored, arm.Press(t + TimeSpan.FromSeconds(1), exited: true));
        Assert.False(arm.IsArmed(t + TimeSpan.FromSeconds(1)));
    }

    // ── ProcessRegistry.StopByUser ──────────────────────────────────────────

    [WindowsFact]
    public async Task StopByUser_AlertsAndNotes_WithoutNotify_InTheUsersWords()
    {
        var session = Start("ping -n 30 127.0.0.1 >nul");   // started without notify
        Assert.True(_registry.StopByUser(session));
        await Exit(session);

        Assert.True(session.StoppedByUser);
        Assert.True(_registry.TryTakeAlert(out var alert));
        Assert.True(alert.ByUser);
        Assert.Equal(session.Id + " was stopped by you after 0.0 s: ping -n 30 127.0.0.1 >nul", ShellText.AlertLine(alert));
        Assert.Equal([session.Id], _registry.TakeNotes());
        Assert.Equal(1, _signals);
        Assert.Equal(session.Id + " stopped by the user after 0.0 s (cmd): ping -n 30 127.0.0.1 >nul — no new output", ShellText.PollHeader(session, 0));
        // The model's list and log say the same as its poll, not "exit 1" (2026-10-05, the code review).
        Assert.Equal(session.Id + "  stopped by the user 0.0 s     cmd        ping -n 30 127.0.0.1 >nul", ShellText.ListRow(session));
        Assert.Equal(session.Id + " no lines (stopped by the user): ping -n 30 127.0.0.1 >nul", ShellText.LogHeader(session, 0, 0));
        Assert.Equal(session.Id + "  stopped by you 0.0 s     cmd        ping -n 30 127.0.0.1 >nul", ProcessWindowText.Row(session));

        Assert.False(_registry.StopByUser(session));   // exited already
    }

    [WindowsFact]
    public async Task StopByUser_AChildGoneWhileItsOutputDrains_IsNotStopped_NorNotified()
    {
        // cmd exits at once while the ping it started holds the output pipe open: the session is not HasExited until the ping ends,
        // but there is nothing left to kill. The stop must say so and leave Notify alone (2026-10-05, the code review).
        var session = Start("start /b ping -n 4 127.0.0.1 & exit /b 3");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!ParentGone(session.Pid) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(ParentGone(session.Pid));
        Assert.False(session.HasExited);                // the ping still drains into the pipe
        Assert.False(_registry.StopByUser(session));
        Assert.False(session.Notify);

        await Exit(session);
        Assert.False(session.StoppedByUser);
        Assert.Equal(3, session.ExitCode);
        Assert.False(_registry.TryTakeAlert(out _));
        Assert.Empty(_registry.TakeNotes());

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

    [WindowsFact]
    public async Task StopByUser_RefusesAnotherRegistrysSession_AndAnOwnExitIsNoUserStop()
    {
        using var other = new ProcessRegistry(new ShellRunner(_time), new Random(3), () => { });
        var foreign = other.Start(ShellCommandLine.For(ShellKind.Cmd, "ping -n 30 127.0.0.1 >nul", _interpreters.Locate(ShellKind.Cmd)!, _dir), notify: false);
        Assert.False(_registry.StopByUser(foreign));
        Assert.False(foreign.HasExited);

        var own = Start("echo hi", notify: true);
        await Exit(own);
        Assert.False(own.StoppedByUser);
        Assert.True(_registry.TryTakeAlert(out var alert));
        Assert.False(alert.ByUser);
        Assert.Equal(own.Id + " exited 0 after 0.0 s: echo hi", ShellText.AlertLine(alert));
    }

    // ── ProcessWindowText ───────────────────────────────────────────────────

    [WindowsFact]
    public async Task Words_ArePinned()
    {
        var session = Start("echo one");
        await Exit(session);

        Assert.Equal("/process", ProcessWindowText.Word);
        Assert.Equal(session.Id + "  exited 0       0.0 s     cmd        echo one", ProcessWindowText.Row(session));
        Assert.Equal("exited 0 · echo one", ProcessWindowText.CompletionNote(session));
        Assert.Equal("echo one echo two", ProcessWindowText.Label("echo one\r\n  echo two\n"));
        Assert.Equal(new string('x', 79) + "…", ProcessWindowText.Label(new string('x', 200)));
        Assert.Equal("No process matches 'proc_9'; /process lists them.", ProcessWindowText.NoMatchError("proc_9"));
        Assert.Equal("'proc_' matches proc_1, proc_2; type more of the id.", ProcessWindowText.AmbiguousError("proc_", ["proc_1", "proc_2"]));
        Assert.Equal("/process takes nothing or a process id, not 'a b'.", ProcessWindowText.UsageError("a b"));
        Assert.Equal("(⚡ proc_1 in the process window; Ctrl+K twice there stops it)", ProcessWindowText.OpenedNotice("proc_1"));
        Assert.Equal("Could not open the process window: no thread", ProcessWindowText.WindowFailedError("no thread"));
    }
}
