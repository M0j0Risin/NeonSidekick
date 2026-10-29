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
    /// The <c>/undo</c> notes the next turn of this conversation carries ahead of the user's text (<see cref="UndoNotes"/>). The turn that
    /// carries them clears them once it is in the history; a withdrawn turn leaves them pending; a rewind keeps those whose turns stay;
    /// <c>/clear</c>, <c>/new</c>, a session restore and a profile switch drop them (<see cref="DropUndoNote"/>).
    /// </summary>
    private readonly UndoNotes _undoNotes = new();

    /// <summary>The start-up or profile-switch sweep running, awaited before a profile is deleted or renamed (2026-10-28, review P10-05).</summary>
    private Task _checkpointSweep = Task.CompletedTask;

    /// <summary>Forgets the pending <c>/undo</c> notes: the conversation they described is gone.</summary>
    private void DropUndoNote() => _undoNotes.Clear();

    /// <summary>
    /// The turn's journal (the plan's "where the turn's journal is made"): over the working folder and the profile's images folder, for a
    /// main-chat turn while <c>File checkpoints</c> is on and a store was given; never a botchat turn or a Ralph pass (their own
    /// conversations). Each root's store writer opens at its first entry. Also hands the pending <c>/undo</c> notes to the turn, without
    /// clearing them (2026-10-28, review U1: <see cref="SealTurnJournal"/> does, once the turn used them); returns both.
    /// </summary>
    private (FileJournal? Journal, string? Note) BeginTurnJournal(Assistant assistant, BotParticipant? bot)
    {
        if (bot is not null || PassTurnNow || _ralphPass is not null)
        {
            return (null, null);
        }

        string? note = _undoNotes.Render();
        assistant.TurnNote = note;
        if (note is not null)
        {
            // Dim beside the user's line, on the page's feed too: the model reads it ahead of the message.
            _transcript.Notice(note);
        }

        var journal = CheckpointTurns.Begin(_checkpoints, _effective(), _settings.ProfileDirectory, _files, _images);
        assistant.Journal = journal;
        return (journal, note);
    }

    /// <summary>
    /// The turn's end, before <see cref="LogTurn"/> (seal → stamp → log, so the stamp reaches the stored history): seals the journal, takes
    /// each path's after-state on the turn's own flow and stamps the turn's user message with the checkpoint id. The notes are cleared only
    /// when the turn put them in its message and wasn't withdrawn; a turn that changed no file, or whose stores all failed, drops its writers.
    /// <paramref name="appToken"/> (the app closing) stops the hashing, and that turn's checkpoint is dropped.
    /// </summary>
    private SealedJournal? SealTurnJournal(Assistant assistant, FileJournal? journal, string? note, bool withdrawn, CancellationToken appToken)
    {
        bool used = note is not null && assistant.TurnNote is null;
        assistant.Journal = null;
        assistant.TurnNote = null;
        if (used && !withdrawn)
        {
            _undoNotes.Clear();
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
    /// default), the conversation kept. The checkpoints are read and hashed under a spinner off the screen's flow (2026-10-28, review P1-12;
    /// Esc stops it); the question quotes the turns and lists the files; conflicts get their own; the report shows what came back, and the
    /// next message tells the model. Only what came back is spent (<see cref="CheckpointTurns.Finish"/>), so a second <c>/undo</c> retries what
    /// may pass and goes on further back. <c>/undo purge [all]</c> deletes stores (<see cref="PurgeCheckpointsAsync"/>).
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

        UndoPick? pick = null;
        var messages = _session.History.Messages;
        await WaitUnderWatchAsync(token => _transcript.WithSpinnerAsync(UndoText.Planning, async () =>
        {
            pick = await CheckpointTurns.PickAsync(_checkpoints, _settings.ProfileDirectory, messages, parsed.Count, _effective().FileCheckpoints, token).ConfigureAwait(false);
            return true;
        }), KeySource.IsTurnCancel, cancellationToken).ConfigureAwait(false);
        if (pick is null)
        {
            _transcript.Notice(KeptNotice);
            return;
        }

        if (pick.Plan is not { } plan)
        {
            _transcript.Notice(pick.Notice!);
            return;
        }

        if (pick.NewerNotKept > 0)
        {
            _transcript.Notice(UndoText.NewerNotKept(pick.NewerNotKept));
        }

        string question = UndoText.Question(CheckpointTurns.UndoneLines(pick)) + "\n" + UndoText.Caption(plan, _files.Root, _images.Root, CheckpointTools.KindsAmong(plan.Tools));
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
        if (CheckpointTurns.Finish(_checkpoints, _settings.ProfileDirectory, _session.History.Messages, report, pick, _undoNotes, _files.Root))
        {
            SaveClaudeHistory();
        }
    }

    /// <summary>
    /// <c>/undo purge</c> (this working folder's store; the purged turns' picture halves go with it, 2026-10-28, review P2-09) and
    /// <c>/undo purge all</c> (every store of the profile), each after a yes; deleted for good. The stores are looked up under the spinner
    /// (review L4). A store another instance's turn is writing to is left and named; one claimed but not yet deleted when Esc stopped it is
    /// named as still going (review P13-02).
    /// </summary>
    private async Task PurgeCheckpointsAsync(bool all, CancellationToken cancellationToken)
    {
        if (_checkpoints is not { } store)
        {
            _transcript.Notice(UndoText.NothingToPurge);
            return;
        }

        string profile = _settings.ProfileDirectory;
        var roots = CheckpointTurns.PurgeRoots(all, _files);
        IReadOnlyList<CheckpointStoreInfo> stores = [];
        await WaitUnderWatchAsync(token => _transcript.WithSpinnerAsync(UndoText.Purging, async () =>
        {
            await store.FlushAsync().ConfigureAwait(false);
            stores = await Task.Run(() => store.Stores(profile, roots), token).ConfigureAwait(false);
            return true;
        }), KeySource.IsTurnCancel, cancellationToken).ConfigureAwait(false);
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
            // Never handed to Task.Run: the purge reads the token between stores and says what it left.
            result = await Task.Run(() => store.Purge(profile, roots, token)).ConfigureAwait(false);
            return true;
        }), KeySource.IsTurnCancel, cancellationToken).ConfigureAwait(false);
        if (result is not { } done)
        {
            return;
        }

        DiagnosticLog.Info(CheckpointStore.Category, UndoText.PurgedLogLine(done.Purged, done.Bytes, done.Pending.Count));
        var lines = CheckpointTurns.PurgeLines(done);
        foreach (string line in lines)
        {
            if (line == lines[0] && done.Purged > 0)
            {
                _transcript.Notice(line);
            }
            else
            {
                _transcript.Warning(line);
            }
        }
    }

    /// <summary>
    /// The conflict question when files changed since the checkpoints (2026-10-28): restore over them (true), skip them (false), or cancel
    /// (null). With only kind changes (always skipped) nothing is asked. It opens on Skip them (2026-10-28, review P7-02: Esc is always the
    /// safe answer); from the page a raced card; without a pane, one three-way question.
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
        var page = new MenuPage(question, UndoText.ConflictRows, UndoText.ConflictKeys) { Hotkeys = UndoText.ConflictHotkeys, Caption = caption, CaptionGrows = true };
        if (FromWebNow)
        {
            var spec = new WebAskSpec("confirm", question, Choices(UndoText.ConflictChoices, UndoText.ConflictRows),
                Body: string.Join('\n', caption.Split('\n').Skip(1)), Lines: UndoText.Listed(asked));
            var outcome = await RaceAsync<string>(spec,
                async token => RewindMenu.ThreeWay(await RewindPicker.ThreeWayAsync(page, 1, token).ConfigureAwait(false)) switch { true => "restore", false => "skip", _ => "cancel" },
                a => a.Choice ?? "cancel", "The conflict pane failed: ", cancellationToken).ConfigureAwait(false);
            return outcome.Value switch { "restore" => true, "skip" => false, _ => null };
        }

        return RewindMenu.ThreeWay(await ChooseThreeAsync(page, 1, UndoText.TypedConflictChoice, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// A three-row question (2026-10-28, review P6-09: one shape for the conflict and the rewind's files choice) with its cursor on
    /// <paramref name="cursor"/>: the pane, else the prompt's menu, else typed (<paramref name="typed"/> names the page's hotkeys; a blank or any
    /// other answer is null). The row picked, null for Esc.
    /// </summary>
    private async Task<int?> ChooseThreeAsync(MenuPage page, int cursor, string typed, CancellationToken cancellationToken)
    {
        if (_pane.Enabled)
        {
            return await RewindPicker.ThreeWayAsync(page, cursor, cancellationToken).ConfigureAwait(false);
        }

        if (_menu.CanShowMenus())
        {
            return await _menu.ChooseAsync(page, cancellationToken, cursor).ConfigureAwait(false);
        }

        _transcript.Notice(page.Title + (page.Caption is { Length: > 0 } caption ? "\n" + caption : "") + "\n" + typed);
        var answer = await _input.ReadAsync(remember: false, allowEmpty: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        return answer is InputResult.Submitted { Text: var text } && text.Trim() is { Length: 1 } one
            && page.Hotkeys is { } hotkeys && hotkeys.TryGetValue(char.ToLowerInvariant(one[0]), out int row) ? row : null;
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
        var directoryFor = CheckpointTurns.DirectoryFor(_files, _images);
        RestoreReport? report = null;
        await WaitUnderWatchAsync(token => _transcript.WithSpinnerAsync(UndoText.Restoring, async () =>
        {
            // Never handed the token: the restore reads it between files and returns its report stopped.
            report = await Task.Run(() => FileRestore.Run(plan, store, directoryFor, restoreOver, bin, token)).ConfigureAwait(false);
            return true;
        }), KeySource.IsTurnCancel, cancellationToken).ConfigureAwait(false);
        report ??= new RestoreReport([], Stopped: true, []);
        DiagnosticLog.Info(CheckpointStore.Category, "Restore: " + FileRestore.Summary(report) + (report.Stopped ? " (stopped)" : ""));
        return report;
    }

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
            _transcript.ToolNote(UndoText.PutBack(UndoText.Listed(names)));
        }

        var lines = CheckpointTurns.ReportLines(report, _files.Root);
        _transcript.Notice(lines.Summary);
        foreach (string line in lines.Warnings)
        {
            _transcript.Warning(line);
        }

        foreach (string picture in report.Pictures)
        {
            ForgetDeletedFile(picture);
        }
    }

    /// <summary>
    /// The files a rewind to <paramref name="cut"/> would put back, worked out once per turn picked (<paramref name="plans"/>) on the thread pool
    /// (2026-10-28, review P1-12): null with no store, no checkpoint among the cut turns, or none of them still kept, so the rewind asks
    /// today's yes/no.
    /// </summary>
    private async Task<RestorePlan?> RewindPlanAsync(RewindTurn turn, RewindCut cut, Dictionary<int, RestorePlan?> plans, CancellationToken cancellationToken)
    {
        if (plans.TryGetValue(turn.Start, out var known))
        {
            return known;
        }

        var store = _checkpoints;
        string profile = _settings.ProfileDirectory;
        var plan = await Task.Run(() => CheckpointTurns.RewindPlan(store, profile, cut, cancellationToken), cancellationToken).ConfigureAwait(false);
        plans[turn.Start] = plan;
        return plan;
    }

    /// <summary>
    /// The files choice's caption for a rewind (null: no files to put back, today's yes/no), with what "conversation only" gives up
    /// (2026-10-28, review P7-03).
    /// </summary>
    private async Task<string?> RewindFilesCaptionAsync(RewindTurn turn, RewindCut cut, Dictionary<int, RestorePlan?> plans, CancellationToken cancellationToken) =>
        await RewindPlanAsync(turn, cut, plans, cancellationToken).ConfigureAwait(false) is { } plan
            ? UndoText.Caption(plan, _files.Root, _images.Root, CheckpointTools.KindsAmong(cut.ChangingTools)) + "\n" + RewindText.ConversationOnlyNote
            : null;

    /// <summary>The page's card for a rewind's files choice: the question, the notes as its body, the files as its lines.</summary>
    private WebAskSpec RewindFilesSpec(RewindTurn turn, RewindCut cut, RestorePlan plan)
    {
        var names = UndoText.Names(plan, _files.Root);
        var notes = UndoText.Notes(plan, _files.Root, _images.Root, CheckpointTools.KindsAmong(cut.ChangingTools));
        return new WebAskSpec("confirm", RewindText.ConfirmPrompt(turn, cut), Choices(RewindText.FilesChoices, RewindText.FilesRows),
            Body: string.Join('\n', [UndoText.FilesHeading(names.Count), .. notes, RewindText.ConversationOnlyNote]), Lines: UndoText.Listed(names));
    }

    /// <summary>
    /// After the rewind's choice: with files, the conflicts asked and the restore run; then the rewind's rule (<see cref="CheckpointTurns.FinishRewind"/>,
    /// 2026-10-28, review P5-01): it goes ahead only when every replayed checkpoint came back whole, else the report and
    /// <see cref="RewindText.NotAllBack"/> are shown and the conversation and every checkpoint stay. A Cancel leaves everything as it was.
    /// The report and plan to show after the rewound notice, and whether to go ahead.
    /// </summary>
    private async Task<(bool Go, RestoreReport? Report, RestorePlan? Plan)> RewindFilesAsync(RewindChoice choice, Dictionary<int, RestorePlan?> plans, CancellationToken cancellationToken)
    {
        var cut = ConversationRewind.Preview(_session.History.Messages, choice.Turn.Start);
        RestorePlan? plan = null;
        RestoreReport? report = null;
        if (choice.Files && await RewindPlanAsync(choice.Turn, cut, plans, cancellationToken).ConfigureAwait(false) is { } planned)
        {
            plan = planned;
            if (await AskConflictsAsync(planned, cancellationToken).ConfigureAwait(false) is not { } over)
            {
                _transcript.Notice(KeptNotice);
                return (false, null, null);
            }

            report = await RestoreFilesAsync(planned, over, cancellationToken).ConfigureAwait(false);
        }

        if (!CheckpointTurns.FinishRewind(_checkpoints, _settings.ProfileDirectory, cut, plan, report))
        {
            ShowRestoreReport(report!);
            _transcript.Warning(RewindText.NotAllBack);
            return (false, null, null);
        }

        return (true, report, plan);
    }

    /// <summary>
    /// The sessions a purge deleted take their file checkpoints with them (<see cref="Sessions.SessionStore.Purged"/>), from every store of
    /// the profile. Raised on the purging thread; the store queues the removal behind the commits in flight (2026-10-28, review P4-06), so
    /// this returns at once and an exit's flush waits for it.
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

    /// <summary>The start-up and profile-switch sweep: the age rule, gone folders, finished purges; in the background, kept to be awaited.</summary>
    private void SweepCheckpoints() =>
        _checkpointSweep = CheckpointTurns.SweepAsync(_checkpoints, _settings.ProfileDirectory, CheckpointLimits.From(_effective()));

    /// <summary>
    /// Every checkpoint commit and the sweep finished (2026-10-28, review P10-05): before a profile is deleted or renamed, so nothing writes into
    /// the folder as it goes.
    /// </summary>
    private async Task SettleCheckpointsAsync()
    {
        if (_checkpoints is { } store)
        {
            await store.FlushAsync().ConfigureAwait(false);
        }

        await _checkpointSweep.ConfigureAwait(false);
    }
}
