using System.Globalization;
using Microsoft.Extensions.AI;
using NeonSidekick.EventLog;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the four event-log tools share on a Mac (2026-10-18, the Mac event-log plan; <see cref="EventLogTool"/>'s shape): the
/// <see cref="MacLogAccess"/> door, the settings in force at each call, the arguments' readers, and the redacted twin. They answer to
/// the Windows tools' names (<see cref="EventLogLogsTool.ToolName"/>…) and argument names where they mean the same (<c>log</c>,
/// <c>record</c>), so the rule, the advisors' twins and <c>AdvisorRecent</c>'s stand-ins work unchanged. A
/// tool built with <c>forceRedacted</c> reads redacted whatever <c>local</c>'s detail. Every tool reads; none changes anything. A refusal
/// is an <c>Error:</c> sentence, never a throw.
/// </summary>
public abstract class MacLogTool : AIFunction, IRedactedTwin
{
    protected MacLogTool(MacLogAccess access, Func<AppSettingsData> effective, bool forceRedacted)
    {
        Access = access ?? throw new ArgumentNullException(nameof(access));
        Effective = effective ?? throw new ArgumentNullException(nameof(effective));
        ForceRedacted = forceRedacted;
    }

    protected MacLogAccess Access { get; }

    protected Func<AppSettingsData> Effective { get; }

    /// <summary>Whether this tool reads redacted whatever <c>local</c>'s detail (an advisor's).</summary>
    public bool ForceRedacted { get; }

    /// <summary>The same tool reading redacted; itself when it already does.</summary>
    public abstract MacLogTool AsRedacted();

    AIFunction IRedactedTwin.AsRedacted() => AsRedacted();

    /// <summary>The zone an answer's times are shown in, the user's own.</summary>
    protected TimeZoneInfo Zone => Access.Time.LocalTimeZone;

    /// <summary>The settings' scan limit, clamped.</summary>
    protected int ScanLimit => Math.Clamp(Effective().EventLogScanLimit, AppSettingsData.MinEventLogScanLimit, AppSettingsData.MaxEventLogScanLimit);

    /// <summary>The settings' page size, clamped.</summary>
    protected int DefaultLimit => Math.Clamp(Effective().EventLogMaxEvents, AppSettingsData.MinEventLogMaxEvents, AppSettingsData.MaxEventLogMaxEvents);

    /// <summary>A string argument, or null when blank.</summary>
    // The shared readers (2026-10-23, the admin review's R18, E7, K6: AdminArguments).
    protected static string? Optional(AIFunctionArguments arguments, string name) => AdminArguments.Optional(arguments, name);

    protected static (long? Value, string? Error) Number(AIFunctionArguments arguments, string name, long min, long max, long? fallback) => AdminArguments.Number(arguments, name, min, max, fallback);

    /// <summary>
    /// A time argument (an age or a moment, in the user's zone); null when missing, the refusal when it is neither. Held to the whole second,
    /// a start down and an end (<paramref name="end"/>) up (2026-10-23, the admin review's R1: the fraction was kept, while the next page's
    /// call restates <c>until</c> to the second, so entries in [T.000, T.300) were skipped between pages).
    /// </summary>
    protected (DateTimeOffset? Value, string? Error) Moment(AIFunctionArguments arguments, string name, bool end = false) =>
        AdminArguments.Moment(arguments, name, Access.Time.GetUtcNow(), Zone, end, EventLogText.BadTime);

    /// <summary>
    /// A search's or a summary's terms read off the call (the same words both take) into the one query builder <c>/eventlog</c> shares
    /// (<see cref="MacLogTerms.QueryFor"/>, 2026-10-20, the code review): the levels (the default faults and errors), the window (the last
    /// hour unless <c>since</c> says; an archive's anchored at its own newest entry), the filters, and the terms a log that is a file lacks
    /// (for the answer's note); null with the refusal when one is wrong.
    /// </summary>
    protected (MacLogQuery? Query, IReadOnlyList<string> Ignored, string? Error) Terms(AIFunctionArguments arguments, MacLogSource source, int limit, MacLogHandle? before)
    {
        var (since, sinceError) = Moment(arguments, MacLogSearchTool.SinceArgument);
        if (sinceError is not null)
        {
            return (null, [], sinceError);
        }

        var (until, untilError) = Moment(arguments, MacLogSearchTool.UntilArgument, end: true);
        if (untilError is not null)
        {
            return (null, [], untilError);
        }

        var (pid, pidError) = Number(arguments, MacLogSearchTool.PidArgument, 0, int.MaxValue, null);
        if (pidError is not null)
        {
            return (null, [], pidError);
        }

        if (Optional(arguments, MacLogSearchTool.TextArgument) is { Length: > ArgumentText.MaxTextChars })
        {
            return (null, [], ArgumentText.TooLong(MacLogSearchTool.TextArgument, ArgumentText.MaxTextChars));   // K6, as the event logs'
        }

        var terms = new MacLogTerms(
            Optional(arguments, MacLogSearchTool.LevelArgument),
            since,
            until,
            Optional(arguments, MacLogSearchTool.ProcessArgument),
            pid is { } p ? (int)p : null,
            Optional(arguments, MacLogSearchTool.SubsystemArgument),
            Optional(arguments, MacLogSearchTool.CategoryArgument),
            Optional(arguments, MacLogSearchTool.TextArgument),
            MacLogQuery.ParseList(Optional(arguments, MacLogSearchTool.ExcludeArgument)));
        return terms.QueryFor(source, Access.Time.GetUtcNow(), limit, before);
    }
}
