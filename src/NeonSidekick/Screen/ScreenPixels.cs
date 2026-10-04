namespace NeonSidekick.Screen;

/// <summary>The screen's pure pixel work (2026-10-04), apart from the Windows-only capture so the tests reach it anywhere.</summary>
public static class ScreenPixels
{
    /// <summary><paramref name="part"/> of a BGRX picture <paramref name="width"/> wide, clamped to it. Pure.</summary>
    public static ScreenFrame Crop(byte[] pixels, int width, ScreenRect part)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        int height = pixels.Length / 4 / width;
        int left = Math.Clamp(part.Left, 0, width);
        int top = Math.Clamp(part.Top, 0, height);
        int w = Math.Clamp(part.Width, 0, width - left);
        int h = Math.Clamp(part.Height, 0, height - top);
        if (w == width && h == height)
        {
            return new ScreenFrame(width, height, pixels);
        }

        var cut = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(pixels, (((top + y) * width) + left) * 4, cut, y * w * 4, w * 4);
        }

        return new ScreenFrame(w, h, cut);
    }
}
