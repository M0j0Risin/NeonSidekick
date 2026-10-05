using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// A sent line and its pastes' preview under it, one write (<see cref="ScreenPane.CommitInput"/>). A preview of more than
/// <see cref="PreviewKeep"/> lines folds once the next thing is said (2026-10-04, the UI review: 25 lines stood inline under every
/// long paste) to one summary row, <see cref="Summary"/>, and unfolds with the transcript's folds (Ctrl+O, <c>/expand</c>).
/// </summary>
public sealed class SentLineView : IRenderable, IFoldLayout
{
    /// <summary>The preview lines that stay unfolded. Pinned.</summary>
    public const int PreviewKeep = 5;

    private readonly string _line;
    private readonly IReadOnlyList<string> _preview;

    /// <param name="lineMarkup">The sent line's markup (<see cref="InputLine.SubmittedMarkup"/>).</param>
    /// <param name="previewMarkup">The preview's markup (<see cref="InputLine.PreviewMarkup"/>), empty for none.</param>
    public SentLineView(string lineMarkup, string previewMarkup)
    {
        _line = lineMarkup ?? throw new ArgumentNullException(nameof(lineMarkup));
        ArgumentNullException.ThrowIfNull(previewMarkup);
        _preview = previewMarkup.Length == 0 ? [] : SplitMarkupLines(previewMarkup);
    }

    /// <summary>The folded (or unfolded) preview's row: <c>  ▸ 📋 25 lines of the paste</c>. Pinned.</summary>
    public static string Summary(int lines, bool expanded) =>
        InputLine.ContinuationIndent + (expanded ? ToolGroupText.ExpandedGlyph : ToolGroupText.CollapsedGlyph) + " 📋 "
        + lines.ToString(CultureInfo.InvariantCulture) + (lines == 1 ? " line of the paste" : " lines of the paste");

    public FoldLayout? Fold => _preview.Count <= PreviewKeep
        ? null
        : new FoldLayout(1, PreviewKeep, _preview.Count, new Text(Summary(_preview.Count, expanded: false), Theme.DimText), new Text(Summary(_preview.Count, expanded: true), Theme.DimText));

    public Measurement Measure(RenderOptions options, int maxWidth) => ((IRenderable)new Markup(_line)).Measure(options, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var segments = new List<Segment>();
        segments.AddRange(((IRenderable)new Markup(_line)).Render(options, maxWidth));
        segments.Add(Segment.LineBreak);
        foreach (string row in _preview)
        {
            segments.AddRange(((IRenderable)new Markup(row)).Render(options, maxWidth));
            segments.Add(Segment.LineBreak);
        }

        return segments;
    }

    /// <summary>A one-style markup (<c>[style]a\nb[/]</c>, <see cref="InputLine.PreviewMarkup"/>'s shape) as one markup per line, each closed.</summary>
    private static List<string> SplitMarkupLines(string markup)
    {
        int close = markup.IndexOf(']', StringComparison.Ordinal);
        if (!markup.StartsWith('[') || close < 0 || !markup.EndsWith("[/]", StringComparison.Ordinal))
        {
            return [markup];
        }

        string open = markup[..(close + 1)];
        return markup[(close + 1)..^3].Split('\n').Select(line => open + line + "[/]").ToList();
    }
}
