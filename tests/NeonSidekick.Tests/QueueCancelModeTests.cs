using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class QueueCancelModeTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder()
    {
        Assert.Equal(new[] { "hold", "drain", "empty" }, QueueCancelMode.Names);
        Assert.Equal("empty", QueueCancelMode.Default);   // hold until 2026-09-20, the user's call
        Assert.Equal(QueueCancelMode.Default, new AppSettingsData().QueueCancelMode);
    }

    [Theory]
    [InlineData("hold", QueueCancel.Hold)]
    [InlineData("drain", QueueCancel.Drain)]
    [InlineData("empty", QueueCancel.Empty)]
    [InlineData("  Empty ", QueueCancel.Empty)]
    public void TryParse_TrimsAndIgnoresCase(string text, QueueCancel expected)
    {
        Assert.True(QueueCancelMode.TryParse(text, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("keep")]
    [InlineData("on")]
    public void TryParse_RejectsAnythingElse_AsHold(string? text)
    {
        Assert.False(QueueCancelMode.TryParse(text, out var mode));
        Assert.Equal(QueueCancel.Hold, mode);
    }

    [Fact]
    public void Name_RoundTripsEveryMode_AndEveryModeHasAHint()
    {
        foreach (var name in QueueCancelMode.Names)
        {
            Assert.True(QueueCancelMode.TryParse(name, out var mode));
            Assert.Equal(name, QueueCancelMode.Name(mode));
            Assert.NotEqual("", QueueCancelMode.Describe(name));
        }

        Assert.Equal("a canceled reply holds the queue; your next message runs first, then it resumes", QueueCancelMode.Describe("hold"));
        Assert.Equal("a canceled reply sends the next queued message at once", QueueCancelMode.Describe("drain"));
        Assert.Equal("a canceled reply drops every queued message", QueueCancelMode.Describe("empty"));
        Assert.Equal("", QueueCancelMode.Describe("keep"));
    }

    [Fact]
    public void Resolve_MapsTheSavedMode()
    {
        Assert.Equal(QueueCancel.Empty, QueueCancelMode.Resolve(new AppSettingsData { QueueCancelMode = "empty" }));
        Assert.Equal(QueueCancel.Drain, QueueCancelMode.Resolve(new AppSettingsData { QueueCancelMode = "Drain" }));
        Assert.Equal(QueueCancel.Empty, QueueCancelMode.Resolve(new AppSettingsData()));   // the default: empty since 2026-09-20
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Screen" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(QueueCancel.Empty, QueueCancelMode.Resolve(new AppSettingsData { QueueCancelMode = "keep" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Contains("QueueCancelMode='keep' is not one of hold, drain, empty. Using empty.", warning.Message);
    }
}
