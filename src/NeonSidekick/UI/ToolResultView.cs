using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// A tool's result under its call (2026-10-04, the UI review: one line cut at 200 characters, the name repeated, so an expanded run
/// could never show more): <c>→</c> and its first line in the call's text column, then up to <see cref="MaxDetail"/> more lines of
/// it, and <c>… N more lines</c> past them. With more than its first line it folds (<see cref="IFoldLayout"/>) once its tool run is
/// over — the first line and <see cref="ToolCallText.MoreLines"/> — and unfolds with the run (Ctrl+O, <c>/expand</c>). A failed one
/// (<see cref="TranscriptRenderer.IsToolFailure"/>) wears <see cref="TranscriptRenderer.ToolFailedGlyph"/> in the warning colour.
/// </summary>
public sealed class ToolResultView : IRenderable, IFoldLayout
{
    /// <summary>The most lines of a result kept under its first. Pinned.</summary>
    public const int MaxDetail = 12;

    /// <summary>What a result line starts with after the glyph's column. Pinned.</summary>
    public const string Arrow = "→ ";

    private readonly string _head;
    private readonly IReadOnlyList<string> _detail;
    private readonly int _hidden;
    private readonly bool _failed;

    public ToolResultView(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        _failed = TranscriptRenderer.IsToolFailure(text);
        _head = lines.Count == 0 ? "" : lines[0];
        var rest = lines.Skip(1).ToList();
        _detail = rest.Take(MaxDetail).ToList();
        _hidden = rest.Count - _detail.Count;
    }

    /// <summary>The lines under the first, the ones past <see cref="MaxDetail"/> counted too.</summary>
    public int MoreLines => _detail.Count + _hidden;

    private string Lead => _failed ? TranscriptRenderer.ToolFailedGlyph : TranscriptRenderer.ToolAnswerIndent;

    private Style HeadStyle => new(foreground: _failed ? Theme.Warn : Theme.Dim);

    /// <summary>The first line's markup: the glyph's column, the arrow, the line cut to <see cref="TranscriptRenderer.ToolTextLimit"/> cells.</summary>
    public string HeadMarkup => Theme.ColorMarkup(_failed ? Theme.Warn : Theme.Dim, Lead + Arrow + TranscriptRenderer.Truncate(_head, TranscriptRenderer.ToolTextLimit));

    public FoldLayout? Fold => MoreLines == 0
        ? null
        : new FoldLayout(0, 1, MoreLines + 1,
            new Markup(HeadMarkup + " " + Theme.DimMarkup(ToolCallText.MoreLines(MoreLines))),
            new Markup(HeadMarkup));

    public Measurement Measure(RenderOptions options, int maxWidth) => new(Math.Min(maxWidth, 8), maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var segments = new List<Segment>();
        segments.AddRange(TranscriptRenderer.Hanging(HeadMarkup).Render(options, maxWidth));
        segments.Add(Segment.LineBreak);
        var dim = new Style(foreground: Theme.Dim);
        string indent = TranscriptRenderer.ToolAnswerIndent + "  ";
        foreach (string line in _detail)
        {
            segments.AddRange(new Markdown.HangingIndent(indent, indent, dim, new Text(line, dim)).Render(options, maxWidth));
            segments.Add(Segment.LineBreak);
        }

        if (_hidden > 0)
        {
            segments.Add(new Segment(indent + "… " + _hidden.ToString(System.Globalization.CultureInfo.InvariantCulture) + (_hidden == 1 ? " more line" : " more lines"), dim));
            segments.Add(Segment.LineBreak);
        }

        return segments;
    }
}
