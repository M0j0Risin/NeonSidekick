using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>date_difference(from, to)</c>: the signed day count between two dates, with weeks alongside
/// once there are any, and years, months and days once the gap is a month or more. "How many days until Christmas" is
/// <c>from: today, to: 2026-12-25</c>. <c>days_between</c> until 2026-10-05, renamed with its years-and-months answer (the
/// user's call: "how old is …" and "how many months since …" left the model turning thousands of days into years by hand,
/// and the old name said days only); <c>shift_date</c> moves a date, this one measures between two.
/// </summary>
public sealed class DateDifferenceTool : AIFunction
{
    public const string ToolName = "date_difference";

    public const string FromArgument = "from";
    public const string ToArgument = "to";

    /// <summary>What joins the day count to the calendar span (<see cref="Describe"/>). Pinned.</summary>
    public const string SpanJoiner = ", which is ";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "from": {
              "type": "string",
              "description": "The first date: today, or a date as yyyy-MM-dd."
            },
            "to": {
              "type": "string",
              "description": "The second date: today, or a date as yyyy-MM-dd."
            }
          },
          "required": ["from", "to"]
        }
        """);

    private readonly TimeProvider _time;

    public DateDifferenceTool(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public override string Name => ToolName;

    public override string Description =>
        "Counts the days from one date to another (negative when the second date is earlier), and in years, months and days " +
        "once the gap is a month or more. Use it for \"how many days until\", \"how long since\" and \"how old\" questions " +
        "instead of counting yourself.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>
    /// <c>105 days (15 weeks) from 2026-09-11 to 2026-12-25, which is 3 months and 14 days</c>; <c>(2 weeks and 3 days)</c>
    /// with a remainder; no parenthesis under a week; <c>-3 days from … to …</c> backwards. The calendar span
    /// (<see cref="CalendarSpan"/>, 2026-10-05) only once it has a month or a year: under a month the weeks already say it.
    /// Its amounts are the gap's size whichever way the dates run; the sign stays on the day count. Pinned.
    /// </summary>
    public static string Describe(DateOnly from, DateOnly to)
    {
        int days = to.DayNumber - from.DayNumber;
        string text = ClockText.Count(days, "day");
        int magnitude = Math.Abs(days);
        if (magnitude >= 7)
        {
            int weeks = magnitude / 7;
            int rest = magnitude % 7;
            text += " (" + ClockText.Count(weeks, "week") + (rest == 0 ? "" : " and " + ClockText.Count(rest, "day")) + ")";
        }

        text += " from " + ClockText.Iso(from) + " to " + ClockText.Iso(to);
        var (years, months, spanDays) = CalendarSpan(from, to);
        return years == 0 && months == 0 ? text : text + SpanJoiner + ClockText.Amounts(years, months, 0, spanDays);
    }

    /// <summary>
    /// The whole years, months and days from the earlier of <paramref name="from"/> and <paramref name="to"/> to the later
    /// (2026-10-05): the most months whose step from the earlier date does not pass the later one, then the days left. A step
    /// is <see cref="ShiftDateTool"/>'s own (the years, then the months, a month-end clamping), so shifting the earlier date by
    /// the span lands on the later one (31 January to 28 February is 1 month; 29 February 2024 to 28 February 2025 is 1 year).
    /// The months between the two dates' months is the most there can be, and one fewer always fits. Pure.
    /// </summary>
    public static (int Years, int Months, int Days) CalendarSpan(DateOnly from, DateOnly to)
    {
        var (start, end) = from <= to ? (from, to) : (to, from);
        int total = ((end.Year - start.Year) * 12) + (end.Month - start.Month);
        if (Step(start, total) > end)
        {
            total--;
        }

        return (total / 12, total % 12, end.DayNumber - Step(start, total).DayNumber);

        static DateOnly Step(DateOnly date, int months) => date.AddYears(months / 12).AddMonths(months % 12);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new ValueTask<object?>(Run(arguments));
    }

    private string Run(AIFunctionArguments arguments)
    {
        var today = ClockText.LocalDate(_time);
        string fromText = ToolArguments.ReadString(arguments, FromArgument);
        if (!ClockText.TryParseDate(fromText, today, out var from))
        {
            return ClockText.BadDate(FromArgument, fromText);
        }

        string toText = ToolArguments.ReadString(arguments, ToArgument);
        if (!ClockText.TryParseDate(toText, today, out var to))
        {
            return ClockText.BadDate(ToArgument, toText);
        }

        return Describe(from, to);
    }
}
