using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class DateDifferenceToolTests
{
    private static readonly DateOnly Today = new(2026, 9, 11);
    private static readonly DateOnly Christmas = new(2026, 12, 25);

    private readonly DateDifferenceTool _tool = new(new ManualTimeProvider());

    private static AIFunctionArguments Args(object? from, object? to) =>
        new(new Dictionary<string, object?> { ["from"] = from, ["to"] = to });

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Schema_HasTwoRequiredStringProperties_MatchingTheKeysRead()
    {
        Assert.Equal("date_difference", DateDifferenceTool.ToolName);
        Assert.Equal(DateDifferenceTool.ToolName, _tool.Name);
        Assert.Contains("negative when the second date is earlier", _tool.Description);
        Assert.Contains("years, months and days", _tool.Description);

        var schema = _tool.JsonSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var properties = schema.GetProperty("properties");
        Assert.Equal(new[] { DateDifferenceTool.FromArgument, DateDifferenceTool.ToArgument }, properties.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("string", properties.GetProperty("from").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("to").GetProperty("type").GetString());
        Assert.Equal(new[] { "from", "to" }, schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void Describe_IsPinned()
    {
        Assert.Equal("105 days (15 weeks) from 2026-09-11 to 2026-12-25, which is 3 months and 14 days", DateDifferenceTool.Describe(Today, Christmas));
        Assert.Equal("-105 days (15 weeks) from 2026-12-25 to 2026-09-11, which is 3 months and 14 days", DateDifferenceTool.Describe(Christmas, Today));
        Assert.Equal("0 days from 2026-09-11 to 2026-09-11", DateDifferenceTool.Describe(Today, Today));
        Assert.Equal("1 day from 2026-09-11 to 2026-09-12", DateDifferenceTool.Describe(Today, Today.AddDays(1)));
        Assert.Equal("6 days from 2026-09-11 to 2026-09-17", DateDifferenceTool.Describe(Today, Today.AddDays(6)));
        Assert.Equal("7 days (1 week) from 2026-09-11 to 2026-09-18", DateDifferenceTool.Describe(Today, Today.AddDays(7)));
        Assert.Equal("17 days (2 weeks and 3 days) from 2026-09-11 to 2026-09-28", DateDifferenceTool.Describe(Today, Today.AddDays(17)));
        Assert.Equal("-8 days (1 week and 1 day) from 2026-09-11 to 2026-09-03", DateDifferenceTool.Describe(Today, Today.AddDays(-8)));
        Assert.Equal("366 days (52 weeks and 2 days) from 2027-09-11 to 2028-09-11, which is 1 year", DateDifferenceTool.Describe(new DateOnly(2027, 9, 11), new DateOnly(2028, 9, 11)));
        // The calendar span (2026-10-05): a birthday, exactly a month (no days part), and under a month none at all.
        Assert.Equal("14450 days (2064 weeks and 2 days) from 1987-03-14 to 2026-10-05, which is 39 years, 6 months and 21 days", DateDifferenceTool.Describe(new DateOnly(1987, 3, 14), new DateOnly(2026, 10, 5)));
        Assert.Equal("30 days (4 weeks and 2 days) from 2026-09-11 to 2026-10-11, which is 1 month", DateDifferenceTool.Describe(Today, new DateOnly(2026, 10, 11)));
        Assert.Equal("29 days (4 weeks and 1 day) from 2026-09-11 to 2026-10-10", DateDifferenceTool.Describe(Today, new DateOnly(2026, 10, 10)));
        Assert.Equal(", which is ", DateDifferenceTool.SpanJoiner);
    }

    /// <summary>
    /// The calendar span (2026-10-05): whole months from the earlier date, then days; a month-end and a leap day clamp as
    /// <c>shift_date</c> does, and the order of the dates does not matter.
    /// </summary>
    [Fact]
    public void CalendarSpan_CountsWholeMonthsThenDays_ClampingAsShiftDateDoes()
    {
        Assert.Equal((0, 3, 14), DateDifferenceTool.CalendarSpan(Today, Christmas));
        Assert.Equal((0, 3, 14), DateDifferenceTool.CalendarSpan(Christmas, Today));
        Assert.Equal((0, 0, 0), DateDifferenceTool.CalendarSpan(Today, Today));
        Assert.Equal((0, 1, 0), DateDifferenceTool.CalendarSpan(new DateOnly(2027, 1, 31), new DateOnly(2027, 2, 28)));
        Assert.Equal((0, 0, 27), DateDifferenceTool.CalendarSpan(new DateOnly(2027, 1, 31), new DateOnly(2027, 2, 27)));
        Assert.Equal((0, 1, 1), DateDifferenceTool.CalendarSpan(new DateOnly(2027, 1, 31), new DateOnly(2027, 3, 1)));
        Assert.Equal((1, 0, 0), DateDifferenceTool.CalendarSpan(new DateOnly(2024, 2, 29), new DateOnly(2025, 2, 28)));
        Assert.Equal((1, 1, 0), DateDifferenceTool.CalendarSpan(new DateOnly(2024, 2, 29), new DateOnly(2025, 3, 28)));
        Assert.Equal((39, 6, 21), DateDifferenceTool.CalendarSpan(new DateOnly(1987, 3, 14), new DateOnly(2026, 10, 5)));
        Assert.Equal((0, 11, 30), DateDifferenceTool.CalendarSpan(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
    }

    /// <summary>Shifting the earlier date by the span with <c>shift_date</c>'s arithmetic lands on the later one, every pair over three years.</summary>
    [Fact]
    public void CalendarSpan_ShiftedBack_LandsOnTheLaterDate()
    {
        var first = new DateOnly(2023, 12, 25);
        for (int a = 0; a < 3 * 366; a += 3)
        {
            for (int b = a; b < 3 * 366; b += 7)
            {
                var (from, to) = (first.AddDays(a), first.AddDays(b));
                var (years, months, days) = DateDifferenceTool.CalendarSpan(from, to);
                Assert.True(months is >= 0 and < 12 && days >= 0);
                Assert.StartsWith(ClockText.Iso(to) + " is a ", ShiftDateTool.Describe(from, years, months, 0, days));
                Assert.True(from.AddYears(years).AddMonths(months + 1) > to);   // the most whole months: one more passes it
            }
        }
    }

    [Fact]
    public async Task Invoke_WithJsonArguments_CountsFromToday()
    {
        object? answer = await _tool.InvokeAsync(Args(Json("\"today\""), Json("\"2026-12-25\"")), CancellationToken.None);

        Assert.Equal("105 days (15 weeks) from 2026-09-11 to 2026-12-25, which is 3 months and 14 days", answer);
    }

    [Fact]
    public async Task Invoke_WithStringArguments_CountsBetweenTwoDates()
    {
        object? answer = await _tool.InvokeAsync(Args("2026-01-01", " today "), CancellationToken.None);

        Assert.Equal("253 days (36 weeks and 1 day) from 2026-01-01 to 2026-09-11, which is 8 months and 10 days", answer);
    }

    [Fact]
    public async Task Invoke_WithABadDate_NamesTheArgument()
    {
        Assert.Equal("Error: 'christmas' is not a date for 'to'; use today or yyyy-MM-dd", await _tool.InvokeAsync(Args("today", "christmas"), CancellationToken.None));
        Assert.Equal("Error: '' is not a date for 'from'; use today or yyyy-MM-dd", await _tool.InvokeAsync(Args(null, "today"), CancellationToken.None));
        Assert.Equal("Error: '' is not a date for 'from'; use today or yyyy-MM-dd", await _tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None));
    }
}
