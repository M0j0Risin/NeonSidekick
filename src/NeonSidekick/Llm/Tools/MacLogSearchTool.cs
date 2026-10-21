using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.EventLog;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>eventlog_search</c> on a Mac (2026-10-18, the Mac event-log plan): the unified log's (or a <c>.logarchive</c>'s) entries newest
/// first, a fenced line each — the handle, the local time, the level, <c>process[pid]</c>, the subsystem and category, the message's
/// first line — filtered in OSLog's predicate (levels, process, pid, subsystem, category, text, processes left out) inside a window
/// (the last hour unless <c>since</c> says: the log is vast). The header says how many the read let through; a stopped read says how far
/// back it got; the loudest process is named when it made most of the read; the next page is the whole call to make.
/// </summary>
public sealed class MacLogSearchTool : MacLogTool
{
    public const string LevelArgument = "level";
    public const string ProcessArgument = "process";
    public const string PidArgument = "pid";
    public const string SubsystemArgument = "subsystem";
    public const string CategoryArgument = "category";
    public const string TextArgument = "text";
    public const string ExcludeArgument = "exclude_process";
    public const string SinceArgument = "since";
    public const string UntilArgument = "until";
    public const string BeforeArgument = "before";
    public const string LimitArgument = "limit";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "log": { "type": "string", "description": "system for this Mac's unified log, crashes for its crash and diagnostic reports, install for install.log, or a .logarchive folder in the working directory; eventlog_logs lists them." },
            "level": { "type": "string", "description": "fault, error, default, info, debug, or all; several joined by commas. Default: fault,error." },
            "process": { "type": "string", "description": "Only this process, by its name as the answers show it (e.g. loginwindow, kernel)." },
            "pid": { "type": "integer", "description": "Only this process id." },
            "subsystem": { "type": "string", "description": "Only subsystems starting with this (e.g. com.apple.network)." },
            "category": { "type": "string", "description": "Only this category." },
            "text": { "type": "string", "description": "Only entries whose message contains this text (case ignored)." },
            "exclude_process": { "type": "string", "description": "Processes to leave out, joined by commas: the answer names one that floods the read." },
            "since": { "type": "string", "description": "Only entries from then on: an age (10m, 1h, 1d) or a moment (2026-10-18T09:00, in the user's time zone unless it gives an offset). Default: the last hour. Keep it short: the log is vast." },
            "until": { "type": "string", "description": "Only entries until then: an age or a moment." },
            "before": { "type": "string", "description": "Only entries older than this one (its handle, or a report's file name): the next page, as a previous answer's next: line gives it." },
            "limit": { "type": "integer", "description": "How many entries, 1 to 100 (default from the settings, 20)." }
          },
          "required": ["log"]
        }
        """);

    public MacLogSearchTool(MacLogAccess access, Func<AppSettingsData> effective, bool forceRedacted = false) : base(access, effective, forceRedacted)
    {
    }

    public override string Name => EventLogSearchTool.ToolName;

    public override string Description =>
        "Searches this Mac's logs newest first — the unified log by level, process, subsystem, category, text and time inside a short window, the crash reports (log=crashes, the last 7 days) or install.log (log=install, the last day). Use it to see what macOS and its apps recorded: crashes, failed launches, installs, network and sign-in errors.";

    public override JsonElement JsonSchema => Schema;

    public override MacLogTool AsRedacted() => ForceRedacted ? this : new MacLogSearchTool(Access, Effective, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, EventLogTool.LogArgument) is not { } log)
        {
            return EventLogText.Missing(EventLogTool.LogArgument);
        }

        var (source, refused) = Access.Resolve(log, ForceRedacted);
        if (source is null)
        {
            return refused;
        }

        var (limit, limitError) = Number(arguments, LimitArgument, 1, EventLogQuery.MaxLimit, DefaultLimit);
        if (limitError is not null)
        {
            return limitError;
        }

        if (source.Kind != MacLogKind.Unified)
        {
            return await FileSearchAsync(arguments, source, (int)limit!.Value, cancellationToken).ConfigureAwait(false);
        }

        MacLogHandle? before = null;
        if (Optional(arguments, BeforeArgument) is { } cursor)
        {
            if (!MacLogHandle.TryParse(cursor, out var handle))
            {
                return MacLogText.BadHandle(cursor);
            }

            before = handle;
        }

        var (query, _, error) = Terms(arguments, source, (int)limit!.Value, before);
        if (query is null)
        {
            return error;
        }

        var scan = Access.Scan(source, ScanLimit);
        var result = await Access.RunAsync(source, (unified, token) =>
        {
            var anchored = MacLogSearch.Anchor(unified, source, query, token);   // an archive's own last hour (2026-10-20)
            using var reader = unified.Open(source.Target, anchored.Filter(source.Redacted), token);
            return (Query: anchored, Page: MacLogSearch.Search(reader, anchored, scan, token, Access.Places));
        }, cancellationToken).ConfigureAwait(false);
        return result.Error ?? MacLogRender.Search(source, result.Value.Query, result.Value.Page, Zone, Access.Time.GetUtcNow());
    }

    /// <summary>
    /// A search of the crash reports or install.log (phase 3): the same arguments, the terms they lack noted, a report's handle or <c>i…</c>
    /// as the cursor — one naming what the log no longer holds said as gone, for both logs (2026-10-20, the code review: install.log's was
    /// read from its offset whatever was there now).
    /// </summary>
    private async Task<string> FileSearchAsync(AIFunctionArguments arguments, MacLogSource source, int limit, CancellationToken cancellationToken)
    {
        string? cursor = Optional(arguments, BeforeArgument);
        bool crashes = source.Kind == MacLogKind.Crashes;
        if (cursor is not null && (crashes ? !CrashReports.IsReportHandle(cursor) : !InstallLog.TryParseHandle(cursor, out _, out _)))
        {
            return MacLogText.BadFileHandle(cursor, source.Log);
        }

        var (query, ignored, error) = Terms(arguments, source, limit, before: null);
        if (query is null)
        {
            return error!;
        }

        var scan = Access.Scan(source, ScanLimit);
        var result = await Access.RunFilesAsync(source, token =>
        {
            if (crashes)
            {
                return MacFileLogs.SearchCrashes(Access.Places, query, scan, cursor, token);
            }

            using var log = Access.OpenInstallLog();
            return MacFileLogs.SearchInstall(log, Access.Places, query, scan, cursor, token);
        }, cancellationToken).ConfigureAwait(false);
        if (result.Error is { } failed)
        {
            return failed;
        }

        return result.Value is { } page ? MacLogRender.FileSearch(source, query, page, ignored, Zone, Access.Time.GetUtcNow()) : MacLogText.CursorGoneIn(source.Log, cursor!);
    }
}
