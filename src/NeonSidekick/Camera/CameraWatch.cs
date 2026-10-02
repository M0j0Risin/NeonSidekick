using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.Camera;

/// <summary>
/// Watch mode (2026-10-02, the user's ask: the model with an eye on what the user does): the camera held open, a settled frame
/// sampled every <c>Camera watch interval</c> seconds, and compared on the spot (<see cref="FrameDiff"/>) with the last one the
/// model was given; one that changed by <c>Camera watch change</c> percent or more is kept — the newest wins, in memory only,
/// never saved — until the screen takes it (<see cref="TakePending"/>) to send with the user's next message or an unprompted
/// turn. The very first sample counts as a change: the model has seen nothing yet. A model sees pictures, not video, so this is
/// what "watching" can be: a cheap local test, and a picture only when something happened. Portable; the clock is injected.
/// </summary>
public sealed class CameraWatch : IDisposable
{
    /// <summary>The subfolder of the <c>Camera output folder</c> a double-clicked watch picture is written into.</summary>
    public const string FolderName = ".watch";

    /// <summary>
    /// The folder, relative to the working directory, a watch picture is written into when its thumbnail is double-clicked
    /// (2026-10-02, the user's call: under the camera's own folder, not the system's temp): <see cref="FolderName"/> in the
    /// <c>Camera output folder</c> (<c>camera_images/.watch</c> by default). Only a clicked picture lands there; it is cleared
    /// when watch mode stops and when a profile loads (<c>ChatScreen.ClearWatchFolder</c>). Pure.
    /// </summary>
    public static string FolderFor(string? outputFolder) => CameraCapture.Under(CameraCapture.OutputFolder(outputFolder), FolderName);

    /// <summary>The longer side a watch picture is sent at: enough to see what happens, light on the context.</summary>
    public const int MaxSide = 1280;

    private const string Category = "Camera";

    private readonly CameraSession _session;
    private readonly TimeProvider _time;
    private readonly Func<int> _threshold;
    private readonly Action _changed;
    private readonly object _gate = new();
    private CameraLease? _lease;
    private ITimer? _timer;
    private byte[]? _reference;
    private CameraFrame? _pending;
    private int _sampling;

    /// <param name="session">The shared camera.</param>
    /// <param name="time">The clock the sampling timer runs on.</param>
    /// <param name="threshold">The change, in percent, that keeps a frame; read at each sample.</param>
    /// <param name="changed">Told (on a pool thread) each time a changed frame is kept; it must not block.</param>
    public CameraWatch(CameraSession session, TimeProvider time, Func<int> threshold, Action changed)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _threshold = threshold ?? throw new ArgumentNullException(nameof(threshold));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
    }

    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _lease is not null;
            }
        }
    }

    /// <summary>The interval in force while running.</summary>
    public int Seconds { get; private set; }

    /// <summary>Whether a changed frame waits to be taken.</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    /// <summary>
    /// Starts (or re-times) the watch: every <paramref name="seconds"/> a sample, the first at once. Throws
    /// <see cref="CameraException"/> where there is no camera layer. <see cref="CameraSession.Revoke"/> of <c>watch</c> stops it.
    /// </summary>
    public void Start(int seconds)
    {
        lock (_gate)
        {
            _lease ??= _session.Acquire("watch", () => Stop());
            Seconds = seconds;
            _timer?.Dispose();
            _timer = _time.CreateTimer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromSeconds(seconds));
        }
    }

    /// <summary>Stops the watch and lets the camera go; a frame still waiting is dropped. True when it was running.</summary>
    public bool Stop()
    {
        CameraLease? lease;
        lock (_gate)
        {
            lease = _lease;
            _lease = null;
            _timer?.Dispose();
            _timer = null;
            _reference = null;
            _pending = null;
        }

        lease?.Dispose();
        return lease is not null;
    }

    /// <summary>
    /// What a watch picture is called, for the local time it was taken: <c>camera-watch-150210.jpg</c> (2026-10-02). A file name
    /// Windows takes (no colon) with the extension the viewer reads, since a double-click on its thumbnail writes the bytes to
    /// <see cref="FolderFor"/> under it and opens that; <c>camera (watch) 15:02:10</c> until then, which neither could. Pinned.
    /// </summary>
    public static string PictureName(DateTimeOffset local) =>
        "camera-watch-" + local.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".jpg";

    /// <summary>The changed frame waiting, as the attachment a message carries (marked as the camera's), and the time it was taken; null for none. Taking it makes it the reference.</summary>
    public (ImageAttachment Image, DateTimeOffset At)? TakePending()
    {
        CameraFrame? frame;
        lock (_gate)
        {
            frame = _pending;
            _pending = null;
        }

        if (frame is null)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTime(frame.At, _time.LocalTimeZone);
        return (CameraJpeg.Attachment(frame, PictureName(local), MaxSide), local);
    }

    /// <summary>One sample: skipped while the last is still being read; a failure is logged and the watch goes on.</summary>
    internal async Task SampleAsync()
    {
        CameraLease? lease;
        lock (_gate)
        {
            lease = _lease;
        }

        if (lease is null || Interlocked.Exchange(ref _sampling, 1) != 0)
        {
            return;
        }

        try
        {
            var frame = await lease.NextFrameAsync(settled: true, CancellationToken.None).ConfigureAwait(false);
            var grid = FrameDiff.Grid(frame);
            bool kept;
            lock (_gate)
            {
                if (!ReferenceEquals(_lease, lease))
                {
                    return;
                }

                double change = _reference is null ? 1 : FrameDiff.Changed(_reference, grid);
                kept = FrameDiff.Reaches(change, Math.Clamp(_threshold(), Settings.AppSettingsData.MinCameraWatchThreshold, Settings.AppSettingsData.MaxCameraWatchThreshold));
                if (kept)
                {
                    _reference = grid;
                    _pending = frame;
                }
            }

            if (kept)
            {
                _changed();
            }
        }
        catch (Exception e) when (e is CameraException or ObjectDisposedException or InvalidOperationException)
        {
            DiagnosticLog.Debug(Category, "A watch sample failed: " + e.Message);
        }
        finally
        {
            _ = Interlocked.Exchange(ref _sampling, 0);
        }
    }

    public void Dispose() => Stop();

    private void Sample() => _ = SampleAsync();
}
