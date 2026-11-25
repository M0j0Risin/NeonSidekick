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

    /// <summary>The lines a fresh private conversation prints: the notice, and the pictures note under <c>ask</c> or <c>delete</c>.</summary>
    private IReadOnlyList<string> PrivateStartLines(bool fresh)
    {
        var lines = new List<string> { fresh ? PrivateText.NewNotice : PrivateText.EnteredNotice };
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

    /// <summary>The conversation goes private: the stretch, the switches, the input line's tagging, the page told.</summary>
    private void EnterPrivate(PrivateReason reason)
    {
        _private.Enter(reason, _effective());
        _privateClaudeThread.Clear();
        // Claude's own saved threads aren't this conversation's from here: a fresh id, never resumed, for /claude and the server alike.
        _claudeSessionId = null;
        _claudeServerSessionId = null;
        _input.TagPrivate = true;
        DiagnosticLog.Info(PrivateText.Category, "The conversation went private (" + reason.ToString().ToLowerInvariant() + ").");
        NudgePrivateChanged();
    }

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
    /// <c>/private</c>: the conversation on screen goes private. With a saved row, a yes first; then the reflection and the title job stopped (awaited up to
    /// 2 s), the row and its input lines deleted (delete first, flag second: a failed delete is an error and nothing changes), and the conversation carries on.
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
        if (summary is not null && !await ConfirmAsync(PrivateText.SwitchQuestion(summary.Id, summary.Turns, summary.StartedAt), cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await StopConversationJobsAsync().ConfigureAwait(false);
        var lines = _private.StoredLines;
        if (row is not null || lines.Count > 0)
        {
            var outcome = await Task.Run(() => _sessions.DeleteForPrivate(row, lines), CancellationToken.None).ConfigureAwait(false);
            if (outcome == PrivateDeleteOutcome.Failed)
            {
                _transcript.Error(PrivateText.DeleteFailedError);
                return;
            }

            _private.ForgetStoredLines();
        }

        if (row is not null)
        {
            // The row is gone: the conversation is no longer a stored session; its lists, the screen and the allows stay but the shell's.
            ForgetSession(left: false);
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
    /// private one starts.
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
    /// A private conversation's end (2026-11-25), before the caller's wipe (<see cref="ClearAndRefresh"/>, <see cref="RestoreSession"/>, a profile switch): the
    /// pictures asked about (under <c>ask</c>, only when the list names a file still there) and deleted under <c>delete</c> or a Delete; the spill logs and
    /// temp copies deleted; the input line's private lines and the <c>/log</c> window's lines dropped; the pastes added while private blanked; the stretch
    /// left. Each step is guarded on its own and safe to run twice. Null when the conversation wasn't private; otherwise the lines to print after the wipe.
    /// </summary>
    private async Task<PrivateEndLines?> EndPrivateAsync(CancellationToken cancellationToken, bool canAsk = true)
    {
        if (!_private.IsPrivate)
        {
            return null;
        }

        var lines = new List<string>();
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

        var result = Guard(() => _private.DeleteFiles(delete, IsHeldOpen), new PrivateFilesResult(0, 0, [], []));
        if (delete && pictures + pdfs > 0)
        {
            lines.Add(PrivateText.DeletedLine(result.Pictures, result.Pdfs, result.Open));
        }
        else if (_private.Pictures == PrivatePicturesMode.Ask && pictures + pdfs > 0)
        {
            lines.Add(PrivateText.KeptLine(pictures, pdfs));
        }

        Guard(() => _input.ForgetPrivate(), 0);
        _privateClaudeThread.Clear();
        // A private bot chat is never carried on after its conversation: --resume would begin a row and save every line.
        if (Volatile.Read(ref _lastBotChat) is { Private.Length: > 0 })
        {
            Volatile.Write(ref _lastBotChat, null);
        }
        Guard(() => { DiagnosticBuffer.Attached?.DropAll(new DiagnosticEvent(DateTime.UtcNow, DiagnosticLevel.Info, PrivateText.Category, PrivateText.LogDroppedLine, null)); return 0; }, 0);
        Guard(() => { _sessions.RetryPrivateTidy(); return 0; }, 0);
        _input.TagPrivate = false;
        _private.Leave();
        // The log hides on until the ended conversation's jobs have stopped: a reflection still running keeps it on, released as it ends.
        if (!_session.IsLearning)
        {
            _private.ReleaseLog();
        }
        else if (_session.Learning is { } learning)
        {
            _ = learning.ContinueWith(_ => _private.ReleaseLog(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        DiagnosticLog.Info(PrivateText.Category, "The private conversation ended.");
        NudgePrivateChanged();
        return new PrivateEndLines(lines);
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
            return RestoreSession(id);
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

        bool restored = RestoreSession(id, privateCopy: copy);
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

        return await Guard(() => AskPrivatePicturesAsync(pictures, pdfs, CancellationToken.None), Task.FromResult<bool?>(null)).ConfigureAwait(false);
    }

    /// <summary>
    /// The private conversation's end at the app's exit: the deletes (pictures under <c>delete</c> or a Delete), the store's owed tidy, the stretch left.
    /// The line naming a file left, or null; printed after the screen is gone.
    /// </summary>
    private string? EndPrivateAtExit(bool? choice)
    {
        if (!_private.IsPrivate)
        {
            return null;
        }

        var (pictures, pdfs) = Guard(() => _private.CountFiles(), (0, 0));
        if (_private.Pictures == PrivatePicturesMode.Ask && choice is null && pictures + pdfs > 0)
        {
            DiagnosticLog.Info(PrivateText.Category, PrivateText.KeptUnaskedLogLine(pictures, pdfs));
        }

        bool delete = _private.Pictures == PrivatePicturesMode.Delete || choice == true;
        var result = Guard(() => _private.DeleteFiles(delete, IsHeldOpen), new PrivateFilesResult(0, 0, [], []));
        Guard(() => { _sessions.RetryPrivateTidy(); return 0; }, 0);
        _private.Leave();
        _private.ReleaseLog();
        var left = result.Open.Concat(result.Failed).ToList();
        return left.Count == 0 ? null : PrivateText.LeftAtExitLine(left);
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
            DiagnosticLog.Warn(PrivateText.Category, "A step of the private conversation's end failed: " + ex.GetType().Name, ex);
            return fallback;
        }
    }

    /// <summary>Whether the viewer holds <paramref name="path"/> open now (the one a delete would fail on).</summary>
    private bool IsHeldOpen(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The pictures question (the user's ask: <c>ask</c> is the default): Keep or Delete, the cursor on Keep, Esc keeps; <c>k</c> and <c>d</c> move the cursor.
    /// On the pane and the page at once when the page started the end. True for Delete, false for Keep, null when nothing could answer.
    /// </summary>
    private async Task<bool?> AskPrivatePicturesAsync(int pictures, int pdfs, CancellationToken cancellationToken)
    {
        string question = PrivateText.PicturesQuestion(pictures, pdfs);
        string caption = PrivateText.PicturesCaption(pictures + pdfs, OperatingSystem.IsMacOS());
        if (FromWebNow && _pane.Enabled)
        {
            var spec = new WebUi.WebAskSpec("confirm", question, [new WebUi.WebChoice("keep", PrivateText.KeepRow), new WebUi.WebChoice("delete", PrivateText.DeleteRow)], Body: caption);
            var outcome = await AskBothAsync<int?>(spec, token => _menu.ChooseAsync(PicturesPage(question, caption), token), a => a.Choice == "delete" ? 1 : 0, "", cancellationToken, noticeAnswer: false).ConfigureAwait(false);
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
