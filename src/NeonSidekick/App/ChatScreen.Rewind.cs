using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.Claude;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
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
    /// </summary>
    private async Task RewindAsync(string args, CancellationToken cancellationToken)
    {
        var turns = ConversationRewind.Turns(_session.History.Messages);
        if (turns.Count == 0)
        {
            _transcript.Notice(RewindText.NothingNotice);
            return;
        }

        if (RewindText.ParseCount(args) is not { } back || back > turns.Count)
        {
            _transcript.Error(RewindText.UsageError(turns.Count));
            return;
        }

        RewindTurn? picked;
        if (_pane.Enabled)
        {
            picked = await RewindPicker.PickAsync(turns, turns.Count - back, turn => ConversationRewind.Preview(_session.History.Messages, turn.Start), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var turn = turns[^back];
            var cut = ConversationRewind.Preview(_session.History.Messages, turn.Start);
            string question = RewindText.ConfirmPrompt(turn, cut) + (RewindText.ConfirmCaption(cut) is { } caption ? " " + caption : "");
            picked = await ConfirmAsync(question, cancellationToken).ConfigureAwait(false) ? turn : null;
            if (picked is null)
            {
                _transcript.Notice(KeptNotice);
            }
        }

        if (picked is not null)
        {
            RewindTo(picked);
        }
    }

    /// <summary>
    /// The rewind (2026-09-30). These steps run in order:
    /// <list type="number">
    /// <item>The history is cut before <paramref name="turn"/> and the session row is cut to match (<see cref="ConversationRewind.Apply"/>).</item>
    /// <item>The Claude CLI server's session is dropped. The CLI holds its own copy of the conversation, so the next turn starts
    /// a new session, given the shorter history as its preamble. <c>/claude</c>'s and the advisor's threads are dropped only
    /// when a turn of theirs was cut.</item>
    /// <item>The context figure is zeroed (<see cref="Llm.TokenTally.ForgetContext"/>) and the reflection's traces are forgotten.</item>
    /// <item>The history is saved.</item>
    /// <item>The screen is redrawn and the turns that stay are replayed: from the session's rows when there is a session, as
    /// <c>/sessions</c> restores it, else from the history.</item>
    /// <item>The rewound notice is shown, with the changing tools' warning under it.</item>
    /// <item>The picked message goes back on the input row (<see cref="RewindDraft"/>).</item>
    /// </list>
    /// The session itself, its title and plan mode stay.
    /// </summary>
    private void RewindTo(RewindTurn turn)
    {
        var (cut, picked) = ConversationRewind.Apply(_session.History, turn, _sessionId is null ? null : _sessions, _sessionId);
        _claudeServerSessionId = null;
        if (cut.HadClaude)
        {
            _claudeSessionId = null;
        }

        if (cut.HadAdvisor)
        {
            _advisorThread.SessionId = null;
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
            RedrawScreen();
            DropQueue();
            _pictureStrip.Clear();
            ForgetReading();
            _log.Clear();
            _lastReply = "";
            if (_sessionId is { } id && _sessions.Load(id) is { } record)
            {
                foreach (var stored in record.Turns)
                {
                    ReplayTurn(stored.UserText, stored.ReplyText, stored.ToolCalls, styled);
                }
            }
            else
            {
                foreach (var replay in ConversationRewind.Replay(_session.History.Messages))
                {
                    if (replay.Summary)
                    {
                        _transcript.Notice(RewindText.CompactedNotice);
                    }
                    else
                    {
                        ReplayTurn(replay.UserText, replay.ReplyText, replay.ToolCalls, styled);
                    }
                }
            }

            _transcript.Notice(RewindText.RewoundNotice(cut.Turns, turn.Text));
            if (cut.ChangingTools.Count > 0)
            {
                _transcript.Warning(RewindText.ChangesStayWarning(cut.ChangingTools));
            }
        }

        _input.Chat.Load(RewindDraft(picked), prependToCurrent: true);
    }

    /// <summary>
    /// One stored turn back on screen, as <see cref="RestoreSession"/> draws it: the user's row, a <c>🛠️ N tool calls</c> line
    /// when the model called any, and the reply through the live slot, under Claude's name for a <c>/claude</c> exchange. The
    /// pair also goes into the <c>/copy</c> log.
    /// </summary>
    private void ReplayTurn(string userText, string replyText, int toolCalls, bool styled)
    {
        _transcript.User(userText);
        if (toolCalls > 0)
        {
            _transcript.ToolNote(SessionText.ToolCallsNote(toolCalls));
        }

        if (replyText.Length > 0)
        {
            if (ClaudeText.IsClaudeLine(userText))
            {
                // A /claude exchange (2026-09-27): its reply under Claude's name, as it was shown.
                _transcript.Speaker(ClaudeText.SpeakerName, ClaudeColor);
            }

            _transcript.BeginAssistant(styled);
            _transcript.AppendDelta(replyText);
            _transcript.EndAssistant();
        }

        _log.Add(userText, replyText);
    }

    /// <summary>
    /// The picked message as a draft for the input row. When the recall list holds the line as it was typed, that entry is used,
    /// in token form, so a pasted block or a picture comes back as its token (the ESC-withdraw path's shape). Otherwise the
    /// draft is rebuilt from the message: its text (<see cref="ConversationRewind.UserLine"/>) with each picture put back as a
    /// new token, in the place of its <c>[Image #n]</c> label, or at the end when the label is gone.
    /// </summary>
    private string RewindDraft(ChatMessage picked)
    {
        string line = ConversationRewind.UserLine(picked);
        var recall = _input.History;
        for (int i = recall.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_input.Pastes.Expand(recall[i]).TrimEnd(), line, StringComparison.Ordinal))
            {
                return recall[i];
            }
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
