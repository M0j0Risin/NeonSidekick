using NeonSidekick.Timers;

namespace NeonSidekick.Tests;

public class TimerTextTests
{
    private static readonly DateTimeOffset Due = new(2026, 9, 11, 14, 15, 30, TimeSpan.FromHours(-7));

    private static TimerSnapshot Running(string name, int durationSeconds, int remainingSeconds) =>
        new(name, TimeSpan.FromSeconds(durationSeconds), TimeSpan.FromSeconds(remainingSeconds), Due, Ringing: false);

    private static TimerSnapshot Ringing(string name) => new(name, TimeSpan.FromMinutes(10), TimeSpan.Zero, Due, Ringing: true);

    [Theory]
    [InlineData("10m", 600)]
    [InlineData("90s", 90)]
    [InlineData("1h30m", 5400)]
    [InlineData("1h 30m", 5400)]
    [InlineData("2h", 7200)]
    [InlineData("2 hours", 7200)]
    [InlineData("5 min", 300)]
    [InlineData("1 hour 5 minutes 3 seconds", 3903)]
    [InlineData("30s 1m", 90)]
    [InlineData("10", 600)]
    [InlineData("  10  ", 600)]
    [InlineData("1H", 3600)]
    public void TryParseDuration_Accepts(string text, int seconds)
    {
        Assert.True(TimerText.TryParseDuration(text, out var duration));
        Assert.Equal(TimeSpan.FromSeconds(seconds), duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("0m")]
    [InlineData("ten minutes")]
    [InlineData("10x")]
    [InlineData("10m 5m")]
    [InlineData("m")]
    [InlineData("1.5h")]
    [InlineData("-5m")]
    [InlineData("1234567")]
    [InlineData("10m cooking")]
    [InlineData("2d")]
    [InlineData("1 day")]
    public void TryParseDuration_Refuses(string text)
    {
        Assert.False(TimerText.TryParseDuration(text, out var duration));
        Assert.Equal(TimeSpan.Zero, duration);
    }

    /// <summary>Days are opt-in (2026-09-21, for <c>/sessions purge older</c>); a bare number is still minutes and zero still refused.</summary>
    [Theory]
    [InlineData("2d", 172800)]
    [InlineData("1 day", 86400)]
    [InlineData("1d 6h", 108000)]
    [InlineData("1D2H3M4S", 93784)]
    [InlineData("10", 600)]
    public void TryParseDuration_WithDays_Accepts(string text, int seconds)
    {
        Assert.True(TimerText.TryParseDuration(text, withDays: true, out var duration));
        Assert.Equal(TimeSpan.FromSeconds(seconds), duration);
    }

    [Theory]
    [InlineData("0d")]
    [InlineData("1d 1d")]
    [InlineData("1w")]
    [InlineData("d")]
    public void TryParseDuration_WithDays_Refuses(string text)
    {
        Assert.False(TimerText.TryParseDuration(text, withDays: true, out var duration));
        Assert.Equal(TimeSpan.Zero, duration);
    }

    [Theory]
    [InlineData(600, "10 minutes", "10 minute", "10:00")]
    [InlineData(5400, "1 hour 30 minutes", "1 hour 30 minute", "01:30:00")]
    [InlineData(90, "1 minute 30 seconds", "1 minute 30 second", "01:30")]
    [InlineData(1, "1 second", "1 second", "00:01")]
    [InlineData(0, "0 seconds", "0 second", "00:00")]
    [InlineData(3600, "1 hour", "1 hour", "01:00:00")]
    [InlineData(3601, "1 hour 1 second", "1 hour 1 second", "01:00:01")]
    [InlineData(11415, "3 hours 10 minutes 15 seconds", "3 hour 10 minute 15 second", "03:10:15")]
    [InlineData(86400, "24 hours", "24 hour", "24:00:00")]
    public void Describe_DefaultName_Countdown_ArePinned(int seconds, string described, string name, string countdown)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        Assert.Equal(described, TimerText.Describe(duration));
        Assert.Equal(name, TimerText.DefaultName(duration));
        Assert.Equal(countdown, TimerText.Countdown(duration));
    }

    [Theory]
    [InlineData(" cooking ", "cooking")]
    [InlineData("the  big\tpot", "the big pot")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    public void NormalizeName_IsPinned(string raw, string normalized) => Assert.Equal(normalized, TimerText.NormalizeName(raw));

    [Theory]
    [InlineData("cooking", true)]
    [InlineData("Cooking Pot", true)]
    [InlineData("", false)]
    [InlineData("all", false)]
    [InlineData("All", false)]
    [InlineData("0123456789012345678901234567890123456789", true)]
    [InlineData("01234567890123456789012345678901234567890", false)]
    public void IsValidName_IsPinned(string name, bool valid) => Assert.Equal(valid, TimerText.IsValidName(name));

    [Fact]
    public void ToolSentences_ArePinned()
    {
        Assert.Equal("started the cooking timer: 10 minutes, done at 14:15", TimerText.Started(Running("cooking", 600, 600)));
        Assert.Equal("Error: a timer called 'cooking' is already running (7 minutes 12 seconds left); stop it first or use another name", TimerText.Duplicate(Running("cooking", 600, 432)));
        Assert.Equal("Error: a timer called 'cooking' is done and ringing; stop it first or use another name", TimerText.Duplicate(Ringing("cooking")));
        Assert.Equal("Error: too many timers (20); stop one first", TimerText.Full(20));
        Assert.Equal("Error: a timer name is 1 to 40 characters and not 'all'", TimerText.BadName);
        Assert.Equal("stopped the cooking timer with 7 minutes 12 seconds left", TimerText.Stopped(Running("cooking", 600, 432)));
        Assert.Equal("silenced the cooking timer", TimerText.Stopped(Ringing("cooking")));
        Assert.Equal("Error: no timer called 'cooking'; running: tea, eggs", TimerText.NoSuchTimer("cooking", new[] { Running("tea", 60, 30), Ringing("eggs") }));
        Assert.Equal("Error: no timer called 'cooking'; no timers are running", TimerText.NoSuchTimer("cooking", Array.Empty<TimerSnapshot>()));
        Assert.Equal("no timers are running", TimerText.List(Array.Empty<TimerSnapshot>()));
        Assert.Equal(
            "cooking: 7 minutes 12 seconds left of 10 minutes (done at 14:15); tea: done, ringing",
            TimerText.List(new[] { Running("cooking", 600, 432), Ringing("tea") }));
    }

    [Fact]
    public void AlertWording_IsPinned()
    {
        var first = new TimerAlert("cooking", TimeSpan.FromMinutes(10), TimeSpan.Zero, 0);
        var repeat = new TimerAlert("cooking", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2), 2);

        Assert.Equal("cooking timer done (10 minutes) — press Enter to silence", TimerText.AlertLine(first));
        Assert.Equal("cooking timer still ringing (2 minutes) — press Enter to silence", TimerText.AlertLine(repeat));
        Assert.Equal("The cooking timer is done.", TimerText.AlertSpeech(first));
        Assert.Equal("The cooking timer is still ringing.", TimerText.AlertSpeech(repeat));
    }

    [Theory]
    [InlineData("tea", "tea")]
    [InlineData("0123456789", "0123456789")]
    [InlineData("01234567890", "012345678…")]
    [InlineData("the big pot of soup", "the big p…")]
    [InlineData("日本語日本語", "日本語日…")]
    [InlineData("🍳🍳🍳🍳🍳🍳", "🍳🍳🍳🍳…")]
    public void StatusName_CutsAtTenCells(string name, string shown)
    {
        Assert.Equal(shown, TimerText.StatusName(name));
        Assert.True(NeonSidekick.UI.TextCells.Width(shown) <= TimerText.StatusNameCells);
    }

    [Fact]
    public void ScreenLines_ArePinned()
    {
        Assert.Null(TimerText.StatusLine(Array.Empty<TimerSnapshot>()));
        Assert.Equal("⏰ cooking 07:12 · tea ringing", TimerText.StatusLine(new[] { Running("cooking", 600, 432), Ringing("tea") }));
        Assert.Equal(2, NeonSidekick.UI.TextCells.Width(TimerText.StatusGlyph));
        Assert.Equal("⏰ the big p… 07:12 · 0123456789 ringing", TimerText.StatusLine(new[] { Running("the big pot", 600, 432), Ringing("0123456789") }));
        Assert.Equal("(⏰ no timers)", TimerText.StoppedAll(0));
        Assert.Equal("(⏰ stopped 1 timer)", TimerText.StoppedAll(1));
        Assert.Equal("(⏰ stopped 2 timers)", TimerText.StoppedAll(2));
    }
}
