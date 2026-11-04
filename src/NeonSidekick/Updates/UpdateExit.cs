using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Updates;

/// <summary>
/// The swap at exit, around <see cref="UpdateSwap"/> (2026-11-04, self-update): only when the interactive app exits, on both of <c>Program.cs</c>'s ways out. A failed swap never changes the exit code. It runs once: a window closed or a signal arriving during an <c>/exit</c>'s swap reaches here too, on another
/// thread, so a one-shot gate lets the first caller run and the second wait for it. After a caught crash it still runs: the recheck vouches for the stage, and the new version may hold the fix for a crash that would otherwise keep recurring.
/// </summary>
public sealed class UpdateExit
{
    /// <summary>A Mac's signal path has no window close's limit, so the swap gives itself the same budget.</summary>
    public static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(4);

    private readonly UpdateContext _context;
    private readonly IUpdateFiles? _files;
    private readonly ManualResetEventSlim _done = new();
    private UpdateLock? _held;
    private int _started;

    public UpdateExit(UpdateContext context, UpdateLock? held, IUpdateFiles? files = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _held = held;
        _files = files;
    }

    /// <summary>
    /// The exit's work, once. <paramref name="say"/> prints the quit lines and is null for a window close, which can't show anything, so its failure goes in <c>swap-failed.txt</c> for the next interactive start. <paramref name="budget"/> limits the recheck: if it can't finish, nothing moves and the install waits for the next exit.
    /// </summary>
    public void Run(Action<string>? say, TimeSpan? budget = null)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            _done.Wait();
            return;
        }

        // Its log lines stay off the console: the screen restored the echo as it ended, so they'd print among the quit lines. The --log file still gets them.
        bool echo = DiagnosticLog.EchoToConsole;
        DiagnosticLog.EchoToConsole = false;
        try
        {
            using var limit = budget is { } time ? new CancellationTokenSource(time) : new CancellationTokenSource();
            Install(say, limit.Token);
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Info(UpdateJournal.Category, UpdateText.RecheckOutOfTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticLog.Warn(UpdateJournal.Category, ex.Message);
            Report(say, ex.Message);
        }
        finally
        {
            _held?.Dispose();
            _held = null;
            DiagnosticLog.EchoToConsole = echo;
            _done.Set();
        }
    }

    private void Install(Action<string>? say, CancellationToken cancellationToken)
    {
        var folder = _context.Folder;
        bool rollback = File.Exists(folder.Rollback);
        if (!folder.IsPackageOrMidSwap || (!rollback && !File.Exists(folder.Ready)))
        {
            return;
        }

        // A renamed copy's exit doesn't swap, by the same name check as preflight: the stage waits for an exit of the listed exe. A manifest set aside by an interrupted swap is what the settling step below puts back.
        if (folder.IsPackage && (UpdateManifest.ReadFile(folder.Manifest, out _) is not { } current || !_context.IsListedExe(current)))
        {
            return;
        }

        // Closes this copy's own shared handle, then needs the lock exclusively, so if another copy runs from this folder, the stage waits for a later interactive exit.
        _held?.Dispose();
        _held = UpdateLock.TryExclusive(folder.InUseLock);
        if (_held is null)
        {
            DiagnosticLog.Info(UpdateJournal.Category, UpdateText.OtherCopyOpen);
            say?.Invoke(UpdateText.OtherCopyOpen);
            return;
        }

        var swap = new UpdateSwap(folder, _files) { BetweenSteps = _context.BetweenSteps };
        if (!SettleLeftover(swap))
        {
            return;
        }

        if (rollback)
        {
            GoBack(swap, say, cancellationToken);
        }
        else
        {
            GoForward(swap, say, cancellationToken);
        }
    }

    // An unfinished journal this far means the start couldn't deal with it (a locked file, another copy holding the lock). A new swap would write over the only record of those moves, so it's settled first, and this exit swaps
    // nothing either way: what the recheck read was the disk before the undo. True only with no journal to settle.
    private bool SettleLeftover(UpdateSwap swap)
    {
        if (UpdateJournal.TryRead(_context.Folder) is not { Finished: false } left)
        {
            return true;
        }

        string running = _context.Running.ToString();
        if (left.Source == running)
        {
            swap.Undo(left);
        }
        else if (left.Target == running)
        {
            swap.Finish(left);
        }

        return false;
    }

    private void GoForward(UpdateSwap swap, Action<string>? say, CancellationToken cancellationToken)
    {
        var folder = _context.Folder;
        var planned = UpdateRecheck.Check(_context, cancellationToken, out string? reason);
        if (planned is null)
        {
            // Any mismatch drops the stage.
            UpdateStage.Drop(folder);
            Report(say, reason ?? UpdateText.StageChanged);
            return;
        }

        string target = planned.Staged.Version;
        say?.Invoke(UpdateText.Installing(target));
        var journal = swap.PlanUpdate(_context.Running.ToString(), target, planned.Installed.Exe, planned.Staged.Exe, planned.Moved, planned.Dropped);
        var result = swap.Run(journal);
        if (result.Done)
        {
            say?.Invoke(UpdateText.InstalledLine(target));
        }
        else
        {
            Report(say, result.Reason ?? UpdateText.StageChanged);
        }
    }

    private void GoBack(UpdateSwap swap, Action<string>? say, CancellationToken cancellationToken)
    {
        var folder = _context.Folder;
        var plan = UpdateRollback.Check(_context, cancellationToken, out string? reason);
        if (plan is null)
        {
            // One that fails at exit drops the pending rollback with its reason, so it can't fail at every exit, and previous stays.
            File.Delete(folder.Rollback);
            Report(say, reason ?? UpdateText.NothingToGoBackTo);
            return;
        }

        string target = plan.Target.Version;
        say?.Invoke(UpdateText.Installing(target));
        var journal = swap.PlanRollback(_context.Running.ToString(), target, plan.Installed.Exe, plan.Target.Exe, plan.Added);
        var result = swap.Run(journal);
        if (result.Done)
        {
            say?.Invoke(UpdateText.InstalledLine(target));
        }
        else
        {
            Report(say, result.Reason ?? UpdateText.NothingToGoBackTo);
        }
    }

    private void Report(Action<string>? say, string reason)
    {
        if (say is not null)
        {
            say(UpdateText.FailedLine(reason));
            return;
        }

        try
        {
            File.WriteAllText(_context.Folder.SwapFailed, UntrustedText.Scrub(reason));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Info(UpdateJournal.Category, ex.Message);
        }
    }
}
