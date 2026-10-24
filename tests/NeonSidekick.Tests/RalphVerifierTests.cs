using NeonSidekick.Files;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The Ralph loop's check runner (2026-10-18, <see cref="RalphVerifier"/>) over real shells: a pass, a failing exit code, the output's tail, a
/// timeout and a cancel; and its refusals (the policy off, the police, a missing shell).
/// </summary>
public sealed class RalphVerifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly Interpreters _interpreters = new(name => Environment.GetEnvironmentVariable(name));
    private readonly RalphVerifier _verifier;

    public RalphVerifierTests()
    {
        Directory.CreateDirectory(_dir);
        _verifier = new RalphVerifier(new ShellRunner(TimeProvider.System), _interpreters.Locate, new Random(7));
    }

    public void Dispose() => GitAccessTests.DeleteTree(_dir);

    private static ShellKind Shell => OperatingSystem.IsWindows() ? ShellKind.Cmd : ShellKind.Bash;

    private Task<RalphCheckResult> Run(string command, int seconds = 60, CancellationToken cancellationToken = default) =>
        _verifier.RunAsync(command, Shell, _dir, TimeSpan.FromSeconds(seconds), cancellationToken);

    [WindowsFact]
    public async Task Cmd_EchoPasses_AndAnExitCodeFails()
    {
        var passed = await Run("echo ok");
        Assert.Equal((RalphCheckKind.Passed, 0, "ok"), (passed.Kind, passed.ExitCode, passed.Tail.Trim()));
        Assert.Equal("passed", passed.Word);

        var failed = await Run("echo bad & exit /b 3");
        Assert.Equal((RalphCheckKind.Failed, 3), (failed.Kind, failed.ExitCode));
        Assert.Equal("failed (exit 3)", failed.Word);
        Assert.Contains("bad", failed.Tail);
    }

    [UnixFact]
    public async Task Sh_EchoPasses_AndAnExitCodeFails()
    {
        var passed = await Run("echo ok");
        Assert.Equal((RalphCheckKind.Passed, "ok"), (passed.Kind, passed.Tail.Trim()));
        var failed = await Run("echo bad; exit 3");
        Assert.Equal((RalphCheckKind.Failed, 3), (failed.Kind, failed.ExitCode));
    }

    [WindowsFact]
    public async Task TheTail_IsTheLastFortyLines()
    {
        var result = await Run("for /l %i in (1,1,60) do @echo line %i");

        var lines = result.Tail.Split('\n');
        Assert.Equal(RalphLog.TailLines, lines.Length);
        Assert.Equal("line 60", lines[^1].Trim());
        Assert.Equal("line 21", lines[0].Trim());
    }

    [UnixFact]
    public async Task TheTail_IsTheLastFortyLines_Unix()
    {
        var result = await Run("for i in $(seq 1 60); do echo line $i; done");

        var lines = result.Tail.Split('\n');
        Assert.Equal(RalphLog.TailLines, lines.Length);
        Assert.Equal("line 60", lines[^1].Trim());
    }

    [WindowsFact]
    public async Task ACheckPastItsTimeout_IsKilled()
    {
        var result = await Run("ping -n 30 127.0.0.1 >nul", seconds: 1);

        Assert.Equal(RalphCheckKind.TimedOut, result.Kind);
        Assert.Null(result.ExitCode);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(20));
    }

    [UnixFact]
    public async Task ACheckPastItsTimeout_IsKilled_Unix()
    {
        var result = await Run("sleep 30", seconds: 1);
        Assert.Equal(RalphCheckKind.TimedOut, result.Kind);
    }

    [Fact]
    public async Task ACancelledCheck_IsStopped()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var result = await Run(OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 >nul" : "sleep 30", cancellationToken: cts.Token);

        Assert.Equal(RalphCheckKind.Cancelled, result.Kind);
        Assert.Equal("stopped", result.Word);
    }

    [Fact]
    public void Refusal_IsThePolicyOff_ThePolice_OrAMissingShell()
    {
        var files = new WorkingDirectory(() => _dir, new ManualTimeProvider());
        var settings = new AppSettingsData { ShellCommandPolicy = "off" };
        Assert.Null(RalphVerifier.Refusal("echo ok", Shell, _dir, files, settings, _interpreters.Locate, out bool off));
        Assert.True(off);

        settings.ShellCommandPolicy = "yolo";
        Assert.Null(RalphVerifier.Refusal("echo ok", Shell, _dir, files, settings, _interpreters.Locate, out off));
        Assert.False(off);
        Assert.NotNull(RalphVerifier.Refusal(OperatingSystem.IsWindows() ? "type C:\\Windows\\win.ini" : "cat /etc/passwd", Shell, _dir, files, settings, _interpreters.Locate, out _));
        Assert.Equal(ShellText.ShellNotInstalled(Shell), RalphVerifier.Refusal("echo ok", Shell, _dir, files, settings, _ => null, out _));
    }

    [Fact]
    public void AScriptNotThereYet_IsNotRun_UnderThePolice_AndPassesTheStartsPreCheck()
    {
        // 2026-10-24, the user's report: a plan whose step writes the script its check runs was refused before it started.
        var files = new WorkingDirectory(() => _dir, new ManualTimeProvider());
        var settings = new AppSettingsData { ShellCommandPolicy = "yolo", ShellPolice = true };
        Assert.Equal("hello.py", RalphVerifier.MissingScript("python hello.py --test", files));
        Assert.NotNull(RalphVerifier.Refusal("python hello.py --test", Shell, _dir, files, settings, _interpreters.Locate, out _));
        Assert.Null(RalphVerifier.Refusal("python hello.py --test", Shell, _dir, files, settings, _interpreters.Locate, out _, missingScriptsPass: true));

        var notThere = RalphVerifier.NotThereYet("python hello.py --test", Shell, files, settings);
        Assert.Equal((RalphCheckKind.NotThereYet, "hello.py", "script not there yet"), (notThere!.Kind, notThere.Script, notThere.Word));
        Assert.Equal(RalphText.ScriptNotThereTail("hello.py"), notThere.Tail);

        // The police off: the line runs (and fails) as before; the policy off: the caller's refusal says so; the script there: nothing to say.
        Assert.Null(RalphVerifier.NotThereYet("python hello.py --test", Shell, files, new AppSettingsData { ShellCommandPolicy = "yolo", ShellPolice = false }));
        Assert.Null(RalphVerifier.NotThereYet("python hello.py --test", Shell, files, new AppSettingsData { ShellCommandPolicy = "off", ShellPolice = true }));
        Assert.Equal("hello.py", RalphVerifier.MissingScript("python hello.py --test", files));
        File.WriteAllText(Path.Combine(_dir, "hello.py"), "print('hi')");
        Assert.Null(RalphVerifier.MissingScript("python hello.py --test", files));
        Assert.Null(RalphVerifier.NotThereYet("python hello.py --test", Shell, files, settings));
    }

    [Fact]
    public void Refusal_APowerShellCommandTooLongToPass_IsRefused()
    {
        // 2026-10-20, a review: run_command and /cmd refused it; a check failed to start and read as a failed check.
        var files = new WorkingDirectory(() => _dir, new ManualTimeProvider());
        var settings = new AppSettingsData { ShellCommandPolicy = "yolo" };
        string command = "Write-Output '" + new string('a', ShellCommandLine.MaxCommandChars) + "'";

        Assert.Equal(ShellText.CommandTooLong(ShellCommandLine.MaxCommandChars), RalphVerifier.Refusal(command, ShellKind.PowerShell, _dir, files, settings, _ => "pwsh", out _));
    }
}
