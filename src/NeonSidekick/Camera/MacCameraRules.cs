using System.Globalization;

namespace NeonSidekick.Camera;

/// <summary>
/// What the Mac camera layer decides without a camera (2026-10-07, Stage 2: the camera on macOS), pure so the tests cover it
/// everywhere: <see cref="MacCameraSystem"/> asks, AVFoundation answers. The device types it lists, the warm-up per type, a
/// format's FourCC, the frame rate a range runs at and the duration that asks for it, the authorization status, AVFoundation's
/// error codes, and the order of the list.
/// </summary>
public static class MacCameraRules
{
    /// <summary>The built-in FaceTime camera's device type.</summary>
    public const string BuiltInType = "AVCaptureDeviceTypeBuiltInWideAngleCamera";

    /// <summary>
    /// A USB webcam's and a Continuity Camera's device type (macOS 14 and later). The exe has no Info.plist, so without
    /// <c>NSCameraUseContinuityCameraDeviceType</c> an iPhone comes through this type, not <c>AVCaptureDeviceTypeContinuityCamera</c>
    /// (which lists nothing then; found probing on 15.7.9). Desk View is left out (the user's call): it is the desk from above,
    /// cut from the same sensor, not a camera of its own.
    /// </summary>
    public const string ExternalType = "AVCaptureDeviceTypeExternal";

    /// <summary>The types the discovery asks for, in that order.</summary>
    public static readonly string[] DeviceTypes = [BuiltInType, ExternalType];

    /// <summary>
    /// The warm-up for a device of <paramref name="deviceType"/>, <paramref name="continuity"/> for an iPhone as Continuity Camera
    /// (AVFoundation's <c>isContinuityCamera</c>; its type is the USB webcam's). Measured on 2026-10-07 at 1280x720: the built-in
    /// camera's first frame came 1.75 s after <c>startRunning</c> returned, two black, then its exposure ramped to frame ~30
    /// (brightness 37 to 86, 95% by frame 28); the iPhone's (an iPhone 15 Pro, still) came 0.9–2.1 s after, out of focus until
    /// frame ~15 and its brightness settling by frame ~25. The session's 800 ms had passed by then, so only the frames count and
    /// <see cref="CameraWarmup.Webcam"/>'s ten gave a dark or soft shot: <see cref="CameraWarmup.Apple"/>. The Logitech StreamCam
    /// looked settled by frame ~20, where <see cref="CameraWarmup.Webcam"/>'s 800 ms ends, so a USB camera keeps the webcam's.
    /// </summary>
    public static CameraWarmup WarmupFor(string? deviceType, bool continuity) =>
        continuity || string.Equals(deviceType, BuiltInType, StringComparison.Ordinal) ? CameraWarmup.Apple : CameraWarmup.Webcam;

    /// <summary>A CoreMedia subtype as its four characters (<c>420v</c>, <c>yuvs</c>), or <c>0x…</c> when they are not printable.</summary>
    public static string FourCc(uint code)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            uint c = (code >> (24 - (8 * i))) & 0xFF;
            if (c is < 0x20 or > 0x7E)
            {
                return "0x" + code.ToString("X8", CultureInfo.InvariantCulture);
            }

            chars[i] = (char)c;
        }

        return new string(chars);
    }

    /// <summary>
    /// The frames per second a format runs at within its range: <see cref="CameraFormats.PreferredFps"/> where the range holds it,
    /// else the range's nearer end (the StreamCam's "30" is 30.00003, a range of one point; the iPhone's 1–60; the built-in's 15–30).
    /// </summary>
    public static double FpsIn(double minFps, double maxFps) => Math.Clamp(CameraFormats.PreferredFps, minFps, Math.Max(minFps, maxFps));

    /// <summary>
    /// Which frame duration asks for <see cref="FpsIn"/>: AVFoundation throws (an Objective-C exception, fatal here) for a duration
    /// outside the range's own, and a one-point range's is not 1/30 (the StreamCam's is 1000000/30000030; found probing). So the
    /// range's ends are passed back as AVFoundation gave them, and 1/30 only strictly inside a range.
    /// </summary>
    public static FrameDuration DurationFor(double minFps, double maxFps)
    {
        double fps = FpsIn(minFps, maxFps);
        const double Near = 1e-6;
        return fps >= maxFps - Near ? FrameDuration.RangeShortest
            : fps <= minFps + Near ? FrameDuration.RangeLongest
            : FrameDuration.Thirtieth;
    }

    /// <summary><c>authorizationStatusForMediaType:</c>'s answer (0 not determined, 1 restricted, 2 denied, 3 authorized).</summary>
    public static CameraAccess Access(long status) => status switch
    {
        0 => CameraAccess.NotDetermined,
        1 => CameraAccess.Restricted,
        2 => CameraAccess.Denied,
        3 => CameraAccess.Authorized,
        _ => CameraAccess.Unknown,
    };

    /// <summary>
    /// The failure an <c>AVError</c> code stands for: -11814 DeviceNotConnected, -11815 DeviceInUseByAnotherApplication and -11817
    /// DeviceLockedForConfigurationByAnotherProcess, -11852 ApplicationIsNotAuthorizedToUseDevice; anything else is plain failure
    /// (its code in the detail).
    /// </summary>
    public static CameraFailure FailureOf(long code) => code switch
    {
        -11814 => CameraFailure.Unplugged,
        -11815 or -11817 => CameraFailure.InUse,
        -11852 => CameraFailure.Blocked,
        _ => CameraFailure.Failed,
    };

    /// <summary>
    /// The list in AVFoundation's order with the suspended cameras last (a MacBook's own with its lid closed): the empty setting
    /// takes the first, and a lid-closed one can't give a picture.
    /// </summary>
    public static IReadOnlyList<CameraDevice> Order(IEnumerable<(CameraDevice Device, bool Suspended)> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return devices.Select((d, i) => (d.Device, d.Suspended, i)).OrderBy(d => d.Suspended ? 1 : 0).ThenBy(d => d.i).Select(d => d.Device).ToList();
    }
}

/// <summary>Which duration <see cref="MacCameraRules.DurationFor"/> passes to <c>setActiveVideoMinFrameDuration:</c>.</summary>
public enum FrameDuration
{
    /// <summary>The range's <c>minFrameDuration</c>: its highest rate.</summary>
    RangeShortest,

    /// <summary>The range's <c>maxFrameDuration</c>: its lowest rate.</summary>
    RangeLongest,

    /// <summary>1/30 s, inside the range.</summary>
    Thirtieth,
}

/// <summary>The camera permission as macOS answers it for the terminal (<see cref="MacCameraRules.Access"/>).</summary>
public enum CameraAccess
{
    NotDetermined,
    Restricted,
    Denied,
    Authorized,
    Unknown,
}
