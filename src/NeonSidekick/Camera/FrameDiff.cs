namespace NeonSidekick.Camera;

/// <summary>
/// How much a camera picture changed (2026-10-02, watch mode's cheap local test before anything goes to the model): each frame
/// is box-averaged to a <see cref="GridWidth"/>×<see cref="GridHeight"/> grid of luma, each grid's own mean is subtracted (so
/// the auto exposure drifting, or a cloud passing, moves nothing), and a cell has changed when the two differ by more than
/// <see cref="CellThreshold"/>. The answer is the fraction of cells that changed. Pure.
/// </summary>
public static class FrameDiff
{
    public const int GridWidth = 64;
    public const int GridHeight = 36;

    /// <summary>Luma steps (of 255) a cell must move to count: above a webcam's own noise, below a hand coming into view.</summary>
    public const int CellThreshold = 24;

    /// <summary>The frame's luma grid, row by row.</summary>
    public static byte[] Grid(CameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var grid = new byte[GridWidth * GridHeight];
        ReadOnlySpan<byte> pixels = frame.Bgrx;
        for (int gy = 0; gy < GridHeight; gy++)
        {
            int y0 = gy * frame.Height / GridHeight;
            int y1 = Math.Max(y0 + 1, (gy + 1) * frame.Height / GridHeight);
            for (int gx = 0; gx < GridWidth; gx++)
            {
                int x0 = gx * frame.Width / GridWidth;
                int x1 = Math.Max(x0 + 1, (gx + 1) * frame.Width / GridWidth);
                long sum = 0;
                int count = 0;
                // Every other pixel each way: the mean of a cell is as good, at a quarter of the reads.
                for (int y = y0; y < Math.Min(y1, frame.Height); y += 2)
                {
                    for (int x = x0; x < Math.Min(x1, frame.Width); x += 2)
                    {
                        int at = ((y * frame.Width) + x) * CameraPixels.BytesPerPixel;
                        sum += CameraPixels.Luma(pixels[at], pixels[at + 1], pixels[at + 2]);
                        count++;
                    }
                }

                grid[(gy * GridWidth) + gx] = (byte)(count == 0 ? 0 : sum / count);
            }
        }

        return grid;
    }

    /// <summary>The fraction (0–1) of cells that changed between two grids of <see cref="Grid"/>, each taken about its own mean.</summary>
    public static double Changed(byte[] before, byte[] after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Length != after.Length || before.Length == 0)
        {
            return 1;
        }

        double meanBefore = Mean(before);
        double meanAfter = Mean(after);
        int changed = 0;
        for (int i = 0; i < before.Length; i++)
        {
            if (Math.Abs((before[i] - meanBefore) - (after[i] - meanAfter)) > CellThreshold)
            {
                changed++;
            }
        }

        return (double)changed / before.Length;
    }

    /// <summary>Whether a change of <paramref name="fraction"/> reaches a threshold of <paramref name="percent"/> percent.</summary>
    public static bool Reaches(double fraction, int percent) => fraction * 100 >= percent;

    private static double Mean(byte[] grid)
    {
        long sum = 0;
        foreach (byte value in grid)
        {
            sum += value;
        }

        return (double)sum / grid.Length;
    }
}
