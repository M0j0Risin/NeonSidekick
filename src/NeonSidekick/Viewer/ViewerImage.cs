using System.Drawing;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.UI;
using PhotoSauce.MagicScaler;

namespace NeonSidekick.Viewer;

/// <summary>
/// A decoded picture ready for the window: <see cref="Width"/> × <see cref="Height"/> pixels, top row first, four bytes each
/// in GDI's blue-green-red-unused order (a 32-bit DIB), transparency already blended over black — the window's background.
/// </summary>
public sealed record ViewerBitmap(int Width, int Height, byte[] Bgrx);

/// <summary>
/// The viewer's decoder (2026-09-27): a file's bytes through Windows' own codecs (WIC, driven by MagicScaler — the same
/// road <see cref="ImageFile"/> and <see cref="ImageThumbnail"/> take, so nothing new is linked) into a
/// <see cref="ViewerBitmap"/>, downscaled only past <see cref="MaxSide"/> (the window scales the rest as it draws, so a
/// resize never decodes again). A file ComfyUI is still writing is either locked or not a picture yet, so
/// <see cref="LoadAsync"/> tries <see cref="Attempts"/> times <see cref="RetryDelay"/> apart, as FolderPictureViewer
/// does. Never throws for a bad file: null, logged at Trace.
/// </summary>
public static class ViewerImage
{
    /// <summary>The longest side a picture is kept at; a bigger one is downscaled as it is decoded.</summary>
    public const int MaxSide = 4096;

    /// <summary>How many times a picture is read before it counts as unreadable.</summary>
    public const int Attempts = 5;

    /// <summary>The wait between two reads of a picture that is still being written.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>A picture file's bytes decoded, or null when the codecs refuse them. Pure apart from the codecs.</summary>
    public static ViewerBitmap? Decode(byte[] bytes, string name) => Decode(bytes, name, MaxSide, thumbnail: false);

    /// <summary>
    /// A thumbnail of a picture file's bytes (2026-10-04, the thumbnail browser): its longest side brought down to
    /// <paramref name="side"/> (never up: a smaller picture is kept whole), MagicScaler's fast hybrid scaling on, as befits a tile.
    /// Null when the codecs refuse them. Pure apart from the codecs.
    /// </summary>
    public static ViewerBitmap? DecodeThumbnail(byte[] bytes, string name, int side) => Decode(bytes, name, Math.Max(1, side), thumbnail: true);

    private static ViewerBitmap? Decode(byte[] bytes, string name, int maxSide, bool thumbnail)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            var info = ImageFileInfo.Load(bytes);
            if (info.Frames.Count == 0 || (long)info.Frames[0].Width * info.Frames[0].Height > ImageFile.MaxPixels * 4)
            {
                return null;
            }

            var settings = new ProcessImageSettings();
            if (Math.Max(info.Frames[0].Width, info.Frames[0].Height) > maxSide)
            {
                settings.Width = maxSide;
                settings.Height = maxSide;
                settings.ResizeMode = CropScaleMode.Max;
                if (thumbnail)
                {
                    settings.HybridMode = HybridScaleMode.Turbo;
                }
            }

            using var source = new MemoryStream(bytes, writable: false);
            using var pipeline = MagicImageProcessor.BuildPipeline(source, settings);

            // The pipeline hands back the source's own layout (ImageThumbnail's finding), read by the format it reports.
            var pixels = pipeline.PixelSource;
            int width = pixels.Width;
            int height = pixels.Height;
            int bytesPerPixel = ImageThumbnail.BytesPerPixel(pixels.Format);
            if (width < 1 || height < 1 || bytesPerPixel == 0)
            {
                DiagnosticLog.Trace("Viewer", $"Not shown: {name} is {width}x{height}, pixel format {pixels.Format}.");
                return null;
            }

            int stride = width * bytesPerPixel;
            var buffer = new byte[stride * height];
            pixels.CopyPixels(new Rectangle(0, 0, width, height), stride, buffer);
            return new ViewerBitmap(width, height, ToBgrx(buffer, bytesPerPixel));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Trace("Viewer", $"Not shown: {name}: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// <paramref name="path"/> read (shared, so ComfyUI's own handle is never in the way) and decoded, up to
    /// <see cref="Attempts"/> times <see cref="RetryDelay"/> apart; null when every attempt failed or it was cancelled.
    /// <paramref name="delay"/> is the wait (tests pass none); <paramref name="decode"/> the decoder (<see cref="Decode(byte[], string)"/>
    /// unless given: the thumbnail browser passes <see cref="DecodeThumbnail"/>'s, 2026-10-04).
    /// </summary>
    public static async Task<ViewerBitmap?> LoadAsync(string path, CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<byte[], string, ViewerBitmap?>? decode = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        delay ??= Task.Delay;
        decode ??= Decode;
        string name = Path.GetFileName(path);
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            byte[]? bytes = null;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
                if (stream.Length is > 0 and <= ImageFile.MaxFileBytes * 4)
                {
                    bytes = new byte[stream.Length];
                    await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Trace("Viewer", $"{name}: read {attempt} of {Attempts} failed: {e.Message}");
            }

            if (bytes is not null && decode(bytes, name) is { } bitmap)
            {
                return bitmap;
            }

            if (attempt < Attempts)
            {
                try
                {
                    await delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary><see cref="LoadAsync"/>, its answer handed to <paramref name="done"/> (the window's thread cannot await: its class is unsafe).</summary>
    public static async Task LoadThenAsync(string path, CancellationToken cancellationToken, Action<ViewerBitmap?> done, Func<byte[], string, ViewerBitmap?>? decode = null)
    {
        ArgumentNullException.ThrowIfNull(done);
        ViewerBitmap? bitmap = null;
        try
        {
            bitmap = await LoadAsync(path, cancellationToken, decode: decode).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Trace("Viewer", $"{Path.GetFileName(path)}: {ex.Message}");
        }

        done(bitmap);
    }

    /// <summary>Packed pixels to GDI's 32-bit order: BGR as it is, grey spread over the channels, BGRA blended over black. Pure.</summary>
    public static byte[] ToBgrx(ReadOnlySpan<byte> bytes, int bytesPerPixel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerPixel, 1);
        int count = bytes.Length / bytesPerPixel;
        var result = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            int at = i * bytesPerPixel;
            int to = i * 4;
            switch (bytesPerPixel)
            {
                case 1:
                    result[to] = result[to + 1] = result[to + 2] = bytes[at];
                    break;
                case 3:
                    result[to] = bytes[at];
                    result[to + 1] = bytes[at + 1];
                    result[to + 2] = bytes[at + 2];
                    break;
                default:
                    int alpha = bytes[at + 3];
                    result[to] = (byte)((bytes[at] * alpha + 127) / 255);
                    result[to + 1] = (byte)((bytes[at + 1] * alpha + 127) / 255);
                    result[to + 2] = (byte)((bytes[at + 2] * alpha + 127) / 255);
                    break;
            }
        }

        return result;
    }
}
