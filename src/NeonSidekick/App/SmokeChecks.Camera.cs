using NeonSidekick.Camera;
using NeonSidekick.Files;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>camera:mf</c> (2026-10-02): the camera's Media Foundation layer in the published binary without a camera — the
    /// attribute store, sample, buffer and 2-D buffer vtable slots each called and read back, and the device list read (nothing
    /// activated, so no LED). The reader and activate slots need a camera: <c>--camera-check</c> proves those. Skipped off
    /// Windows, and where Media Foundation is not installed (Windows N, a Server without the feature), which is a sentence in
    /// the app, not a broken binary.
    /// </summary>
    public static SmokeCheck ProbeCameraMf()
    {
        const string name = "camera:mf";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        try
        {
            return new SmokeCheck(name, true, MediaFoundationCameraSystem.ProbeSlots());
        }
        catch (CameraException e) when (e.Failure == CameraFailure.NoMediaFoundation)
        {
            return new SmokeCheck(name, true, "skipped: Media Foundation is not installed");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// <c>camera:avf</c> (2026-10-07, the camera on macOS; <c>camera:mf</c>'s twin): AVFoundation's capture classes bound, the runtime
    /// delegate class answering its selector, the permission's global block made and the status read (never a request: no question
    /// on a runner or a headless box), a 32BGRA pixel buffer's rows copied as a frame's are, and the cameras listed without opening
    /// one (no LED). "No camera listed" passes (GitHub's macOS runner). Skipped off macOS and before macOS 14 (no external type).
    /// </summary>
    public static SmokeCheck ProbeCameraAvf()
    {
        const string name = "camera:avf";
        if (!OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            return new SmokeCheck(name, true, OperatingSystem.IsMacOS() ? "skipped: macOS 14 or later needed" : "skipped: not macOS");
        }

        try
        {
            return new SmokeCheck(name, true, MacCameraSystem.Probe());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// <c>camera:encode</c> (2026-10-02): a made-up 64×48 BGRX frame (red left, blue right) through <see cref="CameraJpeg"/>
    /// — MagicScaler from the pixels, the X byte dropped — must come out a JPEG that <see cref="ImageFile.TryLoad"/> keeps
    /// byte for byte, and decode red on the left; the change detector must see no change in a frame against itself.
    /// </summary>
    public static SmokeCheck ProbeCameraEncode()
    {
        const string name = "camera:encode";
        try
        {
            const int width = 64;
            const int height = 48;
            var pixels = new byte[width * height * CameraPixels.BytesPerPixel];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int at = ((y * width) + x) * CameraPixels.BytesPerPixel;
                    pixels[at + (x < width / 2 ? 2 : 0)] = 0xFF;
                }
            }

            var frame = new CameraFrame(width, height, pixels, 1, DateTimeOffset.UnixEpoch);
            byte[] jpeg = CameraJpeg.Encode(frame);
            if (!ImageFile.TryLoad(jpeg, "probe.jpg", out var image, out var failure) || image!.MediaType != ImageFile.Jpeg || !ReferenceEquals(image.Bytes, jpeg))
            {
                return new SmokeCheck(name, false, $"the JPEG was not kept as is ({failure})");
            }

            var decoded = Viewer.ViewerImage.Decode(jpeg, "probe.jpg") ?? throw new InvalidOperationException("the JPEG did not decode");
            int left = ((height / 2 * decoded.Width) + 4) * CameraPixels.BytesPerPixel;
            bool red = decoded.Bgrx[left + 2] > 200 && decoded.Bgrx[left] < 60;
            double change = FrameDiff.Changed(FrameDiff.Grid(frame), FrameDiff.Grid(frame));
            return new SmokeCheck(name, red && change == 0, red && change == 0
                ? $"{jpeg.Length} bytes JPEG {image.Width}x{image.Height}, red stays red, no change against itself"
                : $"red decoded as B{decoded.Bgrx[left]} G{decoded.Bgrx[left + 1]} R{decoded.Bgrx[left + 2]}, change {change}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
    }
}
