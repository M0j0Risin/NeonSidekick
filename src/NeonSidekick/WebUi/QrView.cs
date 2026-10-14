using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.WebUi;

/// <summary>
/// A <see cref="QrCode"/> drawn in the terminal (2026-10-14, <c>/webui</c>'s pane): two modules a cell, one above the other, as the upper
/// half block <c>▀</c> with its foreground the upper module and its background the lower one — always black on white, whatever the theme,
/// so a phone's camera reads it, inside a four-module white quiet zone. A cell is about twice as tall as it is wide, so the modules come
/// out square. <see cref="Columns"/> by <see cref="Rows"/> cells.
/// </summary>
public sealed class QrView : IRenderable
{
    /// <summary>The white border around the code, in modules, as the standard asks.</summary>
    public const int QuietZone = 4;

    private static readonly Color Dark = new(0, 0, 0);
    private static readonly Color Light = new(255, 255, 255);

    private readonly QrCode _code;

    public QrView(QrCode code)
    {
        _code = code ?? throw new ArgumentNullException(nameof(code));
    }

    /// <summary>The cells across: a module each, the quiet zone both sides.</summary>
    public int Columns => _code.Size + (2 * QuietZone);

    /// <summary>The rows down: two modules each.</summary>
    public int Rows => (_code.Size + (2 * QuietZone) + 1) / 2;

    public Measurement Measure(RenderOptions options, int maxWidth) => new(Columns, Columns);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        for (int row = 0; row < Rows; row++)
        {
            int y = (row * 2) - QuietZone;
            for (int x = -QuietZone; x < _code.Size + QuietZone; x++)
            {
                yield return new Segment("▀", new Style(_code[x, y] ? Dark : Light, _code[x, y + 1] ? Dark : Light));
            }

            yield return Segment.LineBreak;
        }
    }
}
