using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Updates;

/// <summary>
/// The swap at exit, around <see cref="UpdateSwap"/> (2026-11-04, self-update): only when the interactive app exits, on both of <c>Program.cs</c>'s ways out. A failed swap never changes the exit code. It runs once: a window closed or a signal arriving during an <c>/exit</c>'s swap reaches here too, on another
/// thread, so a one-shot gate lets the first caller run and the second wait for it. After a caught crash it still runs: the recheck vouches for the stage, and the new version may hold the fix for a crash that would otherwise keep recurring.
/// </summary>
public sealed class UpdateExit
{
    /// <summary>
    /// A window close's time for the moves, taken off what's left of the deadline before the recheck starts (2026-11-04, reviews P1-3, P6-3): the budget was a flat 4 s from after the flushes, while the kill comes 4 s after the close began, so a slow recheck started the swap with no time left, and the kill landed mid-move.
    /// </summary>
    public static readonly TimeSpan MoveMargin = TimeSpan.FromSeconds(1);

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

    /// <summary>What's left for a window close's recheck: the deadline, less what the close has used and the margin for the moves. Never negative: with nothing left it cancels at once, so nothing moves and the install waits for the next exit.</summary>
    public static TimeSpan CloseBudget(TimeSpan deadline, TimeSpan used)
    {
        var left = deadline - used - MoveMargin;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>
    /// The exit's work, once. <paramref name="say"/> prints the quit lines and is null for a window close, which can't show anything, so its failure goes in <c>swap-failed.txt</c> for the next interactive start. <paramref name="budget"/> limits the recheck: if it can't finish, nothing moves and the install waits for the next exit.
    /// On a Windows window close the kill at the deadline is what ends a slow swap, and the journal's undo deals with it. A Mac's signal path has no such kill: the budget only limits the recheck, and the moves run to their end.
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

        // A console gone at quit (a Mac terminal closed, EIO) must not escape past "a failed swap never changes the exit" (2026-11-04, review P2-8).
        Action<string>? safe = say is null ? null : line => SayQuietly(say, line);
        try
        {
            using var limit = budget is { } time ? new CancellationTokenSource(time) : new CancellationTokenSource();
            Install(safe, limit.Token);
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Info(UpdateJournal.Category, UpdateText.RecheckOutOfTime);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Broad on purpose, not per symptom (review P3-6): whatever it is, the exit goes on to its own end.
            DiagnosticLog.Warn(UpdateJournal.Category, ex.Message);
            Report(safe, ex.Message);
        }
        finally
        {
            _held?.Dispose();
            _held = null;
            DiagnosticLog.EchoToConsole = echo;
            _done.Set();
        }
    }

    private static void SayQuietly(Action<string> say, string line)
    {
        try
        {
            say(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException)
        {
            DiagnosticLog.Info(UpdateJournal.Category, ex.Message);
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

        // A renamed copy's exit doesn't swap, by the same name check as preflight: the stage waits for an exit of the listed exe. A manifest set aside by an interrupted swap is what the settling step below puts back. It says so (2026-11-04, review P4-4): the marker promised "at quit".
        if (folder.IsPackage)
        {
            if (UpdateManifest.ReadFile(folder.Manifest, out string? problem) is not { } current)
            {
                Report(say, UpdateText.DamagedInstall(problem ?? UpdateText.ManifestUnreadable));
                return;
            }

            if (!_context.IsListedExe(current))
            {
                Report(say, UpdateText.RenamedCopy(current.Exe));
                return;
            }
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
        if (!SettleLeftover(swap, say))
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
    // nothing either way: what the recheck read was the disk before the undo. True only with no journal to settle. It says why this quit installs nothing (2026-11-04, review P3-12).
    private bool SettleLeftover(UpdateSwap swap, Action<string>? say)
    {
        var left = UpdateJournal.TryRead(_context.Folder, out bool unreadable);
        if (unreadable)
        {
            Report(say, UpdateText.UnfinishedSwap);
            return false;
        }

        if (left is not { Finished: false })
        {
            return true;
        }

        var settled = swap.Settle(left, _context.Running.ToString());
        Report(say, settled == UpdateSettled.UndoStopped ? UpdateText.MixedReason : UpdateText.SettledInstead);
        return false;
    }

    private void GoForward(UpdateSwap swap, Action<string>? say, CancellationToken cancellationToken)
    {
        var folder = _context.Folder;
        if (UpdateManifest.ReadFile(folder.StagingManifest, out _) is { } named)
        {
            say?.Invoke(UpdateText.Installing(named.ParsedVersion.ToString()));   // before the recheck: seconds of silence on a slow disk read as a hang (review P4-18)
        }

        var planned = UpdateRecheck.Check(_context, cancellationToken, out string? reason, out var failure);
        if (planned is null)
        {
            // Any mismatch drops the stage. A failure to read keeps it for the next exit.
            if (failure == UpdateRecheckFailure.Drop)
            {
                UpdateStage.Drop(folder);
            }

            Report(say, reason ?? UpdateText.StageChanged);
            return;
        }

        string target = planned.Staged.ParsedVersion.ToString();
        var journal = swap.PlanUpdate(_context.Running.ToString(), target, planned.Installed.Exe, planned.Staged.Exe, planned.Moved, planned.Dropped, planned.Verified, planned.ProofsVouch);
        Finish(swap, journal, say, UpdateText.InstalledLine(target), UpdateText.StageChanged);
    }

    private void GoBack(UpdateSwap swap, Action<string>? say, CancellationToken cancellationToken)
    {
        var folder = _context.Folder;
        var plan = UpdateRollback.Check(_context, cancellationToken, out string? reason);
        if (plan is null)
        {
            // One that fails at exit drops the pending rollback with its reason, so it can't fail at every exit, and previous stays.
            File.Delete(folder.Rollback);
            Report(say, reason ?? UpdateText.NothingToGoBackTo, rollback: true);
            return;
        }

        string target = plan.Target.ParsedVersion.ToString();
        say?.Invoke(UpdateText.GoingBackNow(target));
        var journal = swap.PlanRollback(_context.Running.ToString(), target, plan.Installed.Exe, plan.Target.Exe, plan.Incoming, plan.Added, plan.Verified);
        Finish(swap, journal, say, UpdateText.WentBackLine(target), UpdateText.NothingToGoBackTo, rollback: true);
    }

    private void Finish(UpdateSwap swap, UpdateJournal journal, Action<string>? say, string done, string fallback, bool rollback = false)
    {
        var result = swap.Run(journal);
        if (result.Done)
        {
            say?.Invoke(done);
            return;
        }

        // An undo that stopped too leaves a mix until the next start (review P5-6), which isn't the same news as a clean undo.
        string reason = result.Reason ?? fallback;
        Report(say, result.Undone ? reason : UpdateText.UndoAlsoStopped(reason), rollback);
    }

    private void Report(Action<string>? say, string reason, bool rollback = false)
    {
        if (say is not null)
        {
            say(rollback ? UpdateText.WentBackFailedLine(reason) : UpdateText.FailedLine(reason));
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
