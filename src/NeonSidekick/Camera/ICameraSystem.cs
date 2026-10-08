namespace NeonSidekick.Camera;

/// <summary>A camera Windows lists: the name the user knows it by and the symbolic link that opens it (it changes with the USB port, so only the name is saved).</summary>
public sealed record CameraDevice(string Name, string Id);

/// <summary>A size a camera is asked for or delivers.</summary>
public readonly record struct CameraSize(int Width, int Height)
{
    public long Area => (long)Width * Height;

    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>One of a camera's own formats: its size, frames per second and subtype (<c>NV12</c>, <c>YUY2</c>, <c>MJPG</c>…).</summary>
public sealed record CameraFormat(CameraSize Size, double Fps, string Subtype);

/// <summary>
/// One picture off the camera: <paramref name="Bgrx"/> is packed top-down 32-bit BGRX (<c>Width × 4</c> bytes a row, the
/// fourth byte unused) — the layout <c>ViewerBitmap</c> paints with no decode. <paramref name="Sequence"/> counts from 1
/// for each open of the device; <paramref name="At"/> is the session's clock when the frame arrived.
/// </summary>
public sealed record CameraFrame(int Width, int Height, byte[] Bgrx, long Sequence, DateTimeOffset At);

/// <summary>Why the camera gave no picture. <see cref="CameraText.Failure"/> is each one's sentence.</summary>
public enum CameraFailure
{
    /// <summary>Windows lists no camera.</summary>
    NoCamera,

    /// <summary>Windows' privacy setting keeps desktop apps from the camera (E_ACCESSDENIED).</summary>
    Blocked,

    /// <summary>Another app holds the camera.</summary>
    InUse,

    /// <summary>The camera went away while open (unplugged).</summary>
    Unplugged,

    /// <summary>Media Foundation itself is missing (Windows N without the Media Feature Pack, a Server without the feature).</summary>
    NoMediaFoundation,

    /// <summary>The camera opened but sent no picture in time.</summary>
    NoFrames,

    /// <summary>Not Windows: no camera layer at all.</summary>
    Unsupported,

    /// <summary>Anything else; the detail carries the HRESULT.</summary>
    Failed,

    /// <summary>A Mac's camera is suspended: the MacBook's own with its lid closed (2026-10-07).</summary>
    Suspended,

    /// <summary>macOS is asking the user whether the terminal may use the camera, and had no answer yet (2026-10-07).</summary>
    Asking,

    /// <summary>A Mac's camera is restricted by a configuration profile or Screen Time (2026-10-07).</summary>
    Restricted,
}

/// <summary>A camera failure with its kind; the message is the sentence the user sees.</summary>
public sealed class CameraException : Exception
{
    public CameraException()
        : this(CameraFailure.Failed, null)
    {
    }

    public CameraException(string message)
        : base(message)
    {
        Failure = CameraFailure.Failed;
    }

    public CameraException(string message, Exception innerException)
        : base(message, innerException)
    {
        Failure = CameraFailure.Failed;
    }

    public CameraException(CameraFailure failure, string? detail, Exception? inner = null)
        : base(CameraText.Failure(failure, detail), inner)
    {
        Failure = failure;
        Detail = detail;
    }

    public CameraFailure Failure { get; }

    public string? Detail { get; }
}

/// <summary>
/// The seam over the platform's cameras (2026-10-02): <see cref="MediaFoundationCameraSystem"/> on Windows,
/// <see cref="MacCameraSystem"/> over AVFoundation on macOS 14 and later (2026-10-07), a fake in the tests, null where there is none. <see cref="CameraSession"/> is the only caller; it opens a stream on a thread of its own
/// and reads it there, so an implementation may tie the stream to the opening thread.
/// </summary>
public interface ICameraSystem
{
    /// <summary>The cameras attached now, in Windows' order. Throws <see cref="CameraException"/> when the layer itself fails.</summary>
    IReadOnlyList<CameraDevice> List();

    /// <summary>Opens <paramref name="device"/> at the native format nearest <paramref name="target"/>. Throws <see cref="CameraException"/>.</summary>
    ICameraStream Open(CameraDevice device, CameraSize target);
}

/// <summary>
/// How long a camera's picture takes to settle after it opens (2026-10-02): a frame counts as settled after this many frames
/// <em>and</em> this much time — a webcam's first frames are dark or tinted while its exposure finds the room.
/// </summary>
public readonly record struct CameraWarmup(int Frames, TimeSpan Time)
{
    /// <summary>A webcam's: ten frames and 800 ms (the MX Brio settled at its tenth frame, 2026-10-02).</summary>
    public static readonly CameraWarmup Webcam = new(10, TimeSpan.FromMilliseconds(800));

    /// <summary>
    /// Apple's own cameras on a Mac, the MacBook's built-in one and an iPhone as Continuity Camera (2026-10-07, measured at
    /// 1280x720): thirty frames and 800 ms. Their first frame comes 0.9–2.1 s after the open, past the 800 ms, and their exposure
    /// (and the iPhone's focus) settles over the next twenty or so frames, so the frames decide (<see cref="MacCameraRules.WarmupFor"/>).
    /// </summary>
    public static readonly CameraWarmup Apple = new(30, TimeSpan.FromMilliseconds(800));

    /// <summary>No warm-up: every frame is settled (a fake camera).</summary>
    public static readonly CameraWarmup None = new(0, TimeSpan.Zero);
}

/// <summary>An open camera. Read and disposed on the thread that opened it; <see cref="Interrupt"/> is the one call from elsewhere.</summary>
public interface ICameraStream : IDisposable
{
    /// <summary>The camera's own format the stream runs at.</summary>
    CameraFormat Native { get; }

    /// <summary>When its frames count as settled (<see cref="CameraWarmup.Webcam"/> for a real camera).</summary>
    CameraWarmup Warmup { get; }

    /// <summary>
    /// Blocks for the next frame. With <paramref name="copy"/> false the picture is dropped unread (the stream still runs, so
    /// exposure settles) and the result is a frame with an empty <see cref="CameraFrame.Bgrx"/>. The stream leaves
    /// <see cref="CameraFrame.Sequence"/> and <see cref="CameraFrame.At"/> at their defaults; the session stamps them. Null once
    /// <see cref="Interrupt"/> has been called. Throws <see cref="CameraException"/> when the camera fails.
    /// </summary>
    CameraFrame? Read(bool copy);

    /// <summary>Makes a blocked or later <see cref="Read"/> return null. Thread-safe, idempotent.</summary>
    void Interrupt();
}
