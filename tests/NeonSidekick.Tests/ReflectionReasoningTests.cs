using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Skills;

namespace NeonSidekick.Tests;

public class ReflectionReasoningTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder_NoneTheDefault()
    {
        // profile-default and the profile's level as the default until later on 2026-09-19 (the user's call: none, and the shorter word).
        Assert.Equal(new[] { "profile", "none", "low", "medium", "high", "xhigh" }, ReflectionReasoning.Names);
        Assert.Equal("none", ReflectionReasoning.Default);
        Assert.Equal("profile", ReflectionReasoning.Profile);
        Assert.Equal(ReflectionReasoning.Default, new AppSettingsData().ReflectionReasoning);
        Assert.Equal(ReasoningLevel.Levels, ReflectionReasoning.Names.Skip(1));
    }

    [Theory]
    [InlineData("profile", null)]
    [InlineData("  Profile ", null)]
    [InlineData("none", ReasoningEffort.None)]
    [InlineData("low", ReasoningEffort.Low)]
    [InlineData("medium", ReasoningEffort.Medium)]
    [InlineData("HIGH", ReasoningEffort.High)]
    [InlineData("xhigh", ReasoningEffort.ExtraHigh)]
    public void TryParse_TrimsAndIgnoresCase_NullForTheProfilesLevel(string text, ReasoningEffort? expected)
    {
        Assert.True(ReflectionReasoning.TryParse(text, out var effort));
        Assert.Equal(expected, effort);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    [InlineData("extra-high")]
    public void TryParse_RejectsAnythingElse(string? text)
    {
        Assert.False(ReflectionReasoning.TryParse(text, out var effort));
        Assert.Null(effort);
    }

    [Fact]
    public void EveryWord_HasAHint()
    {
        Assert.Equal("the profile's LLM reasoning level", ReflectionReasoning.Describe("profile"));
        Assert.Equal("thinking off", ReflectionReasoning.Describe("none"));
        Assert.Equal("maximum thinking, slowest", ReflectionReasoning.Describe("xhigh"));
        Assert.Equal("", ReflectionReasoning.Describe("default"));
        Assert.All(ReflectionReasoning.Names, n => Assert.NotEqual("", ReflectionReasoning.Describe(n)));
    }

    [Fact]
    public void Resolve_NoneUnderTheDefault_TheProfilesLevelUnderProfile_TheWordOtherwise()
    {
        Assert.Equal(ReasoningEffort.None, ReflectionReasoning.Resolve(new AppSettingsData()));
        Assert.Equal(ReasoningEffort.None, ReflectionReasoning.Resolve(new AppSettingsData { LlmReasoning = "high" }));   // the default is none whatever the profile thinks at
        Assert.Equal(ReasoningEffort.High, ReflectionReasoning.Resolve(new AppSettingsData { LlmReasoning = "high", ReflectionReasoning = "profile" }));
        Assert.Equal(ReasoningEffort.Low, ReflectionReasoning.Resolve(new AppSettingsData { LlmReasoning = "high", ReflectionReasoning = "low" }));
        Assert.Equal(ReasoningEffort.ExtraHigh, ReflectionReasoning.Resolve(new AppSettingsData { ReflectionReasoning = "xhigh" }));
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesNone()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == SkillCatalog.Category && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(ReasoningEffort.None, ReflectionReasoning.Resolve(new AppSettingsData { LlmReasoning = "medium", ReflectionReasoning = "lots" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Equal("ReflectionReasoning='lots' is not one of profile, none, low, medium, high, xhigh. Using none.", warning.Message);
    }
}
