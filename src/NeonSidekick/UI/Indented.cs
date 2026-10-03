using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// A renderable laid out <paramref name="cells"/> cells narrower and every line of it led by that many spaces (2026-10-03:
/// the <c>/sys</c> tabs' text under a <see cref="SectionRule"/>). Spectre's <see cref="Padder"/> does the left side too but
/// pads every line on the right to the longest one; this leaves a line's end where its text ends.
/// </summary>
internal sealed class Indented(IRenderable child, int cells) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth)
    {
        var inner = child.Measure(options, Math.Max(1, maxWidth - cells));
        return new(Math.Min(maxWidth, inner.Min + cells), Math.Min(maxWidth, inner.Max + cells));
    }

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var lead = new Segment(new string(' ', cells));
        var lines = Segment.SplitLines(child.Render(options, Math.Max(1, maxWidth - cells)));
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                yield return Segment.LineBreak;
            }

            yield return lead;
            foreach (var segment in lines[i])
            {
                yield return segment;
            }
        }
    }
}
