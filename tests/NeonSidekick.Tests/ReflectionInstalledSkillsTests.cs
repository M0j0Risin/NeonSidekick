using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Skills;

namespace NeonSidekick.Tests;

/// <summary><c>Reflection downloaded skills</c> (2026-10-02, the reflection audit): the cooldown mode's shape.</summary>
public class ReflectionInstalledSkillsTests
{
    [Fact]
    public void Names_Default_AndDescriptions_ArePinned()
    {
        Assert.Equal(["read-only", "allow-and-mark"], ReflectionInstalledSkills.Names);
        Assert.Equal("read-only", ReflectionInstalledSkills.Default);
        Assert.Equal(ReflectionInstalledSkills.Default, new AppSettingsData().ReflectionInstalledSkills);
        Assert.Equal("a reflection never changes a skill installed with /skills add; it may write a companion skill", ReflectionInstalledSkills.Describe("read-only"));
        Assert.Equal("a reflection may change an installed skill; an update from its source warns before replacing that", ReflectionInstalledSkills.Describe("allow-and-mark"));
        Assert.Equal("", ReflectionInstalledSkills.Describe("x"));
        Assert.Equal("read-only", ReflectionInstalledSkills.Name(ReflectionInstalledPolicy.ReadOnly));
        Assert.Equal("allow-and-mark", ReflectionInstalledSkills.Name(ReflectionInstalledPolicy.AllowAndMark));
    }

    [Theory]
    [InlineData("read-only", ReflectionInstalledPolicy.ReadOnly, true)]
    [InlineData(" Allow-And-Mark ", ReflectionInstalledPolicy.AllowAndMark, true)]
    [InlineData("always", ReflectionInstalledPolicy.ReadOnly, false)]
    [InlineData(null, ReflectionInstalledPolicy.ReadOnly, false)]
    public void TryParse_TrimsAndIgnoresCase(string? text, ReflectionInstalledPolicy expected, bool parsed)
    {
        Assert.Equal(parsed, ReflectionInstalledSkills.TryParse(text, out var policy));
        Assert.Equal(expected, policy);
    }

    [Fact]
    public void Resolve_ReadsTheSavedWord_AndAnUnknownOne_WarnsAndUsesTheDefault()
    {
        Assert.Equal(ReflectionInstalledPolicy.AllowAndMark, ReflectionInstalledSkills.Resolve(new AppSettingsData { ReflectionInstalledSkills = "allow-and-mark" }));
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == SkillCatalog.Category && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(ReflectionInstalledPolicy.ReadOnly, ReflectionInstalledSkills.Resolve(new AppSettingsData { ReflectionInstalledSkills = "always" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Equal("ReflectionInstalledSkills='always' is not one of read-only, allow-and-mark. Using read-only.", warning.Message);
    }
}
