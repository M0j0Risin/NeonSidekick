using NeonSidekick.Companion;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The welcome robot over every built-in theme (2026-11-16, plan sharded-hugging-puppy #53, the daylight themes): its shell stands off the
/// page, its eyes off the visor, and its mouth is still a shape on the visor — on the light pages as on the dark. The mouth's floor is the
/// one the dark themes always had (1.14 to 1.40); the daylight ones sit inside it.
/// </summary>
public sealed class RobotPaletteTests
{
    public static TheoryData<string> Themes() => new(ShippedThemes.All.Select(p => p.Name));

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheRobot_ReadsOnEveryBuiltIn(string name)
    {
        var palette = ShippedThemes.All.Single(p => p.Name == name);
        var robot = RobotPalette.FromTheme(ThemeRoles.From(palette));
        Assert.True(ColorContrast.Contrast(robot.Shell, palette.Bg) >= 3, $"{name}: shell on the page {ColorContrast.Contrast(robot.Shell, palette.Bg):0.00}");
        Assert.True(ColorContrast.Contrast(robot.Eye, robot.Visor) >= 3, $"{name}: eye on the visor {ColorContrast.Contrast(robot.Eye, robot.Visor):0.00}");
        Assert.True(ColorContrast.Contrast(robot.Mouth, robot.Visor) >= 1.1, $"{name}: mouth on the visor {ColorContrast.Contrast(robot.Mouth, robot.Visor):0.00}");
    }

    [Fact]
    public void IsLight_SplitsAtTheMiddle()
    {
        Assert.True(ColorContrast.IsLight(Spectre.Console.Color.White));
        Assert.False(ColorContrast.IsLight(Spectre.Console.Color.Black));
        Assert.False(ColorContrast.IsLight(new Spectre.Console.Color(0x40, 0x40, 0x40)));
        Assert.True(ColorContrast.IsLight(new Spectre.Console.Color(0xC0, 0xC0, 0xC0)));
        Assert.Equal(8, ShippedThemes.All.Count(p => ColorContrast.IsLight(p.Bg)));   // the daylight eight, no other
        Assert.All(ShippedThemes.All.Where(p => ColorContrast.IsLight(p.Bg)), p => Assert.Equal("daylight", ThemeLibrary.CategoryOf(p.Name)));
    }
}
