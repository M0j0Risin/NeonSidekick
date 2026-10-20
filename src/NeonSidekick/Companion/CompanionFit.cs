namespace NeonSidekick.Companion;

/// <summary>
/// How a character (<see cref="ICompanion"/>) fits a space: the top <see cref="PixelRows"/> rows of its
/// <see cref="Canvas"/> — all of them for the whole character, or <see cref="CompanionCanvas.HeadPixelRows"/> for just the
/// head when the space is too short for the rest. It's always drawn at its one size, a pixel a cell across and two a cell
/// down; it never grows.
/// </summary>
public readonly record struct CompanionFit(CompanionCanvas Canvas, int PixelRows)
{
    /// <summary>The cells across.</summary>
    public int Columns => Canvas.Width;

    /// <summary>The rows down: two pixels a row.</summary>
    public int Rows => PixelRows / 2;

    /// <summary>Whether this is just the head rather than the whole character.</summary>
    public bool IsHeadOnly => PixelRows < Canvas.Height;

    /// <summary>The whole character.</summary>
    public static CompanionFit Full(CompanionCanvas canvas) => new(canvas, canvas.Height);

    /// <summary>Just the head.</summary>
    public static CompanionFit HeadOnly(CompanionCanvas canvas) => new(canvas, canvas.HeadPixelRows);

    /// <summary>
    /// The best fit of <paramref name="canvas"/> into <paramref name="columns"/> × <paramref name="rows"/> cells: the whole
    /// character if it fits, else just the head, or null when not even the head fits.
    /// </summary>
    public static CompanionFit? For(CompanionCanvas canvas, int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(columns);
        ArgumentOutOfRangeException.ThrowIfNegative(rows);

        foreach (var fit in (ReadOnlySpan<CompanionFit>)[Full(canvas), HeadOnly(canvas)])
        {
            if (fit.Columns <= columns && fit.Rows <= rows)
            {
                return fit;
            }
        }

        return null;
    }
}
