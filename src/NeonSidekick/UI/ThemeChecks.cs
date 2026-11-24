using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>
/// One pair of a palette's colors that reads less than it should: the pair in the theme file's words, its ratio and the target. Since 2026-11-24
/// (the <c>/theme edit</c> pane's band) the roles it judges, so a row can be marked and its band list its own pairs (none for a style on its own
/// ground), and for a code color on a diff slab that slab ("good" or "bad"), so the band can fold them into one line per slab.
/// </summary>
public sealed record ContrastProblem(string Pair, double Ratio, double Target, IReadOnlyList<ThemeColorSlot>? Roles = null, string? Slab = null);

/// <summary>
/// The contrast bars every theme is held to (2026-11-16, plan sharded-hugging-puppy #53): the ones the contrast review of 2026-10-16 moved all
/// sixty dark built-ins to, which until now only commit be65b8bb's message held — the panel set off from the page (1.20), the dimmer text still
/// seen (2.5) and the deep slab under a switched-off toolbar item (1.6), the dim text and every accent read on the panel (4.5), code colors on
/// both diff slabs (3) — with body text read (4.5) and the page's color carried on the primary (4.5: the log window's selection and the picture
/// menu's highlight put it there). The built-ins are held to none failing; <c>/theme edit</c> lists them as warnings that never block a save.
/// The code pairs and a style's own ground are judged on the styles as the terminal resolves them (2026-11-17, the admin-themes-render
/// review's #100: a file's role words and its <c>styles</c> moved what the role pairs judged), named by the style's key. Pure.
/// </summary>
public static class ThemeChecks
{
    /// <summary>Panel against the page.</summary>
    public const double PanelTarget = 1.20;

    /// <summary>The dimmer text on the page.</summary>
    public const double DimmerTarget = 2.5;

    /// <summary>The deep slab on the page.</summary>
    public const double DeepTarget = 1.6;

    /// <summary>Body text, the dim text and the accents, and the page's color on the primary.</summary>
    public const double TextTarget = 4.5;

    /// <summary>Code colors on the diff slabs.</summary>
    public const double CodeTarget = 3;

    /// <summary>Each pair of <paramref name="palette"/> under its target, in the order the checks run; empty when every pair reads.</summary>
    public static IReadOnlyList<ContrastProblem> Problems(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var problems = new List<ContrastProblem>();
        void Need(string pair, Color a, Color b, double target, string? slab = null, params ThemeColorSlot[] roles)
        {
            double ratio = ColorContrast.Contrast(a, b);
            if (ratio < target)
            {
                problems.Add(new ContrastProblem(pair, ratio, target, roles.Length > 0 ? roles : null, slab));
            }
        }

        Need(ThemeText.PanelPair, palette.PanelBg, palette.Bg, PanelTarget, null, ThemeColorSlot.PanelBg, ThemeColorSlot.Bg);
        Need(ThemeText.DimmerPair, palette.Dimmer, palette.Bg, DimmerTarget, null, ThemeColorSlot.Dimmer, ThemeColorSlot.Bg);
        Need(ThemeText.DeepPair, palette.Deep, palette.Bg, DeepTarget, null, ThemeColorSlot.Deep, ThemeColorSlot.Bg);
        Need(ThemeText.InkPair, palette.Ink, palette.Bg, TextTarget, null, ThemeColorSlot.Ink, ThemeColorSlot.Bg);
        Need(ThemeText.InkPanelPair, palette.Ink, palette.PanelBg, TextTarget, null, ThemeColorSlot.Ink, ThemeColorSlot.PanelBg);
        Need(ThemeText.DimPanelPair, palette.Dim, palette.PanelBg, TextTarget, null, ThemeColorSlot.Dim, ThemeColorSlot.PanelBg);
        foreach (var (name, color, role) in Accents(palette))
        {
            Need(ThemeText.PanelRolePair(name), color, palette.PanelBg, TextTarget, null, role, ThemeColorSlot.PanelBg);
        }

        Need(ThemeText.BgPrimaryPair, palette.Bg, palette.Primary, TextTarget, null, ThemeColorSlot.Bg, ThemeColorSlot.Primary);
        var styles = Theme.StylesBuilt(palette);
        foreach (var (slab, under, role) in new[] { ("good", palette.Good, ThemeColorSlot.Good), ("bad", palette.Bad, ThemeColorSlot.Bad) })
        {
            var fill = Theme.Mix(palette.Bg, under, Theme.DiffSlabShare);
            foreach (var (_, slot) in Theme.CodeSlots)
            {
                var ink = styles(slot).Foreground;
                // The code color's own role too (2026-11-24, the last-7 review's #19): the /theme edit row to change is the one it comes from.
                ThemeColorSlot[] roles = ink == Color.Default
                    ? [ThemeColorSlot.Bg, role, ThemeColorSlot.Ink]
                    : [.. new[] { ThemeColorSlot.Bg, role }.Concat(CodeColorRoles(palette, slot)).Distinct()];
                Need(ThemeText.SlabPair(ThemeKeys.Styles[(int)slot], slab), ink == Color.Default ? palette.Ink : ink, fill, CodeTarget, slab, roles);
            }
        }

        // A style that sets both its words and its own ground (a heading's "bg": "secondary") reads on that ground.
        foreach (var slot in Enum.GetValues<ThemeStyleSlot>())
        {
            var style = styles(slot);
            if (style.Foreground != Color.Default && style.Background != Color.Default)
            {
                Need(ThemeText.OwnGroundPair(ThemeKeys.Styles[(int)slot]), style.Foreground, style.Background, TextTarget);
            }
        }

        return problems;
    }

    /// <summary>
    /// The role each code style's color comes from with no theme change (<c>Theme</c>'s derived styles; <c>ThemeChecksTests</c> holds the two in
    /// step): a role that merely shares the color (ink drawn in the primary's color) is never blamed for it.
    /// </summary>
    internal static readonly IReadOnlyDictionary<ThemeStyleSlot, ThemeColorSlot> CodeRoles = new Dictionary<ThemeStyleSlot, ThemeColorSlot>
    {
        [ThemeStyleSlot.CodeKeyword] = ThemeColorSlot.Primary,
        [ThemeStyleSlot.CodeType] = ThemeColorSlot.Secondary,
        [ThemeStyleSlot.CodeString] = ThemeColorSlot.Highlight,
        [ThemeStyleSlot.CodeNumber] = ThemeColorSlot.Warm,
        [ThemeStyleSlot.CodeComment] = ThemeColorSlot.Dim,
        [ThemeStyleSlot.CodePunctuation] = ThemeColorSlot.Dim,
        [ThemeStyleSlot.CodeFunction] = ThemeColorSlot.Tertiary,
        [ThemeStyleSlot.CodeVariable] = ThemeColorSlot.Tint,
        [ThemeStyleSlot.CodeHeading] = ThemeColorSlot.Tertiary,
        [ThemeStyleSlot.CodeInserted] = ThemeColorSlot.Good,
        [ThemeStyleSlot.CodeDeleted] = ThemeColorSlot.Bad,
    };

    /// <summary>
    /// The roles a code style's color comes from (2026-11-24, the last-7 review's #19), walking an alias to its source (<c>attribute</c> starts
    /// from <c>type</c>'s final style, <c>tag</c> from <c>keyword</c>'s): a theme's own change of a slot's color, stored as a color and its role
    /// word lost, names the roles of that color (a free hex none); else the <see cref="CodeRoles"/> default. A change that sets only a decoration
    /// or a ground doesn't count. Pure.
    /// </summary>
    internal static IReadOnlyList<ThemeColorSlot> CodeColorRoles(ThemePalette palette, ThemeStyleSlot slot)
    {
        ArgumentNullException.ThrowIfNull(palette);
        for (ThemeStyleSlot? at = slot; at is { } s; at = ThemeKeys.AliasOf(s))
        {
            if (palette.Styles is { } changes && changes.TryGetValue(s, out var change) && change.Foreground is { } color)
            {
                return color == Color.Default ? [] : Enum.GetValues<ThemeColorSlot>().Where(r => palette.ColorOf(r) == color).ToList();
            }

            if (ThemeKeys.AliasOf(s) is null)
            {
                return CodeRoles.TryGetValue(s, out var role) ? [role] : [];
            }
        }

        return [];
    }

    private static (string, Color, ThemeColorSlot)[] Accents(ThemePalette p) =>
        [("primary", p.Primary, ThemeColorSlot.Primary), ("secondary", p.Secondary, ThemeColorSlot.Secondary), ("tertiary", p.Tertiary, ThemeColorSlot.Tertiary),
         ("highlight", p.Highlight, ThemeColorSlot.Highlight), ("warm", p.Warm, ThemeColorSlot.Warm), ("tint", p.Tint, ThemeColorSlot.Tint),
         ("good", p.Good, ThemeColorSlot.Good), ("bad", p.Bad, ThemeColorSlot.Bad), ("warn", p.Warn, ThemeColorSlot.Warn)];
}
