using System.Globalization;
using Microsoft.Extensions.AI;
using NeonSidekick.EventLog;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the four event-log tools share (2026-10-15, the event-log plan, <see cref="DockerTool"/>'s shape): the
/// <see cref="EventLogAccess"/> door, the settings in force at each call, the arguments' readers, and the redacted twin. A tool built
/// with <c>forceRedacted</c> reads every computer redacted whatever its <c>detail</c> — the bots' (<c>BotGeneralTools</c>), the advisors'
/// (<see cref="NeonAdvisorTool.ReadOnlyOf"/>) and a script's through <c>execute_code</c>'s bridge — so <c>full</c> stays the main chat's
/// alone. Every tool reads; none changes anything. A refusal is an <c>Error:</c> sentence, never a throw.
/// </summary>
public abstract class EventLogTool : AIFunction
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

    /// <summary>The zone an answer's times are shown in, the user's own.</summary>
    protected TimeZoneInfo Zone => Access.Time.LocalTimeZone;

    /// <summary>The settings' scan limit, clamped.</summary>
    protected int ScanLimit => Math.Clamp(Effective().EventLogScanLimit, AppSettingsData.MinEventLogScanLimit, AppSettingsData.MaxEventLogScanLimit);

    /// <summary>A string argument, or null when blank.</summary>
    protected static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>A boolean argument: <paramref name="fallback"/> when missing, the refusal when it is not one.</summary>
    protected static (bool Value, string? Error) Flag(AIFunctionArguments arguments, string name, bool fallback)
    {
        if (!ToolArguments.TryReadBoolean(arguments, name, out bool? value, out string raw))
        {
            return (fallback, EventLogText.NotBoolean(name, raw));
        }

        return (value ?? fallback, null);
    }

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
    /// The channel an <c>.evtx</c> holds, from its newest event (<see cref="EventLogQuery.DefaultFor"/> reads a Security export as Security);
    /// the log itself for a channel.
    /// </summary>
    protected static string ChannelOf(IEventLogSystem system, EventLogSource source, CancellationToken cancellationToken)
    {
        if (!source.IsFile)
        {
            return source.Log;
        }

        using var reader = system.Query(source.Target, source.Log, "*", cancellationToken);
        return reader.Next() is { Count: > 0 } batch ? batch[0].System.Channel : "";
    }
}
