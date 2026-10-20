namespace NeonSidekick.Companion;

/// <summary>A rectangle of pixels; <see cref="Right"/> and <see cref="Bottom"/> are exclusive.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static PixelRect Empty => default;

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Whether <paramref name="inner"/> lies wholly inside this one.</summary>
    public bool Contains(PixelRect inner) => inner.X >= X && inner.Y >= Y && inner.Right <= Right && inner.Bottom <= Bottom;

    /// <summary>Whether the two share any pixel.</summary>
    public bool Overlaps(PixelRect other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    /// <summary>This rectangle moved by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public void Fill(PixelBuffer buffer, Spectre.Console.Color? color) => buffer.Fill(X, Y, Width, Height, color);
}
