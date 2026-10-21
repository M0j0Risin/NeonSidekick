using System.Globalization;
using System.Text;

namespace NeonSidekick.EventLog;

/// <summary>
/// One row of any Mac log as <c>/eventlog</c> shows it (2026-10-18, the Mac event-log plan's phase 4): its handle (a <c>u…</c>, a report's
/// file name, an <c>i…</c>), its time, the unified log's level or a report's kind, the process and pid, the unified log's
/// <c>subsystem/category</c>, and the message (a report's summary).
/// </summary>
public sealed record MacLogRow(string Handle, DateTimeOffset Time, int? Level, string? Kind, string Process, int? Pid, string? Where, string Message)
{
    /// <summary>The message's first line, or null when it is blank.</summary>
    public string? FirstLine => Message.Split('\n', 2)[0].Trim() is { Length: > 0 } first ? first : null;
}

/// <summary>
/// A page of rows: newest first, how many were read, whether more may follow and the cursor after the last, a stop and how far back, the
/// loudest process; <see cref="Query"/> (2026-10-20) the query as read, an archive's window anchored at its newest entry.
/// </summary>
public sealed record MacRowsPage(IReadOnlyList<MacLogRow> Rows, int Read, bool More, string? Cursor, bool BudgetStop, DateTimeOffset? Reached, MacLoudest? Loudest, MacLogQuery? Query = null);

/// <summary>
/// The reads <c>/eventlog</c> makes on a Mac (2026-10-18, the Mac event-log plan's phase 4), for its pane and its printed lines alike: a
/// page of any of the logs (the unified log, an archive, the crash reports, install.log) as <see cref="MacLogRow"/>s, the cursor a string
/// whatever the log, and one entry's page in plain text. Over the tools' own policy (<see cref="MacLogSearch"/>, <see cref="MacFileLogs"/>)
/// and the access's doors, so the user's hand reads exactly as the model's does; the user's reads are never redacted.
/// </summary>
public static class MacLogReads
{
    /// <summary>
    /// One page of <paramref name="query"/> in <paramref name="source"/>, after <paramref name="cursor"/> when given; a cursor naming what the
    /// log no longer holds is <see cref="MacLogText.CursorGoneIn"/>, as the tool says it (2026-10-20, the code review: the pane's "more" got an
    /// empty last page instead).
    /// </summary>
    public static async Task<EventLogResult<MacRowsPage>> SearchAsync(MacLogAccess access, MacLogSource source, MacLogQuery query, int scanLimit, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        var scan = access.Scan(source, scanLimit);
        switch (source.Kind)
        {
            case MacLogKind.Crashes:
                return Gone(await access.RunFilesAsync(source, token => MacFileLogs.SearchCrashes(access.Places, query, scan, cursor, token) is { } page ? Rows(page, query) : null,
                    cancellationToken).ConfigureAwait(false), source, cursor);
            case MacLogKind.Install:
                return Gone(await access.RunFilesAsync(source, token =>
                {
                    using var log = access.OpenInstallLog();
                    return MacFileLogs.SearchInstall(log, access.Places, query, scan, cursor, token) is { } page ? Rows(page, query) : null;
                }, cancellationToken).ConfigureAwait(false), source, cursor);
            default:
            {
                var paged = cursor is not null && MacLogHandle.TryParse(cursor, out var before) ? query with { Before = before } : query;
                return await access.RunAsync(source, (unified, token) =>
                {
                    var anchored = MacLogSearch.Anchor(unified, source, paged, token);
                    using var reader = unified.Open(source.Target, anchored.Filter(source.Redacted), token);
                    var page = MacLogSearch.Search(reader, anchored, scan, token, access.Places);
                    return new MacRowsPage(page.Entries.Select(Row).ToList(), page.Read, page.More, page.Cursor?.ToString(), page.BudgetStop, page.Reached, page.Loudest,
                        anchored with { Before = null });
                }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static EventLogResult<MacRowsPage> Gone(EventLogResult<MacRowsPage?> read, MacLogSource source, string? cursor) =>
        read.Error is { } error ? EventLogResult<MacRowsPage>.Failed(error)
            : read.Value is { } page ? EventLogResult<MacRowsPage>.Ok(page)
            : EventLogResult<MacRowsPage>.Failed(MacLogText.CursorGoneIn(source.Log, cursor ?? ""));

    /// <summary>One entry's page as plain text (the user's own: full, unfenced, scrubbed of control characters); null when it is gone.</summary>
    public static Task<EventLogResult<string?>> PageAsync(MacLogAccess access, MacLogSource source, string handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handle);
        var zone = access.Time.LocalTimeZone;
        switch (source.Kind)
        {
            case MacLogKind.Crashes:
                return access.RunFilesAsync(source, _ =>
                    MacFileLogs.CrashEvent(access.Places, handle, source.Redacted) is { } found ? CrashPage(source, found.Report, found.Detail, zone) : null, cancellationToken);
            case MacLogKind.Install:
                return access.RunFilesAsync(source, _ =>
                {
                    using var log = access.OpenInstallLog();
                    return MacFileLogs.InstallEvent(log, access.Places, handle, source.Redacted) is { } view ? InstallPage(source, view, zone) : null;
                }, cancellationToken);
            default:
                if (!MacLogHandle.TryParse(handle, out var parsed))
                {
                    return Task.FromResult(EventLogResult<string?>.Ok(null));
                }

                return access.RunAsync(source, (unified, token) =>
                {
                    using var reader = unified.Open(source.Target, MacLogSearch.FindFilter(parsed), token);
                    return MacLogSearch.Find(reader, parsed, source.Redacted, token, access.Places) is { } view ? UnifiedPage(source, view, zone) : null;
                }, cancellationToken);
        }
    }

    public static MacLogRow Row(MacLogView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var e = view.Entry;
        string where = e.Subsystem + (e.Category.Length > 0 ? "/" + e.Category : "");
        return new MacLogRow(view.Handle.ToString(), e.Time, e.Level, null, e.Process, e.Pid, where.Length > 0 ? where : null, view.Message);
    }

    public static MacLogRow Row(MacFileView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new MacLogRow(view.Handle, view.Time, null, view.Kind, view.Process, view.Pid, null, view.Message);
    }

    private static MacRowsPage Rows(MacFilePage page, MacLogQuery query) =>
        new(page.Entries.Select(Row).ToList(), page.Read, page.More, page.Cursor, page.BudgetStop, page.Reached, page.Loudest, query);

    /// <summary>A unified-log entry's page. Pinned.</summary>
    public static string UnifiedPage(MacLogSource source, MacLogView view, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(view);
        var e = view.Entry;
        var page = new StringBuilder();
        page.Append(source.Log).Append(" entry ").Append(view.Handle).Append(' ').Append(MacLogText.OnThisMac).Append('\n');
        page.Append("time:       ").Append(EventLogText.Time(e.Time, zone)).Append('\n');
        page.Append("level:      ").Append(MacLogRender.LevelWord(e.Level)).Append('\n');
        page.Append("process:    ").Append(e.Process).Append("   pid ").Append(e.Pid.ToString(CultureInfo.InvariantCulture))
            .Append("   thread ").Append(e.Thread.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (e.Subsystem.Length > 0)
        {
            page.Append("subsystem:  ").Append(e.Subsystem).Append('\n');
        }

        if (e.Category.Length > 0)
        {
            page.Append("category:   ").Append(e.Category).Append('\n');
        }

        if (e.Sender.Length > 0 && e.Sender != e.Process)
        {
            page.Append("sender:     ").Append(view.ShownSender).Append('\n');
        }

        page.Append('\n').Append(view.Message.Length > 0 ? view.Message : MacLogText.NoMessage);
        return EventLogText.Scrub(page.ToString().TrimEnd());
    }

    /// <summary>A crash or diagnostic report's page. Pinned.</summary>
    public static string CrashPage(MacLogSource source, CrashReportInfo report, CrashDetail detail, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(detail);
        var page = new StringBuilder();
        page.Append(source.Log).Append(" report ").Append(report.Name).Append(' ').Append(MacLogText.OnThisMac).Append('\n');
        page.Append("time:       ").Append(EventLogText.Time(report.Time, zone)).Append('\n');
        page.Append("kind:       ").Append(report.Kind).Append('\n');
        page.Append("process:    ").Append(report.Process).Append(detail.Pid is { } pid ? "   pid " + pid.ToString(CultureInfo.InvariantCulture) : "").Append('\n');
        page.Append("folder:     ").Append(report.Owner).Append("   ").Append(report.FullPath).Append('\n');
        page.Append("summary:    ").Append(detail.Summary).Append('\n');
        page.Append('\n').Append(detail.Text.Length > 0 ? detail.Text : MacLogText.NoMessage);
        return EventLogText.Scrub(page.ToString().TrimEnd());
    }

    /// <summary>An install.log entry's page. Pinned.</summary>
    public static string InstallPage(MacLogSource source, MacFileView view, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(view);
        var page = new StringBuilder();
        page.Append(source.Log).Append(" entry ").Append(view.Handle).Append(' ').Append(MacLogText.OnThisMac).Append('\n');
        page.Append("time:       ").Append(EventLogText.Time(view.Time, zone)).Append('\n');
        page.Append("process:    ").Append(view.Process.Length > 0 ? view.Process : "?").Append(view.Pid is { } pid ? "   pid " + pid.ToString(CultureInfo.InvariantCulture) : "").Append('\n');
        page.Append('\n').Append(view.Message.Length > 0 ? view.Message : MacLogText.NoMessage);
        return EventLogText.Scrub(page.ToString().TrimEnd());
    }
}
