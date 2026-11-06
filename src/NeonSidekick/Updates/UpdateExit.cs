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
    private bool _rollback;
    private Func<bool>? _terminalGone;

    public UpdateExit(UpdateContext context, UpdateLock? held, IUpdateFiles? files = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _held = held;
        _files = files;
    }

    /// <summary>
    /// What's left for a window close's recheck: the deadline, less what the close has used and the margin for the moves. Never negative: with nothing left it cancels at once, so nothing moves and the install waits for the next exit. <paramref name="killedAtTheDeadline"/> false is a Mac's signal close, which nothing kills (2026-11-04, review P14-6): ordinary close work could use the budget up and skip an install with no reason to wait, so it gets the deadline's full length, and a logout still can't hash without limit.
    /// </summary>
    public static TimeSpan CloseBudget(TimeSpan deadline, TimeSpan used, bool killedAtTheDeadline = true)
    {
        if (!killedAtTheDeadline)
        {
            return deadline;
        }

        var left = deadline - used - MoveMargin;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>
    /// The exit's work, once. <paramref name="say"/> prints the quit lines and is null for a window close, which can't show anything, so its failure goes in <c>swap-failed.txt</c> for the next interactive start. <paramref name="budget"/> limits the recheck: if it can't finish, nothing moves and the install waits for the next exit.
    /// On a Windows window close the kill at the deadline is what ends a slow swap, and the journal's undo deals with it. A Mac's signal path has no such kill: the budget only limits the recheck, and the moves run to their end. <paramref name="terminalGone"/> says a line just said went nowhere (2026-11-04, review P19-2): a closed Terminal's exit can get here first, its failure went to the dead console, and the next start said nothing.
    /// </summary>
    public void Run(Action<string>? say, TimeSpan? budget = null, Func<bool>? terminalGone = null)
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
        _terminalGone = terminalGone;
        try
        {
            using var limit = budget is { } time ? new CancellationTokenSource(time) : new CancellationTokenSource();
            Install(safe, limit.Token);
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Info(UpdateJournal.Category, UpdateText.RecheckOutOfTime);
            if (say is null)
            {
                Save(_rollback ? UpdateText.CloseRanOutBackLine : UpdateText.CloseRanOutLine);   // nothing shows at a close, so the next start says it (review P12-12)
            }
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
        _rollback = File.Exists(folder.Rollback);
        if (!folder.IsPackageOrMidSwap || (!_rollback && !File.Exists(folder.Ready)))
        {
            return;
        }

        // A renamed copy's exit doesn't swap, by the same name check as preflight: the stage waits for an exit of the listed exe. A manifest set aside by an interrupted swap is what the settling step below puts back. It says so (2026-11-04, review P4-4): the marker promised "at quit".
        // Neither leaves a file for the next start (review P20-1): that start may be the listed exe, for which "this copy has been renamed" is false.
        if (folder.IsPackage)
        {
            var load = UpdateManifest.Load(folder.Manifest);
            if (load.Manifest is not { } current)
            {
                Report(say, load.Unreadable ? UpdateText.InstallManifestHeld : UpdateText.DamagedInstall(load.Problem ?? UpdateText.ManifestUnreadable), leaveNoFile: true);
                return;
            }

            if (!_context.IsListedExe(current))
            {
                Report(say, UpdateText.RenamedCopy(current.Exe), leaveNoFile: true);
                return;
            }
        }

        // Closes this copy's own shared handle, then needs the lock exclusively, so if another copy runs from this folder, the stage waits for a later interactive exit. Only a held lock says that (2026-11-04, review P12-9): a root-owned lock file read as another copy, at every quit, for good.
        _held?.Dispose();
        _held = UpdateLock.TryExclusive(folder.InUseLock, out bool inUse);
        if (_held is null)
        {
            if (inUse)
            {
                string open = _rollback ? UpdateText.OtherCopyOpenBack : UpdateText.OtherCopyOpen;
                DiagnosticLog.Info(UpdateJournal.Category, open);
                say?.Invoke(open);
            }
            else
            {
                Report(say, UpdateText.LockCantOpen(folder.InUseLock));
            }

            return;
        }

        // Before settling or swapping (2026-11-04, review P12-3): a window close swapped a ready stage while this process's own Install was still fetching proofs, and that stage's Prepare then deleted files under the moves. Stage work holds this from before its proofs.
        using var staging = UpdateLock.TryExclusive(folder.StagingLock, out bool stageWork);
        if (staging is null)
        {
            Report(say, stageWork ? UpdateText.StageWorkRunning : UpdateText.LockCantOpen(folder.StagingLock));
            return;
        }

        var swap = new UpdateSwap(folder, _files) { BetweenSteps = _context.BetweenSteps };
        if (!SettleLeftover(swap, say))
        {
            return;
        }

        if (_rollback)
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

        switch (swap.Settle(left, _context.Running.ToString(), _context.IsThisExe))
        {
            case UpdateSettled.Finished:
                say?.Invoke(UpdateText.SettledFinished(left.Target, left.Rollback));
                break;
            case UpdateSettled.UndoStopped or UpdateSettled.LeftAlone:
                Report(say, UpdateText.MixedReason);
                break;
            default:
                Report(say, _rollback ? UpdateText.SettledInsteadBack : UpdateText.SettledInstead);
                break;
        }

        return false;
    }

    private void GoForward(UpdateSwap swap, Action<string>? say, CancellationToken cancellationToken)
    {
        var folder = _context.Folder;
        var stagedRead = UpdateManifest.Load(folder.StagingManifest);
        if (stagedRead.Manifest is { } named)
        {
            say?.Invoke(UpdateText.Installing(named.ParsedVersion.ToString()));   // before the recheck: seconds of silence on a slow disk read as a hang (review P4-18)
        }

        var planned = UpdateRecheck.Check(_context, cancellationToken, out string? reason, out var failure, stagedRead);
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
        if (UpdateManifest.ReadFile(folder.PreviousManifest, out _) is { } named)
        {
            say?.Invoke(UpdateText.GoingBackNow(named.ParsedVersion.ToString()));   // before the check, as the forward line is (2026-11-04, review P9-10)
        }

        var plan = UpdateRollback.Check(_context, cancellationToken, out string? reason, out var failure);
        if (plan is null)
        {
            string why = reason ?? UpdateText.NothingToGoBackTo;
            if (failure == UpdateRecheckFailure.Keep)
            {
                Report(say, UpdateText.TriesAgainAtQuit(why));
                return;
            }

            // One that fails at exit drops the pending rollback with its reason, so it can't fail at every exit, and previous stays.
            UpdateFolder.DeleteQuietly(folder.Rollback);
            Report(say, File.Exists(folder.Ready) ? UpdateText.ReadyStillInstalls(why) : why);
            return;
        }

        string target = plan.Target.ParsedVersion.ToString();
        var journal = swap.PlanRollback(_context.Running.ToString(), target, plan.Installed.Exe, plan.Target.Exe, plan.Incoming, plan.Added, plan.Verified);
        Finish(swap, journal, say, UpdateText.WentBackLine(target), UpdateText.NothingToGoBackTo);
    }

    private void Finish(UpdateSwap swap, UpdateJournal journal, Action<string>? say, string done, string fallback)
    {
        var result = swap.Run(journal);
        if (result.Done)
        {
            say?.Invoke(done);
            return;
        }

        // An undo that stopped too leaves a mix until the next start (review P5-6), which isn't the same news as a clean undo.
        string reason = result.Reason ?? fallback;
        Report(say, result.MayBeMixed ? UpdateText.UndoAlsoStopped(reason) : reason);
    }

    // In a rollback's words on every path (2026-11-04, reviews P9-9, P11-10): "the update didn't install" said nothing true of going back, and the broad catch had no way to know.
    private void Report(Action<string>? say, string reason, bool leaveNoFile = false)
    {
        if (say is not null)
        {
            say(_rollback ? UpdateText.WentBackFailedLine(reason) : UpdateText.FailedLine(reason));
            if (_terminalGone?.Invoke() != true)
            {
                return;
            }
        }

        if (!leaveNoFile)
        {
            Save(_rollback ? UpdateText.WindowCloseBackFailed(reason) : UpdateText.WindowCloseFailed(reason));
        }
    }

    private void Save(string sentence)
    {
        try
        {
            File.WriteAllText(_context.Folder.SwapFailed, UntrustedText.Scrub(sentence));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Info(UpdateJournal.Category, ex.Message);
        }
    }
}
