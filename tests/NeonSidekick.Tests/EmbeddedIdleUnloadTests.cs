using NeonSidekick.Diagnostics;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>The <c>Embedded idle unload (min)</c> setting's one reader (2026-11-16, plan lucid-roaming-lantern #44): off by default, never clamped.</summary>
public class EmbeddedIdleUnloadTests
{
    [Fact]
    public void Constants_ArePinned()
    {
        Assert.Equal(0, EmbeddedIdleUnload.Default);
        Assert.Equal(0, EmbeddedIdleUnload.Min);
        Assert.Equal(1440, EmbeddedIdleUnload.Max);
        Assert.Equal(EmbeddedIdleUnload.Default, new AppSettingsData().EmbeddedIdleUnloadMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(1440)]
    public void Resolve_ReadsAValueInRange_AsMinutes(int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), EmbeddedIdleUnload.Resolve(new AppSettingsData { EmbeddedIdleUnloadMinutes = minutes }));
    }

    [Theory]
    [InlineData(1441)]
    [InlineData(-1)]
    public void Resolve_OutOfRange_WarnsAndReadsAsOff(int minutes)
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "EmbeddedLlm" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(TimeSpan.Zero, EmbeddedIdleUnload.Resolve(new AppSettingsData { EmbeddedIdleUnloadMinutes = minutes }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Equal($"EmbeddedIdleUnloadMinutes={minutes} is not 0 to 1440. Using 0 (off).", warning.Message);
    }
}
