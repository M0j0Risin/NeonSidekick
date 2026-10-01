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
    /// rewind to the picked turn would take, for the yes/no page. Returns the turn confirmed, or null when the list was closed.
    /// </summary>
    public async Task<RewindTurn?> PickAsync(IReadOnlyList<RewindTurn> turns, int cursor, Func<RewindTurn, RewindCut> preview, CancellationToken cancellationToken)
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
                var page = new MenuPage(RewindText.Title, rows, RewindText.Keys) { Caption = RewindText.Caption };
                if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { Row: var row })
                {
                    return null;
                }

                var turn = turns[row];
                var cut = preview(turn);
                var confirm = new MenuPage(RewindText.ConfirmPrompt(turn, cut), SettingsMenu.ConfirmRows, SettingsMenu.ConfirmKeys)
                {
                    Hotkeys = SettingsMenu.ConfirmHotkeys,
                    Caption = RewindText.ConfirmCaption(cut),
                };
                if (await _pane.PickAsync(confirm, 0, cancellationToken).ConfigureAwait(false) is { Row: 1 })
                {
                    return turn;
                }

                cursor = row;
            }
        }
        finally
        {
            _pane.Close();
        }
    }
}
