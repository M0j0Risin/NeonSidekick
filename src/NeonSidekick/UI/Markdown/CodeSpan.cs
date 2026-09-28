namespace NeonSidekick.UI.Markdown;

/// <summary>
/// Where a top-level code block sits in a rendered reply (2026-09-22, the code fold): its label from
/// row <see cref="LabelRow"/> for <see cref="LabelRows"/> rows, then its <see cref="BodyRows"/> body
/// rows; <see cref="Label"/> is the label's text (the language, or <see cref="MarkdownView.CodeLabel"/>)
/// and <see cref="SourceLines"/> the block's own lines, which the fold counts (a wrapped line once).
/// <see cref="Open"/>: its closing fence has not streamed yet (<see cref="CodeBlock.Open"/>, 2026-09-27).
/// </summary>
public sealed record CodeSpan(int LabelRow, int LabelRows, int BodyRows, string Label, int SourceLines, bool Open = false)
{
    /// <summary>The first row after the block.</summary>
    public int End => LabelRow + LabelRows + BodyRows;

    /// <summary>The first body row.</summary>
    public int BodyRow => LabelRow + LabelRows;
}
