using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The refusals of an admin tool's arguments, the wording every family shares (2026-10-23, the admin review's R18 and K6: copied into five
/// <c>*Text</c> classes, each echoing the argument whole — a 2 MB string came back as a 2 MB answer into the history and sessions.db). An
/// echoed argument is <see cref="Echo"/>: one line, cut to <see cref="EchoChars"/>, every character that could disguise it escaped. Pinned.
/// </summary>
public static class ArgumentText
{
    /// <summary>The most of an argument a refusal repeats.</summary>
    public const int EchoChars = 40;

    /// <summary><paramref name="raw"/> as a refusal repeats it.</summary>
    public static string Echo(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return UntrustedText.Name(UntrustedText.Cut(raw.Length > 4 * EchoChars ? raw[..(4 * EchoChars)] : raw, EchoChars));
    }

    public static string Missing(string argument) => $"Error: {argument} is required.";

    /// <summary>The longest text term a search takes (2026-10-23, K6).</summary>
    public const int MaxTextChars = 256;

    public static string TooLong(string argument, int max) => $"Error: {argument} is longer than {max.ToString(CultureInfo.InvariantCulture)} characters.";

    public static string NotBoolean(string argument, string raw) => $"Error: {argument} '{Echo(raw)}' is not true or false.";

    public static string OutOfRange(string argument, string given, long min, long max) =>
        $"Error: {argument} '{Echo(given)}' is out of range; it must be {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.";
}

/// <summary>
/// The admin tools' argument readers, one set for the six bases (2026-10-23, the admin review's R18: services, tasks, directory, network,
/// event logs and the Mac's logs each kept a copy, already drifted — the Mac's <c>Moment</c> kept the fraction of a second). A JSON
/// <c>null</c> is not given (E7: <c>ad_find(days=null)</c> was refused as "takes no days"). The refusals come from <see cref="ArgumentText"/>.
/// </summary>
public static class AdminArguments
{
    /// <summary>A string argument, or null when missing, null or blank.</summary>
    public static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>Whether <paramref name="name"/> was given a value: present and not JSON null (E7).</summary>
    public static bool Given(AIFunctionArguments arguments, string name)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.TryGetValue(name, out object? value) && value is not null && value is not JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };
    }

    /// <summary>A boolean argument: <paramref name="fallback"/> when missing, the refusal when it is not one.</summary>
    public static (bool Value, string? Error) Flag(AIFunctionArguments arguments, string name, bool fallback) =>
        !ToolArguments.TryReadBoolean(arguments, name, out bool? value, out string raw) ? (fallback, ArgumentText.NotBoolean(name, raw)) : (value ?? fallback, null);

    /// <summary>A whole number in <paramref name="min"/>..<paramref name="max"/>: <paramref name="fallback"/> when missing, the refusal otherwise.</summary>
    public static (long? Value, string? Error) Number(AIFunctionArguments arguments, string name, long min, long max, long? fallback)
    {
        if (!ToolArguments.TryReadInt64(arguments, name, out long? value, out string raw) || value is { } n && (n < min || n > max))
        {
            string given = raw.Length > 0 ? raw : value?.ToString(CultureInfo.InvariantCulture) ?? "";
            return (null, ArgumentText.OutOfRange(name, given, min, max));
        }

        return (value ?? fallback, null);
    }

    /// <summary><see cref="Number(AIFunctionArguments, string, long, long, long?)"/> as an <see cref="int"/> with a fallback, the shape four bases use.</summary>
    public static (int Value, string? Error) Number(AIFunctionArguments arguments, string name, int min, int max, int fallback)
    {
        var (value, error) = Number(arguments, name, (long)min, max, fallback);
        return (error is null ? (int)value!.Value : fallback, error);
    }

    /// <summary>
    /// A time argument (an age or a moment, in <paramref name="zone"/>); null when missing, the refusal (<paramref name="badTime"/>) when it is
    /// neither. Held to the whole second, a start down and an end up (2026-10-20, the event logs'; every family's since 2026-10-23, R1): the
    /// next call restates it to the second, so a page's window and the next one's differed by the fraction.
    /// </summary>
    public static (DateTimeOffset? Value, string? Error) Moment(AIFunctionArguments arguments, string name, DateTimeOffset now, TimeZoneInfo zone, bool end, Func<string, string, string> badTime)
    {
        ArgumentNullException.ThrowIfNull(badTime);
        if (Optional(arguments, name) is not { } text)
        {
            return (null, null);
        }

        return TimeArgument.Parse(text, now, zone) is { } moment ? (WholeSecond(moment, end), null) : (null, badTime(name, text));
    }

    /// <summary><paramref name="moment"/> at a whole second: down, or (<paramref name="up"/>) up when it has a fraction.</summary>
    public static DateTimeOffset WholeSecond(DateTimeOffset moment, bool up)
    {
        long fraction = moment.Ticks % TimeSpan.TicksPerSecond;
        if (fraction == 0)
        {
            return moment;
        }

        var down = moment.AddTicks(-fraction);
        return up && down < DateTimeOffset.MaxValue.AddSeconds(-1) ? down.AddSeconds(1) : down;
    }
}
