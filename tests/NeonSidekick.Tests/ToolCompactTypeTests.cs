using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class ToolCompactTypeTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder()
    {
        Assert.Equal(new[] { "compact", "prune", "stop", "nothing" }, ToolCompactType.Names);
        Assert.Equal("compact", ToolCompactType.Default);
        Assert.Equal(ToolCompactType.Default, new AppSettingsData().LlmToolCompactType);
    }

    [Theory]
    [InlineData("compact", ToolCompactMode.Compact)]
    [InlineData("prune", ToolCompactMode.Prune)]
    [InlineData("stop", ToolCompactMode.Stop)]
    [InlineData(" COMPACT", ToolCompactMode.Compact)]
    [InlineData("nothing", ToolCompactMode.Nothing)]
    [InlineData("  Stop ", ToolCompactMode.Stop)]
    public void TryParse_TrimsAndIgnoresCase(string text, ToolCompactMode expected)
    {
        Assert.True(ToolCompactType.TryParse(text, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("eager")]
    [InlineData("summary")]
    public void TryParse_RejectsAnythingElse(string? text)
    {
        Assert.False(ToolCompactType.TryParse(text, out _));
    }

    [Fact]
    public void Name_RoundTripsEveryType_AndEveryTypeHasAHint()
    {
        foreach (var name in ToolCompactType.Names)
        {
            Assert.True(ToolCompactType.TryParse(name, out var mode));
            Assert.Equal(name, ToolCompactType.Name(mode));
            Assert.NotEqual("", ToolCompactType.Describe(name));
        }

        Assert.Equal("prune, then summarise if the turn is still over the share", ToolCompactType.Describe("compact"));
        Assert.Equal("stub this turn's older tool results and carry on", ToolCompactType.Describe("prune"));
        Assert.Equal("end the turn with a notice; /compact or /clear first", ToolCompactType.Describe("stop"));
        Assert.Equal("no check; the server's own limit answers", ToolCompactType.Describe("nothing"));
        Assert.Equal("", ToolCompactType.Describe("eager"));
    }

    [Fact]
    public void Resolve_MapsTheSavedType()
    {
        Assert.Equal(ToolCompactMode.Stop, ToolCompactType.Resolve(new AppSettingsData { LlmToolCompactType = "stop" }));
        Assert.Equal(ToolCompactMode.Nothing, ToolCompactType.Resolve(new AppSettingsData { LlmToolCompactType = "nothing" }));
        Assert.Equal(ToolCompactMode.Prune, ToolCompactType.Resolve(new AppSettingsData { LlmToolCompactType = "prune" }));
        Assert.Equal(ToolCompactMode.Compact, ToolCompactType.Resolve(new AppSettingsData()));
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Llm" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(ToolCompactMode.Compact, ToolCompactType.Resolve(new AppSettingsData { LlmToolCompactType = "eager" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Contains("LlmToolCompactType='eager' is not one of compact, prune, stop, nothing. Using compact.", warning.Message);
    }
}
