using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Speech;

namespace NeonSidekick.Updates;

/// <summary>What the start's step found.</summary>
/// <param name="Held">The in-use lock this run holds for its life, or null: not a package, a folder that can't take a lock, or a wait that ran out.</param>
/// <param name="StartAgain">True when this copy is the old exe over new files: <c>Program.cs</c> ends with exit code 2, nothing run.</param>
/// <param name="Line">What the screen says: "Updated to X", "Back on X", why a window close's swap failed, or that the install may be mixed.</param>
/// <param name="JournalMayGo">True when a finished journal may go once the screen has printed: this copy is what it installed, or its target isn't what the folder holds.</param>
/// <param name="FailureShown">True when <paramref name="Line"/> holds <c>swap-failed.txt</c>'s text, so printing it uses the file up.</param>
/// <param name="StartAgainLine">The words behind <paramref name="StartAgain"/>, for a run with no screen to write to stderr.</param>
public sealed record UpdateStartResult(UpdateLock? Held, bool StartAgain = false, string? Line = null, bool JournalMayGo = false, bool FailureShown = false, string? StartAgainLine = null);

/// <summary>
/// The next start's step (2026-11-04, self-update), in <c>Program.cs</c> after the <c>--log</c> sink opens, so it's logged, and before the home is resolved and any bundled native library loads, in every mode that gets that far. A failure in this step is logged and never stops the
/// app from starting. Helper modes and <c>--version</c> end before this, so they never take the lock.
/// </summary>
public static class UpdateStartup
{
    /// <summary>How long a copy started during a swap waits for the lock: longer than a normal swap. If it runs out (a slow disk, a stuck antivirus scan), the copy logs it and starts anyway, so a stuck swap can't keep the app from starting.</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    /// <summary>How long a copy tries for the exclusive lock to settle an interrupted swap: two copies started together each drop their shared hold in turn, so a short retry lets one through.</summary>
    public static readonly TimeSpan SettleLimit = TimeSpan.FromSeconds(3);

    /// <summary>How long an interactive run's "start it again" stays on the screen before the window can close: a double-clicked window would otherwise flash and vanish.</summary>
    public static readonly TimeSpan StartAgainPause = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan WaitStep = TimeSpan.FromMilliseconds(100);

    /// <summary>The step. <paramref name="say"/> prints a line for an interactive run and is null otherwise, so a headless or check run's stdout stays clean.</summary>
    public static UpdateStartResult Run(UpdateContext context, bool interactive, Action<string>? say, Action<TimeSpan>? pause = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var folder = context.Folder;
        if (!folder.IsPackageOrMidSwap)
        {
            return new UpdateStartResult(null);
        }

        // Its log lines stay off the console: they'd print as [Update] ... on the screen, or leak onto --headless's stdout.
        bool echo = DiagnosticLog.EchoToConsole;
        DiagnosticLog.EchoToConsole = false;
        var state = new StepState();
        try
        {
            return Step(context, interactive, say, pause ?? Thread.Sleep, state);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Broad on purpose, the app-starts promise (2026-11-04, reviews P1-7, P3-6). The step's own hold is kept: a second one here would leave the first open for the process's life, and this run's own exit would then read it as another copy.
            DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.StartStepFailed(ex.Message));
            return new UpdateStartResult(state.Held ?? UpdateLock.TryShared(folder.InUseLock), Line: interactive ? state.Line : null);
        }
        finally
        {
            DiagnosticLog.EchoToConsole = echo;
        }
    }

    /// <summary>
    /// What the screen calls once the start's line is printed, with or without one (2026-11-04, review P5-3): the step used to delete both files as it built the line, so <c>--profile typo</c> or a crash before the idle line lost "Updated to X" for good. A journal goes only if it reads
    /// as finished and the step said it may, so a renamed old copy's start leaves it for the listed exe's line. A failure goes only if this line showed it.
    /// </summary>
    public static void Acknowledge(UpdateFolder folder, bool journalMayGo, bool failureShown)
    {
        ArgumentNullException.ThrowIfNull(folder);
        try
        {
            if (journalMayGo && UpdateJournal.TryRead(folder) is { Finished: true })
            {
                UpdateJournal.Delete(folder);
            }

            if (failureShown)
            {
                File.Delete(folder.SwapFailed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.StartStepFailed(ex.Message));
        }
    }

    private sealed class StepState
    {
        public UpdateLock? Held { get; set; }

        public string? Line { get; set; }
    }

    private static UpdateStartResult Step(UpdateContext context, bool interactive, Action<string>? say, Action<TimeSpan> pause, StepState state)
    {
        var folder = context.Folder;
        state.Held = UpdateLock.TryShared(folder.InUseLock, out bool inUse);

        // Only a held lock is an update in progress: one that can't be opened (read-only media, an ACL) never waits, and goes on to settle what it can.
        if (state.Held is null && inUse)
        {
            say?.Invoke(UpdateText.WaitingLine);
            state.Held = WaitForShared(folder, pause);
            if (state.Held is null)
            {
                DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.WaitRanOut);
                return new UpdateStartResult(null, Line: interactive && UpdateJournal.IsUnsettled(folder) ? UpdateText.MixedLine : null);
            }
        }

        var journal = UpdateJournal.TryRead(folder, out bool unreadable);
        bool mixed = unreadable;
        if (journal is { Finished: false })
        {
            journal = FinishUnfinished(context, journal, state, pause, out mixed);
        }

        string running = context.Running.ToString();
        bool mayGo = false;
        string? line = null;
        if (journal is { Finished: true })
        {
            if (journal.Target == running)
            {
                mayGo = true;
                if (interactive)
                {
                    line = journal.Rollback ? UpdateText.BackOnLine(journal.Target) : UpdateText.UpdatedLine(journal.Target);
                    line = JoinLines(line, KeptLine(folder, journal));
                }
            }
            else if (InstalledVersion(folder) == journal.Target)
            {
                // The exe moves last, so a copy started just before it did is the old exe, and its files are now new: the mismatch that swapping at exit exists to avoid. A renamed copy isn't told to start again: that would repeat for as long as the journal
                // stays, and it leaves the journal for the listed exe's "Updated to" line. It no longer rests on having waited (2026-11-04, review P3-1): a copy reaching the lock just after the swap let go ran the old exe over new libraries.
                if (UpdateManifest.ReadFile(folder.Manifest, out _) is { } installed && context.IsListedExe(installed))
                {
                    string startAgain = UpdateText.StartAgainLine(journal.Target);
                    DiagnosticLog.Warn(UpdateJournal.Category, startAgain);
                    say?.Invoke(startAgain);
                    if (interactive)
                    {
                        pause(StartAgainPause);
                    }

                    return new UpdateStartResult(state.Held, StartAgain: true, StartAgainLine: startAgain);
                }
            }
            else
            {
                // Stale: a package unpacked by hand since. It must not lock the app out with exit 2, and nothing is left for it to say.
                mayGo = true;
            }
        }

        state.Line = line;
        bool unsettled = mixed || UpdateJournal.IsUnsettled(folder);
        if (interactive && mixed)
        {
            line = JoinLines(line, UpdateText.MixedLine);
            state.Line = line;
        }

        TidyStage(context, unsettled);
        string? failed = interactive ? ReadSwapFailed(folder) : null;
        return new UpdateStartResult(state.Held, Line: JoinLines(line, failed), JournalMayGo: mayGo, FailureShown: interactive && File.Exists(folder.SwapFailed));
    }

    // A journal this far is a package's own: the installed manifest's version, normalized like the journal's own target.
    private static string? InstalledVersion(UpdateFolder folder) => UpdateManifest.ReadFile(folder.Manifest, out _)?.ParsedVersion.ToString();

    private static string? KeptLine(UpdateFolder folder, UpdateJournal journal)
    {
        // Any step that moved a file under .update/kept. The journal is a file on disk, so what it names is scrubbed like swap-failed.txt's text.
        string prefix = folder.Relative(folder.Kept) + "/";
        foreach (var step in journal.Steps)
        {
            if (step is { Kind: UpdateStepKind.Move, To: { } to } && to.StartsWith(prefix, StringComparison.Ordinal))
            {
                int slash = to.IndexOf('/', prefix.Length);
                if (slash > 0)
                {
                    return UpdateText.KeptFilesLine(UntrustedText.Scrub(folder.Full(to[..slash])));
                }
            }
        }

        return null;
    }

    private static UpdateLock? WaitForShared(UpdateFolder folder, Action<TimeSpan> pause)
    {
        UpdateLock? held = null;
        for (var waiting = TimeSpan.Zero; held is null && waiting < WaitLimit; waiting += WaitStep)
        {
            pause(WaitStep);
            held = UpdateLock.TryShared(folder.InUseLock);
        }

        return held;
    }

    private static UpdateJournal? FinishUnfinished(UpdateContext context, UpdateJournal unfinished, StepState state, Action<TimeSpan> pause, out bool mixed)
    {
        // Exclusively before anything moves, then back to the usual shared hold, so two copies started together after a crash can't both undo it. Each lets go of its own hold first, so a single try could leave each to the other (2026-11-04, review P3-2): it retries, and stops once another copy has settled it.
        // The way back to shared retries too (review P5-1): a single try while the other copy holds it exclusively left this copy running its whole life with no lock.
        var folder = context.Folder;
        state.Held?.Dispose();
        state.Held = null;
        UpdateJournal? after = unfinished;
        mixed = false;
        UpdateLock? exclusive = UpdateLock.TryExclusive(folder.InUseLock, out bool held);
        for (var waiting = TimeSpan.Zero; exclusive is null && held && waiting < SettleLimit; waiting += WaitStep)
        {
            pause(WaitStep);
            after = UpdateJournal.TryRead(folder);
            if (after is not { Finished: false })
            {
                state.Held = WaitForSharedAtOnce(folder, pause);
                return after;
            }

            exclusive = UpdateLock.TryExclusive(folder.InUseLock, out held);
        }

        if (exclusive is null)
        {
            // Another copy holds it, or it can't be opened: this copy can't settle the swap, so it says the folder may be mixed.
            DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.CouldntSettle);
            mixed = true;
            state.Held = WaitForSharedAtOnce(folder, pause);
            return after;
        }

        using (exclusive)
        {
            after = UpdateJournal.TryRead(folder, out bool unreadable);
            mixed = unreadable;
            if (after is { Finished: false })
            {
                var swap = new UpdateSwap(folder) { BetweenSteps = context.BetweenSteps };
                var settled = swap.Settle(after, context.Running.ToString());
                mixed = settled == UpdateSettled.UndoStopped;
                after = settled == UpdateSettled.Finished ? after : UpdateJournal.TryRead(folder);
            }
        }

        state.Held = WaitForSharedAtOnce(folder, pause);
        return after;
    }

    private static UpdateLock? WaitForSharedAtOnce(UpdateFolder folder, Action<TimeSpan> pause) =>
        UpdateLock.TryShared(folder.InUseLock, out bool held) is { } shared ? shared : held ? WaitForShared(folder, pause) : null;

    // Everything here is only under the staging lock, so it never takes files another copy is staging. While a swap isn't settled nothing but a killed run's unpacking folders is touched (2026-11-04, review P3-3): the journal's undo moves files back into staging/ and staged/.
    private static void TidyStage(UpdateContext context, bool unsettled)
    {
        var folder = context.Folder;
        if (NothingToTidy(folder))
        {
            return;   // no lock (review P7-8): every start in every mode took it, and a run starting just as the user pressed Install read as another copy downloading
        }

        using var staging = UpdateLock.TryExclusive(folder.StagingLock);
        if (staging is null)
        {
            return;
        }

        UpdateStage.RemoveExtracting(folder);
        if (unsettled)
        {
            return;
        }

        if (!File.Exists(folder.Journal))
        {
            UpdateFolder.DeleteQuietly(folder.PreviousOld);
            UpdateFolder.DeleteQuietly(folder.Outgoing);
        }

        TidyParts(context);
        if (!Directory.Exists(folder.Staging))
        {
            // An orphan: ready with no stage would fail the next exit with "the update changed", though none was pending (review P5-8); staged/ what the last cleanup couldn't delete.
            UpdateStage.Drop(folder);
            return;
        }

        // A stage no newer than the running version is dropped, and so is one staged over a different installed manifest, since its list of changed files no longer holds and swapping it would leave a mix of versions. One with no manifest it can read is what a cleanup
        // couldn't delete (review P2-10): the swap took its manifest, so nothing else would ever retry it. A complete stage with no ready stays: the next /update checks and test-runs it again with no download.
        var staged = UpdateManifest.ReadFile(folder.StagingManifest, out _);
        if (staged is null)
        {
            UpdateStage.Drop(folder);
            return;
        }

        if (staged.ParsedVersion <= context.Running)
        {
            DiagnosticLog.Info(UpdateJournal.Category, UpdateText.StageDroppedOld(staged.Version));
            UpdateStage.Drop(folder);
            return;
        }

        if (File.Exists(folder.StagedInstallHashes) && !OverThisInstall(folder))
        {
            DiagnosticLog.Info(UpdateJournal.Category, UpdateText.InstallChanged);
            UpdateStage.Drop(folder);
        }
    }

    private static bool NothingToTidy(UpdateFolder folder) =>
        !Directory.Exists(folder.Root)
        || (!Directory.Exists(folder.Staging) && !Directory.Exists(folder.StagingParts) && !Directory.Exists(folder.Staged) && !Directory.Exists(folder.PreviousOld) && !Directory.Exists(folder.Outgoing)
            && !File.Exists(folder.Ready) && !Directory.EnumerateDirectories(folder.Root, ModelStore.ExtractingPattern).Any());

    // A partial for a version no newer than this one is never going to be finished (review P6-8: 0.5.8 half down, then 0.5.9 unpacked by hand, left hundreds of MB for good).
    private static void TidyParts(UpdateContext context)
    {
        var folder = context.Folder;
        if (!Directory.Exists(folder.StagingParts))
        {
            return;
        }

        foreach (string part in Directory.EnumerateFileSystemEntries(folder.StagingParts))
        {
            if (!UpdateCheck.TryParseArchiveVersion(Path.GetFileName(part), out var version) || version <= context.Running)
            {
                UpdateStage.DeleteEntryQuietly(part);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(folder.StagingParts).Any())
        {
            UpdateFolder.DeleteQuietly(folder.StagingParts);
        }
    }

    private static bool OverThisInstall(UpdateFolder folder)
    {
        var hashes = UpdateInstallHashes.TryRead(folder);
        return hashes is not null && File.Exists(folder.Manifest) && UpdateHash.Same(UpdateHash.OfFile(folder.Manifest), hashes.ManifestHash);
    }

    private static string? ReadSwapFailed(UpdateFolder folder)
    {
        if (!File.Exists(folder.SwapFailed))
        {
            return null;
        }

        string text = UntrustedText.Scrub(File.ReadAllText(folder.SwapFailed)).Trim();
        return text.Length == 0 ? null : UpdateText.WindowCloseFailed(text);
    }

    private static string? JoinLines(string? first, string? second) => first is null ? second : second is null ? first : first + "\n" + second;
}
