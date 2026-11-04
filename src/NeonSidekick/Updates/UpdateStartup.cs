using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Updates;

/// <summary>What the start's step found.</summary>
/// <param name="Held">The in-use lock this run holds for its life, or null: not a package, a folder that can't take a lock, or a wait that ran out.</param>
/// <param name="StartAgain">True when a copy that waited out a swap is now the old exe over new files: <c>Program.cs</c> ends with exit code 2, nothing run.</param>
/// <param name="Line">What the screen says: "Updated to X", "Back on X" or why a window close's swap failed.</param>
public sealed record UpdateStartResult(UpdateLock? Held, bool StartAgain = false, string? Line = null);

/// <summary>
/// The next start's step (2026-11-04, self-update), in <c>Program.cs</c> after the <c>--log</c> sink opens, so it's logged, and before the home is resolved and any bundled native library loads, in every mode that gets that far. A failure in this step is logged and never stops the
/// app from starting. Helper modes and <c>--version</c> end before this, so they never take the lock.
/// </summary>
public static class UpdateStartup
{
    /// <summary>How long a copy started during a swap waits for the lock: longer than a normal swap. If it runs out (a slow disk, a stuck antivirus scan), the copy logs it and starts anyway, so a stuck swap can't keep the app from starting.</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

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
        try
        {
            return Step(context, interactive, say, pause ?? Thread.Sleep);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.StartStepFailed(ex.Message));
            return new UpdateStartResult(UpdateLock.TryShared(folder.InUseLock));
        }
        finally
        {
            DiagnosticLog.EchoToConsole = echo;
        }
    }

    private static UpdateStartResult Step(UpdateContext context, bool interactive, Action<string>? say, Action<TimeSpan> pause)
    {
        var folder = context.Folder;
        var held = UpdateLock.TryShared(folder.InUseLock);
        bool waited = false;
        if (held is null && !IsReadOnly(folder))
        {
            waited = true;
            say?.Invoke(UpdateText.WaitingLine);
            for (var waiting = TimeSpan.Zero; held is null && waiting < WaitLimit; waiting += WaitStep)
            {
                pause(WaitStep);
                held = UpdateLock.TryShared(folder.InUseLock);
            }

            if (held is null)
            {
                DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.WaitRanOut);
                return new UpdateStartResult(null);
            }
        }

        var journal = UpdateJournal.TryRead(folder);
        if (journal is { Finished: false })
        {
            held = FinishUnfinished(context, journal, held, out journal);
        }

        // The exe moves last, so a copy started mid-swap is the old exe, and once the swap finishes its files are new: the mismatch that swapping at exit exists to avoid. A swap that was undone leaves old files, so it starts normally.
        if (waited && journal is { Finished: true } && journal.Target != context.Running.ToString())
        {
            say?.Invoke(UpdateText.StartAgainLine(journal.Target));
            return new UpdateStartResult(held, StartAgain: true);
        }

        string? line = null;
        if (journal is { Finished: true } && interactive)
        {
            if (journal.Target == context.Running.ToString())
            {
                line = journal.Rollback ? UpdateText.BackOnLine(journal.Target) : UpdateText.UpdatedLine(journal.Target);
            }

            UpdateJournal.Delete(folder);
        }

        TidyStage(context);
        if (interactive)
        {
            line = JoinLines(line, ReadSwapFailed(folder));
        }

        return new UpdateStartResult(held, Line: line);
    }

    private static UpdateLock? FinishUnfinished(UpdateContext context, UpdateJournal unfinished, UpdateLock? shared, out UpdateJournal? after)
    {
        // Exclusively before anything moves, then back to the usual shared hold, so two copies started together after a crash can't both undo it. If it can't get the lock, another copy is doing or holding it, and this copy leaves the journal for that one.
        var folder = context.Folder;
        shared?.Dispose();
        using (var exclusive = UpdateLock.TryExclusive(folder.InUseLock))
        {
            if (exclusive is null)
            {
                after = unfinished;
                return UpdateLock.TryShared(folder.InUseLock);
            }

            after = UpdateJournal.TryRead(folder);
            if (after is { Finished: false })
            {
                var swap = new UpdateSwap(folder) { BetweenSteps = context.BetweenSteps };
                string running = context.Running.ToString();
                if (after.Target == running)
                {
                    // Only cleanup was left.
                    swap.Finish(after);
                }
                else if (after.Source == running)
                {
                    // Undo it and keep the stage for the next exit. It never finishes forward, because the old exe would then run on with new DLLs.
                    swap.Undo(after);
                    after = UpdateJournal.TryRead(folder);
                }
                else
                {
                    // Neither version: a package unpacked by hand over an interrupted swap. Moving anything now would leave a mix.
                    DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.JournalRejected(UpdateText.JournalNotThisVersion(after.Source, after.Target, running)));
                    UpdateJournal.Delete(folder);
                    after = null;
                }
            }
        }

        return UpdateLock.TryShared(folder.InUseLock);
    }

    // A stage no newer than the running version is dropped, and so is one staged over a different installed manifest, since its list of changed files no longer holds and swapping it would leave a mix of versions. Only
    // under the staging lock, so it never takes files another copy is staging.
    private static void TidyStage(UpdateContext context)
    {
        var folder = context.Folder;
        using var staging = UpdateLock.TryExclusive(folder.StagingLock);
        if (staging is null)
        {
            return;
        }

        UpdateStage.RemoveExtracting(folder);
        if (!File.Exists(folder.Journal))
        {
            WorkingDirectory.DeleteDirectoryQuietly(folder.PreviousOld);
            WorkingDirectory.DeleteDirectoryQuietly(folder.Outgoing);
        }

        if (!Directory.Exists(folder.Staging))
        {
            return;
        }

        if (UpdateManifest.ReadFile(folder.StagingManifest, out _) is { } staged && staged.ParsedVersion <= context.Running)
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

    private static bool OverThisInstall(UpdateFolder folder)
    {
        try
        {
            var hashes = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(folder.StagedInstallHashes), UpdateJsonContext.Default.UpdateInstallHashes);
            return hashes is not null && UpdateHash.Same(UpdateHash.OfFile(folder.Manifest), hashes.ManifestHash);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string? ReadSwapFailed(UpdateFolder folder)
    {
        if (!File.Exists(folder.SwapFailed))
        {
            return null;
        }

        string text = UntrustedText.Scrub(File.ReadAllText(folder.SwapFailed)).Trim();
        File.Delete(folder.SwapFailed);
        return text.Length == 0 ? null : UpdateText.WindowCloseFailed(text);
    }

    private static string? JoinLines(string? first, string? second) => first is null ? second : second is null ? first : first + "\n" + second;

    // No lock and no folder that takes one: the app just can't update here, which preflight reports. An exclusive holder is the only reason worth waiting for.
    private static bool IsReadOnly(UpdateFolder folder)
    {
        try
        {
            Directory.CreateDirectory(folder.Root);
            string probe = Path.Combine(folder.Root, "startup.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
