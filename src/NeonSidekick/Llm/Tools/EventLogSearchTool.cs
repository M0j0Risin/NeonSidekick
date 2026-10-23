using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.EventLog;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>eventlog_search(log, level?, id?, provider?, field?, since?, until?, audit?, text?, before_record?, limit?, computer?)</c>
/// (2026-10-15, the event-log plan): a log's events newest first, a fenced line each — the record, the local time, the level, the
/// provider, the id, the message's first line — filtered in the query (levels, ids, providers, times, the Security log's audits, EventData's
/// named values) and by <c>text</c> client-side. With no level or audit given a log opens on its usual view
/// (<see cref="EventLogQuery.DefaultFor"/>: Security's audit failures, else critical events, errors and warnings). The header says how
/// many were looked at; the next page is the whole call to make (<see cref="EventLogRender.NextCall"/>).
/// </summary>
public sealed class EventLogSearchTool : EventLogTool
{
    public const string ToolName = "eventlog_search";
    public const string LevelArgument = "level";
    public const string IdArgument = "id";
    public const string ProviderArgument = "provider";
    public const string FieldArgument = "field";
    public const string SinceArgument = "since";
    public const string UntilArgument = "until";
    public const string AuditArgument = "audit";
    public const string TextArgument = "text";
    public const string BeforeRecordArgument = "before_record";
    public const string LimitArgument = "limit";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "log": { "type": "string", "description": "The log: a name eventlog_logs lists (Application, System, Security, Setup, Microsoft-Windows-PowerShell/Operational; app, sys, sec, ps for short), or an .evtx file in the working directory." },
            "level": { "type": "string", "description": "critical, error, warning, information, verbose, or all; several joined by commas. Default: critical,error,warning (Security: its audit failures)." },
            "id": { "type": "string", "description": "Event ids and ranges, e.g. \"41,1074,6008\" or \"4700-4799\"." },
            "provider": { "type": "string", "description": "The provider (source) names, as the answers show them; several joined by commas." },
            "field": { "type": "string", "description": "A named EventData value to match, Name=value (e.g. TargetUserName=alice)." },
            "since": { "type": "string", "description": "Only events from then on: an age (30m, 2h, 1d, 1w) or a moment (2026-10-14T21:00, in the user's time zone unless it gives an offset)." },
            "until": { "type": "string", "description": "Only events until then: an age or a moment." },
            "audit": { "type": "string", "enum": ["success", "failure", "any"], "description": "The Security log's audits: failure, success or any." },
            "text": { "type": "string", "description": "Only events whose provider, fields or message contain this text (case ignored)." },
            "before_record": { "type": "string", "description": "The next page: the record (with its tag in a forwarded log, 1200@3f9a2c1b) exactly as a previous answer's next: line gives it." },
            "limit": { "type": "integer", "description": "How many events, 1 to 100 (default from the settings, 20)." },
            "computer": { "type": "string", "description": "Which computer, as eventlog_logs names them; leave out for this PC (or the only one offered). Not with an .evtx file." }
          },
          "required": ["log"]
        }
        """);

    public EventLogSearchTool(EventLogAccess access, Func<AppSettingsData> effective, bool forceRedacted = false) : base(access, effective, forceRedacted)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Searches a Windows event log, newest first, by level, event id, provider, time, a named field or text; Security by its audit failures or successes. Use it to see what Windows recorded: crashes, failed services, driver errors, sign-in failures, PowerShell script blocks.";

    public override JsonElement JsonSchema => Schema;

    public override EventLogTool AsRedacted() => ForceRedacted ? this : new EventLogSearchTool(Access, Effective, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, LogArgument) is not { } log)
        {
            return EventLogText.Missing(LogArgument);
        }

        var (source, refused) = Access.Resolve(Optional(arguments, ComputerArgument), log, ForceRedacted);
        if (source is null)
        {
            return refused;
        }

        int fallback = Math.Clamp(Effective().EventLogMaxEvents, AppSettingsData.MinEventLogMaxEvents, AppSettingsData.MaxEventLogMaxEvents);
        var (parsed, error) = Parse(arguments, source, fallback);
        if (parsed is null)
        {
            return error;
        }

        var now = Access.Time.GetUtcNow();
        var scan = Access.Scan(source, ScanLimit);
        var result = await Access.RunAsync(source, (system, token) =>
        {
            // A file's channel is read only when its default view is wanted (2026-10-20, a review: it opened the file twice for nothing).
            var query = parsed.LevelGiven || parsed.AuditGiven ? parsed.Query : parsed.Complete(ChannelOf(system, source, token));
            // The field terms are the client's alone, whatever the detail (2026-10-23, the admin review's W2: the log's XPath reads only
            // EventData's named values, untrimmed, while the client reads every field, so a full-detail search missed what a redacted one found).
            var (xpath, built) = EventLogXPath.Build(query, serverFields: false);
            if (xpath is null)
            {
                return built!;
            }

            using var reader = system.Query(source.Target, source.Log, xpath, token);
            var page = EventLogSearch.Search(reader, query, scan, token);
            EventLogAccess.LogQuery(source, xpath, query.Text, page.Events.Count, page.Scanned);
            return EventLogRender.Search(source, query, page, Zone, now);
        }, cancellationToken).ConfigureAwait(false);
        return result.Error ?? result.Value;
    }

    /// <summary>The call's terms before the log's default view is known (a file's channel is read first).</summary>
    internal sealed record QueryArgs(EventLogQuery Query, bool LevelGiven, bool AuditGiven)
    {
        /// <summary>The query with the log's default view where the call named neither a level nor an audit.</summary>
        public EventLogQuery Complete(string channel)
        {
            if (LevelGiven || AuditGiven)
            {
                return Query;
            }

            var (levels, audit) = EventLogQuery.DefaultFor(channel);
            return Query with { Levels = levels, Audit = audit };
        }
    }

    /// <summary>Every argument read into a query, or the first refusal.</summary>
    internal (QueryArgs? Args, string? Error) Parse(AIFunctionArguments arguments, EventLogSource source, int fallbackLimit)
    {
        var query = new EventLogQuery { Log = source.Log };
        bool levelGiven = false, auditGiven = false;
        if (Optional(arguments, LevelArgument) is { } levelText)
        {
            var (levels, error) = EventLogQuery.ParseLevels(levelText);
            if (error is not null)
            {
                return (null, error);
            }

            query = query with { Levels = levels };
            levelGiven = true;
        }

        if (Optional(arguments, AuditArgument) is { } auditText)
        {
            var (audit, error) = EventLogQuery.ParseAudit(auditText);
            if (error is not null)
            {
                return (null, error);
            }

            query = query with { Audit = audit };
            auditGiven = true;
        }

        if (Optional(arguments, IdArgument) is { } idText)
        {
            var (ids, error) = EventLogQuery.ParseIds(idText);
            if (error is not null)
            {
                return (null, error);
            }

            query = query with { Ids = ids };
        }

        if (Optional(arguments, ProviderArgument) is { } providerText)
        {
            query = query with { Providers = providerText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
        }

        if (Optional(arguments, FieldArgument) is { } fieldText)
        {
            var (term, error) = EventLogQuery.ParseField(fieldText);
            if (term is null)
            {
                return (null, error);
            }

            if (source.Redacted && EventLogRedaction.IsSecretField(term.Value.Name))
            {
                return (null, EventLogText.SecretField(term.Value.Name));
            }

            query = query with { Fields = [term.Value] };
        }

        var (since, sinceError) = Moment(arguments, SinceArgument);
        if (sinceError is not null)
        {
            return (null, sinceError);
        }

        var (until, untilError) = Moment(arguments, UntilArgument, end: true);
        if (untilError is not null)
        {
            return (null, untilError);
        }

        var (before, beforeError) = Cursor(arguments, BeforeRecordArgument);
        if (beforeError is not null)
        {
            return (null, beforeError);
        }

        var (limit, limitError) = Number(arguments, LimitArgument, 1, EventLogQuery.MaxLimit, fallbackLimit);
        if (limitError is not null)
        {
            return (null, limitError);
        }

        // The text term held to MaxTextChars (2026-10-23, the admin review's K6: the next page's call restated it whole, so a 2 MB term made a
        // 2 MB answer).
        string? text = Optional(arguments, TextArgument);
        if (text is { Length: > ArgumentText.MaxTextChars })
        {
            return (null, ArgumentText.TooLong(TextArgument, ArgumentText.MaxTextChars));
        }

        query = query with { Since = since, Until = until, BeforeRecord = before, Limit = (int)limit!.Value, Text = text };
        return (new QueryArgs(query, levelGiven, auditGiven), null);
    }
}
