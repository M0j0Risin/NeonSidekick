using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>The setting <c>Queue mode</c> (2026-10-09, the user's call: the switch <c>Queue messages</c> became three words).</summary>
public class QueueModeTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder()
    {
        Assert.Equal(new[] { "automatic", "manual", "off" }, QueueMode.Names);
        Assert.Equal("automatic", QueueMode.Default);   // what Queue messages on did
        Assert.Equal(QueueMode.Default, new AppSettingsData().QueueMode);
    }

    [Theory]
    [InlineData("automatic", Queueing.Automatic)]
    [InlineData("manual", Queueing.Manual)]
    [InlineData("off", Queueing.Disabled)]
    [InlineData("  Manual ", Queueing.Manual)]
    public void TryParse_TrimsAndIgnoresCase(string text, Queueing expected)
    {
        Assert.True(QueueMode.TryParse(text, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("on")]
    [InlineData("never")]
    public void TryParse_RejectsAnythingElse_AsAutomatic(string? text)
    {
        Assert.False(QueueMode.TryParse(text, out var mode));
        Assert.Equal(Queueing.Automatic, mode);
    }

    [Fact]
    public void Name_RoundTripsEveryMode_AndEveryModeHasAHint()
    {
        foreach (var name in QueueMode.Names)
        {
            Assert.True(QueueMode.TryParse(name, out var mode));
            Assert.Equal(name, QueueMode.Name(mode));
            Assert.NotEqual("", QueueMode.Describe(name));
        }

        Assert.Equal("messages sent during a reply are queued and sent when it ends", QueueMode.Describe("automatic"));
        Assert.Equal("messages are always queued and sent one at a time with ➤ send on /queue", QueueMode.Describe("manual"));
        Assert.Equal("no queue: a line sent during a reply goes when it ends, unlisted", QueueMode.Describe("off"));
        Assert.Equal("", QueueMode.Describe("on"));
    }

    [Fact]
    public void Resolve_MapsTheSavedMode()
    {
        Assert.Equal(Queueing.Manual, QueueMode.Resolve(new AppSettingsData { QueueMode = "manual" }));
        Assert.Equal(Queueing.Disabled, QueueMode.Resolve(new AppSettingsData { QueueMode = "Disabled" }));
        Assert.Equal(Queueing.Automatic, QueueMode.Resolve(new AppSettingsData()));
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Screen" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(Queueing.Automatic, QueueMode.Resolve(new AppSettingsData { QueueMode = "on" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Contains("QueueMode='on' is not one of automatic, manual, off. Using automatic.", warning.Message);
    }
}
