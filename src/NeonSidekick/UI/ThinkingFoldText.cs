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

    /// <summary>
    /// <see cref="Summary(TimeSpan, bool)"/> with the thinking's first sentence after it (2026-10-04, the UI review: the fold said only how long),
    /// <c>  ▸ 💭 thought for 4.2s — The user wants the time…</c>; the bare summary for a thinking with no words.
    /// </summary>
    public static string Summary(TimeSpan elapsed, bool expanded, string? thinking) =>
        Gist(thinking) is { Length: > 0 } gist ? Summary(elapsed, expanded) + " — " + gist : Summary(elapsed, expanded);

    /// <summary>The most cells of a thinking's first sentence on its fold row. Pinned.</summary>
    public const int GistCells = 60;

    /// <summary>The first sentence of <paramref name="thinking"/> (to its first <c>.</c>, <c>!</c> or <c>?</c> before a blank, or its first line), cut to <see cref="GistCells"/>; empty for none. Pure.</summary>
    public static string Gist(string? thinking)
    {
        string text = (thinking ?? "").Trim();
        int line = text.IndexOf('\n');
        if (line >= 0)
        {
            text = text[..line].Trim();
        }

        for (int i = 0; i < text.Length - 1; i++)
        {
            if (text[i] is '.' or '!' or '?' && char.IsWhiteSpace(text[i + 1]))
            {
                text = text[..(i + 1)];
                break;
            }
        }

        return TranscriptRenderer.Truncate(text, GistCells);
    }

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
