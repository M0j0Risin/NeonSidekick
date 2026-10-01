using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class MenuHeightTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder_ThreeQuartersTheDefault()
    {
        // 2026-10-01, the user's ask: three heights, three-quarters by default.
        Assert.Equal(new[] { "half-screen", "three-quarters", "full-screen" }, MenuHeight.Names);
        Assert.Equal("three-quarters", MenuHeight.Default);
        Assert.Equal(MenuHeight.Default, new AppSettingsData().MenuMaxHeight);
    }

    [Theory]
    [InlineData("half-screen", MenuHeightStyle.Half)]
    [InlineData("three-quarters", MenuHeightStyle.ThreeQuarters)]
    [InlineData("full-screen", MenuHeightStyle.Full)]
    [InlineData("  Full-Screen ", MenuHeightStyle.Full)]
    public void TryParse_TrimsAndIgnoresCase_AndNameRoundTrips(string text, MenuHeightStyle expected)
    {
        Assert.True(MenuHeight.TryParse(text, out var style));
        Assert.Equal(expected, style);
        Assert.Equal(text.Trim().ToLowerInvariant(), MenuHeight.Name(style));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("half")]
    [InlineData("full")]
    public void TryParse_RejectsAnythingElse_AndHandsBackThreeQuarters(string? text)
    {
        Assert.False(MenuHeight.TryParse(text, out var style));
        Assert.Equal(MenuHeightStyle.ThreeQuarters, style);
    }

    [Fact]
    public void Describe_IsPinned()
    {
        Assert.Equal("a menu takes at most half the window", MenuHeight.Describe("half-screen"));
        Assert.Equal("a menu takes at most three quarters of the window", MenuHeight.Describe("three-quarters"));
        Assert.Equal("a menu grows to all but one row of the window", MenuHeight.Describe("full-screen"));
        Assert.Equal("", MenuHeight.Describe("other"));
    }

    [Fact]
    public void Resolve_UnknownWord_WarnsOnce_AndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Menu" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            // A word no other test uses: the warned word is process-wide.
            Assert.Equal(MenuHeightStyle.ThreeQuarters, MenuHeight.Resolve("most-of-it"));
            Assert.Equal(MenuHeightStyle.ThreeQuarters, MenuHeight.Resolve("most-of-it"));   // every draw reads it: one warning
            Assert.Equal(MenuHeightStyle.Half, MenuHeight.Resolve("HALF-SCREEN"));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Contains("MenuMaxHeight='most-of-it' is not one of half-screen, three-quarters, full-screen. Using three-quarters.", warning.Message);
    }

    [Theory]
    // height, input rows → half, three-quarters, full
    [InlineData(48, 0, 21, 33, 44)]     // 24 − 3, 36 − 3, 48 − 4
    [InlineData(48, 2, 19, 31, 42)]     // an open input slot counts inside the share
    [InlineData(24, 0, 9, 15, 20)]
    [InlineData(12, 0, 6, 6, 8)]        // half and three-quarters would leave 3 and 6: never under MinRows
    [InlineData(8, 0, 4, 4, 4)]         // never past MaxOverlayRows either
    public void ContentRows_IsTheShareLessTheChrome_BetweenMinRowsAndMaxOverlayRows(int height, int inputRows, int half, int threeQuarters, int full)
    {
        Assert.Equal(half, MenuHeight.ContentRows(MenuHeightStyle.Half, height, inputRows));
        Assert.Equal(threeQuarters, MenuHeight.ContentRows(MenuHeightStyle.ThreeQuarters, height, inputRows));
        Assert.Equal(full, MenuHeight.ContentRows(MenuHeightStyle.Full, height, inputRows));
        Assert.Equal(ScreenPane.MaxOverlayRows(height, inputRows), MenuHeight.ContentRows(MenuHeightStyle.Full, height, inputRows));
    }
}
