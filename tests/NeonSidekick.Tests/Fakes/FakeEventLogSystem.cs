using System.Globalization;
using System.Security;
using System.Text;
using NeonSidekick.EventLog;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// An event-log reader for the tests (2026-10-15, the event-log plan): logs of events newest first, each event its XML (its system values
/// read from it by <see cref="EventXml.System"/>) and a message template whose <c>%1</c>… are the event's field values in order — so a
/// redacted format rewrites the inserts exactly as the real one does. Records every query; <see cref="Denied"/> logs refuse with 5,
/// <see cref="Fail"/> makes the next query throw its code, <see cref="Gate"/> holds every read until released (or the token fires).
/// </summary>
public sealed class FakeEventLogSystem : IEventLogSystem
{
    /// <summary>One event: its XML, its message template (null: no message), whether its provider is unregistered.</summary>
    public sealed record FakeEvent(string Xml, string? Template = null, bool Unregistered = false);

    /// <summary>The logs by name (a channel, or a file's full path), newest first.</summary>
    public Dictionary<string, List<FakeEvent>> Logs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The channels <see cref="Channels"/> lists; the logs' names when empty.</summary>
    public List<string> ChannelNames { get; } = [];

    /// <summary>A log's facts; a log not here reads as enabled Admin with its event count.</summary>
    public Dictionary<string, EventLogInfo> Infos { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The logs that refuse with 5 (access denied).</summary>
    public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A code the next query throws, then cleared.</summary>
    public int? Fail { get; set; }

    /// <summary>Holds every query until released; the token cancels the wait.</summary>
    public SemaphoreSlim? Gate { get; set; }

    public int BatchSize { get; set; } = 32;

    public List<(EventLogTarget Target, string Log, string XPath)> Queries { get; } = [];

    public List<EventLogTarget> ChannelTargets { get; } = [];

    public IEventLogReader Query(EventLogTarget target, string log, string xpath, CancellationToken cancellationToken)
    {
        lock (Queries)
        {
            Queries.Add((target, log, xpath));
        }

        Gate?.Wait(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail is { } code)
        {
            Fail = null;
            throw new EventLogException(code, "fake failure " + code.ToString(CultureInfo.InvariantCulture));
        }

        string key = target.Kind == EventLogTargetKind.File ? target.FilePath! : log;
        if (Denied.Contains(key))
        {
            throw new EventLogException(5, "access denied");
        }

        if (!Logs.TryGetValue(key, out var events))
        {
            throw new EventLogException(target.Kind == EventLogTargetKind.File ? 2 : 15007, "no such log");
        }

        return new Reader(events, BatchSize);
    }

    public IReadOnlyList<string> Channels(EventLogTarget target, CancellationToken cancellationToken)
    {
        ChannelTargets.Add(target);
        Gate?.Wait(cancellationToken);
        if (Fail is { } code)
        {
            Fail = null;
            throw new EventLogException(code, "fake failure");
        }

        return ChannelNames.Count > 0 ? ChannelNames : Logs.Keys.ToList();
    }

    public EventLogInfo Info(EventLogTarget target, string log, CancellationToken cancellationToken)
    {
        if (Denied.Contains(log))
        {
            throw new EventLogException(5, "access denied");
        }

        if (Infos.TryGetValue(log, out var info))
        {
            return info;
        }

        int records = Logs.TryGetValue(log, out var events) ? events.Count : 0;
        return new EventLogInfo(log, records, records * 1024L, ManualTimeProvider.DefaultUtcNow);
    }

    private sealed class Reader(List<FakeEvent> events, int batch) : IEventLogReader
    {
        private int _at;

        public IReadOnlyList<RawEvent>? Next()
        {
            if (_at >= events.Count)
            {
                return null;
            }

            var page = events.Skip(_at).Take(batch).Select(Raw).ToList();
            _at += page.Count;
            return page;
        }

        public void Dispose()
        {
        }
    }

    private static RawEvent Raw(FakeEvent e) => new(EventXml.System(e.Xml), () => e.Xml, insert =>
    {
        if (e.Unregistered)
        {
            return new EventMessage(null, EventMessageProblem.Unregistered);
        }

        if (e.Template is null)
        {
            return new EventMessage(null, EventMessageProblem.NoMessage);
        }

        var fields = EventXml.Parse(e.Xml).Fields;
        var text = new StringBuilder(e.Template);
        for (int i = fields.Count; i >= 1; i--)
        {
            string value = insert is null ? fields[i - 1].Value : insert(i - 1, fields[i - 1].Value);
            text.Replace("%" + i.ToString(CultureInfo.InvariantCulture), value);
        }

        return new EventMessage(text.ToString());
    });

    /// <summary>An event's XML as <c>EvtRenderEventXml</c> writes it: the system part and named EventData.</summary>
    public static string Xml(string provider, int id, int level, long record, DateTimeOffset time, string channel = "System", string? computer = null, ulong keywords = 0x8080000000000000, string? userSid = null, params (string Name, string Value)[] data)
    {
        var xml = new StringBuilder("<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System>");
        xml.Append("<Provider Name='").Append(SecurityElement.Escape(provider)).Append("'/>");
        xml.Append("<EventID>").Append(id.ToString(CultureInfo.InvariantCulture)).Append("</EventID><Version>0</Version>");
        xml.Append("<Level>").Append(level.ToString(CultureInfo.InvariantCulture)).Append("</Level><Task>0</Task><Opcode>0</Opcode>");
        xml.Append("<Keywords>0x").Append(keywords.ToString("x", CultureInfo.InvariantCulture)).Append("</Keywords>");
        xml.Append("<TimeCreated SystemTime='").Append(time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)).Append("'/>");
        xml.Append("<EventRecordID>").Append(record.ToString(CultureInfo.InvariantCulture)).Append("</EventRecordID>");
        xml.Append("<Correlation/><Execution ProcessID='4' ThreadID='8'/>");
        xml.Append("<Channel>").Append(SecurityElement.Escape(channel)).Append("</Channel><Computer>").Append(SecurityElement.Escape(computer ?? Environment.MachineName)).Append("</Computer>");
        xml.Append(userSid is null ? "<Security/>" : "<Security UserID='" + userSid + "'/>");
        xml.Append("</System><EventData>");
        foreach (var (name, value) in data)
        {
            xml.Append("<Data Name='").Append(name).Append("'>").Append(SecurityElement.Escape(value)).Append("</Data>");
        }

        return xml.Append("</EventData></Event>").ToString();
    }

    /// <summary>Adds an event to <paramref name="log"/> (in the order given: give the newest first).</summary>
    public FakeEventLogSystem Add(string log, string xml, string? template = null, bool unregistered = false)
    {
        if (!Logs.TryGetValue(log, out var events))
        {
            Logs[log] = events = [];
        }

        events.Add(new FakeEvent(xml, template, unregistered));
        return this;
    }
}
