using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The picture viewer window (2026-09-27, the user's ask: their FolderPictureViewer, a WinForms app, brought inside so the
/// ComfyUI picture strip can open it on the output folder and watch pictures arrive as they are generated). Not WinForms —
/// under NativeAOT that is only the unsupported <c>_SuppressWinFormsTrimError</c> opt-in and would move the app to
/// <c>net10.0-windows</c> — and not the other exe as a child (a process-start site and a second binary to ship), the
/// user's call: a plain Win32 window over <see cref="ViewerNative"/>, in this process on its own thread, decoded by the
/// WIC codecs the app already links (<see cref="ViewerImage"/>). One window per process: a second
/// <see cref="Open"/> points it at the folder and brings it forward. It dies with the app (<see cref="CloseAll"/>, and
/// its thread is a background one). Everything it decides is <see cref="ViewerState"/>'s, tested without a window; this
/// is the Windows layer, proven by the smoke's <c>viewer:window</c> (<see cref="Probe"/>) and by hand. The shown picture
/// drags out of it and is copied where it is dropped (2026-09-28, <see cref="PictureWindowDrag"/>, <c>viewer:drag</c>).
/// </summary>
public static class PictureWindow
{
    private static readonly Lock s_gate = new();
    private static PictureWindowThread? s_open;

    /// <summary>The camera's live window (2026-10-02, the user's call: a window of its own, never the picture viewer's), or null.</summary>
    private static PictureWindowThread? s_live;

    /// <summary>
    /// Whether the window wears the theme (later on 2026-09-27, the <c>Themed image viewer</c> setting): asked on the window's
    /// thread each time it opens or is focused. The app supplies it (<c>Program</c>, over the effective settings) — the viewer
    /// never reads settings itself; on until then.
    /// </summary>
    public static Func<bool> Themed { get; set; } = static () => true;

    /// <summary>
    /// The picture the user's own keys moved to (2026-09-28: the strip highlights it): ← / →,
    /// Home / End, and the picture shown after a double-Del — never a slide, an arriving picture, a retarget or a
    /// <see cref="Follow"/>, so the strip and the window cannot chase each other. Called on the window's thread with the full
    /// path; it must not block. The app supplies it (<c>Program</c>); nothing until then.
    /// </summary>
    public static Action<string>? Browsed { get; set; }

    /// <summary>
    /// Where the window was when it last closed (2026-09-28, the user's ask: "remember the screen position where it was last
    /// closed so it opens there the next time"; position only, the size stays <see cref="DefaultWidth"/> ×
    /// <see cref="DefaultHeight"/>): the top-left corner of its restored placement, in workspace coordinates — the pair
    /// <c>GetWindowPlacement</c> / <c>SetWindowPlacement</c> keeps, so the round trip is exact and Windows itself brings a
    /// corner left on a monitor that is gone back onto one. Asked on the window's thread as a new window is made; null
    /// (or nothing supplied) is Windows' own default place. The app supplies it (<c>Program</c>, over the settings).
    /// </summary>
    public static Func<(int X, int Y)?>? Position { get; set; }

    /// <summary>
    /// Told the corner <see cref="Position"/> will answer next, on the window's thread as it closes (Esc, the ×, Alt+F4, the
    /// app's own <see cref="CloseAll"/>): the restored placement's even when it closes maximized, minimized or full screen
    /// (then the window it was before F11). It must not block. The app supplies it (<c>Program</c>: the profile's
    /// <c>ViewerLeft</c> / <c>ViewerTop</c>); nothing until then.
    /// </summary>
    public static Action<int, int>? Placed { get; set; }

    /// <summary>
    /// Where the camera's live window was when it last closed (2026-10-02): <see cref="Position"/>'s twin for that window alone,
    /// so moving one never moves the other. The app supplies it (<c>Program</c>: the profile's <c>CameraWindowLeft</c> /
    /// <c>CameraWindowTop</c>); null is Windows' own default place.
    /// </summary>
    public static Func<(int X, int Y)?>? LivePosition { get; set; }

    /// <summary>Told the camera window's corner as it closes (<see cref="Placed"/>'s twin). It must not block.</summary>
    public static Action<int, int>? LivePlaced { get; set; }

    /// <summary>Whether a window can be opened here at all: Windows only.</summary>
    public static bool IsAvailable => OperatingSystem.IsWindows();

    /// <summary>
    /// The window on <paramref name="folder"/> (a full path that exists): opened, or the open one pointed at it and brought
    /// forward. Throws <see cref="InvalidOperationException"/> with the reason when no window could be made.
    /// </summary>
    public static void Open(string folder) => OpenOn(folder, null);

    /// <summary>
    /// The window on the folder of <paramref name="picture"/> (a full path to a file that exists), held on that picture — a
    /// double-clicked one (2026-09-27, the user's call: clicks open the built-in viewer, not the app Windows registers).
    /// Opened, or the open one pointed at it and brought forward. Throws as <see cref="Open(string)"/> does.
    /// </summary>
    public static void OpenAt(string picture) => OpenAt(picture, activate: true);

    /// <summary>
    /// <see cref="OpenAt(string)"/>, brought forward or not: <paramref name="activate"/> false (2026-10-02, the camera's
    /// <c>post</c> preview) shows the window on the picture without taking the keyboard from the terminal, where the camera
    /// pane still waits for its keys.
    /// </summary>
    public static void OpenAt(string picture, bool activate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(picture);
        string folder = Path.GetDirectoryName(picture) ?? throw new ArgumentException($"{picture} has no folder", nameof(picture));
        OpenOn(folder, picture, activate);
    }

    /// <summary>
    /// The camera's live picture (2026-10-02, the camera's <c>live</c> preview and <c>/camera live</c>) in a window of its own —
    /// since later that day (the user's call), never the picture viewer's, so the two never take each other's window and both
    /// can be open: the live window already open is handed the new use (the old one told it ended), or a new one is made at
    /// <see cref="PictureWindowThread.LiveWidth"/> × <see cref="PictureWindowThread.LiveHeight"/> where it last closed
    /// (<see cref="LivePosition"/>), shown without taking the keyboard. <paramref name="closed"/> runs (on the window's thread)
    /// when the user closes the window or another use takes it over; the use's end closes the window. Throws as
    /// <see cref="Open(string)"/> does.
    /// </summary>
    public static ILiveView ShowLive(string title, Action closed)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(closed);
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException(ViewerText.Unavailable);
        }

        var view = new LiveView(title, closed);
        lock (s_gate)
        {
            if (s_live is { Alive: true } live && live.StartLive(view))
            {
                view.Window = live;
                return view;
            }

            var window = new PictureWindowThread(null, null, view);
            view.Window = window;
            window.Start();
            s_live = window;
        }

        return view;
    }

    private static void OpenOn(string folder, string? select, bool activate = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException(ViewerText.Unavailable);
        }

        lock (s_gate)
        {
            if (s_open is { Alive: true } open && open.Retarget(folder, select, activate))
            {
                return;
            }

            var window = new PictureWindowThread(folder, select, activate: activate);
            window.Start();
            s_open = window;
        }
    }

    /// <summary>
    /// The open window moved to <paramref name="picture"/> (a full path) without being brought forward (2026-09-28, the strip's
    /// arrows and a click on one of its tiles): only a window on that picture's folder, and only a
    /// file that exists. Nothing without a window — this never opens one.
    /// </summary>
    public static void Follow(string picture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(picture);
        lock (s_gate)
        {
            if (s_open is { Alive: true } open)
            {
                open.Follow(picture);
            }
        }
    }

    /// <summary>The open windows closed — the picture viewer and the camera's — each waited for briefly; nothing without one.</summary>
    public static void CloseAll()
    {
        PictureWindowThread? open;
        PictureWindowThread? live;
        lock (s_gate)
        {
            open = s_open;
            live = s_live;
            s_open = null;
            s_live = null;
        }

        open?.Close();
        live?.Close();
    }

    /// <summary>
    /// <c>viewer:window</c> for the smoke: the class registered, a hidden window made, a private message sent through the
    /// <c>[UnmanagedCallersOnly]</c> window procedure and answered, the window destroyed. Proves the imports and the
    /// callback in the published exe without showing anything.
    /// </summary>
    public static (bool Ok, string Detail) Probe()
    {
        if (!IsAvailable)
        {
            return (true, "skipped: not Windows");
        }

        return PictureWindowThread.Probe();
    }
}

/// <summary>One viewer window and the thread that pumps its messages (<see cref="PictureWindow"/>).</summary>
internal sealed unsafe class PictureWindowThread
{
    private const uint ChangedMessage = WmApp + 1;
    private const uint LoadedMessage = WmApp + 2;
    private const uint RetargetMessage = WmApp + 3;
    private const uint FollowMessage = WmApp + 4;
    private const uint LiveStartMessage = WmApp + 5;
    private const uint LiveFrameMessage = WmApp + 6;
    private const uint LiveEndMessage = WmApp + 7;
    private const uint ProbeMessage = WmApp + 9;
    private static readonly IntPtr ProbeAnswer = new(0x5EE);
    private static readonly IntPtr DebounceTimer = new(1);
    private static readonly IntPtr DeleteArmTimer = new(2);
    private static readonly IntPtr SlideTimer = new(3);

    /// <summary>The wait after a folder change before the picture is read: a burst of events is one load (FolderPictureViewer's 250 ms).</summary>
    public const uint DebounceMilliseconds = 250;

    /// <summary>The window's size at 96 DPI before the user changes it (FolderPictureViewer's).</summary>
    public const int DefaultWidth = 1024;
    public const int DefaultHeight = 768;

    /// <summary>The camera window's size at 96 DPI (2026-10-02): 16:9, a webcam's shape.</summary>
    public const int LiveWidth = 960;
    public const int LiveHeight = 540;

    private static readonly Lock s_classGate = new();
    private static IntPtr s_className;
    private static ushort s_atom;

    private readonly ViewerState _state = new();
    private readonly ConcurrentQueue<Change> _changes = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Lock _gate = new();
    private readonly string? _startFolder;
    private readonly string? _startSelect;
    private readonly bool _startActivate;
    private LiveView? _live;
    private LiveView? _pendingLive;
    private LiveView? _endingLive;

    /// <summary>The camera's window (made for a live use; its own size and place), never a folder's.</summary>
    private readonly bool _camera;
    private (LiveView View, ViewerBitmap? Frame, string? Title)? _pendingFrame;
    private bool _framePosted;
    private bool _pendingActivate = true;
    private Thread? _thread;
    private IntPtr _hwnd;
    private string? _failure;
    private bool _started;
    private volatile bool _alive = true;
    private string? _pendingFolder;
    private string? _pendingSelect;
    private string? _pendingFollow;
    private FileSystemWatcher? _watcher;
    private int _generation;
    private ViewerBitmap? _bitmap;
    private string? _unreadable;
    private int _loadVersion;
    private CancellationTokenSource? _load;
    private (int Version, string Path, ViewerBitmap? Bitmap)? _loaded;
    private bool _fullScreen;
    private ViewerStyle? _style;
    private IntPtr _background;
    private IntPtr _savedStyle;
    private WindowPlacement _savedPlacement;
    private bool _ole;
    private (int X, int Y)? _press;

    private enum ChangeKind
    {
        Created,
        Deleted,
        Renamed,
    }

    private sealed record Change(int Generation, ChangeKind Kind, string Path, string? OldPath);

    public PictureWindowThread(string? folder, string? select = null, LiveView? live = null, bool activate = true)
    {
        _startFolder = folder;
        _startSelect = select;
        _live = live;
        _camera = live is not null;
        _startActivate = activate && live is null;
    }

    public bool Alive => _alive;

    /// <summary>
    /// The thread started and the window made; throws with the reason when it could not be.
    ///
    /// <para>Success is <c>_started</c>, set as the window's setup's last step, not a window handle (2026-09-28, code review): a
    /// failure after the window was made (the first folder's listing throwing, say) ended the thread with the handle still set,
    /// so this returned, the dead window was kept and the chat said it had opened. <c>_ready</c>'s set and wait carry the flag
    /// across the threads.</para>
    /// </summary>
    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Picture viewer" };
        if (OperatingSystem.IsWindows())
        {
            // An STA (2026-09-28): the drag out (PictureWindowDrag) wants OLE on the window's thread.
            _thread.SetApartmentState(ApartmentState.STA);
        }

        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)) || !_started)
        {
            throw new InvalidOperationException(_failure ?? "the window did not start");
        }
    }

    /// <summary>Points the live window at <paramref name="folder"/> (held on <paramref name="select"/> when one is given) and brings it forward (or shows it quietly when <paramref name="activate"/> is false); false when the window is gone.</summary>
    public bool Retarget(string folder, string? select = null, bool activate = true)
    {
        lock (_gate)
        {
            _pendingFolder = folder;
            _pendingSelect = select;
            _pendingActivate = activate;
        }

        return _alive && PostMessageW(_hwnd, RetargetMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Moves the live window to <paramref name="picture"/> quietly (<see cref="PictureWindow.Follow"/>); false when the window is gone.</summary>
    public bool Follow(string picture)
    {
        lock (_gate)
        {
            _pendingFollow = picture;
        }

        return _alive && PostMessageW(_hwnd, FollowMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Hands the window to a live picture (<see cref="PictureWindow.ShowLive"/>); false when the window is gone.</summary>
    public bool StartLive(LiveView view)
    {
        lock (_gate)
        {
            _pendingLive = view;
        }

        return _alive && PostMessageW(_hwnd, LiveStartMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// A live frame (or a held shot, or only a title) for the window: the newest replaces one still waiting, and the message is
    /// posted only when none is pending, so a slow window is never flooded.
    /// </summary>
    public void PostLiveFrame(LiveView view, ViewerBitmap? frame, string? title)
    {
        bool post;
        lock (_gate)
        {
            if (_pendingFrame is { } waiting && waiting.View == view)
            {
                if (waiting.Frame is { } replaced && frame is not null)
                {
                    view.Recycle(replaced.Bgrx);
                }

                frame ??= waiting.Frame;
                title ??= waiting.Title;
            }

            _pendingFrame = (view, frame, title);
            post = !_framePosted;
            _framePosted = true;
        }

        if (post && (!_alive || !PostMessageW(_hwnd, LiveFrameMessage, IntPtr.Zero, IntPtr.Zero)))
        {
            lock (_gate)
            {
                _framePosted = false;
            }
        }
    }

    /// <summary>Ends <paramref name="view"/>'s live picture, if it still has the window: the folder before it again, or the window closed.</summary>
    public void EndLive(LiveView view)
    {
        lock (_gate)
        {
            _endingLive = view;
        }

        if (_alive)
        {
            PostMessageW(_hwnd, LiveEndMessage, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>The window asked to close, and its thread waited for a moment.</summary>
    public void Close()
    {
        if (_alive && PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero))
        {
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
    }

    public static (bool Ok, string Detail) Probe()
    {
        try
        {
            IntPtr instance = GetModuleHandle(IntPtr.Zero);
            if (!EnsureClass(instance, out string? failure))
            {
                return (false, failure ?? "the window class was not registered");
            }

            IntPtr hwnd = CreateWindowExW(0, s_className, IntPtr.Zero, WsOverlappedWindow, 0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                return (false, $"CreateWindowExW failed ({Marshal.GetLastPInvokeError()})");
            }

            try
            {
                IntPtr answer = SendMessageW(hwnd, ProbeMessage, IntPtr.Zero, IntPtr.Zero);
                if (answer != ProbeAnswer)
                {
                    return (false, $"the window procedure answered 0x{answer:X}");
                }

                // dwmapi bound (the themed bar): a refusal on an old Windows is reported, not a failure.
                int dark = 1;
                int hr = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, &dark, sizeof(int));
                return (true, $"user32/gdi32/dwmapi bound; a hidden window answered through the window procedure; dark bar 0x{hr:X8}");
            }
            finally
            {
                DestroyWindow(hwnd);
            }
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // The window class, registered once per process: the procedure below, the app's icon, no background brush (the paint
    // covers every pixel, so a resize never flashes).
    private static bool EnsureClass(IntPtr instance, out string? failure)
    {
        lock (s_classGate)
        {
            failure = null;
            if (s_atom != 0)
            {
                return true;
            }

            if (s_className == IntPtr.Zero)
            {
                s_className = Marshal.StringToHGlobalUni("NeonSidekick.PictureViewer");
            }

            IntPtr icon = LoadImageW(instance, (IntPtr)ApplicationIconId, ImageIcon, 0, 0, LrDefaultSize | LrShared);
            var wc = new WndClassEx
            {
                cbSize = (uint)sizeof(WndClassEx),
                style = CsHRedraw | CsVRedraw | CsDoubleClicks,
                lpfnWndProc = &WindowProcedure,
                hInstance = instance,
                hIcon = icon,
                hIconSm = icon,
                hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IdcArrow),
                lpszClassName = s_className,
            };
            s_atom = RegisterClassExW(&wc);
            if (s_atom == 0)
            {
                failure = $"RegisterClassExW failed ({Marshal.GetLastPInvokeError()})";
                return false;
            }

            return true;
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == ProbeMessage)
            {
                return ProbeAnswer;
            }

            if (message == WmNcCreate && ((CreateStruct*)lParam)->lpCreateParams is var created && created != IntPtr.Zero)
            {
                SetWindowLongPtr(hwnd, GwlpUserData, created);
            }

            IntPtr user = GetWindowLongPtr(hwnd, GwlpUserData);
            if (user != IntPtr.Zero && GCHandle.FromIntPtr(user).Target is PictureWindowThread window)
            {
                return window.Handle(hwnd, message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // Nothing may cross back into user32: an exception here would take the process down.
            DiagnosticLog.Error("Viewer", $"The picture viewer failed on message 0x{message:X}.", ex);
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void Run()
    {
        var self = GCHandle.Alloc(this);
        try
        {
            SetThreadDpiAwarenessContext(PerMonitorAwareV2);

            // OLE for the drag out (2026-09-28); without it the viewer still works, and a drag does nothing.
            int ole = OleInitialize(IntPtr.Zero);
            _ole = ole >= 0;
            if (!_ole)
            {
                DiagnosticLog.Warn("Viewer", $"OLE did not start on the picture viewer's thread (0x{ole:X8}): no picture can be dragged out of it.");
            }

            IntPtr instance = GetModuleHandle(IntPtr.Zero);
            if (!EnsureClass(instance, out _failure))
            {
                return;
            }

            _hwnd = CreateWindowExW(0, s_className, IntPtr.Zero, WsOverlappedWindow, CwUseDefault, CwUseDefault, CwUseDefault, CwUseDefault, IntPtr.Zero, IntPtr.Zero, instance, GCHandle.ToIntPtr(self));
            if (_hwnd == IntPtr.Zero)
            {
                _failure = $"CreateWindowExW failed ({Marshal.GetLastPInvokeError()})";
                return;
            }

            // Where it last closed first (2026-09-28), so the size below is the DPI of the monitor it opens on.
            RestorePosition();
            uint dpi = Math.Max(96u, GetDpiForWindow(_hwnd));
            int width = _camera ? LiveWidth : DefaultWidth;
            int height = _camera ? LiveHeight : DefaultHeight;
            SetWindowPos(_hwnd, HwndTop, 0, 0, (int)(width * dpi / 96), (int)(height * dpi / 96), SwpNoMove | SwpNoZOrder | SwpNoActivate);
            ApplyStyle();   // before it is shown: the bar is never light first
            if (_live is { } live)
            {
                SetWindowTextW(_hwnd, live.Title);
                ShowWindow(_hwnd, SwShowNoActivate);
                RaiseQuietly();
                DiagnosticLog.Info("Viewer", "Camera window opened.");
            }
            else
            {
                Show(_startFolder!);
                Select(_startSelect);
                if (_startActivate)
                {
                    ShowWindow(_hwnd, SwShow);
                    BringForward();
                }
                else
                {
                    ShowWindow(_hwnd, SwShowNoActivate);
                    RaiseQuietly();
                }

                DiagnosticLog.Info("Viewer", $"Picture viewer opened on {_startFolder}.");
            }

            _started = true;
            _ready.Set();

            Msg msg;
            while (GetMessageW(&msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
        catch (Exception ex)
        {
            _failure ??= $"{ex.GetType().Name}: {ex.Message}";
            DiagnosticLog.Error("Viewer", "The picture viewer's thread failed.", ex);
        }
        finally
        {
            _alive = false;
            _live?.OnEnded();
            _live = null;
            if (!_started && _hwnd != IntPtr.Zero)
            {
                // Made but never set up: gone before the handle its procedure reads is freed.
                DestroyWindow(_hwnd);
            }

            _watcher?.Dispose();
            _load?.Cancel();
            if (_background != IntPtr.Zero)
            {
                DeleteObject(_background);
            }

            if (_ole)
            {
                OleUninitialize();
            }

            self.Free();
            _ready.Set();
            DiagnosticLog.Info("Viewer", "Picture viewer closed.");
        }
    }

    private IntPtr Handle(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmPaint:
                Paint(hwnd);
                return IntPtr.Zero;
            case WmEraseBackground:
                return 1;
            case WmSize:
                InvalidateRect(hwnd, null, false);
                return IntPtr.Zero;
            case WmActivate:
                // A /theme change reaches an open window the next time it is focused.
                ApplyStyle();
                break;
            case WmKeyDown:
            case WmSysKeyDown when (int)wParam == ViewerState.VkF10:   // F10 (random order) is the menu key: a system key, its menu mode not wanted
            {
                var action = ViewerState.ActionFor((int)wParam, _fullScreen, _state.SlideShow);
                if (_live is not null)
                {
                    // A camera's picture (2026-10-02): full screen and closing only.
                    action = LiveViewState.Filter(action);
                }

                // Any key but Del disarms a first Del (2026-09-27), mapped or not.
                if (action != ViewerAction.Delete && _state.Disarm())
                {
                    KillTimer(hwnd, DeleteArmTimer);
                    UpdateTitle();
                }

                if (action == ViewerAction.None)
                {
                    break;
                }

                Do(action);
                return IntPtr.Zero;
            }

            case WmLeftButtonDoubleClick:
                Do(ViewerAction.ToggleFullScreen);
                return IntPtr.Zero;

            // The drag out (2026-09-28): a press remembered and the mouse captured; the first move past the system's drag
            // rectangle starts it. A double-click never moves, so it stays full screen's.
            case WmLeftButtonDown when _live is null:
                _press = PointOf(lParam);
                SetCapture(hwnd);
                return IntPtr.Zero;
            case WmMouseMove when _press is { } press:
            {
                if (((int)wParam & MkLeftButton) == 0)
                {
                    EndPress();
                    return IntPtr.Zero;
                }

                var (x, y) = PointOf(lParam);
                if (ViewerState.PastDragThreshold(x - press.X, y - press.Y, GetSystemMetrics(SmCxDrag), GetSystemMetrics(SmCyDrag)))
                {
                    EndPress();
                    DragOut(hwnd);
                }

                return IntPtr.Zero;
            }

            case WmLeftButtonUp:
                EndPress();
                return IntPtr.Zero;
            case WmCaptureChanged:
                _press = null;
                return IntPtr.Zero;
            case ChangedMessage:
                Drain();
                return IntPtr.Zero;
            case LoadedMessage:
                TakeLoaded();
                return IntPtr.Zero;
            case RetargetMessage:
            {
                string? folder;
                string? select;
                lock (_gate)
                {
                    folder = _pendingFolder;
                    select = _pendingSelect;
                    _pendingFolder = null;
                    _pendingSelect = null;
                }

                bool activate;
                lock (_gate)
                {
                    activate = _pendingActivate;
                    _pendingActivate = true;
                }

                if (folder is not null && !string.Equals(folder, _state.Folder, StringComparison.OrdinalIgnoreCase))
                {
                    Show(folder);
                }

                // The same folder still moves to the clicked picture (2026-09-27): a double-click on an older one jumps to it.
                Select(select);

                ApplyStyle();

                if (activate)
                {
                    BringForward();
                }
                else
                {
                    ShowWindow(_hwnd, SwShowNoActivate);
                    RaiseQuietly();
                }

                return IntPtr.Zero;
            }

            case FollowMessage:
            {
                string? follow;
                lock (_gate)
                {
                    follow = _pendingFollow;
                    _pendingFollow = null;
                }

                // The strip's picture (2026-09-28): on this folder only, never a retarget and never brought forward.
                if (follow is not null && _live is null
                    && string.Equals(Path.GetDirectoryName(follow), _state.Folder, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(follow))
                {
                    Select(follow);
                    RestartSlides();
                }

                return IntPtr.Zero;
            }

            case WmTimer when wParam == DebounceTimer:
                KillTimer(hwnd, DebounceTimer);
                LoadCurrent();
                return IntPtr.Zero;
            case WmTimer when wParam == DeleteArmTimer:
                KillTimer(hwnd, DeleteArmTimer);
                _state.Disarm();
                UpdateTitle();
                return IntPtr.Zero;
            case WmTimer when wParam == SlideTimer:
                // A picture armed for deleting holds the show until it is deleted or disarmed.
                if (!_state.DeleteArmed && _state.NextSlide(Random.Shared))
                {
                    UpdateTitle();
                    LoadCurrent();
                }

                return IntPtr.Zero;
            case LiveStartMessage:
            {
                LiveView? view;
                lock (_gate)
                {
                    view = _pendingLive;
                    _pendingLive = null;
                }

                if (view is not null)
                {
                    TakeLive(view);
                }

                return IntPtr.Zero;
            }

            case LiveFrameMessage:
                TakeLiveFrame();
                return IntPtr.Zero;
            case LiveEndMessage:
            {
                LiveView? ending;
                lock (_gate)
                {
                    ending = _endingLive;
                    _endingLive = null;
                }

                if (ending is not null && ending == _live)
                {
                    // The camera's window is the use's alone: it closes with it.
                    LeaveLive();
                    PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                }

                return IntPtr.Zero;
            }

            case WmClose:
                LeaveLive();
                RememberPosition();
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    // A new live use of the camera's window (2026-10-02): the one before told it ended, the picture blank until the first frame,
    // the window raised without the keyboard. Only ever the camera's window: the picture viewer is never handed a live use.
    private void TakeLive(LiveView view)
    {
        if (_live is { } old && old != view)
        {
            old.OnEnded();
        }

        _live = view;
        _watcher?.Dispose();
        _watcher = null;
        _generation++;
        _loadVersion++;
        _load?.Cancel();
        KillTimer(_hwnd, SlideTimer);
        _bitmap = null;
        _unreadable = null;
        SetWindowTextW(_hwnd, view.Title);
        InvalidateRect(_hwnd, null, false);
        ShowWindow(_hwnd, SwShowNoActivate);
        RaiseQuietly();
    }

    // The newest frame (or held shot, or title) of the live use: painted; the frame it replaces goes back for reuse.
    private void TakeLiveFrame()
    {
        (LiveView View, ViewerBitmap? Frame, string? Title)? pending;
        lock (_gate)
        {
            pending = _pendingFrame;
            _pendingFrame = null;
            _framePosted = false;
        }

        if (pending is not { } next || next.View != _live)
        {
            return;
        }

        if (next.Title is { } title)
        {
            SetWindowTextW(_hwnd, title);
        }

        if (next.Frame is { } frame)
        {
            if (_bitmap is { } old && old != frame)
            {
                next.View.Recycle(old.Bgrx);
            }

            _bitmap = frame;
            _unreadable = null;
            InvalidateRect(_hwnd, null, false);
        }
    }

    // The live use over (the window closing, the use ended): told once; true when there was one.
    private bool LeaveLive()
    {
        if (_live is not { } live)
        {
            return false;
        }

        _live = null;
        _bitmap = null;
        live.OnEnded();
        return true;
    }

    // Above the other windows without the keyboard (2026-10-02): the camera pane in the terminal keeps its keys.
    private void RaiseQuietly()
    {
        SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        SetWindowPos(_hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void Do(ViewerAction action)
    {
        switch (action)
        {
            case ViewerAction.ToggleFullScreen:
                SetFullScreen(!_fullScreen);
                break;
            case ViewerAction.LeaveFullScreen:
                SetFullScreen(false);
                break;
            case ViewerAction.Close:
                PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                break;
            case ViewerAction.Delete:
                DeleteShown();
                break;
            case ViewerAction.ToggleSlideShow or ViewerAction.StopSlideShow or ViewerAction.ToggleShuffle or ViewerAction.LongerSlides or ViewerAction.ShorterSlides:
                if (_state.Slides(action))
                {
                    UpdateTitle();
                    RestartSlides();
                }

                break;
            default:
                if (_state.Browse(action))
                {
                    UpdateTitle();
                    LoadCurrent();
                    NotifyBrowsed();

                    // A picture browsed to during the show gets a whole slide's time.
                    RestartSlides();
                }

                break;
        }
    }

    // A mouse message's client point: GET_X_LPARAM / GET_Y_LPARAM, signed (a captured mouse left of or above the window).
    private static (int X, int Y) PointOf(IntPtr lParam) => ((short)((long)lParam & 0xFFFF), (short)(((long)lParam >> 16) & 0xFFFF));

    private void EndPress()
    {
        _press = null;
        ReleaseCapture();
    }

    // The shown picture dragged out and copied where it is dropped (2026-09-28, the user's ask; PictureWindowDrag). Nothing
    // without OLE, a picture, or its file; a failure only logged, as a delete's is. The slide show's timer may move the
    // window on meanwhile: the drag carries the picture it started with.
    private void DragOut(IntPtr hwnd)
    {
        if (!_ole || _state.Current is not { } path || !File.Exists(path))
        {
            return;
        }

        string name = Path.GetFileName(path);
        var (hr, effect) = PictureWindowDrag.Start(hwnd, path);
        if (hr < 0)
        {
            DiagnosticLog.Warn("Viewer", ViewerText.DragFailed(name, $"0x{hr:X8}"));
            return;
        }

        DiagnosticLog.Info("Viewer", hr == DragDropDropped && (effect & DropEffectCopy) != 0 ? $"Dragged {name} out: copied." : $"Dragged {name} out: nothing dropped.");
    }

    // The slide show's timer (later on 2026-09-27): a slide's time from now while the show runs, else stopped. Starting the
    // show leaves the shown picture up for a whole slide first.
    private void RestartSlides()
    {
        if (_state.SlideShow)
        {
            SetTimer(_hwnd, SlideTimer, (uint)(_state.SlideSeconds * 1000), IntPtr.Zero);
        }
        else
        {
            KillTimer(_hwnd, SlideTimer);
        }
    }

    // Del (2026-09-27, the user's call): the first arms the shown picture, the title says so and a timer disarms it; a
    // second on the same picture in time deletes it for good, and the next one is shown without waiting for the watcher
    // (whose Deleted then finds nothing to remove).
    private void DeleteShown()
    {
        string? path = _state.PressDelete(Environment.TickCount64);
        if (path is null)
        {
            UpdateTitle();
            if (_state.DeleteArmed)
            {
                SetTimer(_hwnd, DeleteArmTimer, ViewerState.DeleteArmMilliseconds, IntPtr.Zero);
            }

            return;
        }

        KillTimer(_hwnd, DeleteArmTimer);
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("Viewer", ViewerText.DeleteFailed(Path.GetFileName(path), ex.Message));
            UpdateTitle();
            return;
        }

        DiagnosticLog.Info("Viewer", $"Deleted {path}.");
        _state.Remove(path);
        UpdateTitle();
        LoadCurrent();
        RestartSlides();
        NotifyBrowsed();
    }

    // The picture the user's keys moved to, told to the app (PictureWindow.Browsed, 2026-09-28); its failure only logged.
    private void NotifyBrowsed()
    {
        if (_state.Current is not { } path || PictureWindow.Browsed is not { } browsed)
        {
            return;
        }

        try
        {
            browsed(path);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Viewer", $"Could not tell the strip about {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    // A folder's pictures listed and watched, the newest shown; the old watcher's events are dropped by generation.
    private void Show(string folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        int generation = ++_generation;
        var pictures = new List<ViewerEntry>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (ImageFile.IsImagePath(path))
                {
                    pictures.Add(new ViewerEntry(path, File.GetCreationTimeUtc(path)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not list {folder}: {ex.Message}");
        }

        _state.Reset(folder, pictures);
        RestartSlides();   // a new folder stops the show
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName,
            };
            watcher.Created += (_, e) => Post(new Change(generation, ChangeKind.Created, e.FullPath, null));
            watcher.Deleted += (_, e) => Post(new Change(generation, ChangeKind.Deleted, e.FullPath, null));
            watcher.Renamed += (_, e) => Post(new Change(generation, ChangeKind.Renamed, e.FullPath, e.OldFullPath));
            watcher.Error += (_, e) => DiagnosticLog.Warn("Viewer", $"Watching {folder} failed: {e.GetException().Message}");
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not watch {folder}: {ex.Message}");
        }

        UpdateTitle();
        LoadCurrent();
    }

    // A double-clicked picture shown (2026-09-27): held on it, or live on the newest; nothing without one.
    private void Select(string? path)
    {
        if (path is null)
        {
            return;
        }

        DateTime created;
        try
        {
            created = File.GetCreationTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            created = DateTime.UtcNow;
        }

        if (_state.Select(path, created))
        {
            UpdateTitle();
            LoadCurrent();
        }
    }

    // From the watcher's thread: queued, and the window told (the state is only touched on the window's thread).
    private void Post(Change change)
    {
        _changes.Enqueue(change);
        if (_alive)
        {
            PostMessageW(_hwnd, ChangedMessage, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void Drain()
    {
        bool shownChanged = false;
        bool any = false;
        while (_changes.TryDequeue(out var change))
        {
            if (change.Generation != _generation)
            {
                continue;
            }

            any = true;
            if (change.OldPath is { } old && ImageFile.IsImagePath(old))
            {
                shownChanged |= _state.Remove(old);
            }

            if (change.Kind == ChangeKind.Deleted)
            {
                shownChanged |= _state.Remove(change.Path);
            }
            else if (ImageFile.IsImagePath(change.Path))
            {
                shownChanged |= _state.Add(change.Path, DateTime.UtcNow);
            }
        }

        if (!any)
        {
            return;
        }

        UpdateTitle();
        if (shownChanged)
        {
            // Restarted by every change: a burst is one read, the last picture's.
            SetTimer(_hwnd, DebounceTimer, DebounceMilliseconds, IntPtr.Zero);
        }
    }

    private void UpdateTitle() => SetWindowTextW(_hwnd, _state.Title());

    // The shown picture read off the window's thread; an older read still running is cancelled, and its answer dropped by version.
    private void LoadCurrent()
    {
        int version = ++_loadVersion;
        _load?.Cancel();
        _load = null;
        string? path = _state.Current;
        if (path is null)
        {
            _bitmap = null;
            _unreadable = null;
            InvalidateRect(_hwnd, null, false);
            return;
        }

        var load = new CancellationTokenSource();
        _load = load;
        var token = load.Token;
        _ = Task.Run(() => ViewerImage.LoadThenAsync(path, token, bitmap =>
        {
            lock (_gate)
            {
                _loaded = (version, path, bitmap);
            }

            if (_alive)
            {
                PostMessageW(_hwnd, LoadedMessage, IntPtr.Zero, IntPtr.Zero);
            }
        }));
    }

    private void TakeLoaded()
    {
        (int Version, string Path, ViewerBitmap? Bitmap)? loaded;
        lock (_gate)
        {
            loaded = _loaded;
            _loaded = null;
        }

        if (loaded is not { } result || result.Version != _loadVersion)
        {
            return;
        }

        _bitmap = result.Bitmap;
        _unreadable = result.Bitmap is null ? Path.GetFileName(result.Path) : null;
        InvalidateRect(_hwnd, null, false);
    }

    private void Paint(IntPtr hwnd)
    {
        PaintStruct ps;
        IntPtr hdc = BeginPaint(hwnd, &ps);
        try
        {
            Rect client;
            GetClientRect(hwnd, &client);
            IntPtr fill = _background != IntPtr.Zero ? _background : GetStockObject(BlackBrush);
            var bitmap = _bitmap;
            if (bitmap is null)
            {
                FillRect(hdc, &client, fill);
                string text = _live is not null ? ViewerText.LiveWaiting : _unreadable is { } name ? ViewerText.Unreadable(name) : _state.Count == 0 ? ViewerText.Waiting(_state.Folder) : "";
                if (text.Length > 0)
                {
                    SelectObject(hdc, GetStockObject(DefaultGuiFont));
                    SetTextColor(hdc, _style?.Text ?? 0x00A0A0A0);
                    SetBkMode(hdc, Transparent);
                    DrawTextW(hdc, text, -1, &client, DtCenter | DtVCenter | DtSingleLine | DtNoPrefix);
                }

                return;
            }

            var (x, y, w, h) = ViewerState.Fit(bitmap.Width, bitmap.Height, client.Right, client.Bottom);

            // The bars round the picture, then the picture: every pixel painted once, no flash.
            var top = new Rect { Left = 0, Top = 0, Right = client.Right, Bottom = y };
            var bottom = new Rect { Left = 0, Top = y + h, Right = client.Right, Bottom = client.Bottom };
            var left = new Rect { Left = 0, Top = y, Right = x, Bottom = y + h };
            var right = new Rect { Left = x + w, Top = y, Right = client.Right, Bottom = y + h };
            FillRect(hdc, &top, fill);
            FillRect(hdc, &bottom, fill);
            FillRect(hdc, &left, fill);
            FillRect(hdc, &right, fill);

            SetStretchBltMode(hdc, Halftone);
            SetBrushOrgEx(hdc, 0, 0, null);
            var header = new BitmapInfoHeader
            {
                biSize = (uint)sizeof(BitmapInfoHeader),
                biWidth = bitmap.Width,
                biHeight = -bitmap.Height,   // negative: top row first
                biPlanes = 1,
                biBitCount = 32,
            };
            fixed (byte* bits = bitmap.Bgrx)
            {
                StretchDIBits(hdc, x, y, w, h, 0, 0, bitmap.Width, bitmap.Height, bits, &header, DibRgbColors, SrcCopy);
            }
        }
        finally
        {
            EndPaint(hwnd, &ps);
        }
    }

    // The theme in force on the bar (DWM) and round the picture (later on 2026-09-27, ViewerStyle); nothing when it has not
    // changed. The HRESULTs are ignored: an older Windows refuses the colours and keeps its bar.
    private void ApplyStyle()
    {
        bool themed;
        try
        {
            themed = PictureWindow.Themed();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Viewer", $"Could not read Themed image viewer: {ex.Message}");
            themed = true;
        }

        var style = ViewerStyle.For(NeonSidekick.UI.Theme.Current, themed);
        if (_style == style)
        {
            return;
        }

        _style = style;
        int dark = 1;
        uint caption = style.Caption, text = style.CaptionText, border = style.Border;
        DwmSetWindowAttribute(_hwnd, DwmwaUseImmersiveDarkMode, &dark, sizeof(int));
        DwmSetWindowAttribute(_hwnd, DwmwaCaptionColor, &caption, sizeof(uint));
        DwmSetWindowAttribute(_hwnd, DwmwaTextColor, &text, sizeof(uint));
        DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, &border, sizeof(uint));

        IntPtr old = _background;
        _background = CreateSolidBrush(style.Background);
        if (old != IntPtr.Zero)
        {
            DeleteObject(old);
        }

        InvalidateRect(_hwnd, null, false);
    }

    // The window, not yet shown, moved to where the last one closed (PictureWindow.Position): its placement's restored
    // rectangle carried to the corner at its own size, the show state left hidden for the ShowWindow that follows.
    // SetWindowPlacement keeps a window that would land off every monitor on one, so a corner saved on a monitor since
    // unplugged still opens in view.
    private void RestorePosition()
    {
        (int X, int Y)? at;
        try
        {
            at = (_camera ? PictureWindow.LivePosition : PictureWindow.Position)?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not read the viewer's last position: {ex.Message}");
            return;
        }

        var placement = new WindowPlacement { length = (uint)sizeof(WindowPlacement) };
        if (at is not { } corner || !GetWindowPlacement(_hwnd, &placement))
        {
            return;
        }

        var r = placement.rcNormalPosition;
        placement.rcNormalPosition = new Rect { Left = corner.X, Top = corner.Y, Right = corner.X + (r.Right - r.Left), Bottom = corner.Y + (r.Bottom - r.Top) };
        placement.flags = 0;
        placement.showCmd = (uint)SwHide;
        SetWindowPlacement(_hwnd, &placement);
    }

    // Where the window is as it closes, for the next one (PictureWindow.Placed): the restored placement's corner — the one
    // saved before F11 when it is full screen — never the maximized or minimized frame.
    private void RememberPosition()
    {
        if ((_camera ? PictureWindow.LivePlaced : PictureWindow.Placed) is not { } placed)
        {
            return;
        }

        var placement = _savedPlacement;
        if (!_fullScreen)
        {
            placement = new WindowPlacement { length = (uint)sizeof(WindowPlacement) };
            if (!GetWindowPlacement(_hwnd, &placement))
            {
                return;
            }
        }

        try
        {
            placed(placement.rcNormalPosition.Left, placement.rcNormalPosition.Top);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not keep the viewer's position: {ex.Message}");
        }
    }

    // Borderless over the whole monitor and back to the placement it had (FolderPictureViewer's F11).
    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen)
        {
            return;
        }

        if (on)
        {
            _savedStyle = GetWindowLongPtr(_hwnd, GwlStyle);
            var placement = new WindowPlacement { length = (uint)sizeof(WindowPlacement) };
            GetWindowPlacement(_hwnd, &placement);
            _savedPlacement = placement;
            var monitor = new MonitorInfo { cbSize = (uint)sizeof(MonitorInfo) };
            GetMonitorInfoW(MonitorFromWindow(_hwnd, MonitorDefaultToNearest), &monitor);
            SetWindowLongPtr(_hwnd, GwlStyle, new IntPtr((long)(WsPopup | WsVisible)));
            var r = monitor.rcMonitor;
            SetWindowPos(_hwnd, HwndTop, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SwpFrameChanged | SwpShowWindow);
        }
        else
        {
            SetWindowLongPtr(_hwnd, GwlStyle, _savedStyle);
            var placement = _savedPlacement;
            SetWindowPlacement(_hwnd, &placement);
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged | SwpShowWindow);
        }

        _fullScreen = on;
    }

    // In front of the terminal. Windows lets only the foreground thread take the foreground, and that is the terminal's,
    // not ours (the click reached it, not us): the input is shared with it for the call, and a moment on top covers the
    // case where even that is refused.
    private void BringForward()
    {
        if (IsIconic(_hwnd))
        {
            ShowWindow(_hwnd, SwRestore);
        }

        IntPtr foreground = GetForegroundWindow();
        uint theirs = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, IntPtr.Zero);
        uint ours = GetCurrentThreadId();
        bool attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
        try
        {
            SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
            SetWindowPos(_hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
            BringWindowToTop(_hwnd);
            SetForegroundWindow(_hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(ours, theirs, false);
            }
        }
    }
}
