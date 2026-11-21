using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.Claude;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Sessions;

namespace NeonSidekick.App;

// ── /rewind (2026-09-30) ──────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>How long after a first ESC on an empty line the second one opens the rewind picker (the hint shows meanwhile), as <see cref="ExitConfirmWindow"/>.</summary>
    public static readonly TimeSpan RewindConfirmWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The rewind's arm (2026-09-30, the user's pick: Esc Esc, as Claude Code opens its rewind): the UTC tick until which a second
    /// ESC on an empty idle line opens <c>/rewind</c>, 0 = none. Written on the loop's task, read by <see cref="HintText"/> on the
    /// pane's timer thread, as <see cref="_exitArmedUntil"/> is. Typing does not disarm it, as with the double Ctrl+C; anything
    /// the loop reads other than an ESC does, and so does a turn's start.
    /// </summary>
    private long _rewindArmedUntil;

    /// <summary>The picker in the bottom pane.</summary>
    private RewindMenu? _rewindMenuInstance;

    private RewindMenu RewindPicker => _rewindMenuInstance ??= new RewindMenu(_menuPane);

    /// <summary>
    /// The store ordinal a turn just logged got (<see cref="SessionStore.AppendTurn"/>), stamped on its user message: the
    /// history's last turn start, which the turn committed before its first request. A rewind cuts the session's rows by it
    /// (<see cref="ConversationRewind.KeepThrough"/>). Nothing without an ordinal, or when the message carries one already.
    /// </summary>
    internal static void StampTurn(Llm.ConversationHistory history, int? ordinal)
    {
        if (ordinal is { } stamp && history.LastTurnStart() is { } start && Llm.ConversationHistory.TurnOrdinal(start) is null)
        {
            Llm.ConversationHistory.SetTurnOrdinal(start, stamp);
        }
    }

    /// <summary>A first ESC is still fresh: the next one opens the picker.</summary>
    private bool RewindArmed() => _time.GetUtcNow().UtcTicks < Volatile.Read(ref _rewindArmedUntil);

    /// <summary>Forgets a first ESC: the next one is a first again.</summary>
    private void DisarmRewind() => Volatile.Write(ref _rewindArmedUntil, 0);

    /// <summary>
    /// An ESC on an empty idle line that silenced nothing. The first arms the rewind and shows <see cref="RewindText.ArmedHint"/>,
    /// but only while there is a turn to go back to and the bottom pane can show the picker. The second, inside
    /// <see cref="RewindConfirmWindow"/>, is <c>/rewind</c> through the dispatch, as a typed line would be. True when the shell should exit.
    /// </summary>
    private async Task<bool> EscapeOnEmptyLineAsync(CancellationToken cancellationToken)
    {
        long now = _time.GetUtcNow().UtcTicks;
        if (now < Volatile.Read(ref _rewindArmedUntil))
        {
            DisarmRewind();
            _pane.RefreshHint();
            return await HandleAsync(RewindText.Word, [], cancellationToken).ConfigureAwait(false);
        }

        if (!_pane.Enabled || ConversationRewind.Turns(_session.History.Messages).Count == 0)
        {
            return false;
        }

        Volatile.Write(ref _rewindArmedUntil, now + RewindConfirmWindow.Ticks);
        _pane.RefreshHint();
        return false;
    }

    /// <summary>
    /// <c>/rewind [n]</c> (2026-09-30, the user's ask). With the bottom pane, it shows the picker with the cursor on the
    /// <paramref name="args"/>-th message from the end (the last by default), then a yes/no. Without the pane it asks the yes/no
    /// for that message (<see cref="ConfirmAsync"/>: the menus' page or a typed answer). A yes rewinds (<see cref="RewindTo"/>).
    /// <para>When the dropped turns hold file checkpoints (2026-10-28, the checkpoints plan), the yes/no is the files choice: conversation
    /// and files, conversation only, or cancel, opening on Cancel (<c>/rewind files</c> puts the cursor on the first, on the page too; review
    /// P7-01, P4-09). Every question comes first (the choice, then conflicts), then the restore, then the cut; a restore that didn't put
    /// everything back leaves the conversation and its checkpoints as they were (<see cref="RewindFilesAsync"/>).</para>
    /// </summary>
    private async Task RewindAsync(string args, CancellationToken cancellationToken)
    {
        var turns = ConversationRewind.Turns(_session.History.Messages);
        if (turns.Count == 0)
        {
            _transcript.Notice(RewindText.NothingNotice);
            return;
        }

        if (RewindText.ParseArgs(args) is not { } parsed || parsed.Count > turns.Count)
        {
            _transcript.Error(RewindText.UsageError(turns.Count));
            return;
        }

        int back = parsed.Count;
        if (_checkpoints is { } store)
        {
            // The turn just ended may still be committing its checkpoint.
            await store.FlushAsync().ConfigureAwait(false);
        }

        var plans = new Dictionary<int, RestorePlan?>();
        RewindChoice? picked;
        if (FromWebNow)
        {
            picked = await RewindFromWebAsync(turns, back, plans, parsed.Files, cancellationToken).ConfigureAwait(false);
        }
        else if (_pane.Enabled)
        {
            picked = await RewindPicker.PickAsync(turns, turns.Count - back, turn => ConversationRewind.Preview(_session.History.Messages, turn.Start), cancellationToken,
                (turn, cut, token) => RewindFilesCaptionAsync(turn, cut, plans, token), parsed.Files).ConfigureAwait(false);
        }
        else
        {
            var turn = turns[^back];
            var cut = ConversationRewind.Preview(_session.History.Messages, turn.Start);
            if (await RewindFilesCaptionAsync(turn, cut, plans, cancellationToken).ConfigureAwait(false) is { } files)
            {
                // One three-way question (2026-10-28, review P7-03: a blank second answer rewound and dropped every checkpoint).
                bool? withFiles = RewindMenu.ThreeWay(await ChooseThreeAsync(RewindMenu.FilesPage(turn, cut, files), RewindMenu.FilesCursor(parsed.Files), RewindText.TypedFilesChoice, cancellationToken).ConfigureAwait(false));
                picked = withFiles is { } choice ? new RewindChoice(turn, choice) : null;
            }
            else
            {
                string question = RewindText.ConfirmPrompt(turn, cut) + (RewindText.ConfirmCaption(cut) is { } caption ? " " + caption : "");
                picked = await ConfirmAsync(question, cancellationToken).ConfigureAwait(false) ? new RewindChoice(turn, Files: false) : null;
            }

            if (picked is null)
            {
                _transcript.Notice(KeptNotice);
            }
        }

        if (picked is null)
        {
            return;
        }

        var (go, report, plan) = await RewindFilesAsync(picked, plans, cancellationToken).ConfigureAwait(false);
        if (go)
        {
            RewindTo(picked.Turn, report, plan);
        }
    }

    /// <summary>
    /// The rewind (2026-09-30). These steps run in order:
    /// <list type="number">
    /// <item>The history is cut before <paramref name="turn"/> and the session row is cut to match (<see cref="ConversationRewind.Apply"/>).</item>
    /// <item>The Claude Code server's session is dropped. The CLI holds its own copy of the conversation, so the next turn starts
    /// a new session, given the shorter history as its preamble. <c>/claude</c>'s thread and the profile advisors' are dropped only
    /// when a turn of theirs was cut.</item>
    /// <item>The context figure is zeroed (<see cref="Llm.TokenTally.ForgetContext"/>) and the reflection's traces are forgotten.</item>
    /// <item>The history is saved.</item>
    /// <item>The screen is redrawn and the turns that stay are replayed: from the session's rows when there is a session, as
    /// <c>/sessions</c> restores it, else from the history.</item>
    /// <item>The rewound notice is shown, then the files' <paramref name="report"/> when they came back (2026-10-28, held until after the
    /// redraw, which would wipe it), with the changing tools' warning under it: after files only the tools no file checkpoint covers.</item>
    /// <item>The picked message goes back on the input row (<see cref="RewindDraft"/>).</item>
    /// </list>
    /// The session itself, its title and plan mode stay.
    /// </summary>
    private void RewindTo(RewindTurn turn, RestoreReport? report = null, RestorePlan? plan = null)
    {
        // The made files of the turns cut go (2026-11-16, #41): by their messages, so a /compact's shifted indexes don't matter.
        var before = new HashSet<ChatMessage>(_session.History.Messages, ReferenceEqualityComparer.Instance);
        var (cut, picked) = ConversationRewind.Apply(_session.History, turn, _sessionId is null ? null : _sessions, _sessionId);
        _madeFiles.Rewound(before, _session.History.Messages);
        // Only the notes of turns the cut took go (2026-10-28, review P6-04): an /undo of a turn that stays still tells the model.
        _undoNotes.Prune(_session.History.Messages);
        _claudeServerSessionId = null;
        if (cut.HadClaude)
        {
            _claudeSessionId = null;
        }

        if (cut.HadAdvisors)
        {
            // A cut neon_advisor call takes the advisors' threads with it (2026-10-10): they would remember a question the chat no longer holds.
            _advisorThreads.Clear();
        }

        _session.Usage.ForgetContext();
        _lastTrace = null;
        _learnTrace = null;
        DiagnosticLog.Info(AppCategory, RewindText.RewoundLogLine(cut.Turns, turn.Number));
        SaveClaudeHistory();
        RefreshSessionTitle();

        bool styled = StyledReply(_effective().TranscriptMarkdown, _pane.Enabled);
        using (_pane.Batch())
        {
            RepaintConversation(styled, refillStrip: true, afterRedraw: () =>
            {
                DropQueue();
                _pictureStrip.Clear();
                ForgetReading();
                _log.Clear();
                _lastReply = "";
            });
            _transcript.Notice(RewindText.RewoundNotice(cut.Turns, turn.Text));
            if (report is not null)
            {
                ShowRestoreReport(report);
            }

            var staying = report is null || plan is null ? cut.ChangingTools : RewindText.StayingAfterFiles(cut.ChangingTools, CheckpointTurns.AllBack(plan, report));
            if (staying.Count > 0)
            {
                _transcript.Warning(RewindText.ChangesStayWarning(staying));
            }
        }

        // From the Web UI's page (later on 2026-10-14) the text goes to the page's draft: its pictures, PDFs and pasted blocks stay the PC's.
        ToDraft(UI.PasteBlocks.WithoutSavedPaths(ConversationRewind.UserLine(picked)), () => _input.Chat.Load(RewindDraft(picked), prependToCurrent: true));
        if (FromWebNow && (ConversationRewind.Images(picked).Count > 0 || ConversationRewind.DocumentPaths(picked).Count > 0))
        {
            _transcript.Notice(WebUi.WebUiText.RewindAttachmentsNotice);
        }
    }

    /// <summary>
    /// <c>/rewind [n]</c> from the Web UI's page (later on 2026-10-14): the messages as a pick card on the page and the list on the PC's pane at once,
    /// the cursor on the n-th from the end; then the yes/no on both. The message picked when the yes came, else null (kept said). When the
    /// dropped turns hold file checkpoints (2026-10-28) the yes/no is the files choice, a card of three buttons with the files as its lines.
    /// </summary>
    private async Task<RewindChoice?> RewindFromWebAsync(IReadOnlyList<RewindTurn> turns, int back, Dictionary<int, RestorePlan?> plans, bool filesFirst, CancellationToken cancellationToken)
    {
        var options = turns.Select((t, i) => new PickOption(i.ToString(System.Globalization.CultureInfo.InvariantCulture), RewindText.WebLabel(t), RewindText.WebNote(t))).ToList();
        string current = (turns.Count - back).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string? chosen = await PickBothAsync(RewindText.Title, options, current,
            async token => await RewindPicker.PickTurnAsync(turns, turns.Count - back, token).ConfigureAwait(false) is { } turn ? turns.ToList().IndexOf(turn).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            cancellationToken).ConfigureAwait(false);
        if (chosen is null || !int.TryParse(chosen, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) || index >= turns.Count)
        {
            _transcript.Notice(KeptNotice);
            return null;
        }

        var picked = turns[index];
        var cut = ConversationRewind.Preview(_session.History.Messages, picked.Start);
        if (await RewindPlanAsync(picked, cut, plans, cancellationToken).ConfigureAwait(false) is { } plan)
        {
            string files = (await RewindFilesCaptionAsync(picked, cut, plans, cancellationToken).ConfigureAwait(false))!;
            var outcome = await RaceAsync<string>(RewindFilesSpec(picked, cut, plan),
                async token => await RewindPicker.ChooseAsync(picked, cut, files, filesFirst, token).ConfigureAwait(false) switch { true => "files", false => "conversation", _ => "cancel" },
                a => a.Choice ?? "cancel", "The rewind pane failed: ", cancellationToken).ConfigureAwait(false);
            switch (outcome.Value)
            {
                case "files":
                    return new RewindChoice(picked, Files: true);
                case "conversation":
                    return new RewindChoice(picked, Files: false);
            }

            _transcript.Notice(KeptNotice);
            return null;
        }

        string question = RewindText.ConfirmPrompt(picked, cut) + (RewindText.ConfirmCaption(cut) is { } caption ? " " + caption : "");
        if (await ConfirmFromWebAsync(question, cancellationToken).ConfigureAwait(false) == true)
        {
            return new RewindChoice(picked, Files: false);
        }

        _transcript.Notice(KeptNotice);
        return null;
    }

    /// <summary>
    /// The conversation drawn again from what is kept (2026-11-16, plan sharded-hugging-puppy, lifted out of <see cref="RewindTo"/> for the theme
    /// repaint): the Web UI's copy emptied (the replay fills it again), the screen redrawn (<see cref="RedrawScreen"/>), then
    /// <paramref name="afterRedraw"/> (a rewind's own forgetting), then each turn as a resume draws it — the stored session's turns when there is
    /// a row, else the history's — with its pictures. <paramref name="refillStrip"/> puts the turns' pictures back in the picture strip: a rewind
    /// empties it first; a theme change keeps it, so it passes false, or every picture would be listed twice.
    /// <paramref name="theme"/> (2026-11-17, the admin-themes-render review's #1, #40, #85, #90, #154, the user's call) is a theme change's
    /// repaint: the Web UI's copy is kept as it is (its colors come from CSS, so nothing on the page needs drawing again), the <c>/copy</c> log
    /// is left alone, at most <see cref="ThemeRepaintTurns"/> of the latest turns are drawn under a dim line naming the rest, the history is
    /// drawn instead of a stored row holding fewer turns (turns made while logging was off), and each picture keeps the id it was given.
    /// </summary>
    private void RepaintConversation(bool styled, bool refillStrip, Action? afterRedraw = null, bool theme = false)
    {
        // The page's two inputs held off for a theme's replay (#154): what it shows already is the conversation.
        var feed = _transcript.Feed;
        var committed = _pane.Committed;
        if (theme)
        {
            _transcript.Feed = null;
            _pane.Committed = null;
        }
        else
        {
            // The Web UI's copy starts afresh (2026-10-14); the kept turns' replay below fills it again.
            _webUi.Feed.Clear();
        }

        try
        {
            RedrawScreen();
            afterRedraw?.Invoke();
            ReplayKept(styled, refillStrip, theme);
        }
        finally
        {
            if (theme)
            {
                _transcript.Feed = feed;
                _pane.Committed = committed;
            }
        }
    }

    /// <summary><see cref="RepaintConversation"/>'s turns: the stored row's, else the history's, the latest <see cref="ThemeRepaintTurns"/> for a theme.</summary>
    private void ReplayKept(bool styled, bool refillStrip, bool theme)
    {
        // The kept turns' pictures back in the strip and, with thumbnails on, the transcript (2026-10-10, the user's ask).
        var pictures = RestoredPictures.Turns(_session.History.Messages);
        var box = ReplayPictures(pictures, refillStrip);
        var record = _sessionId is { } id ? _sessions.Load(id) : null;
        IReadOnlyList<RewindReplay>? replays = null;
        if (theme && record is not null)
        {
            // Fewer stored turns than the history holds (#85: turns made with Session logging off) draws the history. After /compact the
            // history holds fewer, the normal case, and the stored turns are drawn whole.
            replays = ConversationRewind.Replay(_session.History.Messages);
            if (record.Turns.Count < replays.Count(r => !r.Summary))
            {
                record = null;
            }
        }

        if (record is not null)
        {
            int skip = theme ? Math.Max(0, record.Turns.Count - ThemeRepaintTurns) : 0;
            EarlierTurnsLine(skip);
            var byRow = RestoredPictures.ByRow(pictures);
            foreach (var stored in record.Turns.Skip(skip))
            {
                ReplayTurn(stored.UserText, stored.ReplyText, stored.ToolCalls, styled, byRow.GetValueOrDefault(stored.Ordinal), box, stored.SourceList, log: !theme);
            }

            return;
        }

        replays ??= ConversationRewind.Replay(_session.History.Messages);
        int left = theme ? Math.Max(0, replays.Count(r => !r.Summary) - ThemeRepaintTurns) : 0;
        EarlierTurnsLine(left);
        int seen = 0;
        for (int i = 0; i < replays.Count; i++)
        {
            var replay = replays[i];
            if (replay.Summary)
            {
                // A summary sits before the turns: drawn only when none is left out, else the earlier-turns line stands in its place (gap pass 249).
                if (left == 0)
                {
                    _transcript.Notice(RewindText.CompactedNotice);
                }
            }
            else if (seen++ >= left)
            {
                // Both lists hold one entry per turn start, in order; skipping never renumbers them.
                ReplayTurn(replay.UserText, replay.ReplyText, replay.ToolCalls, styled, i < pictures.Count ? pictures[i] : null, box, log: !theme);
            }
        }
    }

    /// <summary>The <c>/copy</c> log's exchanges, for the tests that hold a theme repaint to leaving it alone (2026-11-17, #1).</summary>
    internal int CopyLogCountForTests => _log.Count;

    /// <summary>
    /// A pane's hand-over of <paramref name="changes"/> at a turn's end, for the tests that hold a run's theme repaint to the idle line
    /// (2026-11-17, the admin-themes-render review's #76): <paramref name="run"/> as <see cref="EndTurnAsync"/> passes it.
    /// </summary>
    internal Task HandOverForTestsAsync(SettingsChanges changes, bool run) => ApplySettingsChangesAsync(changes, CancellationToken.None, run);

    /// <summary>Whether an automation loop is under way, for the tests that hold which turns count as a loop's (2026-11-17).</summary>
    internal bool InAutomationLoopForTests => InAutomationLoop;

    /// <summary>How many theme repaints the screen has drawn (2026-11-17).</summary>
    internal int ThemeRepaintsForTests => _themeRepaints;

    /// <summary>The pictures registered for a double-click, for the tests that hold a second theme repaint to adding none (#90).</summary>
    internal int PictureCountForTests
    {
        get
        {
            lock (_pictures)
            {
                return _pictures.Count;
            }
        }
    }

    /// <summary>The theme repaint's dim line above the first turn drawn, when <paramref name="skipped"/> turns were left out (#40).</summary>
    private void EarlierTurnsLine(int skipped)
    {
        if (skipped > 0)
        {
            _transcript.Notice(UI.ThemeText.EarlierTurns(skipped), UI.NoticeTone.Dim);
        }
    }

    /// <summary>
    /// One stored turn back on screen, as <see cref="RestoreSession"/> draws it: the user's row, a <c>🛠️ N tool calls</c> line
    /// when the model called any, and the reply through the live slot, under Claude's name for a <c>/claude</c> exchange. The
    /// pair also goes into the <c>/copy</c> log, unless <paramref name="log"/> is false (a theme repaint, 2026-11-17, #1: the turns are in it
    /// already). With <paramref name="box"/> (<c>Show image thumbnails</c> on, 2026-10-10, the user's ask) the turn's
    /// <paramref name="pictures"/> are drawn where a live turn drew them: <c>/imagine</c>'s before the user's row, the user's own under it,
    /// the tools' after the tool line.
    /// </summary>
    private void ReplayTurn(string userText, string replyText, int toolCalls, bool styled, TurnPictures? pictures = null, UI.ThumbnailBox? box = null, IReadOnlyList<Web.WebSource>? sources = null, bool log = true)
    {
        // The 📚 line behind Web footer (2026-11-21, the user's ask), so a cancelled turn that read pages opens no reply for it either.
        if (!_effective().WebFooter)
        {
            sources = null;
        }

        // Every turn's pictures: drawn with a box, offered to the Web UI's page alone without (later on 2026-10-14).
        var drawn = pictures is { Any: true } turn ? turn : null;
        ReplayThumbnails(drawn?.Imagined, box, sandbox: true, log);
        _transcript.User(userText);
        // A dropped picture's path is full; a pasted one's has none, as live (its bytes are kept for a double-click).
        ReplayThumbnails(drawn?.Own, box, sandbox: false, log);
        if (toolCalls > 0)
        {
            _transcript.ToolNote(SessionText.ToolCallsNote(toolCalls));
        }

        ReplayThumbnails(drawn?.Tools, box, sandbox: true, log);

        // A turn that read pages but said nothing (cancelled, failed) still opens its reply (2026-11-16, the web-tools review's #9): the
        // end writes the no-reply line itself, as the live turn showed it, and the sources under it.
        if (replyText.Length > 0 || Web.WebSources.HasLine(sources))
        {
            bool claude = ClaudeText.IsClaudeLine(userText);
            if (claude)
            {
                // A /claude exchange (2026-09-27): its reply under Claude's name, as it was shown.
                _transcript.Speaker(ClaudeText.SpeakerName, ClaudeColor);
            }

            // The profile's avatar (2026-10-10): a session is its profile's, so its replies wear today's face; Claude's keep the dot.
            _transcript.BeginAssistant(styled, claude ? null : UI.TranscriptRenderer.GlyphFor(_effective().ModelAvatar));
            if (replyText.Length > 0)
            {
                _transcript.AppendDelta(replyText);
            }

            // The turn's stored sources (2026-11-15, the web-tools plan's §2): a resumed session shows where its answers came from.
            _transcript.EndAssistant(sources: sources);
        }

        if (log)
        {
            _log.Add(userText, replyText);
        }
    }

    /// <summary>
    /// A restored turn's pictures as thumbnails at <paramref name="box"/>, as <see cref="ToolThumbnails"/> draws a live one's; without a box,
    /// offered to the Web UI's page alone. A theme repaint (<paramref name="log"/> false, 2026-11-17, #90, #154) gives each picture the id an
    /// earlier repaint gave it (<see cref="ReplayedPictureId"/>), so repaints don't pile copies into the picture list, and offers the page
    /// nothing: its copy is kept.
    /// </summary>
    private void ReplayThumbnails(IReadOnlyList<DataContent>? pictures, UI.ThumbnailBox? box, bool sandbox, bool log = true)
    {
        if (pictures is not { Count: > 0 })
        {
            return;
        }

        if (box is { } size)
        {
            if (log)
            {
                var (tiles, ids, web) = ReadThumbnails(RestoredImages(pictures), size, sandbox);
                _transcript.Images(tiles, ids, web);
            }
            else
            {
                var images = RestoredImages(pictures, out var sources);
                var (tiles, ids, web) = ReadThumbnails(images, size, sandbox, (i, webOnly) => ReplayedPictureId(sources[i], images[i], sandbox, webOnly));
                _transcript.Images(tiles, ids, web);
            }
        }
        else if (log)
        {
            WebOnlyPictures(RestoredImages(pictures), sandbox);
        }
    }

    /// <summary>
    /// The id a theme repaint draws <paramref name="source"/> under (2026-11-17, #90): the one an earlier repaint registered it as, else a new
    /// one, kept against the history's own picture. A live turn's picture was registered from its attachment, with no history picture to key
    /// on, so the first repaint after a turn adds one entry per picture and later ones add none.
    /// </summary>
    private int ReplayedPictureId(DataContent source, ImageAttachment image, bool sandbox, bool webOnly)
    {
        if (_replayedPictureIds.TryGetValue(source, out var known))
        {
            return known.Value;
        }

        int id = RegisterPicture(image, sandbox, webOnly);
        _replayedPictureIds.AddOrUpdate(source, new System.Runtime.CompilerServices.StrongBox<int>(id));
        return id;
    }

    /// <summary>
    /// A restored conversation's pictures before its turns are replayed (2026-10-10, the user's ask): the ComfyUI ones into the
    /// strip first, as a live turn puts them (a thumbnail box then leaves the strip's rows), and the box the turns' pictures are
    /// drawn at — null with <c>Show image thumbnails</c> off, when the transcript gets none.
    /// </summary>
    private UI.ThumbnailBox? ReplayPictures(IReadOnlyList<TurnPictures> turns, bool refillStrip = true)
    {
        var strip = turns.SelectMany(t => t.Strip).ToList();
        if (refillStrip && strip.Count > 0)
        {
            AddToPictureStrip(RestoredImages(strip));
        }

        var effective = _effective();
        return effective.ShowImageThumbnails ? UI.ThumbnailSize.Resolve(effective, WindowBox()) : null;
    }

    /// <summary>
    /// Stored pictures as attachments again (2026-10-10), each under its own path (<see cref="ConversationHistory.PicturePath"/>), so
    /// a double-click and the strip's order find its file as they did live; one with none is named as a pasted one is. A picture whose
    /// bytes no longer read is left out.
    /// </summary>
    private static List<ImageAttachment> RestoredImages(IReadOnlyList<DataContent> pictures) => RestoredImages(pictures, out _);

    /// <summary>As <see cref="RestoredImages(IReadOnlyList{DataContent})"/>, with the history's picture each one came from, one per image returned (2026-11-17, #90).</summary>
    private static List<ImageAttachment> RestoredImages(IReadOnlyList<DataContent> pictures, out List<DataContent> sources)
    {
        var images = new List<ImageAttachment>(pictures.Count);
        sources = new List<DataContent>(pictures.Count);
        foreach (var picture in pictures)
        {
            string name = ConversationHistory.PicturePath(picture) ?? ImageFile.ClipboardName(images.Count + 1);
            if (ImageFile.TryLoad(picture.Data.ToArray(), name, out var image, out _) && image is not null)
            {
                images.Add(image with { Path = name });
                sources.Add(picture);
            }
        }

        return images;
    }

    /// <summary>
    /// The picked message as a draft for the input row. When the recall list holds the line as it was typed, that entry is used,
    /// in token form, so a pasted block or a picture comes back as its token (the ESC-withdraw path's shape). Otherwise the
    /// draft is rebuilt from the message: its text (<see cref="ConversationRewind.UserLine"/>) with each picture put back as a
    /// new token, in the place of its <c>[Image #n]</c> label, or at the end when the label is gone.
    /// </summary>
    private string RewindDraft(ChatMessage picked)
    {
        // A saved paste's label back as typed (2026-10-09): the recall list holds [Image #1], the message [Image #1, saved as …].
        string line = UI.PasteBlocks.WithoutSavedPaths(ConversationRewind.UserLine(picked));
        var recall = _input.History;
        for (int i = recall.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_input.Pastes.Expand(recall[i]).TrimEnd(), line, StringComparison.Ordinal))
            {
                return recall[i];
            }
        }

        // An attached PDF's label back as its token (2026-10-14, the PDF plan's gap 49): its file loaded again (the cache answers its text at
        // once); one gone or unreadable leaves the label as text.
        var documents = ConversationRewind.DocumentPaths(picked);
        if (documents.Count > 0)
        {
            var withDocuments = new StringBuilder(line.Length);
            int at = 0;
            int nextDocument = 0;
            foreach (var (start, length) in ConversationRewind.DocumentLabels(line))
            {
                withDocuments.Append(line, at, start - at);
                at = start + length;
                if (nextDocument < documents.Count && documents[nextDocument++] is { Length: > 0 } path
                    && LoadAttachedPdf(path) is ({ } document, _))
                {
                    withDocuments.Append(_input.Pastes.AddDocument(document));
                }
                else
                {
                    withDocuments.Append(line, start, length);
                }
            }

            line = withDocuments.Append(line, at, line.Length - at).ToString();
        }

        var images = ConversationRewind.Images(picked);
        if (images.Count == 0)
        {
            return line;
        }

        var draft = new StringBuilder(line.Length);
        int next = 0;
        int from = 0;
        foreach (var (start, length) in ConversationRewind.ImageLabels(line))
        {
            draft.Append(line, from, start - from);
            from = start + length;
            if (next < images.Count && AttachImage(images[next++]) is { } token)
            {
                draft.Append(token);
            }
            else
            {
                draft.Append(line, start, length);
            }
        }

        draft.Append(line, from, line.Length - from);
        while (next < images.Count)
        {
            if (AttachImage(images[next++]) is { } token)
            {
                draft.Append(token);
            }
        }

        return draft.ToString();
    }

    /// <summary>A picture of the picked message kept in the input's paste store again: its token, or null for bytes that no longer read as an image.</summary>
    private char? AttachImage(DataContent picture) =>
        ImageFile.TryLoad(picture.Data.ToArray(), ImageFile.ClipboardName(_input.Pastes.ImageCount + 1), out var image, out _) && image is not null
            ? _input.Pastes.AddImage(image)
            : null;
}
