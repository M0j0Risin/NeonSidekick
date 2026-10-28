using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.App;

// ── File checkpoints headless: /undo, /undo purge [all], /rewind … files (2026-10-28, the checkpoints plan) ──────────────

public sealed partial class SidekickApp
{
    /// <summary>A root's pinned working directory for a restore, as the screen picks it: the images folder's own guards for its root.</summary>
    private static Func<string, WorkingDirectory> RestoreDirectories(WorkingDirectory files, WorkingDirectory images) =>
        root => WorkingDirectory.PathComparer.Equals(root, images.Root) ? images.PinnedAt(root) : files.PinnedAt(root);

    /// <summary>A restore with nothing asked: conflicts skipped and named, the bin as <c>File deletes to Recycle bin</c> says.</summary>
    private RestoreReport HeadlessRestore(RestorePlan plan, WorkingDirectory files, WorkingDirectory images, CancellationToken cancellationToken)
    {
        var report = FileRestore.Run(plan, _checkpoints!, RestoreDirectories(files, images), restoreOver: false,
            EffectiveSettings.FileDeletesToRecycleBin ? _recycleBin : null, cancellationToken);
        DiagnosticLog.Info(CheckpointStore.Category, "Restore: " + FileRestore.Summary(report) + (report.Stopped ? " (stopped)" : ""));
        return report;
    }

    /// <summary>A restore's report as lines: what came back with its +/− counts, then the counts, then what was skipped, not kept or failed.</summary>
    private async Task HeadlessRestoreLinesAsync(RestoreReport report, string currentRoot)
    {
        // The files and the count as replies; what was skipped or not kept as notices, a failure as an error.
        var lines = CheckpointTurns.HeadlessLines(report, currentRoot);
        int warnings = UndoText.Report(report, currentRoot).Count - 1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (i < lines.Count - warnings)
            {
                await HeadlessLineAsync(HeadlessReplyPrefix + lines[i]).ConfigureAwait(false);
            }
            else if (lines[i].StartsWith(UndoText.CouldNotPrefix, StringComparison.Ordinal))
            {
                await HeadlessLineAsync("[error] " + lines[i]).ConfigureAwait(false);
            }
            else
            {
                await HeadlessNoticeLineAsync("[notice] " + lines[i]).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Headless <c>/rewind [n] files</c>: the cut turns' files put back (conflicts skipped), or with no <paramref name="files"/> word nothing but
    /// their checkpoints dropped (the conversation alone rewinds). The report, or null.
    /// </summary>
    private async Task<RestoreReport?> HeadlessRewindFilesAsync(RewindCut cut, bool files, WorkingDirectory filesDirectory, WorkingDirectory images, CancellationToken cancellationToken)
    {
        if (_checkpoints is not { } store || cut.Checkpoints is not { Count: > 0 } ids)
        {
            return null;
        }

        await store.FlushAsync().ConfigureAwait(false);
        RestoreReport? report = null;
        if (files)
        {
            var plan = FileRestore.Plan(store, _settings.ProfileDirectory, ids);
            if (!plan.IsEmpty)
            {
                report = HeadlessRestore(plan, filesDirectory, images, cancellationToken);
            }
        }

        store.Remove(_settings.ProfileDirectory, ids);
        return report;
    }

    /// <summary>Headless <c>/undo [n]</c>: the files put back, conflicts skipped; the model's note for the next line, or null.</summary>
    private async Task<string?> HeadlessUndoAsync(IReadOnlyList<ChatMessage> messages, int count, WorkingDirectory files, WorkingDirectory images, CancellationToken cancellationToken)
    {
        if (_checkpoints is { } pending)
        {
            await pending.FlushAsync().ConfigureAwait(false);
        }

        var pick = CheckpointTurns.Pick(_checkpoints, _settings.ProfileDirectory, messages, count, EffectiveSettings.FileCheckpoints);
        if (pick.Plan is not { } plan)
        {
            await HeadlessLineAsync(HeadlessReplyPrefix + pick.Notice).ConfigureAwait(false);
            return null;
        }

        var report = HeadlessRestore(plan, files, images, cancellationToken);
        await HeadlessRestoreLinesAsync(report, files.Root).ConfigureAwait(false);
        CheckpointTurns.Spend(_checkpoints, _settings.ProfileDirectory, messages, report.Spent);
        return CheckpointTurns.Note(report, pick.Undone, files.Root);
    }

    /// <summary>Headless <c>/undo purge [all]</c>: run as typed, no yes.</summary>
    private async Task HeadlessPurgeCheckpointsAsync(bool all, WorkingDirectory files, WorkingDirectory images, CancellationToken cancellationToken)
    {
        if (_checkpoints is not { } store)
        {
            await HeadlessLineAsync(HeadlessReplyPrefix + UndoText.NothingToPurge).ConfigureAwait(false);
            return;
        }

        await store.FlushAsync().ConfigureAwait(false);
        IReadOnlyCollection<string>? roots = all ? null : [files.Root, images.Root];
        if (!store.Stores(_settings.ProfileDirectory).Any(s => roots is null || roots.Contains(s.Root, WorkingDirectory.PathComparer)))
        {
            await HeadlessLineAsync(HeadlessReplyPrefix + UndoText.NothingToPurge).ConfigureAwait(false);
            return;
        }

        var result = store.Purge(_settings.ProfileDirectory, roots, cancellationToken);
        DiagnosticLog.Info(CheckpointStore.Category, UndoText.PurgedLogLine(result.Purged, result.Bytes));
        await HeadlessLineAsync(HeadlessReplyPrefix + UndoText.Purged(result.Purged, result.Bytes)).ConfigureAwait(false);
        if (result.Busy.Count + result.Failed.Count > 0)
        {
            await HeadlessLineAsync("[error] " + UndoText.PurgeLeft([.. result.Busy, .. result.Failed])).ConfigureAwait(false);
        }
    }
}
