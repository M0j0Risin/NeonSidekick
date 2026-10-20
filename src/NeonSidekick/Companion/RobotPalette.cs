using Spectre.Console;

namespace NeonSidekick.Companion;

/// <summary>
/// The robot's colors. <see cref="Default"/> follows the 🤖 emoji as Windows draws it; <see cref="FromTheme"/> builds one
/// from a Neon Sidekick theme's roles. Either can be used — the themed one is a choice, not a replacement. On a 256-color
/// terminal Spectre maps each to the nearest palette entry.
/// </summary>
/// <param name="Shell">The head and body.</param>
/// <param name="ShellShade">The shell's shadowed left edge and underside.</param>
/// <param name="ShellLight">The top of the head, seen when looking down.</param>
/// <param name="Visor">The dark face plate.</param>
/// <param name="Eye">The eyes' lit side.</param>
/// <param name="EyeShade">The eyes' darker side.</param>
/// <param name="Mouth">The mouth.</param>
/// <param name="Cap">The light on top of the head.</param>
/// <param name="CapShade">The cap's lower edge.</param>
/// <param name="Ear">The side ears and antenna stalks.</param>
/// <param name="Accent">The antenna tips and the chest panel.</param>
public sealed record RobotPalette(
    Color Shell,
    Color ShellShade,
    Color ShellLight,
    Color Visor,
    Color Eye,
    Color EyeShade,
    Color Mouth,
    Color Cap,
    Color CapShade,
    Color Ear,
    Color Accent)
{
    /// <summary>Pale lavender shell, deep purple visor, cyan eyes, a gold cap, pink ears and antennas.</summary>
    public static RobotPalette Default { get; } = new(
        Shell: new Color(0xD7, 0xC7, 0xE6),
        ShellShade: new Color(0xB8, 0x92, 0xAE),
        ShellLight: new Color(0xEA, 0xE3, 0xF1),
        Visor: new Color(0x36, 0x28, 0x50),
        Eye: new Color(0x3B, 0xB0, 0xED),
        EyeShade: new Color(0x1D, 0x7E, 0xC0),
        Mouth: new Color(0x2E, 0x1D, 0x39),
        Cap: new Color(0xF9, 0xC2, 0x2B),
        CapShade: new Color(0xC6, 0x7D, 0x29),
        Ear: new Color(0xE0, 0x28, 0x60),
        Accent: new Color(0xF8, 0x26, 0x5B));

    /// <summary>
    /// The robot in a theme's colors (<paramref name="roles"/>), keeping the emoji's logic: a pale shell (<c>Ink</c> tinted
    /// toward <c>Tint</c>, so it reads on the dark <c>Bg</c>), a dark visor (<c>PanelBg</c> toward <c>Deep</c>), eyes in
    /// <c>Secondary</c> (the color the prompt wears), a dark mouth, ears and antennas in <c>Primary</c>, and a cap light in
    /// whichever of <c>Highlight</c>, <c>Tertiary</c> and <c>Warm</c> stands out most from the shell — a pale highlight would
    /// vanish into it. In a pale (pastel or monochrome) theme even that may be too close to the shell, so the cap and the ears
    /// are deepened toward <c>Deep</c>, only as far as it takes to stand out (<see cref="MinDistinct"/>): they keep the
    /// theme's hue, and a gray theme stays gray.
    /// </summary>
    public static RobotPalette FromTheme(ThemeRoles roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var shell = Mix(roles.Ink, roles.Tint, 0.2);
        var cap = StandOut(new[] { roles.Highlight, roles.Tertiary, roles.Warm }.MaxBy(c => Distance(c, shell)), shell, roles.Deep);
        return new RobotPalette(
            Shell: shell,
            ShellShade: Mix(shell, roles.Dim, 0.5),
            ShellLight: Mix(roles.Ink, Color.White, 0.5),
            Visor: Mix(roles.PanelBg, roles.Deep, 0.25),
            Eye: roles.Secondary,
            EyeShade: Mix(roles.Secondary, roles.Deep, 0.4),
            Mouth: Mix(roles.Bg, roles.PanelBg, 0.5),
            Cap: cap,
            CapShade: Mix(cap, roles.Bg, 0.3),
            Ear: StandOut(Mix(roles.Primary, roles.Bg, 0.12), shell, roles.Deep),
            Accent: roles.Primary);
    }

    /// <summary>
    /// How far apart (<see cref="Distance"/>) the cap and the ears must be from the shell not to vanish into it. The emoji's
    /// gold cap on its lavender shell is about 190 apart, its ears about 210 — clearly distinct, though in lightness the cap
    /// and shell are nearly the same.
    /// </summary>
    internal const double MinDistinct = 120;

    /// <summary><paramref name="color"/>, or as little of the way toward <paramref name="deep"/> as makes it stand out from <paramref name="shell"/>.</summary>
    private static Color StandOut(Color color, Color shell, Color deep)
    {
        for (int step = 0; step <= 10; step++)
        {
            var deepened = Mix(color, deep, step / 10.0);
            if (Distance(deepened, shell) >= MinDistinct)
            {
                return deepened;
            }
        }

        return deep;
    }

    /// <summary>The color <paramref name="t"/> of the way from <paramref name="a"/> to <paramref name="b"/>, each channel rounded.</summary>
    internal static Color Mix(Color a, Color b, double t)
    {
        static byte Channel(byte from, byte to, double t) => (byte)Math.Round(from + ((to - from) * t), MidpointRounding.AwayFromZero);
        return new Color(Channel(a.R, b.R, t), Channel(a.G, b.G, t), Channel(a.B, b.B, t));
    }

    /// <summary>How far apart two colors are, straight-line in RGB: hue differences count, not just lightness.</summary>
    internal static double Distance(Color a, Color b)
    {
        double dr = a.R - b.R;
        double dg = a.G - b.G;
        double db = a.B - b.B;
        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }
}
