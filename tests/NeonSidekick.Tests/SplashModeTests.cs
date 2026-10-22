using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class SplashModeTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder_OffTheDefault()
    {
        // 2026-09-24: the on/off Welcome splash became three words; on was fullsize (the user's name over "normal").
        // 2026-10-20 (the user's call): tiled retired, fullsize renamed image, robot added.
        Assert.Equal(new[] { "image", "robot", "off" }, SplashMode.Names);
        Assert.Equal("off", SplashMode.Default);   // 2026-10-21, the user's call; image before
        Assert.Equal(SplashMode.Default, new AppSettingsData().WelcomeSplashMode);
    }

    [Theory]
    [InlineData("image", SplashStyle.Image)]
    [InlineData("robot", SplashStyle.Robot)]
    [InlineData("off", SplashStyle.Disabled)]
    [InlineData("  Robot ", SplashStyle.Robot)]
    public void TryParse_TrimsAndIgnoresCase_AndNameRoundTrips(string text, SplashStyle expected)
    {
        Assert.True(SplashMode.TryParse(text, out var style));
        Assert.Equal(expected, style);
        Assert.Equal(text.Trim().ToLowerInvariant(), SplashMode.Name(style));
    }

    [Theory]
    [InlineData("fullsize", SplashStyle.Image)]
    [InlineData("tiled", SplashStyle.Image)]
    [InlineData(" TILED ", SplashStyle.Image)]
    [InlineData("disabled", SplashStyle.Disabled)]
    public void TryParse_ReadsTheRetiredWords(string text, SplashStyle expected)
    {
        Assert.True(SplashMode.TryParse(text, out var style));
        Assert.Equal(expected, style);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("normal")]
    [InlineData("on")]
    [InlineData("never")]
    public void TryParse_RejectsAnythingElse_AndHandsBackImage(string? text)
    {
        Assert.False(SplashMode.TryParse(text, out var style));
        Assert.Equal(SplashStyle.Image, style);
    }

    [Fact]
    public void Describe_IsPinned()
    {
        // The user's words (2026-10-20).
        Assert.Equal("a picture fills the transcript pane at startup", SplashMode.Describe("image"));
        Assert.Equal("an animated robot occupies the transcript pane at startup", SplashMode.Describe("robot"));
        Assert.Equal("the transcript pane is clear at startup", SplashMode.Describe("off"));
        Assert.Equal("", SplashMode.Describe("other"));
    }

    [Fact]
    public void Resolve_MapsTheSavedMode()
    {
        Assert.Equal(SplashStyle.Disabled, SplashMode.Resolve(new AppSettingsData()));
        Assert.Equal(SplashStyle.Robot, SplashMode.Resolve(new AppSettingsData { WelcomeSplashMode = "ROBOT" }));
        Assert.Equal(SplashStyle.Image, SplashMode.Resolve(new AppSettingsData { WelcomeSplashMode = "tiled" }));
        Assert.Equal(SplashStyle.Disabled, SplashMode.Resolve(new AppSettingsData { WelcomeSplashMode = "off" }));
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Splash" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(SplashStyle.Disabled, SplashMode.Resolve(new AppSettingsData { WelcomeSplashMode = "normal" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Contains("WelcomeSplashMode='normal' is not one of image, robot, off. Using off.", warning.Message);
    }
}
