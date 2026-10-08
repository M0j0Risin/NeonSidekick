using NeonSidekick.Camera;
using NeonSidekick.Files;

namespace NeonSidekick.Tests;

/// <summary>
/// Resolves once per assembly whether a real camera may be used (2026-10-02): <c>NEONSIDEKICK_TEST_CAMERA</c> is <c>1</c> (the
/// first camera) or a camera's name, on Windows or macOS 14 and later (AVFoundation since 2026-10-07; the camera permission is the
/// terminal's, granted once). Skipped without; never a CI safety net. The camera's LED lights while these
/// run. <c>NEONSIDEKICK_TEST_CAMERA_OUT</c>, a folder, keeps the snapped JPEG there to look at (the right way up, colours right).
/// </summary>
internal static class LiveCamera
{
    public const string Variable = "NEONSIDEKICK_TEST_CAMERA";
    public const string OutVariable = "NEONSIDEKICK_TEST_CAMERA_OUT";

    public static readonly string? Setting = Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value ? value.Trim() : null;

    public static string DeviceName => Setting is null or "1" ? "" : Setting;

    /// <summary>The platform's camera layer: Media Foundation on Windows, AVFoundation on a Mac.</summary>
    public static ICameraSystem NewSystem() =>
        OperatingSystem.IsWindows() ? new MediaFoundationCameraSystem()
        : OperatingSystem.IsMacOSVersionAtLeast(14) ? new MacCameraSystem()
        : throw new PlatformNotSupportedException(Unavailable);

    public static string? Unavailable =>
        !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOSVersionAtLeast(14) ? "The camera layer is Windows or macOS 14 and later only."
        : Setting is null ? $"{Variable} is not set (1 for the first camera, or a camera's name)."
        : null;
}

/// <summary>Skips unless <see cref="LiveCamera"/> allows the real camera.</summary>
public sealed class CameraFactAttribute : FactAttribute
{
    public CameraFactAttribute()
    {
        if (LiveCamera.Unavailable is { } why)
        {
            Skip = why;
        }
    }
}

/// <summary>
/// The platform's camera layer on a real camera (2026-10-02; on a Mac too since 2026-10-07): the list, a settled shot, one open
/// for two leases, the linger.
/// </summary>
public sealed class LiveCameraTests
{
    private static CameraSession NewSession(ICameraSystem system) =>
        new(system, () => new CameraOptions(LiveCamera.DeviceName, new CameraSize(1280, 720)), TimeProvider.System);

    [CameraFact]
    public void Lists_at_least_one_camera()
    {
        var devices = LiveCamera.NewSystem().List();

        Assert.NotEmpty(devices);
        Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.Id)));
    }

    [CameraFact]
    public async Task A_settled_shot_encodes_as_a_jpeg_sent_as_is()
    {
        using var session = NewSession(LiveCamera.NewSystem());
        using var lease = session.Acquire("test");

        var frame = await lease.NextFrameAsync(settled: true, CancellationToken.None);
        byte[] jpeg = CameraJpeg.Encode(frame);

        Assert.True(frame.Sequence >= CameraWarmup.Webcam.Frames);
        Assert.Equal(frame.Width * frame.Height * 4, frame.Bgrx.Length);
        Assert.True(ImageFile.TryLoad(jpeg, "shot.jpg", out var image, out _));
        Assert.Equal(ImageFile.Jpeg, image!.MediaType);
        Assert.Same(jpeg, image.Bytes);
        if (Environment.GetEnvironmentVariable(LiveCamera.OutVariable) is { Length: > 0 } folder)
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "live-camera-test.jpg"), jpeg);
        }
    }

    [CameraFact]
    public async Task Two_leases_share_one_open_and_the_linger_closes_it()
    {
        var counting = new CountingSystem(LiveCamera.NewSystem());
        using var session = NewSession(counting);

        var first = session.Acquire("one");
        var second = session.Acquire("two");
        _ = await first.NextFrameAsync(settled: false, CancellationToken.None);
        _ = await second.NextFrameAsync(settled: false, CancellationToken.None);
        first.Dispose();
        second.Dispose();
        var deadline = DateTime.UtcNow + CameraSession.Linger + TimeSpan.FromSeconds(5);
        while (session.State != CameraState.Off && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Equal(1, counting.Opens);
        Assert.Equal(CameraState.Off, session.State);
    }

    private sealed class CountingSystem(ICameraSystem inner) : ICameraSystem
    {
        public int Opens;

        public IReadOnlyList<CameraDevice> List() => inner.List();

        public ICameraStream Open(CameraDevice device, CameraSize target)
        {
            _ = Interlocked.Increment(ref Opens);
            return inner.Open(device, target);
        }
    }
}
