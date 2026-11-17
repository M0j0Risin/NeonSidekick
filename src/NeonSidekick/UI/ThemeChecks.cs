using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>One pair of a palette's colors that reads less than it should: the pair in the theme file's words, its ratio and the target.</summary>
public sealed record ContrastProblem(string Pair, double Ratio, double Target);

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
        void Need(string pair, Color a, Color b, double target)
        {
            double ratio = ColorContrast.Contrast(a, b);
            if (ratio < target)
            {
                problems.Add(new ContrastProblem(pair, ratio, target));
            }
        }

        Need(ThemeText.PanelPair, palette.PanelBg, palette.Bg, PanelTarget);
        Need(ThemeText.DimmerPair, palette.Dimmer, palette.Bg, DimmerTarget);
        Need(ThemeText.DeepPair, palette.Deep, palette.Bg, DeepTarget);
        Need(ThemeText.InkPair, palette.Ink, palette.Bg, TextTarget);
        Need(ThemeText.InkPanelPair, palette.Ink, palette.PanelBg, TextTarget);
        Need(ThemeText.DimPanelPair, palette.Dim, palette.PanelBg, TextTarget);
        foreach (var (name, color) in Accents(palette))
        {
            Need(ThemeText.PanelRolePair(name), color, palette.PanelBg, TextTarget);
        }

        Need(ThemeText.BgPrimaryPair, palette.Bg, palette.Primary, TextTarget);
        var styles = Theme.StylesBuilt(palette);
        foreach (var (slab, under) in new[] { ("good", palette.Good), ("bad", palette.Bad) })
        {
            var fill = Theme.Mix(palette.Bg, under, Theme.DiffSlabShare);
            foreach (var (_, slot) in Theme.CodeSlots)
            {
                var ink = styles(slot).Foreground;
                Need(ThemeText.SlabPair(ThemeKeys.Styles[(int)slot], slab), ink == Color.Default ? palette.Ink : ink, fill, CodeTarget);
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

    private static (string, Color)[] Accents(ThemePalette p) =>
        [("primary", p.Primary), ("secondary", p.Secondary), ("tertiary", p.Tertiary), ("highlight", p.Highlight), ("warm", p.Warm), ("tint", p.Tint),
         ("good", p.Good), ("bad", p.Bad), ("warn", p.Warn)];
}
