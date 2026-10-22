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
/// <remarks>
/// The transcript's roles follow the terminal's (2026-10-21, the user's call: a consistent experience): <c>--user</c> is the theme's <c>user</c>
/// style, the one the terminal draws the user's line in (the palette's tertiary until then, which left the page's user lines another color than
/// the PC's), and <c>--accent-3</c> its <c>accentTertiary</c> style, a review's pass lines (<see cref="UI.NoticeTone.Progress"/>). Each style
/// travels whole (<see cref="StyleTokens"/>), so a theme's <c>"bold": false</c> on its <c>user</c> (miami, commodore) reaches the page too.
/// </remarks>
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

        var css = new StringBuilder("/* The profile's theme, ").Append(CommentSafe(palette.Name)).Append(" (2026-10-15, Web UI profile theme). */\n:root {\n");
        void Token(string name, Color color) => css.Append("  ").Append(name).Append(": ").Append(Hex(color)).Append(";\n");
        // Built here, never the pickers' cache (2026-10-22, a review: a request's thread and the PC's /theme preview rebuilt one shared slot in turn).
        var styles = Theme.StylesBuilt(palette);
        bool readable = Readable(palette);
        Token("--accent", palette.Primary);
        Token("--accent-2", palette.Secondary);
        StyleTokens(css, "--accent-3", styles(ThemeStyleSlot.AccentTertiary), readable);
        StyleTokens(css, "--user", styles(ThemeStyleSlot.User), readable);
        Token("--ok", palette.Good);
        Token("--error", palette.Bad);
        Token("--warn", palette.Warn);
        if (readable)
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

    /// <summary>
    /// A theme style as the page's tokens (2026-10-21): <paramref name="name"/> its foreground, then <c>-weight</c>, <c>-font-style</c> and
    /// <c>-decoration</c> from its decoration, and <c>-bg</c> only when it has a background and <paramref name="grounds"/> (the theme <see cref="Readable"/>:
    /// 2026-10-22, a review, a light theme's ground reached the dark page under ink meant for the light one) — <c>app.css</c> holds each one's default.
    /// </summary>
    internal static void StyleTokens(StringBuilder css, string name, Style style, bool grounds = true)
    {
        ArgumentNullException.ThrowIfNull(css);
        void Line(string suffix, string value) => css.Append("  ").Append(name).Append(suffix).Append(": ").Append(value).Append(";\n");
        Line("", Hex(style.Foreground));
        Line("-weight", style.Decoration.HasFlag(Decoration.Bold) ? "700" : "400");
        Line("-font-style", style.Decoration.HasFlag(Decoration.Italic) ? "italic" : "normal");
        Line("-decoration", style.Decoration.HasFlag(Decoration.Underline) ? "underline" : "none");
        if (grounds && style.Background != Color.Default)
        {
            Line("-bg", Hex(style.Background));
        }
    }

    /// <summary>
    /// <paramref name="text"/> safe inside a CSS comment: every <c>*/</c> gone, however nested (2026-10-19, the review: one pass turned <c>**//</c>
    /// back into <c>*/</c>, and a user theme's name closed the comment in a sheet served before sign-in).
    /// </summary>
    internal static string CommentSafe(string text)
    {
        while (text.Contains("*/", StringComparison.Ordinal))
        {
            text = text.Replace("*/", "", StringComparison.Ordinal);
        }

        return text;
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
