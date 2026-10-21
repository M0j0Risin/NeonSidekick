using NeonSidekick.Plans;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// Bare <c>/plan open</c> (2026-10-18, the user's ask: a picker, not a list to read and type a name from), <c>/plan open --chat</c>, and from
/// later the same day bare <c>/plan ralph</c> with nothing paused: the plans under <c>.neon/plans/</c> (<see cref="PlanFiles.List"/>, read once,
/// newest first) on a <see cref="MenuPage"/>, a row each (<see cref="PlanText.ListLine"/>, an unreadable plan dim), the highlighted one's path
/// and requirement in the footer (and, when it has run as a Ralph loop, how its last run stood), typing filters by name and title (Shift for
/// a letter a button has). Enter or a double-click picks in the typed mode (<see cref="PlanPickMode"/>); the buttons pick in theirs: <c>o</c>
/// open, <c>c</c> talk, and — outside plan mode, once <c>/plan ralph</c> exists — <c>l</c> Ralph loop. A single click only moves the
/// highlight; an unreadable plan picked says so on the status line and stays. The cursor starts on the plan being planned, else the newest.
/// <see cref="SavedPromptsMenu"/>'s shape. Bare <c>/plan edit</c> (2026-10-20, <see cref="PlanPickMode.Edit"/>): no buttons, Enter opens the
/// pick in its default app, an unreadable plan too.
/// </summary>
internal sealed class PlansMenu
{
    private readonly IReadOnlyList<PlanListing> _plans;
    private readonly MenuPane _pane;
    private readonly PlanPickMode _mode;
    private readonly bool _ralphButton;
    private readonly string? _current;
    private readonly IReadOnlyDictionary<string, string> _ralph;

    /// <param name="plans">The plans to list, newest first (<see cref="PlanFiles.List"/>).</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    /// <param name="mode">What Enter and a double-click do: the typed form's mode.</param>
    /// <param name="ralphButton">Whether the <c>l</c> Ralph loop button is drawn (outside plan mode).</param>
    /// <param name="current">The plan being planned, where the cursor starts; null for the newest.</param>
    /// <param name="ralph">Each plan's Ralph line by path (<see cref="RalphLog"/>, read once as the pane opens); null or a missing path for none.</param>
    public PlansMenu(IReadOnlyList<PlanListing> plans, MenuPane pane, PlanPickMode mode, bool ralphButton, string? current, IReadOnlyDictionary<string, string>? ralph = null)
    {
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _mode = mode;
        _ralphButton = ralphButton;
        _current = current;
        _ralph = ralph ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>The buttons in the order drawn: open, talk, then Ralph loop when offered.</summary>
    public static IReadOnlyList<MenuButton> ButtonsFor(bool ralphButton) => ralphButton
        ? [new(PlanText.OpenButton, PlanText.OpenKey), new(PlanText.TalkButton, PlanText.TalkKey), new(PlanText.RalphButton, PlanText.RalphKey)]
        : [new(PlanText.OpenButton, PlanText.OpenKey), new(PlanText.TalkButton, PlanText.TalkKey)];

    private const int OpenAt = 0;
    private const int TalkAt = 1;
    private const int RalphAt = 2;

    /// <summary>Shows the plans until Esc (null) or a pick (the pane closed).</summary>
    public async Task<PlanPick?> ShowAsync(CancellationToken cancellationToken)
    {
        string filter = "";
        string? on = _current is not null && _plans.Any(p => p.Path == _current) ? _current : _plans.Count > 0 ? _plans[0].Path : null;
        try
        {
            while (true)
            {
                var shown = _plans.Where(p => MenuFilter.Matches(filter, p.Name, p.Title)).ToList();
                List<string> rows = shown.Count == 0
                    ? [MenuFilter.NoMatchRow(filter)]
                    : shown.Select(p => p.Unreadable ? Theme.DimMarkup(PlanText.ListLine(p).Trim()) : Markup.Escape(PlanText.ListLine(p).Trim())).ToList();
                int at = on is null ? -1 : shown.FindIndex(p => p.Path == on);
                var page = new MenuPage(PlanText.PickerTitle, rows, MenuFilter.Hint(PlanText.PickerKeys(_mode, _ralphButton), filter))
                {
                    Buttons = _mode == PlanPickMode.Edit ? [] : ButtonsFor(_ralphButton),
                    Filter = filter,
                    Caption = MenuFilter.CaptionOrNull(filter, shown.Count, _plans.Count),
                    Footer = (_, row) => row >= 0 && row < shown.Count ? PlanText.PickerFooter(shown[row], _ralph.GetValueOrDefault(shown[row].Path)) : null,
                };
                if (await _pane.PickAsync(page, Math.Max(0, at), cancellationToken).ConfigureAwait(false) is not { } pick)
                {
                    return null;
                }

                if (pick.Filter is { } typed)
                {
                    filter = typed;
                    continue;
                }

                if (pick.Row < 0 || pick.Row >= shown.Count)
                {
                    continue;   // the no-match row
                }

                var plan = shown[pick.Row];
                on = plan.Path;
                var mode = _mode == PlanPickMode.Edit ? _mode : pick.Button switch
                {
                    OpenAt => PlanPickMode.Open,
                    TalkAt => PlanPickMode.Talk,
                    RalphAt when _ralphButton => PlanPickMode.Ralph,
                    _ => _mode,
                };
                // A plan too long to read whole opens in its editor all the same (2026-10-20): that is how it gets shorter.
                if (plan.Unreadable && mode != PlanPickMode.Edit)
                {
                    _pane.Error(PlanText.PickerUnreadableError(plan.Path));
                    continue;
                }

                return new PlanPick(plan, mode);
            }
        }
        finally
        {
            _pane.Close();
        }
    }
}
