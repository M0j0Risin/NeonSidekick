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
    /// hold file checkpoints, worked out off the flow (review P1-12), which makes it the files choice: its cursor on Cancel, or on Conversation
    /// and files with <paramref name="filesFirst"/> (review P7-01: Enter on the default dropped every checkpoint).
    /// Returns the turn confirmed with its files answer, or null when the list was closed.
    /// </summary>
    public async Task<RewindChoice?> PickAsync(IReadOnlyList<RewindTurn> turns, int cursor, Func<RewindTurn, RewindCut> preview, CancellationToken cancellationToken, Func<RewindTurn, RewindCut, CancellationToken, Task<string?>>? files = null, bool filesFirst = false)
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
                if (files is not null && await files(turn, cut, cancellationToken).ConfigureAwait(false) is { } caption)
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
    /// A three-row question on the pane (2026-10-28, review P6-09: the conflict question and the files choice each copied the switch), the
    /// pane closed after: the row picked, null for Esc. Callers put the cursor on the safe row (review P7-01, P7-02).
    /// </summary>
    public async Task<int?> ThreeWayAsync(MenuPage page, int cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        try
        {
            return await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is { Row: var row } ? row : null;
        }
        finally
        {
            _pane.Close();
        }
    }

    /// <summary>A three-row answer: the first row true, the second false, the third or Esc null (nothing changes).</summary>
    public static bool? ThreeWay(int? row) => row switch
    {
        0 => true,
        1 => false,
        _ => null,
    };

    /// <summary>The files choice's page for <paramref name="turn"/>, <paramref name="caption"/> under its question.</summary>
    public static MenuPage FilesPage(RewindTurn turn, RewindCut cut, string caption) =>
        new(RewindText.ConfirmPrompt(turn, cut), RewindText.FilesRows, RewindText.FilesKeys)
        {
            Hotkeys = RewindText.FilesHotkeys,
            Caption = caption,
            CaptionGrows = true,
        };

    /// <summary>The files choice's cursor: Cancel, or Conversation and files for <c>/rewind files</c> (review P7-01, P4-09).</summary>
    public static int FilesCursor(bool filesFirst) => filesFirst ? 0 : 2;

    private async Task<bool?> ChooseOnPaneAsync(RewindTurn turn, RewindCut cut, string caption, bool filesFirst, CancellationToken cancellationToken) =>
        ThreeWay(await _pane.PickAsync(FilesPage(turn, cut, caption), FilesCursor(filesFirst), cancellationToken).ConfigureAwait(false) is { Row: var row } ? row : null);

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
