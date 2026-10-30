using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// One line of markup made for the width it is rendered at, then its line break (2026-10-29, the user's report: after a resize the
/// banner's path stayed where the old width put it and its rule kept the old length): the banner's title line and rule, and the
/// <c>/new</c> divider. The factory is handed the room and builds the line for it; whatever it shows that hangs on the theme or the
/// settings — the gradient's stops, the dim color, the working directory — its caller takes when the line is first written, so a
/// later <c>/cwd</c> or theme never changes a line already in the transcript, and only the cut and the padding follow a resize.
/// <see cref="IReflowable"/>: the transcript lays it out again at a new width.
/// </summary>
public sealed class WidthMarkup : IRenderable, IReflowable
{
    private readonly Func<int, string> _markup;

    /// <param name="markup">The line's markup for a width in cells.</param>
    public WidthMarkup(Func<int, string> markup)
    {
        _markup = markup ?? throw new ArgumentNullException(nameof(markup));
    }

    /// <summary>The line's markup for <paramref name="maxWidth"/> measured (2026-10-29, the resize review: a fixed 1..max mis-sized it in a container).</summary>
    public Measurement Measure(RenderOptions options, int maxWidth) => ((IRenderable)new Markup(_markup(maxWidth))).Measure(options, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var segments = ((IRenderable)new Markup(_markup(maxWidth))).Render(options, maxWidth).ToList();
        segments.Add(Segment.LineBreak);
        return segments;
    }
}
