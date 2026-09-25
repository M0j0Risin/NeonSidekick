using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class ThemeNameTests
{
    [Fact]
    public void Names_ArePinned_InMenuOrder_SynthwaveFirstAndTheDefault()
    {
        Assert.Equal(new[] { "synthwave", "netrunner", "nostromo", "noir", "cyberpunk", "vaporwave" }, ThemeName.Names);
        Assert.Equal("synthwave", ThemeName.Default);
        Assert.Equal(ThemeName.Default, new AppSettingsData().Theme);
        Assert.Equal(ThemePalette.All.Select(p => p.Name), ThemeName.Names);
    }

    [Theory]
    [InlineData("netrunner")]
    [InlineData("NetRunner")]
    [InlineData("  noir ")]
    public void TryParse_TrimsAndIgnoresCase(string text)
    {
        Assert.True(ThemeName.TryParse(text, out var palette));
        Assert.Equal(text.Trim().ToLowerInvariant(), palette.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("matrix")]
    public void TryParse_AnythingElse_IsFalseAndSynthwave(string? text)
    {
        Assert.False(ThemeName.TryParse(text, out var palette));
        Assert.Same(ThemePalette.Synthwave, palette);
    }

    [Fact]
    public void Describe_IsThePalettesNote_Pinned()
    {
        Assert.Equal("default theme", ThemeName.Describe("synthwave"));
        Assert.Equal("green phosphor", ThemeName.Describe("netrunner"));
        Assert.Equal("amber phosphor", ThemeName.Describe("nostromo"));
        Assert.Equal("greyscale", ThemeName.Describe("noir"));
        Assert.Equal("colorful", ThemeName.Describe("cyberpunk"));
        Assert.Equal("pastel", ThemeName.Describe("vaporwave"));
        Assert.Equal("", ThemeName.Describe("matrix"));
    }

    [Fact]
    public void Resolve_MapsTheSavedName()
    {
        Assert.Same(ThemePalette.Cyberpunk, ThemeName.Resolve(new AppSettingsData { Theme = "cyberpunk" }));
        Assert.Same(ThemePalette.Synthwave, ThemeName.Resolve(new AppSettingsData()));
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Theme" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Same(ThemePalette.Synthwave, ThemeName.Resolve(new AppSettingsData { Theme = "matrix" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Contains("Theme='matrix' is not one of synthwave, netrunner, nostromo, noir, cyberpunk, vaporwave. Using synthwave.", warning.Message);
    }

    [Fact]
    public void Apply_PutsTheSavedThemeInForce()
    {
        using var scope = new ThemeScope();
        ThemeName.Apply(new AppSettingsData { Theme = "vaporwave" });
        Assert.Same(ThemePalette.Vaporwave, Theme.Current);
        ThemeName.Apply(new AppSettingsData());
        Assert.Same(ThemePalette.Synthwave, Theme.Current);
    }
}
