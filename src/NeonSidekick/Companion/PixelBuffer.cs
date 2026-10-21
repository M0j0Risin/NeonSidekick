using Spectre.Console;

namespace NeonSidekick.Companion;

/// <summary>
/// A grid of pixels, each a color or empty (null: the terminal's own background shows through). Reads outside the grid are
/// empty and writes outside it are dropped, so painting code never has to clip.
/// </summary>
public sealed class PixelBuffer
{
    private readonly Color?[] _pixels;

    public PixelBuffer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        _pixels = new Color?[width * height];
    }

    /// <summary>A buffer the size of the robot's canvas.</summary>
    public static PixelBuffer ForRobot() => new(RobotMetrics.Width, RobotMetrics.Height);

    public int Width { get; }

    public int Height { get; }

    public Color? this[int x, int y] => Contains(x, y) ? _pixels[(y * Width) + x] : null;

    public bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public void Set(int x, int y, Color? color)
    {
        if (Contains(x, y))
        {
            _pixels[(y * Width) + x] = color;
        }
    }

    /// <summary>Fills the <paramref name="width"/> by <paramref name="height"/> rectangle at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void Fill(int x, int y, int width, int height, Color? color)
    {
        for (int row = y; row < y + height; row++)
        {
            for (int column = x; column < x + width; column++)
            {
                Set(column, row, color);
            }
        }
    }

    public void Clear() => Array.Clear(_pixels);

    /// <summary>Whether every pixel matches <paramref name="other"/>'s (same size required).</summary>
    public bool SameAs(PixelBuffer other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Width == other.Width && Height == other.Height && _pixels.AsSpan().SequenceEqual(other._pixels, EqualityComparer<Color?>.Default);
    }

    public void CopyTo(PixelBuffer target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Width != Width || target.Height != Height)
        {
            throw new ArgumentException("The buffers differ in size.", nameof(target));
        }

        _pixels.AsSpan().CopyTo(target._pixels);
    }
}
