using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.EventLog;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>eventlog_summary</c> on a Mac (2026-10-18, the Mac event-log plan): the window's entries counted by process, subsystem and
/// category, the top 30 by count, each with its highest level, its newest entry's time and handle, and a sample message — what is going
/// wrong, before listing. Faults and errors over the last hour unless the call says; under the scan's limit, which the header and a
/// stopped read's note make plain.
/// </summary>
public sealed class MacLogSummaryTool : MacLogTool
{
    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "log": { "type": "string", "description": "system for this Mac's unified log, crashes for its crash and diagnostic reports, install for install.log, or a .logarchive folder in the working directory." },
            "since": { "type": "string", "description": "From then on: an age (10m, 1h, 1d) or a moment. Default: the last hour (crashes: 7 days, install: a day)." },
            "level": { "type": "string", "description": "fault, error, default, info, debug, or all; several joined by commas. Default: fault,error." },
            "process": { "type": "string", "description": "Only this process." },
            "subsystem": { "type": "string", "description": "Only subsystems starting with this." },
            "exclude_process": { "type": "string", "description": "Processes to leave out, joined by commas." }
          },
          "required": ["log"]
        }
        """);

    public MacLogSummaryTool(MacLogAccess access, Func<AppSettingsData> effective, bool forceRedacted = false) : base(access, effective, forceRedacted)
    {
    }

    public override string Name => EventLogSummaryTool.ToolName;

    public override string Description =>
        "Counts this Mac's log entries over a window — the unified log's by process, subsystem and category (faults and errors over the last hour by default), the crash reports by process and kind, install.log's by process: what is going wrong, before searching.";

    public override JsonElement JsonSchema => Schema;

    public override MacLogTool AsRedacted() => ForceRedacted ? this : new MacLogSummaryTool(Access, Effective, forceRedacted: true);

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

        var (query, error) = Terms(arguments, source.Log, EventLogQuery.MaxLimit, before: null, source.Kind);
        if (query is null)
        {
            return error;
        }

        var scan = Access.Scan(source, ScanLimit);
        if (source.Kind != MacLogKind.Unified)
        {
            var files = await Access.RunFilesAsync(source, token =>
            {
                if (source.Kind == MacLogKind.Crashes)
                {
                    return MacFileLogs.SummarizeCrashes(Access.Places, query, scan, token);
                }

                using var log = Access.OpenInstallLog();
                return MacFileLogs.SummarizeInstall(log, Access.Places, query, scan, token);
            }, cancellationToken).ConfigureAwait(false);
            return files.Error ?? MacLogRender.FileSummary(source, query, files.Value!, Ignored(arguments, source.Kind), Zone, Access.Time.GetUtcNow());
        }

        var result = await Access.RunAsync(source, (unified, token) =>
        {
            using var reader = unified.Open(source.Target, query.Filter(source.Redacted), token);
            return MacLogSearch.Summarize(reader, query, scan, token);
        }, cancellationToken).ConfigureAwait(false);
        return result.Error ?? MacLogRender.Summary(source, query, result.Value!, Zone, Access.Time.GetUtcNow());
    }
}

/// <summary>
/// <c>eventlog_event</c> on a Mac (2026-10-18, the Mac event-log plan): one entry in full by its handle (<c>record</c>, the <c>u…</c> an
/// answer shows) — every field and the whole message, fenced. The entry is read again from its own second with its pid, so the read is
/// short; an entry the log has moved past says so.
/// </summary>
public sealed class MacLogEventTool : MacLogTool
{
    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "log": { "type": "string", "description": "system, crashes, install, or the .logarchive the entry came from." },
            "record": { "type": "string", "description": "The entry's handle as an answer shows it: a u… (system, an archive), a report's file name (crashes) or an i… (install)." }
          },
          "required": ["log", "record"]
        }
        """);

    public MacLogEventTool(MacLogAccess access, Func<AppSettingsData> effective, bool forceRedacted = false) : base(access, effective, forceRedacted)
    {
    }

    public override string Name => EventLogEventTool.ToolName;

    public override string Description => "Reads one entry of this Mac's logs in full by its handle (the u…, report file name or i… a search or summary shows): a crash report's exception, termination and faulting thread, an install.log entry's whole message.";

    public override JsonElement JsonSchema => Schema;

    public override MacLogTool AsRedacted() => ForceRedacted ? this : new MacLogEventTool(Access, Effective, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, EventLogTool.LogArgument) is not { } log)
        {
            return EventLogText.Missing(EventLogTool.LogArgument);
        }

        if (Optional(arguments, EventLogEventTool.RecordArgument) is not { } record)
        {
            return EventLogText.Missing(EventLogEventTool.RecordArgument);
        }

        var (source, refused) = Access.Resolve(log, ForceRedacted);
        if (source is null)
        {
            return refused;
        }

        if (source.Kind != MacLogKind.Unified)
        {
            return await FileEventAsync(source, record, cancellationToken).ConfigureAwait(false);
        }

        if (!MacLogHandle.TryParse(record, out var handle))
        {
            return MacLogText.BadHandle(record);
        }

        var result = await Access.RunAsync(source, (unified, token) =>
        {
            using var reader = unified.Open(source.Target, MacLogSearch.FindFilter(handle), token);
            return MacLogSearch.Find(reader, handle, source.Redacted, token);
        }, cancellationToken).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            return error;
        }

        return result.Value is { } view ? MacLogRender.Event(source, view, Zone) : MacLogText.NoEntry(handle.ToString(), source.Log);
    }

    /// <summary>One crash report by its file name, or one install.log entry by its <c>i…</c> handle, its time checked again (phase 3).</summary>
    private async Task<string> FileEventAsync(MacLogSource source, string record, CancellationToken cancellationToken)
    {
        if (source.Kind == MacLogKind.Crashes)
        {
            if (!CrashReports.IsReportName(record))
            {
                return MacLogText.BadFileHandle(record, source.Log);
            }

            var report = await Access.RunFilesAsync(source, _ => MacFileLogs.CrashEvent(Access.Places, record, source.Redacted), cancellationToken).ConfigureAwait(false);
            return report.Error ?? (report.Value is { } found ? MacLogRender.CrashEvent(source, found.Report, found.Detail, Zone) : MacLogText.NoEntry(record, source.Log));
        }

        if (!InstallLog.TryParseHandle(record, out _, out _))
        {
            return MacLogText.BadFileHandle(record, source.Log);
        }

        var entry = await Access.RunFilesAsync(source, _ =>
        {
            using var log = Access.OpenInstallLog();
            return MacFileLogs.InstallEvent(log, Access.Places, record, source.Redacted);
        }, cancellationToken).ConfigureAwait(false);
        return entry.Error ?? (entry.Value is { } view ? MacLogRender.InstallEvent(source, view, Zone) : MacLogText.NoEntry(record, source.Log));
    }
}

/// <summary>
/// <c>eventlog_logs</c> on a Mac (2026-10-18, the Mac event-log plan): the logs a call may name — the unified log as <c>system</c>,
/// readable or why not (its store opened, nothing read), the crash reports as <c>crashes</c> (the week's count, each folder readable or
/// not) and install.log as <c>install</c> (its newest entry's time; phase 3), and the working directory's <c>.logarchive</c> folders,
/// <c>filter</c> narrowing them by name.
/// </summary>
public sealed class MacLogLogsTool : MacLogTool
{
    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "filter": { "type": "string", "description": "Only archives whose names contain this." }
          }
        }
        """);

    public MacLogLogsTool(MacLogAccess access, Func<AppSettingsData> effective, bool forceRedacted = false) : base(access, effective, forceRedacted)
    {
    }

    public override string Name => EventLogLogsTool.ToolName;

    public override string Description => "Lists the logs on this Mac the other eventlog_ tools read: the unified log (system), the crash and diagnostic reports (crashes), install.log (install) and the .logarchive folders in the working directory.";

    public override JsonElement JsonSchema => Schema;

    public override MacLogTool AsRedacted() => ForceRedacted ? this : new MacLogLogsTool(Access, Effective, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string? filter = Optional(arguments, "filter");
        var source = new MacLogSource(MacLogAccess.SystemNames[0], UnifiedLogTarget.Local, Redacted: true);
        var probed = await Access.RunAsync(source, (unified, _) =>
        {
            unified.Probe(UnifiedLogTarget.Local);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        string? why = Bare(probed.Error);
        var now = Access.Time.GetUtcNow();
        var crashSource = new MacLogSource(MacLogAccess.CrashNames[0], UnifiedLogTarget.Local, Redacted: true, MacLogKind.Crashes);
        var crashes = await Access.RunFilesAsync(crashSource, token =>
        {
            var (reports, folders) = CrashReports.List(Access.Places.CrashFolders, now - MacFileLogs.CrashWindow, null, token);
            return MacLogText.CrashesLine(reports.Count, folders.Select(f => (f.Folder.Owner, f.Refused)).ToList());
        }, cancellationToken).ConfigureAwait(false);
        var installSource = new MacLogSource(MacLogAccess.InstallNames[0], UnifiedLogTarget.Local, Redacted: true, MacLogKind.Install);
        var install = await Access.RunFilesAsync(installSource, _ =>
        {
            using var stream = Access.OpenInstallLog();
            return MacLogText.InstallLine(InstallLog.Backward(stream).FirstOrDefault() is { } newest ? EventLogText.Time(newest.Time, Zone) : null);
        }, cancellationToken).ConfigureAwait(false);
        return MacLogRender.Logs(why, crashes.Error is { } crashError ? Bare(crashError)! : crashes.Value!, install.Error is { } installError ? MacLogText.InstallRefused(Bare(installError)!) : install.Value!,
            Access.Archives(), filter);
    }

    private static string? Bare(string? error) => error is null ? null : error.StartsWith("Error: ", StringComparison.Ordinal) ? error["Error: ".Length..] : error;
}
