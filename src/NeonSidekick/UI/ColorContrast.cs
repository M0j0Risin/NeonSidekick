using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>
/// WCAG's relative luminance and contrast ratio (2026-11-01, moved here from <c>WebUi/WebTheme</c> for the quiet gradient rules,
/// <see cref="Theme.QuietStops"/>): one copy for the Web UI's readability check, the session export's light test and the rules. Pure.
/// </summary>
public static class ColorContrast
{
    /// <summary>WCAG's contrast ratio of two colors, 1 to 21.</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>WCAG's relative luminance of a color, 0 (black) to 1 (white).</summary>
    public static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
