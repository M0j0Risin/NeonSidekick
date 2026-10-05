using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class ThemeNameTests
{
    [Fact]
    public void Names_AreTheBuiltIns_AToZ_ColliderTheDefault()
    {
        // A to Z since 2026-10-03 (the user's ask); the built-ins are assets/themes' files since 2026-10-05, collider the default.
        Assert.Equal(ShippedThemes.All.Select(p => p.Name), ThemeName.Names);
        Assert.Equal(ThemeName.Names.Order(StringComparer.Ordinal), ThemeName.Names);
        Assert.Equal("collider", ThemeName.Default);
        Assert.Equal(ThemeName.Default, new AppSettingsData().Theme);
        Assert.Contains("synthwave", ThemeName.Names);
        Assert.Same(ThemeLibrary.Default, ThemeLibrary.Get("collider"));
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
    public void TryParse_AnythingElse_IsFalseAndTheDefault(string? text)
    {
        Assert.False(ThemeName.TryParse(text, out var palette));
        Assert.Same(ThemeLibrary.Default, palette);
    }

    [Fact]
    public void Describe_IsThePalettesNote_Pinned()
    {
        Assert.Equal("neon sunset", ThemeName.Describe("synthwave"));
        Assert.Equal("particle collision", ThemeName.Describe("collider"));
        Assert.Equal("green phosphor", ThemeName.Describe("netrunner"));
        Assert.Equal("amber phosphor", ThemeName.Describe("nostromo"));
        Assert.Equal("greyscale", ThemeName.Describe("noir"));
        Assert.Equal("colorful", ThemeName.Describe("cyberpunk"));
        Assert.Equal("pastel", ThemeName.Describe("vaporwave"));
        Assert.Equal("blue phosphor", ThemeName.Describe("mainframe"));
        Assert.Equal("light cycle", ThemeName.Describe("grid"));
        Assert.Equal("smog and sodium", ThemeName.Describe("replicant"));
        Assert.Equal("bioluminescent", ThemeName.Describe("abyssal"));
        Assert.Equal("", ThemeName.Describe("matrix"));
    }

    [Fact]
    public void Resolve_MapsTheSavedName()
    {
        Assert.Same(ShippedThemes.Cyberpunk, ThemeName.Resolve(new AppSettingsData { Theme = "cyberpunk" }));
        Assert.Same(ThemeLibrary.Default, ThemeName.Resolve(new AppSettingsData()));
    }

    [Fact]
    public void Resolve_UnknownSavedValue_WarnsAndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Theme" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Same(ThemeLibrary.Default, ThemeName.Resolve(new AppSettingsData { Theme = "matrix" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Contains($"Theme='matrix' is not one of {string.Join(", ", ThemeName.Names)}. Using collider.", warning.Message);
    }

    [Fact]
    public void Apply_PutsTheSavedThemeInForce()
    {
        using var scope = new ThemeScope();
        ThemeName.Apply(new AppSettingsData { Theme = "vaporwave" });
        Assert.Same(ShippedThemes.Vaporwave, Theme.Current);
        ThemeName.Apply(new AppSettingsData());
        Assert.Same(ThemeLibrary.Default, Theme.Current);
    }
}
