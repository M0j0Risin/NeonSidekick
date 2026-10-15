using System.Globalization;
using System.Text;
using System.Text.Json;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.WebUi;

/// <summary>
/// The PC's theme on the Web UI's pages (2026-10-15, the plan's E10, behind <i>Web UI profile theme</i>): <c>/theme.css</c>, the palette's colors as
/// the page's tokens over <c>app.css</c>'s own — the accents, the tones, the grounds and the inks — and the manifest an installed page takes its
/// colors from. The pages are dark: a theme whose ink does not read on its own ground (under <see cref="MinContrast"/>) lends its accents and
/// tones alone, the page keeping its own grounds and inks. Off, an empty sheet: <c>app.css</c> stands. Pure.
/// </summary>
public static class WebTheme
{
    /// <summary>The least contrast (WCAG's ratio) between the theme's ink and its ground for the page to take its grounds.</summary>
    public const double MinContrast = 4.5;

    /// <summary>The page's own ground (<c>app.css</c>'s <c>--bg</c>), the manifest's colors while the theme is off.</summary>
    public const string PageGround = "#120b1f";

    /// <summary>The page's own accent (<c>app.css</c>'s <c>--accent</c>).</summary>
    public const string PageAccent = "#ff4fd8";

    /// <summary><c>/theme.css</c> for <paramref name="palette"/>; empty while <paramref name="on"/> is off.</summary>
    public static string Css(ThemePalette palette, bool on)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (!on)
        {
            return "";
        }

        var css = new StringBuilder("/* The profile's theme, ").Append(palette.Name.Replace("*/", "", StringComparison.Ordinal)).Append(" (2026-10-15, Web UI profile theme). */\n:root {\n");
        void Token(string name, Color color) => css.Append("  ").Append(name).Append(": ").Append(Hex(color)).Append(";\n");
        Token("--accent", palette.Primary);
        Token("--accent-2", palette.Secondary);
        Token("--user", palette.Tertiary);
        Token("--ok", palette.Good);
        Token("--error", palette.Bad);
        Token("--warn", palette.Warn);
        if (Readable(palette))
        {
            Token("--bg", palette.Bg);
            Token("--panel", palette.PanelBg);
            Token("--panel-2", Mix(palette.PanelBg, palette.Ink, 0.08));
            Token("--line", Mix(palette.PanelBg, palette.Dim, 0.35));
            Token("--text", palette.Ink);
            Token("--dim", palette.Dim);
        }

        return css.Append("}\n").ToString();
    }

    /// <summary>Whether the theme's ink reads on its ground (<see cref="MinContrast"/>), so the page may take its grounds and inks.</summary>
    public static bool Readable(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return Contrast(palette.Ink, palette.Bg) >= MinContrast && Contrast(palette.Ink, palette.PanelBg) >= MinContrast;
    }

    /// <summary>WCAG's contrast ratio of two colors, 1 to 21.</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static Color Mix(Color a, Color b, double t) =>
        new((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary><c>#rrggbb</c>.</summary>
    public static string Hex(Color color) => "#" + color.ToHex().ToLowerInvariant();

    /// <summary>
    /// The manifest an installed page reads (E8): the app's name, the chat as its start, standalone, its two icons, and the theme's ground and accent
    /// (the page's own while the theme is off or does not read).
    /// </summary>
    public static string Manifest(ThemePalette palette, bool on)
    {
        ArgumentNullException.ThrowIfNull(palette);
        bool theme = on && Readable(palette);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("name", "NeonSidekick");
            json.WriteString("short_name", "NeonSidekick");
            json.WriteString("start_url", "/");
            json.WriteString("scope", "/");
            json.WriteString("display", "standalone");
            json.WriteString("background_color", theme ? Hex(palette.Bg) : PageGround);
            json.WriteString("theme_color", theme ? Hex(palette.PanelBg) : PageGround);
            json.WriteStartArray("icons");
            foreach (int size in new[] { 192, 512 })
            {
                string sizes = size.ToString(CultureInfo.InvariantCulture) + "x" + size.ToString(CultureInfo.InvariantCulture);
                json.WriteStartObject();
                json.WriteString("src", "/icon-" + size.ToString(CultureInfo.InvariantCulture) + ".png");
                json.WriteString("sizes", sizes);
                json.WriteString("type", "image/png");
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
