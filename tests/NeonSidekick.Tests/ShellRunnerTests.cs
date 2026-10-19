using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The runner over real children (2026-09-21): <c>cmd.exe</c> built-ins alone, so nothing depends on
/// what is installed. The exit code, both streams pumped and tagged, UTF-8 through <c>chcp</c>, stdin
/// closed or written, a kill that ends the tree, and the session's bookkeeping. Timeouts use the
/// manual clock (<see cref="ProcessSession.WaitAsync"/> races <c>Task.Delay</c> on it), so a test
/// never sleeps for real.
/// </summary>
public sealed class ShellRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly ShellRunner _runner;
    private readonly Interpreters _interpreters = new(_ => null);

    public ShellRunnerTests()
    {
        Directory.CreateDirectory(_dir);
        _runner = new ShellRunner(_time);
    }

    public void Dispose() => GitAccessTests.DeleteTree(_dir);

    private ProcessLaunch Cmd(string command) => ShellCommandLine.For(ShellKind.Cmd, command, _interpreters.Locate(ShellKind.Cmd)!, _dir);

    private ProcessLaunch PowerShell(string command) => ShellCommandLine.For(ShellKind.PowerShell, command, _interpreters.Locate(ShellKind.PowerShell)!, _dir);

    private ProcessLaunch Zsh(string command) => ShellCommandLine.For(ShellKind.Zsh, command, _interpreters.Locate(ShellKind.Zsh)!, _dir);

    private static async Task<int> ExitAsync(ProcessSession session) => await session.Exited.WaitAsync(TimeSpan.FromSeconds(60));

    [WindowsFact]
    public async Task Cmd_Echo_ExitsZero_WithTheLineOnStdout()
    {
        using var session = _runner.Start(Cmd("echo hi"), "run_000001");
        session.CloseInput();

        Assert.Equal(0, await ExitAsync(session));
        Assert.True(session.HasExited);
        Assert.Equal(0, session.ExitCode);
        Assert.Equal([new OutputLine("hi", false)], session.Output.Lines());
        Assert.Equal("run_000001", session.Id);
        Assert.Equal("echo hi", session.Label);
        Assert.Equal("cmd", session.Kind);
        Assert.True(session.Pid > 0);
        Assert.False(session.Killed);
    }

    [WindowsFact]
    public async Task Cmd_ExitCode_Stderr_AndUtf8_ComeThrough()
    {
        using var session = _runner.Start(Cmd("echo ü & echo err 1>&2 & exit /b 3"), "run_000002");
        session.CloseInput();

        Assert.Equal(3, await ExitAsync(session));
        // cmd's echo keeps the spaces around a redirect and before &; the two pipes arrive in no fixed order, so each stream is checked alone.
        Assert.Equal(["ü "], session.Output.Lines().Where(l => !l.IsError).Select(l => l.Text));
        Assert.Equal(["err  "], session.Output.Lines().Where(l => l.IsError).Select(l => l.Text));
        Assert.True(await session.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));   // already exited: true at once
    }

    [WindowsFact]
    public async Task Cmd_ReadsStdin_UntilItIsClosed()
    {
        using var session = _runner.Start(Cmd("set /p name=&& call echo hello %name%"), "run_000003");   // call: %name% is expanded when the line is parsed, before set ran
        Assert.True(await session.WriteAsync("world\n", CancellationToken.None));
        session.CloseInput();

        Assert.Equal(0, await ExitAsync(session));
        Assert.Equal("hello world", session.Output.Lines()[^1].Text);
        Assert.False(await session.WriteAsync("late\n", CancellationToken.None));   // closed
    }

    [WindowsFact]
    public async Task Kill_EndsAWaitingChild_AndTheSessionSaysSo()
    {
        using var session = _runner.Start(Cmd("ping -n 30 127.0.0.1 >nul"), "run_000004");
        session.CloseInput();
        var wait = session.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(11));

        Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(30)));   // the manual clock ran out first
        session.Kill();
        await ExitAsync(session);
        Assert.True(session.Killed);
        Assert.True(session.HasExited);
        Assert.NotEqual(0, session.ExitCode);
        session.Kill();   // a second kill is a no-op
    }

    [WindowsFact]
    public async Task WaitAsync_TheTurnToken_Throws()
    {
        using var session = _runner.Start(Cmd("ping -n 30 127.0.0.1 >nul"), "run_000005");
        using var cts = new CancellationTokenSource();
        var wait = session.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        session.Kill();
        await ExitAsync(session);
    }

    [WindowsFact]
    public async Task Dispose_KillsARunningChild()
    {
        var session = _runner.Start(Cmd("ping -n 30 127.0.0.1 >nul"), "run_000006");
        session.Dispose();

        Assert.True(session.Killed);
        // Dispose waited for the kill to land before the handle went (2026-09-22), so the runner's WaitForExitAsync
        // saw the exit and the pumps the pipes close: the session completes soon after, never hangs.
        Assert.NotEqual(0, await session.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(session.HasExited);
    }

    [Fact]
    public void Start_AMissingProgram_IsTheStartSentence()
    {
        var ex = Assert.Throws<ShellStartException>(() => _runner.Start(new ProcessLaunch(Path.Combine(_dir, "nope.exe"), ["-x"], null, _dir, "nope", "cmd"), "run_000007"));
        Assert.StartsWith("Error: could not start nope.exe (", ex.Message);
    }

    [WindowsFact]
    public async Task PowerShell_Runs_WithTheWrapper_NativeExitCodePropagates_AndUtf8Out()
    {
        using var ok = _runner.Start(PowerShell("Write-Output 'héllo'; cmd /c exit 7"), "run_000008");
        ok.CloseInput();
        Assert.Equal(7, await ExitAsync(ok));
        Assert.Equal("héllo", ok.Output.Lines()[0].Text);

        using var failed = _runner.Start(PowerShell("Get-Item 'C:\\definitely\\not\\here\\x.txt'"), "run_000009");
        failed.CloseInput();
        Assert.Equal(1, await ExitAsync(failed));   // a cmdlet's error: $? false, exit 1
        Assert.Contains(failed.Output.Lines(), l => l.IsError);

        using var clean = _runner.Start(PowerShell("Write-Output 'fine'"), "run_000010");
        clean.CloseInput();
        Assert.Equal(0, await ExitAsync(clean));
    }

    [Fact]
    public void NewId_IsThePrefixAndSixHex()
    {
        string id = ShellRunner.NewId(new Random(1), "proc_");
        Assert.StartsWith("proc_", id);
        Assert.Equal(11, id.Length);
        Assert.Matches("^proc_[0-9a-f]{6}$", id);
        Assert.Matches("^run_[0-9a-f]{6}$", ShellRunner.NewId(new Random(2), "run_"));
    }

    /// <summary>
    /// <c>name.cmd</c>: it starts a grandchild that writes its pid to <c>name.pid</c> and sleeps, and exits at once. With
    /// <paramref name="detached"/> the grandchild comes by <c>Start-Process</c> (ShellExecute: it takes none of the pipes, so the command's
    /// session can end); without, by <c>start /b</c>, which hands it the pipes, so the session lasts as long as it does.
    /// </summary>
    private void WriteOrphan(string name, bool detached)
    {
        string body = "$PID | Out-File -Encoding ascii " + name + ".pid; Start-Sleep 120";
        File.WriteAllText(Path.Combine(_dir, name + ".cmd"), detached
            ? "@powershell -NoProfile -Command \"Start-Process powershell -WindowStyle Hidden -ArgumentList '-NoProfile','-Command','" + body + "'\"\r\n"
            : "@start /b powershell -NoProfile -Command \"" + body + "\" >nul 2>nul\r\n");
    }

    /// <summary>The pid the grandchild of <see cref="WriteOrphan"/> wrote, once it is there.</summary>
    private async Task<int> OrphanPidAsync(string name)
    {
        string pidFile = Path.Combine(_dir, name + ".pid");
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            try
            {
                // Whole once its newline is there; while PowerShell still holds it open the read is refused.
                if (File.Exists(pidFile) && File.ReadAllText(pidFile) is var text && text.EndsWith('\n') && int.TryParse(text.Trim(), out int pid))
                {
                    return pid;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("the grandchild never wrote " + pidFile);
    }

    private static async Task<bool> GoneAsync(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            return true;
        }
        catch (ArgumentException)
        {
            return true;   // already gone
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// The job (2026-10-12, the shell review): a grandchild whose parent already exited is outside the tree walk, so a kill left it
    /// running; the command's own job ends it with the rest.
    /// </summary>
    [WindowsFact]
    public async Task Kill_EndsAGrandchildWhoseParentIsGone()
    {
        WriteOrphan("orphan1", detached: false);
        using var session = _runner.Start(Cmd("cmd /c .\\orphan1.cmd & ping -n 120 127.0.0.1 >nul"), "run_000010");
        session.CloseInput();
        Assert.True(session.InJob);
        int orphan = await OrphanPidAsync("orphan1");

        Assert.True(session.Kill());

        Assert.True(await GoneAsync(orphan), "the orphan outlived the kill");
        await ExitAsync(session);
    }

    /// <summary>
    /// The Mac's side (2026-10-12): an inner zsh starts a <c>sleep</c> in the background and exits, so launchd adopts it and the tree
    /// walk no longer finds it; the command's own group (it leads a session of its own) still holds it, and the kill ends it.
    /// </summary>
    [UnixFact]
    public async Task Kill_EndsAGrandchildWhoseParentIsGone_Unix()
    {
        File.WriteAllText(Path.Combine(_dir, "orphan1.sh"), "sleep 120 </dev/null >/dev/null 2>&1 &\necho $! > orphan1.pid\n");
        using var session = _runner.Start(Zsh("zsh ./orphan1.sh; sleep 120"), "run_000012");
        session.CloseInput();
        int orphan = await OrphanPidAsync("orphan1");
        try
        {
            Assert.True(session.Kill());

            Assert.True(await GoneAsync(orphan), "the orphan outlived the kill");
            await ExitAsync(session);
        }
        finally
        {
            KillIfThere(orphan);
        }
    }

    /// <summary>
    /// The Mac's side of <see cref="ABackgroundCommandsLeftovers_EndWithItsSession_AForegroundsDoNot"/> (2026-10-12, the user's call): a
    /// background command's group is killed when its session is closed, even after the command itself exited; a foreground one's is not.
    /// </summary>
    [UnixFact]
    public async Task ABackgroundCommandsLeftovers_EndWithItsSession_AForegroundsDoNot_Unix()
    {
        const string Leave = "nohup sleep 120 </dev/null >/dev/null 2>&1 &\necho $! > {0}.pid\n";
        using var registry = new ProcessRegistry(_runner, new Random(3), () => { });
        File.WriteAllText(Path.Combine(_dir, "orphan2.sh"), string.Format(System.Globalization.CultureInfo.InvariantCulture, Leave, "orphan2"));
        File.WriteAllText(Path.Combine(_dir, "orphan3.sh"), string.Format(System.Globalization.CultureInfo.InvariantCulture, Leave, "orphan3"));
        var background = registry.Start(Zsh("zsh ./orphan2.sh"), notify: false);
        int left = await OrphanPidAsync("orphan2");
        int opened = 0;
        try
        {
            await ExitAsync(background);
            Assert.False(await GoneQuicklyAsync(left));   // still running after its parent's exit

            Assert.True(registry.Close(background));
            Assert.True(await GoneAsync(left), "the background command's leftover outlived its session");

            var foreground = _runner.Start(Zsh("zsh ./orphan3.sh"), "run_000013");
            foreground.CloseInput();
            opened = await OrphanPidAsync("orphan3");
            await ExitAsync(foreground);
            foreground.Dispose();
            Assert.False(await GoneQuicklyAsync(opened));   // a foreground command's leftover lives on
        }
        finally
        {
            KillIfThere(left);
            if (opened != 0)
            {
                KillIfThere(opened);
            }
        }
    }

    /// <summary>The Mac's child leads a session with no terminal (2026-10-12, the user's pick): a <c>/dev/tty</c> read fails at once, never waits.</summary>
    [UnixFact]
    public async Task ACommandReadingTheTerminal_FailsAtOnce_Unix()
    {
        using var session = _runner.Start(Zsh("cat /dev/tty"), "run_000014");
        session.CloseInput();

        Assert.NotEqual(0, await session.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(session.Output.Lines(), l => l.IsError && l.Text.Contains("/dev/tty", StringComparison.Ordinal));
    }

    /// <summary>The exit codes as .NET reports them on Unix (2026-10-12, <see cref="PosixExit"/>): its own code, 128 plus a signal.</summary>
    [UnixFact]
    public async Task ExitCodes_AreTheChildsOwn_OrTheSignalsAsAShellSaysThem_Unix()
    {
        using var three = _runner.Start(Zsh("exit 3"), "run_000015");
        three.CloseInput();
        Assert.Equal(3, await ExitAsync(three));

        using var terminated = _runner.Start(Zsh("kill -TERM $$"), "run_000016");
        terminated.CloseInput();
        Assert.Equal(143, await ExitAsync(terminated));

        using var killed = _runner.Start(Zsh("sleep 30"), "run_000017");
        killed.CloseInput();
        Assert.True(killed.Kill());
        Assert.Equal(137, await ExitAsync(killed));
        Assert.True(killed.Killed);
    }

    /// <summary>The smoke's <c>shell:posix-spawn</c> passes here too (2026-10-12): a session leader, ended by its group's kill.</summary>
    [MacFact]
    public void TheSmokesProbe_SpawnsASessionLeader_AndKillsItsGroup()
    {
        var check = App.SmokeChecks.ProbePosixSpawn();

        Assert.True(check.Passed, check.Detail);
        Assert.Equal("shell:posix-spawn", check.Name);
    }

    [Fact]
    public void PosixExit_IsTheStatus_Or128PlusTheSignal()
    {
        Assert.Equal(0, PosixExit.Code(PosixExit.Exited, 0));
        Assert.Equal(255, PosixExit.Code(PosixExit.Exited, 255));
        Assert.Equal(137, PosixExit.Code(PosixExit.Killed, 9));
        Assert.Equal(139, PosixExit.Code(PosixExit.Dumped, 11));
        Assert.Equal(-1, PosixExit.Code(5, 17));   // stopped: not an exit
        Assert.False(PosixExit.IsExit(5));
        Assert.True(PosixExit.IsExit(PosixExit.Killed));
    }

    /// <summary>A program or a folder that is not there (2026-10-12): libc's reason in the start error, as <c>Process.Start</c>'s was.</summary>
    [UnixFact]
    public void AMissingProgramOrFolder_IsAStartError_NamingTheProgram_Unix()
    {
        string zsh = _interpreters.Locate(ShellKind.Zsh)!;
        Assert.StartsWith("Error: could not start nope (", Assert.Throws<ShellStartException>(() => _runner.Start(ShellCommandLine.For(ShellKind.Zsh, "echo hi", Path.Combine(_dir, "nope"), _dir), "run_000018")).Message);
        Assert.StartsWith("Error: could not start zsh (", Assert.Throws<ShellStartException>(() => _runner.Start(ShellCommandLine.For(ShellKind.Zsh, "echo hi", zsh, Path.Combine(_dir, "gone")), "run_000019")).Message);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("Error: could not start zsh (a raw argument string is cmd's)", Assert.Throws<ShellStartException>(() => _runner.Start(new ProcessLaunch(zsh, null, "-c 'echo hi'", _dir, "echo hi", "zsh"), "run_000021")).Message);
        }
    }

    /// <summary>The child's environment (2026-10-12): the app's own, <see cref="ChildEnvironment.Overrides"/> on top, the launch's own last.</summary>
    [UnixFact]
    public async Task TheEnvironment_IsTheAppsWithTheOverrides_AndTheLaunchsOwn_Unix()
    {
        var launch = Zsh("echo $NO_COLOR $TERM $NS_TEST_OWN; [[ -n $PATH ]] && echo path") with { Environment = new Dictionary<string, string> { ["NS_TEST_OWN"] = "mine" } };
        using var session = _runner.Start(launch, "run_000020");
        session.CloseInput();

        Assert.Equal(0, await ExitAsync(session));
        Assert.Equal(["1 dumb mine", "path"], session.Output.Lines().Where(l => !l.IsError).Select(l => l.Text));
    }

    private static void KillIfThere(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// A background command is tied to the app (2026-10-12, the user's call): what it left running, even detached, ends when the
    /// session is closed (or evicted, or the app ends: the job is kill-on-close). A foreground command's job is not: a program it opened
    /// outlives it.
    /// </summary>
    [WindowsFact]
    public async Task ABackgroundCommandsLeftovers_EndWithItsSession_AForegroundsDoNot()
    {
        using var registry = new ProcessRegistry(_runner, new Random(3), () => { });
        WriteOrphan("orphan2", detached: true);
        var background = registry.Start(Cmd("cmd /c .\\orphan2.cmd"), notify: false);
        int left = await OrphanPidAsync("orphan2");
        await ExitAsync(background);
        Assert.False(await GoneQuicklyAsync(left));   // still running after its parent's exit

        Assert.True(registry.Close(background));
        Assert.True(await GoneAsync(left), "the background command's leftover outlived its session");

        WriteOrphan("orphan3", detached: true);
        var foreground = _runner.Start(Cmd("cmd /c .\\orphan3.cmd"), "run_000011");
        foreground.CloseInput();
        int opened = await OrphanPidAsync("orphan3");
        await ExitAsync(foreground);
        foreground.Dispose();
        Assert.False(await GoneQuicklyAsync(opened));   // a foreground command's leftover lives on
        using (var process = System.Diagnostics.Process.GetProcessById(opened))
        {
            process.Kill();
        }
    }

    private static async Task<bool> GoneQuicklyAsync(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>A reader that hands out the chunks it was given, one per read, and says after each what the buffer held.</summary>
    private sealed class ChunkedReader(OutputBuffer output, params string[] chunks) : TextReader
    {
        private int _next;

        public List<IReadOnlyList<OutputLine>> UnfinishedAfterEachRead { get; } = new();

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (_next > 0)
            {
                UnfinishedAfterEachRead.Add(output.Unfinished());
            }

            if (_next == chunks.Length)
            {
                return ValueTask.FromResult(0);
            }

            string chunk = chunks[_next++];
            chunk.AsSpan().CopyTo(buffer.Span);
            return ValueTask.FromResult(chunk.Length);
        }
    }

    /// <summary>
    /// The pump (2026-10-12, the shell review): lines end at \n or \r\n, also across reads; a lone \r starts the line over (a progress
    /// bar is its last drawing); what a read leaves without a newline is the unfinished line, a prompt; the end appends it.
    /// </summary>
    [Fact]
    public async Task Pump_SplitsLines_RedrawsOnCarriageReturns_AndKeepsAPromptAsTheUnfinishedLine()
    {
        var output = new OutputBuffer();
        var reader = new ChunkedReader(output, "one\r\ntwo\r", "\nthree\n", " 10%\r 50%\r100%\n", "ü", "ber\nName: ", "bob");

        await ShellRunner.PumpAsync(reader, output, isError: false);

        Assert.Equal(["one", "two", "three", "100%", "über", "Name: bob"], output.Lines().Select(l => l.Text));
        Assert.Equal(
            ["two", "", "", "ü", "Name: ", "Name: bob"],
            reader.UnfinishedAfterEachRead.Select(u => u.Count == 0 ? "" : Assert.Single(u).Text));
        Assert.Empty(output.Unfinished());   // the end appended it
        Assert.All(output.Lines(), l => Assert.False(l.IsError));
    }

    /// <summary>A line ending \r\r\n keeps its text (2026-10-19, the code review): Python on Windows writes \r\n to a text-mode stdout as \r\r\n, and the second \r used to clear the line.</summary>
    [Fact]
    public async Task Pump_ALineEndingWithTwoCarriageReturns_KeepsItsText()
    {
        var output = new OutputBuffer();
        var reader = new ChunkedReader(output, "a,b,c\r\r\nd,e,f\r\r\n", "tail\r\r");

        await ShellRunner.PumpAsync(reader, output, isError: false);

        Assert.Equal(["a,b,c", "d,e,f", "tail"], output.Lines().Select(l => l.Text));
    }

    [Fact]
    public async Task Pump_HoldsAHugeLineToTheCap_AsItArrives()
    {
        var output = new OutputBuffer();
        string block = new('x', ShellRunner.PumpChunk);
        var reader = new ChunkedReader(output, [.. Enumerable.Repeat(block, 64), "\nnext\n"]);

        await ShellRunner.PumpAsync(reader, output, isError: true);

        var lines = output.Lines();
        Assert.Equal(2, lines.Count);
        Assert.Equal(OutputBuffer.MaxLineChars, lines[0].Text.Length);
        Assert.EndsWith(OutputBuffer.CutMark, lines[0].Text);
        Assert.Equal("next", lines[1].Text);
        Assert.True(lines[1].IsError);
        Assert.All(reader.UnfinishedAfterEachRead.Take(64), u => Assert.True(Assert.Single(u).Text.Length <= OutputBuffer.MaxLineChars));
    }

    [Fact]
    public void Unfinished_IsStdoutsThenStderrs_AndCountsNowhere()
    {
        var output = new OutputBuffer();
        output.Append("done", false);
        output.SetUnfinished("Password: ", true);
        output.SetUnfinished(">>> ", false);

        Assert.Equal([(">>> ", false), ("Password: ", true)], output.Unfinished().Select(l => (l.Text, l.IsError)));
        Assert.Equal(1, output.TotalLines);
        Assert.Single(output.Since(0, out long next));
        Assert.Equal(1, next);
        output.SetUnfinished("", false);
        output.SetUnfinished(null, true);
        Assert.Empty(output.Unfinished());
    }
}
