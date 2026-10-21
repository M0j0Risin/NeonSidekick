using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.Companion;

/// <summary>
/// A <see cref="PixelBuffer"/> drawn in the terminal, two pixels a cell, one above the other. A cell whose two pixels are
/// the same color is a space on that background (the background fills the whole cell, so there are no font seams across
/// solid areas, and no terminal can tint it the way it may adjust a foreground for contrast); two different colors are the
/// upper half block <c>▀</c> with its foreground the upper pixel and its background the lower one; only the upper is
/// <c>▀</c> on the terminal's own background, only the lower <c>▄</c>, neither a plain space. So nothing but the robot is
/// drawn and it sits on any theme.
///
/// <para>Cut to the buffer's top <c>pixelRows</c> rows (just the head, <see cref="CompanionFit"/>). Runs of identical cells go
/// out as one segment, so the robot doesn't cost a color code per cell. Reads the buffer when rendered, so one view shows
/// every frame of a buffer that is painted again.</para>
/// </summary>
public sealed class RobotView : IRenderable
{
    private const char Upper = '▀';
    private const char Lower = '▄';
    private const char Blank = ' ';

    private readonly PixelBuffer _pixels;
    private readonly int _pixelRows;

    public RobotView(PixelBuffer pixels, int? pixelRows = null)
    {
        _pixels = pixels ?? throw new ArgumentNullException(nameof(pixels));
        if (pixelRows is { } rows && (rows <= 0 || rows % 2 != 0 || rows > pixels.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelRows), rows, "Must be an even number of rows within the buffer.");
        }

        _pixelRows = pixelRows ?? pixels.Height;
    }

    /// <summary>The cells across: one a pixel.</summary>
    public int Columns => _pixels.Width;

    /// <summary>The rows down: two pixels each.</summary>
    public int Rows => (_pixelRows + 1) / 2;

    public Measurement Measure(RenderOptions options, int maxWidth) => new(Columns, Columns);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var run = new StringBuilder();
        for (int row = 0; row < Rows; row++)
        {
            (char Glyph, Style Style)? current = null;
            for (int x = 0; x < Columns; x++)
            {
                var cell = Cell(Pixel(x, row * 2), Pixel(x, (row * 2) + 1));
                if (current is { } c && c != cell)
                {
                    yield return new Segment(run.ToString(), c.Style);
                    run.Clear();
                }

                current = cell;
                run.Append(cell.Glyph);
            }

            if (current is { } last)
            {
                yield return new Segment(run.ToString(), last.Style);
                run.Clear();
            }

            yield return Segment.LineBreak;
        }
    }

    // Pixel (x, y); empty past the crop.
    private Color? Pixel(int x, int y) => y < _pixelRows ? _pixels[x, y] : null;

    private static (char Glyph, Style Style) Cell(Color? top, Color? bottom) => (top, bottom) switch
    {
        ({ } t, { } b) when t == b => (Blank, new Style(background: t)),
        ({ } t, { } b) => (Upper, new Style(t, b)),
        ({ } t, null) => (Upper, new Style(t)),
        (null, { } b) => (Lower, new Style(b)),
        _ => (Blank, Style.Plain),
    };
}
