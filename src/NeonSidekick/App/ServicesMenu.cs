using NeonSidekick.Computers;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Services;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>/services</c>' pane (2026-10-15, the admin-tools plan, <see cref="EventLogMenu"/>'s shape): a computer's services on a tabbed page —
/// Running · Stopped · Failed · All — with refresh and (when more than one is ticked) computer as buttons on the tab row; type to filter.
/// Enter opens a service's page on the info pane — its whole configuration, the user's own view (unfenced, unredacted) — with copy, ask, and
/// under <c>access: readwrite</c> the actions its <c>controlActions</c> allow. A change from here is the user's own hand: it skips only
/// <c>Service writes</c> — every other step of the order applies (<see cref="ServiceAccess.WriteRefusal"/>, the dependents) — then a yes/no
/// question, and the audit line says "by you".
/// </summary>
internal sealed class ServicesMenu
{
    public const string Title = ServiceText.Glyph + " Services";

    public const string Keys = "Enter = open · r = refresh · c = computer · ←/→ states · " + MenuFilter.TypeOrShiftAndCloseKeys;

    public static readonly IReadOnlyList<string> TabTitles = ["Running", "Stopped", "Failed", "All"];

    public static readonly InfoAction CopyAction = new("copy", 'c', "copy");
    public static readonly InfoAction AskAction = new("ask", 'a', "ask");
    public static readonly InfoAction StartAction = new("start", 's', "start");
    public static readonly InfoAction StopAction = new("stop", 'x', "stop");
    public static readonly InfoAction RestartAction = new("restart", 'r', "restart");
    public static readonly InfoAction StartTypeAction = new("start type", 't', "start type");

    public static string TitleFor(string where) => Title + " · " + where;

    public static string CopiedNotice(string name) => ServiceText.CopiedNotice(name);

    public const string AskOffNotice = ServiceText.AskOffNotice;

    /// <summary>
    /// One row in <paramref name="columns"/>: the name and the display name each fitted to its column, then the state (and a failed
    /// service's exit code) in its color, unpadded at the end. Since 2026-11-08 (the user's ask: uniform columns) the widths come from the
    /// whole list (<see cref="ServiceColumns.Of"/>); before, the display name took what the row's own state left, so the state wandered.
    /// Each cell is scrubbed, then fitted, then escaped — escaping first could part an escaped bracket pair. Pinned.
    /// </summary>
    public static string RowMarkup(ServiceEntry entry, ServiceColumns columns)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(columns);
        string name = TextCells.Cell(UntrustedText.Scrub(entry.Name), columns.Name);
        string display = TextCells.Cell(UntrustedText.Scrub(entry.DisplayName), columns.Display);
        // A column too narrow for any of the exit code shows the bare word: a cut there would end "stoppe…" and lose the word itself.
        string word = ServiceText.StateWord(entry.State);
        string full = ServiceText.PaneState(entry);
        string state = TextCells.Cut(TextCells.Width(full) <= columns.State || columns.State > TextCells.Width(word) + 1 ? full : word, columns.State);
        var color = entry.State == ServiceRunState.Running ? Theme.Good : ServiceRender.Failed(entry) ? Theme.Bad : Theme.Dim;
        return Markup.Escape(name) + ServiceColumns.Gap + Markup.Escape(display) + ServiceColumns.Gap + Theme.ColorMarkup(color, state);
    }

    private readonly ServiceAccess _access;
    private readonly Func<Settings.AppSettingsData> _effective;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly InfoPane _info;
    private readonly Func<string, bool> _copy;
    private readonly Func<int> _width;
    private readonly Func<string, CancellationToken, Task<bool?>> _confirm;
    private readonly Func<IReadOnlyList<string>, string, CancellationToken, Task<int?>> _choose;

    public ServicesMenu(ServiceAccess access, Func<Settings.AppSettingsData> effective, INoticeSink transcript, MenuPane pane, InfoPane info, Func<string, bool> copy, Func<int> width,
        Func<string, CancellationToken, Task<bool?>> confirm, Func<IReadOnlyList<string>, string, CancellationToken, Task<int?>> choose)
    {
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _copy = copy ?? throw new ArgumentNullException(nameof(copy));
        _width = width ?? throw new ArgumentNullException(nameof(width));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _choose = choose ?? throw new ArgumentNullException(nameof(choose));
    }

    private INoticeSink Sink => _pane.IsOpen ? _pane : _transcript;

    /// <summary>Shows the services for <paramref name="line"/> until ESC; the draft <c>ask</c> asked for, or null.</summary>
    public async Task<string?> ShowAsync(ServiceCommandArgs line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        string? computer = line.Computer;
        string filter = line.Words;
        int tab = Math.Max(0, ServiceCommand.States.ToList().IndexOf(line.State));
        try
        {
            while (true)
            {
                var (source, refused) = _access.Computer(computer, forceRedacted: false);
                if (source is null)
                {
                    Sink.Error(refused!);
                    return null;
                }

                var outcome = await BrowseAsync(source with { Redacted = false }, tab, filter, cancellationToken).ConfigureAwait(false);
                if (outcome.Draft is not null || outcome.Computer is null)
                {
                    return outcome.Draft;
                }

                computer = outcome.Computer;
            }
        }
        finally
        {
            _pane.Close();
        }
    }

    private readonly record struct Outcome(string? Draft, string? Computer);

    private async Task<Outcome> BrowseAsync(ServiceSource source, int tab, string filter, CancellationToken cancellationToken)
    {
        bool showComputer = _access.Catalog().Computers.Count > 1;
        IReadOnlyList<ServiceEntry>? all = null;
        string? error = null;
        var filters = TabTitles.Select(_ => filter).ToArray();
        var cursors = new int[TabTitles.Count];
        while (true)
        {
            if (all is null && error is null)
            {
                Sink.Notice(ServiceText.Working);
                var read = await _access.ReadAsync(source, null, session => session.List(drivers: false).Where(e => ServiceAccess.Visible(source.Computer, e.Name)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(), cancellationToken).ConfigureAwait(false);
                all = read.Value;
                error = read.Error;
            }

            int width = Math.Max(40, _width());
            var lists = TabTitles.Select((_, i) => (all ?? []).Where(e => InTab(e, i) && MenuFilter.Matches(filters[i], e.Name, e.DisplayName)).ToList()).ToList();
            var columns = ServiceColumns.Of(all ?? [], width);
            var tabs = TabTitles.Select((title, i) => new MenuTab(title, lists[i].Count == 0
                    ? [Theme.DimMarkup(error ?? (filters[i].Length > 0 ? MenuFilter.NoMatchLine(filters[i]) : "none"))]
                    : lists[i].Select(e => RowMarkup(e, columns)).ToList())
            {
                Filter = filters[i],
                Hint = MenuFilter.Hint(Keys, filters[i]),
                Placeholder = lists[i].Count == 0,
            }).ToList();
            var page = MenuPage.Tabbed(TitleFor(source.Where), tabs, tab, Keys) with
            {
                Buttons = showComputer ? [new("⟳ refresh", 'r'), new("computer", 'c')] : [new MenuButton("⟳ refresh", 'r')],
                TabButtons = true,
            };
            var picked = await _pane.PickAsync(page, cursors[tab], cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                return new Outcome(null, null);
            }

            tab = Math.Clamp(pick.Tab, 0, TabTitles.Count - 1);
            if (pick.Filter is { } typed)
            {
                filters[tab] = typed;
                cursors[tab] = 0;
                continue;
            }

            if (pick.Button == 0)
            {
                all = null;
                error = null;
                continue;
            }

            if (pick.Button == 1 && showComputer)
            {
                if (await PickComputerAsync(source.Computer.Name, cancellationToken).ConfigureAwait(false) is { } other)
                {
                    return new Outcome(null, other);
                }

                continue;
            }

            var shown = lists[tab];
            if (pick.Row < 0 || pick.Row >= shown.Count)
            {
                continue;
            }

            cursors[tab] = pick.Row;
            var (draft, changed) = await ShowServiceAsync(source, shown[pick.Row].Name, cancellationToken).ConfigureAwait(false);
            if (draft is not null)
            {
                return new Outcome(draft, null);
            }

            if (changed)
            {
                all = null;
            }
        }
    }

    private static bool InTab(ServiceEntry entry, int tab) => tab switch
    {
        0 => entry.State == ServiceRunState.Running,
        1 => entry.State == ServiceRunState.Stopped,
        2 => ServiceRender.Failed(entry),
        _ => true,
    };

    private Task<string?> PickComputerAsync(string current, CancellationToken cancellationToken) =>
        SettingsMenu.PickComputerAsync(_pane, Title, _access.Catalog().Computers, current, cancellationToken);   // shared (T19)

    /// <summary>A service's page with its actions: the draft <c>ask</c> asked for, and whether a change was made (the list is read again).</summary>
    private async Task<(string? Draft, bool Changed)> ShowServiceAsync(ServiceSource source, string name, CancellationToken cancellationToken)
    {
        bool changed = false;
        while (true)
        {
            // The computer read again before each page and each change (2026-10-23, the admin review's T17: every change of a browse was judged
            // against the config read when the pane opened, so a service just made protected, or a computer put back to read-only, still changed).
            if (Fresh(source) is not { } fresh)
            {
                return (null, changed);
            }

            source = fresh;
            var read = await _access.ReadAsync(source, name, session => session.Detail(name), cancellationToken).ConfigureAwait(false);
            if (read.Error is { } failed)
            {
                Sink.Error(failed);
                return (null, changed);
            }

            string text = UntrustedText.Unfenced(ServiceRender.Detail(source.Where, read.Value!, redacted: false, n => ServiceAccess.Visible(source.Computer, n)));
            var actions = new List<InfoAction> { CopyAction, AskAction };
            var config = source.Computer.Config;
            if (config.ReadWrite && config.InControl(name, NameGlob.NoSeparator) && !read.Value!.Config.IsDriver)
            {
                if (config.AllowsAction(ServiceActions.Start))
                {
                    actions.Add(StartAction);
                }

                if (config.AllowsAction(ServiceActions.Stop))
                {
                    actions.Add(StopAction);
                }

                if (config.AllowsAction(ServiceActions.Restart))
                {
                    actions.Add(RestartAction);
                }

                if (config.AllowsAction(ServiceActions.StartType))
                {
                    actions.Add(StartTypeAction);
                }
            }

            await _info.ShowAsync(TitleFor(source.Where), [new InfoTab(name, () => new Text(text))], 0, cancellationToken, actions).ConfigureAwait(false);
            string? action = _info.Pressed?.Key switch
            {
                'c' => "copy",
                'a' => "ask",
                's' => ServiceActions.Start,
                'x' => ServiceActions.Stop,
                'r' => ServiceActions.Restart,
                't' => ServiceActions.StartType,
                _ => null,
            };
            switch (action)
            {
                case null:
                    return (null, changed);
                case "copy":
                    if (_copy(text))
                    {
                        Sink.Notice(CopiedNotice(name));
                    }

                    continue;
                case "ask":
                    if (!_effective().ServiceTools)
                    {
                        Sink.Notice(AskOffNotice);
                        return (null, changed);
                    }

                    return (ServiceCommand.AskDraft(name, source.Computer), changed);
            }

            ServiceStartChoice? choice = null;
            if (action == ServiceActions.StartType)
            {
                var words = ServiceText.StartTypeWords;
                if (await _choose(words, Title + " · " + name + " · start type", cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    continue;
                }

                choice = ServiceChanges.Choice(words[picked]);
            }

            if (Fresh(source) is not { } now)
            {
                return (null, changed);
            }

            string answer = await ServiceChanges.RunAsync(_access, now, name, action, choice, writesOn: true, byUser: true, _confirm, cancellationToken).ConfigureAwait(false);
            var (shown, bad, made) = Computers.ComputerText.ForPane(answer);
            if (bad)
            {
                Sink.Error(shown);
            }
            else
            {
                Sink.Notice(shown);
            }

            changed |= made;
        }
    }

    /// <summary>The computer as its entry stands now (T17), unredacted for the user's own pane; null, said, when it is gone or unticked.</summary>
    private ServiceSource? Fresh(ServiceSource source)
    {
        var (fresh, refused) = _access.Computer(source.Computer.Name, forceRedacted: false);
        if (fresh is null)
        {
            Sink.Error(refused!);
            return null;
        }

        return fresh with { Redacted = false };
    }
}

/// <summary>
/// The widths of <c>/services</c>' columns — name · display name · state — worked out once over the whole list, so typing a filter or
/// switching tabs never moves them (2026-11-08, the user's ask: draw the pane in clean columns, as the other list panes are drawn). The
/// name is the widest one up to <see cref="NameCap"/>; the state the widest state text (<see cref="ServiceText.PaneState"/>) up to
/// <see cref="StateCap"/>; the display name takes what the row has left. When the row is still too wide, the display name gives way to
/// <see cref="DisplayFloor"/>, then the state to its widest state word (an exit code is cut, the word stays), then the name to what is left,
/// never under <see cref="NameFloor"/>; past that, the pane's own cut handles it. No header row, as in the other list panes.
/// </summary>
internal sealed record ServiceColumns(int Name, int Display, int State)
{
    /// <summary>The blanks between two columns.</summary>
    public const string Gap = "  ";

    public const int NameCap = 32;
    public const int StateCap = 28;
    public const int DisplayFloor = 10;
    public const int NameFloor = 12;

    /// <summary>
    /// The cells a row may fill at window <paramref name="width"/>: the pane draws each row after its pointer (<see cref="MenuPane.NoPointer"/>)
    /// and never writes the last column. Pure.
    /// </summary>
    public static int Room(int width) => width - TextCells.Width(MenuPane.NoPointer) - 1;

    /// <summary>The columns for <paramref name="all"/> at window <paramref name="width"/>. Pure.</summary>
    public static ServiceColumns Of(IReadOnlyList<ServiceEntry> all, int width)
    {
        ArgumentNullException.ThrowIfNull(all);
        int room = Room(width);
        int gaps = 2 * Gap.Length;
        int name = Math.Min(NameCap, Widest(all.Select(e => UntrustedText.Scrub(e.Name))));
        int state = Math.Min(StateCap, Widest(all.Select(ServiceText.PaneState)));
        int widestDisplay = Widest(all.Select(e => UntrustedText.Scrub(e.DisplayName)));
        int display = Math.Min(widestDisplay, Math.Max(DisplayFloor, room - name - state - gaps));
        if (name + display + state + gaps > room)
        {
            state = Math.Max(Math.Min(state, Widest(all.Select(e => ServiceText.StateWord(e.State)))), room - name - display - gaps);
            name = Math.Min(name, Math.Max(NameFloor, room - display - state - gaps));
        }

        return new ServiceColumns(name, display, state);
    }

    private static int Widest(IEnumerable<string> texts) => texts.Select(TextCells.Width).DefaultIfEmpty(0).Max();
}
