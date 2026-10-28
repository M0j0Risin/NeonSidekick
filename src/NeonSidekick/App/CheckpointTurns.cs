using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>A turn's journal once sealed: the after-state its commit records.</summary>
internal sealed record SealedJournal(FileJournal Journal, IReadOnlyDictionary<string, List<CheckpointState>> After);

/// <summary>What <c>/undo</c> found: the plan and the turns it undoes, or the notice to print instead (nothing to undo, no longer kept, unreadable).</summary>
internal sealed record UndoPick(RestorePlan? Plan, IReadOnlyList<(ChatMessage Message, string Id)> Undone, string? Notice);

/// <summary>
/// The steps of a checkpointed turn and of <c>/undo</c> that the screen and headless share (2026-10-28, the checkpoints plan): the journal
/// made at the turn's start, sealed and stamped before the turn is logged, committed after it; the stamped turns of a history, the turns an
/// <c>/undo</c> takes, and the spending of what it put back.
/// </summary>
internal static class CheckpointTurns
{
    /// <summary>A short random checkpoint id: the turn's name in the stores' refs and the history's stamp.</summary>
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>The turn's journal over <paramref name="roots"/>, or null with no store or <c>File checkpoints</c> off; each root's writer opens at its first entry.</summary>
    public static FileJournal? Begin(ICheckpointStore? store, AppSettingsData effective, string profileDirectory, IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (store is null || !effective.FileCheckpoints)
        {
            return null;
        }

        string id = NewId();
        return new FileJournal(id, roots, root => store.Begin(profileDirectory, root, id), CheckpointLimits.From(effective).LargestFileBytes);
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

    /// <summary>The history's stamped turns, newest first: each user message and its checkpoint id.</summary>
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
        }

        return stamped;
    }

    /// <summary>
    /// The turns an <c>/undo</c> of <paramref name="count"/> takes: the newest stamped turns whose checkpoints are still kept, and their
    /// restore worked out. A notice instead when none is stamped, none is kept any more, or the newest can't be read.
    /// </summary>
    public static UndoPick Pick(ICheckpointStore? store, string profileDirectory, IReadOnlyList<ChatMessage> messages, int count, bool checkpointsOn)
    {
        var stamped = Stamped(messages);
        if (stamped.Count == 0 || store is null)
        {
            return new UndoPick(null, [], UndoText.NothingNotice(!checkpointsOn));
        }

        var kept = store.Find(profileDirectory, stamped.Select(s => s.Id).ToList()).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var take = stamped.Where(s => kept.Contains(s.Id)).Take(count).ToList();
        if (take.Count == 0)
        {
            return new UndoPick(null, [], UndoText.NoLongerKept);
        }

        var plan = FileRestore.Plan(store, profileDirectory, take.Select(t => t.Id).ToList());
        if (plan.IsEmpty)
        {
            return new UndoPick(null, [], plan.StoppedAt is { } bad ? UndoText.Unreadable(bad.Problem) : UndoText.NoLongerKept);
        }

        var ids = plan.Ids.ToHashSet(StringComparer.Ordinal);
        return new UndoPick(plan, take.Where(t => ids.Contains(t.Id)).ToList(), null);
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
            if (gone.Contains(id))
            {
                ConversationHistory.SetTurnCheckpoint(message, null);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>The model's note after an <c>/undo</c> (<see cref="UndoText.ModelNote"/>), or null when nothing came back.</summary>
    public static string? Note(RestoreReport report, IReadOnlyList<(ChatMessage Message, string Id)> undone, string currentRoot)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(undone);
        var back = report.Files.Where(f => f.Outcome is RestoreOutcome.Restored or RestoreOutcome.AlreadyBack).Select(f => UndoText.Shown(f.Where, currentRoot)).ToList();
        if (back.Count == 0)
        {
            return null;
        }

        var kept = report.Files.Where(f => f.Outcome == RestoreOutcome.Skipped).Select(f => UndoText.Shown(f.Where, currentRoot)).ToList();
        return UndoText.ModelNote(undone.Select(t => ConversationRewind.UserLine(t.Message)).ToList(), back, kept);
    }

    /// <summary>
    /// A restore's report as headless lines: a line per file with its +/− counts (no diff rows), then the report's own lines. Pictures have no
    /// strip there.
    /// </summary>
    public static IReadOnlyList<string> HeadlessLines(RestoreReport report, string currentRoot)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>();
        foreach (var file in report.Files.Where(f => f.Outcome == RestoreOutcome.Restored))
        {
            string shown = UndoText.Shown(file.Where, currentRoot);
            lines.Add(file.Diff is { } diff ? UndoText.DiffNote(shown) + " (+" + diff.Added.ToString(System.Globalization.CultureInfo.InvariantCulture) + " −" + diff.Removed.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : UndoText.DiffNote(shown));
        }

        lines.AddRange(UndoText.Report(report, currentRoot));
        return lines;
    }
}
