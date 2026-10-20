using NeonSidekick.Llm.Tools;
using NeonSidekick.Tasks;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>What the tasks pane ended with: a draft for the line, an <c>/eventlog</c> line to open (a task's History), or nothing.</summary>
internal readonly record struct TasksMenuOutcome(string? Draft, string? History);

/// <summary>
/// <c>/tasks</c>' pane (2026-10-15, the admin-tools plan, <see cref="ServicesMenu"/>'s twin): a computer's scheduled tasks on a tabbed page —
/// Ready · Running · Disabled · All — with refresh, computer, folder and Microsoft (Windows' own tasks shown or not) as buttons. Enter opens a
/// task's page — its triggers in words, its actions, who it runs as, its last result — with copy, ask, the actions its computer allows, and
/// History (the Task Scheduler's own log on <c>/eventlog</c>, when that computer is a ticked event-log computer). A change from here is the
/// user's own hand: it skips only <c>Task writes</c>; the rest of the order applies, the privileged guard too, then a yes/no question.
/// </summary>
internal sealed class TasksMenu
{
    public const string Title = TaskText.Glyph + " Tasks";

    public const string Keys = "Enter = open · r = refresh · c = computer · f = folder · w = Windows' own · " + MenuFilter.TypeOrShiftAndCloseKeys;

    public static readonly IReadOnlyList<string> TabTitles = ["Ready", "Running", "Disabled", "All"];

    public static readonly InfoAction CopyAction = new("copy", 'c', "copy");
    public static readonly InfoAction AskAction = new("ask", 'a', "ask");
    public static readonly InfoAction RunAction = new("run", 'r', "run");
    public static readonly InfoAction EndAction = new("end", 'e', "end");
    public static readonly InfoAction EnableAction = new("enable", 'n', "enable");
    public static readonly InfoAction DisableAction = new("disable", 'd', "disable");
    public static readonly InfoAction HistoryAction = new("history", 'h', "runs");

    public static string TitleFor(string where, string folder) => Title + " · " + where + " · " + folder;

    public const string AskOffNotice = "Task tools is off (/tools tasks): the model could not read the task, so nothing was drafted.";

    public static string RowMarkup(TaskEntry entry, int width)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string tail = TaskText.StateWord(entry.State) + "  last " + TaskText.When(entry.LastRun) + (entry.LastRun is null ? "" : " " + TaskWords.Result(entry.LastResult)) + "  next " + TaskText.When(entry.NextRun);
        string path = ScreenPane.Fit(UntrustedText.Scrub(entry.Path), Math.Max(20, width - TextCells.Width(tail) - 6));
        var color = entry.State == TaskRunState.Running ? Theme.Good : entry.LastRun is not null && entry.LastResult is not (0 or 0x41301 or 0x41303) ? Theme.Bad : Theme.Dim;
        return Markup.Escape(path) + "  " + Theme.ColorMarkup(color, tail);
    }

    private readonly TaskAccess _access;
    private readonly Func<Settings.AppSettingsData> _effective;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly InfoPane _info;
    private readonly Func<string, bool> _copy;
    private readonly Func<int> _width;
    private readonly Func<string, CancellationToken, Task<bool?>> _confirm;
    private readonly Func<Computers.Computer<TaskComputerConfig>, string?> _historyComputer;
    private readonly SortedSet<string> _folders = new(StringComparer.OrdinalIgnoreCase) { "\\" };

    public TasksMenu(TaskAccess access, Func<Settings.AppSettingsData> effective, INoticeSink transcript, MenuPane pane, InfoPane info, Func<string, bool> copy, Func<int> width,
        Func<string, CancellationToken, Task<bool?>> confirm, Func<Computers.Computer<TaskComputerConfig>, string?> historyComputer)
    {
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _copy = copy ?? throw new ArgumentNullException(nameof(copy));
        _width = width ?? throw new ArgumentNullException(nameof(width));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _historyComputer = historyComputer ?? throw new ArgumentNullException(nameof(historyComputer));
    }

    private INoticeSink Sink => _pane.IsOpen ? _pane : _transcript;

    public async Task<TasksMenuOutcome> ShowAsync(TaskCommandArgs line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        string? computer = line.Computer;
        string folder = line.Folder;
        bool microsoft = line.Microsoft;
        int tab = line.State switch { "ready" => 0, "running" => 1, "disabled" => 2, _ => 3 };
        try
        {
            while (true)
            {
                var (source, refused) = _access.Computer(computer, forceRedacted: false);
                if (source is null)
                {
                    Sink.Error(refused!);
                    return default;
                }

                var outcome = await BrowseAsync(source with { Redacted = false }, tab, folder, microsoft, line.Words, cancellationToken).ConfigureAwait(false);
                if (outcome.Computer is null)
                {
                    return outcome.Result;
                }

                computer = outcome.Computer;
            }
        }
        finally
        {
            _pane.Close();
        }
    }

    private readonly record struct Browse(TasksMenuOutcome Result, string? Computer);

    private async Task<Browse> BrowseAsync(TaskSource source, int tab, string folder, bool microsoft, string filter, CancellationToken cancellationToken)
    {
        bool showComputer = _access.Catalog().Computers.Count > 1;
        IReadOnlyList<TaskEntry>? all = null;
        string? error = null;
        var filters = TabTitles.Select(_ => filter).ToArray();
        var cursors = new int[TabTitles.Count];
        while (true)
        {
            if (all is null && error is null)
            {
                Sink.Notice(TaskText.Working);
                var taskFilter = new TaskFilter(folder, true, null, null, microsoft);
                var read = await _access.ReadAsync(source, folder, session => Llm.Tools.TaskListTool.Matches(session, source, taskFilter).Matched, cancellationToken).ConfigureAwait(false);
                all = read.Value;
                error = read.Error;
                foreach (var seen in all ?? [])
                {
                    _folders.Add(seen.Folder);
                }
            }

            int width = Math.Max(40, _width());
            var lists = TabTitles.Select((_, i) => (all ?? []).Where(t => InTab(t, i) && MenuFilter.Matches(filters[i], t.Path, t.Name)).ToList()).ToList();
            var tabs = TabTitles.Select((title, i) => new MenuTab(title, lists[i].Count == 0
                    ? [Theme.DimMarkup(error ?? (filters[i].Length > 0 ? MenuFilter.NoMatchLine(filters[i]) : "none"))]
                    : lists[i].Select(t => RowMarkup(t, width)).ToList())
            {
                Filter = filters[i],
                Hint = MenuFilter.Hint(Keys, filters[i]),
            }).ToList();
            var buttons = new List<MenuButton> { new("⟳ refresh", 'r') };
            if (showComputer)
            {
                buttons.Add(new("computer", 'c'));
            }

            buttons.Add(new("folder", 'f'));
            buttons.Add(new(microsoft ? "Windows' own: shown" : "Windows' own: hidden", 'w'));
            var page = MenuPage.Tabbed(TitleFor(source.Where, folder), tabs, tab, Keys) with { Buttons = buttons, TabButtons = true };
            var picked = await _pane.PickAsync(page, cursors[tab], cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                return new Browse(default, null);
            }

            tab = Math.Clamp(pick.Tab, 0, TabTitles.Count - 1);
            if (pick.Filter is { } typed)
            {
                filters[tab] = typed;
                cursors[tab] = 0;
                continue;
            }

            if (pick.Button >= 0)
            {
                string label = buttons[pick.Button].Title;
                if (label.StartsWith("⟳", StringComparison.Ordinal))
                {
                    all = null;
                    error = null;
                }
                else if (label == "computer")
                {
                    if (await PickComputerAsync(source.Computer.Name, cancellationToken).ConfigureAwait(false) is { } other)
                    {
                        return new Browse(default, other);
                    }
                }
                else if (label == "folder")
                {
                    if (await PickFolderAsync(folder, cancellationToken).ConfigureAwait(false) is { } pickedFolder)
                    {
                        folder = pickedFolder;
                        all = null;
                        error = null;
                    }
                }
                else
                {
                    microsoft = !microsoft;
                    all = null;
                    error = null;
                }

                continue;
            }

            var shown = lists[tab];
            if (pick.Row < 0 || pick.Row >= shown.Count)
            {
                continue;
            }

            cursors[tab] = pick.Row;
            var (outcome, changed) = await ShowTaskAsync(source, shown[pick.Row].Path, cancellationToken).ConfigureAwait(false);
            if (outcome.Draft is not null || outcome.History is not null)
            {
                return new Browse(outcome, null);
            }

            if (changed)
            {
                all = null;
            }
        }
    }

    /// <summary>The folders seen so far (the root first) on a pick page; the picked one, or null for ESC.</summary>
    private async Task<string?> PickFolderAsync(string current, CancellationToken cancellationToken)
    {
        var folders = _folders.ToList();
        var page = new MenuPage(Title + " · folder", folders.Select(f => Markup.Escape(UntrustedText.Scrub(f))).ToList(), SettingsMenu.PickKeys);
        int cursor = Math.Max(0, folders.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase)));
        return await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is { Row: var row } && row >= 0 && row < folders.Count ? folders[row] : null;
    }

    private static bool InTab(TaskEntry entry, int tab) => tab switch
    {
        0 => entry.State is TaskRunState.Ready or TaskRunState.Queued,
        1 => entry.State == TaskRunState.Running,
        2 => entry.State == TaskRunState.Disabled,
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

    private async Task<(TasksMenuOutcome Outcome, bool Changed)> ShowTaskAsync(TaskSource source, string path, CancellationToken cancellationToken)
    {
        bool changed = false;
        while (true)
        {
            var read = await _access.ReadAsync(source, path, session => (Detail: session.Detail(path), session.Self), cancellationToken).ConfigureAwait(false);
            if (read.Error is { } failed)
            {
                Sink.Error(failed);
                return (default, changed);
            }

            TaskDefinition? definition = null;
            string? definitionError = null;
            try
            {
                definition = TaskXml.Parse(read.Value.Detail.Xml);
            }
            catch (TaskXmlException ex)
            {
                definitionError = ex.Message;
            }

            string text = UntrustedText.Unfenced(TaskRender.Detail(source.Where, read.Value.Detail, definition, definitionError, redacted: false, read.Value.Self));
            var actions = new List<InfoAction> { CopyAction, AskAction };
            var config = source.Computer.Config;
            if (config.ReadWrite && config.Control is { } control && control.Any(p => TaskAccess.Matches(p.Trim(), path)))
            {
                foreach (var (word, button) in new[] { (TaskActions.Run, RunAction), (TaskActions.End, EndAction), (TaskActions.Enable, EnableAction), (TaskActions.Disable, DisableAction) })
                {
                    if (config.AllowsAction(word))
                    {
                        actions.Add(button);
                    }
                }
            }

            string? historyOn = _historyComputer(source.Computer);
            if (historyOn is not null)
            {
                actions.Add(HistoryAction);
            }

            await _info.ShowAsync(TitleFor(source.Where, path), [new InfoTab(read.Value.Detail.Entry.Name, () => new Text(text))], 0, cancellationToken, actions).ConfigureAwait(false);
            string? action = _info.Pressed?.Key switch
            {
                'c' => "copy",
                'a' => "ask",
                'r' => TaskActions.Run,
                'e' => TaskActions.End,
                'n' => TaskActions.Enable,
                'd' => TaskActions.Disable,
                'h' => "history",
                _ => null,
            };
            switch (action)
            {
                case null:
                    return (default, changed);
                case "copy":
                    if (_copy(text))
                    {
                        Sink.Notice("(" + TaskText.Glyph + " copied " + path + ")");
                    }

                    continue;
                case "ask":
                    if (!_effective().TaskTools)
                    {
                        Sink.Notice(AskOffNotice);
                        return (default, changed);
                    }

                    return (new TasksMenuOutcome(TaskCommand.AskDraft(path, source.Computer), null), changed);
                case "history":
                    return (new TasksMenuOutcome(null, TaskCommand.HistoryLine(path, historyOn == Computers.ComputerConfig.LocalName ? null : historyOn)), changed);
            }

            string answer = await TaskChanges.RunAsync(_access, source, path, action, writesOn: true, byUser: true, _confirm, cancellationToken).ConfigureAwait(false);
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
