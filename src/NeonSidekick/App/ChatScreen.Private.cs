using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// Private conversations (2026-11-25, the private-conversations plan, the user's ask): a conversation that leaves no trace in session history, at the PC
/// and on the page. <c>/private</c> switches the one on screen (its saved row and input lines deleted after a yes), <c>/private new</c> ends it as
/// <c>/clear</c> does and starts a private one, <c>/private off</c> ends it. <c>--private</c>, <c>NEONSIDEKICK_PRIVATE</c> and <c>Private at start</c> start
/// every conversation private. The state and the lists are <see cref="PrivateConversation"/>'s, shared with headless; this part holds the command, the
/// asks and the end (<see cref="EndPrivateAsync"/>), which runs before <see cref="ClearAndRefresh"/>, <see cref="RestoreSession"/> or a profile switch.
/// </summary>
internal sealed partial class ChatScreen
{
    /// <summary>The conversation's privacy and its lists.</summary>
    private readonly PrivateConversation _private = new();

    /// <summary>The private conversation's state, for the tests.</summary>
    internal PrivateConversation Private => _private;

    /// <summary>
    /// Whether this conversation is written to the store (2026-11-25): <c>Session logging</c> on and not private. Every write-side read of the setting goes
    /// through it; the reads of other sessions (<c>/learn sessions</c>, a reflection's earlier sessions, <c>session_manager</c>) don't.
    /// </summary>
    private bool SavesSession(AppSettingsData effective) => effective.SessionLogging && !_private.IsPrivate;

    /// <summary>Whether reflection, skill records and <c>save_memory</c> may run: always outside a private conversation, inside it under <c>Private learning</c>.</summary>
    private bool LearnsNow(AppSettingsData effective) => !_private.IsPrivate || effective.PrivateLearning;

    /// <summary>
    /// The turn's disabled set with <c>save_memory</c> and <c>skill_editor</c> added while <see cref="LearnsNow"/> is false (2026-11-25): never
    /// <c>/tools</c>' off-by-name list, so the user's own list is unchanged and the next conversation offers them again.
    /// </summary>
    private IReadOnlySet<string> PrivateWithheld(IReadOnlySet<string> disabled, AppSettingsData effective)
    {
        if (LearnsNow(effective))
        {
            return disabled;
        }

        return new HashSet<string>(disabled, StringComparer.Ordinal) { Llm.Tools.SaveMemoryTool.ToolName, Llm.Tools.SkillEditorTool.ToolName };
    }

    /// <summary>The two learning tools while <see cref="LearnsNow"/> is false, else none: what <c>/tools</c> notes as withheld (2026-11-26).</summary>
    private IReadOnlySet<string> WithheldNow(AppSettingsData effective) =>
        LearnsNow(effective) ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal) { Llm.Tools.SaveMemoryTool.ToolName, Llm.Tools.SkillEditorTool.ToolName };

    /// <summary>
    /// Why the run itself starts conversations private, or <see cref="PrivateReason.None"/>: the flag, then the variable, then the setting, as
    /// <see cref="SidekickApp.OverriddenBy"/> names them.
    /// </summary>
    private PrivateReason StartReason()
    {
        if (!_effective().PrivateAtStart)
        {
            return PrivateReason.None;
        }

        return _overriddenBy(SettingsField.PrivateAtStart) switch
        {
            SidekickOptions.PrivateFlag => PrivateReason.Flag,
            EnvironmentOverrides.PrivateVariable => PrivateReason.Variable,
            _ => PrivateReason.Setting,
        };
    }

    /// <summary>
    /// A fresh conversation under the flag, variable or setting starts private: the start, <c>/new</c>, <c>/clear</c>, a profile switch. Quiet: the caller
    /// says it (<see cref="PrivateStartLines"/>). True when it went private.
    /// </summary>
    private bool StartPrivateIfSet()
    {
        var reason = StartReason();
        if (_private.IsPrivate || reason == PrivateReason.None)
        {
            return false;
        }

        EnterPrivate(reason);
        return true;
    }

    /// <summary>
    /// The lines a fresh private conversation prints: the notice, what deliberate saves still do (<see cref="PrivateText.SavesNote"/>, 2026-11-26), and the
    /// pictures note under <c>ask</c> or <c>delete</c>.
    /// </summary>
    private IReadOnlyList<string> PrivateStartLines(bool fresh)
    {
        var lines = new List<string> { fresh ? PrivateText.NewNotice : PrivateText.EnteredNotice, PrivateText.SavesNote };
        switch (_private.Pictures)
        {
            case PrivatePicturesMode.Ask:
                lines.Add(PrivateText.AskNote);
                break;
            case PrivatePicturesMode.Delete:
                lines.Add(PrivateText.DeleteNote);
                break;
        }

        return lines;
    }

    private void SayPrivateStart(bool fresh)
    {
        foreach (string line in PrivateStartLines(fresh))
        {
            _transcript.Notice(line);
        }
    }

    /// <summary>
    /// The conversation goes private: the stretch, the switches, the pastes' mark (blanked from there at the end), the page told. The input line tags
    /// its lines by its own read of <see cref="PrivateConversation.IsPrivate"/>.
    /// </summary>
    private void EnterPrivate(PrivateReason reason)
    {
        _private.Enter(reason, _effective());
        _privatePasteMark = _input.Pastes.Count;
        _privateClaudeThread.Clear();
        // Claude's own saved threads aren't this conversation's from here: a fresh id, never resumed, for /claude and the server alike.
        _claudeSessionId = null;
        _claudeServerSessionId = null;
        // The advisors' Claude Code ids likewise (2026-11-26, the review's #36): their threads stay, a new id each.
        _advisorThreads.RestoreSessions(null);
        DiagnosticLog.Info(PrivateText.Category, PrivateText.WentPrivateLogLine(reason));
        NudgePrivateChanged();
    }

    /// <summary>The pastes' count when the conversation went private: the end blanks every block from it on (<see cref="PasteBlocks.BlankFrom"/>).</summary>
    private int _privatePasteMark;

    /// <summary>The page's state and the rule follow (the store's version moves neither when nothing was saved).</summary>
    private void NudgePrivateChanged()
    {
        Interlocked.Increment(ref _privateVersion);
    }

    /// <summary>Raised on every switch in or out, for the page's Sessions tag.</summary>
    private int _privateVersion;

    /// <summary><c>/private [new|off]</c>.</summary>
    private async Task HandlePrivateAsync(string args, CancellationToken cancellationToken)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "":
                await GoPrivateAsync(cancellationToken).ConfigureAwait(false);
                break;
            case PrivateText.NewWord:
                await NewPrivateAsync(cancellationToken).ConfigureAwait(false);
                break;
            case PrivateText.OffWord:
                await PrivateOffAsync(cancellationToken).ConfigureAwait(false);
                break;
            default:
                _transcript.Error(PrivateText.Usage);
                break;
        }
    }

    /// <summary>A loop or a bot chat holds the switch back; its words, or null.</summary>
    private string? PrivateRefusal() =>
        InAutomationLoop || _plan.Closing is not null ? PrivateText.LoopRefused
        : _botChatRunning ? PrivateText.BotChatRefused
        : null;

    /// <summary>
    /// <c>/private</c>: the conversation on screen goes private. With a saved row, a yes first (with no row but file checkpoints, its own yes, and those
    /// checkpoints removed); then the reflection and the title job stopped (awaited up to 2 s), the row and its input lines deleted (delete first, flag
    /// second: a failed delete is an error, the conversation stays as it was and a reflection waiting its turn is put back; one that was already running
    /// was stopped), and the conversation carries on.
    /// </summary>
    private async Task GoPrivateAsync(CancellationToken cancellationToken)
    {
        if (_private.IsPrivate)
        {
            _transcript.Notice(PrivateText.AlreadyNotice);
            return;
        }

        if (PrivateRefusal() is { } refused)
        {
            _transcript.Error(refused);
            return;
        }

        long? row = _sessionId;
        var summary = row is { } id ? _sessions.Summary(id) : null;
        // With no row, the turns' file checkpoints are the only trace left (logging off, a failed turn): asked about, and removed on a yes (2026-11-26).
        var stamped = summary is null ? CheckpointTurns.Stamped(_session.History.Messages).Select(s => s.Id).Distinct(StringComparer.Ordinal).ToList() : [];
        if (summary is not null && !await ConfirmAsync(PrivateText.SwitchQuestion(summary.Id, summary.Turns, summary.StartedAt), cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (summary is null && stamped.Count > 0 && !await ConfirmAsync(PrivateText.SwitchCheckpointsQuestion, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // The reflection waiting its turn is put back if the delete fails (2026-11-26, the review's #93).
        var pending = _pendingLearn;
        var paused = Volatile.Read(ref _pausedLearn);
        await StopConversationJobsAsync().ConfigureAwait(false);
        var lines = _private.StoredLines;
        if (row is not null || lines.Count > 0)
        {
            var outcome = await Task.Run(() => _sessions.DeleteForPrivate(row, lines), CancellationToken.None).ConfigureAwait(false);
            if (outcome == PrivateDeleteOutcome.Failed)
            {
                _pendingLearn = pending;
                Interlocked.CompareExchange(ref _pausedLearn, paused, null);
                _transcript.Error(PrivateText.DeleteFailedError);
                return;
            }

            _private.ForgetStoredLines();
            // Their ↑ entries are the private conversation's now: its end takes them out (2026-11-26, the review's #14).
            _input.TagAsPrivate(lines.Select(line => line.Text));
        }

        if (stamped.Count > 0 && _checkpoints is { } store)
        {
            // Queued behind the commits still landing (never the bare Remove, which a late commit would undo).
            Guard(() => { store.RemoveNow(_settings.ProfileDirectory, stamped); return 0; }, 0);
        }

        if (row is not null)
        {
            // The row is gone: the conversation is no longer a stored session. Only the row's own state goes (2026-11-26, the review's #6): the session
            // allows and the MCP pictures' numbers stay, as the screen and the lists do.
            ForgetSessionRow(advisorLinks: true);
        }

        EnterPrivate(PrivateReason.Command);
        if (summary is not null)
        {
            _transcript.Notice(PrivateText.DeletedNotice(summary.Id));
            foreach (string line in PrivateStartLines(fresh: false).Skip(1))
            {
                _transcript.Notice(line);
            }
        }
        else
        {
            SayPrivateStart(fresh: false);
        }
    }

    /// <summary>
    /// The conversation's background jobs that would write it stopped before a switch (2026-11-25): a running or waiting reflection and the title job,
    /// awaited together up to 2 s. One still running after that finds its writes refused (its row is gone, and its number may soon be another's).
    /// </summary>
    private async Task StopConversationJobsAsync()
    {
        _pendingLearn = null;
        ForgetPausedLearn();
        _session.CancelLearning();
        _session.CancelTitling();
        var jobs = new[] { _session.Learning, _session.Titling }.OfType<Task>().Where(t => !t.IsCompleted).ToArray();
        if (jobs.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(jobs), Task.Delay(TimeSpan.FromSeconds(2), _time)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>/private new</c>: the conversation on screen ends as <c>/clear</c> ends it (a saved one stays saved; a private one goes with its end steps), then a
    /// private one starts. A reflection still running from a saved conversation finishes there, its log lines read as <c>[private]</c> under
    /// <c>Private log text</c> <c>hide</c> meanwhile (2026-11-26, the review's #21: the hide is process-wide).
    /// </summary>
    private async Task NewPrivateAsync(CancellationToken cancellationToken)
    {
        if (PrivateRefusal() is { } refused)
        {
            _transcript.Error(refused);
            return;
        }

        var after = await EndPrivateAsync(cancellationToken).ConfigureAwait(false);
        ClearAndRefresh();
        _private.Reset();
        EnterPrivate(StartReason() is PrivateReason.Flag or PrivateReason.Variable ? StartReason() : PrivateReason.Command);
        PrintAfterEnd(after);
        SayPrivateStart(fresh: true);
    }

    /// <summary><c>/private off</c>: the private conversation ends as <c>/clear</c> ends it, and a normal one starts; refused under the flag or variable.</summary>
    private async Task PrivateOffAsync(CancellationToken cancellationToken)
    {
        if (!_private.IsPrivate)
        {
            _transcript.Notice(PrivateText.NotPrivateNotice);
            return;
        }

        if (_private.Forced)
        {
            _transcript.Error(PrivateText.OffRefused(_overriddenBy(SettingsField.PrivateAtStart) ?? SidekickOptions.PrivateFlag));
            return;
        }

        if (PrivateRefusal() is { } refused)
        {
            _transcript.Error(refused);
            return;
        }

        var after = await EndPrivateAsync(cancellationToken).ConfigureAwait(false);
        ClearAndRefresh();
        _private.Reset();
        PrintAfterEnd(after);
        _transcript.Notice(PrivateText.OffNotice);
    }

    /// <summary>
    /// <c>/new</c>, <c>/clear</c> and <c>/splash</c> (the dispatch's): a private conversation's end first; <c>/new</c> then wipes as <c>/clear</c> does while
    /// it was private. Afterwards the flag, variable or setting starts the next one private. Returns whether it was private.
    /// </summary>
    private async Task<bool> EndPrivateForResetAsync(CancellationToken cancellationToken, Action reset, bool wipeWhenPrivate = true)
    {
        bool was = _private.IsPrivate;
        var after = await EndPrivateAsync(cancellationToken).ConfigureAwait(false);
        if (was && wipeWhenPrivate)
        {
            ClearAndRefresh();
        }
        else
        {
            reset();
        }

        _private.Reset();
        PrintAfterEnd(after);
        if (StartPrivateIfSet())
        {
            SayPrivateStart(fresh: true);
        }

        return was;
    }

    /// <summary>What the end has to say once the wipe is done.</summary>
    private sealed record PrivateEndLines(IReadOnlyList<string> Lines);

    private void PrintAfterEnd(PrivateEndLines? after)
    {
        foreach (string line in after?.Lines ?? [])
        {
            _transcript.Notice(line);
        }
    }

    /// <summary>
    /// A private conversation's end (2026-11-25), before the caller's wipe (<see cref="ClearAndRefresh"/>, <see cref="RestoreSession"/>, a profile switch), in
    /// this order (2026-11-26, the review's fixes): the pictures being made cancelled (<c>/imagine</c>'s and a bot chat's, as the strip's double-click
    /// does) and awaited a few seconds, so one saved as the cancel lands is counted; the pictures asked about (under <c>ask</c>, only when the list names a
    /// file still there) and deleted under <c>delete</c> or a Delete, with the spill logs, temp copies and owed files; the file checkpoints made while
    /// private removed (queued behind their commits, the profile folder taken now); the pastes added while private blanked; the Claude thread and a
    /// private bot chat let go; the <c>/log</c> window's lines dropped; the store's owed tidy; the stretch left; the input line's private lines dropped
    /// (after the leave: a line before it was never stored and is tagged, one after it stored and untagged); the log's hide let go. Each step is guarded
    /// on its own and safe to run twice. Null when the conversation wasn't private; otherwise the lines to print after the wipe.
    /// </summary>
    private async Task<PrivateEndLines?> EndPrivateAsync(CancellationToken cancellationToken, bool canAsk = true)
    {
        if (!_private.IsPrivate)
        {
            return null;
        }

        var lines = new List<string>();
        var deletedNow = new System.Runtime.CompilerServices.StrongBox<bool>();
        await StopPicturesAsync(deletedNow).ConfigureAwait(false);
        bool delete = _private.Pictures == PrivatePicturesMode.Delete;
        var (pictures, pdfs) = Guard(() => _private.CountFiles(), (0, 0));
        if (_private.Pictures == PrivatePicturesMode.Ask && pictures + pdfs > 0)
        {
            bool? choice = canAsk ? await AskPrivatePicturesAsync(pictures, pdfs, cancellationToken).ConfigureAwait(false) : null;
            if (choice is null)
            {
                DiagnosticLog.Info(PrivateText.Category, PrivateText.KeptUnaskedLogLine(pictures, pdfs));
            }

            delete = choice == true;
        }

        deletedNow.Value = delete;
        var result = Guard(() => _private.DeleteFiles(delete, IsHeldOpen), new PrivateFilesResult(0, 0, [], []));
        if ((delete && pictures + pdfs > 0) || result.Failed.Count > 0)
        {
            lines.Add(PrivateText.DeletedLine(result.Pictures, result.Pdfs, result.Open, result.Failed));
        }

        if (!delete && _private.Pictures == PrivatePicturesMode.Ask && pictures + pdfs > 0)
        {
            lines.Insert(0, PrivateText.KeptLine(pictures, pdfs));
        }

        string profile = _settings.ProfileDirectory;
        Guard(() => { RemovePrivateCheckpoints(profile); return 0; }, 0);
        Guard(() => _input.Pastes.BlankFrom(_privatePasteMark), 0);
        _privateClaudeThread.Clear();
        // A private bot chat is never carried on after its conversation: --resume would begin a row and save every line.
        if (Volatile.Read(ref _lastBotChat) is { Private.Length: > 0 })
        {
            Volatile.Write(ref _lastBotChat, null);
        }
        Guard(() => { DiagnosticBuffer.Attached?.DropAll(new DiagnosticEvent(DateTime.UtcNow, DiagnosticLevel.Info, PrivateText.Category, PrivateText.LogDroppedLine, null)); return 0; }, 0);
        Guard(() => { _sessions.RetryPrivateTidy(); return 0; }, 0);
        _private.Leave();
        Guard(() => _input.ForgetPrivate(), 0);
        // The log hides on until the ended conversation's jobs have stopped: a reflection still running keeps it on, released as it ends.
        if (!_session.IsLearning)
        {
            _private.ReleaseLog();
        }
        else if (_session.Learning is { } learning)
        {
            _ = learning.ContinueWith(_ => _private.ReleaseLog(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        DiagnosticLog.Info(PrivateText.Category, PrivateText.EndedLogLine);
        NudgePrivateChanged();
        return new PrivateEndLines(lines);
    }

    /// <summary>How long the end waits for the pictures it cancelled.</summary>
    private static readonly TimeSpan PicturesStopWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The end's first step: every picture being made cancelled (<see cref="DrainPictures"/>), the background <c>/imagine</c>s awaited up to
    /// <see cref="PicturesStopWait"/>, then let go. One still running past that is logged; a picture it saves later is owed a delete when the end deleted
    /// (<paramref name="deleted"/>, known once the end has asked), never left as the next conversation's.
    /// </summary>
    private async Task StopPicturesAsync(System.Runtime.CompilerServices.StrongBox<bool> deleted)
    {
        Guard(DrainPictures, false);
        var jobs = _imagining.Select(j => j.Job).ToList();
        _imagining.Clear();
        var running = jobs.Where(j => !j.IsCompleted).ToArray();
        if (running.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(running), Task.Delay(PicturesStopWait, _time)).ConfigureAwait(false);
        }

        foreach (var job in jobs.Where(j => !j.IsCompleted))
        {
            DiagnosticLog.Info(PrivateText.Category, PrivateText.PictureStillRunningLogLine);
            _ = job.ContinueWith(done =>
            {
                if (deleted.Value && done.IsCompletedSuccessfully)
                {
                    foreach (var image in done.Result.Images)
                    {
                        if (image.Path is { Length: > 0 } path)
                        {
                            _private.Owe(path);
                        }
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>The file checkpoints made while private removed with their saved files (<see cref="Files.ICheckpointStore.RemoveNow"/>), in <paramref name="profile"/>.</summary>
    private void RemovePrivateCheckpoints(string profile)
    {
        var ids = _private.TakeCheckpoints();
        if (ids.Count > 0 && _checkpoints is { } store)
        {
            store.RemoveNow(profile, ids);
        }
    }

    /// <summary>
    /// A restore (<c>/sessions</c>, its pane, the mid-turn hand-over, the page's resume): outside a private conversation <see cref="RestoreSession"/> as ever.
    /// While private, the row is loaded and parsed first (a session gone or unreadable is its error, and the private conversation stays); then under the
    /// flag or variable the private conversation ends (its end steps, its pictures question) and the session opens as a private copy, a new stretch — no
    /// row, no title, no Claude or advisor ids — and under <c>/private</c> or the setting a yes ends it and the session resumes as usual.
    /// </summary>
    private async Task<bool> RestoreFlowAsync(long id, CancellationToken cancellationToken)
    {
        if (!_private.IsPrivate)
        {
            // A new conversation's lists start empty (2026-11-26, the review's #2), only once it is on screen: a false leaves the one there whole.
            bool replaced = RestoreSession(id);
            if (replaced)
            {
                _private.Reset();
            }

            return replaced;
        }

        if (_sessions.Load(id) is not { } record)
        {
            _transcript.Error(SessionMissingError(id));
            return false;
        }

        try
        {
            SessionHistory.FromJsonWhole(record.HistoryJson, out _);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            _transcript.Error(SessionRestoreFailedError(id, ex.Message));
            return false;
        }

        var reason = _private.Reason;
        bool copy = _private.Forced;
        if (!copy && !await ConfirmAsync(PrivateText.RestoreQuestion(id), cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var after = await EndPrivateAsync(cancellationToken).ConfigureAwait(false);
        _private.Reset();
        if (copy)
        {
            EnterPrivate(reason);
        }

        // The record already read and parsed is the one opened, so nothing can fail past the end; should it still, the ended conversation is wiped.
        bool restored = RestoreSession(id, record, privateCopy: copy);
        if (!restored)
        {
            ClearAndRefresh();
        }

        PrintAfterEnd(after);
        return restored;
    }

    /// <summary>
    /// A file the images folder's writers created (any thread): listed for the conversation (under every <c>Private pictures</c> value and outside private
    /// too, as <c>delete</c> covers pictures saved before a switch); a splash picture never.
    /// </summary>
    private void TrackCreatedPicture(string full)
    {
        string splash = _settings.ProfileSplashDirectory;
        if (Files.MadeFiles.IsUnder(full, splash))
        {
            return;
        }

        _private.TrackPicture(full);
    }

    /// <summary>
    /// A new file the picture menu's edit wrote from <paramref name="source"/> (any thread): one of the conversation's pictures makes it one too (2026-11-26,
    /// the review's #24), so a private end deletes the edit with the original.
    /// </summary>
    internal void PictureCreated(string source, string written)
    {
        if (_private.Tracks(source))
        {
            _private.TrackPicture(written);
        }
    }

    /// <summary>
    /// The pictures question at the app's exit (2026-11-25): only at <c>/exit</c> and Ctrl+C twice (end of input and the app token keep them), under
    /// <c>ask</c>, with a listed file still there. Null when not asked (kept).
    /// </summary>
    private async Task<bool?> AskPrivateAtExitAsync()
    {
        if (!_private.IsPrivate || _private.Pictures != PrivatePicturesMode.Ask || _exitReason is not (ExitByCommand or ExitByInterrupt))
        {
            return null;
        }

        var (pictures, pdfs) = Guard(() => _private.CountFiles(), (0, 0));
        if (pictures + pdfs == 0)
        {
            return null;
        }

        // Awaited inside the guard (2026-11-26, the review's #103 part): a pane that throws keeps the pictures, logged, never ends the exit.
        try
        {
            return await AskPrivatePicturesAsync(pictures, pdfs, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn(PrivateText.Category, PrivateText.EndStepFailedLogLine(ex.GetType().Name), ex);
            return null;
        }
    }

    /// <summary>
    /// The private conversation's end at the app's exit: the deletes (pictures under <c>delete</c> or a Delete, the owed files even when not private), the
    /// checkpoints made while private, the store's owed tidy, the stretch left with the crash entry's hide latched. The line naming a file left, or null;
    /// printed after the screen is gone.
    /// </summary>
    private string? EndPrivateAtExit(bool? choice)
    {
        if (!_private.IsPrivate)
        {
            var owed = Guard(() => _private.DeleteOwed(), []);
            return PrivateText.LeftAtExitLine([], owed);
        }

        var (pictures, pdfs) = Guard(() => _private.CountFiles(), (0, 0));
        if (_private.Pictures == PrivatePicturesMode.Ask && choice is null && pictures + pdfs > 0)
        {
            DiagnosticLog.Info(PrivateText.Category, PrivateText.KeptUnaskedLogLine(pictures, pdfs));
        }

        bool delete = _private.Pictures == PrivatePicturesMode.Delete || choice == true;
        var result = Guard(() => _private.DeleteFiles(delete, IsHeldOpen), new PrivateFilesResult(0, 0, [], []));
        string profile = _settings.ProfileDirectory;
        Guard(() => { RemovePrivateCheckpoints(profile); return 0; }, 0);
        Guard(() => { _sessions.RetryPrivateTidy(); return 0; }, 0);
        if (PrivateMode.HideLog)
        {
            PrivateMode.HidAtExit = true;   // a crash after this still keeps no message (2026-11-26, the review's #34)
        }

        _private.Leave();
        _private.ReleaseLog();
        return PrivateText.LeftAtExitLine(result.Open, result.Failed);
    }

    /// <summary>One end step guarded on its own: a throw is logged and the rest go on.</summary>
    private static T Guard<T>(Func<T> step, T fallback)
    {
        try
        {
            return step();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn(PrivateText.Category, PrivateText.EndStepFailedLogLine(ex.GetType().Name), ex);
            return fallback;
        }
    }

    /// <summary>
    /// Whether the viewer holds <paramref name="path"/> open now (the one a delete would fail on). A file or folder gone is not held (2026-11-26, the
    /// review's #66: both are IOExceptions).
    /// </summary>
    private bool IsHeldOpen(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>A draft file the editor left deleted; one that won't go is a temp copy of the conversation's, which a private end deletes (2026-11-26).</summary>
    private void DropDraft(string? path)
    {
        if (path is not null && File.Exists(path) && !DraftFile.TryDelete(path))
        {
            _private.TrackTemp(path);
        }
    }

    /// <summary>
    /// The pictures question (the user's ask: <c>ask</c> is the default): Keep or Delete, the cursor on Keep, Esc keeps; <c>k</c> and <c>d</c> move the cursor.
    /// When the page started the end, on the page too, whether or not the pane is on (2026-11-26, the review's #95; the page alone without one). True for
    /// Delete, false for Keep, null when nothing could answer.
    /// </summary>
    private async Task<bool?> AskPrivatePicturesAsync(int pictures, int pdfs, CancellationToken cancellationToken)
    {
        string question = PrivateText.PicturesQuestion(pictures, pdfs);
        string caption = PrivateText.PicturesCaption(pictures + pdfs);
        if (FromWebNow)
        {
            var spec = new WebUi.WebAskSpec("confirm", question, [new WebUi.WebChoice("keep", PrivateText.KeepRow), new WebUi.WebChoice("delete", PrivateText.DeleteRow)], Body: caption);
            var outcome = await RaceAsync<int?>(spec, token => _menu.ChooseAsync(PicturesPage(question, caption), token), a => a.Choice == "delete" ? 1 : 0, "", cancellationToken, noticeAnswer: false).ConfigureAwait(false);
            return outcome.NeverAsked ? null : outcome.Value == 1;
        }

        if (!_menu.CanShowMenus())
        {
            return null;
        }

        try
        {
            int? picked = await _menu.ChooseAsync(PicturesPage(question, caption), cancellationToken).ConfigureAwait(false);
            return picked == 1;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static MenuPage PicturesPage(string question, string caption) =>
        new MenuPage(question, [PrivateText.KeepRow, PrivateText.DeleteRow], PrivateText.PicturesHint)
        {
            Caption = caption,
            Hotkeys = new Dictionary<char, int> { ['k'] = 0, ['d'] = 1 },
        };

}
