using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// The <c>/rewind</c> picker (2026-09-30, the user's ask), in the <see cref="QueueMenu"/> shape. One row per turn the history
/// can go back to, oldest first (<see cref="RewindText.RowMarkup"/>), with the cursor where the caller puts it (the newest by
/// default). Enter opens a yes/no page under the list. Its question says how many messages go (<see cref="RewindText.ConfirmPrompt"/>)
/// and its caption names the tools whose changes stay (<see cref="RewindText.ConfirmCaption"/>). The rows and keys are
/// <see cref="SettingsMenu.ConfirmAsync"/>'s, with the cursor on No. Yes returns the turn. No or ESC goes back to the list with
/// the cursor where it was, and ESC on the list closes it with nothing picked. The rewind itself is the screen's, done after
/// the pane has closed so its redraw and notices land in the transcript.
/// <para>When the dropped turns hold file checkpoints (2026-10-28, the checkpoints plan), the yes/no is a three-row page instead:
/// <see cref="RewindText.FilesRows"/> (conversation and files, conversation only, cancel) with the files coming back as its caption, and the
/// answer comes back as a <see cref="RewindChoice"/>. Esc is always the safe answer: nothing changes.</para>
/// </summary>
internal sealed class RewindMenu
{
    private readonly MenuPane _pane;

    /// <param name="pane">The menu host in the bottom pane.</param>
    public RewindMenu(MenuPane pane)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
    }

    /// <summary>
    /// The list over <paramref name="turns"/> with the cursor on <paramref name="cursor"/>. <paramref name="preview"/> says what a
    /// rewind to the picked turn would take, for the yes/no page; <paramref name="files"/> (2026-10-28) the files caption when the dropped turns
    /// hold file checkpoints, which makes it the files choice, its cursor on Conversation and files with <paramref name="filesFirst"/>.
    /// Returns the turn confirmed with its files answer, or null when the list was closed.
    /// </summary>
    public async Task<RewindChoice?> PickAsync(IReadOnlyList<RewindTurn> turns, int cursor, Func<RewindTurn, RewindCut> preview, CancellationToken cancellationToken, Func<RewindTurn, RewindCut, string?>? files = null, bool filesFirst = false)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(preview);
        if (turns.Count == 0)
        {
            return null;
        }

        cursor = Math.Clamp(cursor, 0, turns.Count - 1);
        var rows = turns.Select(RewindText.RowMarkup).ToList();
        try
        {
            while (true)
            {
                var page = new MenuPage(RewindText.Title, rows, RewindText.Keys) { Caption = RewindText.Caption, Footer = (_, row) => row < turns.Count ? RewindText.Footer(turns[row]) : null };
                if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { Row: var row })
                {
                    return null;
                }

                var turn = turns[row];
                var cut = preview(turn);
                if (files?.Invoke(turn, cut) is { } caption)
                {
                    if (await ChooseOnPaneAsync(turn, cut, caption, filesFirst, cancellationToken).ConfigureAwait(false) is { } withFiles)
                    {
                        return new RewindChoice(turn, withFiles);
                    }
                }
                else if (await ConfirmOnPaneAsync(turn, cut, cancellationToken).ConfigureAwait(false))
                {
                    return new RewindChoice(turn, Files: false);
                }

                cursor = row;
            }
        }
        finally
        {
            _pane.Close();
        }
    }

    /// <summary>
    /// The list alone (later on 2026-10-14, the Web UI's card races it): the message picked, or null on ESC; the pane closed after. The yes/no is
    /// the caller's (<see cref="ConfirmAsync"/>, or the screen's raced one).
    /// </summary>
    public async Task<RewindTurn?> PickTurnAsync(IReadOnlyList<RewindTurn> turns, int cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turns);
        if (turns.Count == 0)
        {
            return null;
        }

        try
        {
            var page = new MenuPage(RewindText.Title, turns.Select(RewindText.RowMarkup).ToList(), RewindText.Keys) { Caption = RewindText.Caption, Footer = (_, row) => row < turns.Count ? RewindText.Footer(turns[row]) : null };
            return await _pane.PickAsync(page, Math.Clamp(cursor, 0, turns.Count - 1), cancellationToken).ConfigureAwait(false) is { Row: var row } && row < turns.Count ? turns[row] : null;
        }
        finally
        {
            _pane.Close();
        }
    }

    /// <summary>
    /// The files choice for <paramref name="turn"/> on the pane, the pane closed after (2026-10-28, the PC side of the Web UI's raced card):
    /// true for Conversation and files, false for Conversation only, null for Cancel or Esc.
    /// </summary>
    public async Task<bool?> ChooseAsync(RewindTurn turn, RewindCut cut, string caption, bool filesFirst, CancellationToken cancellationToken)
    {
        try
        {
            return await ChooseOnPaneAsync(turn, cut, caption, filesFirst, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pane.Close();
        }
    }

    /// <summary>
    /// A conflict question on the pane, the pane closed after (2026-10-28): <see cref="UndoText.ConflictRows"/>, true for Restore over them,
    /// false for Skip them, null for Cancel or Esc.
    /// </summary>
    public async Task<bool?> ConflictAsync(string question, string caption, CancellationToken cancellationToken)
    {
        try
        {
            var page = new MenuPage(question, UndoText.ConflictRows, UndoText.ConflictKeys) { Hotkeys = UndoText.ConflictHotkeys, Caption = caption, CaptionGrows = true };
            return await _pane.PickAsync(page, 1, cancellationToken).ConfigureAwait(false) switch
            {
                { Row: 0 } => true,
                { Row: 1 } => false,
                _ => null,
            };
        }
        finally
        {
            _pane.Close();
        }
    }

    private async Task<bool?> ChooseOnPaneAsync(RewindTurn turn, RewindCut cut, string caption, bool filesFirst, CancellationToken cancellationToken)
    {
        var choose = new MenuPage(RewindText.ConfirmPrompt(turn, cut), RewindText.FilesRows, RewindText.FilesKeys)
        {
            Hotkeys = RewindText.FilesHotkeys,
            Caption = caption,
            CaptionGrows = true,
        };
        return await _pane.PickAsync(choose, filesFirst ? 0 : 1, cancellationToken).ConfigureAwait(false) switch
        {
            { Row: 0 } => true,
            { Row: 1 } => false,
            _ => null,
        };
    }

    private async Task<bool> ConfirmOnPaneAsync(RewindTurn turn, RewindCut cut, CancellationToken cancellationToken)
    {
        var confirm = new MenuPage(RewindText.ConfirmPrompt(turn, cut), SettingsMenu.ConfirmRows, SettingsMenu.ConfirmKeys)
        {
            Hotkeys = SettingsMenu.ConfirmHotkeys,
            Caption = RewindText.ConfirmCaption(cut),
        };
        return await _pane.PickAsync(confirm, 0, cancellationToken).ConfigureAwait(false) is { Row: 1 };
    }
}

/// <summary>The turn a rewind goes back to, and whether the files its dropped turns changed come back too (2026-10-28).</summary>
internal sealed record RewindChoice(RewindTurn Turn, bool Files);
