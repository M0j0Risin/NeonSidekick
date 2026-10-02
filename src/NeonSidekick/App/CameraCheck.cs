using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Camera;
using NeonSidekick.Files;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>--camera-check</c> (2026-10-02, the camera): the Media Foundation layer's proof on the published binary on the camera the
/// settings name — the cameras listed, the one opened (the format chosen, the time it took), frames read until the exposure
/// settles (the buffer's layout, the mean brightness, a warning for a black picture: a lens cover or a privacy shutter), one
/// encoded as the JPEG a shot would be, and the noise between two still frames (the floor under watch mode's change threshold).
/// Nothing is saved. Exit 0 only when every line passes. Not part of the build gate: it needs a camera, and lights its LED.
/// </summary>
internal static class CameraCheck
{
    public const string IntroLine = "Camera check: the camera's LED lights for a few seconds; nothing is saved.";

    /// <summary>A mean brightness (0–255) under this is a black picture.</summary>
    public const double BlackLuma = 8;

    public static async Task<int> RunAsync(IAnsiConsole console, CameraSession camera, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(camera);
        console.MarkupLine(Markup.Escape(IntroLine));
        var checks = new List<SmokeCheck>();
        IReadOnlyList<CameraDevice> devices;
        try
        {
            devices = await Task.Run(camera.List, cancellationToken).ConfigureAwait(false);
        }
        catch (CameraException e)
        {
            checks.Add(new SmokeCheck("camera:list", false, e.Message));
            return Report(console, checks);
        }

        checks.Add(new SmokeCheck("camera:list", devices.Count > 0, devices.Count == 0 ? CameraText.Failure(CameraFailure.NoCamera, null) : string.Join(" · ", devices.Select(d => d.Name))));
        if (devices.Count == 0)
        {
            return Report(console, checks);
        }

        using var lease = camera.Acquire("check");
        var clock = Stopwatch.StartNew();
        CameraFrame first;
        try
        {
            first = await lease.NextFrameAsync(settled: false, cancellationToken).ConfigureAwait(false);
        }
        catch (CameraException e)
        {
            checks.Add(new SmokeCheck("camera:open", false, e.Message));
            return Report(console, checks);
        }

        long opened = clock.ElapsedMilliseconds;
        string format = camera.Format is { } native ? CameraFormats.Describe(native) : "?";
        checks.Add(new SmokeCheck("camera:open", true, $"{camera.Device?.Name}: {format}, frames {first.Width}x{first.Height} RGB32, first frame in {Ms(opened)}"
            + (camera.TakeNotice() is { } notice ? " — " + notice : "")));

        CameraFrame settled;
        CameraFrame still;
        try
        {
            settled = await lease.NextFrameAsync(settled: true, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
            still = await lease.NextFrameAsync(settled: true, cancellationToken).ConfigureAwait(false);
        }
        catch (CameraException e)
        {
            checks.Add(new SmokeCheck("camera:frames", false, e.Message));
            return Report(console, checks);
        }

        double luma = CameraPixels.MeanLuma(settled);
        checks.Add(new SmokeCheck("camera:frames", true, $"settled at frame {N(settled.Sequence)} after {Ms(clock.ElapsedMilliseconds)}, mean brightness {luma.ToString("0", CultureInfo.InvariantCulture)}/255"
            + (luma < BlackLuma ? " — the picture is black: a lens cover or a privacy shutter?" : "")));

        try
        {
            byte[] jpeg = CameraJpeg.Encode(settled);
            bool kept = ImageFile.TryLoad(jpeg, "check.jpg", out var image, out _) && image!.MediaType == ImageFile.Jpeg && ReferenceEquals(image.Bytes, jpeg);
            checks.Add(new SmokeCheck("camera:encode", kept, $"{N(jpeg.Length)} bytes JPEG, {image?.Width}x{image?.Height}" + (kept ? ", sent as is" : ", re-encoded (it should not be)")));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            checks.Add(new SmokeCheck("camera:encode", false, $"{e.GetType().Name}: {e.Message}"));
        }

        double noise = FrameDiff.Changed(FrameDiff.Grid(settled), FrameDiff.Grid(still));
        checks.Add(new SmokeCheck("camera:noise", true, $"{(noise * 100).ToString("0.#", CultureInfo.InvariantCulture)}% of the picture changed between two frames 300 ms apart (watch mode's threshold should sit above it)"));
        return Report(console, checks);
    }

    private static string Ms(long milliseconds) => milliseconds.ToString(CultureInfo.InvariantCulture) + " ms";

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static int Report(IAnsiConsole console, IReadOnlyList<SmokeCheck> checks)
    {
        int failed = 0;
        foreach (var check in checks)
        {
            failed += check.Passed ? 0 : 1;
            string verdict = check.Passed ? Theme.ColorMarkup(Theme.Good, "PASS") : Theme.ColorMarkup(Theme.Bad, "FAIL");
            console.MarkupLine($"  {verdict}  {Markup.Escape(check.Name)}  {Theme.DimMarkup(check.Detail)}");
        }

        console.WriteLine();
        console.MarkupLine(failed == 0
            ? Theme.ColorMarkup(Theme.Good, $"CAMERA CHECK PASS  {N(checks.Count)} checks")
            : Theme.ColorMarkup(Theme.Bad, $"CAMERA CHECK FAIL  {N(failed)} of {N(checks.Count)} checks failed"));
        return failed == 0 ? 0 : 1;
    }
}
