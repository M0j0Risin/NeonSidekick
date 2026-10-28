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
    /// and files, conversation only, or cancel (<c>/rewind files</c> puts the pane's cursor on the first). Every question comes first (the
    /// choice, then conflicts), then the restore, then the cut; an Esc-stopped restore leaves the conversation as it was.</para>
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
            picked = await RewindFromWebAsync(turns, back, plans, cancellationToken).ConfigureAwait(false);
        }
        else if (_pane.Enabled)
        {
            picked = await RewindPicker.PickAsync(turns, turns.Count - back, turn => ConversationRewind.Preview(_session.History.Messages, turn.Start), cancellationToken,
                (turn, cut) => RewindFilesCaption(turn, cut, plans), parsed.Files).ConfigureAwait(false);
        }
        else
        {
            var turn = turns[^back];
            var cut = ConversationRewind.Preview(_session.History.Messages, turn.Start);
            string? files = RewindFilesCaption(turn, cut, plans);
            string? caption = files is null ? RewindText.ConfirmCaption(cut) : null;
            string question = RewindText.ConfirmPrompt(turn, cut) + (caption is null ? "" : " " + caption);
            picked = await ConfirmAsync(question, cancellationToken).ConfigureAwait(false) ? new RewindChoice(turn, Files: false) : null;
            if (picked is not null && files is not null)
            {
                // The typed second question (no pane): yes puts the files back too.
                picked = picked with { Files = await ConfirmAsync(RewindText.TypedFilesQuestion + "\n" + files, cancellationToken).ConfigureAwait(false) };
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

        var (go, report) = await RewindFilesAsync(picked, plans, cancellationToken).ConfigureAwait(false);
        if (go)
        {
            RewindTo(picked.Turn, report);
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
    private void RewindTo(RewindTurn turn, RestoreReport? report = null)
    {
        var (cut, picked) = ConversationRewind.Apply(_session.History, turn, _sessionId is null ? null : _sessions, _sessionId);
        DropUndoNote();
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
            // The Web UI's copy starts afresh (2026-10-14); the kept turns' replay below fills it again.
            _webUi.Feed.Clear();
            RedrawScreen();
            DropQueue();
            _pictureStrip.Clear();
            ForgetReading();
            _log.Clear();
            _lastReply = "";
            // The kept turns' pictures back in the strip and, with thumbnails on, the transcript (2026-10-10, the user's ask).
            var pictures = RestoredPictures.Turns(_session.History.Messages);
            var box = ReplayPictures(pictures);
            if (_sessionId is { } id && _sessions.Load(id) is { } record)
            {
                var byRow = PicturesByRow(pictures);
                foreach (var stored in record.Turns)
                {
                    ReplayTurn(stored.UserText, stored.ReplyText, stored.ToolCalls, styled, byRow.GetValueOrDefault(stored.Ordinal), box);
                }
            }
            else
            {
                var replays = ConversationRewind.Replay(_session.History.Messages);
                for (int i = 0; i < replays.Count; i++)
                {
                    var replay = replays[i];
                    if (replay.Summary)
                    {
                        _transcript.Notice(RewindText.CompactedNotice);
                    }
                    else
                    {
                        // Both lists hold one entry per turn start, in order.
                        ReplayTurn(replay.UserText, replay.ReplyText, replay.ToolCalls, styled, i < pictures.Count ? pictures[i] : null, box);
                    }
                }
            }

            _transcript.Notice(RewindText.RewoundNotice(cut.Turns, turn.Text));
            if (report is not null)
            {
                ShowRestoreReport(report);
            }

            var staying = report is null ? cut.ChangingTools : RewindText.StayingAfterFiles(cut.ChangingTools);
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
    private async Task<RewindChoice?> RewindFromWebAsync(IReadOnlyList<RewindTurn> turns, int back, Dictionary<int, RestorePlan?> plans, CancellationToken cancellationToken)
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
        if (RewindPlan(picked, cut, plans) is { } plan)
        {
            string files = RewindFilesCaption(picked, cut, plans)!;
            var outcome = await RaceAsync<string>(RewindFilesSpec(picked, cut, plan),
                async token => await RewindPicker.ChooseAsync(picked, cut, files, filesFirst: false, token).ConfigureAwait(false) switch { true => "files", false => "conversation", _ => "cancel" },
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
    /// One stored turn back on screen, as <see cref="RestoreSession"/> draws it: the user's row, a <c>🛠️ N tool calls</c> line
    /// when the model called any, and the reply through the live slot, under Claude's name for a <c>/claude</c> exchange. The
    /// pair also goes into the <c>/copy</c> log. With <paramref name="box"/> (<c>Show image thumbnails</c> on, 2026-10-10, the
    /// user's ask) the turn's <paramref name="pictures"/> are drawn where a live turn drew them: <c>/imagine</c>'s before the
    /// user's row, the user's own under it, the tools' after the tool line.
    /// </summary>
    private void ReplayTurn(string userText, string replyText, int toolCalls, bool styled, TurnPictures? pictures = null, UI.ThumbnailBox? box = null)
    {
        // Every turn's pictures: drawn with a box, offered to the Web UI's page alone without (later on 2026-10-14).
        var drawn = pictures is { Any: true } turn ? turn : null;
        ReplayThumbnails(drawn?.Imagined, box, sandbox: true);
        _transcript.User(userText);
        // A dropped picture's path is full; a pasted one's has none, as live (its bytes are kept for a double-click).
        ReplayThumbnails(drawn?.Own, box, sandbox: false);
        if (toolCalls > 0)
        {
            _transcript.ToolNote(SessionText.ToolCallsNote(toolCalls));
        }

        ReplayThumbnails(drawn?.Tools, box, sandbox: true);

        if (replyText.Length > 0)
        {
            bool claude = ClaudeText.IsClaudeLine(userText);
            if (claude)
            {
                // A /claude exchange (2026-09-27): its reply under Claude's name, as it was shown.
                _transcript.Speaker(ClaudeText.SpeakerName, ClaudeColor);
            }

            // The profile's avatar (2026-10-10): a session is its profile's, so its replies wear today's face; Claude's keep the dot.
            _transcript.BeginAssistant(styled, claude ? null : UI.TranscriptRenderer.GlyphFor(_effective().ModelAvatar));
            _transcript.AppendDelta(replyText);
            _transcript.EndAssistant();
        }

        _log.Add(userText, replyText);
    }

    /// <summary>The turns' pictures by the store row each turn wrote (<see cref="TurnPictures.Ordinal"/>); a turn never stamped has none to find (a session saved before 2026-09-30).</summary>
    private static Dictionary<int, TurnPictures> PicturesByRow(IReadOnlyList<TurnPictures> turns)
    {
        var byRow = new Dictionary<int, TurnPictures>();
        foreach (var turn in turns)
        {
            if (turn.Ordinal is { } ordinal)
            {
                byRow[ordinal] = turn;
            }
        }

        return byRow;
    }

    /// <summary>A restored turn's pictures as thumbnails at <paramref name="box"/>, as <see cref="ToolThumbnails"/> draws a live one's; without a box, offered to the Web UI's page alone.</summary>
    private void ReplayThumbnails(IReadOnlyList<DataContent>? pictures, UI.ThumbnailBox? box, bool sandbox)
    {
        if (pictures is not { Count: > 0 })
        {
            return;
        }

        if (box is { } size)
        {
            var (tiles, ids, web) = ReadThumbnails(RestoredImages(pictures), size, sandbox);
            _transcript.Images(tiles, ids, web);
        }
        else
        {
            WebOnlyPictures(RestoredImages(pictures), sandbox);
        }
    }

    /// <summary>
    /// A restored conversation's pictures before its turns are replayed (2026-10-10, the user's ask): the ComfyUI ones into the
    /// strip first, as a live turn puts them (a thumbnail box then leaves the strip's rows), and the box the turns' pictures are
    /// drawn at — null with <c>Show image thumbnails</c> off, when the transcript gets none.
    /// </summary>
    private UI.ThumbnailBox? ReplayPictures(IReadOnlyList<TurnPictures> turns)
    {
        var strip = turns.SelectMany(t => t.Strip).ToList();
        if (strip.Count > 0)
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
    private static List<ImageAttachment> RestoredImages(IReadOnlyList<DataContent> pictures)
    {
        var images = new List<ImageAttachment>(pictures.Count);
        foreach (var picture in pictures)
        {
            string name = ConversationHistory.PicturePath(picture) ?? ImageFile.ClipboardName(images.Count + 1);
            if (ImageFile.TryLoad(picture.Data.ToArray(), name, out var image, out _) && image is not null)
            {
                images.Add(image with { Path = name });
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
