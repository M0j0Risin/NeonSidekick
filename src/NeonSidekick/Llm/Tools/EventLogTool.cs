using System.Globalization;
using Microsoft.Extensions.AI;
using NeonSidekick.EventLog;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the four event-log tools share (2026-10-15, the event-log plan, <see cref="DockerTool"/>'s shape): the
/// <see cref="EventLogAccess"/> door, the settings in force at each call, the arguments' readers, and the redacted twin. A tool built
/// with <c>forceRedacted</c> reads every computer redacted whatever its <c>detail</c> — the advisors'
/// (<see cref="NeonAdvisorTool.ReadOnlyOf"/>) — so <c>full</c> stays the main chat's alone. Every tool reads; none changes anything. A refusal is an <c>Error:</c> sentence, never a throw.
/// </summary>
public abstract class EventLogTool : AIFunction, IRedactedTwin
{
    public const string LogArgument = "log";
    public const string ComputerArgument = "computer";

    protected EventLogTool(EventLogAccess access, Func<AppSettingsData> effective, bool forceRedacted)
    {
        Access = access ?? throw new ArgumentNullException(nameof(access));
        Effective = effective ?? throw new ArgumentNullException(nameof(effective));
        ForceRedacted = forceRedacted;
    }

    protected EventLogAccess Access { get; }

    protected Func<AppSettingsData> Effective { get; }

    /// <summary>Whether this tool reads every computer redacted (a bot's, an advisor's, a script's).</summary>
    public bool ForceRedacted { get; }

    /// <summary>The same tool reading every computer redacted; itself when it already does.</summary>
    public abstract EventLogTool AsRedacted();

    AIFunction IRedactedTwin.AsRedacted() => AsRedacted();

    /// <summary>The zone an answer's times are shown in, the user's own.</summary>
    protected TimeZoneInfo Zone => Access.Time.LocalTimeZone;

    /// <summary>The settings' scan limit, clamped.</summary>
    protected int ScanLimit => Math.Clamp(Effective().EventLogScanLimit, AppSettingsData.MinEventLogScanLimit, AppSettingsData.MaxEventLogScanLimit);

    /// <summary>A string argument, or null when blank.</summary>
    // The shared readers (2026-10-23, the admin review's R18, E7, K6: AdminArguments).
    protected static string? Optional(AIFunctionArguments arguments, string name) => AdminArguments.Optional(arguments, name);

    protected static (bool Value, string? Error) Flag(AIFunctionArguments arguments, string name, bool fallback) => AdminArguments.Flag(arguments, name, fallback);

    protected static (long? Value, string? Error) Number(AIFunctionArguments arguments, string name, long min, long max, long? fallback) => AdminArguments.Number(arguments, name, min, max, fallback);

    /// <summary>
    /// A time argument (an age or a moment, in the user's zone); null when missing, the refusal when it is neither. Held to the whole second,
    /// the start down and an end (<paramref name="end"/>) up (2026-10-20, a review: the next call restates it to the second, so a page's
    /// window and the next one's differed by the fraction).
    /// </summary>
    protected (DateTimeOffset? Value, string? Error) Moment(AIFunctionArguments arguments, string name, bool end = false) =>
        AdminArguments.Moment(arguments, name, Access.Time.GetUtcNow(), Zone, end, EventLogText.BadTime);

    internal static DateTimeOffset WholeSecond(DateTimeOffset moment, bool up) => AdminArguments.WholeSecond(moment, up);

    /// <summary>A cursor argument (a record, or one with its tag): null when missing, the refusal when it is neither.</summary>
    protected static (EventCursor? Value, string? Error) Cursor(AIFunctionArguments arguments, string name)
    {
        if (Optional(arguments, name) is not { } text)
        {
            return (null, null);
        }

        return EventCursor.Parse(text) is { } cursor ? (cursor, null) : (null, EventLogText.BadCursor(name, text));
    }

    /// <summary>The channel the log's default view is read for (<see cref="EventLogAccess.ChannelOf"/>).</summary>
    protected static string ChannelOf(IEventLogSystem system, EventLogSource source, CancellationToken cancellationToken) =>
        EventLogAccess.ChannelOf(system, source, cancellationToken);
}
