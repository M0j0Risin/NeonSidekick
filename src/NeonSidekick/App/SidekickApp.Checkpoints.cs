using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.App;

// ── File checkpoints headless: /undo, /undo purge [all], /rewind … files (2026-10-28, the checkpoints plan) ──────────────

public sealed partial class SidekickApp
{
    /// <summary>A restore with nothing asked: conflicts skipped and named, the bin as <c>File deletes to Recycle bin</c> says.</summary>
    private RestoreReport HeadlessRestore(RestorePlan plan, WorkingDirectory files, WorkingDirectory images, CancellationToken cancellationToken)
    {
        var report = FileRestore.Run(plan, _checkpoints!, CheckpointTurns.DirectoryFor(files, images), restoreOver: false,
            EffectiveSettings.FileDeletesToRecycleBin ? _recycleBin : null, cancellationToken);
        DiagnosticLog.Info(CheckpointStore.Category, "Restore: " + FileRestore.Summary(report) + (report.Stopped ? " (stopped)" : ""));
        return report;
    }

    /// <summary>
    /// A restore's report as lines (<see cref="CheckpointTurns.ReportLines"/>): the files and the count as replies, what was skipped, not kept or
    /// kept for another try as notices, a failure as an error.
    /// </summary>
    private async Task HeadlessRestoreLinesAsync(RestoreReport report, string currentRoot)
    {
        var lines = CheckpointTurns.ReportLines(report, currentRoot);
        foreach (string line in lines.Files.Append(lines.Summary))
        {
            await HeadlessLineAsync(HeadlessReplyPrefix + line).ConfigureAwait(false);
        }

        foreach (string line in lines.Warnings)
        {
            if (line.StartsWith(UndoText.CouldNotPrefix, StringComparison.Ordinal))
            {
                await HeadlessLineAsync("[error] " + line).ConfigureAwait(false);
            }
            else
            {
                await HeadlessNoticeLineAsync("[notice] " + line).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Headless <c>/rewind [n] files</c>: the cut turns' files put back (conflicts skipped), or with no <paramref name="files"/> word nothing but
    /// their checkpoints given up (the conversation alone rewinds). The rewind's rule (<see cref="CheckpointTurns.FinishRewind"/>, 2026-10-28,
    /// review P4-04): a restore that didn't put everything back prints its report and <see cref="RewindText.NotAllBack"/>, and the rewind
    /// doesn't go ahead (false). The report and plan to print after the rewound line.
    /// </summary>
    private async Task<(bool Go, RestoreReport? Report, RestorePlan? Plan)> HeadlessRewindFilesAsync(RewindCut cut, bool files, WorkingDirectory filesDirectory, WorkingDirectory images, CancellationToken cancellationToken)
    {
        if (_checkpoints is not { } store || cut.Checkpoints is not { Count: > 0 })
        {
            return (true, null, null);
        }

        await store.FlushAsync().ConfigureAwait(false);
        RestorePlan? plan = null;
        RestoreReport? report = null;
        if (files && CheckpointTurns.RewindPlan(store, _settings.ProfileDirectory, cut, cancellationToken) is { } planned)
        {
            plan = planned;
            report = HeadlessRestore(planned, filesDirectory, images, cancellationToken);
        }

        if (!CheckpointTurns.FinishRewind(store, _settings.ProfileDirectory, cut, plan, report))
        {
            await HeadlessRestoreLinesAsync(report!, filesDirectory.Root).ConfigureAwait(false);
            await HeadlessNoticeLineAsync("[notice] " + RewindText.NotAllBack).ConfigureAwait(false);
            return (false, null, null);
        }

        return (true, report, plan);
    }

    /// <summary>
    /// Headless <c>/undo [n]</c>: the files put back, conflicts skipped; what came back spent and the model's note recorded in
    /// <paramref name="notes"/> (<see cref="CheckpointTurns.Finish"/>). True when a stamp went (save the history).
    /// </summary>
    private async Task<bool> HeadlessUndoAsync(IReadOnlyList<ChatMessage> messages, int count, WorkingDirectory files, WorkingDirectory images, UndoNotes notes, CancellationToken cancellationToken)
    {
        UndoPick pick;
        try
        {
            pick = await CheckpointTurns.PickAsync(_checkpoints, _settings.ProfileDirectory, messages, count, EffectiveSettings.FileCheckpoints, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (pick.Plan is not { } plan)
        {
            await HeadlessLineAsync(HeadlessReplyPrefix + pick.Notice).ConfigureAwait(false);
            return false;
        }

        if (pick.NewerNotKept > 0)
        {
            await HeadlessNoticeLineAsync("[notice] " + UndoText.NewerNotKept(pick.NewerNotKept)).ConfigureAwait(false);
        }

        var report = HeadlessRestore(plan, files, images, cancellationToken);
        await HeadlessRestoreLinesAsync(report, files.Root).ConfigureAwait(false);
        return CheckpointTurns.Finish(_checkpoints, _settings.ProfileDirectory, messages, report, pick, notes, files.Root);
    }

    /// <summary>Headless <c>/undo purge [all]</c>: run as typed, no yes; the working folder's store, or every one with <c>all</c>.</summary>
    private async Task HeadlessPurgeCheckpointsAsync(bool all, WorkingDirectory files, CancellationToken cancellationToken)
    {
        if (_checkpoints is not { } store)
        {
            await HeadlessLineAsync(HeadlessReplyPrefix + UndoText.NothingToPurge).ConfigureAwait(false);
            return;
        }

        await store.FlushAsync().ConfigureAwait(false);
        var roots = CheckpointTurns.PurgeRoots(all, files);
        if (store.Stores(_settings.ProfileDirectory, roots).Count == 0)
        {
            await HeadlessLineAsync(HeadlessReplyPrefix + UndoText.NothingToPurge).ConfigureAwait(false);
            return;
        }

        var result = store.Purge(_settings.ProfileDirectory, roots, cancellationToken);
        DiagnosticLog.Info(CheckpointStore.Category, UndoText.PurgedLogLine(result.Purged, result.Bytes, result.Pending.Count));
        var lines = CheckpointTurns.PurgeLines(result);
        for (int i = 0; i < lines.Count; i++)
        {
            await HeadlessLineAsync((i == 0 && result.Purged > 0 ? HeadlessReplyPrefix : "[error] ") + lines[i]).ConfigureAwait(false);
        }
    }
}
