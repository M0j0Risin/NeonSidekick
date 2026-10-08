using System.Runtime.InteropServices;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The log window (2026-10-02, the user's ask: <c>/log</c> opens the run's diagnostic log in a window of its own, beside the
/// picture viewer and the camera's, instead of the editor): the <see cref="DiagnosticBuffer"/>'s lines, coloured by level,
/// following the newest while the view is at the bottom and holding still once the user scrolls away (Ctrl+E, Ctrl+End or
/// the bottom again follow; Ctrl+Home goes to the top and pauses). A drag selects, Ctrl+A selects everything, Ctrl+C copies.
/// F11 or a double-click is full screen, Esc leaves full screen and then closes it, and it opens where it last closed
/// (<see cref="Position"/>) — the picture viewer's frame, shared through <see cref="WindowChrome"/>. A plain Win32 window on
/// its own thread over <see cref="ViewerNative"/>, as <see cref="PictureWindow"/> is; everything it decides is
/// <see cref="LogViewState"/>'s, tested without a window, and this layer is proven by the smoke's <c>viewer:log-window</c>
/// (<see cref="Probe"/>) and by hand. One per process: a second <see cref="Show"/> brings it forward.
/// </summary>
public static class LogWindow
{
    private static readonly Lock s_gate = new();
    private static LogWindowThread? s_open;

    /// <summary>
    /// Where the window was when it last closed: <see cref="PictureWindow.Position"/>'s twin for this window alone. The app
    /// supplies it (<c>Program</c>: the profile's <c>LogWindowLeft</c> / <c>LogWindowTop</c>); null is Windows' own default place.
    /// </summary>
    public static Func<(int X, int Y)?>? Position { get; set; }

    /// <summary>Told the window's corner as it closes (<see cref="PictureWindow.Placed"/>'s twin), on its thread. It must not block.</summary>
    public static Action<int, int>? Placed { get; set; }

    /// <summary>Whether a window can be opened here at all: on Windows, and on a Mac with a window server since 2026-10-07 (<see cref="MacLineWindows"/>).</summary>
    public static bool IsAvailable => OperatingSystem.IsWindows() || (OperatingSystem.IsMacOS() && AppKitHost.IsEnabled);

    /// <summary>
    /// The window on <paramref name="buffer"/>: opened at the bottom, following, and brought forward — or the open one brought
    /// forward. Throws <see cref="InvalidOperationException"/> with the reason when no window could be made.
    /// </summary>
    public static void Show(DiagnosticBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException(LogViewText.UnavailableHere);
        }

        if (OperatingSystem.IsMacOS())
        {
            MacLineWindows.ShowLog(buffer);
            return;
        }

        lock (s_gate)
        {
            if (s_open is { Alive: true } open && open.Raise())
            {
                return;
            }

            var window = new LogWindowThread(new DiagnosticFeed(buffer), "Log window", Position, Placed);
            window.Start();
            s_open = window;
        }
    }

    /// <summary>
    /// The open window closed and waited for briefly; nothing without one. True when one was open (later on 2026-10-02, the
    /// user's ask: Ctrl+Alt+G closes the window it opened), false when there was none or the user had closed it already.
    /// </summary>
    public static bool Close()
    {
        if (OperatingSystem.IsMacOS())
        {
            return MacLineWindows.CloseLog();
        }

        LogWindowThread? open;
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
    /// <c>viewer:log-window</c> for the smoke: the class registered, a hidden window answering through its
    /// <c>[UnmanagedCallersOnly]</c> procedure, and the text side of gdi32 the window draws with — Consolas made and measured,
    /// a line drawn into a memory DC and copied out. Nothing is shown.
    /// </summary>
    public static (bool Ok, string Detail) Probe()
    {
        if (OperatingSystem.IsMacOS())
        {
            return MacLineWindows.Probe();
        }

        if (!IsAvailable)
        {
            return (true, "skipped: not Windows");
        }

        return LogWindowThread.Probe();
    }
}

/// <summary>
/// The log window and the thread that pumps its messages (<see cref="LogWindow"/>). Since 2026-10-05 it reads an
/// <see cref="ILineFeed"/>, so <c>/process</c>'s window (<see cref="ProcessWindow"/>) is the same window over a process's output:
/// the class, the drawing and the smoke's probe are shared, and only the feed, the name and the place differ.
/// </summary>
internal sealed unsafe class LogWindowThread
{
    private const uint AppendedMessage = WmApp + 1;
    private const uint RaiseMessage = WmApp + 2;
    private const uint SwapMessage = WmApp + 3;
    private const uint ProbeMessage = WmApp + 9;
    private static readonly IntPtr ProbeAnswer = new(0x10C);
    private static readonly IntPtr AutoScrollTimer = new(1);

    /// <summary>The window's size at 96 DPI when it opens.</summary>
    public const int DefaultWidth = 960;
    public const int DefaultHeight = 600;

    /// <summary>The text's size in points: the console's own.</summary>
    public const int FontPoints = 10;

    /// <summary>How often a drag held above or below the text scrolls it.</summary>
    public const uint AutoScrollMilliseconds = 50;

    // The layout at 96 DPI: the gap round the text, the scroll bar's width and its thumb's shortest length.
    private const int Margin = 6;
    private const int BarWidth = 12;
    private const int MinThumb = 24;

    private static readonly Lock s_classGate = new();
    private static IntPtr s_className;
    private static ushort s_atom;

    private ILineFeed _feed;
    private ILineFeed? _pendingFeed;
    private readonly string _name;
    private readonly Func<(int X, int Y)?>? _position;
    private readonly Action<int, int>? _placed;
    private LogViewState _state = new();
    private readonly WindowChrome _chrome;
    private readonly ManualResetEventSlim _ready = new();
    private readonly List<LogLine> _scratch = [];
    private Thread? _thread;
    private IntPtr _hwnd;
    private string? _failure;
    private bool _started;
    private volatile bool _alive = true;
    private int _appendPosted;
    private bool _subscribed;
    private LogViewStyle _style = LogViewStyle.Black;
    private IntPtr _font;
    private uint _dpi = 96;
    private int _cellWidth;
    private int _cellHeight;
    private int[] _dx = [];
    private IntPtr _memDc;
    private IntPtr _memBitmap;
    private (int Width, int Height) _memSize;
    private IntPtr _arrow;
    private IntPtr _beam;
    private bool _selecting;
    private int? _thumbGrab;
    private (int X, int Y) _mouse;
    private int _wheel;
    private string? _title;

    /// <param name="feed">The lines shown; the window owns it and disposes it as it closes.</param>
    /// <param name="name">The window's name in the log and on its thread: <c>Log window</c>, <c>Process window</c>.</param>
    /// <param name="position">Where it last closed, read as it opens (<see cref="LogWindow.Position"/>); null is Windows' place.</param>
    /// <param name="placed">Told its corner as it closes (<see cref="LogWindow.Placed"/>), on its thread.</param>
    public LogWindowThread(ILineFeed feed, string name, Func<(int X, int Y)?>? position, Action<int, int>? placed)
    {
        _feed = feed;
        _name = name;
        _position = position;
        _placed = placed;
        _chrome = new WindowChrome(name.ToLowerInvariant());
    }

    public bool Alive => _alive;

    /// <summary>The thread started and the window made; throws with the reason when it could not be (the picture viewer's <c>Start</c>).</summary>
    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = _name };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)) || !_started)
        {
            throw new InvalidOperationException(_failure ?? "the window did not start");
        }
    }

    /// <summary>The open window brought forward; false when it is gone.</summary>
    public bool Raise() => _alive && PostMessageW(_hwnd, RaiseMessage, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// The open window shown over <paramref name="feed"/> in place of its own, without a wait (2026-10-05, the code review: the
    /// process window switched to another process by closing and reopening, which held the caller — a slash command under a
    /// reply — for the close's join and the new thread's start). The swap is the window thread's: the old feed is disposed there,
    /// the view starts again at the bottom and following, and the place and size stay. The window owns <paramref name="feed"/>
    /// whatever comes of it: true when the swap was posted, false when the window is gone (the feed disposed; the caller opens a
    /// new window with a feed of its own). A swap posted as the window closes is disposed by the closing thread.
    /// </summary>
    public bool Swap(ILineFeed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        if (!_alive || _hwnd == IntPtr.Zero)
        {
            feed.Dispose();
            return false;
        }

        // A swap not taken yet is overtaken by this one: the window only ever shows the newest.
        Interlocked.Exchange(ref _pendingFeed, feed)?.Dispose();
        if (PostMessageW(_hwnd, SwapMessage, IntPtr.Zero, IntPtr.Zero))
        {
            return true;
        }

        // Not posted (the window went meanwhile): taken back and disposed, unless the closing thread disposed it already.
        if (Interlocked.CompareExchange(ref _pendingFeed, null, feed) == feed)
        {
            feed.Dispose();
        }

        return false;
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
                font = MakeFont(96);
                mem = CreateCompatibleDC(dc);
                bitmap = CreateCompatibleBitmap(dc, 64, 16);
                if (dc == IntPtr.Zero || font == IntPtr.Zero || mem == IntPtr.Zero || bitmap == IntPtr.Zero)
                {
                    return (false, "a DC, the font or the bitmap was not made");
                }

                SelectObject(mem, bitmap);
                SelectObject(mem, font);
                TextMetric metric;
                if (!GetTextMetricsW(mem, &metric) || metric.tmAveCharWidth <= 0 || metric.tmHeight <= 0)
                {
                    return (false, "GetTextMetricsW measured nothing");
                }

                var rect = new Rect { Right = 64, Bottom = 16 };
                int* dx = stackalloc int[4] { metric.tmAveCharWidth, metric.tmAveCharWidth, metric.tmAveCharWidth, metric.tmAveCharWidth };
                SetBkColor(mem, 0);
                SetTextColor(mem, 0x00FFFFFF);
                bool drawn;
                fixed (char* text = "log!")
                {
                    drawn = ExtTextOutW(mem, 0, 0, EtoOpaque, &rect, text, 4, dx);
                }

                if (!drawn || !BitBlt(dc, 0, 0, 64, 16, mem, 0, 0, SrcCopy))
                {
                    return (false, "ExtTextOutW or BitBlt failed");
                }

                return (true, $"a hidden window answered through the window procedure; Consolas {metric.tmAveCharWidth}x{metric.tmHeight} at 96 DPI drawn in a memory DC and copied");
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

    // The window class, registered once per process: the procedure below, the app's icon, no background brush (the paint
    // covers every pixel, so a resize never flashes). The cursor is set per point (WM_SETCURSOR): a beam over the text.
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
                s_className = Marshal.StringToHGlobalUni("NeonSidekick.LogViewer");
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
            if (user != IntPtr.Zero && GCHandle.FromIntPtr(user).Target is LogWindowThread window)
            {
                return window.Handle(hwnd, message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // Nothing may cross back into user32: an exception here would take the process down.
            DiagnosticLog.Error("Viewer", $"The log window failed on message 0x{message:X}.", ex);
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void Run()
    {
        var self = GCHandle.Alloc(this);
        try
        {
            SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            IntPtr instance = GetModuleHandle(IntPtr.Zero);
            if (!EnsureClass(instance, out _failure))
            {
                return;
            }

            _arrow = LoadCursorW(IntPtr.Zero, (IntPtr)IdcArrow);
            _beam = LoadCursorW(IntPtr.Zero, (IntPtr)IdcIBeam);
            _hwnd = CreateWindowExW(0, s_className, IntPtr.Zero, WsOverlappedWindow, CwUseDefault, CwUseDefault, CwUseDefault, CwUseDefault, IntPtr.Zero, IntPtr.Zero, instance, GCHandle.ToIntPtr(self));
            if (_hwnd == IntPtr.Zero)
            {
                _failure = $"CreateWindowExW failed ({Marshal.GetLastPInvokeError()})";
                return;
            }

            // Where it last closed first, so the size and the font are the DPI of the monitor it opens on.
            _chrome.Window = _hwnd;
            _chrome.RestorePosition(_position);
            _chrome.SizeForDpi(DefaultWidth, DefaultHeight);
            SetFont(Math.Max(96u, GetDpiForWindow(_hwnd)));
            ApplyStyle();   // before it is shown: the bar is never light first

            // The run so far, then every line as it comes; a line between the two is read by the second take.
            TakeAppended();
            _feed.Appended += OnAppended;
            _subscribed = true;
            TakeAppended();
            Layout();
            UpdateTitle(force: true);
            ShowWindow(_hwnd, SwShow);
            _chrome.BringForward();
            DiagnosticLog.Info("Viewer", $"{_name} opened.");

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
            DiagnosticLog.Error("Viewer", $"The {_name.ToLowerInvariant()}'s thread failed.", ex);
        }
        finally
        {
            _alive = false;
            if (_subscribed)
            {
                _feed.Appended -= OnAppended;
            }

            _feed.Dispose();
            Interlocked.Exchange(ref _pendingFeed, null)?.Dispose();   // a swap posted as it closed

            if (!_started && _hwnd != IntPtr.Zero)
            {
                // Made but never set up: gone before the handle its procedure reads is freed.
                DestroyWindow(_hwnd);
            }

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

            self.Free();
            _ready.Set();
            DiagnosticLog.Info("Viewer", $"{_name} closed.");
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
                Layout();
                return IntPtr.Zero;
            case WmActivate:
                // A /theme change reaches an open window the next time it is focused.
                ApplyStyle();
                break;
            case WmDpiChanged:
            {
                // Dragged onto a monitor of another scale: Windows' suggested frame, and the font made again for it.
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
                // A key with Alt held is never the log's (2026-10-03): Ctrl+Alt+C is /clear, not a copy.
                var action = TerminalHandoff.AltHeld() ? LogViewAction.None : LogViewState.ActionFor((int)wParam, GetKeyState(VkControl) < 0, _chrome.FullScreen);
                if (action == LogViewAction.None)
                {
                    // The feed's own key first (2026-10-05: the process window's Ctrl+K), its title read again.
                    if (!TerminalHandoff.AltHeld() && _feed.Key((int)wParam, GetKeyState(VkControl) < 0, ViewerState.IsAutoRepeat((long)lParam)))
                    {
                        UpdateTitle();
                        return IntPtr.Zero;
                    }

                    // TAB to the terminal, a Ctrl or Alt chord to the chat (2026-10-03); Alt+F4 and the rest to the default.
                    if (TerminalHandoff.Take((int)wParam))
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

            case WmSetCursor when ((long)lParam & 0xFFFF) == HtClient:
            {
                uint at = GetMessagePos();
                var point = new Point { X = (short)(at & 0xFFFF), Y = (short)((at >> 16) & 0xFFFF) };
                ScreenToClient(hwnd, &point);
                SetCursor(point.X < BarLeft() ? _beam : _arrow);
                return 1;
            }

            case WmMouseWheel:
            {
                uint lines = 3;
                SystemParametersInfoW(SpiGetWheelScrollLines, 0, &lines, 0);
                int rows = LogViewState.WheelRows(ref _wheel, (short)(((long)wParam >> 16) & 0xFFFF), unchecked((int)lines), _state.Page);
                if (rows != 0)
                {
                    _state.ScrollBy(rows);
                    if (_selecting)
                    {
                        ExtendToMouse();
                    }

                    Refresh();
                }

                return IntPtr.Zero;
            }

            case WmLeftButtonDown:
            {
                var (x, y) = PointOf(lParam);
                _mouse = (x, y);
                if (x >= BarLeft())
                {
                    PressBar(y);
                }
                else
                {
                    if (HitAt(x, y) is { } hit)
                    {
                        if (((int)wParam & MkShift) != 0 && _state.Selection is not null)
                        {
                            _state.ExtendSelection(hit);
                        }
                        else
                        {
                            _state.BeginSelection(hit);
                        }
                    }

                    _selecting = true;
                }

                SetCapture(hwnd);
                InvalidateRect(hwnd, null, false);
                return IntPtr.Zero;
            }

            case WmMouseMove when _thumbGrab is { } grab:
                DragThumb(PointOf(lParam).Y - grab);
                return IntPtr.Zero;
            case WmMouseMove when _selecting && ((int)wParam & MkLeftButton) != 0:
            {
                _mouse = PointOf(lParam);
                ExtendToMouse();
                var (top, bottom) = TextRows();
                if (_mouse.Y < top || _mouse.Y >= bottom)
                {
                    SetTimer(hwnd, AutoScrollTimer, AutoScrollMilliseconds, IntPtr.Zero);
                }
                else
                {
                    KillTimer(hwnd, AutoScrollTimer);
                }

                InvalidateRect(hwnd, null, false);
                return IntPtr.Zero;
            }

            case WmTimer when wParam == AutoScrollTimer:
                AutoScroll();
                return IntPtr.Zero;
            case WmLeftButtonUp:
                ReleaseCapture();
                return IntPtr.Zero;
            case WmCaptureChanged:
                _selecting = false;
                _thumbGrab = null;
                KillTimer(hwnd, AutoScrollTimer);
                InvalidateRect(hwnd, null, false);
                return IntPtr.Zero;
            case WmLeftButtonDoubleClick when PointOf(lParam).X < BarLeft():
                Do(LogViewAction.ToggleFullScreen);
                return IntPtr.Zero;
            case AppendedMessage:
                TakeAppended();
                return IntPtr.Zero;
            case RaiseMessage:
                ApplyStyle();
                _chrome.BringForward();
                return IntPtr.Zero;
            case SwapMessage:
                TakeSwap();
                ApplyStyle();
                _chrome.BringForward();
                return IntPtr.Zero;
            case WmClose:
                _chrome.RememberPosition(_placed);
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    // From the logging thread (DiagnosticBuffer.Appended) or a process's pump (OutputBuffer.Appended): one message in flight however many lines arrive, so a burst of
    // Trace lines is one read and one paint. Nothing here may log: it would raise itself again.
    private void OnAppended()
    {
        if (Interlocked.Exchange(ref _appendPosted, 1) == 0 && (!_alive || _hwnd == IntPtr.Zero || !PostMessageW(_hwnd, AppendedMessage, IntPtr.Zero, IntPtr.Zero)))
        {
            Volatile.Write(ref _appendPosted, 0);
        }
    }

    // Swap's other half, on the window's thread: the old feed let go, a fresh view over the new one (at the bottom, following, the
    // grid the same), its lines read and the title forced, as Run does at the open. A drag in progress ends first.
    private void TakeSwap()
    {
        var next = Interlocked.Exchange(ref _pendingFeed, null);
        if (next is null)
        {
            return;   // overtaken: a later swap's message takes the newest
        }

        ReleaseCapture();
        _feed.Appended -= OnAppended;
        _feed.Dispose();
        _feed = next;
        _state = new LogViewState();
        _scratch.Clear();
        TakeAppended();
        _feed.Appended += OnAppended;
        TakeAppended();
        Layout();
        UpdateTitle(force: true);
        InvalidateRect(_hwnd, null, false);
    }

    // The lines the window lacks read from the buffer (the flag cleared first, so a line added during the read posts again).
    private void TakeAppended()
    {
        Volatile.Write(ref _appendPosted, 0);
        _scratch.Clear();
        long first = _feed.CopySince(_state.NextSeq, _scratch);
        if (_state.Append(_scratch, first) && _hwnd != IntPtr.Zero)
        {
            Refresh();
        }
        else if (_hwnd != IntPtr.Zero)
        {
            UpdateTitle();   // no new line, but the feed's title may have changed (a process's exit, a lapsed kill key)
        }
    }

    private void Do(LogViewAction action)
    {
        switch (action)
        {
            case LogViewAction.ToggleFullScreen:
                _chrome.SetFullScreen(!_chrome.FullScreen);
                break;
            case LogViewAction.LeaveFullScreen:
                _chrome.SetFullScreen(false);
                break;
            case LogViewAction.Close:
                PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                break;
            case LogViewAction.Copy:
                Copy();
                break;
            default:
                if (_state.Apply(action))
                {
                    Refresh();
                }

                break;
        }
    }

    // Ctrl+C: the selection on the clipboard as text; nothing selected copies nothing.
    private void Copy()
    {
        string text = _state.SelectedText();
        if (text.Length > 0 && !NeonSidekick.UI.WindowsClipboard.TrySetText(text))
        {
            DiagnosticLog.Warn("Viewer", "The log window could not copy: the clipboard is busy.");
        }
    }

    private void Refresh()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;   // InvalidateRect on no window would repaint every window on the desktop
        }

        UpdateTitle();
        InvalidateRect(_hwnd, null, false);
    }

    // The title the feed gives (whether the window follows, and since 2026-10-05 a process's state); set only when it changes.
    private void UpdateTitle(bool force = false)
    {
        string title = _feed.Title(_state.Following);
        if (force || !string.Equals(title, _title, StringComparison.Ordinal))
        {
            _title = title;
            SetWindowTextW(_hwnd, title);
        }
    }

    // The theme in force on the bar and in the text (LogViewStyle); repainted only when it changed.
    private void ApplyStyle()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        bool themed = _chrome.Themed();
        var palette = NeonSidekick.UI.Theme.Current;
        _chrome.ApplyCaption(ViewerStyle.For(palette, themed));
        var style = LogViewStyle.For(palette, themed);
        if (style != _style)
        {
            _style = style;
            InvalidateRect(_hwnd, null, false);
        }
    }

    // Consolas at FontPoints for dpi, measured for the grid's cell; the old font let go only once the new one is selected.
    private void SetFont(uint dpi)
    {
        IntPtr font = MakeFont(dpi);
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
        if (!measured || metric.tmAveCharWidth <= 0 || metric.tmHeight <= 0)
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
        _cellWidth = metric.tmAveCharWidth;
        _cellHeight = metric.tmHeight + metric.tmExternalLeading;
    }

    private static IntPtr MakeFont(uint dpi)
    {
        int height = -(int)Math.Round(FontPoints * dpi / 72.0, MidpointRounding.AwayFromZero);
        fixed (char* face = "Consolas")
        {
            return CreateFontW(height, 0, 0, 0, FwNormal, 0, 0, 0, DefaultCharset, 0, 0, ClearTypeQuality, FixedPitchModern, face);
        }
    }

    private int Scale(int pixels) => (int)(pixels * _dpi / 96);

    private int BarLeft()
    {
        Rect client;
        GetClientRect(_hwnd, &client);
        return client.Right - Scale(BarWidth);
    }

    // The text area's first and past-last whole row's pixel rows.
    private (int Top, int Bottom) TextRows() => (Scale(Margin), Scale(Margin) + _state.VisibleRows * Math.Max(1, _cellHeight));

    // The grid the client area holds, handed to the state, which re-wraps when the columns change.
    private void Layout()
    {
        if (_cellWidth <= 0 || _cellHeight <= 0)
        {
            return;
        }

        Rect client;
        GetClientRect(_hwnd, &client);
        int width = client.Right - Scale(BarWidth) - 2 * Scale(Margin);
        int height = client.Bottom - 2 * Scale(Margin);
        _state.Resize(Math.Max(1, width / _cellWidth), Math.Max(1, height / _cellHeight));
        Refresh();
    }

    // The place under a client point: the row by the cell's height, the column rounded to the nearer caret.
    private LogPosition? HitAt(int x, int y)
    {
        if (_cellWidth <= 0 || _cellHeight <= 0)
        {
            return null;
        }

        int row = (int)Math.Floor((double)(y - Scale(Margin)) / _cellHeight);
        int column = (int)Math.Floor((x - Scale(Margin) + _cellWidth / 2.0) / _cellWidth);
        return _state.HitTest(row, column);
    }

    private void ExtendToMouse()
    {
        if (HitAt(_mouse.X, _mouse.Y) is { } hit)
        {
            _state.ExtendSelection(hit);
        }
    }

    // A drag held above or below the text: a row a tick (more the further out), the selection following the mouse.
    private void AutoScroll()
    {
        var (top, bottom) = TextRows();
        int rows = _mouse.Y < top ? -(1 + (top - _mouse.Y) / Math.Max(1, _cellHeight)) : _mouse.Y >= bottom ? 1 + (_mouse.Y - bottom) / Math.Max(1, _cellHeight) : 0;
        if (rows == 0 || !_selecting)
        {
            KillTimer(_hwnd, AutoScrollTimer);
            return;
        }

        _state.ScrollBy(rows);
        ExtendToMouse();
        Refresh();
    }

    private (int Offset, int Length, int Track) ThumbNow()
    {
        Rect client;
        GetClientRect(_hwnd, &client);
        var (offset, length) = LogViewState.Thumb(_state.TotalRows, _state.VisibleRows, _state.TopRow, client.Bottom, Scale(MinThumb));
        return (offset, length, client.Bottom);
    }

    // A press on the scroll bar: on the thumb it is held for a drag, above or below it a page that way.
    private void PressBar(int y)
    {
        var (offset, length, _) = ThumbNow();
        if (_state.TotalRows <= _state.VisibleRows)
        {
            return;
        }

        if (y >= offset && y < offset + length)
        {
            _thumbGrab = y - offset;
            return;
        }

        _state.Apply(y < offset ? LogViewAction.PageUp : LogViewAction.PageDown);
        Refresh();
    }

    private void DragThumb(int offset)
    {
        var (_, length, track) = ThumbNow();
        _state.ScrollTo(LogViewState.TopForThumb(offset, _state.TotalRows, _state.VisibleRows, track, length));
        Refresh();
    }

    // A mouse message's client point: GET_X_LPARAM / GET_Y_LPARAM, signed (a captured mouse left of or above the window).
    private static (int X, int Y) PointOf(IntPtr lParam) => ((short)((long)lParam & 0xFFFF), (short)(((long)lParam >> 16) & 0xFFFF));

    // A rectangle filled with one colour: ExtTextOutW's opaque fill, no brush to make and free.
    private static void Fill(IntPtr dc, Rect rect, uint color)
    {
        SetBkColor(dc, color);
        ExtTextOutW(dc, 0, 0, EtoOpaque, &rect, null, 0, null);
    }

    // Characters [from, from + count) of text at (x, y), one cell each, so what is drawn is where a click lands.
    private void Text(IntPtr dc, int x, int y, string text, int from, int count, uint color)
    {
        if (count <= 0)
        {
            return;
        }

        if (_dx.Length < count)
        {
            _dx = new int[Math.Max(count, _state.Columns + 1)];
        }

        _dx.AsSpan().Fill(_cellWidth);
        SetTextColor(dc, color);
        fixed (char* chars = text)
        fixed (int* dx = _dx)
        {
            ExtTextOutW(dc, x, y, 0, null, chars + from, (uint)count, dx);
        }
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
                if (_font != IntPtr.Zero)
                {
                    SelectObject(_memDc, _font);
                }
            }

            if (_memSize != (width, height))
            {
                IntPtr bitmap = CreateCompatibleBitmap(hdc, width, height);
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
            int left = Scale(Margin), top = Scale(Margin);
            if (_cellWidth > 0)
            {
                if (_state.LineCount == 0)
                {
                    string empty = _feed.Empty;
                    Text(dc, left, top, empty, 0, empty.Length, _style.Dim);
                }

                var rows = _state.Rows();
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    int y = top + i * _cellHeight;
                    var selected = _state.RowSelection(row);
                    if (selected is { } run)
                    {
                        Fill(dc, new Rect { Left = left + run.From * _cellWidth, Top = y, Right = left + run.To * _cellWidth, Bottom = y + _cellHeight }, _style.SelectionBack);
                    }

                    Text(dc, left, y, row.Text, row.Start, row.Length, _style.ColorOf(row.Level));
                    if (selected is { } over)
                    {
                        int count = Math.Min(over.To, row.Length) - over.From;
                        Text(dc, left + over.From * _cellWidth, y, row.Text, row.Start + over.From, count, _style.SelectionText);
                    }
                }
            }

            // The scroll bar: its track the whole height, the thumb on it while there is more than a page.
            int barLeft = width - Scale(BarWidth);
            Fill(dc, new Rect { Left = barLeft, Right = width, Bottom = height }, _style.Track);
            if (_state.TotalRows > _state.VisibleRows)
            {
                var (offset, length) = LogViewState.Thumb(_state.TotalRows, _state.VisibleRows, _state.TopRow, height, Scale(MinThumb));
                int inset = Scale(2);
                Fill(dc, new Rect { Left = barLeft + inset, Top = offset, Right = width - inset, Bottom = offset + length }, _thumbGrab is null ? _style.Thumb : _style.ThumbActive);
            }

            BitBlt(hdc, 0, 0, width, height, dc, 0, 0, SrcCopy);
        }
        finally
        {
            EndPaint(hwnd, &ps);
        }
    }
}
