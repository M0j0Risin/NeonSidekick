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
    public static readonly InfoAction AskAction = new("ask", 'a', "ask the model");
    public static readonly InfoAction StartAction = new("start", 's', "start it");
    public static readonly InfoAction StopAction = new("stop", 'x', "stop it");
    public static readonly InfoAction RestartAction = new("restart", 'r', "restart it");
    public static readonly InfoAction StartTypeAction = new("start type", 't', "change its start type");

    public static string TitleFor(string where) => Title + " · " + where;

    public static string CopiedNotice(string name) => "(" + ServiceText.Glyph + " copied " + name + ")";

    public const string AskOffNotice = "Service tools is off (/tools services): the model could not read the service, so nothing was drafted.";

    /// <summary>One row: the name padded, the display name, the state. Pinned.</summary>
    public static string RowMarkup(ServiceEntry entry, int nameWidth, int width)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string name = UntrustedText.Scrub(entry.Name);
        string state = ServiceText.StateWord(entry.State) + (ServiceRender.Failed(entry) ? " (exit " + ServiceRender.ExitCode(entry.Win32ExitCode, entry.ServiceExitCode) + ")" : "");
        int room = Math.Max(10, width - nameWidth - TextCells.Width(state) - 8);
        string display = ScreenPane.Fit(UntrustedText.Scrub(entry.DisplayName), room);
        var color = entry.State == ServiceRunState.Running ? Theme.Good : ServiceRender.Failed(entry) ? Theme.Bad : Theme.Dim;
        return Markup.Escape(name.PadRight(nameWidth)) + "  " + Markup.Escape(display) + "  " + Theme.ColorMarkup(color, state);
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
            int nameWidth = Math.Min(32, (all ?? []).Select(e => e.Name.Length).DefaultIfEmpty(10).Max());
            var tabs = TabTitles.Select((title, i) => new MenuTab(title, lists[i].Count == 0
                    ? [Theme.DimMarkup(error ?? (filters[i].Length > 0 ? MenuFilter.NoMatchLine(filters[i]) : "none"))]
                    : lists[i].Select(e => RowMarkup(e, nameWidth, width)).ToList())
            {
                Filter = filters[i],
                Hint = MenuFilter.Hint(Keys, filters[i]),
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

    private async Task<string?> PickComputerAsync(string current, CancellationToken cancellationToken)
    {
        var computers = _access.Catalog().Computers;
        int width = computers.Max(c => c.Name.Length) + 2;
        var rows = computers.Select(c => Markup.Escape(c.Name.PadRight(width)) + Theme.DimMarkup(SettingsMenu.ComputerNote(c))).ToList();
        int cursor = Math.Max(0, computers.ToList().FindIndex(c => string.Equals(c.Name, current, StringComparison.OrdinalIgnoreCase)));
        var page = new MenuPage(Title + " · computer", rows, SettingsMenu.PickKeys);
        return await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is { Row: var row } && row >= 0 && row < computers.Count ? computers[row].Name : null;
    }

    /// <summary>A service's page with its actions: the draft <c>ask</c> asked for, and whether a change was made (the list is read again).</summary>
    private async Task<(string? Draft, bool Changed)> ShowServiceAsync(ServiceSource source, string name, CancellationToken cancellationToken)
    {
        bool changed = false;
        while (true)
        {
            var read = await _access.ReadAsync(source, name, session => session.Detail(name), cancellationToken).ConfigureAwait(false);
            if (read.Error is { } failed)
            {
                Sink.Error(failed);
                return (null, changed);
            }

            string text = UntrustedText.Unfenced(ServiceRender.Detail(source.Where, read.Value!, redacted: false));
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
                string[] words = ["automatic", "delayed", "manual", "disabled"];
                if (await _choose(words, Title + " · " + name + " · start type", cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    continue;
                }

                choice = ServiceChanges.Choice(words[picked]);
            }

            string answer = await ServiceChanges.RunAsync(_access, source, name, action, choice, writesOn: true, byUser: true, _confirm, cancellationToken).ConfigureAwait(false);
            if (answer.StartsWith("Error:", StringComparison.Ordinal))
            {
                Sink.Error(answer.Replace("Error: ", "", StringComparison.Ordinal));
            }
            else
            {
                Sink.Notice(answer);
                changed = true;
            }
        }
    }
}
