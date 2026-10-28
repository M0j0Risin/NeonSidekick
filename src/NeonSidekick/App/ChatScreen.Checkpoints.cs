using System.Globalization;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

// ── File checkpoints, /undo and /rewind … files (2026-10-28, the checkpoints plan) ──────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// The <c>/undo</c> note the next turn of this conversation carries ahead of the user's text (<see cref="UndoText.ModelNote"/>), null for
    /// none. Two <c>/undo</c>s before a message merge into one; a withdrawn turn gives it back; <c>/rewind</c>, <c>/clear</c>, <c>/new</c>, a
    /// session restore and a profile switch drop it (<see cref="DropUndoNote"/>), since the conversation it described is gone.
    /// </summary>
    private string? _undoNote;

    /// <summary>Forgets a pending <c>/undo</c> note: the conversation it described is gone.</summary>
    private void DropUndoNote() => _undoNote = null;

    /// <summary>
    /// The turn's journal (the plan's "where the turn's journal is made"): over the working folder and the profile's images folder, for a
    /// main-chat turn while <c>File checkpoints</c> is on and a store was given; never a botchat turn or a Ralph pass (their own
    /// conversations). Each root's store writer opens at its first entry. Also hands the pending <c>/undo</c> note to the turn; returns both.
    /// </summary>
    private (FileJournal? Journal, string? Note) BeginTurnJournal(Assistant assistant, BotParticipant? bot)
    {
        if (bot is not null || PassTurnNow || _ralphPass is not null)
        {
            return (null, null);
        }

        string? note = _undoNote;
        _undoNote = null;
        assistant.TurnNote = note;
        if (note is not null)
        {
            // Dim beside the user's line, on the page's feed too: the model reads it ahead of the message.
            _transcript.Notice(note);
        }

        var journal = CheckpointTurns.Begin(_checkpoints, _effective(), _settings.ProfileDirectory, [_files.Root, _images.Root]);
        assistant.Journal = journal;
        return (journal, note);
    }

    /// <summary>
    /// The turn's end, before <see cref="LogTurn"/> (seal → stamp → log, so the stamp reaches the stored history): seals the journal, takes
    /// each path's after-state on the turn's own flow and stamps the turn's user message with the checkpoint id. A withdrawn turn gives
    /// its note back and keeps nothing; a turn that changed no file, or whose stores all failed, drops its writers. <paramref name="appToken"/>
    /// (the app closing) stops the hashing, and that turn's checkpoint is dropped.
    /// </summary>
    private SealedJournal? SealTurnJournal(Assistant assistant, FileJournal? journal, string? note, bool withdrawn, CancellationToken appToken)
    {
        assistant.Journal = null;
        assistant.TurnNote = null;
        if (withdrawn && note is not null)
        {
            // The message goes back on the line, its note pending again (an /undo since is only possible after the turn: merged after it).
            _undoNote = _undoNote is null ? note : UndoText.Merge(note, _undoNote);
        }

        return CheckpointTurns.Seal(_checkpoints, assistant.History, journal, withdrawn, appToken);
    }

    /// <summary>
    /// The turn's checkpoint committed after <see cref="LogTurn"/>, so its session is known (<c>none</c> with <c>Session logging</c> off):
    /// one record per root that changed, in the background (the idle line isn't held).
    /// </summary>
    private void CommitTurnJournal(Assistant assistant, SealedJournal? turn, string text, AppSettingsData effective)
    {
        if (turn is null || _checkpoints is not { } store)
        {
            return;
        }

        string session = effective.SessionLogging && _sessionId is { } id ? id.ToString(CultureInfo.InvariantCulture) : CheckpointStore.NoSession;
        CheckpointTurns.Commit(store, assistant.History, turn, text, session, effective, _time.GetUtcNow());
    }

    /// <summary>
    /// <c>/undo [&lt;n&gt;]</c>: puts back the files of the last <c>n</c> turns of this conversation that hold kept file checkpoints (1 by
    /// default), the conversation kept. The question lists the files; conflicts get their own; the report shows what came back, and the next
    /// message tells the model (<see cref="UndoText.ModelNote"/>). The checkpoints are spent once the restore finishes (not when Esc stopped
    /// it), so a second <c>/undo</c> goes one turn further back. <c>/undo purge [all]</c> deletes the stores (<see cref="PurgeCheckpointsAsync"/>).
    /// </summary>
    private async Task UndoAsync(string args, CancellationToken cancellationToken)
    {
        if (UndoText.Parse(args) is not { } parsed)
        {
            _transcript.Error(UndoText.UsageError);
            return;
        }

        if (parsed.Purge)
        {
            await PurgeCheckpointsAsync(parsed.All, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_checkpoints is { } pending)
        {
            // A turn just ended may still be committing: its checkpoint is the first undone.
            await pending.FlushAsync().ConfigureAwait(false);
        }

        var pick = CheckpointTurns.Pick(_checkpoints, _settings.ProfileDirectory, _session.History.Messages, parsed.Count, _effective().FileCheckpoints);
        if (pick.Plan is not { } plan)
        {
            _transcript.Notice(pick.Notice!);
            return;
        }

        string question = UndoText.Question(pick.Undone.Count) + "\n" + UndoText.Caption(plan, _files.Root, _images.Root, CheckpointTools.KindsAmong(plan.Tools));
        if (!await ConfirmAsync(question, cancellationToken).ConfigureAwait(false))
        {
            _transcript.Notice(KeptNotice);
            return;
        }

        if (await AskConflictsAsync(plan, cancellationToken).ConfigureAwait(false) is not { } over)
        {
            _transcript.Notice(KeptNotice);
            return;
        }

        var report = await RestoreFilesAsync(plan, over, cancellationToken).ConfigureAwait(false);
        ShowRestoreReport(report);
        SpendCheckpoints(report.Spent);
        if (CheckpointTurns.Note(report, pick.Undone, _files.Root) is { } note)
        {
            _undoNote = UndoText.Merge(_undoNote, note);
        }
    }

    /// <summary>The spent checkpoints' refs removed and their stamps taken off this conversation, the history saved.</summary>
    private void SpendCheckpoints(IReadOnlyList<string> spent)
    {
        if (CheckpointTurns.Spend(_checkpoints, _settings.ProfileDirectory, _session.History.Messages, spent))
        {
            SaveClaudeHistory();
        }
    }

    /// <summary>
    /// <c>/undo purge</c> (this working folder's store and the images folder's) and <c>/undo purge all</c> (every store of the profile), each
    /// after a yes; deleted for good. A store another instance's turn is writing to is left and named.
    /// </summary>
    private async Task PurgeCheckpointsAsync(bool all, CancellationToken cancellationToken)
    {
        if (_checkpoints is not { } store)
        {
            _transcript.Notice(UndoText.NothingToPurge);
            return;
        }

        await store.FlushAsync().ConfigureAwait(false);
        string profile = _settings.ProfileDirectory;
        IReadOnlyCollection<string>? roots = all ? null : [_files.Root, _images.Root];
        var stores = store.Stores(profile).Where(s => roots is null || roots.Contains(s.Root, WorkingDirectory.PathComparer)).ToList();
        if (stores.Count == 0)
        {
            _transcript.Notice(UndoText.NothingToPurge);
            return;
        }

        string question = all ? UndoText.PurgeAllQuestion(stores.Sum(s => s.Bytes), stores.Count) : UndoText.PurgeQuestion(_files.Root);
        if (!await ConfirmAsync(question, cancellationToken).ConfigureAwait(false))
        {
            _transcript.Notice(KeptNotice);
            return;
        }

        CheckpointPurgeResult? result = null;
        await WaitUnderWatchAsync(token => _transcript.WithSpinnerAsync(UndoText.Purging, async () =>
        {
            result = await Task.Run(() => store.Purge(profile, roots, token)).ConfigureAwait(false);
            return true;
        }), KeySource.IsTurnCancel, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return;
        }

        DiagnosticLog.Info(CheckpointStore.Category, UndoText.PurgedLogLine(result.Purged, result.Bytes));
        _transcript.Notice(UndoText.Purged(result.Purged, result.Bytes));
        if (result.Busy.Count + result.Failed.Count > 0)
        {
            _transcript.Warning(UndoText.PurgeLeft([.. result.Busy, .. result.Failed]));
        }
    }

    /// <summary>
    /// The conflict question when files changed since the checkpoints (2026-10-28): restore over them (true), skip them (false), or cancel
    /// (null). With only kind changes (always skipped) nothing is asked. From the page a raced card; without a pane, a typed yes/no.
    /// </summary>
    private async Task<bool?> AskConflictsAsync(RestorePlan plan, CancellationToken cancellationToken)
    {
        var asked = plan.Conflicts.Where(c => !c.KindChanged).Select(c => UndoText.Shown(c.Where, _files.Root)).ToList();
        if (asked.Count == 0)
        {
            return false;
        }

        bool toBin = _effective().FileDeletesToRecycleBin && _recycleBin is not null;
        string question = UndoText.ConflictQuestion(asked.Count);
        string caption = UndoText.ConflictCaption(plan.Conflicts, _files.Root, toBin);
        if (FromWebNow)
        {
            var spec = new WebAskSpec("confirm", question, Choices(UndoText.ConflictChoices, UndoText.ConflictRows),
                Body: string.Join('\n', caption.Split('\n').Skip(1)), Lines: UndoText.Listed(asked));
            var outcome = await RaceAsync<string>(spec,
                async token => await RewindPicker.ConflictAsync(question, caption, token).ConfigureAwait(false) switch { true => "restore", false => "skip", _ => "cancel" },
                a => a.Choice ?? "cancel", "The conflict pane failed: ", cancellationToken).ConfigureAwait(false);
            return outcome.Value switch { "restore" => true, "skip" => false, _ => null };
        }

        if (_pane.Enabled)
        {
            return await RewindPicker.ConflictAsync(question, caption, cancellationToken).ConfigureAwait(false);
        }

        // No pane: yes restores over them, no skips them.
        return await ConfirmAsync(UndoText.TypedConflictQuestion + "\n" + caption, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A card's choices: the page's ids beside the pane's rows.</summary>
    private static List<WebChoice> Choices(IReadOnlyList<string> ids, IReadOnlyList<string> rows) =>
        ids.Select((id, i) => new WebChoice(id, rows[i])).ToList();

    /// <summary>
    /// The restore itself under the working spinner, as an attached PDF's read waits (<see cref="WaitUnderWatchAsync"/>): Esc, the page's
    /// Stop and the kill switch stop it between files, and the report says what came back. The bin is <c>File deletes to Recycle bin</c>'s,
    /// for both folders.
    /// </summary>
    private async Task<RestoreReport> RestoreFilesAsync(RestorePlan plan, bool restoreOver, CancellationToken cancellationToken)
    {
        var store = _checkpoints!;
        var bin = _effective().FileDeletesToRecycleBin ? _recycleBin : null;
        RestoreReport? report = null;
        await WaitUnderWatchAsync(token => _transcript.WithSpinnerAsync(UndoText.Restoring, async () =>
        {
            // Never handed the token: the restore reads it between files and returns its report stopped.
            report = await Task.Run(() => FileRestore.Run(plan, store, RestoreDirectoryFor, restoreOver, bin, token)).ConfigureAwait(false);
            return true;
        }), KeySource.IsTurnCancel, cancellationToken).ConfigureAwait(false);
        report ??= new RestoreReport([], Stopped: true, []);
        DiagnosticLog.Info(CheckpointStore.Category, "Restore: " + FileRestore.Summary(report) + (report.Stopped ? " (stopped)" : ""));
        return report;
    }

    /// <summary>A root's pinned working directory for a restore: the live guards, its own seen record (<see cref="WorkingDirectory.PinnedAt"/>).</summary>
    private WorkingDirectory RestoreDirectoryFor(string root) =>
        WorkingDirectory.PathComparer.Equals(root, _images.Root) ? _images.PinnedAt(root) : _files.PinnedAt(root);

    /// <summary>
    /// What came back, as the tool writes show it: the first <see cref="FileRestore.ListedFiles"/> text files' diffs (with <c>Show file
    /// diffs</c> on), the rest by name, then the counts and what was skipped, not kept or failed. Pictures it deleted or replaced leave the
    /// strip and the page.
    /// </summary>
    private void ShowRestoreReport(RestoreReport report)
    {
        var effective = _effective();
        int diffs = 0;
        var names = new List<string>();
        foreach (var file in report.Files.Where(f => f.Outcome == RestoreOutcome.Restored))
        {
            string shown = UndoText.Shown(file.Where, _files.Root);
            if (file.Diff is { } diff && effective.ShowFileDiffs && diffs < FileRestore.ListedFiles)
            {
                _transcript.ToolDiff(UndoText.DiffNote(shown), diff,
                    Math.Clamp(effective.DiffMaxLines, AppSettingsData.MinDiffMaxLines, AppSettingsData.MaxDiffMaxLines),
                    Math.Clamp(effective.DiffCollapseCount, AppSettingsData.MinDiffCollapseCount, AppSettingsData.MaxDiffCollapseCount));
                diffs++;
            }
            else
            {
                names.Add(shown);
            }
        }

        if (names.Count > 0)
        {
            _transcript.ToolNote(UndoText.PutBack(names));
        }

        var lines = UndoText.Report(report, _files.Root);
        _transcript.Notice(lines[0]);
        foreach (string line in lines.Skip(1))
        {
            _transcript.Warning(line);
        }

        foreach (string picture in report.Pictures)
        {
            ForgetDeletedFile(picture);
        }
    }

    /// <summary>
    /// The files a rewind to <paramref name="cut"/> would put back, worked out once per turn picked (<paramref name="plans"/>): null with no
    /// store, no checkpoint among the cut turns, or none of them still kept, so the rewind asks today's yes/no.
    /// </summary>
    private RestorePlan? RewindPlan(RewindTurn turn, RewindCut cut, Dictionary<int, RestorePlan?> plans)
    {
        if (plans.TryGetValue(turn.Start, out var known))
        {
            return known;
        }

        RestorePlan? plan = null;
        if (_checkpoints is { } store && cut.Checkpoints is { Count: > 0 } ids)
        {
            var made = FileRestore.Plan(store, _settings.ProfileDirectory, ids);
            plan = made.IsEmpty ? null : made;
        }

        plans[turn.Start] = plan;
        return plan;
    }

    /// <summary>The files choice's caption for a rewind (null: no files to put back, today's yes/no).</summary>
    private string? RewindFilesCaption(RewindTurn turn, RewindCut cut, Dictionary<int, RestorePlan?> plans) =>
        RewindPlan(turn, cut, plans) is { } plan ? UndoText.Caption(plan, _files.Root, _images.Root, CheckpointTools.KindsAmong(cut.ChangingTools)) : null;

    /// <summary>The page's card for a rewind's files choice: the question, the notes as its body, the files as its lines.</summary>
    private WebAskSpec RewindFilesSpec(RewindTurn turn, RewindCut cut, RestorePlan plan)
    {
        var names = UndoText.Names(plan, _files.Root);
        var notes = UndoText.Notes(plan, _files.Root, _images.Root, CheckpointTools.KindsAmong(cut.ChangingTools));
        return new WebAskSpec("confirm", RewindText.ConfirmPrompt(turn, cut), Choices(RewindText.FilesChoices, RewindText.FilesRows),
            Body: string.Join('\n', [UndoText.FilesHeading(names.Count), .. notes]), Lines: UndoText.Listed(names));
    }

    /// <summary>
    /// After the rewind's choice: with files, the conflicts asked and the restore run (a Cancel or an Esc-stopped restore leaves the
    /// conversation as it was: null back); the cut turns' checkpoints dropped either way once the rewind goes ahead. The report to print
    /// after the rewound notice, or none, and whether to go ahead.
    /// </summary>
    private async Task<(bool Go, RestoreReport? Report)> RewindFilesAsync(RewindChoice choice, Dictionary<int, RestorePlan?> plans, CancellationToken cancellationToken)
    {
        var cut = ConversationRewind.Preview(_session.History.Messages, choice.Turn.Start);
        RestoreReport? report = null;
        if (choice.Files && RewindPlan(choice.Turn, cut, plans) is { } plan)
        {
            if (await AskConflictsAsync(plan, cancellationToken).ConfigureAwait(false) is not { } over)
            {
                _transcript.Notice(KeptNotice);
                return (false, null);
            }

            report = await RestoreFilesAsync(plan, over, cancellationToken).ConfigureAwait(false);
            if (report.Stopped)
            {
                // Stopped by Esc: the conversation stays; /rewind or /undo again finishes the job.
                ShowRestoreReport(report);
                _transcript.Notice(KeptNotice);
                return (false, null);
            }
        }

        if (_checkpoints is { } store && cut.Checkpoints is { Count: > 0 } ids)
        {
            store.Remove(_settings.ProfileDirectory, ids);
        }

        return (true, report);
    }

    /// <summary>
    /// The sessions a purge deleted take their file checkpoints with them (<see cref="Sessions.SessionStore.Purged"/>), from every store of
    /// the profile. Raised on the purging thread; the store's work is small and never blocks on the screen.
    /// </summary>
    private void OnSessionsPurged(IReadOnlyList<long> ids)
    {
        if (_checkpoints is not { } store || ids.Count == 0)
        {
            return;
        }

        try
        {
            store.RemoveSessions(_settings.ProfileDirectory, ids.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LibGit2Sharp.LibGit2SharpException)
        {
            DiagnosticLog.Warn(CheckpointStore.Category, "Could not remove purged sessions' file checkpoints: " + ex.Message);
        }
    }

    /// <summary>The start-up and profile-switch sweep: the age rule, gone folders, finished purges; in the background.</summary>
    private void SweepCheckpoints()
    {
        if (_checkpoints is not { } store)
        {
            return;
        }

        string profile = _settings.ProfileDirectory;
        var limits = CheckpointLimits.From(_effective());
        _ = Task.Run(() =>
        {
            try
            {
                store.Sweep(profile, limits);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LibGit2Sharp.LibGit2SharpException)
            {
                DiagnosticLog.Warn(CheckpointStore.Category, "The file checkpoints' sweep failed: " + ex.Message);
            }
        });
    }
}
