using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.EventLog;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>eventlog_event(log, record, computer?)</c> (2026-10-15, the event-log plan): one event whole — every system value, the message,
/// the named fields — under the computer's detail; the raw XML only under <c>full</c>, never <c>redacted</c>. Clipped at
/// <see cref="EventLogRender.EventMaxChars"/> with a note.
/// </summary>
public sealed class EventLogEventTool : EventLogTool
{
    public const string ToolName = "eventlog_event";
    public const string RecordArgument = "record";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "log": { "type": "string", "description": "The log the event is in, as eventlog_search named it, or the .evtx file." },
            "record": { "type": "integer", "description": "The event's record number (the # in eventlog_search's lines)." },
            "computer": { "type": "string", "description": "Which computer, as eventlog_logs names them; leave out for this PC (or the only one offered)." }
          },
          "required": ["log", "record"]
        }
        """);

    public EventLogEventTool(EventLogAccess access, Func<AppSettingsData> effective, bool forceRedacted = false) : base(access, effective, forceRedacted)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Reads one Windows event in full by its record number: its time, level, provider, id, message and named fields.";

    public override JsonElement JsonSchema => Schema;

    public override EventLogTool AsRedacted() => ForceRedacted ? this : new EventLogEventTool(Access, Effective, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, LogArgument) is not { } log)
        {
            return EventLogText.Missing(LogArgument);
        }

        var (record, recordError) = Number(arguments, RecordArgument, 1, long.MaxValue, null);
        if (recordError is not null)
        {
            return recordError;
        }

        if (record is not { } number)
        {
            return EventLogText.Missing(RecordArgument);
        }

        var (source, refused) = Access.Resolve(Optional(arguments, ComputerArgument), log, ForceRedacted);
        if (source is null)
        {
            return refused;
        }

        var result = await Access.RunAsync(source, (system, token) =>
        {
            string xpath = "*[System[EventRecordID=" + number.ToString(CultureInfo.InvariantCulture) + "]]";
            using var reader = system.Query(source.Target, source.Log, xpath, token);
            var view = EventLogSearch.Find(reader, number, source.Redacted, token);
            EventLogAccess.LogQuery(source, xpath, null, view is null ? 0 : 1, view is null ? 0 : 1);
            return view is null ? EventLogText.NoEvent(number, source.Log) : EventLogRender.Event(source, view, Zone);
        }, cancellationToken).ConfigureAwait(false);
        return result.Error ?? result.Value;
    }
}
