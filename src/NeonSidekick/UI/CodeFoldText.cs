using System.Globalization;

namespace NeonSidekick.UI;

/// <summary>
/// The wording of a folded code block's label line (2026-09-22, the user's ask: the tool runs' fold
/// for code): <c>▸ 📜 csharp · 57 lines</c> — the scroll (<see cref="Markdown.MarkdownView.CodeGlyph"/>)
/// after the triangle as the tools' glyph is in theirs, later that day — the triangle down (<see cref="ToolGroupText.ExpandedGlyph"/>)
/// while the block shows every line. It stands where the plain label stood, in its colour. Pinned.
/// </summary>
public static class CodeFoldText
{
    /// <summary>
    /// The copy glyph at a code block's label's end (2026-11-01, copy a code block): <c>📜 csharp ⧉</c>, or folded
    /// <c>▸ 📜 csharp · 57 lines ⧉</c>. A text glyph one cell wide, so it dims; a click on it copies the block. Pinned.
    /// </summary>
    public const string CopyGlyph = "⧉";

    /// <summary>What goes after the label: a space, then <see cref="CopyGlyph"/>.</summary>
    public const string CopyTail = " " + CopyGlyph;

    /// <summary>The label line of a block of <paramref name="lines"/> lines labelled <paramref name="label"/>, folded or not.</summary>
    public static string Summary(string label, int lines, bool expanded)
    {
        ArgumentNullException.ThrowIfNull(label);
        return (expanded ? ToolGroupText.ExpandedGlyph : ToolGroupText.CollapsedGlyph) + " " + Markdown.MarkdownView.CodeGlyph + label + " · "
            + lines.ToString(CultureInfo.InvariantCulture) + (lines == 1 ? " line" : " lines");
    }
}
