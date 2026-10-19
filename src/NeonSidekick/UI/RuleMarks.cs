using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>
/// <c>/mark</c>'s colors (2026-10-18, the user's ask and picks: a window told apart from the others at a glance, its rules around the draft
/// line drawn in a color of its own, kept per session; <see cref="ScreenPane.RuleMark"/>): ten fixed names, no hex, in this order — red,
/// orange, yellow, green, cyan, blue, purple, pink, white, gray — each a shade that reads on every built-in theme's background (5.0:1 to
/// 17.6:1, held by <c>RuleMarksTests</c>). <c>grey</c> reads as gray, case aside; <see cref="Default"/> is the theme's own rule. Pure.
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

    private static readonly IReadOnlyDictionary<string, Style> s_styles = s_colors.ToDictionary(p => p.Key, p => new Style(p.Value), StringComparer.Ordinal);

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

    /// <summary>The color of <paramref name="name"/> (one of <see cref="Names"/>).</summary>
    public static Color ColorOf(string name) => s_colors[name];

    /// <summary>The rules' style for <paramref name="name"/>; null for none or an unknown name.</summary>
    public static Style? StyleOf(string? name) => name is not null && s_styles.TryGetValue(name, out var style) ? style : (Style?)null;
}
