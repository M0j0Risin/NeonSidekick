using System.Globalization;
using NeonSidekick.EventLog;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>/eventlog</c>'s pane on a Mac (2026-10-18, the Mac event-log plan's phase 4; <see cref="EventLogMenu"/>'s shape): this Mac's logs on a
/// <see cref="MenuPane"/> page, newest first — the title <c>🪵 Event logs · this Mac</c>, a tab each for <c>system</c> (the unified log:
/// faults and errors over the last hour unless the line says), <c>crashes</c> (the week's reports) and <c>install</c> (install.log's last
/// day), and a fourth for a <c>.logarchive</c> the line named. The title row's buttons: <see cref="EventLogMenu.RefreshButton"/> reads the
/// tab again, <see cref="EventLogMenu.MoreButton"/> (lit while there is more) appends the next page. Rows: the time, a glyph in the theme's
/// colors (◆ fault, ● error or a crash, ▲ a jetsam, ○ default and the other reports, · info, debug and install.log), the process and pid
/// padded, the message's first line (a report's summary) cut to the width; a dim date line where the day changes. Type to filter the
/// loaded rows. Enter or a double-click opens the entry's page on the info pane — full text: it is the user's own pane — with <c>copy</c>
/// and <c>ask</c> (a draft asking the model about it); Esc there comes back to the row. Every drawn string is scrubbed and escaped. The
/// reads are the tools' (<see cref="MacLogReads"/> over <see cref="MacLogAccess"/>), bounded by its timeouts.
/// </summary>
internal sealed class MacLogMenu
{
    public const string Keys = "Enter = open · r = refresh · m = more · ←/→ logs · " + MenuFilter.TypeOrShiftAndCloseKeys;

    /// <summary>The page's title. Pinned.</summary>
    public const string Title = EventLogMenu.Title + " · " + MacLogText.OnThisMacWords;

    /// <summary>The draft <c>ask</c> leaves: <c>Explain log entry u1792….395.7.0 in system</c>. Pinned.</summary>
    public static string AskDraft(string log, string handle) => "Explain log entry " + handle + " in " + log;

    /// <summary>What <c>copy</c> says. Pinned.</summary>
    public static string CopiedNotice(string handle) => "(" + EventLogText.Glyph + " copied " + handle + ")";

    /// <summary>How many cells of a process and pid a row shows.</summary>
    public const int ProcessCells = 28;

    /// <summary>A row's glyph and its theme color: by the unified log's level, a report's kind, or install.log's plain dot.</summary>
    public static (string Glyph, Color Color) GlyphFor(MacLogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Level is { } level)
        {
            return level switch
            {
                5 => ("◆", Theme.Bad),
                4 => ("●", Theme.Bad),
                3 => ("○", Theme.Dim),
                _ => ("·", Theme.Dim),
            };
        }

        return row.Kind switch
        {
            "crash" or "panic" or "fault" => ("●", Theme.Bad),
            "jetsam" => ("▲", Theme.Warn),
            null => ("·", Theme.Dim),
            _ => ("○", Theme.Dim),
        };
    }

    /// <summary>
    /// One row: <c>01:14:40 ● dotnet[83930]                  EXC_CRASH (SIGABRT) — …</c>, the message fitted to what <paramref name="width"/>
    /// leaves; every part scrubbed and escaped. Pinned.
    /// </summary>
    public static string RowMarkup(MacLogRow row, TimeZoneInfo zone, int width)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(zone);
        string time = TimeZoneInfo.ConvertTime(row.Time, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var (glyph, color) = GlyphFor(row);
        string who = (row.Process.Length > 0 ? row.Process : "?") + (row.Pid is { } pid ? "[" + pid.ToString(CultureInfo.InvariantCulture) + "]" : "");
        who = ScreenPane.Fit(EventLogText.Scrub(who), ProcessCells);
        who += new string(' ', Math.Max(0, ProcessCells - TextCells.Width(who)));
        string head = time + " " + glyph + " " + who + " ";
        int room = Math.Max(10, width - TextCells.Width(head) - 4);
        string message = ScreenPane.Fit(EventLogText.Scrub(row.FirstLine ?? MacLogText.NoMessage).Replace('\t', ' ').Replace('\n', ' '), room);
        return Markup.Escape(time) + " " + Theme.ColorMarkup(color, glyph) + " " + Markup.Escape(who) + " " + Markup.Escape(message);
    }

    /// <summary>Whether <paramref name="row"/> stays under <paramref name="filter"/>: its process, handle, subsystem or message holds it.</summary>
    public static bool Matches(string filter, MacLogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return MenuFilter.Matches(filter, row.Process, row.Handle + " " + row.Kind + " " + row.Where + " " + row.Message);
    }

    private readonly MacLogAccess _access;
    private readonly Func<Settings.AppSettingsData> _effective;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly InfoPane _info;
    private readonly Func<string, bool> _copy;
    private readonly Func<int> _width;

    /// <summary>One log's tab: its source, query, what is loaded, the next page's cursor, and the list's state.</summary>
    private sealed class LogTab(MacLogSource source)
    {
        public MacLogSource Source { get; } = source;

        public MacLogQuery? Query { get; set; }

        public List<MacLogRow> Rows { get; } = [];

        public string? Cursor { get; set; }

        public bool More { get; set; }

        public bool Loaded { get; set; }

        public string? Error { get; set; }

        public string Filter { get; set; } = "";

        public int Cursor0 { get; set; }
    }

    public MacLogMenu(MacLogAccess access, Func<Settings.AppSettingsData> effective, INoticeSink transcript, MenuPane pane, InfoPane info, Func<string, bool> copy, Func<int> width)
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

    /// <summary>Shows the logs for <paramref name="line"/> until Esc; the draft <c>ask</c> asked for, or null.</summary>
    public async Task<string?> ShowAsync(MacLogCommandArgs line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        try
        {
            var (named, error) = _access.Resolve(line.Log, forceRedacted: false);
            if (named is null)
            {
                Sink.Error(error!);
                return null;
            }

            var tabs = MacLogCommand.TabLogs.Select(l => new LogTab(_access.Resolve(l, forceRedacted: false).Source! with { Redacted = false })).ToList();
            int first = tabs.FindIndex(t => t.Source.Log == named.Log && t.Source.Kind == named.Kind);
            if (first < 0)
            {
                tabs.Add(new LogTab(named with { Redacted = false }));
                first = tabs.Count - 1;
            }

            return await BrowseAsync(line, tabs, first, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pane.Close();
        }
    }

    private async Task<string?> BrowseAsync(MacLogCommandArgs line, List<LogTab> tabs, int tab, CancellationToken cancellationToken)
    {
        var zone = _access.Time.LocalTimeZone;
        while (true)
        {
            foreach (var unread in tabs.Where(t => !t.Loaded))
            {
                await LoadAsync(line, unread, append: false, cancellationToken).ConfigureAwait(false);
            }

            var current = tabs[tab];
            var menuTabs = tabs.Select(t => BuildTab(t, zone)).ToList();
            var page = MenuPage.Tabbed(Title, menuTabs.Select(m => m.Tab).ToList(), tab, Keys) with
            {
                Buttons = [new(EventLogMenu.RefreshButton, EventLogMenu.RefreshKey), new(EventLogMenu.MoreButton, EventLogMenu.MoreKey, current.More)],
                TabButtons = true,
            };
            var picked = await _pane.PickAsync(page, current.Cursor0, cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                return null;
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
                    current.Rows.Clear();
                    current.Loaded = false;
                    current.Cursor = null;
                    current.Cursor0 = 0;
                    continue;
                case 1:
                    if (current.More)
                    {
                        await LoadAsync(line, current, append: true, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
            }

            if (pick.Button >= 0 || pick.Row < 0 || pick.Row >= shown.Count || shown[pick.Row] is not { } row)
            {
                continue;
            }

            if (await ShowEntryAsync(current.Source, row, cancellationToken).ConfigureAwait(false) is { } draft)
            {
                return draft;
            }
        }
    }

    /// <summary>A tab as the pane draws it: its rows (a dim date line where the day changes), and the entry behind each row.</summary>
    private (MenuTab Tab, List<MacLogRow?> Rows) BuildTab(LogTab tab, TimeZoneInfo zone)
    {
        int width = Math.Max(40, _width());
        var rows = new List<string>();
        var behind = new List<MacLogRow?>();
        var headings = new HashSet<int>();
        string? day = null;
        var listed = tab.Rows.Where(r => Matches(tab.Filter, r)).ToList();
        foreach (var row in listed)
        {
            if (EventLogMenu.DayLine(row.Time, zone) is var line && line != day)
            {
                day = line;
                headings.Add(rows.Count);
                rows.Add(SectionRule.Markup(line));
                behind.Add(null);
            }

            rows.Add(RowMarkup(row, zone, width));
            behind.Add(row);
        }

        if (listed.Count == 0)
        {
            string empty = tab.Error ?? (tab.Filter.Length > 0 ? MenuFilter.NoMatchLine(tab.Filter) : tab.Query is { } q
                ? MacLogCommand.Header(tab.Source, q, new MacRowsPage([], 0, false, null, false, null, null), _access.Time.GetUtcNow(), zone)
                : EventLogText.Working);
            rows.Add(Theme.DimMarkup(Markup.Escape(empty)));
            behind.Add(null);
        }

        var menuTab = new MenuTab(EventLogText.Scrub(tab.Source.Log), rows)
        {
            Headings = headings.Count > 0 ? headings : null,
            Filter = tab.Filter,
            Caption = MenuFilter.CaptionOrNull(tab.Filter, listed.Count, tab.Rows.Count),
            Hint = MenuFilter.Hint(Keys, tab.Filter),
        };
        return (menuTab, behind);
    }

    /// <summary>Reads the tab's first page, or (<paramref name="append"/>) the next one after its cursor; a refusal is the tab's line.</summary>
    private async Task LoadAsync(MacLogCommandArgs line, LogTab tab, bool append, CancellationToken cancellationToken)
    {
        Sink.Notice(EventLogText.Working);
        var now = _access.Time.GetUtcNow();
        MacLogQuery query;
        IReadOnlyList<string> ignored = [];
        if (tab.Query is { } kept)
        {
            query = kept;
        }
        else
        {
            (query, ignored) = line.QueryFor(tab.Source, now, Limit);
        }

        var result = await MacLogReads.SearchAsync(_access, tab.Source, query, ScanLimit, append ? tab.Cursor : null, cancellationToken).ConfigureAwait(false);
        tab.Loaded = true;
        tab.Query = query;
        if (result.Error is { } failed)
        {
            tab.Error = failed;
            tab.More = false;
            return;
        }

        var page = result.Value!;
        tab.Error = null;
        if (!append)
        {
            tab.Rows.Clear();
        }

        tab.Rows.AddRange(page.Rows);
        tab.More = page.More && page.Cursor is not null;
        tab.Cursor = page.Cursor;
        if (ignored.Count > 0 && line.LogGiven && string.Equals(line.Log, tab.Source.Log, StringComparison.OrdinalIgnoreCase))
        {
            Sink.Notice(MacLogText.NotApplied(tab.Source.Log, ignored));
        }

        if (page.BudgetStop)
        {
            Sink.Notice(MacLogText.ScanStopped(page.Read, page.Reached is { } reached ? EventLogText.Time(reached, _access.Time.LocalTimeZone) : null));
        }
    }

    /// <summary>The entry's page on the info pane, read again by its handle; <c>copy</c> copies it, <c>ask</c> returns the draft, Esc comes back.</summary>
    private async Task<string?> ShowEntryAsync(MacLogSource source, MacLogRow row, CancellationToken cancellationToken)
    {
        var read = await MacLogReads.PageAsync(_access, source, row.Handle, cancellationToken).ConfigureAwait(false);
        if (read.Error is { } failed)
        {
            Sink.Error(failed);
            return null;
        }

        if (read.Value is not { } text)
        {
            Sink.Error(MacLogText.NoEntry(row.Handle, source.Log));
            return null;
        }

        while (true)
        {
            await _info.ShowAsync(Title, [new InfoTab(row.Handle, () => new Text(text))], 0, cancellationToken, [EventLogMenu.CopyAction, EventLogMenu.AskAction]).ConfigureAwait(false);
            switch (_info.Pressed?.Key)
            {
                case 'c':
                    if (_copy(text))
                    {
                        Sink.Notice(CopiedNotice(row.Handle));
                    }
                    else
                    {
                        Sink.Error(EventLogMenu.CopyFailedError);
                    }

                    continue;
                case 'a':
                    if (!_effective().EventLogTools)
                    {
                        Sink.Notice(EventLogMenu.AskOffNotice);
                        return null;
                    }

                    return AskDraft(source.Log, row.Handle);
                default:
                    return null;
            }
        }
    }
}
