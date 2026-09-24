using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// Where one picture sits on one transcript line (later on 2026-09-24, the user's ask: double-click a thumbnail to open
/// it in an image editor): its first column, its width in cells, and the screen's id for it. The pane keeps them on the
/// lines a picture's rows were stored as (<see cref="Scrollback.PictureAt"/>), so a click maps back to the picture.
/// </summary>
public readonly record struct PictureSpan(int Col, int Width, int Id);

/// <summary>
/// A renderable that says where its pictures landed: <see cref="Spans"/> holds one list per line of its last render,
/// read by <see cref="ScreenPane.WritePictures"/> once the pane has rendered it.
/// </summary>
public interface IPictureLayout
{
    IReadOnlyList<IReadOnlyList<PictureSpan>> Spans { get; }
}

/// <summary>
/// One picture centred in the transcript's width (<c>/view</c>, <c>/imagine</c>, the splash): <see cref="Align.Center"/>
/// over the canvas, as <see cref="TranscriptRenderer.Picture"/> drew it before, with its span on every line — the
/// canvas's column is <c>(width − picture) / 2</c>, Spectre's own centring.
/// </summary>
public sealed class CenteredPicture : IRenderable, IPictureLayout
{
    private readonly ImageThumbnail _thumbnail;
    private readonly int _id;
    private List<IReadOnlyList<PictureSpan>> _spans = [];

    public CenteredPicture(ImageThumbnail thumbnail, int id)
    {
        _thumbnail = thumbnail ?? throw new ArgumentNullException(nameof(thumbnail));
        _id = id;
    }

    public IReadOnlyList<IReadOnlyList<PictureSpan>> Spans => _spans;

    public Measurement Measure(RenderOptions options, int maxWidth) => ((IRenderable)Align.Center(_thumbnail.ToCanvas())).Measure(options, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var segments = ((IRenderable)Align.Center(_thumbnail.ToCanvas())).Render(options, maxWidth).ToList();
        int lines = Segment.SplitLines(segments).Count;
        int width = Math.Min(_thumbnail.Width, maxWidth);
        var span = new PictureSpan(Math.Max(0, (maxWidth - width) / 2), width, _id);
        _spans = Enumerable.Range(0, lines).Select(_ => (IReadOnlyList<PictureSpan>)[span]).ToList();
        return segments;
    }
}
