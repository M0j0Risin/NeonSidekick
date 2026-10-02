using System.Drawing;
using NeonSidekick.Files;
using PhotoSauce.MagicScaler;

namespace NeonSidekick.Camera;

/// <summary>
/// A camera frame to the JPEG the model and the working directory get (2026-10-02): MagicScaler straight from the frame's
/// pixels (an <see cref="IPixelSource"/> over it), scaled to fit <c>maxSide</c> and encoded at <see cref="ImageFile.JpegQuality"/>.
/// A photo goes as JPEG, never the PNG an in-memory bitmap through <see cref="ImageFile.TryLoad"/> would become (three times
/// the bytes); and a JPEG that fits <see cref="ImageFile.MaxSide"/> is one <see cref="ImageFile.TryLoad"/> keeps byte for byte.
/// MagicScaler 0.15's pixel sources have no BGRX format, so the source offers BGR and drops the fourth byte as it copies.
/// </summary>
public static class CameraJpeg
{
    /// <summary>The frame as JPEG bytes, its longer side at most <paramref name="maxSide"/> (never enlarged).</summary>
    public static byte[] Encode(CameraFrame frame, int maxSide = ImageFile.MaxSide, int quality = ImageFile.JpegQuality)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Bgrx.Length < frame.Width * frame.Height * CameraPixels.BytesPerPixel)
        {
            throw new ArgumentException("The frame holds no pixels.", nameof(frame));
        }

        var (width, height) = CameraPixels.Fit(frame.Width, frame.Height, Math.Min(maxSide, ImageFile.MaxSide));
        var settings = new ProcessImageSettings
        {
            Width = width,
            Height = height,
            ResizeMode = CropScaleMode.Stretch,
            EncoderOptions = new JpegEncoderOptions(quality, ChromaSubsampleMode.Default, false),
        };
        if (!settings.TrySetEncoderFormat(ImageFile.Jpeg))
        {
            throw new InvalidOperationException("No JPEG encoder.");
        }

        using var output = new MemoryStream();
        _ = MagicImageProcessor.ProcessImage(new FrameSource(frame), output, settings);
        return output.ToArray();
    }

    /// <summary>The frame as an attachment named <paramref name="name"/>, marked as the camera's.</summary>
    public static ImageAttachment Attachment(CameraFrame frame, string name, int maxSide = ImageFile.MaxSide)
    {
        ArgumentNullException.ThrowIfNull(frame);
        byte[] bytes = Encode(frame, maxSide);
        var (width, height) = CameraPixels.Fit(frame.Width, frame.Height, Math.Min(maxSide, ImageFile.MaxSide));
        return new ImageAttachment(name, bytes, ImageFile.Jpeg, width, height) { Camera = true };
    }

    private sealed class FrameSource(CameraFrame frame) : IPixelSource
    {
        public Guid Format => PixelFormats.Bgr24bpp;

        public int Width => frame.Width;

        public int Height => frame.Height;

        public void CopyPixels(Rectangle sourceArea, int cbStride, Span<byte> buffer)
        {
            ReadOnlySpan<byte> pixels = frame.Bgrx;
            for (int y = 0; y < sourceArea.Height; y++)
            {
                var source = pixels.Slice((((sourceArea.Y + y) * frame.Width) + sourceArea.X) * CameraPixels.BytesPerPixel, sourceArea.Width * CameraPixels.BytesPerPixel);
                var target = buffer.Slice(y * cbStride, sourceArea.Width * 3);
                for (int x = 0; x < sourceArea.Width; x++)
                {
                    target[x * 3] = source[x * 4];
                    target[(x * 3) + 1] = source[(x * 4) + 1];
                    target[(x * 3) + 2] = source[(x * 4) + 2];
                }
            }
        }
    }
}
