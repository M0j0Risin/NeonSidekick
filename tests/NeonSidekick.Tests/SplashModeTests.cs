using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class SplashModeTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder_FullsizeTheDefault()
    {
        // 2026-09-24: the on/off Welcome splash became three words; on was fullsize (the user's name over "normal").
        Assert.Equal(new[] { "fullsize", "tiled", "disabled" }, SplashMode.Names);
        Assert.Equal("fullsize", SplashMode.Default);
        Assert.Equal(SplashMode.Default, new AppSettingsData().WelcomeSplashMode);
    }

    [Theory]
    [InlineData("fullsize", SplashStyle.FullSize)]
    [InlineData("tiled", SplashStyle.Tiled)]
    [InlineData("disabled", SplashStyle.Disabled)]
    [InlineData("  Tiled ", SplashStyle.Tiled)]
    public void TryParse_TrimsAndIgnoresCase_AndNameRoundTrips(string text, SplashStyle expected)
    {
        Assert.True(SplashMode.TryParse(text, out var style));
        Assert.Equal(expected, style);
        Assert.Equal(text.Trim().ToLowerInvariant(), SplashMode.Name(style));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("normal")]
    [InlineData("on")]
    [InlineData("off")]
    public void TryParse_RejectsAnythingElse_AndHandsBackFullSize(string? text)
    {
        Assert.False(SplashMode.TryParse(text, out var style));
        Assert.Equal(SplashStyle.FullSize, style);
    }

    [Fact]
    public void Describe_IsPinned()
    {
        Assert.Equal("one picture fills the screen under the banner at startup; ← → walk them", SplashMode.Describe("fullsize"));
        Assert.Equal("the pictures as thumbnails at the image thumbnail size, a screenful at a time; ← → page", SplashMode.Describe("tiled"));
        Assert.Equal("the banner alone at startup", SplashMode.Describe("disabled"));
        Assert.Equal("", SplashMode.Describe("other"));
    }

    [Fact]
    public void Resolve_MapsTheSavedMode()
    {
        Assert.Equal(SplashStyle.FullSize, SplashMode.Resolve(new AppSettingsData()));
        Assert.Equal(SplashStyle.Tiled, SplashMode.Resolve(new AppSettingsData { WelcomeSplashMode = "TILED" }));
        Assert.Equal(SplashStyle.Disabled, SplashMode.Resolve(new AppSettingsData { WelcomeSplashMode = "disabled" }));
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Splash" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(SplashStyle.FullSize, SplashMode.Resolve(new AppSettingsData { WelcomeSplashMode = "normal" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Contains("WelcomeSplashMode='normal' is not one of fullsize, tiled, disabled. Using fullsize.", warning.Message);
    }
}
