using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>
/// <c>/mark</c>'s colors (2026-10-18, the user's ask and picks: a window told apart from the others at a glance, its rules around the draft
/// line drawn in a color of its own, kept per session; <see cref="ScreenPane.RuleMark"/>): ten fixed names, no hex, in this order — red,
/// orange, yellow, green, cyan, blue, purple, pink, white, gray — each a shade that reads on every built-in theme's background (4.5:1 at
/// least, held by <c>RuleMarksTests</c>). A light page (2026-11-16, plan sharded-hugging-puppy #53, the daylight themes) takes a deeper shade
/// of each, <c>white</c> the near-black of plain text there. <c>grey</c> reads as gray, case aside; <see cref="Default"/> is the theme's own rule. Pure.
/// </summary>
public static class RuleMarks
{
    /// <summary>The word that takes the mark off: the theme's rule again.</summary>
    public const string Default = "default";

    /// <summary>The names in the order <c>/mark</c> lists them.</summary>
    public static readonly IReadOnlyList<string> Names = ["red", "orange", "yellow", "green", "cyan", "blue", "purple", "pink", "white", "gray"];

    private static readonly IReadOnlyDictionary<string, Color> s_colors = new Dictionary<string, Color>(StringComparer.Ordinal)
    {
        ["red"] = new(0xFF, 0x5F, 0x5F),
        ["orange"] = new(0xFF, 0xA9, 0x4D),
        ["yellow"] = new(0xFF, 0xE0, 0x66),
        ["green"] = new(0x69, 0xDB, 0x7C),
        ["cyan"] = new(0x66, 0xD9, 0xE8),
        ["blue"] = new(0x5C, 0x9D, 0xFF),
        ["purple"] = new(0xB1, 0x97, 0xFC),
        ["pink"] = new(0xF7, 0x83, 0xAC),
        ["white"] = new(0xF1, 0xF3, 0xF5),
        ["gray"] = new(0xA0, 0xA2, 0xA6),
    };

    // The same names on a light page: each at least 4.5:1 on every daylight background.
    private static readonly IReadOnlyDictionary<string, Color> s_lightColors = new Dictionary<string, Color>(StringComparer.Ordinal)
    {
        ["red"] = new(0xC4, 0x2B, 0x2B),
        ["orange"] = new(0xA8, 0x4D, 0x06),
        ["yellow"] = new(0x80, 0x62, 0x00),
        ["green"] = new(0x25, 0x73, 0x2C),
        ["cyan"] = new(0x00, 0x70, 0x7C),
        ["blue"] = new(0x1F, 0x5F, 0xCC),
        ["purple"] = new(0x6B, 0x3F, 0xC4),
        ["pink"] = new(0xB0, 0x2F, 0x66),
        ["white"] = new(0x2A, 0x2E, 0x35),
        ["gray"] = new(0x5B, 0x62, 0x6C),
    };

    /// <summary>The name <paramref name="word"/> means, trimmed and case aside (<c>grey</c> is gray), else false.</summary>
    public static bool TryParse(string? word, out string name)
    {
        string folded = (word ?? "").Trim().ToLowerInvariant();
        if (folded == "grey")
        {
            folded = "gray";
        }

        name = s_colors.ContainsKey(folded) ? folded : "";
        return name.Length > 0;
    }

    /// <summary>The color of <paramref name="name"/> (one of <see cref="Names"/>) on a dark page, or a light one with <paramref name="light"/>.</summary>
    public static Color ColorOf(string name, bool light = false) => (light ? s_lightColors : s_colors)[name];

    /// <summary>The contrast every mark reaches on its page.</summary>
    public const double Target = 4.5;

    /// <summary>
    /// The rules' style for <paramref name="name"/> on <paramref name="page"/> (2026-11-17, the admin-themes-render review's #122, #129): the
    /// light table on a light page, else the dark one, and a shade under <see cref="Target"/> there moved toward <paramref name="ink"/> just
    /// far enough (a custom or mid-tone ground; every built-in reads as the tables are). Null for none or an unknown name.
    /// </summary>
    public static Style? StyleFor(string? name, Color page, Color ink) =>
        name is not null && s_colors.ContainsKey(name) ? new Style(Floor(ColorOf(name, ColorContrast.IsLight(page)), page, ink)) : (Style?)null;

    // The color at the target on the page: Theme's bisection, then a byte step at a time where its rounding landed a hair under.
    private static Color Floor(Color color, Color page, Color ink)
    {
        if (ColorContrast.Contrast(color, page) >= Target)
        {
            return color;
        }

        var moved = Theme.Quieted(color, page, ink, Target);
        for (int step = 0; step < 255 && ColorContrast.Contrast(moved, page) < Target && !moved.Equals(ink); step++)
        {
            moved = new Color(Toward(moved.R, ink.R), Toward(moved.G, ink.G), Toward(moved.B, ink.B));
        }

        return moved;
    }

    private static byte Toward(byte from, byte to) => from < to ? (byte)(from + 1) : from > to ? (byte)(from - 1) : from;
}
