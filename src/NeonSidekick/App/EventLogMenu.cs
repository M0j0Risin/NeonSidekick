using System.Globalization;
using System.Text;
using NeonSidekick.EventLog;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>/eventlog</c>'s pane (2026-10-15, the event-log plan's phase 2, the <see cref="DockerMenu"/> shape): a computer's (or an <c>.evtx</c>
/// file's) events on a <see cref="MenuPane"/> page, newest first — the title <c>🪵 Event logs · local</c>, a tab per log among Application,
/// System, Security and Setup that the computer's <c>logs</c> allows (and the one the line named), each opening on its default view
/// (<see cref="EventLogQuery.DefaultFor"/>: Security's audit failures) unless the line named a level; a tab is marked "no access" once a
/// read was refused. The title row's buttons: <see cref="RefreshButton"/> reads the tab again, <see cref="MoreButton"/> (lit while there is
/// more) appends the next page, <see cref="ComputerButton"/> (not for a file or a single ticked computer) picks another computer. Rows:
/// the time, a level glyph in the theme's colors (an audit's on Security), the provider padded, the id, the message's first line cut to
/// the width; a dim date line where the day changes. Type to filter the loaded rows; Shift+letter starts a filter with a button's letter.
/// Enter or a double-click opens the event's page on the info pane — full text, whatever a computer's detail says: it is the user's own
/// pane — with <c>copy</c> and <c>ask</c> (a draft asking the model about it); ESC there comes back to the row. Every drawn string is
/// scrubbed of control characters and escaped. The reads are the tools' (<see cref="EventLogAccess"/>), bounded by its timeouts.
/// </summary>
internal sealed class EventLogMenu
{
    public const string Title = EventLogText.Glyph + " Event logs";

    public const string Keys = "Enter = open · r = refresh · m = more · c = computer · ←/→ logs · " + MenuFilter.TypeOrShiftAndCloseKeys;

    public const string RefreshButton = "⟳ refresh";
    public const char RefreshKey = 'r';
    public const string MoreButton = "more";
    public const char MoreKey = 'm';
    public const string ComputerButton = "computer";
    public const char ComputerKey = 'c';

    /// <summary>The event page's buttons: copy its text, and ask the model about it. Pinned.</summary>
    public static readonly InfoAction CopyAction = new("copy", 'c', "copy");

    public static readonly InfoAction AskAction = new("ask", 'a', "ask");

    /// <summary>A tab's title once a read of it was refused. Pinned.</summary>
    public static string NoAccessTitle(string log) => log + " (no access)";

    /// <summary>The page's title: <c>🪵 Event logs · local</c>, or <c>· crash\System.evtx</c> for a file. Pinned.</summary>
    public static string TitleFor(string where) => Title + " · " + where;

    /// <summary>The draft <c>ask</c> leaves: <c>Explain event System record 81234 on dc01</c>, or for a file <c>… record 81234 in crash\System.evtx</c>. Pinned.</summary>
    public static string AskDraft(EventLogSource source, long record)
    {
        ArgumentNullException.ThrowIfNull(source);
        string number = record.ToString(CultureInfo.InvariantCulture);
        return source.IsFile ? "Explain event record " + number + " in " + source.Log : "Explain event " + source.Log + " record " + number + " on " + source.Computer.Name;
    }

    /// <summary>What <c>ask</c> says with the group off. Pinned.</summary>
    public const string AskOffNotice = "Event log tools is off (/tools eventlog): the model could not read the event, so nothing was drafted.";

    /// <summary>What <c>copy</c> says. Pinned.</summary>
    public static string CopiedNotice(long record) => "(" + EventLogText.Glyph + " copied record " + record.ToString(CultureInfo.InvariantCulture) + ")";

    public const string CopyFailedError = "Could not copy the event to the clipboard";

    /// <summary>A level's glyph and its theme color: ◆ critical, ● error, ▲ warning, ○ information, · verbose; ✗ an audit failure, ✓ a success.</summary>
    public static (string Glyph, Color Color) LevelGlyph(int level, ulong keywords)
    {
        if ((keywords & EventSystemValues.AuditFailureKeyword) != 0)
        {
            return ("✗", Theme.Bad);
        }

        if ((keywords & EventSystemValues.AuditSuccessKeyword) != 0)
        {
            return ("✓", Theme.Good);
        }

        return level switch
        {
            1 => ("◆", Theme.Bad),
            2 => ("●", Theme.Bad),
            3 => ("▲", Theme.Warn),
            5 => ("·", Theme.Dim),
            _ => ("○", Theme.Dim),
        };
    }

    /// <summary>How many cells of a provider's name a row shows.</summary>
    public const int ProviderCells = 28;

    /// <summary>
    /// One row: <c>21:03:11 ● Service Control Manager      7000  The … service failed</c>, the message fitted to what <paramref name="width"/> leaves;
    /// every part scrubbed and escaped. Pinned.
    /// </summary>
    public static string RowMarkup(EventView view, TimeZoneInfo zone, int width)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(zone);
        var s = view.System;
        string time = s.Time is { } t ? TimeZoneInfo.ConvertTime(t, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "--:--:--";
        var (glyph, color) = LevelGlyph(s.Level, s.Keywords);
        string provider = ScreenPane.Fit(EventLogText.Scrub(s.Provider), ProviderCells);
        provider += new string(' ', Math.Max(0, ProviderCells - TextCells.Width(provider)));
        string id = s.Id.ToString(CultureInfo.InvariantCulture).PadLeft(5);
        string head = time + " " + glyph + " " + provider + " " + id + "  ";
        int room = Math.Max(10, width - TextCells.Width(head) - 4);
        string message = ScreenPane.Fit(EventLogText.Scrub(view.FirstLine ?? EventLogRender.NoMessage(view)).Replace('\t', ' ').Replace('\n', ' '), room);
        return Markup.Escape(time) + " " + Theme.ColorMarkup(color, glyph) + " " + Markup.Escape(provider) + " " + Theme.DimMarkup(id) + "  " + Markup.Escape(message);
    }

    /// <summary>The dim line where the day changes: <c>Wednesday 14 October 2026</c>. Pinned.</summary>
    public static string DayLine(DateTimeOffset moment, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(moment, zone).ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="view"/> stays under <paramref name="filter"/>: its provider, id or message holds it.</summary>
    public static bool Matches(string filter, EventView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return MenuFilter.Matches(filter, view.System.Provider, view.System.Id.ToString(CultureInfo.InvariantCulture) + " " + view.Message);
    }

    /// <summary>
    /// The event's page as plain text (the user's own: full text, never fenced): the system values, the message, the named fields and the
    /// XML, scrubbed. Pinned.
    /// </summary>
    public static string PageText(EventLogSource source, EventView view, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(view);
        var s = view.System;
        var page = new StringBuilder();
        page.Append(source.Log).Append(" record ").Append(s.Record.ToString(CultureInfo.InvariantCulture)).Append(" on ").Append(source.Where).Append('\n');
        page.Append("time:      ").Append(EventLogText.Time(s.Time, zone)).Append('\n');
        page.Append("level:     ").Append(EventLogText.Level(s.Level, s.Keywords)).Append('\n');
        page.Append("provider:  ").Append(s.Provider);
        if (view.SourceName is { Length: > 0 } sourceName && !string.Equals(sourceName, s.Provider, StringComparison.OrdinalIgnoreCase))
        {
            page.Append(" (source ").Append(sourceName).Append(')');
        }

        if (view.Unregistered)
        {
            page.Append("  (").Append(EventLogText.UnregisteredSource).Append(')');
        }

        page.Append('\n');
        page.Append("id:        ").Append(s.Id.ToString(CultureInfo.InvariantCulture)).Append("   task ").Append(s.Task.ToString(CultureInfo.InvariantCulture))
            .Append("   opcode ").Append(s.Opcode.ToString(CultureInfo.InvariantCulture)).Append("   keywords 0x").Append(s.Keywords.ToString("X", CultureInfo.InvariantCulture)).Append('\n');
        page.Append("computer:  ").Append(s.Computer).Append('\n');
        if (s.UserSid is { Length: > 0 } sid)
        {
            page.Append("user:      ").Append(sid).Append('\n');
        }

        if (s.ProcessId is { } process)
        {
            page.Append("process:   ").Append(process.ToString(CultureInfo.InvariantCulture)).Append("   thread ").Append((s.ThreadId ?? 0).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        page.Append('\n').Append(view.Message ?? EventLogRender.NoMessage(view)).Append('\n');
        if (view.Fields.Count > 0)
        {
            page.Append("\nfields:\n");
            foreach (var field in view.Fields)
            {
                page.Append("  ").Append(field.Name).Append(": ").Append(field.Value).Append('\n');
            }
        }

        if (view.Xml is { Length: > 0 } xml)
        {
            page.Append("\nxml:\n").Append(xml).Append('\n');
        }

        return EventLogText.Scrub(page.ToString().TrimEnd());
    }

    private readonly EventLogAccess _access;
    private readonly Func<Settings.AppSettingsData> _effective;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly InfoPane _info;
    private readonly Func<string, bool> _copy;
    private readonly Func<int> _width;

    /// <summary>One log's tab: its log (a channel or the file), what is loaded, the next page's cursor, and whether a read was refused.</summary>
    private sealed class LogTab(string log)
    {
        public string Log { get; } = log;

        public EventLogSource? Source { get; set; }

        public EventLogQuery? Query { get; set; }

        public List<EventView> Events { get; } = [];

        public long? Cursor { get; set; }

        public bool More { get; set; }

        public bool Loaded { get; set; }

        public bool NoAccess { get; set; }

        public string? Error { get; set; }

        public string Filter { get; set; } = "";

        public int Cursor0 { get; set; }
    }

    public EventLogMenu(EventLogAccess access, Func<Settings.AppSettingsData> effective, INoticeSink transcript, MenuPane pane, InfoPane info, Func<string, bool> copy, Func<int> width)
    {
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _copy = copy ?? throw new ArgumentNullException(nameof(copy));
        _width = width ?? throw new ArgumentNullException(nameof(width));
    }

    private INoticeSink Sink => _pane.IsOpen ? _pane : _transcript;

    private int Limit => Math.Clamp(_effective().EventLogMaxEvents, Settings.AppSettingsData.MinEventLogMaxEvents, Settings.AppSettingsData.MaxEventLogMaxEvents);

    private int ScanLimit => Math.Clamp(_effective().EventLogScanLimit, Settings.AppSettingsData.MinEventLogScanLimit, Settings.AppSettingsData.MaxEventLogScanLimit);

    /// <summary>
    /// Shows the list for <paramref name="line"/> until ESC; the draft <c>ask</c> asked for, or null. The host puts it on the draft line (at
    /// the idle line) or keeps it for after the reply (under one).
    /// </summary>
    public async Task<string?> ShowAsync(EventLogCommandArgs line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        string? computer = line.Computer;
        try
        {
            while (true)
            {
                var (tabs, first, error) = TabsFor(line, computer);
                if (tabs is null)
                {
                    Sink.Error(error!);
                    return null;
                }

                var outcome = await BrowseAsync(line, tabs, first, cancellationToken).ConfigureAwait(false);
                if (outcome.Draft is not null || outcome.Computer is null)
                {
                    return outcome.Draft;
                }

                computer = outcome.Computer;
                line = line with { Log = EventLogCommand.DefaultLog, LogGiven = false };
            }
        }
        finally
        {
            _pane.Close();
        }
    }

    /// <summary>The tabs for a computer (or the file): the four logs it allows, and the line's own when it is none of them.</summary>
    private (List<LogTab>? Tabs, int First, string? Error) TabsFor(EventLogCommandArgs line, string? computer)
    {
        if (EventLogQuery.IsFile(line.Log))
        {
            var (fileSource, fileError) = _access.Resolve(null, line.Log, forceRedacted: false);
            return fileSource is null ? (null, 0, fileError) : ([new LogTab(line.Log) { Source = fileSource with { Redacted = false } }], 0, null);
        }

        string wanted = EventLogQuery.ResolveAlias(line.Log);
        var (source, error) = _access.Resolve(computer, wanted, forceRedacted: false);
        if (source is null)
        {
            return (null, 0, error);
        }

        var logs = EventLogCommand.TabLogs.Where(l => EventLogCatalog.Allows(source.Computer.Config.Logs, l)).ToList();
        if (!logs.Contains(wanted, StringComparer.OrdinalIgnoreCase))
        {
            logs.Add(wanted);
        }

        var tabs = logs.Select(l => new LogTab(l) { Source = source with { Log = l, Redacted = false } }).ToList();
        int first = Math.Max(0, logs.FindIndex(l => string.Equals(l, wanted, StringComparison.OrdinalIgnoreCase)));
        return (tabs, first, null);
    }

    private readonly record struct Outcome(string? Draft, string? Computer);

    private async Task<Outcome> BrowseAsync(EventLogCommandArgs line, List<LogTab> tabs, int tab, CancellationToken cancellationToken)
    {
        var zone = _access.Time.LocalTimeZone;
        bool showComputer = !tabs[0].Source!.IsFile && _access.Catalog().Computers.Count > 1;
        string where = tabs[0].Source!.IsFile ? tabs[0].Log : tabs[0].Source!.Computer.Name;
        while (true)
        {
            // Every tab read before the page shows (the pane switches tabs itself, without coming back here): four small reads.
            foreach (var unread in tabs.Where(t => !t.Loaded))
            {
                await LoadAsync(line, unread, append: false, cancellationToken).ConfigureAwait(false);
            }

            var current = tabs[tab];

            var menuTabs = tabs.Select(t => BuildTab(t, zone)).ToList();
            var page = MenuPage.Tabbed(TitleFor(where), menuTabs.Select(m => m.Tab).ToList(), tab, Keys) with
            {
                Buttons = ButtonsFor(current.More, showComputer),
                TabButtons = true,
            };
            var picked = await _pane.PickAsync(page, current.Cursor0, cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                return new Outcome(null, null);
            }

            if (pick.Tab != tab)
            {
                tab = Math.Clamp(pick.Tab, 0, tabs.Count - 1);
                current = tabs[tab];
            }

            var shown = menuTabs[tab].Rows;
            if (pick.Filter is { } typed)
            {
                current.Filter = typed;
                current.Cursor0 = 0;
                continue;
            }

            if (pick.Row >= 0 && pick.Row < shown.Count)
            {
                current.Cursor0 = pick.Row;
            }

            switch (pick.Button)
            {
                case 0:
                    current.Events.Clear();
                    current.Loaded = false;
                    current.Cursor0 = 0;
                    continue;
                case 1:
                    if (current.More)
                    {
                        await LoadAsync(line, current, append: true, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                case 2 when showComputer:
                {
                    if (await PickComputerAsync(where, cancellationToken).ConfigureAwait(false) is { } other)
                    {
                        return new Outcome(null, other);
                    }

                    continue;
                }
            }

            if (pick.Button >= 0 || pick.Row < 0 || pick.Row >= shown.Count || shown[pick.Row] is not { } view)
            {
                continue;
            }

            if (await ShowEventAsync(current.Source!, view, cancellationToken).ConfigureAwait(false) is { } draft)
            {
                return new Outcome(draft, null);
            }
        }
    }

    /// <summary>A tab as the pane draws it: its rows (a dim date line where the day changes), and the event behind each row.</summary>
    private (MenuTab Tab, List<EventView?> Rows) BuildTab(LogTab tab, TimeZoneInfo zone)
    {
        int width = Math.Max(40, _width());
        var rows = new List<string>();
        var behind = new List<EventView?>();
        var headings = new HashSet<int>();
        string? day = null;
        var listed = tab.Events.Where(e => Matches(tab.Filter, e)).ToList();
        foreach (var view in listed)
        {
            if (view.System.Time is { } t && DayLine(t, zone) is var line && line != day)
            {
                day = line;
                headings.Add(rows.Count);
                rows.Add(SectionRule.Markup(line));
                behind.Add(null);
            }

            rows.Add(RowMarkup(view, zone, width));
            behind.Add(view);
        }

        if (listed.Count == 0)
        {
            rows.Add(Theme.DimMarkup(tab.Error ?? (tab.Filter.Length > 0 ? MenuFilter.NoMatchLine(tab.Filter) : tab.Query is { } q
                ? EventLogText.NoEvents(tab.Log, q.Levels, q.Audit, q.Since, _access.Time.GetUtcNow(), zone) : EventLogText.Working)));
            behind.Add(null);
        }

        string title = tab.NoAccess ? NoAccessTitle(tab.Log) : tab.Log;
        var menuTab = new MenuTab(title, rows)
        {
            Headings = headings.Count > 0 ? headings : null,
            Filter = tab.Filter,
            Caption = MenuFilter.CaptionOrNull(tab.Filter, listed.Count, tab.Events.Count),
            Hint = MenuFilter.Hint(Keys, tab.Filter),
        };
        return (menuTab, behind);
    }

    private static IReadOnlyList<MenuButton> ButtonsFor(bool more, bool computer) =>
        computer
            ? [new(RefreshButton, RefreshKey), new(MoreButton, MoreKey, more), new(ComputerButton, ComputerKey)]
            : [new(RefreshButton, RefreshKey), new(MoreButton, MoreKey, more)];

    /// <summary>Reads the tab's first page, or (<paramref name="append"/>) the next one after its cursor; a refusal marks the tab.</summary>
    private async Task LoadAsync(EventLogCommandArgs line, LogTab tab, bool append, CancellationToken cancellationToken)
    {
        var source = tab.Source!;
        Sink.Notice(EventLogText.Working);
        var now = _access.Time.GetUtcNow();
        var scan = _access.Scan(source, ScanLimit);
        var result = await _access.RunAsync(source, (system, token) =>
        {
            var query = tab.Query ?? line.For(source.Log, source.IsFile ? EventLogCommand.FileChannel(system, source, token) : source.Log) with { Limit = Limit };
            if (append)
            {
                query = query with { BeforeRecord = tab.Cursor };
            }

            var (xpath, built) = EventLogXPath.Build(query, serverFields: true);
            if (xpath is null)
            {
                return (Query: query, Page: (EventSearchPage?)null, Error: built);
            }

            using var reader = system.Query(source.Target, source.Log, xpath, token);
            var page = EventLogSearch.Search(reader, query, scan, token);
            EventLogAccess.LogQuery(source, xpath, query.Text, page.Events.Count, page.Scanned);
            return (Query: query with { BeforeRecord = null }, Page: page, Error: (string?)null);
        }, cancellationToken).ConfigureAwait(false);
        tab.Loaded = true;
        if (result.Error is { } failed)
        {
            tab.Error = failed;
            tab.NoAccess = failed.Contains("Event Log Readers", StringComparison.Ordinal);
            tab.More = false;
            return;
        }

        var (query, page, problem) = result.Value;
        tab.Query = query;
        if (page is null)
        {
            tab.Error = problem;
            tab.More = false;
            return;
        }

        tab.Error = null;
        if (!append)
        {
            tab.Events.Clear();
        }

        tab.Events.AddRange(page.Events);
        tab.More = page.More && page.Cursor is not null;
        tab.Cursor = page.Cursor;
        if (page.BudgetStop)
        {
            Sink.Notice(EventLogRender.BudgetNote(page.Scanned, page.ReachedTime, _access.Time.LocalTimeZone));
        }
    }

    /// <summary>The ticked computers on a pick page; the picked name, or null for ESC.</summary>
    private async Task<string?> PickComputerAsync(string current, CancellationToken cancellationToken)
    {
        var computers = _access.Catalog().Computers;
        int width = computers.Max(c => c.Name.Length) + 2;
        var rows = computers.Select(c => Markup.Escape(c.Name.PadRight(width)) + Theme.DimMarkup(SettingsMenu.EventLogComputerNote(c))).ToList();
        int cursor = Math.Max(0, computers.ToList().FindIndex(c => string.Equals(c.Name, current, StringComparison.OrdinalIgnoreCase)));
        var page = new MenuPage(Title + " · " + ComputerButton, rows, SettingsMenu.PickKeys);
        return await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is { Row: var row } && row >= 0 && row < computers.Count ? computers[row].Name : null;
    }

    /// <summary>
    /// The event's page on the info pane, its XML read first; <c>copy</c> copies it and shows it again, <c>ask</c> returns the draft (or, with
    /// the group off, says so on the status line), ESC comes back to the list.
    /// </summary>
    private async Task<string?> ShowEventAsync(EventLogSource source, EventView listed, CancellationToken cancellationToken)
    {
        var zone = _access.Time.LocalTimeZone;
        var whole = await _access.RunAsync(source, (system, token) =>
        {
            using var reader = system.Query(source.Target, source.Log, "*[System[EventRecordID=" + listed.System.Record.ToString(CultureInfo.InvariantCulture) + "]]", token);
            return EventLogSearch.Find(reader, listed.System.Record, redacted: false, token);
        }, cancellationToken).ConfigureAwait(false);
        var view = whole.Value ?? listed;
        string text = PageText(source, view, zone);
        while (true)
        {
            await _info.ShowAsync(TitleFor(source.IsFile ? source.Log : source.Computer.Name), [new InfoTab("#" + view.System.Record.ToString(CultureInfo.InvariantCulture), () => new Text(text))], 0, cancellationToken, [CopyAction, AskAction]).ConfigureAwait(false);
            switch (_info.Pressed?.Key)
            {
                case 'c':
                    if (_copy(text))
                    {
                        Sink.Notice(CopiedNotice(view.System.Record));
                    }
                    else
                    {
                        Sink.Error(CopyFailedError);
                    }

                    continue;
                case 'a':
                    if (!_effective().EventLogTools)
                    {
                        Sink.Notice(AskOffNotice);
                        return null;
                    }

                    return AskDraft(source, view.System.Record);
                default:
                    return null;
            }
        }
    }
}
