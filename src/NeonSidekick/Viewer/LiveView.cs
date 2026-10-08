using System.Collections.Concurrent;

namespace NeonSidekick.Viewer;

/// <summary>
/// The picture viewer showing a live picture (2026-10-02, the camera's <c>live</c> preview): frames posted as they come (the
/// newest wins; one waiting is replaced, never queued), a shot held still over them (<see cref="Freeze"/>) and the live
/// picture again (<see cref="Resume"/>). Disposing it ends the live picture and closes the camera's window (a window of its own
/// since later on 2026-10-02, never the picture viewer's). Thread-safe: the camera's thread posts, the screen's thread freezes.
/// </summary>
public interface ILiveView : IDisposable
{
    /// <summary>False once the user closed the window (or another use took it over).</summary>
    bool Open { get; }

    /// <summary>A buffer of at least <paramref name="length"/> bytes for the next frame: one the window has finished with when there is one.</summary>
    byte[] Rent(int length);

    /// <summary>The next live frame; ignored while a shot is held.</summary>
    void Post(ViewerBitmap frame);

    /// <summary>Holds <paramref name="shot"/> with <paramref name="title"/> until <see cref="Resume"/>.</summary>
    void Freeze(ViewerBitmap shot, string title);

    /// <summary>The live picture again, under <paramref name="title"/>.</summary>
    void Resume(string title);
}

/// <summary>What the live window decides without a window (2026-10-02): the keys it keeps, the frame rate it shows. Pure.</summary>
public static class LiveViewState
{
    /// <summary>The most frames a second the window is sent: enough to frame a shot, half a webcam's rate in CPU.</summary>
    public const int Fps = 15;

    /// <summary>The longer side a live frame is scaled to before it is posted.</summary>
    public const int MaxSide = 960;

    /// <summary>The keys a live window keeps: full screen and closing. Browsing, deleting and slides are a folder's, not a camera's.</summary>
    public static ViewerAction Filter(ViewerAction action) => action is ViewerAction.ToggleFullScreen or ViewerAction.LeaveFullScreen or ViewerAction.Close ? action : ViewerAction.None;

    /// <summary>Whether a frame arriving <paramref name="sinceLast"/> after the last one posted is posted (<see cref="Fps"/>).</summary>
    public static bool Due(TimeSpan sinceLast) => sinceLast >= TimeSpan.FromSeconds(1.0 / Fps) - TimeSpan.FromMilliseconds(2);
}

/// <summary>
/// The window a live use posts to (2026-10-07, the camera's live window on a Mac): <see cref="PictureWindowThread"/> on Windows,
/// <see cref="MacLiveWindow"/> over AppKit. Both take a frame, a held shot or a title from any thread, the newest replacing one waiting.
/// </summary>
internal interface ILiveWindow
{
    void PostLiveFrame(LiveView view, ViewerBitmap? frame, string? title);

    void EndLive(LiveView view);
}

/// <summary>One live use of the window (<see cref="PictureWindow.ShowLive"/>): its title, its held shot, its spare buffers, the callback when it ends.</summary>
internal sealed class LiveView : ILiveView
{
    private readonly Action _closed;
    private readonly ConcurrentBag<byte[]> _spare = [];
    private int _ended;
    private int _disposed;
    private volatile bool _frozen;

    public LiveView(string title, Action closed)
    {
        Title = title;
        _closed = closed;
    }

    public ILiveWindow? Window { get; set; }

    public string Title { get; private set; }

    public bool Open => Volatile.Read(ref _ended) == 0;

    public byte[] Rent(int length)
    {
        while (_spare.TryTake(out var buffer))
        {
            if (buffer.Length == length)
            {
                return buffer;
            }
        }

        return new byte[length];
    }

    /// <summary>A buffer the window has stopped painting, back for the next frame (two at most are kept).</summary>
    public void Recycle(byte[] buffer)
    {
        if (_spare.Count < 2)
        {
            _spare.Add(buffer);
        }
    }

    public void Post(ViewerBitmap frame)
    {
        if (!_frozen && Open)
        {
            Window?.PostLiveFrame(this, frame, null);
        }
    }

    public void Freeze(ViewerBitmap shot, string title)
    {
        _frozen = true;
        Title = title;
        Window?.PostLiveFrame(this, shot, title);
    }

    public void Resume(string title)
    {
        _frozen = false;
        Title = title;
        Window?.PostLiveFrame(this, null, title);
    }

    /// <summary>The window let this view go (closed, or taken over): the callback, once.</summary>
    public void OnEnded()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            try
            {
                _closed();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Diagnostics.DiagnosticLog.Warn("Viewer", "The live view's close callback failed: " + ex.Message);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ = Interlocked.Exchange(ref _ended, 1);
            Window?.EndLive(this);
        }
    }
}
