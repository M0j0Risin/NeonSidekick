using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.ActiveDirectory;

/// <summary>
/// <c>/ad</c>'s card on the info pane (2026-10-15, the AD gaps plan, §6; the <see cref="FittedLine"/> shape): laid out at the width Spectre gives it
/// — the pane builds a tab again on a resize — by the pure <see cref="DirectoryRender.ForCard"/>, which wraps and cuts so no line is wider, and
/// drawn one segment line per card line, the header rows and notes dim. The pane splits a tab's segments at line breaks only
/// (<c>ScreenPane.RenderLines</c> → <c>Segment.SplitLines</c>), never wrapping again, so a content line's index is the card line's: the lines last
/// drawn are kept, and a double-click's index finds its row's target (<see cref="TargetAt"/>), never parsed back out of the text.
/// </summary>
public sealed class DirectoryCardView(DirectoryAnswer answer) : IRenderable
{
    public DirectoryAnswer Answer { get; } = answer ?? throw new ArgumentNullException(nameof(answer));

    /// <summary>The lines last drawn.</summary>
    public IReadOnlyList<DirectoryCardLine> Lines { get; private set; } = [];

    public Measurement Measure(RenderOptions options, int maxWidth) => new(Math.Min(1, maxWidth), maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var lines = DirectoryRender.ForCard(Answer, maxWidth);
        Lines = lines;
        var segments = new List<Segment>(lines.Count * 2);
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                segments.Add(Segment.LineBreak);
            }

            var line = lines[i];
            segments.Add(new Segment(line.Text.Length == 0 ? " " : line.Text, line.Style switch
            {
                DirectoryCardStyle.Header or DirectoryCardStyle.Note => Theme.DimText,
                DirectoryCardStyle.Title => Theme.Label,
                DirectoryCardStyle.Head => new Style(decoration: Decoration.Bold),
                _ => Style.Plain,
            }));
        }

        return segments;
    }

    /// <summary>The object the content line at <paramref name="index"/> opens; null for none (a header, a note, a contact, past the end).</summary>
    public DirectoryRowTarget? TargetAt(int index) => index >= 0 && index < Lines.Count ? Lines[index].Target : null;
}
