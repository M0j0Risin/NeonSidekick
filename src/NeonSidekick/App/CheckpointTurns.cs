using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>A turn's journal once sealed: the after-state its commit records.</summary>
internal sealed record SealedJournal(FileJournal Journal, IReadOnlyDictionary<string, List<CheckpointState>> After);

/// <summary>
/// What <c>/undo</c> found: the plan and the turns it undoes, or the notice to print instead (nothing to undo, no longer kept, unreadable);
/// <paramref name="NewerNotKept"/> the newer stamped turns it reached past, their file checkpoints gone (2026-10-28, review P1-10).
/// </summary>
internal sealed record UndoPick(RestorePlan? Plan, IReadOnlyList<(ChatMessage Message, string Id)> Undone, string? Notice, int NewerNotKept = 0);

/// <summary>A restore's report as lines (2026-10-28, review P7-08): one per file put back, the count, then what wasn't put back.</summary>
internal sealed record RestoreLines(IReadOnlyList<string> Files, string Summary, IReadOnlyList<string> Warnings);

/// <summary>
/// The steps of a checkpointed turn and of <c>/undo</c> and <c>/rewind … files</c> that the screen and headless share (2026-10-28, the
/// checkpoints plan; review P4-15: one place, so the two front ends can't drift): the journal made at the turn's start, sealed and stamped
/// before the turn is logged, committed after it; the stamped turns of a history, the turns an <c>/undo</c> takes, the rule for spending
/// what it put back, the rewind's all-or-nothing rule, the purge's folders and the sweep. Asking, spinners and printing stay in each front end.
/// </summary>
internal static class CheckpointTurns
{
    /// <summary>A short random checkpoint id: the turn's name in the stores' refs and the history's stamp.</summary>
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>
    /// The turn's journal over <paramref name="directories"/>' roots, or null with no store or <c>File checkpoints</c> off; each root's writer
    /// opens at its first entry. The directories are watched (2026-10-28, review P11-02): one whose folder moves mid-turn marks the turn incomplete.
    /// </summary>
    public static FileJournal? Begin(ICheckpointStore? store, AppSettingsData effective, string profileDirectory, params WorkingDirectory[] directories)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(directories);
        if (store is null || !effective.FileCheckpoints)
        {
            return null;
        }

        string id = NewId();
        var journal = new FileJournal(id, directories.Select(d => d.Root).Distinct(WorkingDirectory.PathComparer), root => store.Begin(profileDirectory, root, id), CheckpointLimits.From(effective).LargestFileBytes);
        journal.Watch(directories);
        return journal;
    }

    /// <summary>
    /// Seals the journal (after-states taken on the turn's own flow; <paramref name="stop"/>, the app closing, drops it) and stamps the
    /// history's last turn start with its id. Null, its writers dropped, when the turn was withdrawn, changed no file or all its stores failed.
    /// </summary>
    public static SealedJournal? Seal(ICheckpointStore? store, ConversationHistory history, FileJournal? journal, bool withdrawn, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (journal is null)
        {
            return null;
        }

        var after = journal.Seal(stop);
        bool keeps = !withdrawn && after is not null && journal.Logs.Any(l => l.Entries.Count > 0 && l.Writer is not null);
        if (!keeps || history.LastTurnStart() is not { } start)
        {
            Abandon(store, journal);
            return null;
        }

        ConversationHistory.SetTurnCheckpoint(start, journal.Id);
        return new SealedJournal(journal, after!);
    }

    /// <summary>Drops every store writer the journal opened: the turn keeps no checkpoint.</summary>
    public static void Abandon(ICheckpointStore? store, FileJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        foreach (var log in journal.Logs)
        {
            if (log.Writer is { } writer)
            {
                store?.Abandon(writer);
            }
        }
    }

    /// <summary>One record per root that changed, committed (in the background in the app) under <paramref name="session"/>.</summary>
    public static void Commit(ICheckpointStore store, ConversationHistory history, SealedJournal turn, string text, string session, AppSettingsData effective, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(turn);
        int ordinal = history.LastTurnStart() is { } start ? ConversationHistory.TurnOrdinal(start) ?? 0 : 0;
        var limits = CheckpointLimits.From(effective);
        var journal = turn.Journal;
        foreach (var log in journal.Logs)
        {
            if (log.Writer is not { } writer)
            {
                continue;
            }

            if (log.Entries.Count == 0)
            {
                store.Abandon(writer);
                continue;
            }

            store.Commit(writer, new CheckpointRecord
            {
                Id = journal.Id,
                Root = log.Root,
                Session = session,
                Turn = ordinal,
                Message = UndoText.Excerpt(PasteBlocks.WithoutSavedPaths(text)),
                Time = now,
                Tools = journal.Tools.ToList(),
                Entries = log.Entries,
                After = turn.After.GetValueOrDefault(log.Root) ?? new List<CheckpointState>(),
                SavedBytes = log.SavedBytes,
                Incomplete = journal.Incomplete,
            }, limits);
        }
    }

    /// <summary>
    /// The history's stamped turns, newest first: each user message and its checkpoint id; then a compact's summary once per checkpoint of the
    /// turns it stands for (2026-10-28, review P2-06: a compaction dropped their reach), newest first.
    /// </summary>
    public static List<(ChatMessage Message, string Id)> Stamped(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var stamped = new List<(ChatMessage, string)>();
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (ConversationHistory.IsTurnStart(messages[i]) && ConversationHistory.TurnCheckpoint(messages[i]) is { } id)
            {
                stamped.Add((messages[i], id));
            }

            foreach (string compacted in ConversationHistory.CheckpointList(messages[i]))
            {
                stamped.Add((messages[i], compacted));
            }
        }

        return stamped;
    }

    /// <summary>A root's pinned working directory for a restore: the images folder's own guards for its root, the working folder's for any other.</summary>
    public static Func<string, WorkingDirectory> DirectoryFor(WorkingDirectory files, WorkingDirectory images)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(images);
        return root => WorkingDirectory.PathComparer.Equals(root, images.Root) ? images.PinnedAt(root) : files.PinnedAt(root);
    }

    /// <summary>
    /// <see cref="Pick"/> off the caller's flow (2026-10-28, review P1-12: the hashing held the screen): a turn still committing first, then
    /// the pick on the thread pool. <paramref name="cancellationToken"/> stops it between files (thrown).
    /// </summary>
    public static async Task<UndoPick> PickAsync(ICheckpointStore? store, string profileDirectory, IReadOnlyList<ChatMessage> messages, int count, bool checkpointsOn, CancellationToken cancellationToken)
    {
        if (store is not null)
        {
            await store.FlushAsync().ConfigureAwait(false);
        }

        var snapshot = messages.ToList();
        return await Task.Run(() => Pick(store, profileDirectory, snapshot, count, checkpointsOn, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The turns an <c>/undo</c> of <paramref name="count"/> takes: the newest stamped turns whose checkpoints are still kept, and their
    /// restore worked out (one <c>Find</c> for both, review P1-12), with how many newer ones it reached past (review P1-10). A notice
    /// instead when none is stamped, none is kept any more, or the newest can't be read.
    /// </summary>
    public static UndoPick Pick(ICheckpointStore? store, string profileDirectory, IReadOnlyList<ChatMessage> messages, int count, bool checkpointsOn, CancellationToken cancellationToken = default)
    {
        var stamped = Stamped(messages);
        if (stamped.Count == 0 || store is null)
        {
            return new UndoPick(null, [], UndoText.NothingNotice(!checkpointsOn));
        }

        var found = store.Find(profileDirectory, stamped.Select(s => s.Id).Distinct(StringComparer.Ordinal).ToList());
        var kept = found.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        int newer = stamped.TakeWhile(s => !kept.Contains(s.Id)).Count();
        var take = stamped.Where(s => kept.Contains(s.Id)).Take(count).ToList();
        if (take.Count == 0)
        {
            return new UndoPick(null, [], UndoText.NoLongerKept);
        }

        var plan = FileRestore.Plan(store, profileDirectory, take.Select(t => t.Id).ToList(), found, cancellationToken);
        if (plan.IsEmpty)
        {
            return new UndoPick(null, [], plan.StoppedAt is { } bad ? UndoText.Unreadable(bad.Problem) : UndoText.NoLongerKept);
        }

        var ids = plan.Ids.ToHashSet(StringComparer.Ordinal);
        return new UndoPick(plan, take.Where(t => ids.Contains(t.Id)).ToList(), null, newer);
    }

    /// <summary>The messages an <c>/undo</c>'s question and note quote: each turn's line, or for a compact's summary the record's saved excerpt.</summary>
    public static IReadOnlyList<string> UndoneLines(UndoPick pick)
    {
        ArgumentNullException.ThrowIfNull(pick);
        var lines = new List<string>();
        foreach (var (message, id) in pick.Undone)
        {
            string line = ConversationRewind.IsSummary(message)
                ? pick.Plan?.Checkpoints.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal))?.Record?.Message ?? ""
                : ConversationRewind.UserLine(message);
            if (!lines.Contains(line, StringComparer.Ordinal))
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>
    /// The files a rewind cutting <paramref name="cut"/> would put back: null with no store, no checkpoint among the cut turns, or none still
    /// kept. <paramref name="cancellationToken"/> stops the hashing (thrown).
    /// </summary>
    public static RestorePlan? RewindPlan(ICheckpointStore? store, string profileDirectory, RewindCut cut, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cut);
        if (store is null || cut.Checkpoints is not { Count: > 0 } ids)
        {
            return null;
        }

        var plan = FileRestore.Plan(store, profileDirectory, ids, null, cancellationToken);
        return plan.IsEmpty ? null : plan;
    }

    /// <summary>
    /// The rewind's rule after its files (2026-10-28, review P5-01 and P4-04: a stopped or partial restore still cut the conversation and dropped
    /// every checkpoint): with no restore (conversation only, or nothing to put back) the cut turns' checkpoints are given up and it goes ahead;
    /// with one, only when it wasn't stopped and spent every checkpoint it replayed — then all of the cut's go. Otherwise nothing is dropped and
    /// the conversation stays (false).
    /// </summary>
    public static bool FinishRewind(ICheckpointStore? store, string profileDirectory, RewindCut cut, RestorePlan? plan, RestoreReport? report)
    {
        ArgumentNullException.ThrowIfNull(cut);
        if (report is not null && plan is not null && (report.Stopped || !plan.Ids.All(report.Spent.Contains)))
        {
            return false;
        }

        if (store is not null && cut.Checkpoints is { Count: > 0 } ids)
        {
            store.Remove(profileDirectory, ids);
        }

        return true;
    }

    /// <summary>
    /// Whether a restore put everything back (2026-10-28, review P5-07): every path listed came back or was back already, no turn lost
    /// changes, none stopped the replay or was no longer kept. Only then does the rewind's warning leave the file tools out.
    /// </summary>
    public static bool AllBack(RestorePlan plan, RestoreReport report)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(report);
        return !report.Stopped && !plan.Incomplete && plan.StoppedAt is null && plan.NoLongerKept.Count == 0
            && report.Files.All(f => f.Outcome is RestoreOutcome.Restored or RestoreOutcome.AlreadyBack);
    }

    /// <summary>
    /// An <c>/undo</c>'s end, the one rule for spending (2026-10-28, review P5-01): only what the report spent is removed and unstamped, and the
    /// model's note recorded in <paramref name="notes"/>. True when a stamp went (save the history).
    /// </summary>
    public static bool Finish(ICheckpointStore? store, string profileDirectory, IReadOnlyList<ChatMessage> messages, RestoreReport report, UndoPick pick, UndoNotes notes, string currentRoot)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(pick);
        ArgumentNullException.ThrowIfNull(notes);
        notes.Record(report, pick, currentRoot);
        return Spend(store, profileDirectory, messages, report.Spent);
    }

    /// <summary>The spent checkpoints' refs removed and their stamps taken off <paramref name="messages"/>; true when any stamp went (save the history).</summary>
    public static bool Spend(ICheckpointStore? store, string profileDirectory, IReadOnlyList<ChatMessage> messages, IReadOnlyList<string> spent)
    {
        ArgumentNullException.ThrowIfNull(spent);
        if (spent.Count == 0 || store is null)
        {
            return false;
        }

        store.Remove(profileDirectory, spent);
        var gone = spent.ToHashSet(StringComparer.Ordinal);
        bool changed = false;
        foreach (var (message, id) in Stamped(messages))
        {
            if (!gone.Contains(id))
            {
                continue;
            }

            if (ConversationHistory.TurnCheckpoint(message) == id)
            {
                ConversationHistory.SetTurnCheckpoint(message, null);
            }
            else
            {
                ConversationHistory.SetCheckpointList(message, ConversationHistory.CheckpointList(message).Where(c => !gone.Contains(c)).ToList());
            }

            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// The folders <c>/undo purge</c> clears (2026-10-28, review P2-09 and P9-13): the working folder's store only, every store for
    /// <c>purge all</c> (null). The store drops the purged turns' picture halves with it.
    /// </summary>
    public static IReadOnlyCollection<string>? PurgeRoots(bool all, WorkingDirectory files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return all ? null : [files.Root];
    }

    /// <summary>A purge's lines: what it freed, then what it left (in use, failed) and what is still being deleted. The first line is a notice.</summary>
    public static IReadOnlyList<string> PurgeLines(CheckpointPurgeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = new List<string>();
        if (result.Purged > 0 || result.Busy.Count + result.Failed.Count + result.Pending.Count == 0)
        {
            lines.Add(UndoText.Purged(result.Purged, result.Bytes));
        }

        if (result.Busy.Count + result.Failed.Count > 0)
        {
            lines.Add(UndoText.PurgeLeft([.. result.Busy, .. result.Failed]));
        }

        if (result.Pending.Count > 0)
        {
            lines.Add(UndoText.PurgeStopped(result.Pending));
        }

        return lines;
    }

    /// <summary>
    /// A restore's report as lines (2026-10-28, review P7-08: headless counted the report's lines to tell them apart): a line per file put back
    /// with its +/− counts (no diff rows), the summary, then what was skipped, not kept, failed or kept for another try.
    /// </summary>
    public static RestoreLines ReportLines(RestoreReport report, string currentRoot)
    {
        ArgumentNullException.ThrowIfNull(report);
        var files = new List<string>();
        foreach (var file in report.Files.Where(f => f.Outcome == RestoreOutcome.Restored))
        {
            string shown = UndoText.Shown(file.Where, currentRoot);
            files.Add(file.Diff is { } diff
                ? UndoText.DiffNote(shown) + " (+" + diff.Added.ToString(CultureInfo.InvariantCulture) + " −" + diff.Removed.ToString(CultureInfo.InvariantCulture) + ")"
                : UndoText.DiffNote(shown));
        }

        var lines = UndoText.Report(report, currentRoot);
        return new RestoreLines(files, lines[0], lines.Skip(1).ToList());
    }

    /// <summary>
    /// The start-up and profile-switch sweep (2026-10-28, review P3-10: headless never swept): the age rule, gone folders, finished purges, on
    /// the thread pool; never faults.
    /// </summary>
    public static Task SweepAsync(ICheckpointStore? store, string profileDirectory, CheckpointLimits limits)
    {
        if (store is null)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            try
            {
                store.Sweep(profileDirectory, limits);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LibGit2Sharp.LibGit2SharpException)
            {
                DiagnosticLog.Warn(CheckpointStore.Category, "The file checkpoints' sweep failed: " + ex.Message);
            }
        });
    }
}

/// <summary>
/// The <c>/undo</c> notes the next turn of a conversation carries (2026-10-28, review U1: the screen and headless each kept a string and merged
/// by trimming its bracket), one entry per <c>/undo</c>: the turns it undid, what came back, what was kept, what couldn't be put back, and
/// whether it was partial. An <c>/undo</c> of a turn already noted replaces that entry (review P3-11: a stopped then repeated one read twice);
/// a rewind keeps only the entries whose turns are still in the history (review P6-04).
/// </summary>
internal sealed class UndoNotes
{
    private sealed record Entry(IReadOnlyList<ChatMessage> Turns, string Clause);

    private readonly List<Entry> _entries = [];

    /// <summary>Whether no note is pending.</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>
    /// An <c>/undo</c>'s note: nothing when no file came back (the model's files are as it left them), else its clause, replacing an entry
    /// that named any of the same turns.
    /// </summary>
    public void Record(RestoreReport report, UndoPick pick, string currentRoot)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(pick);
        var back = report.Files.Where(f => f.Outcome is RestoreOutcome.Restored or RestoreOutcome.AlreadyBack).Select(f => UndoText.Shown(f.Where, currentRoot)).ToList();
        if (back.Count == 0)
        {
            return;
        }

        var kept = report.Files.Where(f => f.Outcome == RestoreOutcome.Skipped).Select(f => UndoText.Shown(f.Where, currentRoot)).ToList();
        var notBack = report.Files.Where(f => f.Outcome is RestoreOutcome.NotSaved or RestoreOutcome.Failed).Select(f => UndoText.Shown(f.Where, currentRoot)).ToList();
        bool partly = report.Stopped || report.Kept || notBack.Count > 0 || pick.Plan is { Incomplete: true };
        var turns = pick.Undone.Select(t => t.Message).Distinct().ToList();
        _entries.RemoveAll(e => e.Turns.Any(turns.Contains));
        _entries.Add(new Entry(turns, UndoText.NoteClause(CheckpointTurns.UndoneLines(pick), back, kept, notBack, partly)));
    }

    /// <summary>The note for the next turn (<see cref="UndoText.ModelNote"/>), or null with none pending.</summary>
    public string? Render() => _entries.Count == 0 ? null : UndoText.ModelNote(_entries.Select(e => e.Clause).ToList());

    /// <summary>Forgets every note: the turn carried them, or the conversation they described is gone.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>After a rewind: only the notes whose turns <paramref name="messages"/> still holds stay.</summary>
    public void Prune(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var held = messages.ToHashSet();
        _entries.RemoveAll(e => !e.Turns.Any(held.Contains));
    }
}
