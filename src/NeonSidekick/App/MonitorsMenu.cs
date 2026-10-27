using NeonSidekick.Monitors;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The monitors pane (2026-10-23, the monitors plan): the 🖥️ chip's right-click, <c>/monitor</c> and <c>/monitor &lt;n&gt;</c>. A tabbed page over
/// the menu pane — a Layout tab (Extend all · Duplicate · Only <i>n</i> · Make <i>n</i> primary, a row that would change nothing left out,
/// the layout in force its caption) then a tab per monitor: its inputs (● current, ↩ the one the app switched it away from, given names
/// beside the standard ones, hidden ones left out unless current, the one picked as this PC's tagged), a rule, Only this monitor · Make primary ·
/// This PC's input… (later on 2026-10-23: the pick list, <c>Monitor PC inputs</c>) · Enter a code… · Input names….
/// A tab's note (off the desktop, duplicated, not reported, no DDC/CI) is its caption; each row's footer says what it does. With one monitor
/// there is no Layout tab; with one monitor and no DDC/CI nothing can be done (<see cref="MonitorText.NothingToDo"/>). It opens at once from
/// the board's snapshot and is built again at the same tab and row as each read lands (<see cref="MenuPane.PickAsync"/>'s <c>stale</c>, a
/// watcher on <see cref="MonitorBoard.Changed"/>), the topology clock leased for its life. A pick that changes something closes the pane, asks
/// first where <see cref="MonitorActions.WarningAsync"/> says to (the last screen showing this PC, a layout around a monitor on another
/// input), then runs on the board's worker in the background, its outcome flashed on the hint row; This PC's input…, Input names… and a
/// cancelled prompt come back to the tab.
/// </summary>
internal sealed class MonitorsMenu
{
    private readonly MonitorBoard _board;
    private readonly MenuPane _pane;
    private readonly InputLine _line;
    private readonly AppSettings _settings;
    private readonly Func<AppSettingsData> _effective;
    private readonly Func<string, CancellationToken, Task<bool>> _confirm;
    private readonly Action<string> _flash;

    public MonitorsMenu(MonitorBoard board, MenuPane pane, InputLine line, AppSettings settings, Func<AppSettingsData> effective,
        Func<string, CancellationToken, Task<bool>> confirm, Action<string> flash)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _line = line ?? throw new ArgumentNullException(nameof(line));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _flash = flash ?? throw new ArgumentNullException(nameof(flash));
    }

    /// <summary>The change a pick started, when the caller wants to wait for it (the tests); null when nothing ran.</summary>
    public Task? Running { get; private set; }

    /// <summary>The input names and this PC's inputs in force (the settings in force, so a profile switch follows at once).</summary>
    public static MonitorWords WordsFor(AppSettingsData effective, MonitorSnapshot snapshot) => MonitorWords.For(effective, snapshot.Monitors);

    // One row as drawn and what Enter on it does.
    private sealed record Row(string Markup, string? Footer, MonitorChange? Change = null, MonitorInfo? Code = null, MonitorInfo? Names = null, MonitorInfo? Pc = null);

    // One tab: its strip title, rows, caption, and whether it is dim (a monitor off the desktop).
    private sealed record Tab(string Title, List<Row> Rows, string? Caption, bool Off, HashSet<int> Headings);

    /// <summary>
    /// Shows the pane until Esc or a pick that changes something, on monitor <paramref name="number"/>'s tab when given (the Layout tab
    /// otherwise, or the only monitor's). The caller closes the pane.
    /// </summary>
    public async Task ShowAsync(int? number, CancellationToken cancellationToken)
    {
        using var lease = _board.Watch();
        await _board.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var reading = _board.ReadInputsAsync(cancellationToken);
        _ = reading.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        string? onTarget = number is { } n ? _board.State.Topology.ByNumber(n)?.Target : null;
        int tabIndex = -1;
        int cursor = 0;
        while (true)
        {
            var state = _board.State;
            var snapshot = state.Topology;
            var words = WordsFor(_effective(), snapshot);
            var tabs = Tabs(state, words);
            if (tabs.All(t => t.Rows.All(r => r.Change is null && r.Code is null && r.Names is null && r.Pc is null)))
            {
                if (snapshot.Monitors.Count > 0 && !reading.IsCompleted)
                {
                    // One monitor whose inputs are still being read: no tab is drawn with nothing on it, so the read is waited for.
                    try
                    {
                        await reading.ConfigureAwait(false);
                    }
                    catch (MonitorException)
                    {
                        // Its sentence is in the state.
                    }

                    continue;
                }

                string line = snapshot.Monitors.Count == 0 ? MonitorText.NoMonitors : MonitorText.NothingToDo;
                await _pane.ShowEmptyAsync(MonitorText.Title, line, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (tabIndex < 0)
            {
                tabIndex = onTarget is null ? 0 : Math.Max(0, tabs.FindIndex(t => t.Title == snapshot.ByTarget(onTarget)?.Label));
            }

            tabIndex = Math.Clamp(tabIndex, 0, tabs.Count - 1);
            var menuTabs = tabs.Select(t => new MenuTab(t.Title, t.Rows.Select(r => r.Markup).ToList())
            {
                Caption = t.Caption,
                Off = t.Off,
                Headings = t.Headings.Count > 0 ? t.Headings : null,
            }).ToList();
            var page = MenuPage.Tabbed(MonitorText.Title, menuTabs, tabIndex, MonitorText.Keys) with
            {
                Footer = (tab, row) => tab >= 0 && tab < tabs.Count && row >= 0 && row < tabs[tab].Rows.Count && tabs[tab].Rows[row].Footer is { } f ? new MenuFooter(f) : null,
            };

            MenuPick? picked;
            using (var stale = new CancellationTokenSource())
            {
                long drawn = state.Version;
                void OnChanged()
                {
                    if (_board.State.Version != drawn)
                    {
                        try
                        {
                            stale.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                            // The pick already ended.
                        }
                    }
                }

                _board.Changed += OnChanged;
                try
                {
                    OnChanged();
                    picked = await _pane.PickAsync(page, cursor, cancellationToken, stale: stale.Token).ConfigureAwait(false);
                }
                finally
                {
                    _board.Changed -= OnChanged;
                }
            }

            if (picked is not { } pick)
            {
                return;
            }

            tabIndex = pick.Tab;
            cursor = pick.Row;
            if (pick.Refresh || pick.Tab < 0 || pick.Tab >= tabs.Count || pick.Row < 0 || pick.Row >= tabs[pick.Tab].Rows.Count)
            {
                continue;
            }

            var row = tabs[pick.Tab].Rows[pick.Row];
            if (row.Names is { } named)
            {
                await NamesAsync(named, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (row.Pc is { } forPc)
            {
                await PcInputAsync(forPc, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var change = row.Change;
            if (row.Code is { } coded)
            {
                change = await CodeAsync(page, pick.Row, coded, state, cancellationToken).ConfigureAwait(false);
                if (change is null)
                {
                    continue;
                }
            }

            if (change is not null)
            {
                _pane.Close();
                await RunAsync(change, words, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    // Asks where the change says to, then runs it in the background, its outcome flashed.
    private async Task RunAsync(MonitorChange change, MonitorWords words, CancellationToken cancellationToken)
    {
        string? warning = await _board.Actions.WarningAsync(change, words, cancellationToken).ConfigureAwait(false);
        if (warning is not null && !await _confirm(warning, cancellationToken).ConfigureAwait(false))
        {
            _flash(MonitorText.Declined);
            return;
        }

        Running = Task.Run(async () =>
        {
            var outcome = await _board.Actions.ApplyAsync(change, words, CancellationToken.None).ConfigureAwait(false);
            _board.NoteAway();
            _flash(outcome.Text);
        }, CancellationToken.None);
    }

    private static List<Tab> Tabs(MonitorState state, MonitorWords words)
    {
        var snapshot = state.Topology;
        var tabs = new List<Tab>();
        if (snapshot.Monitors.Count == 0)
        {
            return tabs;
        }

        if (snapshot.Monitors.Count > 1)
        {
            tabs.Add(LayoutTab(state, words));
        }

        foreach (var m in snapshot.Monitors)
        {
            tabs.Add(MonitorTab(state, words, m));
        }

        return tabs;
    }

    private static Tab LayoutTab(MonitorState state, MonitorWords words)
    {
        var snapshot = state.Topology;
        var rows = new List<Row>();
        void Add(MonitorChange change, string label, string footer, bool current)
        {
            if (MonitorActions.WouldChange(change, snapshot))
            {
                rows.Add(new Row(Markup.Escape(label) + (current ? Theme.DimMarkup("  " + MonitorText.CurrentMark) : ""), footer, change));
            }
        }

        var elsewhere = state.Elsewhere(words);
        var away = elsewhere.FirstOrDefault(m => m.Active);
        Add(MonitorChange.Arrange(MonitorLayout.Extend), MonitorText.ExtendRow, MonitorText.ExtendFooter, snapshot.Layout == MonitorArrangement.Extended);
        Add(MonitorChange.Arrange(MonitorLayout.Duplicate), MonitorText.DuplicateRow, away is null ? MonitorText.DuplicateFooter : MonitorText.DuplicateAwayFooter, snapshot.Layout == MonitorArrangement.Duplicated);
        foreach (var m in snapshot.Monitors)
        {
            Add(MonitorChange.Arrange(MonitorLayout.Only, m), MonitorText.OnlyRow(m.Label), AwayFooter(state, words, m, MonitorText.OnlyFooter), snapshot.Layout == MonitorArrangement.Single && m.Active);
        }

        foreach (var m in snapshot.Monitors.Where(m => m.Active))
        {
            Add(MonitorChange.Arrange(MonitorLayout.Primary, m), MonitorText.PrimaryRow(m.Label), AwayFooter(state, words, m, MonitorText.PrimaryFooter), current: false);
        }

        return new Tab(MonitorText.LayoutTab, rows, MonitorText.LayoutNow(snapshot.Layout, elsewhere.Select(m => m.Label).ToList()), Off: false, []);
    }

    // A layout row's footer: a monitor on another input (any basis, later on 2026-10-23) asks first, and says why.
    private static string AwayFooter(MonitorState state, MonitorWords words, MonitorInfo m, string footer) =>
        state.ShowingOf(m, words) is { IsElsewhere: true } showing ? MonitorText.AwayRowFooter(footer, MonitorText.Why(showing, words, m)) : footer;

    private static Tab MonitorTab(MonitorState state, MonitorWords words, MonitorInfo m)
    {
        var snapshot = state.Topology;
        var inputs = state.InputsOf(m);
        var rows = new List<Row>();
        var headings = new HashSet<int>();
        string? caption = null;
        // Off the desktop and read through NVIDIA's driver (2026-10-27, the NVIDIA plan): its inputs as on the desktop. Its current input must be
        // known: caps kept from the desktop alone would show rows that can't switch. Unread, refused or failed, today's caption.
        bool reached = !m.Active && !m.Shared && inputs is { Problem: null, Reading: false, Current: not null };
        bool layoutOnly = (!m.Active && !reached) || m.Shared || inputs.Problem is not null || inputs.Reading;
        if (!m.Active && !reached)
        {
            caption = inputs.Reading && inputs.Problem is null ? MonitorText.Reading : MonitorText.OffDesktop;
        }
        else if (m.Shared)
        {
            caption = MonitorText.SharedSource;
        }
        else if (inputs.Problem is { } problem)
        {
            caption = problem;
        }
        else if (inputs.Reading || !inputs.CanSwitch)
        {
            caption = MonitorText.Reading;
            layoutOnly = true;
        }
        else
        {
            foreach (var input in words.Rows(m, inputs.Offered, inputs.Current, state.AwayOf(m)))
            {
                string mark = input.Current ? MonitorText.CurrentMark : input.Away ? MonitorText.AwayMark : " ";
                var change = input.Current ? null : state.SwitchTo(m, input.Code, words);
                rows.Add(new Row(Markup.Escape(mark + " " + input.Words) + (input.ThisPc ? Theme.DimMarkup("  " + MonitorText.ThisPcTag) : ""),
                    change is { Return: true } ? MonitorText.PcInputOffDesktopFooter : MonitorText.InputFooter(input.Code, input.Away, input.ThisPc), change));
            }

            caption = reached ? MonitorText.OffDesktopReached + (inputs.Reported ? "" : " " + MonitorText.NotReported)
                : !inputs.Reported ? MonitorText.NotReported : inputs.Stale ? MonitorText.StaleWord : null;
            headings.Add(rows.Count);
            rows.Add(new Row(SectionRule.Markup(MonitorText.ActionsHeading), null));
        }

        var only = MonitorChange.Arrange(MonitorLayout.Only, m);
        if (MonitorActions.WouldChange(only, snapshot))
        {
            rows.Add(new Row(Markup.Escape(MonitorText.OnlyThisRow), AwayFooter(state, words, m, MonitorText.OnlyFooter), only));
        }

        var primary = MonitorChange.Arrange(MonitorLayout.Primary, m);
        if (MonitorActions.WouldChange(primary, snapshot))
        {
            rows.Add(new Row(Markup.Escape(MonitorText.PrimaryThisRow), AwayFooter(state, words, m, MonitorText.PrimaryFooter), primary));
        }

        var extend = MonitorChange.Arrange(MonitorLayout.Extend);
        if ((!m.Active || m.Shared) && MonitorActions.WouldChange(extend, snapshot))
        {
            rows.Add(new Row(Markup.Escape(MonitorText.ExtendRow), MonitorText.ExtendFooter, extend));
        }

        if (!layoutOnly)
        {
            if (words.SavedKey(m) is not null)
            {
                // This PC's input (later on 2026-10-23, the user's ask: a choice, not "This PC" typed as a name).
                string? picked = words.PcInput(m) is { } pc ? words.Describe(m, pc) : null;
                string? guessed = MonitorShowing.CableGuess(m, inputs.Current, inputs.Offered) is { } guess ? words.Describe(m, guess) : null;
                rows.Add(new Row(Markup.Escape(MonitorText.PcRow(picked, guessed)), MonitorText.PcRowFooter, Pc: m));
            }

            rows.Add(new Row(Markup.Escape(MonitorText.EnterCodeRow), MonitorText.EnterCodeFooter, Code: m));
            rows.Add(new Row(Markup.Escape(MonitorText.InputNamesRow), MonitorText.InputNamesFooter, Names: m));
        }

        // A rule with nothing under it goes.
        if (headings.Count > 0 && headings.Max() == rows.Count - 1)
        {
            rows.RemoveAt(rows.Count - 1);
            headings.Clear();
        }

        return new Tab(m.Label, rows, caption, Off: !m.Active, headings);
    }

    // Enter a code…: typed on the input row under the page; the change, or null for Esc or a code refused (said on the status line).
    private async Task<MonitorChange?> CodeAsync(MenuPage page, int row, MonitorInfo m, MonitorState state, CancellationToken cancellationToken)
    {
        string? typed = await ReadTextAsync(page with { Title = MonitorText.EnterCodePrompt(m.Label) }, row, "", cancellationToken).ConfigureAwait(false);
        if (typed is null)
        {
            return null;
        }

        if (!Mccs.TryParseCode(typed, out byte code))
        {
            _pane.Error(MonitorText.NotACode(typed.Trim()));
            return null;
        }

        return state.SwitchTo(m, code, WordsFor(_effective(), state.Topology));
    }

    // Input names…: every input the monitor reports (or the common four), any code named, the current one; Enter renames, h hides, Del clears.
    private async Task NamesAsync(MonitorInfo m, CancellationToken cancellationToken)
    {
        int cursor = 0;
        while (true)
        {
            var state = _board.State;
            var monitor = state.Topology.ByTarget(m.Target) ?? m;
            var effective = _effective();
            var words = WordsFor(effective, state.Topology);
            var inputs = state.InputsOf(monitor);
            var rows = words.Rows(monitor, inputs.Offered, inputs.Current, null, all: true);
            if (rows.Count == 0 || words.SavedKey(monitor) is not { } key)
            {
                return;
            }

            var page = new MenuPage(MonitorText.NamesTitle(monitor.Label), rows.Select(NameRow).ToList(), MonitorText.NamesKeys)
            {
                Buttons = [new MenuButton(MonitorText.HiddenWord, 'h') { Hidden = true }],
                Removes = true,
                Footer = (_, r) => r >= 0 && r < rows.Count ? new MenuFooter(Mccs.Hex(rows[r].Code)) : null,
            };
            if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
            {
                return;
            }

            cursor = Math.Clamp(pick.Row, 0, rows.Count - 1);
            var input = rows[cursor];
            string standard = input.Standard ?? Mccs.Hex(input.Code);
            if (pick.Button >= 0)
            {
                bool hide = !input.Hidden;
                _settings.Update(d => d.MonitorInputsHidden = MonitorWords.Hide(d.MonitorInputsHidden, key, input.Code, hide));
                _pane.Notice(hide ? MonitorText.HiddenNow(standard) : MonitorText.ShownNow(standard));
                continue;
            }

            if (pick.Remove)
            {
                _settings.Update(d => d.MonitorInputNames = MonitorWords.Rename(d.MonitorInputNames, key, input.Code, null));
                _pane.Notice(MonitorText.NameCleared(standard));
                continue;
            }

            string? typed = await ReadTextAsync(page, cursor, input.Given ?? "", cancellationToken).ConfigureAwait(false);
            if (typed is null)
            {
                continue;
            }

            if (MonitorWords.CleanName(typed, input.Code, rows) is not { } clean)
            {
                _pane.Error(MonitorText.NameTaken(typed.Trim()));
                continue;
            }

            _settings.Update(d => d.MonitorInputNames = MonitorWords.Rename(d.MonitorInputNames, key, input.Code, clean));
            _pane.Notice(MonitorText.Renamed(standard, clean));
        }

        static string NameRow(MonitorInputRow r) =>
            Markup.Escape((r.Standard ?? Mccs.Hex(r.Code)).PadRight(14)) + (r.Given is { } g ? "  " + Markup.Escape(g) : "")
            + (r.Hidden ? Theme.DimMarkup("  " + MonitorText.HiddenWord) : "");
    }

    // This PC's input… (later on 2026-10-23): the input it shows now (read afresh on Enter, a failed read or 0x00 saving nothing), every
    // input the names page lists, then no pick; ● on the choice in force, the current input and the cable's guess tagged. Enter saves and
    // comes back to the tab, Esc comes back; nothing switches.
    private async Task PcInputAsync(MonitorInfo m, CancellationToken cancellationToken)
    {
        var state = _board.State;
        var monitor = state.Topology.ByTarget(m.Target) ?? m;
        var words = WordsFor(_effective(), state.Topology);
        if (words.SavedKey(monitor) is not { } key)
        {
            return;
        }

        var inputs = state.InputsOf(monitor);
        byte? current = MonitorShowing.Usable(inputs.Current);
        byte? picked = words.PcInput(monitor);
        byte? guess = MonitorShowing.CableGuess(monitor, inputs.Current, inputs.Offered);
        var inputRows = words.Rows(monitor, inputs.Offered, inputs.Current, null, all: true);
        if (inputRows.Count == 0)
        {
            return;
        }

        // Each row: its markup, its footer, and the code it saves (null: no pick); the "now" row reads afresh.
        var rows = new List<(string Markup, string Footer, byte? Code, bool Now)>();
        // Off the desktop it shows nothing of this PC whatever its input (2026-10-27): no "now" row.
        if (current is { } now && monitor.Active)
        {
            rows.Add((Markup.Escape("  " + MonitorText.PcNowRow(words.Describe(monitor, now))), MonitorText.PcNowFooter(Mccs.Hex(now)), now, true));
        }

        foreach (var r in inputRows)
        {
            var tags = new List<string>();
            if (r.Current)
            {
                tags.Add(MonitorText.CurrentTag);
            }

            if (picked is null && r.Code == guess)
            {
                tags.Add(MonitorText.GuessedTag);
            }

            rows.Add((Markup.Escape((r.Code == picked ? MonitorText.CurrentMark : " ") + " " + r.Words) + (tags.Count > 0 ? Theme.DimMarkup("  " + string.Join(" · ", tags)) : ""),
                Mccs.Hex(r.Code), r.Code, false));
        }

        bool cable = MonitorShowing.CableKnown(monitor.Connector);
        rows.Add((Markup.Escape((picked is null ? MonitorText.CurrentMark : " ") + " " + (cable ? MonitorText.PcGuessRow : MonitorText.PcNotSetRow)),
            cable ? MonitorText.PcGuessFooter(monitor.Connector) : MonitorText.PcNotSetFooter(monitor.Connector), null, false));

        int cursor = Math.Max(0, rows.FindIndex(r => !r.Now && r.Code == picked));
        var page = new MenuPage(MonitorText.PcTitle(monitor.Label), rows.Select(r => r.Markup).ToList(), MonitorText.PcKeys)
        {
            Footer = (_, r) => r >= 0 && r < rows.Count ? new MenuFooter(rows[r].Footer) : null,
        };
        if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick || pick.Row < 0 || pick.Row >= rows.Count)
        {
            return;
        }

        var chosen = rows[pick.Row];
        MonitorPcResult result;
        if (chosen.Now)
        {
            byte read;
            try
            {
                read = await _board.Actions.ReadInputAsync(monitor, cancellationToken).ConfigureAwait(false);
            }
            catch (MonitorException)
            {
                _pane.Error(MonitorText.PcNoRead(monitor.Label));
                return;
            }

            result = MonitorPc.Saved(monitor, words, key, read);
        }
        else if (chosen.Code is { } code)
        {
            result = MonitorPc.Saved(monitor, words, key, code, withCode: false);
        }
        else
        {
            string? guessed = guess is { } g ? words.Describe(monitor, g) : null;
            result = new MonitorPcResult([MonitorText.PcCleared(monitor.Label, guessed, monitor.Connector)], false, list => MonitorWords.SetPc(list, key, null));
        }

        if (result.Save is { } save)
        {
            _settings.Update(d => d.MonitorPcInputs = save(d.MonitorPcInputs));
        }

        foreach (string said in result.Lines)
        {
            if (result.Failed)
            {
                _pane.Error(said);
            }
            else
            {
                _pane.Notice(said);
            }
        }
    }

    // A line typed on the input row under the page, the row marked; null for Esc or nothing typed.
    private async Task<string?> ReadTextAsync(MenuPage page, int row, string initial, CancellationToken cancellationToken)
    {
        var result = await _pane.EditAsync(page with { Hint = MonitorText.EditKeys, Buttons = null }, row, _line, initial, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        return result is InputResult.Submitted { Text: var text } && !string.IsNullOrWhiteSpace(text) ? text : null;
    }
}
