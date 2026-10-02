using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>
/// One named look (2026-09-23, the user's ask: themes beside synthwave, chosen on the General tab's
/// <c>Theme</c> row or with <c>/theme</c>): the colour of every role <see cref="Theme"/> composes its
/// styles from. This is the <em>only</em> file that names a colour. The slots are roles, not hues —
/// <see cref="Primary"/> is synthwave's magenta and netrunner's green — so a call site reads the
/// same under every theme. <see cref="Synthwave"/> is the default and keeps the original values
/// exactly; the tests pin them.
/// </summary>
/// <param name="Name">The name the setting stores and <c>/theme</c> takes (lower case).</param>
/// <param name="Description">The note beside the name in the picker and the argument list.</param>
/// <param name="Primary">The main accent: headings, borders, keywords, markup tags.</param>
/// <param name="Secondary">The counter-accent: the user's lines, the spinner, table headers, inline code, types and keys, the selection.</param>
/// <param name="Tertiary">The third accent: section headings, bullets, the quote bar, calls, the paste label.</param>
/// <param name="Deep">The receding structural colour: the pane rule, a reply table's border.</param>
/// <param name="Highlight">The warm highlight: string literals, the top of the sun.</param>
/// <param name="Warm">Between <paramref name="Highlight"/> and <paramref name="Primary"/>: numbers.</param>
/// <param name="Tint">A soft accent: <c>$variables</c>, the lower sun.</param>
/// <param name="Ink">Body text.</param>
/// <param name="Dim">Secondary, dim text.</param>
/// <param name="Dimmer">A step darker than <paramref name="Dim"/>: the input row's ghost text.</param>
/// <param name="Bg">The page background (the selection's text, disabled menu rows, a picture's transparent pixels).</param>
/// <param name="PanelBg">The lifted fill: code blocks, the highlighted menu row.</param>
/// <param name="Good">Success, enabled, connected.</param>
/// <param name="Bad">Failure, error.</param>
/// <param name="Warn">A warning.</param>
/// <param name="GradientStops">The stops of the banner title and its rule, left to right: five on every built-in, 2 to 16 from a theme file.</param>
/// <param name="Styles">A theme file's changes to single styles (2026-10-01, the user's ask; <see cref="ThemeStyleSlot"/>), its base's under its own; null on every built-in.</param>
/// <param name="SourcePath">The file a user theme was read from (<see cref="ThemeCatalog"/>); null on a built-in.</param>
public sealed record ThemePalette(
    string Name,
    string Description,
    Color Primary,
    Color Secondary,
    Color Tertiary,
    Color Deep,
    Color Highlight,
    Color Warm,
    Color Tint,
    Color Ink,
    Color Dim,
    Color Dimmer,
    Color Bg,
    Color PanelBg,
    Color Good,
    Color Bad,
    Color Warn,
    Color[] GradientStops,
    IReadOnlyDictionary<ThemeStyleSlot, StyleOverride>? Styles = null,
    string? SourcePath = null)
{
    /// <summary>The original neon sunset (the default): hot magenta, cyan, violet, sunset amber on deep space.</summary>
    public static readonly ThemePalette Synthwave = Build(
        "synthwave", "default theme",
        primary: new(0xFF, 0x2E, 0x97),
        secondary: new(0x33, 0xE0, 0xFF),
        tertiary: new(0xB1, 0x5B, 0xFF),
        deep: new(0x7B, 0x2F, 0xF7),
        highlight: new(0xFF, 0xC8, 0x32),
        warm: new(0xFF, 0x8A, 0x3D),
        tint: new(0xF4, 0x5B, 0x9B),
        ink: new(0xEF, 0xE6, 0xFF),
        dim: new(0x9A, 0x8B, 0xB8),
        dimmer: new(0x44, 0x3C, 0x56),
        bg: new(0x0B, 0x04, 0x16),
        panelBg: new(0x16, 0x0A, 0x28),
        good: new(0x3D, 0xF2, 0x7A),
        bad: new(0xFF, 0x4D, 0x6D),
        warn: null,
        gradient: null);

    /// <summary>Matrix-style green phosphor: bright green, aqua-mint and lime on near-black green.</summary>
    public static readonly ThemePalette Netrunner = Build(
        "netrunner", "green phosphor",
        primary: new(0x00, 0xFF, 0x41),
        secondary: new(0x3D, 0xFF, 0xD0),
        tertiary: new(0xB6, 0xFF, 0x3D),
        deep: new(0x0F, 0x6B, 0x2E),
        highlight: new(0xE0, 0xFF, 0x6B),
        warm: new(0x9C, 0xFF, 0x57),
        tint: new(0x5C, 0xFF, 0xA8),
        ink: new(0xD7, 0xFF, 0xE0),
        dim: new(0x5E, 0x9A, 0x6C),
        dimmer: new(0x24, 0x40, 0x2C),
        bg: new(0x02, 0x0A, 0x04),
        panelBg: new(0x07, 0x17, 0x0C),
        good: new(0x39, 0xFF, 0x88),
        bad: new(0xFF, 0x33, 0x55),
        warn: null,
        gradient: [new(0x0F, 0x6B, 0x2E), new(0x00, 0xC8, 0x3A), new(0x00, 0xFF, 0x41), new(0x3D, 0xFF, 0xD0), new(0xE0, 0xFF, 0x6B)]);

    /// <summary>An amber CRT, the Nostromo's monitors (named so 2026-09-23, the user's call; <c>phosphor</c> until then): amber, pale gold and burnt orange on brown-black; red only for failures.</summary>
    public static readonly ThemePalette Nostromo = Build(
        "nostromo", "amber phosphor",
        primary: new(0xFF, 0xB0, 0x00),
        secondary: new(0xFF, 0xD2, 0x7A),
        tertiary: new(0xE0, 0x9A, 0x3A),
        deep: new(0x6E, 0x46, 0x00),
        highlight: new(0xFF, 0xE6, 0xA8),
        warm: new(0xFF, 0x8C, 0x1A),
        tint: new(0xFF, 0xC2, 0x66),
        ink: new(0xFF, 0xE9, 0xC2),
        dim: new(0x9C, 0x7A, 0x45),
        dimmer: new(0x45, 0x34, 0x1B),
        bg: new(0x0D, 0x08, 0x00),
        panelBg: new(0x1A, 0x10, 0x04),
        good: new(0xD4, 0xFF, 0x7A),
        bad: new(0xFF, 0x4A, 0x2E),
        warn: null,
        gradient: [new(0x6E, 0x46, 0x00), new(0xCC, 0x84, 0x00), new(0xFF, 0xB0, 0x00), new(0xFF, 0xD2, 0x7A), new(0xFF, 0xF3, 0xD6)]);

    /// <summary>Monochrome: greys and white, with a muted red kept for errors and failures (the user's call, 2026-09-23).</summary>
    public static readonly ThemePalette Noir = Build(
        "noir", "greyscale",
        primary: new(0xFF, 0xFF, 0xFF),
        secondary: new(0xC8, 0xC8, 0xC8),
        tertiary: new(0xA0, 0xA0, 0xA0),
        deep: new(0x4A, 0x4A, 0x4A),
        highlight: new(0xE0, 0xE0, 0xE0),
        warm: new(0xB8, 0xB8, 0xB8),
        tint: new(0xD0, 0xD0, 0xD0),
        ink: new(0xE6, 0xE6, 0xE6),
        dim: new(0x8C, 0x8C, 0x8C),
        dimmer: new(0x3A, 0x3A, 0x3A),
        bg: new(0x0A, 0x0A, 0x0A),
        panelBg: new(0x17, 0x17, 0x17),
        good: new(0xF5, 0xF5, 0xF5),
        bad: new(0xD6, 0x45, 0x45),
        warn: new(0xBD, 0xBD, 0xBD),
        gradient: [new(0x4A, 0x4A, 0x4A), new(0x8C, 0x8C, 0x8C), new(0xC8, 0xC8, 0xC8), new(0xE6, 0xE6, 0xE6), new(0xFF, 0xFF, 0xFF)]);

    /// <summary>Night City: electric yellow, cyan and danger red on blue-black.</summary>
    public static readonly ThemePalette Cyberpunk = Build(
        "cyberpunk", "colorful",
        primary: new(0xFC, 0xEE, 0x0A),
        secondary: new(0x00, 0xF0, 0xFF),
        tertiary: new(0xFF, 0x2A, 0x6D),
        deep: new(0x7A, 0x15, 0x30),
        highlight: new(0xFF, 0xB8, 0x00),
        warm: new(0xFF, 0x6B, 0x00),
        tint: new(0xC5, 0xF9, 0x00),
        ink: new(0xF2, 0xF2, 0xF2),
        dim: new(0x8A, 0x8A, 0x99),
        dimmer: new(0x35, 0x35, 0x3F),
        bg: new(0x0A, 0x0A, 0x12),
        panelBg: new(0x15, 0x15, 0x1F),
        good: new(0x00, 0xFF, 0x9F),
        bad: new(0xFF, 0x00, 0x3C),
        warn: null,
        gradient: [new(0x00, 0xF0, 0xFF), new(0x7A, 0xF7, 0xFF), new(0xFC, 0xEE, 0x0A), new(0xFF, 0x6B, 0x00), new(0xFF, 0x00, 0x3C)]);

    /// <summary>The soft pastel variant: pink, sky blue, lavender and mint on dusk purple.</summary>
    public static readonly ThemePalette Vaporwave = Build(
        "vaporwave", "pastel",
        primary: new(0xFF, 0x71, 0xCE),
        secondary: new(0x01, 0xCD, 0xFE),
        tertiary: new(0xB9, 0x67, 0xFF),
        deep: new(0x6A, 0x3F, 0xB0),
        highlight: new(0xFF, 0xFB, 0x96),
        warm: new(0xFF, 0xB3, 0x8A),
        tint: new(0x05, 0xFF, 0xA1),
        ink: new(0xF5, 0xEE, 0xFF),
        dim: new(0xA9, 0x9C, 0xC4),
        dimmer: new(0x4A, 0x3F, 0x63),
        bg: new(0x1A, 0x10, 0x30),
        panelBg: new(0x25, 0x18, 0x3F),
        good: new(0x05, 0xFF, 0xA1),
        bad: new(0xFF, 0x5C, 0x8A),
        warn: null,
        gradient: [new(0x01, 0xCD, 0xFE), new(0x05, 0xFF, 0xA1), new(0xB9, 0x67, 0xFF), new(0xFF, 0x71, 0xCE), new(0xFF, 0xFB, 0x96)]);

    /// <summary>A blue-white CRT, an IBM mainframe terminal's (2026-09-27, the user's pick: the third phosphor beside netrunner's green and nostromo's amber): ice blue, pale sky and cobalt on blue-black.</summary>
    public static readonly ThemePalette Mainframe = Build(
        "mainframe", "blue phosphor",
        primary: new(0x6E, 0xC8, 0xFF),
        secondary: new(0xD6, 0xF0, 0xFF),
        tertiary: new(0x3D, 0x8B, 0xFF),
        deep: new(0x1A, 0x3F, 0x7A),
        highlight: new(0xBD, 0xF0, 0xFF),
        warm: new(0x8F, 0xB8, 0xFF),
        tint: new(0x9A, 0xDC, 0xFF),
        ink: new(0xE2, 0xF2, 0xFF),
        dim: new(0x6A, 0x88, 0xA8),
        dimmer: new(0x22, 0x32, 0x4A),
        bg: new(0x02, 0x08, 0x12),
        panelBg: new(0x08, 0x16, 0x28),
        good: new(0x7A, 0xFF, 0xD4),
        bad: new(0xFF, 0x4D, 0x5E),
        warn: null,
        gradient: [new(0x1A, 0x3F, 0x7A), new(0x2F, 0x6F, 0xD0), new(0x6E, 0xC8, 0xFF), new(0xD6, 0xF0, 0xFF), new(0xFF, 0xFF, 0xFF)]);

    /// <summary>The Grid of Tron Legacy (2026-09-27, the user's pick): light-cycle cyan led, program orange against it, on black.</summary>
    public static readonly ThemePalette Grid = Build(
        "grid", "light cycle",
        primary: new(0x00, 0xE5, 0xFF),
        secondary: new(0xFF, 0x9E, 0x1B),
        tertiary: new(0x7D, 0xF9, 0xFF),
        deep: new(0x0A, 0x4F, 0x66),
        highlight: new(0xFF, 0xD2, 0x7A),
        warm: new(0xFF, 0x7A, 0x1A),
        tint: new(0xB8, 0xF4, 0xFF),
        ink: new(0xE6, 0xFB, 0xFF),
        dim: new(0x6F, 0x97, 0xA3),
        dimmer: new(0x20, 0x38, 0x40),
        bg: new(0x00, 0x05, 0x08),
        panelBg: new(0x06, 0x14, 0x19),
        good: new(0x3D, 0xFF, 0xB0),
        bad: new(0xFF, 0x3B, 0x3B),
        warn: null,
        gradient: [new(0x0A, 0x4F, 0x66), new(0x00, 0xA8, 0xC8), new(0x00, 0xE5, 0xFF), new(0x7D, 0xF9, 0xFF), new(0xFF, 0x9E, 0x1B)]);

    /// <summary>Blade Runner 2049's Los Angeles (2026-09-27, the user's pick): sodium-lamp orange, smog teal and tan, muted, on wet slate; a pink-leaning red for failures, apart from the orange.</summary>
    public static readonly ThemePalette Replicant = Build(
        "replicant", "smog and sodium",
        primary: new(0xFF, 0x8A, 0x2A),
        secondary: new(0x2F, 0xD4, 0xC4),
        tertiary: new(0xC9, 0xA2, 0x6B),
        deep: new(0x5A, 0x3A, 0x1E),
        highlight: new(0xFF, 0xC7, 0x7A),
        warm: new(0xFF, 0x6A, 0x3D),
        tint: new(0x7F, 0xB8, 0xB0),
        ink: new(0xED, 0xE3, 0xD6),
        dim: new(0x8E, 0x85, 0x78),
        dimmer: new(0x3A, 0x35, 0x30),
        bg: new(0x0B, 0x0E, 0x10),
        panelBg: new(0x16, 0x1A, 0x1D),
        good: new(0x8F, 0xE3, 0xA0),
        bad: new(0xFF, 0x3D, 0x5A),
        warn: null,
        gradient: [new(0x1E, 0x4A, 0x4F), new(0x2F, 0xD4, 0xC4), new(0xC9, 0xA2, 0x6B), new(0xFF, 0x8A, 0x2A), new(0xFF, 0xC7, 0x7A)]);

    /// <summary>The deep sea's own light (2026-09-27, the user's pick): jellyfish cyan, violet and anglerfish yellow on midnight navy.</summary>
    public static readonly ThemePalette Abyssal = Build(
        "abyssal", "bioluminescent",
        primary: new(0x00, 0xFF, 0xD5),
        secondary: new(0x9D, 0x6B, 0xFF),
        tertiary: new(0xFF, 0xE4, 0x5C),
        deep: new(0x1B, 0x3A, 0x6B),
        highlight: new(0xFF, 0xF3, 0xA0),
        warm: new(0xFF, 0xB8, 0x6B),
        tint: new(0x6B, 0xD6, 0xFF),
        ink: new(0xE0, 0xF7, 0xFF),
        dim: new(0x6C, 0x8A, 0xA6),
        dimmer: new(0x1E, 0x2F, 0x48),
        bg: new(0x02, 0x0A, 0x1A),
        panelBg: new(0x0A, 0x18, 0x30),
        good: new(0x5C, 0xFF, 0xA0),
        bad: new(0xFF, 0x4F, 0x7A),
        warn: null,
        gradient: [new(0x1B, 0x3A, 0x6B), new(0x6B, 0xD6, 0xFF), new(0x00, 0xFF, 0xD5), new(0x9D, 0x6B, 0xFF), new(0xFF, 0xE4, 0x5C)]);

    /// <summary>Every theme, in menu order (the default first, newcomers last).</summary>
    public static readonly IReadOnlyList<ThemePalette> All = [Synthwave, Netrunner, Nostromo, Noir, Cyberpunk, Vaporwave, Mainframe, Grid, Replicant, Abyssal];

    /// <summary>True for the compiled themes; false for one read from <c>&lt;home&gt;/themes</c>.</summary>
    public bool IsBuiltIn => SourcePath is null;

    /// <summary>The colour of <paramref name="slot"/>.</summary>
    public Color ColorOf(ThemeColorSlot slot) => slot switch
    {
        ThemeColorSlot.Primary => Primary,
        ThemeColorSlot.Secondary => Secondary,
        ThemeColorSlot.Tertiary => Tertiary,
        ThemeColorSlot.Deep => Deep,
        ThemeColorSlot.Highlight => Highlight,
        ThemeColorSlot.Warm => Warm,
        ThemeColorSlot.Tint => Tint,
        ThemeColorSlot.Ink => Ink,
        ThemeColorSlot.Dim => Dim,
        ThemeColorSlot.Dimmer => Dimmer,
        ThemeColorSlot.Bg => Bg,
        ThemeColorSlot.PanelBg => PanelBg,
        ThemeColorSlot.Good => Good,
        ThemeColorSlot.Bad => Bad,
        ThemeColorSlot.Warn => Warn,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null),
    };

    /// <summary>Every colour, in <see cref="ThemeColorSlot"/> order.</summary>
    public Color[] Colors() => Enumerable.Range(0, ThemeKeys.Colors.Count).Select(i => ColorOf((ThemeColorSlot)i)).ToArray();

    /// <summary>
    /// A palette from <paramref name="colors"/> in <see cref="ThemeColorSlot"/> order (<see cref="ThemeFile"/>'s way in).
    /// </summary>
    public static ThemePalette FromColors(
        string name, string description, Color[] colors, Color[] gradient,
        IReadOnlyDictionary<ThemeStyleSlot, StyleOverride>? styles, string? sourcePath)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (colors.Length != ThemeKeys.Colors.Count)
        {
            throw new ArgumentException($"Expected {ThemeKeys.Colors.Count} colours, got {colors.Length}.", nameof(colors));
        }

        return new ThemePalette(name, description, colors[0], colors[1], colors[2], colors[3], colors[4], colors[5], colors[6],
            colors[7], colors[8], colors[9], colors[10], colors[11], colors[12], colors[13], colors[14], gradient, styles, sourcePath);
    }

    /// <summary>The gradient a theme that names none gets: secondary → tertiary → primary → warm → highlight (synthwave's sunset).</summary>
    public static Color[] DerivedGradient(Color primary, Color secondary, Color tertiary, Color warm, Color highlight) =>
        [secondary, tertiary, primary, warm, highlight];

    /// <summary>
    /// Value equality (2026-10-01): a user theme is read afresh at every scan, a new instance each time, so
    /// <see cref="Theme.Use"/> and <c>/theme</c> ask whether the <em>look</em> changed — the colours, the gradient,
    /// the style changes — not whether it is the same object. The generated equality would compare the array and the
    /// dictionary by reference.
    /// </summary>
    public bool Equals(ThemePalette? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return string.Equals(Name, other.Name, StringComparison.Ordinal)
            && string.Equals(Description, other.Description, StringComparison.Ordinal)
            && string.Equals(SourcePath, other.SourcePath, StringComparison.Ordinal)
            && Colors().SequenceEqual(other.Colors())
            && GradientStops.SequenceEqual(other.GradientStops)
            && SameStyles(Styles, other.Styles);
    }

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(Name), Primary, Bg);

    private static bool SameStyles(IReadOnlyDictionary<ThemeStyleSlot, StyleOverride>? a, IReadOnlyDictionary<ThemeStyleSlot, StyleOverride>? b)
    {
        int countA = a?.Count ?? 0, countB = b?.Count ?? 0;
        if (countA != countB)
        {
            return false;
        }

        return countA == 0 || a!.All(pair => b!.TryGetValue(pair.Key, out var other) && pair.Value == other);
    }

    /// <summary>
    /// A palette with synthwave's two derived slots filled in when a theme leaves them out:
    /// <c>Warn</c> is the <paramref name="highlight"/> (synthwave's amber) and the gradient runs
    /// secondary → tertiary → primary → warm → highlight (synthwave's cyan → violet → magenta →
    /// orange → amber).
    /// </summary>
    private static ThemePalette Build(
        string name, string description, Color primary, Color secondary, Color tertiary, Color deep,
        Color highlight, Color warm, Color tint, Color ink, Color dim, Color dimmer, Color bg, Color panelBg,
        Color good, Color bad, Color? warn, Color[]? gradient) =>
        new(name, description, primary, secondary, tertiary, deep, highlight, warm, tint, ink, dim, dimmer, bg, panelBg,
            good, bad, warn ?? highlight, gradient ?? DerivedGradient(primary, secondary, tertiary, warm, highlight));
}
