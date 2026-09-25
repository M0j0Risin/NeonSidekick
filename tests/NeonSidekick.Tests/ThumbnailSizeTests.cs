using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class ThumbnailSizeTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder()
    {
        Assert.Equal(new[] { "tiny", "small", "medium", "large", "xlarge", "fullsize" }, ThumbnailSize.Names);
        Assert.Equal("fullsize", ThumbnailSize.FullSize);
        Assert.Equal("small", ThumbnailSize.Default);
        Assert.Equal(ThumbnailSize.Default, new AppSettingsData().ImageThumbnailSize);
    }

    [Fact]
    public void Boxes_ArePinned_SmallIsTheThumbnailsOwnDefault()
    {
        Assert.Equal(new ThumbnailBox(ImageThumbnail.Columns, ImageThumbnail.MaxRows), ThumbnailSize.Small);
        Assert.Equal(new ThumbnailBox(48, 12), ThumbnailSize.Small);
        Assert.Equal(new ThumbnailBox(32, 8), ThumbnailSize.Tiny);
        Assert.Equal(new ThumbnailBox(64, 16), ThumbnailSize.Medium);
        Assert.Equal(new ThumbnailBox(80, 20), ThumbnailSize.Large);
        Assert.Equal(new ThumbnailBox(96, 24), ThumbnailSize.ExtraLarge);
    }

    [Fact]
    public void Fit_IsTheWindowLessTheMarginAndTheReservedRows_NeverUnderOneByOne()
    {
        // /view's box (2026-09-17): 240 × 50 with the pane's 6 rows spoken for.
        Assert.Equal(new ThumbnailBox(238, 43), ThumbnailSize.Fit(240, 50, 6));
        Assert.Equal(new ThumbnailBox(78, 23), ThumbnailSize.Fit(80, 24, 0));
        Assert.Equal(new ThumbnailBox(1, 1), ThumbnailSize.Fit(2, 5, 5));
        Assert.Equal(new ThumbnailBox(1, 1), ThumbnailSize.Fit(0, 0, 0));
        Assert.Equal(2, ThumbnailSize.FitMargin);
    }

    [Theory]
    [InlineData("tiny", 32, 8)]
    [InlineData("small", 48, 12)]
    [InlineData("medium", 64, 16)]
    [InlineData("large", 80, 20)]
    [InlineData("xlarge", 96, 24)]
    [InlineData("  XLarge ", 96, 24)]
    public void TryParse_TrimsAndIgnoresCase(string text, int columns, int maxRows)
    {
        Assert.True(ThumbnailSize.TryParse(text, out var box));
        Assert.Equal(new ThumbnailBox(columns, maxRows), box);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("huge")]
    [InlineData("48x12")]
    [InlineData("extra large")]
    [InlineData("fullsize")]   // no fixed box: only Resolve, given the window, knows it
    public void TryParse_RejectsAnythingElse_AndHandsBackSmall(string? text)
    {
        Assert.False(ThumbnailSize.TryParse(text, out var box));
        Assert.Equal(ThumbnailSize.Small, box);
    }

    [Fact]
    public void EverySizeHasAHint()
    {
        foreach (var name in ThumbnailSize.Names)
        {
            Assert.True(ThumbnailSize.TryParse(name, out _) || ThumbnailSize.IsFullSize(name));
            Assert.NotEqual("", ThumbnailSize.Describe(name));
        }

        Assert.Equal("32 columns × 8 rows", ThumbnailSize.Describe("tiny"));
        Assert.Equal("48 columns × 12 rows", ThumbnailSize.Describe("small"));
        Assert.Equal("64 columns × 16 rows", ThumbnailSize.Describe("medium"));
        Assert.Equal("80 columns × 20 rows", ThumbnailSize.Describe("large"));
        Assert.Equal("96 columns × 24 rows", ThumbnailSize.Describe("xlarge"));
        Assert.Equal("fits the window", ThumbnailSize.Describe("fullsize"));
        Assert.Equal("", ThumbnailSize.Describe("huge"));
    }

    [Fact]
    public void Resolve_MapsTheSavedSize()
    {
        var window = new ThumbnailBox(238, 43);
        Assert.Equal(ThumbnailSize.Large, ThumbnailSize.Resolve(new AppSettingsData { ImageThumbnailSize = "large" }, window));
        Assert.Equal(ThumbnailSize.Small, ThumbnailSize.Resolve(new AppSettingsData(), window));
    }

    [Theory]
    [InlineData("fullsize")]
    [InlineData("  FullSize ")]
    public void Resolve_FullSize_IsTheWindowGiven(string saved)
    {
        Assert.True(ThumbnailSize.IsFullSize(saved));
        Assert.Equal(new ThumbnailBox(238, 43), ThumbnailSize.Resolve(new AppSettingsData { ImageThumbnailSize = saved }, new ThumbnailBox(238, 43)));
        Assert.Equal(new ThumbnailBox(78, 23), ThumbnailSize.Resolve(new AppSettingsData { ImageThumbnailSize = saved }, ThumbnailSize.Fit(80, 24, 0)));
        Assert.False(ThumbnailSize.IsFullSize("xlarge"));
        Assert.False(ThumbnailSize.IsFullSize(null));
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Image" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(ThumbnailSize.Small, ThumbnailSize.Resolve(new AppSettingsData { ImageThumbnailSize = "huge" }, new ThumbnailBox(238, 43)));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Contains("ImageThumbnailSize='huge' is not one of tiny, small, medium, large, xlarge, fullsize. Using small.", warning.Message);
    }
}
