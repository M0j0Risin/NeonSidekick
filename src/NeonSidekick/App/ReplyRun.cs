namespace NeonSidekick.App;

/// <summary>
/// Whether a reply runs under a pane, asked live (2026-10-12, the user's ask: a pane opened under a reply kept refusing its rows after the
/// reply had ended, until it was closed and opened again). <see cref="Ended"/> fires when the reply ends with the pane still open
/// (<c>ChatScreen.EndWatchAsync</c>); <see cref="Running"/> reads it at each pick, so the rows the reply held back
/// (<see cref="SettingsMenu.RefusedMidTurn"/>) open in the same pane. While it runs, <see cref="Stale"/> is the pick's stale signal
/// (<see cref="UI.MenuPane.PickAsync"/>): the pane shows again at the reply's end, saying so (<see cref="SettingsMenu.ReplyEndedNotice"/>).
/// <see cref="Idle"/> (the default) is no reply; <see cref="Held"/> one that never ends while the pane is open (a bare <c>midTurn: true</c>).
/// </summary>
public readonly record struct ReplyRun(CancellationToken Ended)
{
    private static readonly CancellationTokenSource s_never = new();

    /// <summary>No reply under the pane: the idle line's.</summary>
    public static ReplyRun Idle => default;

    /// <summary>A reply that runs for as long as the pane is open (the menus' <c>midTurn: true</c>).</summary>
    public static ReplyRun Held => new(s_never.Token);

    /// <summary><see cref="Held"/> or <see cref="Idle"/>, for the menus' <c>bool midTurn</c> overloads.</summary>
    public static ReplyRun Of(bool midTurn) => midTurn ? Held : Idle;

    /// <summary>Whether the reply still runs, read now.</summary>
    public bool Running => Ended.CanBeCanceled && !Ended.IsCancellationRequested;

    /// <summary>The pick's stale signal while the reply runs; none once it ended (a fired token would end every pick at once).</summary>
    public CancellationToken Stale => Running ? Ended : default;
}
