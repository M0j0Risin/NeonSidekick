using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class ClockTextTests
{
    private static readonly DateOnly Today = new(2026, 9, 11);

    [Fact]
    public void FormatOffset_IsUtcWithSignAndMinutes()
    {
        Assert.Equal("UTC+09:00", ClockText.FormatOffset(TimeSpan.FromHours(9)));
        Assert.Equal("UTC-07:00", ClockText.FormatOffset(TimeSpan.FromHours(-7)));
        Assert.Equal("UTC+05:30", ClockText.FormatOffset(new TimeSpan(5, 30, 0)));
        Assert.Equal("UTC+00:00", ClockText.FormatOffset(TimeSpan.Zero));
    }

    [Fact]
    public void FormatMoment_IsPinned_AndNamesTheZoneForThatInstant()
    {
        var moment = new DateTimeOffset(2026, 9, 11, 14, 5, 30, TimeSpan.FromHours(-7));

        Assert.Equal("Friday 11 September 2026, 14:05 (Pacific Daylight Time, UTC-07:00)", ClockText.FormatMoment(moment, ManualTimeProvider.DefaultZone));
        Assert.Equal("Friday 11 September 2026, 14:05", ClockText.FormatClock(moment));
    }

    [Fact]
    public void FormatMoment_UsesTheDaylightName_WhenTheZoneSaysSo()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test Central", TimeSpan.FromHours(-6), "(UTC-06:00) Test Central", "Central Standard Time", "Central Daylight Time",
            new[]
            {
                TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                    DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 8),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1)),
            });

        var summer = TimeZoneInfo.ConvertTime(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), zone);
        var winter = TimeZoneInfo.ConvertTime(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), zone);

        Assert.Equal("Wednesday 1 July 2026, 07:00 (Central Daylight Time, UTC-05:00)", ClockText.FormatMoment(summer, zone));
        Assert.Equal("Thursday 1 January 2026, 06:00 (Central Standard Time, UTC-06:00)", ClockText.FormatMoment(winter, zone));
    }

    [Fact]
    public void FormatElsewhere_PutsBothMomentsOnOneLine()
    {
        var there = new DateTimeOffset(2026, 9, 12, 6, 5, 30, TimeSpan.FromHours(9));
        var local = new DateTimeOffset(2026, 9, 11, 14, 5, 30, TimeSpan.FromHours(-7));

        Assert.Equal(
            "Saturday 12 September 2026, 06:05 in Asia/Tokyo (UTC+09:00); local time is Friday 11 September 2026, 14:05",
            ClockText.FormatElsewhere(there, "Asia/Tokyo", local));
    }

    [Fact]
    public void Dates_FormatInvariantly()
    {
        var christmas = new DateOnly(2026, 12, 25);

        Assert.Equal("2026-12-25", ClockText.Iso(christmas));
        Assert.Equal("Friday", ClockText.Weekday(Today));
        Assert.Equal("0001-01-01", ClockText.Iso(DateOnly.MinValue));
    }

    [Theory]
    [InlineData("today", 2026, 9, 11)]
    [InlineData("  Today ", 2026, 9, 11)]
    [InlineData("TODAY", 2026, 9, 11)]
    [InlineData("2026-12-25", 2026, 12, 25)]
    [InlineData(" 2028-02-29 ", 2028, 2, 29)]
    public void TryParseDate_AcceptsTodayAndIso(string text, int year, int month, int day)
    {
        Assert.True(ClockText.TryParseDate(text, Today, out var date));
        Assert.Equal(new DateOnly(year, month, day), date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tomorrow")]
    [InlineData("2026-9-1")]
    [InlineData("25/12/2026")]
    [InlineData("2026-12-25T00:00:00")]
    [InlineData("2027-02-29")]
    [InlineData("December 25")]
    public void TryParseDate_RejectsEverythingElse(string text)
    {
        Assert.False(ClockText.TryParseDate(text, Today, out _));
    }

    [Fact]
    public void TryFindZone_ResolvesIanaNames_ThroughTheTable()
    {
        Assert.True(ClockText.TryFindZone("Asia/Tokyo", out var tokyo));
        Assert.Equal("Tokyo Standard Time", tokyo.Id);
        Assert.Equal(TimeSpan.FromHours(9), tokyo.BaseUtcOffset);

        // The current spelling as well as CLDR's old one, and any case.
        Assert.True(ClockText.TryFindZone("Asia/Kolkata", out var kolkata));
        Assert.True(ClockText.TryFindZone("asia/calcutta", out var calcutta));
        Assert.Equal(kolkata.Id, calcutta.Id);
        Assert.Equal(new TimeSpan(5, 30, 0), kolkata.BaseUtcOffset);

        Assert.True(ClockText.TryFindZone(" Europe/Paris ", out var paris));
        Assert.Equal(TimeSpan.FromHours(1), paris.BaseUtcOffset);
    }

    [Fact]
    public void TryFindZone_ResolvesWindowsIdsAndUtc()
    {
        Assert.True(ClockText.TryFindZone("Tokyo Standard Time", out var tokyo));
        Assert.Equal("Tokyo Standard Time", tokyo.Id);
        Assert.True(ClockText.TryFindZone("UTC", out var utc));
        Assert.Equal(TimeSpan.Zero, utc.BaseUtcOffset);
    }

    [Fact]
    public void TryFindZone_ResolvesACity_TheWindowsDisplayNameLists()
    {
        Assert.True(ClockText.TryFindZone("Tokyo", out var tokyo));
        Assert.Equal(TimeSpan.FromHours(9), tokyo.BaseUtcOffset);
        Assert.True(ClockText.TryFindZone("Nowhere/Tokyo", out var viaSegment));
        Assert.Equal(tokyo.Id, viaSegment.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Nowhere/Land")]
    [InlineData("Mars")]
    [InlineData("UTC+9")]
    public void TryFindZone_IsFalseForBlankAndUnknown(string text)
    {
        Assert.False(ClockText.TryFindZone(text, out _));
    }

    [Fact]
    public void ListsCity_MatchesOneWholeItemAfterTheOffset()
    {
        const string display = "(UTC+09:00) Osaka, Sapporo, Tokyo";

        Assert.True(ClockText.ListsCity(display, "Tokyo"));
        Assert.True(ClockText.ListsCity(display, "osaka"));
        Assert.False(ClockText.ListsCity(display, "Tok"));
        Assert.False(ClockText.ListsCity(display, "UTC+09:00"));
        Assert.False(ClockText.ListsCity(display, ""));
        Assert.True(ClockText.ListsCity("Coordinated Universal Time", "Coordinated Universal Time"));
    }

    [Fact]
    public void Count_Pluralises()
    {
        Assert.Equal("1 day", ClockText.Count(1, "day"));
        Assert.Equal("-1 day", ClockText.Count(-1, "day"));
        Assert.Equal("0 days", ClockText.Count(0, "day"));
        Assert.Equal("15 weeks", ClockText.Count(15, "week"));
    }

    [Fact]
    public void Amounts_ListsTheNonZeroUnits_LargestFirst()
    {
        Assert.Equal("", ClockText.Amounts(0, 0, 0, 0));
        Assert.Equal("6 weeks", ClockText.Amounts(0, 0, 6, 0));
        Assert.Equal("2 months and 3 days", ClockText.Amounts(0, 2, 0, 3));
        Assert.Equal("1 year, 2 months and 3 days", ClockText.Amounts(1, 2, 0, 3));
        Assert.Equal("1 year, 1 month, 1 week and 1 day", ClockText.Amounts(1, 1, 1, 1));
        Assert.Equal("-2 weeks and 3 days", ClockText.Amounts(0, 0, -2, 3));
    }

    [Fact]
    public void ErrorSentences_ArePinned()
    {
        Assert.Equal("Error: unknown time zone 'Mars'; give an IANA name such as Europe/Paris", ClockText.UnknownZone(" Mars "));
        Assert.Equal("Error: 'soon' is not a date for 'from'; use today or yyyy-MM-dd", ClockText.BadDate("from", "soon"));
        Assert.Equal("Error: 'many' is not a whole number for 'days'", ClockText.BadInteger("days", "many"));
    }

    [Fact]
    public void LocalNowAndDate_ComeFromTheProvider()
    {
        var time = new ManualTimeProvider();

        Assert.Equal(new DateTimeOffset(2026, 9, 11, 14, 5, 30, TimeSpan.FromHours(-7)), ClockText.LocalNow(time));
        Assert.Equal(Today, ClockText.LocalDate(time));

        // Just past local midnight: the date is the local one, not UTC's.
        time.UtcNow = new DateTimeOffset(2026, 9, 12, 6, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 9, 11), ClockText.LocalDate(time));
    }
}
