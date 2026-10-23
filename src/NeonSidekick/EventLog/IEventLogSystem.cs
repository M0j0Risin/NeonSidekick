namespace NeonSidekick.EventLog;

/// <summary>Where a read goes: this PC's live logs, an <c>.evtx</c> file, or a remote computer's live logs.</summary>
public enum EventLogTargetKind
{
    Local,
    File,
    Remote,
}

/// <summary>
/// The account a remote read signs in as (2026-10-15, the event-log plan's phase 3: <c>auth: runas</c> in <c>eventlog.json</c>):
/// passed straight into <c>EvtRpcLogin</c>, no impersonation token. <paramref name="AuthMethod"/> is <c>default</c>, <c>negotiate</c>,
/// <c>kerberos</c> or <c>ntlm</c>. The password is never shown, logged or sent to the model.
/// </summary>
public sealed record EventLogLogin(string User, string Password, string AuthMethod)
{
    /// <summary>The password left out, so a record printed in a log or a test failure never shows it.</summary>
    public override string ToString() => $"EventLogLogin {{ User = {User}, AuthMethod = {AuthMethod} }}";
}

/// <summary>
/// One read's target (2026-10-15): <see cref="EventLogTargetKind.Local"/> (the app's own sign-in, no session), a
/// <see cref="FilePath"/> (an <c>.evtx</c> under the working directory, already resolved), or a <see cref="Host"/> reached over RPC
/// as the user or <see cref="Login"/>'s account.
/// </summary>
/// <remarks><c>AuthMethod</c> (2026-10-23, the admin review's J2): the entry's sign-in method under <c>windows</c> auth too, not only with a runas login.</remarks>
public sealed record EventLogTarget(EventLogTargetKind Kind, string? Host = null, string? FilePath = null, EventLogLogin? Login = null, string? AuthMethod = null)
{
    public static readonly EventLogTarget Local = new(EventLogTargetKind.Local);

    public static EventLogTarget File(string path) => new(EventLogTargetKind.File, FilePath: path);

    public static EventLogTarget Remote(string host, EventLogLogin? login, string? authMethod = null) => new(EventLogTargetKind.Remote, Host: host, Login: login, AuthMethod: authMethod);
}

/// <summary>
/// An event's system values (2026-10-15): what every query reads before a filter or a line, from the render context's System
/// values (the real reader) or the event's XML (<see cref="EventXml.System"/>, the fake). <see cref="Id"/> is the 16-bit id Event
/// Viewer shows; <see cref="Time"/> is UTC.
/// </summary>
public sealed record EventSystemValues(
    string Provider,
    int Id,
    int Level,
    ulong Keywords,
    DateTimeOffset? Time,
    long Record,
    string Channel,
    string Computer,
    string? UserSid = null,
    int Task = 0,
    int Opcode = 0,
    int? ProcessId = null,
    int? ThreadId = null)
{
    /// <summary>The Security log's audit-success keyword (<c>0x20000000000000</c>).</summary>
    public const ulong AuditSuccessKeyword = 0x20000000000000;

    /// <summary>The Security log's audit-failure keyword (<c>0x10000000000000</c>).</summary>
    public const ulong AuditFailureKeyword = 0x10000000000000;

    public bool AuditSuccess => (Keywords & AuditSuccessKeyword) != 0;

    public bool AuditFailure => (Keywords & AuditFailureKeyword) != 0;
}

/// <summary>Why an event has no message of its own.</summary>
public enum EventMessageProblem
{
    None,

    /// <summary>The provider is not registered here (no publisher metadata): any program could have written the event.</summary>
    Unregistered,

    /// <summary>The provider is registered but has no message for this event.</summary>
    NoMessage,

    /// <summary>The message could not be formatted for another reason.</summary>
    Failed,

    /// <summary>
    /// The redacted detail withheld the message (2026-10-23, the admin review's I1 and W13): its values could not be matched to its fields
    /// (an array or a structure is one value over several XML leaves), so a secret's name might not have hidden its value; or a forwarded
    /// event's own text held a secret too short to take out safely.
    /// </summary>
    Withheld,
}

/// <summary>An event's formatted message, or why there is none (a message that will not format never fails the query).</summary>
public sealed record EventMessage(string? Text, EventMessageProblem Problem = EventMessageProblem.None);

/// <summary>
/// One event as a reader hands it over (2026-10-15): its system values, its XML (rendered on first use) and its message, formatted
/// on demand — as Windows formats it from the event's own values, or (the redacted detail) from those values after
/// <c>insert</c> rewrote each string one, given its position among the event's values, so a hidden value never reaches the message.
/// Valid only until the reader's next batch or its disposal: whatever is needed is read before then.
/// </summary>
public sealed class RawEvent
{
    private readonly Func<EventSystemValues>? _systemValues;
    private readonly Func<string> _xml;
    private readonly Func<Func<int, string, string>?, EventMessage> _format;
    private EventSystemValues? _system;
    private string? _xmlText;
    private EventMessage? _ownMessage;

    public RawEvent(EventSystemValues system, Func<string> xml, Func<Func<int, string, string>?, EventMessage> format)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _xml = xml ?? throw new ArgumentNullException(nameof(xml));
        _format = format ?? throw new ArgumentNullException(nameof(format));
    }

    /// <summary>An event whose system values are rendered on first use (2026-10-20, a review: a file's channel is read off its first event, so the rest of that batch is never rendered).</summary>
    public RawEvent(Func<EventSystemValues> system, Func<string> xml, Func<Func<int, string, string>?, EventMessage> format)
    {
        _systemValues = system ?? throw new ArgumentNullException(nameof(system));
        _xml = xml ?? throw new ArgumentNullException(nameof(xml));
        _format = format ?? throw new ArgumentNullException(nameof(format));
    }

    public EventSystemValues System => _system ??= _systemValues!();

    /// <summary>The event as <c>EvtRenderEventXml</c> gives it; rendered once.</summary>
    public string Xml => _xmlText ??= _xml();

    /// <summary>The message as Windows formats it, or (<paramref name="insert"/> given) from the event's values after it rewrote each string one.</summary>
    public EventMessage Format(Func<int, string, string>? insert = null)
    {
        if (insert is null)
        {
            return _ownMessage ??= _format(null);
        }

        return _format(insert);
    }
}

/// <summary>A query's results as a reader hands them over, a batch at a time, newest first.</summary>
public interface IEventLogReader : IDisposable
{
    /// <summary>The next batch, or null at the end. The batch before it is no longer valid.</summary>
    /// <remarks>An empty batch (not the end) is a round that found only events too large to read (2026-10-23, D6): the caller reads its budget and calls again.</remarks>
    IReadOnlyList<RawEvent>? Next();

    /// <summary>How many events so far were too large to read even alone and were stepped over (2026-10-20, a review); never counted among the batches.</summary>
    int Skipped => 0;
}

/// <summary>A channel's kind, as its configuration says (<c>EvtChannelConfigType</c>).</summary>
public enum EventChannelType
{
    Admin,
    Operational,
    Analytic,
    Debug,
}

/// <summary>What <see cref="IEventLogSystem.Info"/> reads of one log; a value Windows would not give is null.</summary>
public sealed record EventLogInfo(string Name, long? Records, long? SizeBytes, DateTimeOffset? LastWrite, bool Enabled = true, EventChannelType Type = EventChannelType.Admin);

/// <summary>
/// The event logs as the event-log tools and <c>/eventlog</c> read them (2026-10-15, the event-log plan): a raw reader with no policy —
/// open a query (target, log, XPath) and take its events a batch at a time, newest first; list the channels; read a log's facts.
/// <see cref="WindowsEventLogSystem"/> over <c>wevtapi.dll</c> on Windows; <c>FakeEventLogSystem</c> in the tests; none elsewhere
/// (<c>Program</c> passes null, and nothing is offered). Every call may throw <see cref="EventLogException"/>; the token cancels a read
/// in flight (the real reader's <c>EvtCancel</c> on its query handle).
/// </summary>
public interface IEventLogSystem
{
    /// <summary>Opens <paramref name="xpath"/> over <paramref name="log"/> (a channel; for a file target, ignored) newest first.</summary>
    IEventLogReader Query(EventLogTarget target, string log, string xpath, CancellationToken cancellationToken);

    /// <summary>Every channel's name on the target.</summary>
    IReadOnlyList<string> Channels(EventLogTarget target, CancellationToken cancellationToken);

    /// <summary>One log's facts: records, size, last write, whether it is enabled and its kind.</summary>
    EventLogInfo Info(EventLogTarget target, string log, CancellationToken cancellationToken);

    /// <summary>
    /// Each of <paramref name="logs"/>' facts, or the refusal that log met (2026-10-20, a review: <c>eventlog_logs</c> on a remote computer
    /// signed in once per log, 41 times a call): the real reader opens one session for all of them. Only a cancel throws.
    /// </summary>
    IReadOnlyList<EventLogInfoResult> Infos(EventLogTarget target, IReadOnlyList<string> logs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logs);
        var results = new List<EventLogInfoResult>(logs.Count);
        foreach (string log in logs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.Add(new EventLogInfoResult(log, Info(target, log, cancellationToken), null));
            }
            catch (EventLogException ex)
            {
                results.Add(new EventLogInfoResult(log, null, ex));
            }
        }

        return results;
    }
}

/// <summary>One log's facts as <see cref="IEventLogSystem.Infos"/> reads them, or the refusal it met.</summary>
public sealed record EventLogInfoResult(string Log, EventLogInfo? Info, EventLogException? Error);

/// <summary>
/// A failure of the event-log reader (2026-10-15): <see cref="Code"/> is the Win32 code (5 access denied, 15007 no such channel…),
/// which <see cref="EventLogErrors.Sentence"/> turns into the sentence the model and the user read; the message is the raw one, for the log.
/// </summary>
public sealed class EventLogException : Exception
{
    public EventLogException()
    {
    }

    public EventLogException(string message)
        : base(message)
    {
    }

    public EventLogException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public EventLogException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>The Win32 code the call failed with; 0 when there was none.</summary>
    public int Code { get; }
}
