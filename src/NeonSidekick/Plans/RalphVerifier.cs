using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Shell;

namespace NeonSidekick.Plans;

/// <summary>How a Ralph check ended (2026-10-18).</summary>
public enum RalphCheckKind
{
    Passed,
    Failed,
    TimedOut,

    /// <summary>Esc, the page's Stop, the kill switch.</summary>
    Cancelled,

    /// <summary>It could not start: its shell is not installed, the police refused it, the start failed.</summary>
    NotRun,

    /// <summary>
    /// The app would not run it when it was due (2026-10-19, the fourth review): the police, the policy turned off, a shell gone, or headless's allow
    /// list. It came back as <see cref="Cancelled"/> before, so the loop stopped as if the user had stopped it, and the stop tone stayed silent.
    /// </summary>
    Refused,
}

/// <summary>A check's result: how it ended, its exit code, the last lines of its output (at most <see cref="RalphLog.TailLines"/>) and how long it ran.</summary>
public sealed record RalphCheckResult(RalphCheckKind Kind, int? ExitCode, string Tail, TimeSpan Elapsed)
{
    public bool Passed => Kind == RalphCheckKind.Passed;

    /// <summary>What the log says of it: <c>passed</c>, <c>failed (exit 1)</c>, <c>timed out</c>, <c>stopped</c>, <c>not run</c>.</summary>
    public string Word => Kind switch
    {
        RalphCheckKind.Passed => "passed",
        RalphCheckKind.Failed => "failed (exit " + (ExitCode ?? -1).ToString(CultureInfo.InvariantCulture) + ")",
        RalphCheckKind.TimedOut => "timed out",
        RalphCheckKind.Cancelled => "stopped",
        RalphCheckKind.Refused => "refused",
        _ => "not run",
    };
}

/// <summary>Runs a Ralph loop's check command (2026-10-18): the screen's and headless's seam, <see cref="RalphVerifier"/> for real, a fake in the tests.</summary>
public interface IRalphVerifier
{
    /// <summary>
    /// <paramref name="command"/> run in <paramref name="shell"/> in <paramref name="workdir"/>, killed past <paramref name="timeout"/> or on
    /// <paramref name="cancellationToken"/>; never throws for the command's own failure.
    /// </summary>
    Task<RalphCheckResult> RunAsync(string command, ShellKind shell, string workdir, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// The Ralph loop's check runner (2026-10-18, the user's pick: an app-run verify command is the most foolproof check there is): <c>/cmd</c>'s launch
/// (<see cref="ShellCommandLine.For"/> on the shell snapshotted at approval, started by <see cref="ShellRunner.Start"/> — <b>no new process-start
/// site</b>), an id <c>verify_…</c>, the timeout and Esc killing it with <c>run_command</c>'s grace for the pumps to drain, the result its exit code
/// and its last <see cref="RalphLog.TailLines"/> lines. The approval of the plan is the command's consent, under <c>Shell command policy</c> ask and
/// yolo alike (no approval pane); <see cref="Refusal"/> holds the police and the policy (<c>off</c> runs nothing: the steps go unchecked).
/// </summary>
public sealed class RalphVerifier : IRalphVerifier
{
    /// <summary>The id prefix of a check run.</summary>
    public const string IdPrefix = "verify_";

    private readonly ShellRunner _runner;
    private readonly Func<ShellKind, string?> _locate;
    private readonly Random _random;

    public RalphVerifier(ShellRunner runner, Func<ShellKind, string?> locate, Random? random = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _locate = locate ?? throw new ArgumentNullException(nameof(locate));
        _random = random ?? Random.Shared;
    }

    /// <summary>
    /// Why <paramref name="command"/> may not run as a check: <c>Shell command policy</c> off (null result in <paramref name="off"/>: no check, the
    /// steps unchecked), the police's refusal, or a shell that is not installed; null when it may.
    /// </summary>
    public static string? Refusal(string command, ShellKind shell, string workdir, WorkingDirectory files, AppSettingsData effective, Func<ShellKind, string?> locate, out bool off)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(locate);
        off = CommandPolicy.Resolve(effective) == CommandPolicyMode.Off;
        if (off)
        {
            return null;
        }

        if (locate(shell) is null)
        {
            return ShellText.ShellNotInstalled(shell);
        }

        var request = new CommandRequest(ShellKinds.Name(shell), command, CommandPrefix.All(command, shell));
        return ShellPoliceChecks.Judge(request, command, workdir, files, effective) is { } policed ? policed.Shown ?? ShellText.Note(policed.ModelText) : null;
    }

    /// <summary>The log line a check's start writes (the approval it rides on).</summary>
    public static string ApprovedLogLine(string command, ShellKind shell) =>
        ShellText.ApprovedLogLine(new CommandRequest(ShellKinds.Name(shell), command, CommandPrefix.All(command, shell)), "approved with the plan");

    public async Task<RalphCheckResult> RunAsync(string command, ShellKind shell, string workdir, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(workdir);
        if (_locate(shell) is not { } executable)
        {
            return new RalphCheckResult(RalphCheckKind.NotRun, null, ShellText.ShellNotInstalled(shell), TimeSpan.Zero);
        }

        DiagnosticLog.Info(ShellKinds.Category, ApprovedLogLine(command, shell));
        ProcessSession session;
        try
        {
            session = _runner.Start(ShellCommandLine.For(shell, command, executable, workdir), ShellRunner.NewId(_random, IdPrefix));
        }
        catch (ShellStartException ex)
        {
            return new RalphCheckResult(RalphCheckKind.NotRun, null, ex.Message, TimeSpan.Zero);
        }

        await using (session.ConfigureAwait(false))
        {
            session.CloseInput();
            bool exited = false;
            bool cancelled = false;
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(timeout);
            try
            {
                await session.Exited.WaitAsync(timer.Token).ConfigureAwait(false);
                exited = true;
            }
            catch (OperationCanceledException)
            {
                cancelled = cancellationToken.IsCancellationRequested;
            }

            if (!exited)
            {
                session.Kill();
                await session.WaitAsync(Llm.Tools.RunCommandTool.KillGrace, CancellationToken.None).ConfigureAwait(false);
            }

            var lines = session.Output.Snapshot(out _);
            string tail = string.Join('\n', lines.Skip(Math.Max(0, lines.Count - RalphLog.TailLines)).Select(l => l.Text));
            var kind = cancelled ? RalphCheckKind.Cancelled
                : !exited ? RalphCheckKind.TimedOut
                : session.ExitCode == 0 ? RalphCheckKind.Passed
                : RalphCheckKind.Failed;
            string outcome = kind == RalphCheckKind.Failed ? "exit " + (session.ExitCode ?? -1).ToString(CultureInfo.InvariantCulture) : kind.ToString().ToLowerInvariant();
            DiagnosticLog.Info(ShellKinds.Category, $"ralph check: {outcome} in {ShellText.Elapsed(session.Elapsed)} — {ShellKinds.Name(shell)} {command}");
            return new RalphCheckResult(kind, exited ? session.ExitCode : null, tail, session.Elapsed);
        }
    }
}
