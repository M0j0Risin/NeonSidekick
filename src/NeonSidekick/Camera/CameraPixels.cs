namespace NeonSidekick.Camera;

/// <summary>
/// The pixel copies the camera needs (2026-10-02), pure over spans: a locked RGB32 buffer to packed top-down BGRX whatever
/// its pitch (a bottom-up buffer has a negative one), and a box-filtered downscale for the live view and the change detector.
/// </summary>
public static class CameraPixels
{
    public const int BytesPerPixel = 4;

    /// <summary>
    /// Copies <paramref name="height"/> rows of <paramref name="width"/> BGRX pixels out of <paramref name="buffer"/>, row 0
    /// at <paramref name="firstRow"/> and each next row <paramref name="pitch"/> bytes on (negative for a bottom-up buffer),
    /// into <paramref name="destination"/> packed and top-down. Throws when a row would fall outside either span.
    /// </summary>
    public static void CopyTopDown(ReadOnlySpan<byte> buffer, int firstRow, int pitch, int width, int height, Span<byte> destination)
    {
        int rowBytes = width * BytesPerPixel;
        if (width <= 0 || height <= 0 || Math.Abs(pitch) < rowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(pitch), "The pitch is shorter than a row.");
        }

        if (destination.Length < rowBytes * height)
        {
            throw new ArgumentException("The destination is too small.", nameof(destination));
        }

        for (int y = 0; y < height; y++)
        {
            buffer.Slice(firstRow + (y * pitch), rowBytes).CopyTo(destination.Slice(y * rowBytes, rowBytes));
        }
    }

    /// <summary>
    /// The size <paramref name="width"/>×<paramref name="height"/> scales to with its longer side at most
    /// <paramref name="maxSide"/> (never enlarged; each side at least 1).
    /// </summary>
    public static (int Width, int Height) Fit(int width, int height, int maxSide)
    {
        int longer = Math.Max(width, height);
        if (longer <= maxSide)
        {
            return (width, height);
        }

        double scale = (double)maxSide / longer;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// <paramref name="frame"/> box-averaged to <paramref name="width"/>×<paramref name="height"/> BGRX (each target pixel the
    /// mean of the source pixels it covers), mirrored left to right when <paramref name="mirror"/> is set. The frame itself
    /// when the size already matches and there is no mirror.
    /// </summary>
    public static byte[] Scale(CameraFrame frame, int width, int height, bool mirror = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (width == frame.Width && height == frame.Height && !mirror)
        {
            return frame.Bgrx;
        }

        var output = new byte[width * height * BytesPerPixel];
        ScaleInto(frame, width, height, mirror, output);
        return output;
    }

    /// <summary><see cref="Scale"/> into <paramref name="output"/> (at least <c>width × height × 4</c> bytes): the live view's reused buffers.</summary>
    public static void ScaleInto(CameraFrame frame, int width, int height, bool mirror, byte[] output)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length < width * height * BytesPerPixel)
        {
            throw new ArgumentException("The output is too small.", nameof(output));
        }

        ReadOnlySpan<byte> source = frame.Bgrx;
        int sourceRow = frame.Width * BytesPerPixel;
        for (int y = 0; y < height; y++)
        {
            int y0 = (int)((long)y * frame.Height / height);
            int y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * frame.Height / height));
            for (int x = 0; x < width; x++)
            {
                int x0 = (int)((long)x * frame.Width / width);
                int x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * frame.Width / width));
                int b = 0, g = 0, r = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * sourceRow;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int at = row + (sx * BytesPerPixel);
                        b += source[at];
                        g += source[at + 1];
                        r += source[at + 2];
                    }
                }

                int count = (y1 - y0) * (x1 - x0);
                int target = ((y * width) + (mirror ? width - 1 - x : x)) * BytesPerPixel;
                output[target] = (byte)(b / count);
                output[target + 1] = (byte)(g / count);
                output[target + 2] = (byte)(r / count);
                output[target + 3] = 0xFF;
            }
        }
    }

    /// <summary>The frame's mean luma, 0–255 (77R + 150G + 29B over 256), on a coarse grid: the check's black-frame test.</summary>
    public static double MeanLuma(CameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Bgrx.Length == 0)
        {
            return 0;
        }

        long sum = 0;
        long count = 0;
        int stepX = Math.Max(1, frame.Width / 64);
        int stepY = Math.Max(1, frame.Height / 36);
        for (int y = 0; y < frame.Height; y += stepY)
        {
            for (int x = 0; x < frame.Width; x += stepX)
            {
                int at = ((y * frame.Width) + x) * BytesPerPixel;
                sum += Luma(frame.Bgrx[at], frame.Bgrx[at + 1], frame.Bgrx[at + 2]);
                count++;
            }
        }

        return count == 0 ? 0 : (double)sum / count;
    }

    /// <summary>A pixel's luma, 0–255.</summary>
    public static int Luma(byte b, byte g, byte r) => ((77 * r) + (150 * g) + (29 * b)) >> 8;
}
