using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>
/// One named look (2026-09-23, the user's ask: themes beside synthwave, chosen on the General tab's
/// <c>Theme</c> row or with <c>/theme</c>): the colour of every role <see cref="Theme"/> composes its
/// styles from. The slots are roles, not hues — <see cref="Primary"/> is synthwave's magenta and
/// netrunner's green — so a call site reads the same under every theme. Since 2026-10-05 (the user's
/// ask) every theme is a JSON file: the built-ins are <c>assets/themes/&lt;category&gt;/*.json</c>,
/// embedded and built by <see cref="ThemeLibrary"/>, the user's are <c>&lt;home&gt;/themes</c>'
/// (<see cref="ThemeCatalog"/>); no code names a theme's colours but <see cref="Emergency"/>.
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
/// <param name="GradientStops">The stops of the banner title and its rule, left to right: 2 to 16 from a theme file.</param>
/// <param name="Styles">A theme file's changes to single styles (2026-10-01, the user's ask; <see cref="ThemeStyleSlot"/>); null when it has none.</param>
/// <param name="SourcePath">The file a user theme was read from (<see cref="ThemeCatalog"/>); null on a built-in (<see cref="ThemeLibrary"/>).</param>
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
    /// <summary>
    /// The stand-in when the built-in default does not load (2026-10-05, <see cref="ThemeLibrary"/>): plain greys and readable
    /// accents, named as the default so a saved setting still reads. The only colours left in code since the themes became
    /// <c>assets/themes</c>' files; a shipped build never shows it, since the tests hold every theme file to loading.
    /// </summary>
    public static readonly ThemePalette Emergency = new(
        ThemeName.Default, "stand-in",
        Primary: new(0x5F, 0xAF, 0xFF), Secondary: new(0x87, 0xD7, 0xD7), Tertiary: new(0xAF, 0x87, 0xFF), Deep: new(0x4E, 0x4E, 0x4E),
        Highlight: new(0xFF, 0xD7, 0x87), Warm: new(0xFF, 0xAF, 0x5F), Tint: new(0xD7, 0xAF, 0xD7), Ink: new(0xE4, 0xE4, 0xE4),
        Dim: new(0x9E, 0x9E, 0x9E), Dimmer: new(0x4A, 0x4A, 0x4A), Bg: new(0x0C, 0x0C, 0x0C), PanelBg: new(0x1C, 0x1C, 0x1C),
        Good: new(0x87, 0xD7, 0x87), Bad: new(0xFF, 0x5F, 0x5F), Warn: new(0xFF, 0xD7, 0x87),
        GradientStops: [new(0x87, 0xD7, 0xD7), new(0xAF, 0x87, 0xFF), new(0x5F, 0xAF, 0xFF), new(0xFF, 0xAF, 0x5F), new(0xFF, 0xD7, 0x87)]);

    /// <summary>True for a built-in (<see cref="ThemeLibrary"/>, no source path); false for one read from <c>&lt;home&gt;/themes</c>.</summary>
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

    /// <summary>The gradient a theme file that gives none gets, from its own colours: secondary → tertiary → primary → warm → highlight (synthwave's sunset).</summary>
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
}
