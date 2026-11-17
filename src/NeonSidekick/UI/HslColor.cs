using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>
/// A color as hue (0–360°), saturation and lightness (0–100 %) (2026-11-16, plan sharded-hugging-puppy #54, <c>/theme edit</c>'s color
/// picker): the bars the user moves. Held as doubles, so a color taken in and given back unmoved is the very color it was (<see cref="From"/>
/// then <see cref="ToColor"/> rounds back to the same bytes); the picker still keeps the starting color itself until a bar moves, so a value
/// that only went out and back is never rewritten. Pure.
/// </summary>
public readonly record struct HslColor(double Hue, double Saturation, double Lightness)
{
    /// <summary><paramref name="color"/> as hue, saturation and lightness.</summary>
    public static HslColor From(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        double d = max - min;
        if (d == 0)
        {
            return new HslColor(0, 0, l * 100);
        }

        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? ((g - b) / d) + (g < b ? 6 : 0)
            : max == g ? ((b - r) / d) + 2
            : ((r - g) / d) + 4;
        return new HslColor(h * 60, s * 100, l * 100);
    }

    /// <summary>The color, each channel rounded to the nearest byte.</summary>
    public Color ToColor()
    {
        double h = (((Hue % 360) + 360) % 360) / 360, s = Math.Clamp(Saturation, 0, 100) / 100, l = Math.Clamp(Lightness, 0, 100) / 100;
        if (s == 0)
        {
            byte grey = Byte(l);
            return new Color(grey, grey, grey);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        double p = (2 * l) - q;
        return new Color(Byte(Channel(p, q, h + (1.0 / 3))), Byte(Channel(p, q, h)), Byte(Channel(p, q, h - (1.0 / 3))));
    }

    /// <summary>The hue moved by <paramref name="degrees"/>, wrapping round the circle.</summary>
    public HslColor WithHue(double degrees) => this with { Hue = (((Hue + degrees) % 360) + 360) % 360 };

    /// <summary>The saturation moved by <paramref name="percent"/>, held to 0–100.</summary>
    public HslColor WithSaturation(double percent) => this with { Saturation = Math.Clamp(Saturation + percent, 0, 100) };

    /// <summary>The lightness moved by <paramref name="percent"/>, held to 0–100.</summary>
    public HslColor WithLightness(double percent) => this with { Lightness = Math.Clamp(Lightness + percent, 0, 100) };

    private static double Channel(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        return t < 1.0 / 6 ? p + ((q - p) * 6 * t)
            : t < 0.5 ? q
            : t < 2.0 / 3 ? p + ((q - p) * ((2.0 / 3) - t) * 6)
            : p;
    }

    private static byte Byte(double unit) => (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255, MidpointRounding.AwayFromZero);
}
