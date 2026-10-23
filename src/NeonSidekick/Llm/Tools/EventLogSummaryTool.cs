using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.EventLog;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>eventlog_summary(log, since?, level?, computer?)</c> (2026-10-15, the event-log plan): what a log holds since a time (a day by
/// default), counted by provider and event id — the top <see cref="EventLogSearch.SummaryGroups"/>, each with its newest time and record
/// and one fenced sample message — the first look for "what is wrong". The level defaults as <c>eventlog_search</c>'s; a budget stop says
/// the counts are of the newest events looked at.
/// </summary>
public sealed class EventLogSummaryTool : EventLogTool
{
    public const string ToolName = "eventlog_summary";

    /// <summary>The summary's reach when the call names no <c>since</c>.</summary>
    public static readonly TimeSpan DefaultSince = TimeSpan.FromDays(1);

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "log": { "type": "string", "description": "The log: a name eventlog_logs lists (Application, System, Security…; app, sys, sec, ps for short), or an .evtx file in the working directory." },
            "since": { "type": "string", "description": "From when: an age (2h, 1d, 7d) or a moment (2026-10-14T21:00). Default 1d." },
            "level": { "type": "string", "description": "critical, error, warning, information, verbose, or all; several joined by commas. Default: critical,error,warning (Security: its audit failures)." },
            "computer": { "type": "string", "description": "Which computer, as eventlog_logs names them; leave out for this PC (or the only one offered)." }
          },
          "required": ["log"]
        }
        """);

    public EventLogSummaryTool(EventLogAccess access, Func<AppSettingsData> effective, bool forceRedacted = false) : base(access, effective, forceRedacted)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Counts a Windows event log's events since a time (a day by default) by provider and event id, with each kind's newest time and a sample message. Call it first to see what has been going wrong, then eventlog_search or eventlog_event for detail.";

    public override JsonElement JsonSchema => Schema;

    public override EventLogTool AsRedacted() => ForceRedacted ? this : new EventLogSummaryTool(Access, Effective, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, LogArgument) is not { } log)
        {
            return EventLogText.Missing(LogArgument);
        }

        // The arguments read before the computer is resolved (2026-10-23, the admin review's R6: its password was decrypted first).
        var now = Access.Time.GetUtcNow();
        var (since, sinceError) = Moment(arguments, EventLogSearchTool.SinceArgument);
        if (sinceError is not null)
        {
            return sinceError;
        }

        EventLevels? levels = null;
        if (Optional(arguments, EventLogSearchTool.LevelArgument) is { } levelText)
        {
            var (parsed, error) = EventLogQuery.ParseLevels(levelText);
            if (error is not null)
            {
                return error;
            }

            levels = parsed;
        }

        var (source, refused) = Access.Resolve(Optional(arguments, ComputerArgument), log, ForceRedacted);
        if (source is null)
        {
            return refused;
        }

        var scan = Access.Scan(source, ScanLimit);
        var result = await Access.RunAsync(source, (system, token) =>
        {
            // The log's own default only when no level was given (2026-10-23, the admin review's M5: an .evtx was opened for it and then not used).
            var (defaultLevels, audit) = levels is null ? EventLogQuery.DefaultFor(ChannelOf(system, source, token)) : (levels.Value, EventAudit.Any);
            var query = new EventLogQuery
            {
                Log = source.Log,
                Levels = levels ?? defaultLevels,
                Audit = levels is null ? audit : EventAudit.Any,
                Since = since ?? now - DefaultSince,
            };
            var (xpath, built) = EventLogXPath.Build(query, serverFields: false);
            if (xpath is null)
            {
                return built!;
            }

            using var reader = system.Query(source.Target, source.Log, xpath, token);
            var summary = EventLogSearch.Summarize(reader, query, scan, token);
            EventLogAccess.LogQuery(source, xpath, null, summary.Total, summary.Scanned);
            return EventLogRender.Summary(source, query, summary, Zone, now);
        }, cancellationToken).ConfigureAwait(false);
        return result.Error ?? result.Value;
    }
}
