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

    private sealed record KeyCache(ThemePalette Palette, string Key);

    private static KeyCache? s_key;

    /// <summary>
    /// The pulse's theme value (2026-11-16, plan sharded-hugging-puppy): the name, <c>~</c>, and a short hash of the sheet <see cref="Css"/> writes,
    /// so a theme edited under the same name (<c>/theme edit</c>) re-links the page's sheet too. The pages only compare it and put it in
    /// <c>?v=</c>. Worked out once per palette (the pulse runs often; <see cref="Theme.Use"/> swaps the palette whole).
    /// </summary>
    public static string Key(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (s_key is { } cached && ReferenceEquals(cached.Palette, palette))
        {
            return cached.Key;
        }

        // FNV-1a over the sheet's text: stable across runs, unlike string.GetHashCode.
        uint hash = 2166136261;
        foreach (char c in Css(palette, on: true))
        {
            hash = (hash ^ c) * 16777619;
        }

        string key = palette.Name + "~" + hash.ToString("x8", CultureInfo.InvariantCulture);
        s_key = new KeyCache(palette, key);
        return key;
    }

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
        // A botchat's speakers (2026-11-12, plan what-would-it-take-tingly-ullman): the six hues the terminal names them in, in its order
        // (ChatScreen.SpeakerColor). Accents, so lent whether or not the theme reads; slot 2 is the palette's Tertiary, not --accent-3.
        Token("--speaker-0", palette.Primary);
        Token("--speaker-1", palette.Secondary);
        Token("--speaker-2", palette.Tertiary);
        Token("--speaker-3", palette.Highlight);
        Token("--speaker-4", palette.Warm);
        Token("--speaker-5", palette.Tint);
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

        if (readable && ColorContrast.IsLight(palette.Bg))
        {
            // A light page (2026-11-16, plan sharded-hugging-puppy #53, the daylight themes): app.css's colors fixed for a dark one given the
            // theme's — the scheme, the gradient's far end, the diff slabs and the code block's ground as the terminal draws them, the text on
            // the accent and on the error, and the code colors through the export's map. A dark theme keeps app.css's own.
            css.Append("  color-scheme: light;\n");
            Token("--bg-mix", Color.Black);
            Token("--diff-add", Theme.Mix(palette.Bg, palette.Good, Theme.DiffSlabShare));
            Token("--diff-del", Theme.Mix(palette.Bg, palette.Bad, Theme.DiffSlabShare));
            var block = styles(ThemeStyleSlot.MarkdownCodeBlock);
            Token("--code-bg", block.Background == Color.Default ? palette.PanelBg : block.Background);
            Token("--on-accent", OnColor(palette.Primary, palette));
            Token("--on-error", OnColor(palette.Bad, palette));
            foreach (var (kind, slot) in CodeSlots)
            {
                if (Pdf.PdfHtml.CodeClass(kind) is { } name && styles(slot).Foreground != Color.Default)
                {
                    Token("--" + name, styles(slot).Foreground);
                }
            }
        }

        return css.Append("}\n").ToString();
    }

    /// <summary>
    /// The code token kinds and the style each wears, as the terminal colors them: the session export's classes and the light page's
    /// <c>--tk-*</c> tokens read one map (2026-11-16; the export's own until then).
    /// </summary>
    internal static readonly IReadOnlyList<(UI.Markdown.CodeTokenKind Kind, ThemeStyleSlot Slot)> CodeSlots =
    [
        (UI.Markdown.CodeTokenKind.Keyword, ThemeStyleSlot.CodeKeyword),
        (UI.Markdown.CodeTokenKind.Type, ThemeStyleSlot.CodeType),
        (UI.Markdown.CodeTokenKind.String, ThemeStyleSlot.CodeString),
        (UI.Markdown.CodeTokenKind.Number, ThemeStyleSlot.CodeNumber),
        (UI.Markdown.CodeTokenKind.Comment, ThemeStyleSlot.CodeComment),
        (UI.Markdown.CodeTokenKind.Punctuation, ThemeStyleSlot.CodePunctuation),
        (UI.Markdown.CodeTokenKind.Function, ThemeStyleSlot.CodeFunction),
        (UI.Markdown.CodeTokenKind.Variable, ThemeStyleSlot.CodeVariable),
        (UI.Markdown.CodeTokenKind.Attribute, ThemeStyleSlot.CodeAttribute),
        (UI.Markdown.CodeTokenKind.Tag, ThemeStyleSlot.CodeTag),
        (UI.Markdown.CodeTokenKind.Heading, ThemeStyleSlot.CodeHeading),
        (UI.Markdown.CodeTokenKind.Inserted, ThemeStyleSlot.CodeInserted),
        (UI.Markdown.CodeTokenKind.Deleted, ThemeStyleSlot.CodeDeleted),
    ];

    /// <summary>The page's ground or its ink, whichever reads better on <paramref name="fill"/>: the text on a button or badge of that color.</summary>
    internal static Color OnColor(Color fill, ThemePalette palette) =>
        ColorContrast.Contrast(palette.Bg, fill) >= ColorContrast.Contrast(palette.Ink, fill) ? palette.Bg : palette.Ink;

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
    /// back into <c>*/</c>, and a user theme's name closed the comment in a sheet served before sign-in). Since 2026-10-30 (review F40) every
    /// <c>&lt;</c> and <c>&gt;</c> goes too: a session export's sheet sits in the page's own <c>&lt;style&gt;</c>, which a <c>&lt;/style&gt;</c> in
    /// a theme's name would close.
    /// </summary>
    internal static string CommentSafe(string text)
    {
        text = text.Replace("<", "", StringComparison.Ordinal).Replace(">", "", StringComparison.Ordinal);
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
        return ColorContrast.Contrast(palette.Ink, palette.Bg) >= MinContrast && ColorContrast.Contrast(palette.Ink, palette.PanelBg) >= MinContrast;
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
