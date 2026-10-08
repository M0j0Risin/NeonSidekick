using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The camera's live window on a Mac (2026-10-07, Stage 2: the camera on macOS) — <see cref="PictureWindow.ShowLive"/>'s calls land
/// here when the AppKit host runs. As on Windows it is a window of its own, never the picture viewer's: one per process, handed each
/// new live use (the one before told it ended), <see cref="PictureWindowThread.LiveWidth"/> × <see cref="PictureWindowThread.LiveHeight"/>
/// points where it last closed (<see cref="PictureWindow.LivePosition"/>, moved onto a screen), shown without taking the keyboard, and
/// closed with its use. Frames come from the camera's thread to <see cref="Router"/>, which keeps one waiting (the newest replaces it)
/// and posts the main thread once, never once a frame. Main thread only past the router; excluded from coverage with the AppKit layer.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacLiveWindows
{
    private static readonly Router s_router = new();
    private static MacLiveWindow? s_live;   // main thread only
    private static int s_registered;

    /// <summary>A live use of the camera's window: made, or the open one handed it. Throws when the main thread cannot be reached.</summary>
    public static ILiveView Show(string title, Action closed)
    {
        RegisterEnd();
        var view = new LiveView(title, closed) { Window = s_router };
        if (!AppKitHost.Post(() => StartOnMain(view)))
        {
            throw new InvalidOperationException("the window did not start");
        }

        return view;
    }

    private static void StartOnMain(LiveView view)
    {
        if (!view.Open)
        {
            return;   // ended before the main thread got to it
        }

        if (s_live is { Alive: true } live)
        {
            live.TakeLive(view);
            return;
        }

        var window = new MacLiveWindow();
        s_live = window;
        window.Start(view);
    }

    internal static void Gone(MacLiveWindow window)
    {
        if (s_live == window)
        {
            s_live = null;
        }
    }

    // The app's end closes the live window while the loop still runs (its place kept).
    private static void RegisterEnd()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 0)
        {
            AppKitHost.AtEnd(() => s_live?.Close());
        }
    }

    /// <summary>The live uses' way to the main thread: one frame waiting at most, one post outstanding at most.</summary>
    private sealed class Router : ILiveWindow
    {
        private readonly Lock _gate = new();
        private (LiveView View, ViewerBitmap? Frame, string? Title)? _pending;
        private bool _posted;

        public void PostLiveFrame(LiveView view, ViewerBitmap? frame, string? title)
        {
            bool post;
            lock (_gate)
            {
                if (_pending is { } waiting && waiting.View == view)
                {
                    if (waiting.Frame is { } replaced && frame is not null)
                    {
                        view.Recycle(replaced.Bgrx);
                    }

                    frame ??= waiting.Frame;
                    title ??= waiting.Title;
                }

                _pending = (view, frame, title);
                post = !_posted;
                _posted = true;
            }

            if (post && !AppKitHost.Post(Drain))
            {
                lock (_gate)
                {
                    _posted = false;
                }
            }
        }

        public void EndLive(LiveView view) => AppKitHost.Post(() => s_live?.End(view));

        private void Drain()
        {
            (LiveView View, ViewerBitmap? Frame, string? Title)? pending;
            lock (_gate)
            {
                pending = _pending;
                _pending = null;
                _posted = false;
            }

            if (pending is { } next)
            {
                s_live?.Take(next.View, next.Frame, next.Title);
            }
        }
    }
}

/// <summary>The camera's live window on a Mac (<see cref="MacLiveWindows"/>); main thread only.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacLiveWindow : AppKitWindow
{
    private LiveView? _live;
    private nint _picture;   // the frame's layer
    private nint _image;     // the CGImage shown, retained here too

    protected override Action<int, int>? Placed => PictureWindow.LivePlaced;

    public void Start(LiveView view)
    {
        Create(PictureWindowThread.LiveWidth, PictureWindowThread.LiveHeight, PictureWindow.LivePosition);
        var (width, height) = ClientSize;
        _picture = Send(Send(Class("CALayer"), Sel("alloc")), Sel("init"));
        SendVoidRect(_picture, Sel("setFrame:"), new CGRect(0, 0, width, height));
        SendVoidUInt(_picture, Sel("setAutoresizingMask:"), LayerSizable);
        SendVoid(_picture, Sel("setContentsGravity:"), NSString("resizeAspect"));
        SendVoid(_picture, Sel("setMinificationFilter:"), NSString("trilinear"));
        SendVoid(RootLayer, Sel("addSublayer:"), _picture);
        var style = ViewerStyle.For(NeonSidekick.UI.Theme.Current, PictureWindow.Themed());
        ApplyChrome(style);
        nint background = CGColor(style.Background);
        WithoutAnimation(() => SendVoid(RootLayer, Sel("setBackgroundColor:"), background));
        CGColorRelease(background);
        TakeLive(view);
        DiagnosticLog.Info("Viewer", "Camera window opened.");
    }

    /// <summary>A new live use: the one before told it ended, the picture blank until its first frame, shown without the keyboard.</summary>
    public void TakeLive(LiveView view)
    {
        if (_live is { } old && old != view)
        {
            old.OnEnded();
        }

        _live = view;
        Show(0);
        SetTitle(view.Title);
        ShowWindow(activate: false, raise: true);
    }

    /// <summary>The newest frame, held shot or title of <paramref name="view"/>; a frame for a use that no longer has the window is dropped.</summary>
    public void Take(LiveView view, ViewerBitmap? frame, string? title)
    {
        if (!Alive || view != _live)
        {
            return;
        }

        if (title is not null)
        {
            SetTitle(title);
        }

        if (frame is not null)
        {
            // The array goes back to the use only when CoreGraphics lets go of it: Core Animation may still draw the old contents.
            Show(CGImage(frame.Bgrx, frame.Width, frame.Height, BitmapBgrx, view.Recycle));
        }
    }

    /// <summary>The use over: the window is the use's alone, so it closes with it.</summary>
    public void End(LiveView view)
    {
        if (view == _live)
        {
            _live = null;
            view.OnEnded();
            Close();
        }
    }

    private void Show(nint image)
    {
        nint old = _image;
        _image = image;
        WithoutAnimation(() => SendVoid(_picture, Sel("setContents:"), image));
        if (old != 0)
        {
            CGImageRelease(old);
        }
    }

    // Full screen and closing only, as on Windows (LiveViewState.Filter); TAB and the chat's chords go to the terminal.
    internal override bool KeyDown(MacKeyEvent key)
    {
        switch (LiveViewState.Filter(MacKeys.ViewerAction(key.KeyCode, key.Flags, FullScreen, slideShow: false)))
        {
            case ViewerAction.ToggleFullScreen:
                SetFullScreen(!FullScreen);
                return true;
            case ViewerAction.LeaveFullScreen:
                SetFullScreen(false);
                return true;
            case ViewerAction.Close:
                Close();
                return true;
            default:
                return TerminalHandoff.TakeMac(key);
        }
    }

    internal override void MouseDown(nint e)
    {
        if (SendLong(e, Sel("clickCount")) >= 2)
        {
            SetFullScreen(!FullScreen);
        }
    }

    protected override void OnClosed()
    {
        var live = _live;
        _live = null;
        if (_image != 0)
        {
            CGImageRelease(_image);
            _image = 0;
        }

        if (_picture != 0)
        {
            SendVoid(_picture, Sel("release"));
            _picture = 0;
        }

        MacLiveWindows.Gone(this);
        live?.OnEnded();
        DiagnosticLog.Info("Viewer", "Camera window closed.");
    }
}
