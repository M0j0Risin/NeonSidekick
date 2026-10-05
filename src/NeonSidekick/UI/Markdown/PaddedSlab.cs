using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI.Markdown;

/// <summary>
/// A code block's rows padded to its widest row in the block's style (2026-10-04, the UI review: the fill stopped where each line's
/// text did, so the slab's edge was ragged): one even slab, no wider than the width it is given. Rows only grow; none is added.
/// </summary>
internal sealed class PaddedSlab(IRenderable inner, Style style) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => inner.Measure(options, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var lines = Segment.SplitLines(inner.Render(options, maxWidth), maxWidth);
        int widest = Math.Min(maxWidth, lines.Count == 0 ? 0 : lines.Max(l => Segment.CellCount(l)));
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                yield return Segment.LineBreak;
            }

            foreach (var segment in lines[i])
            {
                yield return segment;
            }

            int pad = widest - Segment.CellCount(lines[i]);
            if (pad > 0)
            {
                yield return new Segment(new string(' ', pad), style);
            }
        }
    }
}
