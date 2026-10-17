using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Skills;

namespace NeonSidekick.Tests;

/// <summary><c>Reflection mode</c> (2026-10-16, the user's ask): <see cref="ReflectionInstalledSkillsTests"/>' shape.</summary>
public class ReflectionModeTests
{
    [Fact]
    public void Names_Default_AndDescriptions_ArePinned()
    {
        Assert.Equal(["create-update", "update-only"], ReflectionMode.Names);
        Assert.Equal("create-update", ReflectionMode.Default);
        Assert.Equal(ReflectionMode.Default, new AppSettingsData().ReflectionMode);
        Assert.Equal("a reflection may write a new skill or improve one that exists", ReflectionMode.Describe("create-update"));
        Assert.Equal("a reflection only improves a skill that exists; it never writes a new one", ReflectionMode.Describe("update-only"));
        Assert.Equal("", ReflectionMode.Describe("x"));
    }

    [Theory]
    [InlineData("create-update", ReflectionWriteMode.CreateUpdate, true)]
    [InlineData(" Update-Only ", ReflectionWriteMode.UpdateOnly, true)]
    [InlineData("update", ReflectionWriteMode.CreateUpdate, false)]
    [InlineData(null, ReflectionWriteMode.CreateUpdate, false)]
    public void TryParse_TrimsAndIgnoresCase(string? text, ReflectionWriteMode expected, bool parsed)
    {
        Assert.Equal(parsed, ReflectionMode.TryParse(text, out var mode));
        Assert.Equal(expected, mode);
    }

    [Fact]
    public void Resolve_ReadsTheSavedWord_AndAnUnknownOne_WarnsAndUsesTheDefault()
    {
        Assert.Equal(ReflectionWriteMode.UpdateOnly, ReflectionMode.Resolve(new AppSettingsData { ReflectionMode = "update-only" }));
        Assert.Equal(ReflectionWriteMode.CreateUpdate, ReflectionMode.Resolve(new AppSettingsData { ReflectionMode = "create-update" }));
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == SkillCatalog.Category && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(ReflectionWriteMode.CreateUpdate, ReflectionMode.Resolve(new AppSettingsData { ReflectionMode = "never" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Equal("ReflectionMode='never' is not one of create-update, update-only. Using create-update.", warning.Message);
    }
}
