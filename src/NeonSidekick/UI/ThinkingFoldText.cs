using System.Globalization;

namespace NeonSidekick.UI;

/// <summary>
/// The wording of the model's thinking block (2026-09-26, the user's ask: thinking streamed into the
/// transcript, folded once the answer starts): <c>  ▾ 💭 thinking…</c> over the block while it
/// streams, <c>  ▸ 💭 thought for 4.2s</c> once it folds, the triangle down while it shows every line
/// (<see cref="ToolGroupText.CollapsedGlyph"/>, <see cref="ToolGroupText.ExpandedGlyph"/>). The indent is
/// a tool line's, so the reply's glyph after it stands in its own column. Pinned.
/// </summary>
public static class ThinkingFoldText
{
    /// <summary>The thought bubble after the triangle, as the tools' 🛠️ and the code's 📜 are in theirs.</summary>
    public const string Glyph = "💭 ";

    /// <summary>The block's header while the thinking streams.</summary>
    public const string LiveHeader = "  " + ToolGroupText.ExpandedGlyph + " " + Glyph + "thinking…";

    /// <summary>The folded (or unfolded) block's summary: how long the thinking streamed.</summary>
    public static string Summary(TimeSpan elapsed, bool expanded) =>
        "  " + (expanded ? ToolGroupText.ExpandedGlyph : ToolGroupText.CollapsedGlyph) + " " + Glyph + "thought for " + Duration(elapsed);

    /// <summary><c>4.2s</c> under a minute, <c>1m 05s</c> from one on; never negative.</summary>
    public static string Duration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed < TimeSpan.FromSeconds(59.95))
        {
            return elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";
        }

        long seconds = (long)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}m {seconds % 60:00}s");
    }
}
