using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Skills;

namespace NeonSidekick.Tests;

public class SkillCompactModeTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder_ProtectedTheDefault()
    {
        Assert.Equal(new[] { "protected", "unprotected" }, SkillCompactMode.Names);
        Assert.Equal("protected", SkillCompactMode.Default);
        Assert.Equal(SkillCompactMode.Default, new AppSettingsData().SkillCompactMode);
    }

    [Theory]
    [InlineData("protected", true)]
    [InlineData("unprotected", false)]
    [InlineData("  Unprotected ", false)]
    [InlineData("PROTECTED", true)]
    public void TryParse_TrimsAndIgnoresCase(string text, bool expected)
    {
        Assert.True(SkillCompactMode.TryParse(text, out bool protect));
        Assert.Equal(expected, protect);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("prune")]
    [InlineData("on")]
    public void TryParse_RejectsAnythingElse(string? text)
    {
        Assert.False(SkillCompactMode.TryParse(text, out _));
    }

    [Fact]
    public void EveryMode_HasAHint()
    {
        Assert.Equal("loaded skills are kept through a prune, during a reply too", SkillCompactMode.Describe("protected"));
        Assert.Equal("loaded skills prune like any tool result", SkillCompactMode.Describe("unprotected"));
        Assert.Equal("", SkillCompactMode.Describe("prune"));
    }

    [Fact]
    public void Resolve_MapsTheSavedMode()
    {
        Assert.True(SkillCompactMode.Resolve(new AppSettingsData()));
        Assert.False(SkillCompactMode.Resolve(new AppSettingsData { SkillCompactMode = "unprotected" }));
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == SkillCatalog.Category && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.True(SkillCompactMode.Resolve(new AppSettingsData { SkillCompactMode = "sometimes" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Equal("SkillCompactMode='sometimes' is not one of protected, unprotected. Using protected.", warning.Message);
    }
}
