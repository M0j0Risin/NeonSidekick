using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Images;
using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The thumbnail browser (2026-10-04, the user's ask: <c>/thumbs &lt;folder&gt;</c> and <c>/comfy thumbs</c>, a window of a folder's
/// pictures as thumbnails beside the picture viewer). A click on a tile moves the viewer to that picture (<see cref="Picked"/>, through
/// the chat, which also moves the console's picture strip); the viewer's own keys and the strip's arrows move the selection here
/// (<see cref="Follow"/>, which never answers back, so the three cannot chase each other). Pictures arriving in the folder go on the
/// end and nothing already drawn moves (<see cref="ThumbsState"/>); the tiles fit the window down to a smallest size and then scroll,
/// with Ctrl+wheel or +/− to zoom for the session (no setting, the user's call). A right-click (or the Apps key) opens the picture menu
/// (<see cref="PictureMenu"/>, <see cref="ContextMenuWindow"/>). F11, TAB, Esc, the chords and the remembered place are the other
/// windows' (<see cref="WindowChrome"/>, <see cref="TerminalHandoff"/>). The tiles are read off the window's thread, the ones in view
/// first, at the bucket the tile size needs, and kept in a <see cref="ThumbCache"/>. A plain Win32 window on its own thread (an STA,
/// for Explorer's shell call), as the viewer is; proven by the smoke's <c>viewer:thumbs</c> (<see cref="Probe"/>) and by hand. One per
/// process: a second <see cref="Open"/> points it at the folder and brings it forward.
/// </summary>
public static class ThumbsWindow
{
    private static readonly Lock s_gate = new();
    private static ThumbsWindowThread? s_open;

    /// <summary>
    /// The picture the user's own hand selected — a click, the keys, an edit's result — told to the app (<c>Program</c>: the chat, which
    /// moves the viewer and the strip). Never a <see cref="Follow"/>. Called on the window's thread with the full path; it must not block.
    /// </summary>
    public static Action<string>? Picked { get; set; }

    /// <summary>Where the window was when it last closed (<see cref="PictureWindow.Position"/>'s twin; the profile's <c>ThumbsWindowLeft</c> / <c>ThumbsWindowTop</c>).</summary>
    public static Func<(int X, int Y)?>? Position { get; set; }

    /// <summary>Told the window's corner as it closes, on its thread. It must not block.</summary>
    public static Action<int, int>? Placed { get; set; }

    /// <summary>Whether a window can be opened here at all: Windows only.</summary>
    public static bool IsAvailable => OperatingSystem.IsWindows();

    /// <summary>
    /// The window on <paramref name="folder"/> (a full path that exists), <paramref name="select"/> selected when given: opened, or
    /// the open one pointed at it and brought forward. Throws <see cref="InvalidOperationException"/> with the reason when no window
    /// could be made.
    /// </summary>
    public static void Open(string folder, string? select = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException(ThumbsText.Unavailable);
        }

        lock (s_gate)
        {
            if (s_open is { Alive: true } open && open.Retarget(folder, select))
            {
                return;
            }

            var window = new ThumbsWindowThread(folder, select);
            window.Start();
            s_open = window;
        }
    }

    /// <summary>The open window's selection moved to <paramref name="picture"/> (a full path) quietly: only a window on that picture's folder. Nothing without a window.</summary>
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

    /// <summary>The open window closed and waited for briefly. True when one was open.</summary>
    public static bool Close()
    {
        ThumbsWindowThread? open;
        lock (s_gate)
        {
            open = s_open;
            s_open = null;
        }

        bool alive = open is { Alive: true };
        open?.Close();
        return alive;
    }

    /// <summary>
    /// <c>viewer:thumbs</c> for the smoke: the class registered, a hidden window answering through its <c>[UnmanagedCallersOnly]</c>
    /// procedure, a caption measured in Segoe UI and a tile drawn into a memory DC with <c>StretchDIBits</c>. Nothing is shown.
    /// </summary>
    public static (bool Ok, string Detail) Probe() => IsAvailable ? ThumbsWindowThread.Probe() : (true, "skipped: not Windows");
}

/// <summary>The thumbnail browser and the thread that pumps its messages (<see cref="ThumbsWindow"/>).</summary>
internal sealed unsafe class ThumbsWindowThread
{
    private const uint ChangedMessage = WmApp + 1;
    private const uint DecodedMessage = WmApp + 2;
    private const uint RetargetMessage = WmApp + 3;
    private const uint FollowMessage = WmApp + 4;
    private const uint EditedMessage = WmApp + 5;
    private const uint MenuChosenMessage = WmApp + 6;
    private const uint ProbeMessage = WmApp + 9;
    private static readonly IntPtr ProbeAnswer = new(0x7B5);
    private static readonly IntPtr DebounceTimer = new(1);

    /// <summary>The wait after a picture is written before its tile is read again: a burst of writes is one read.</summary>
    public const uint DebounceMilliseconds = 250;

    /// <summary>The window's size at 96 DPI before the user changes it.</summary>
    public const int DefaultWidth = 1024;
    public const int DefaultHeight = 768;

    /// <summary>The caption's text size in points.</summary>
    public const int CaptionPoints = 9;

    private static readonly Lock s_classGate = new();
    private static IntPtr s_className;
    private static ushort s_atom;

    private readonly ThumbsState _state = new();
    private readonly ThumbCache _cache = new(ThumbCache.DefaultBudget);
    private readonly ConcurrentQueue<Change> _changes = new();
    private readonly ConcurrentQueue<Decoded> _decoded = new();
    private readonly Dictionary<string, int> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stale = new(StringComparer.OrdinalIgnoreCase);   // written again: read anew, the old tile shown meanwhile
    private readonly ManualResetEventSlim _ready = new();
    private readonly Lock _gate = new();
    private readonly WindowChrome _chrome = new("thumbnail browser");
    private readonly int _maxInFlight = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
    private readonly string _startFolder;
    private readonly string? _startSelect;
    private ContextMenuWindow? _menu;
    private string? _menuPath;
    private PictureEditOutcome? _edited;
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
    private CancellationTokenSource? _decodes;
    private ThumbsStyle _style = ThumbsStyle.Black;
    private IntPtr _font;
    private uint _dpi = 96;
    private int _captionHeight;
    private IntPtr _memDc;
    private IntPtr _memBitmap;
    private (int Width, int Height) _memSize;
    private int? _thumbGrab;
    private int _wheel;
    private int _zoomWheel;
    private bool _ole;

    private enum ChangeKind
    {
        Created,
        Deleted,
        Renamed,
        Changed,
    }

    private sealed record Change(int Generation, ChangeKind Kind, string Path, string? OldPath);

    private sealed record Decoded(int Generation, string Path, int Version, int Bucket, ViewerBitmap? Bitmap);

    public ThumbsWindowThread(string folder, string? select)
    {
        _startFolder = folder;
        _startSelect = select;
    }

    public bool Alive => _alive;

    /// <summary>The thread started and the window made; throws with the reason when it could not be (the picture viewer's <c>Start</c>).</summary>
    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Thumbnail browser" };
        if (OperatingSystem.IsWindows())
        {
            // An STA with OLE: Show in Explorer's shell call wants COM on the thread.
            _thread.SetApartmentState(ApartmentState.STA);
        }

        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)) || !_started)
        {
            throw new InvalidOperationException(_failure ?? "the window did not start");
        }
    }

    /// <summary>Points the live window at <paramref name="folder"/> (<paramref name="select"/> selected) and brings it forward; false when the window is gone.</summary>
    public bool Retarget(string folder, string? select)
    {
        lock (_gate)
        {
            _pendingFolder = folder;
            _pendingSelect = select;
        }

        return _alive && PostMessageW(_hwnd, RetargetMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Moves the selection to <paramref name="picture"/> quietly (<see cref="ThumbsWindow.Follow"/>); false when the window is gone.</summary>
    public bool Follow(string picture)
    {
        lock (_gate)
        {
            _pendingFollow = picture;
        }

        return _alive && PostMessageW(_hwnd, FollowMessage, IntPtr.Zero, IntPtr.Zero);
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

            IntPtr dc = IntPtr.Zero, mem = IntPtr.Zero, bitmap = IntPtr.Zero, font = IntPtr.Zero;
            try
            {
                IntPtr answer = SendMessageW(hwnd, ProbeMessage, IntPtr.Zero, IntPtr.Zero);
                if (answer != ProbeAnswer)
                {
                    return (false, $"the window procedure answered 0x{answer:X}");
                }

                dc = GetDC(hwnd);
                font = ContextMenuWindow.MakeFont(96, CaptionPoints);
                mem = CreateCompatibleDC(dc);
                bitmap = CreateCompatibleBitmap(dc, 32, 32);
                if (dc == IntPtr.Zero || font == IntPtr.Zero || mem == IntPtr.Zero || bitmap == IntPtr.Zero)
                {
                    return (false, "a DC, the font or the bitmap was not made");
                }

                SelectObject(mem, bitmap);
                SelectObject(mem, font);
                var caption = new Rect { Right = 32, Bottom = 16 };
                DrawTextW(mem, "0001.png", -1, &caption, DtCenter | DtSingleLine | DtNoPrefix | DtEndEllipsis);
                var tile = new ViewerBitmap(2, 2, new byte[16]);
                var header = new BitmapInfoHeader { biSize = (uint)sizeof(BitmapInfoHeader), biWidth = 2, biHeight = -2, biPlanes = 1, biBitCount = 32 };
                int lines;
                fixed (byte* bits = tile.Bgrx)
                {
                    SetStretchBltMode(mem, Halftone);
                    lines = StretchDIBits(mem, 0, 0, 16, 16, 0, 0, 2, 2, bits, &header, DibRgbColors, SrcCopy);
                }

                if (lines <= 0 || !BitBlt(dc, 0, 0, 32, 32, mem, 0, 0, SrcCopy))
                {
                    return (false, "StretchDIBits or BitBlt failed");
                }

                return (true, "a hidden window answered through the window procedure; a caption in Segoe UI and a tile drawn in a memory DC and copied");
            }
            finally
            {
                if (mem != IntPtr.Zero)
                {
                    DeleteDC(mem);
                }

                if (bitmap != IntPtr.Zero)
                {
                    DeleteObject(bitmap);
                }

                if (font != IntPtr.Zero)
                {
                    DeleteObject(font);
                }

                if (dc != IntPtr.Zero)
                {
                    ReleaseDC(hwnd, dc);
                }

                DestroyWindow(hwnd);
            }
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // The window class, registered once per process: the procedure below, the app's icon, no background brush.
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
                s_className = Marshal.StringToHGlobalUni("NeonSidekick.Thumbnails");
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
            if (user != IntPtr.Zero && GCHandle.FromIntPtr(user).Target is ThumbsWindowThread window)
            {
                return window.Handle(hwnd, message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // Nothing may cross back into user32: an exception here would take the process down.
            DiagnosticLog.Error("Viewer", $"The thumbnail browser failed on message 0x{message:X}.", ex);
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void Run()
    {
        var self = GCHandle.Alloc(this);
        try
        {
            SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            int ole = OleInitialize(IntPtr.Zero);
            _ole = ole >= 0;
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

            _menu = new ContextMenuWindow(_hwnd, MenuChosenMessage);

            // Where it last closed first, so the size and the font are the DPI of the monitor it opens on.
            _chrome.Window = _hwnd;
            _chrome.RestorePosition(ThumbsWindow.Position);
            _chrome.SizeForDpi(DefaultWidth, DefaultHeight);
            SetFont(Math.Max(96u, GetDpiForWindow(_hwnd)));
            ApplyStyle();   // before it is shown: the bar is never light first
            Show(_startFolder);
            Select(_startSelect);
            ShowWindow(_hwnd, SwShow);
            _chrome.BringForward();
            DiagnosticLog.Info("Viewer", $"Thumbnail browser opened on {_startFolder}.");

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
            DiagnosticLog.Error("Viewer", "The thumbnail browser's thread failed.", ex);
        }
        finally
        {
            _alive = false;
            if (!_started && _hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
            }

            _menu?.Close();
            _watcher?.Dispose();
            _decodes?.Cancel();
            if (_memDc != IntPtr.Zero)
            {
                DeleteDC(_memDc);
            }

            if (_memBitmap != IntPtr.Zero)
            {
                DeleteObject(_memBitmap);
            }

            if (_font != IntPtr.Zero)
            {
                DeleteObject(_font);
            }

            if (_ole)
            {
                OleUninitialize();
            }

            self.Free();
            _ready.Set();
            DiagnosticLog.Info("Viewer", "Thumbnail browser closed.");
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
                _menu?.Close();
                Layout();
                return IntPtr.Zero;
            case WmMove:
                _menu?.Close();
                break;
            case WmActivate:
                if (((long)wParam & 0xFFFF) == 0)
                {
                    _menu?.Close();   // another window took the keyboard: the menu goes, as Windows' own does
                }

                // A /theme change reaches an open window the next time it is focused.
                ApplyStyle();
                break;
            case WmDpiChanged:
            {
                if (!_chrome.FullScreen)
                {
                    var r = (Rect*)lParam;
                    SetWindowPos(hwnd, IntPtr.Zero, r->Left, r->Top, r->Right - r->Left, r->Bottom - r->Top, SwpNoZOrder | SwpNoActivate);
                }

                SetFont(Math.Max(96u, (uint)((long)wParam & 0xFFFF)));
                Layout();
                return IntPtr.Zero;
            }

            case WmKeyDown:
            case WmSysKeyDown:
            {
                int key = (int)wParam;
                bool alt = TerminalHandoff.AltHeld();
                if (_menu is { IsOpen: true } menu)
                {
                    // The menu has the keys while it shows; a chord or TAB closes it and goes on as usual.
                    if (!alt && GetKeyState(VkControl) >= 0 && menu.Key(key))
                    {
                        return IntPtr.Zero;
                    }

                    menu.Close();
                }

                var action = alt ? ThumbsAction.None : ThumbsState.ActionFor(key, GetKeyState(VkControl) < 0, GetKeyState(VkShift) < 0, _chrome.FullScreen);
                if (action == ThumbsAction.None)
                {
                    // F10 alone: no menu bar to enter (the default's menu mode would swallow the next key).
                    if (TerminalHandoff.Take(key) || (key == ThumbsState.VkF10 && !alt))
                    {
                        return IntPtr.Zero;
                    }

                    break;
                }

                Do(action);
                return IntPtr.Zero;
            }

            // A passed Alt chord's character: the default would look for a menu mnemonic and beep. Alt+Space keeps the system menu.
            case WmSysChar when (int)wParam != ' ':
                return IntPtr.Zero;

            // The keyboard's menu is opened on the key itself (the Apps key, Shift+F10) and the mouse's on the button's release: the
            // default's own WM_CONTEXTMENU is not wanted there. One from the title bar is the system menu's, left to the default.
            case WmContextMenu when ContextMenuWindow.IsClientContextMenu(hwnd, lParam):
                return IntPtr.Zero;

            case WmMouseWheel:
            {
                int delta = (short)(((long)wParam >> 16) & 0xFFFF);
                if (((int)wParam & MkControl) != 0)
                {
                    // Ctrl+wheel: a zoom step a notch, a touchpad's small turns added up.
                    _zoomWheel += delta;
                    int steps = _zoomWheel / 120;
                    _zoomWheel -= steps * 120;
                    if (steps != 0 && _state.Zoom(steps))
                    {
                        Refresh();
                    }

                    return IntPtr.Zero;
                }

                if (_state.ScrollBy(ThumbsState.WheelPixels(ref _wheel, delta, _state.PitchY)))
                {
                    Refresh();
                }

                return IntPtr.Zero;
            }

            case WmLeftButtonDown:
            {
                _menu?.Close();
                var (x, y) = PointOf(lParam);
                if (x >= BarLeft())
                {
                    PressBar(y);
                    SetCapture(hwnd);
                    return IntPtr.Zero;
                }

                if (_state.HitTest(x, y) is int index && _state.SelectIndex(index))
                {
                    Refresh();
                    NotifyPicked();
                }

                return IntPtr.Zero;
            }

            case WmLeftButtonDoubleClick:
            {
                var (x, y) = PointOf(lParam);
                if (x >= BarLeft())
                {
                    return IntPtr.Zero;
                }

                if (_state.HitTest(x, y) is int index)
                {
                    _state.SelectIndex(index);
                    Refresh();
                    OpenInViewer();
                }
                else
                {
                    Do(ThumbsAction.ToggleFullScreen);
                }

                return IntPtr.Zero;
            }

            case WmRightButtonUp:
            {
                var (x, y) = PointOf(lParam);
                if (x < BarLeft() && _state.HitTest(x, y) is int index)
                {
                    if (_state.SelectIndex(index))
                    {
                        Refresh();
                        NotifyPicked();
                    }

                    var point = new Point { X = x, Y = y };
                    ClientToScreen(hwnd, &point);
                    OpenMenu(point.X, point.Y, keyboard: false);
                }

                return IntPtr.Zero;
            }

            case WmMouseMove when _thumbGrab is { } grab:
                DragThumb(PointOf(lParam).Y - grab);
                return IntPtr.Zero;
            case WmLeftButtonUp:
                if (_thumbGrab is not null)
                {
                    ReleaseCapture();
                }

                return IntPtr.Zero;
            case WmCaptureChanged:
                if (_thumbGrab is not null)
                {
                    _thumbGrab = null;
                    InvalidateRect(hwnd, null, false);
                }

                return IntPtr.Zero;
            case ChangedMessage:
                Drain();
                return IntPtr.Zero;
            case DecodedMessage:
                TakeDecoded();
                return IntPtr.Zero;
            case RetargetMessage:
            {
                string? folder, select;
                lock (_gate)
                {
                    folder = _pendingFolder;
                    select = _pendingSelect;
                    _pendingFolder = null;
                    _pendingSelect = null;
                }

                if (folder is not null && !string.Equals(folder, _state.Folder, StringComparison.OrdinalIgnoreCase))
                {
                    Show(folder);
                }

                Select(select);
                ApplyStyle();
                _chrome.BringForward();
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

                // The viewer's or the strip's picture: on this folder only, never brought forward, never told back.
                if (follow is not null && string.Equals(Path.GetDirectoryName(follow), _state.Folder, StringComparison.OrdinalIgnoreCase) && File.Exists(follow))
                {
                    Select(follow);
                }

                return IntPtr.Zero;
            }

            case EditedMessage:
                TakeEdited();
                return IntPtr.Zero;
            case MenuChosenMessage:
                RunCommand((PictureCommand)(int)wParam);
                return IntPtr.Zero;
            case WmTimer when wParam == DebounceTimer:
                KillTimer(hwnd, DebounceTimer);
                ReadTouchedAgain();
                return IntPtr.Zero;
            case WmClose:
                _menu?.Close();
                _chrome.RememberPosition(ThumbsWindow.Placed);
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void Do(ThumbsAction action)
    {
        switch (action)
        {
            case ThumbsAction.ToggleFullScreen:
                _menu?.Close();
                _chrome.SetFullScreen(!_chrome.FullScreen);
                break;
            case ThumbsAction.LeaveFullScreen:
                _chrome.SetFullScreen(false);
                break;
            case ThumbsAction.Close:
                PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                break;
            case ThumbsAction.Open:
                OpenInViewer();
                break;
            case ThumbsAction.Refresh:
            {
                string? selected = _state.SelectedPath;
                Show(_state.Folder);
                Select(selected);
                break;
            }

            case ThumbsAction.ZoomIn or ThumbsAction.ZoomOut:
                if (_state.Zoom(action == ThumbsAction.ZoomIn ? 1 : -1))
                {
                    Refresh();
                }

                break;
            case ThumbsAction.Menu:
                if (_state.Selected is int index)
                {
                    var (x, y, side) = _state.TileRect(index);
                    var point = new Point { X = x + side / 2, Y = y + side / 2 };
                    ClientToScreen(_hwnd, &point);
                    OpenMenu(point.X, point.Y, keyboard: true);
                }

                break;
            default:
                if (_state.Apply(action))
                {
                    Refresh();
                    NotifyPicked();
                }

                break;
        }
    }

    // The picture menu on the selected picture: the path kept now, so a picture arriving or the selection moving meanwhile changes nothing.
    private void OpenMenu(int x, int y, bool keyboard)
    {
        if (_menu is null || _state.SelectedPath is not { } path)
        {
            return;
        }

        _menuPath = path;
        _menu.Open(PictureMenu.BuildNow(thumbs: true, path), x, y, PictureMenu.StyleNow(_chrome), keyboard);
    }

    // A menu row chosen (posted by the menu after it closed): an edit off the thread, a file action, Delete or Open.
    private void RunCommand(PictureCommand command)
    {
        if (_menuPath is not { } path)
        {
            return;
        }

        _menuPath = null;
        if (PictureActions.IsEdit(command))
        {
            PictureMenu.Edit(path, command, outcome =>
            {
                lock (_gate)
                {
                    _edited = outcome;
                }

                if (_alive)
                {
                    PostMessageW(_hwnd, EditedMessage, IntPtr.Zero, IntPtr.Zero);
                }
            });
            return;
        }

        switch (command)
        {
            case PictureCommand.OpenInViewer:
                OpenAt(path);
                break;
            case PictureCommand.Delete:
                if (PictureMenu.Delete(path))
                {
                    _cache.Remove(path);
                    _state.Remove(path);
                    Refresh();
                    NotifyPicked();
                }

                break;
            default:
                PictureMenu.RunFileAction(path, command);
                break;
        }
    }

    // An edit's end: a new picture added and selected (the viewer following), one replaced read again, a converted one in its source's place.
    private void TakeEdited()
    {
        PictureEditOutcome? outcome;
        lock (_gate)
        {
            outcome = _edited;
            _edited = null;
        }

        if (outcome is not { Failed: false, Written: { } written })
        {
            return;
        }

        if (outcome.Replaced)
        {
            Touch(written);
            ReadTouchedAgain();
            return;
        }

        if (outcome.Removed is { } removed)
        {
            _cache.Remove(removed);
            _state.Remove(removed);
        }

        if (string.Equals(Path.GetDirectoryName(written), _state.Folder, StringComparison.OrdinalIgnoreCase))
        {
            _state.Select(written, DateTime.UtcNow);
            Refresh();
            NotifyPicked();
        }
    }

    private void OpenInViewer()
    {
        if (_state.SelectedPath is { } path)
        {
            OpenAt(path);
        }
    }

    // The picture viewer on the picture and in front (a double-click, Enter, Open in the viewer).
    private static void OpenAt(string path)
    {
        try
        {
            PictureWindow.OpenAt(path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or ArgumentException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not open the viewer on {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    // The selection the user's hand made, told to the app (ThumbsWindow.Picked); its failure only logged.
    private void NotifyPicked()
    {
        if (_state.SelectedPath is not { } path || ThumbsWindow.Picked is not { } picked)
        {
            return;
        }

        try
        {
            picked(path);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Viewer", $"Could not tell the viewer about {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    // A folder's pictures listed and watched, the tiles fitted; the old watcher's events and reads are dropped by generation.
    private void Show(string folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        _decodes?.Cancel();
        _decodes = new CancellationTokenSource();
        int generation = ++_generation;
        _cache.Clear();
        _inFlight.Clear();
        _versions.Clear();
        _touched.Clear();
        _stale.Clear();
        var pictures = new List<ThumbEntry>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (ImageFile.IsImagePath(path))
                {
                    pictures.Add(new ThumbEntry(path, File.GetCreationTimeUtc(path)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not list {folder}: {ex.Message}");
        }

        _state.Reset(folder, pictures);
        Layout();
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            watcher.Created += (_, e) => Post(new Change(generation, ChangeKind.Created, e.FullPath, null));
            watcher.Deleted += (_, e) => Post(new Change(generation, ChangeKind.Deleted, e.FullPath, null));
            watcher.Renamed += (_, e) => Post(new Change(generation, ChangeKind.Renamed, e.FullPath, e.OldFullPath));
            watcher.Changed += (_, e) => Post(new Change(generation, ChangeKind.Changed, e.FullPath, null));
            watcher.Error += (_, e) => DiagnosticLog.Warn("Viewer", $"Watching {folder} failed: {e.GetException().Message}");
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not watch {folder}: {ex.Message}");
        }

        Refresh();
    }

    // A picture selected on request (a retarget, a follow, F5 keeping it): scrolled into view; nothing without one.
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

        _state.Select(path, created);
        Refresh();
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

    // The folder's changes: arrivals appended, a rename kept in place, a deletion closing up, a write read again after a pause.
    private void Drain()
    {
        bool any = false;
        while (_changes.TryDequeue(out var change))
        {
            if (change.Generation != _generation)
            {
                continue;
            }

            bool image = ImageFile.IsImagePath(change.Path);
            switch (change.Kind)
            {
                case ChangeKind.Deleted:
                    if (_state.Remove(change.Path))
                    {
                        _cache.Remove(change.Path);
                        any = true;
                    }

                    break;
                case ChangeKind.Renamed when change.OldPath is { } old && _state.IndexOf(old) >= 0:
                    if (!image)
                    {
                        _state.Remove(old);
                        _cache.Remove(old);
                    }
                    else if (_state.Rename(old, change.Path, DateTime.UtcNow) == ThumbChange.Renamed)
                    {
                        _cache.Rename(old, change.Path);
                    }
                    else
                    {
                        _cache.Remove(old);
                        Touch(change.Path);
                    }

                    any = true;
                    break;
                case ChangeKind.Changed when _state.IndexOf(change.Path) >= 0:
                    Touch(change.Path);
                    break;
                default:
                    if (image && change.Kind != ChangeKind.Deleted && File.Exists(change.Path))
                    {
                        if (_state.Add(change.Path, DateTime.UtcNow) == ThumbChange.Changed)
                        {
                            Touch(change.Path);
                        }

                        any = true;
                    }

                    break;
            }
        }

        if (any)
        {
            Refresh();
        }
    }

    // A listed picture written again: read again once the writes stop (a burst is one read).
    private void Touch(string path)
    {
        _touched.Add(path);
        SetTimer(_hwnd, DebounceTimer, DebounceMilliseconds, IntPtr.Zero);
    }

    private void ReadTouchedAgain()
    {
        foreach (string path in _touched)
        {
            _versions[path] = _versions.GetValueOrDefault(path) + 1;
            _stale.Add(path);
        }

        _touched.Clear();
        Refresh();
    }

    // The tiles in view (then a page above and below) that lack a read at the bucket the size needs, started up to the cap; the
    // cache trimmed past its budget, never of those.
    private void Pump()
    {
        if (_decodes is not { } decodes || _state.Count == 0)
        {
            return;
        }

        int bucket = ThumbsState.Bucket(_state.Tile);
        var (first, last) = _state.Visible();
        var (aheadFirst, aheadLast) = _state.Visible(_state.PageRows);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = aheadFirst; i <= aheadLast; i++)
        {
            keep.Add(_state.Entries[i].Path);
        }

        // In view first, then the rows ahead below, then above.
        var order = Enumerable.Range(first, Math.Max(0, last - first + 1))
            .Concat(Enumerable.Range(last + 1, Math.Max(0, aheadLast - last)))
            .Concat(Enumerable.Range(aheadFirst, Math.Max(0, first - aheadFirst)));
        foreach (int i in order)
        {
            if (_inFlight.Count >= _maxInFlight)
            {
                break;
            }

            string path = _state.Entries[i].Path;
            if (_inFlight.ContainsKey(path) || _touched.Contains(path) || (!_stale.Contains(path) && _cache.Satisfies(path, bucket)))
            {
                continue;
            }

            StartDecode(path, bucket, decodes.Token);
        }

        _cache.Trim(keep);
    }

    private void StartDecode(string path, int bucket, CancellationToken token)
    {
        int generation = _generation;
        int version = _versions.GetValueOrDefault(path);
        _inFlight[path] = bucket;
        _ = Task.Run(() => ViewerImage.LoadThenAsync(path, token, bitmap =>
        {
            _decoded.Enqueue(new Decoded(generation, path, version, bucket, token.IsCancellationRequested ? null : bitmap));
            if (_alive)
            {
                PostMessageW(_hwnd, DecodedMessage, IntPtr.Zero, IntPtr.Zero);
            }
        }, (bytes, name) => ViewerImage.DecodeThumbnail(bytes, name, bucket)));
    }

    // Reads that finished: kept unless the folder changed or the picture was written again since (then read once more), drawn, more started.
    private void TakeDecoded()
    {
        bool any = false;
        while (_decoded.TryDequeue(out var done))
        {
            if (done.Generation != _generation)
            {
                continue;
            }

            _inFlight.Remove(done.Path);
            if (done.Version != _versions.GetValueOrDefault(done.Path) || _state.IndexOf(done.Path) < 0)
            {
                continue;
            }

            _cache.Put(done.Path, done.Bucket, done.Bitmap);
            _stale.Remove(done.Path);
            any = true;
        }

        if (any)
        {
            InvalidateRect(_hwnd, null, false);
        }

        Pump();
    }

    private void Refresh()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;   // InvalidateRect on no window would repaint every window on the desktop
        }

        SetWindowTextW(_hwnd, _state.Title());
        InvalidateRect(_hwnd, null, false);
        Pump();
    }

    // The client area handed to the state (which fits the tiles again unless zoomed).
    private void Layout()
    {
        Rect client;
        GetClientRect(_hwnd, &client);
        _state.Relayout(client.Right, client.Bottom, _dpi, _captionHeight);
        Refresh();
    }

    private void ApplyStyle()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        bool themed = _chrome.Themed();
        var palette = NeonSidekick.UI.Theme.Current;
        _chrome.ApplyCaption(ViewerStyle.For(palette, themed));
        var style = ThumbsStyle.For(palette, themed);
        if (style != _style)
        {
            _style = style;
            InvalidateRect(_hwnd, null, false);
        }
    }

    // Segoe UI for the captions at dpi, measured for the caption row; the old font let go once the new one is selected.
    private void SetFont(uint dpi)
    {
        IntPtr font = ContextMenuWindow.MakeFont(dpi, CaptionPoints);
        if (font == IntPtr.Zero)
        {
            return;
        }

        IntPtr dc = GetDC(_hwnd);
        IntPtr old = SelectObject(dc, font);
        TextMetric metric;
        bool measured = GetTextMetricsW(dc, &metric);
        SelectObject(dc, old);
        ReleaseDC(_hwnd, dc);
        if (!measured || metric.tmHeight <= 0)
        {
            DeleteObject(font);
            return;
        }

        if (_memDc != IntPtr.Zero)
        {
            SelectObject(_memDc, font);
        }

        if (_font != IntPtr.Zero)
        {
            DeleteObject(_font);
        }

        _font = font;
        _dpi = dpi;
        _captionHeight = metric.tmHeight + (int)(8 * dpi / 96);
    }

    private int BarLeft()
    {
        Rect client;
        GetClientRect(_hwnd, &client);
        return client.Right - _state.BarPixels;
    }

    private (int Offset, int Length, int Track) ThumbNow()
    {
        Rect client;
        GetClientRect(_hwnd, &client);
        var (offset, length) = LogViewState.Thumb(_state.ContentHeight, client.Bottom, _state.ScrollTop, client.Bottom, ThumbsState.Scale(24, _dpi));
        return (offset, length, client.Bottom);
    }

    // A press on the scroll bar: on the thumb it is held for a drag, above or below it a page that way.
    private void PressBar(int y)
    {
        if (_state.MaxScroll <= 0)
        {
            return;
        }

        var (offset, length, _) = ThumbNow();
        if (y >= offset && y < offset + length)
        {
            _thumbGrab = y - offset;
            InvalidateRect(_hwnd, null, false);
            return;
        }

        int page = Math.Max(1, _state.ClientHeight - _state.PitchY / 2);
        if (_state.ScrollBy(y < offset ? -page : page))
        {
            Refresh();
        }
    }

    private void DragThumb(int offset)
    {
        var (_, length, track) = ThumbNow();
        if (_state.ScrollTo((int)LogViewState.TopForThumb(offset, _state.ContentHeight, _state.ClientHeight, track, length)))
        {
            Refresh();
        }
    }

    private static (int X, int Y) PointOf(IntPtr lParam) => ((short)((long)lParam & 0xFFFF), (short)(((long)lParam >> 16) & 0xFFFF));

    // A rectangle filled with one colour: ExtTextOutW's opaque fill, no brush to make and free.
    private static void Fill(IntPtr dc, Rect rect, uint color)
    {
        SetBkColor(dc, color);
        ExtTextOutW(dc, 0, 0, EtoOpaque, &rect, null, 0, null);
    }

    // Everything drawn into the memory bitmap (kept until the size changes), then copied out at once: no flicker.
    private void Paint(IntPtr hwnd)
    {
        PaintStruct ps;
        IntPtr hdc = BeginPaint(hwnd, &ps);
        try
        {
            Rect client;
            GetClientRect(hwnd, &client);
            int width = Math.Max(1, client.Right), height = Math.Max(1, client.Bottom);
            if (_memDc == IntPtr.Zero)
            {
                _memDc = CreateCompatibleDC(hdc);
                if (_memDc == IntPtr.Zero)
                {
                    return;
                }

                if (_font != IntPtr.Zero)
                {
                    SelectObject(_memDc, _font);
                }
            }

            if (_memSize != (width, height))
            {
                IntPtr bitmap = CreateCompatibleBitmap(hdc, width, height);
                if (bitmap == IntPtr.Zero)
                {
                    return;
                }

                SelectObject(_memDc, bitmap);
                if (_memBitmap != IntPtr.Zero)
                {
                    DeleteObject(_memBitmap);
                }

                _memBitmap = bitmap;
                _memSize = (width, height);
            }

            IntPtr dc = _memDc;
            Fill(dc, new Rect { Right = width, Bottom = height }, _style.Background);
            SetBkMode(dc, Transparent);
            SetStretchBltMode(dc, Halftone);
            SetBrushOrgEx(dc, 0, 0, null);
            if (_state.Count == 0)
            {
                SetTextColor(dc, _style.Text);
                var all = new Rect { Right = width - _state.BarPixels, Bottom = height };
                DrawTextW(dc, ThumbsText.Empty(_state.Folder), -1, &all, DtCenter | DtVCenter | DtSingleLine | DtNoPrefix | DtEndEllipsis);
            }
            else
            {
                var (first, last) = _state.Visible();
                for (int i = first; i <= last; i++)
                {
                    DrawTile(dc, i);
                }
            }

            // The scroll bar: its track the whole height, the thumb on it while the grid is taller than the window.
            int barLeft = width - _state.BarPixels;
            Fill(dc, new Rect { Left = barLeft, Right = width, Bottom = height }, _style.Track);
            if (_state.MaxScroll > 0)
            {
                var (offset, length) = LogViewState.Thumb(_state.ContentHeight, height, _state.ScrollTop, height, ThumbsState.Scale(24, _dpi));
                int inset = ThumbsState.Scale(2, _dpi);
                Fill(dc, new Rect { Left = barLeft + inset, Top = offset, Right = width - inset, Bottom = offset + length }, _thumbGrab is null ? _style.Thumb : _style.ThumbActive);
            }

            BitBlt(hdc, 0, 0, width, height, dc, 0, 0, SrcCopy);
        }
        finally
        {
            EndPaint(hwnd, &ps);
        }
    }

    // One tile: the placeholder square, the picture fitted in it (or the unreadable mark), the selection's frame, the name under it.
    private void DrawTile(IntPtr dc, int index)
    {
        var entry = _state.Entries[index];
        var (x, y, side) = _state.TileRect(index);
        bool selected = _state.Selected == index;
        if (selected)
        {
            int frame = ThumbsState.Scale(3, _dpi);
            Fill(dc, new Rect { Left = x - frame, Top = y - frame, Right = x + side + frame, Bottom = y + side + _captionHeight + frame }, _style.Selected);
        }

        var (bitmap, failed) = _cache.Get(entry.Path);
        if (bitmap is null)
        {
            Fill(dc, new Rect { Left = x, Top = y, Right = x + side, Bottom = y + side }, _style.Placeholder);
            if (failed)
            {
                SetTextColor(dc, _style.Text);
                var box = new Rect { Left = x, Top = y, Right = x + side, Bottom = y + side };
                DrawTextW(dc, ThumbsText.Unreadable, -1, &box, DtCenter | DtVCenter | DtSingleLine | DtNoPrefix);
            }
        }
        else
        {
            Fill(dc, new Rect { Left = x, Top = y, Right = x + side, Bottom = y + side }, selected ? _style.Selected : _style.Background);
            var (px, py, pw, ph) = ViewerState.Fit(bitmap.Width, bitmap.Height, side, side);
            var header = new BitmapInfoHeader { biSize = (uint)sizeof(BitmapInfoHeader), biWidth = bitmap.Width, biHeight = -bitmap.Height, biPlanes = 1, biBitCount = 32 };
            fixed (byte* bits = bitmap.Bgrx)
            {
                StretchDIBits(dc, x + px, y + py, pw, ph, 0, 0, bitmap.Width, bitmap.Height, bits, &header, DibRgbColors, SrcCopy);
            }
        }

        SetTextColor(dc, selected ? _style.SelectedText : _style.Caption);
        var caption = new Rect { Left = x + ThumbsState.Scale(2, _dpi), Top = y + side, Right = x + side - ThumbsState.Scale(2, _dpi), Bottom = y + side + _captionHeight };
        DrawTextW(dc, Path.GetFileName(entry.Path), -1, &caption, DtCenter | DtVCenter | DtSingleLine | DtNoPrefix | DtEndEllipsis);
    }
}
