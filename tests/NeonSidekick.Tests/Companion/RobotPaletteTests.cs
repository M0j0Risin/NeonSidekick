using NeonSidekick.Companion;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests.Companion;

public sealed class RobotPaletteTests
{
    // How far apart (in RGB) the cap and ears must be from the shell not to vanish into it — the mapping's own aim, which the
    // emoji's palette meets too (its gold cap is about 190 from its shell, though their luminance contrast is only about 1.05).
    private const double Distinct = RobotPalette.MinDistinct;

    // Every built-in theme (the demo's copy of them until the robot moved in, 2026-10-20).
    public static TheoryData<string> Themes => [.. ThemeLibrary.All.Select(t => t.Name)];

    // The WCAG contrast ratio, 1 (same) to 21 (black on white).
    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Linear(byte v)
            {
                double s = v / 255.0;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Linear(c.R)) + (0.7152 * Linear(c.G)) + (0.0722 * Linear(c.B));
        }

        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void InEveryThemeTheRobotStaysReadable(string name)
    {
        var roles = ThemeRoles.From(ThemeLibrary.Get(name));
        var robot = RobotPalette.FromTheme(roles);

        Assert.True(Contrast(robot.Shell, roles.Bg) >= 4.5, $"{name}: shell on bg {Contrast(robot.Shell, roles.Bg):0.00}");
        Assert.True(Contrast(robot.Eye, robot.Visor) >= 3, $"{name}: eye on visor {Contrast(robot.Eye, robot.Visor):0.00}");
        Assert.True(Contrast(robot.Mouth, robot.Shell) >= 4.5, $"{name}: mouth on shell {Contrast(robot.Mouth, robot.Shell):0.00}");
        AssertDistinctParts(name, robot);
    }

    [Fact]
    public void TheEmojisOwnPartsAreDistinctToo()
    {
        AssertDistinctParts("emoji", RobotPalette.Default);
    }

    private static void AssertDistinctParts(string name, RobotPalette robot)
    {
        Assert.True(RobotPalette.Distance(robot.Cap, robot.Shell) >= Distinct, $"{name}: cap {RobotPalette.Distance(robot.Cap, robot.Shell):0} from the shell");
        Assert.True(RobotPalette.Distance(robot.Ear, robot.Shell) >= Distinct, $"{name}: ear {RobotPalette.Distance(robot.Ear, robot.Shell):0} from the shell");
    }

    // Some roles to vary from: a plain dark theme.
    private static readonly ThemeRoles Plain = new(
        Primary: Color.Red, Secondary: Color.Aqua, Tertiary: Color.Yellow, Deep: Color.Purple, Highlight: Color.Yellow,
        Warm: Color.Orange1, Tint: Color.Blue, Ink: Color.White, Dim: Color.Grey, Dimmer: Color.Grey35, Bg: Color.Black,
        PanelBg: Color.Navy, Good: Color.Green, Bad: Color.Red, Warn: Color.Yellow);

    [Fact]
    public void TheEyesWearThePromptsColorAndTheAccentThePrimary()
    {
        var secondary = new Color(0x3D, 0xE8, 0xFF);
        var primary = new Color(0xFF, 0x3D, 0xB8);
        var robot = RobotPalette.FromTheme(Plain with { Primary = primary, Secondary = secondary });

        Assert.Equal(secondary, robot.Eye);
        Assert.Equal(primary, robot.Accent);
    }

    [Fact]
    public void TheCapIsWhicheverStandsOutMostFromTheShell()
    {
        // A cream highlight would vanish into the pale shell; the yellow tertiary stands out more, the warm orange most.
        var cream = new Color(0xFF, 0xF8, 0xD0);
        var yellow = new Color(0xFF, 0xE4, 0x5C);
        var orange = new Color(0xFF, 0x8A, 0x3D);
        var robot = RobotPalette.FromTheme(Plain with { Ink = new Color(0xEE, 0xF0, 0xF8), Tint = new Color(0xA0, 0x8A, 0xFF), Tertiary = yellow, Highlight = cream, Warm = orange });

        Assert.Equal(orange, robot.Cap);
    }

    [Fact]
    public void InAPaleThemeTheCapAndEarsDeepenButKeepTheirHue()
    {
        // Noir is all grays: its cap and ears are deepened toward its gray "deep" until they stand out — still gray.
        var noir = RobotPalette.FromTheme(ThemeRoles.From(ThemeLibrary.Get("noir")));

        foreach (var part in new[] { noir.Cap, noir.Ear })
        {
            Assert.True(part.R == part.G && part.G == part.B, $"{part} isn't gray");
            Assert.True(RobotPalette.Distance(part, noir.Shell) >= RobotPalette.MinDistinct);
        }

        // A theme whose colors already stand out keeps them as they are.
        var collider = RobotPalette.FromTheme(ThemeRoles.From(ThemeLibrary.Get("collider")));
        Assert.Equal(new Color(0xFF, 0x8A, 0x3D), collider.Cap); // its warm, the most distinct of the three
    }

    [Fact]
    public void MixGoesFromOneColorToTheOther()
    {
        var a = new Color(0, 100, 255);
        var b = new Color(255, 101, 0);

        Assert.Equal(a, RobotPalette.Mix(a, b, 0));
        Assert.Equal(b, RobotPalette.Mix(a, b, 1));
        Assert.Equal(new Color(128, 101, 128), RobotPalette.Mix(a, b, 0.5)); // 127.5 and 100.5 round up
    }
}
