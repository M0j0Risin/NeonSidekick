using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class CompactTypeTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder()
    {
        Assert.Equal(new[] { "summary", "prune" }, CompactType.Names);
        Assert.Equal("summary", CompactType.Default);
        Assert.Equal(CompactType.Default, new AppSettingsData().LlmCompactType);
    }

    [Theory]
    [InlineData("summary", CompactMode.Summary)]
    [InlineData("prune", CompactMode.Prune)]
    [InlineData("  Prune ", CompactMode.Prune)]
    public void TryParse_TrimsAndIgnoresCase(string text, CompactMode expected)
    {
        Assert.True(CompactType.TryParse(text, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("both")]
    [InlineData("summarize")]
    public void TryParse_RejectsAnythingElse(string? text)
    {
        Assert.False(CompactType.TryParse(text, out _));
    }

    [Fact]
    public void Name_RoundTripsEveryType_AndEveryTypeHasAHint()
    {
        foreach (var name in CompactType.Names)
        {
            Assert.True(CompactType.TryParse(name, out var mode));
            Assert.Equal(name, CompactType.Name(mode));
            Assert.NotEqual("", CompactType.Describe(name));
        }

        Assert.Equal("summarize the older turns into one message, keep the recent ones", CompactType.Describe("summary"));
        Assert.Equal("stub the bulky tool results and call arguments in the older turns, keep every turn", CompactType.Describe("prune"));
        Assert.Equal("", CompactType.Describe("both"));
    }

    [Fact]
    public void Resolve_MapsTheSavedType()
    {
        Assert.Equal(CompactMode.Prune, CompactType.Resolve(new AppSettingsData { LlmCompactType = "prune" }));
        Assert.Equal(CompactMode.Summary, CompactType.Resolve(new AppSettingsData()));
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Llm" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(CompactMode.Summary, CompactType.Resolve(new AppSettingsData { LlmCompactType = "both" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Contains("CompactType='both' is not one of summary, prune. Using summary.", warning.Message);
    }
}
