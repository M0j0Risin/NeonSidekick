using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The video window (2026-10-05, the YouTube plan): a YouTube video in a window of the app's own, a plain Win32 window on its
/// own thread like the picture viewer's, hosting WebView2 (<see cref="WebViewHost"/>) on the IFrame Player API page
/// (<see cref="VideoPage"/>). One window per process: a second <see cref="Play"/> switches the open one to the new video in
/// place. It shows without taking the keyboard (the terminal keeps it until the user clicks into the video), opens where it
/// last closed (<see cref="Position"/>, its own place), wears the theme on its bar, and closes with the app. Commands and the
/// page's reports go both ways as JSON (<see cref="VideoMessages"/>); what it last reported is <see cref="Snapshot"/>, read
/// from any thread without waiting on the window. <see cref="Player"/> is all of it as an <see cref="IVideoPlayer"/>. Windows-only, so
/// <c>Program</c> reaches it behind <c>OperatingSystem.IsWindows()</c> and passes null elsewhere (the camera's shape).
/// </summary>
[SupportedOSPlatform("windows")]
public static class VideoWindow
{
    private static readonly Lock s_gate = new();
    private static VideoWindowThread? s_open;

    /// <summary>Where the window was when it last closed (<c>Program</c>: the profile's <c>VideoWindowLeft</c> / <c>VideoWindowTop</c>); null is Windows' own place.</summary>
    public static Func<(int X, int Y)?>? Position { get; set; }

    /// <summary>Told the window's corner as it closes, on its thread. It must not block.</summary>
    public static Action<int, int>? Placed { get; set; }

    /// <summary>The app's home (<c>Program</c>): the window's WebView2 profile and page live in <c>&lt;home&gt;\webview2</c>.</summary>
    public static string? Home { get; set; }

    /// <summary>
    /// Where a link the player opens in a new window goes (the YouTube logo, "Watch on YouTube"): the default browser
    /// (<see cref="Llm.PersonaFile.OpenInBrowser"/>, the counted process-start site), on the window's thread.
    /// </summary>
    public static Action<string> OpenLink { get; set; } = Llm.PersonaFile.OpenInBrowser;

    /// <summary>The window as an <see cref="IVideoPlayer"/> (<c>Program</c> hands it to the app on Windows).</summary>
    public static IVideoPlayer Player { get; } = new WindowsVideoPlayer();

    /// <summary>Each snapshot the open window reports, and null as it closes (<see cref="IVideoPlayer.Changed"/>).</summary>
    internal static event Action<VideoSnapshot?>? Changed;

    /// <summary>
    /// The window on <paramref name="request"/>'s video: the open one switched to it (raised over the others, the keyboard left
    /// where it is), else a new one. Throws <see cref="InvalidOperationException"/> with <see cref="VideoText"/>'s reason when
    /// none can be had.
    /// </summary>
    public static void Play(VideoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (s_gate)
        {
            if (s_open is { Alive: true } open && open.Play(request))
            {
                return;
            }

            CheckRuntime();
            string home = Home ?? Path.Combine(Path.GetTempPath(), "NeonSidekick");
            var window = new VideoWindowThread(request, VideoPage.UserDataFolder(home), VideoPage.SiteFolder(home));
            s_open = window;
            window.Start();
        }
    }

    /// <summary>A command for the open video; false when none is open.</summary>
    public static bool Send(VideoCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (s_gate)
        {
            return s_open is { Alive: true } open && open.Send(VideoMessages.Command(command));
        }
    }

    /// <summary>
    /// What the open window last reported; null with none open — except that a window whose browser would not start keeps its
    /// failed snapshot after it closes, until the next <see cref="Play"/>, so the caller can say why.
    /// </summary>
    public static VideoSnapshot? Snapshot
    {
        get
        {
            lock (s_gate)
            {
                return s_open switch
                {
                    { Alive: true } open => open.Snapshot,
                    { Snapshot.State: VideoState.Failed } failed => failed.Snapshot,
                    _ => null,
                };
            }
        }
    }

    /// <summary>The open window closed and waited for briefly (Esc, <c>youtube_control close</c>, the app's exit); true when one was open.</summary>
    public static bool Close()
    {
        VideoWindowThread? open;
        lock (s_gate)
        {
            open = s_open;
            s_open = null;
        }

        bool alive = open is { Alive: true };
        open?.Close();
        return alive;
    }

    internal static void Raise(VideoSnapshot? snapshot)
    {
        try
        {
            Changed?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Video", "A video window listener failed.", ex);
        }
    }

    // The runtime, before a window is made for nothing: none installed, or no loader beside the exe, is a sentence.
    private static void CheckRuntime()
    {
        string? version;
        int hr;
        try
        {
            version = WebViewNative.RuntimeVersion(out hr);
        }
        catch (DllNotFoundException)
        {
            throw new InvalidOperationException(VideoText.NoLoader);
        }

        if (version is null)
        {
            throw new InvalidOperationException(hr == WebViewNative.ENoRuntime ? VideoText.NoRuntime : VideoText.Failed("runtime", hr));
        }
    }

    private sealed class WindowsVideoPlayer : IVideoPlayer
    {
        public VideoSnapshot? Snapshot => VideoWindow.Snapshot;

        public event Action<VideoSnapshot?>? Changed
        {
            add => VideoWindow.Changed += value;
            remove => VideoWindow.Changed -= value;
        }

        public void Play(VideoRequest request) => VideoWindow.Play(request);

        public bool Send(VideoCommand command) => VideoWindow.Send(command);

        public bool Close() => VideoWindow.Close();
    }
}

/// <summary>The video window and the thread that pumps its messages and owns its WebView2 (<see cref="VideoWindow"/>).</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class VideoWindowThread
{
    private const uint PlayMessage = WmApp + 1;
    private const uint CommandMessage = WmApp + 2;

    /// <summary>The window's size at 96 DPI before the user changes it: 16:9, the camera window's.</summary>
    public const int DefaultWidth = 960;
    public const int DefaultHeight = 540;

    private static readonly Lock s_classGate = new();
    private static IntPtr s_className;
    private static ushort s_atom;

    private readonly WindowChrome _chrome = new("video window");
    private readonly ManualResetEventSlim _ready = new();
    private readonly Lock _gate = new();
    private readonly Queue<string> _outbox = new();
    private readonly string _userDataFolder;
    private readonly string _siteFolder;
    private VideoRequest? _pending;
    private VideoSnapshot _snapshot;
    private WebViewHost? _host;
    private bool _pageReady;
    private bool _pageFullScreen;
    private string? _shownTitle;
    private Thread? _thread;
    private IntPtr _hwnd;
    private string? _failure;
    private bool _started;
    private bool _ole;
    private volatile bool _alive = true;

    public VideoWindowThread(VideoRequest first, string userDataFolder, string siteFolder)
    {
        _pending = first;
        _snapshot = VideoSnapshot.Opening(first.VideoId);
        _userDataFolder = userDataFolder;
        _siteFolder = siteFolder;
    }

    public bool Alive => _alive;

    public VideoSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>The thread started and the window made (the WebView follows on its own); throws with the reason when it could not be.</summary>
    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Video window" };
        _thread.SetApartmentState(ApartmentState.STA);   // WebView2 wants an STA that pumps messages
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)) || !_started)
        {
            throw new InvalidOperationException(_failure ?? "the window did not start");
        }
    }

    /// <summary>Switches the open window to <paramref name="request"/> (the newest wins); false when the window is gone.</summary>
    public bool Play(VideoRequest request)
    {
        lock (_gate)
        {
            _pending = request;
            _snapshot = VideoSnapshot.Opening(request.VideoId) with { Version = _snapshot.Version + 1 };
        }

        return _alive && PostMessageW(_hwnd, PlayMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>A command's message for the page, sent once the page can hear it; false when the window is gone.</summary>
    public bool Send(string json)
    {
        lock (_gate)
        {
            _outbox.Enqueue(json);
        }

        return _alive && PostMessageW(_hwnd, CommandMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>The window asked to close, and its thread waited for a moment.</summary>
    public void Close()
    {
        if (_alive && PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero))
        {
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
    }

    // The window class, registered once per process: the procedure below, the app's icon, black behind the view.
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
                s_className = Marshal.StringToHGlobalUni("NeonSidekick.VideoPlayer");
            }

            IntPtr icon = LoadImageW(instance, (IntPtr)ApplicationIconId, ImageIcon, 0, 0, LrDefaultSize | LrShared);
            var wc = new WndClassEx
            {
                cbSize = (uint)sizeof(WndClassEx),
                style = CsHRedraw | CsVRedraw,
                lpfnWndProc = &WindowProcedure,
                hInstance = instance,
                hIcon = icon,
                hIconSm = icon,
                hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IdcArrow),
                hbrBackground = GetStockObject(BlackBrush),
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
            if (message == WmNcCreate && ((CreateStruct*)lParam)->lpCreateParams is var created && created != IntPtr.Zero)
            {
                SetWindowLongPtr(hwnd, GwlpUserData, created);
            }

            IntPtr user = GetWindowLongPtr(hwnd, GwlpUserData);
            if (user != IntPtr.Zero && GCHandle.FromIntPtr(user).Target is VideoWindowThread window)
            {
                return window.Handle(hwnd, message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // Nothing may cross back into user32: an exception here would take the process down.
            DiagnosticLog.Error("Video", $"The video window failed on message 0x{message:X}.", ex);
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void Run()
    {
        var self = GCHandle.Alloc(this);
        try
        {
            SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            _ole = OleInitialize(IntPtr.Zero) >= 0;
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

            // Where it last closed first, so the size below is the DPI of the monitor it opens on.
            _chrome.Window = _hwnd;
            _chrome.RestorePosition(VideoWindow.Position);
            _chrome.SizeForDpi(DefaultWidth, DefaultHeight);
            ApplyStyle();   // before it is shown: the bar is never light first
            ShowTitle(null);
            ShowWindow(_hwnd, SwShowNoActivate);
            _chrome.RaiseQuietly();

            VideoPage.Write(_siteFolder);
            _host = new WebViewHost(_hwnd)
            {
                Message = OnPageMessage,
                Popup = OnPopup,
                Failed = OnFailed,
                Key = OnKey,
                FullScreen = OnPageFullScreen,
            };
            _host.Start(_userDataFolder, _siteFolder, VideoPage.Url);
            DiagnosticLog.Info("Video", "Video window opened.");

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
            DiagnosticLog.Error("Video", "The video window's thread failed.", ex);
        }
        finally
        {
            _alive = false;
            _host?.Close();
            if (!_started && _hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
            }

            if (_ole)
            {
                OleUninitialize();
            }

            self.Free();
            _ready.Set();
            if (_started)
            {
                VideoWindow.Raise(null);
                DiagnosticLog.Info("Video", "Video window closed.");
            }
        }
    }

    private IntPtr Handle(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case PlayMessage:
                Flush();
                if (IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SwShowNoActivate);
                }

                _chrome.RaiseQuietly();
                return IntPtr.Zero;
            case CommandMessage:
                Flush();
                return IntPtr.Zero;
            case WmSize:
                _host?.Resize();
                return IntPtr.Zero;
            case WmMove:
                _host?.ParentMoved();
                break;
            case WmActivate:
                ApplyStyle();   // a /theme change reaches the window the next time it is focused
                break;
            case WmKeyDown:
            case WmSysKeyDown:
                if (OnKey((int)wParam))
                {
                    return IntPtr.Zero;
                }

                break;
            case WmClose:
                _chrome.RememberPosition(VideoWindow.Placed);
                _host?.Close();
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    // What waits for the page, sent once it can hear it: the newest video first, then the commands in their order. A command
    // the page gets before its player is ready is dropped there; the snapshot says what state it is in.
    private void Flush()
    {
        if (!_pageReady || _host is null)
        {
            return;
        }

        VideoRequest? request;
        string[] commands;
        lock (_gate)
        {
            request = _pending;
            _pending = null;
            commands = [.. _outbox];
            _outbox.Clear();
        }

        if (request is not null)
        {
            _host.Post(VideoMessages.Load(request));
            ShowTitle(null);
        }

        foreach (string command in commands)
        {
            _host.Post(command);
        }
    }

    private void OnPageMessage(string text)
    {
        VideoPageEvent kind;
        VideoSnapshot snapshot;
        lock (_gate)
        {
            (kind, snapshot) = VideoMessages.Apply(_snapshot, text);
            _snapshot = snapshot;
        }

        switch (kind)
        {
            case VideoPageEvent.Page:
                _pageReady = true;
                Flush();
                break;
            case VideoPageEvent.State:
                ShowTitle(snapshot.Title);
                VideoWindow.Raise(snapshot);
                break;
            case VideoPageEvent.Error:
                DiagnosticLog.Info("Video", $"YouTube refused video {snapshot.VideoId} in the player (error {snapshot.Error}).");
                VideoWindow.Raise(snapshot);
                break;
        }
    }

    private void OnPopup(string url)
    {
        try
        {
            VideoWindow.OpenLink(url);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn("Video", $"Could not open {url} in the browser: {ex.Message}");
        }
    }

    // The browser would not start: the snapshot says why, and the window, which could only ever be black, closes.
    private void OnFailed(WebViewStep step, int hr)
    {
        VideoSnapshot snapshot;
        lock (_gate)
        {
            _snapshot = snapshot = _snapshot with { State = VideoState.Failed, Failure = VideoText.Failed(step.ToString(), hr), Version = _snapshot.Version + 1 };
        }

        VideoWindow.Raise(snapshot);
        PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    // A key the window has (from the view's accelerators, or its own procedure): F11, Esc, then a chord for the chat.
    private bool OnKey(int virtualKey)
    {
        bool control = GetKeyState(VkControl) < 0;
        bool alt = GetKeyState(VkMenu) < 0;
        switch (VideoKeys.ActionFor(virtualKey, _chrome.FullScreen || _pageFullScreen, control, alt))
        {
            case VideoKeyAction.ToggleFullScreen:
                _chrome.SetFullScreen(!_chrome.FullScreen);
                _host?.Resize();
                return true;
            case VideoKeyAction.LeaveFullScreen:
                if (_pageFullScreen)
                {
                    _host?.LeavePageFullScreen();
                }

                _chrome.SetFullScreen(false);
                _host?.Resize();
                return true;
            case VideoKeyAction.Close:
                PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                return true;
            default:
                return TerminalHandoff.Take(virtualKey);
        }
    }

    // The player's own full screen (its button, a double-click) takes the window with it.
    private void OnPageFullScreen(bool on)
    {
        _pageFullScreen = on;
        _chrome.SetFullScreen(on);
        _host?.Resize();
    }

    private void ShowTitle(string? videoTitle)
    {
        string title = VideoText.Title(videoTitle);
        if (title != _shownTitle)
        {
            _shownTitle = title;
            SetWindowTextW(_hwnd, title);
        }
    }

    private void ApplyStyle()
    {
        if (_hwnd != IntPtr.Zero)
        {
            _chrome.ApplyCaption(ViewerStyle.For(NeonSidekick.UI.Theme.Current, _chrome.Themed()));
        }
    }
}
