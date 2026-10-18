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
            "log": { "type": "string", "description": "system for this Mac's unified log, or a .logarchive folder in the working directory." },
            "since": { "type": "string", "description": "From then on: an age (10m, 1h, 1d) or a moment. Default: the last hour." },
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
        "Counts this Mac's unified-log entries by process, subsystem and category over a window (faults and errors over the last hour by default): what is going wrong, before searching.";

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

        var (query, error) = Terms(arguments, source.Log, EventLogQuery.MaxLimit, before: null);
        if (query is null)
        {
            return error;
        }

        var scan = Access.Scan(source, ScanLimit);
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
            "log": { "type": "string", "description": "system, or the .logarchive the entry came from." },
            "record": { "type": "string", "description": "The entry's handle, the u… an answer shows." }
          },
          "required": ["log", "record"]
        }
        """);

    public MacLogEventTool(MacLogAccess access, Func<AppSettingsData> effective, bool forceRedacted = false) : base(access, effective, forceRedacted)
    {
    }

    public override string Name => EventLogEventTool.ToolName;

    public override string Description => "Reads one entry of this Mac's unified log in full by its handle (the u… a search or summary shows).";

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

        if (!MacLogHandle.TryParse(record, out var handle))
        {
            return MacLogText.BadHandle(record);
        }

        var (source, refused) = Access.Resolve(log, ForceRedacted);
        if (source is null)
        {
            return refused;
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
}

/// <summary>
/// <c>eventlog_logs</c> on a Mac (2026-10-18, the Mac event-log plan): the logs a call may name — the unified log as <c>system</c>,
/// readable or why not (its store opened, nothing read), and the working directory's <c>.logarchive</c> folders, <c>filter</c> narrowing
/// them by name.
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

    public override string Description => "Lists the logs on this Mac the other eventlog_ tools read: the unified log (system) and the .logarchive folders in the working directory.";

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
        string? why = probed.Error is { } error ? (error.StartsWith("Error: ", StringComparison.Ordinal) ? error["Error: ".Length..] : error) : null;
        return MacLogRender.Logs(why, Access.Archives(), filter);
    }
}
