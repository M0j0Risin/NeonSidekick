using System.Globalization;
using Microsoft.Extensions.AI;
using NeonSidekick.EventLog;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the four event-log tools share on a Mac (2026-10-18, the Mac event-log plan; <see cref="EventLogTool"/>'s shape): the
/// <see cref="MacLogAccess"/> door, the settings in force at each call, the arguments' readers, and the redacted twin. They answer to
/// the Windows tools' names (<see cref="EventLogLogsTool.ToolName"/>…) and argument names where they mean the same (<c>log</c>,
/// <c>record</c>), so the rule, the bots', the advisors' and a script's twins and <c>AdvisorRecent</c>'s stand-ins work unchanged. A
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

    /// <summary>Whether this tool reads redacted whatever <c>local</c>'s detail (a bot's, an advisor's, a script's).</summary>
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
    protected static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>A whole-number argument in <paramref name="min"/>..<paramref name="max"/>: <paramref name="fallback"/> when missing, the refusal when out of range or not a number.</summary>
    protected static (long? Value, string? Error) Number(AIFunctionArguments arguments, string name, long min, long max, long? fallback)
    {
        if (!ToolArguments.TryReadInt64(arguments, name, out long? value, out string raw) || value is { } n && (n < min || n > max))
        {
            string given = raw.Length > 0 ? raw : value?.ToString(CultureInfo.InvariantCulture) ?? "";
            return (null, EventLogText.OutOfRange(name, given, (int)Math.Min(min, int.MaxValue), (int)Math.Min(max, int.MaxValue)));
        }

        return (value ?? fallback, null);
    }

    /// <summary>A time argument (an age or a moment, in the user's zone); null when missing, the refusal when it is neither.</summary>
    protected (DateTimeOffset? Value, string? Error) Moment(AIFunctionArguments arguments, string name)
    {
        if (Optional(arguments, name) is not { } text)
        {
            return (null, null);
        }

        return TimeArgument.Parse(text, Access.Time.GetUtcNow(), Zone) is { } moment ? (moment, null) : (null, EventLogText.BadTime(name, text));
    }

    /// <summary>
    /// A search's or a summary's terms read off the call (the same words both take): the levels (the default faults and errors), the
    /// window (the last hour unless <c>since</c> says), the filters; null with the refusal when one is wrong.
    /// </summary>
    protected (MacLogQuery? Query, string? Error) Terms(AIFunctionArguments arguments, string log, int limit, MacLogHandle? before, MacLogKind kind = MacLogKind.Unified)
    {
        var (levels, levelError) = kind == MacLogKind.Unified ? MacLogQuery.ParseLevels(Optional(arguments, MacLogSearchTool.LevelArgument)) : ([], null);
        if (levels is null)
        {
            return (null, levelError);
        }

        var (since, sinceError) = Moment(arguments, MacLogSearchTool.SinceArgument);
        if (sinceError is not null)
        {
            return (null, sinceError);
        }

        var (until, untilError) = Moment(arguments, MacLogSearchTool.UntilArgument);
        if (untilError is not null)
        {
            return (null, untilError);
        }

        var (pid, pidError) = Number(arguments, MacLogSearchTool.PidArgument, 0, int.MaxValue, null);
        if (pidError is not null)
        {
            return (null, pidError);
        }

        var now = Access.Time.GetUtcNow();
        return (new MacLogQuery(
            log,
            levels,
            since ?? now - WindowFor(kind),
            until,
            Optional(arguments, MacLogSearchTool.ProcessArgument),
            pid is { } p && kind != MacLogKind.Crashes ? (int)p : null,
            kind == MacLogKind.Unified ? Optional(arguments, MacLogSearchTool.SubsystemArgument) : null,
            kind == MacLogKind.Unified ? Optional(arguments, MacLogSearchTool.CategoryArgument) : null,
            Optional(arguments, MacLogSearchTool.TextArgument),
            MacLogQuery.ParseList(Optional(arguments, MacLogSearchTool.ExcludeArgument)),
            before,
            limit), null);
    }

    /// <summary>The window a call gets when it gives no <c>since</c>: an hour of the unified log, a week of reports, a day of install.log.</summary>
    public static TimeSpan WindowFor(MacLogKind kind) => kind switch
    {
        MacLogKind.Crashes => MacFileLogs.CrashWindow,
        MacLogKind.Install => MacFileLogs.InstallWindow,
        _ => MacLogQuery.DefaultWindow,
    };

    /// <summary>The terms a call gave that a log that is a file does not have (levels, subsystems, categories; a report's pid), for the answer's note.</summary>
    protected static IReadOnlyList<string> Ignored(AIFunctionArguments arguments, MacLogKind kind)
    {
        if (kind == MacLogKind.Unified)
        {
            return [];
        }

        string[] names = kind == MacLogKind.Crashes
            ? [MacLogSearchTool.LevelArgument, MacLogSearchTool.PidArgument, MacLogSearchTool.SubsystemArgument, MacLogSearchTool.CategoryArgument]
            : [MacLogSearchTool.LevelArgument, MacLogSearchTool.SubsystemArgument, MacLogSearchTool.CategoryArgument];
        return names.Where(n => arguments.TryGetValue(n, out object? value) && value?.ToString() is { } text && text.Trim().Length > 0).ToList();
    }
}
