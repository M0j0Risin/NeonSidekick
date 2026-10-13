using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class LlmScanModeTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder()
    {
        // off last (2026-09-15), so the picker's saved-row arithmetic for the three scans is unchanged.
        Assert.Equal(new[] { "local", "lan", "both", "off" }, LlmScanMode.Names);
        Assert.Equal("off", LlmScanMode.Default);   // "local" until 2026-09-29 (the user's call)
        Assert.Equal(LlmScanMode.Default, new AppSettingsData().LlmScanMode);
    }

    [Theory]
    [InlineData("local", ScanScope.Local)]
    [InlineData("lan", ScanScope.Remote)]
    [InlineData("both", ScanScope.Both)]
    [InlineData("off", ScanScope.Disabled)]
    [InlineData("  Remote ", ScanScope.Remote)]
    [InlineData("DISABLED", ScanScope.Disabled)]
    public void TryParse_TrimsAndIgnoresCase(string text, ScanScope expected)
    {
        Assert.True(LlmScanMode.TryParse(text, out var scope));
        Assert.Equal(expected, scope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("network")]
    [InlineData("all")]
    [InlineData("never")]
    [InlineData("none")]
    public void TryParse_RejectsAnythingElse_AsTheDefault(string? text)
    {
        Assert.False(LlmScanMode.TryParse(text, out var scope));
        Assert.Equal(ScanScope.Disabled, scope);   // the default's, so a display agrees with Resolve (Local until 2026-09-29)
    }

    [Fact]
    public void Name_RoundTripsEveryMode_AndEveryModeHasAHint()
    {
        foreach (var name in LlmScanMode.Names)
        {
            Assert.True(LlmScanMode.TryParse(name, out var scope));
            Assert.Equal(name, LlmScanMode.Name(scope));
            Assert.NotEqual("", LlmScanMode.Describe(name));
        }

        Assert.Equal("the usual ports on this machine (127.0.0.1)", LlmScanMode.Describe("local"));
        Assert.Equal("the usual ports on every other machine on the local network", LlmScanMode.Describe("lan"));
        Assert.Equal("this machine first, then the local network", LlmScanMode.Describe("both"));
        Assert.Equal("no scan; set LLM URL by hand", LlmScanMode.Describe("off"));
        Assert.Equal("", LlmScanMode.Describe("network"));
    }

    [Fact]
    public void IncludesNetwork_IsRemoteOrBoth()
    {
        Assert.False(LlmScanMode.IncludesNetwork(ScanScope.Local));
        Assert.True(LlmScanMode.IncludesNetwork(ScanScope.Remote));
        Assert.True(LlmScanMode.IncludesNetwork(ScanScope.Both));
        Assert.False(LlmScanMode.IncludesNetwork(ScanScope.Disabled));
    }

    [Fact]
    public void Scans_IsFalseForDisabledAlone()
    {
        Assert.True(LlmScanMode.Scans(ScanScope.Local));
        Assert.True(LlmScanMode.Scans(ScanScope.Remote));
        Assert.True(LlmScanMode.Scans(ScanScope.Both));
        Assert.False(LlmScanMode.Scans(ScanScope.Disabled));
    }

    [Fact]
    public void Resolve_MapsTheSavedMode()
    {
        Assert.Equal(ScanScope.Remote, LlmScanMode.Resolve(new AppSettingsData { LlmScanMode = "lan" }));
        Assert.Equal(ScanScope.Both, LlmScanMode.Resolve(new AppSettingsData { LlmScanMode = "Both" }));
        Assert.Equal(ScanScope.Disabled, LlmScanMode.Resolve(new AppSettingsData { LlmScanMode = "off" }));
        Assert.Equal(ScanScope.Disabled, LlmScanMode.Resolve(new AppSettingsData()));
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Llm" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(ScanScope.Disabled, LlmScanMode.Resolve(new AppSettingsData { LlmScanMode = "network" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Contains("LlmScanMode='network' is not one of local, lan, both, off. Using off.", warning.Message);
    }
}
