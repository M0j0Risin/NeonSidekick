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
}
