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
/// Pure.
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

        Need("panelBg on bg", palette.PanelBg, palette.Bg, PanelTarget);
        Need("dimmer on bg", palette.Dimmer, palette.Bg, DimmerTarget);
        Need("deep on bg", palette.Deep, palette.Bg, DeepTarget);
        Need("ink on bg", palette.Ink, palette.Bg, TextTarget);
        Need("ink on panelBg", palette.Ink, palette.PanelBg, TextTarget);
        Need("dim on panelBg", palette.Dim, palette.PanelBg, TextTarget);
        foreach (var (name, color) in Accents(palette))
        {
            Need(name + " on panelBg", color, palette.PanelBg, TextTarget);
        }

        Need("bg on primary", palette.Bg, palette.Primary, TextTarget);
        foreach (var (slab, under) in new[] { ("good", palette.Good), ("bad", palette.Bad) })
        {
            var fill = Theme.Mix(palette.Bg, under, Theme.DiffSlabShare);
            foreach (var (name, color) in Code(palette))
            {
                Need(name + " on the " + slab + " diff", color, fill, CodeTarget);
            }
        }

        return problems;
    }

    private static (string, Color)[] Accents(ThemePalette p) =>
        [("primary", p.Primary), ("secondary", p.Secondary), ("tertiary", p.Tertiary), ("highlight", p.Highlight), ("warm", p.Warm), ("tint", p.Tint),
         ("good", p.Good), ("bad", p.Bad), ("warn", p.Warn)];

    private static (string, Color)[] Code(ThemePalette p) =>
        [("primary", p.Primary), ("secondary", p.Secondary), ("tertiary", p.Tertiary), ("highlight", p.Highlight), ("warm", p.Warm), ("tint", p.Tint),
         ("dim", p.Dim), ("ink", p.Ink)];
}
