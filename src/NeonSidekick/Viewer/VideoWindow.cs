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
///
/// <para>Two locks (2026-10-06, the code review's catch): <c>s_gate</c> guards only which window is open and is never held while
/// anything waits, so <see cref="Snapshot"/> and <see cref="Send"/> never block; <c>s_playGate</c> lines up the plays across a
/// new window's start (up to 10 s). Until then one lock did both, so a play opening a window blocked every snapshot read, and
/// the voice pause, which reads one under its own lock, could wait on a window thread that was waiting on it. Versions come
/// from one count for the process (<see cref="NextVersion"/>), so a new window's reports are newer than any old one's, and a
/// window another has replaced reports nothing more, its close included.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class VideoWindow
{
    private static readonly Lock s_gate = new();
    private static readonly Lock s_playGate = new();
    private static VideoWindowThread? s_open;
    private static long s_version;

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

        // A file's src made here, on the caller's thread (2026-11-12): a path no URI can take fails with a sentence, never on the window's.
        string? src;
        try
        {
            src = request.IsFile ? VideoPage.FileSrc(request.FilePath!) : null;
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException(e.Message, e);
        }

        lock (s_playGate)
        {
            VideoWindowThread? open;
            lock (s_gate)
            {
                open = s_open;
            }

            if (open is { Alive: true } && open.Play(request, src))
            {
                return;
            }

            CheckRuntime();
            string home = Home ?? Path.Combine(Path.GetTempPath(), "NeonSidekick");
            var window = new VideoWindowThread(request, src, VideoPage.UserDataFolder(home), VideoPage.SiteFolder(home));
            lock (s_gate)
            {
                s_open = window;
            }

            window.Start();   // outside s_gate: a snapshot read or a command never waits on a window starting
        }
    }

    /// <summary>The next report's <see cref="VideoSnapshot.Version"/>: one count for every window of the process.</summary>
    internal static long NextVersion() => Interlocked.Increment(ref s_version);

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

    /// <summary>
    /// <paramref name="from"/>'s report (null as it closes) raised, unless another window has replaced it: a closing window that
    /// was swapped out under a play (ESC as the model played) would otherwise end that play's wait as "closed", and reset the
    /// voice pause, with a video opening.
    /// </summary>
    internal static void Report(VideoWindowThread from, VideoSnapshot? snapshot)
    {
        bool current;
        lock (s_gate)
        {
            current = s_open == from || (snapshot is null && s_open is null or { Alive: false });
        }

        if (current)
        {
            Raise(snapshot);
        }
    }

    private static void Raise(VideoSnapshot? snapshot)
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

        /// <summary>The Windows window plays video files on its file page (2026-11-12).</summary>
        public bool PlaysFiles => true;
    }
}

/// <summary>The video window and the thread that pumps its messages and owns its WebView2 (<see cref="VideoWindow"/>).</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class VideoWindowThread
{
    private const uint PlayMessage = WmApp + 1;
    private const uint CommandMessage = WmApp + 2;

    // The page's timer (2026-11-13, the review's fix): armed at each navigation, killed by the page's own "page" message.
    private const nint PageTimer = 1;

    /// <summary>
    /// How long a navigated page may take to say it is ready before the window gives it up: inside <c>play_video</c>'s 12 s wait even
    /// after a cold WebView2 start, so that wait sees the failure and sends the file to its default app.
    /// </summary>
    public static readonly TimeSpan PageWait = TimeSpan.FromSeconds(8);

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
    private string? _pendingSrc;

    // The level the waiting file play carries over from the play it replaced (2026-11-13).
    private VideoSound? _pendingSound;
    private string? _filePath;
    private VideoSnapshot _snapshot;
    private WebViewHost? _host;

    // Which page the view was last navigated to, whether it said it was ready, whether its wait ran out (2026-11-13). Under _gate.
    private readonly VideoPageWatch _watch;
    private bool _pageFullScreen;
    private string? _shownTitle;
    private Thread? _thread;
    private IntPtr _hwnd;
    private string? _failure;
    private bool _started;
    private bool _ole;
    private volatile bool _alive = true;

    public VideoWindowThread(VideoRequest first, string? src, string userDataFolder, string siteFolder)
    {
        _pending = first;
        _pendingSrc = src;
        _filePath = first.FilePath;
        _watch = new VideoPageWatch(first.IsFile);   // a window opened by a file play starts on the file page
        _snapshot = VideoSnapshot.Opening(first) with { Version = VideoWindow.NextVersion() };
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

    /// <summary>
    /// Switches the open window to <paramref name="request"/> (the newest wins); false when the window is gone. While the window
    /// is still starting (no handle yet) the request only waits: the page's first flush takes the newest.
    /// </summary>
    public bool Play(VideoRequest request, string? src)
    {
        lock (_gate)
        {
            // The level of the play this replaces, any kind (2026-11-13): the file's load applies it, and its opening snapshot says it.
            _pendingSound = VideoSnapshot.SoundOf(_snapshot);
            _pending = request;
            _pendingSrc = src;
            _filePath = request.FilePath;
            _snapshot = VideoSnapshot.Opening(request, _pendingSound) with { Version = VideoWindow.NextVersion() };
            // The last video's commands still queued (a seek sent while it loaded) are never the new one's (2026-11-14, the review's #35),
            // as a page timeout drops them; a command sent after this play queues after it and still goes.
            _outbox.Clear();
        }

        return Post(PlayMessage);
    }

    /// <summary>A command's message for the page, sent once the page can hear it; false when the window is gone.</summary>
    public bool Send(string json)
    {
        lock (_gate)
        {
            _outbox.Enqueue(json);
        }

        return Post(CommandMessage);
    }

    /// <summary>The window asked to close (once it has started, if it is starting), and its thread waited for a moment.</summary>
    public void Close()
    {
        _ready.Wait(TimeSpan.FromSeconds(10));
        if (_alive && PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero))
        {
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
    }

    // A wake-up for the window's procedure. Before the window is up there is no handle to post to (a null one would post to
    // the caller's own thread): what was queued waits for the page's first flush, which comes after the window is made.
    private bool Post(uint message) =>
        _alive && (!_ready.IsSet || PostMessageW(_hwnd, message, IntPtr.Zero, IntPtr.Zero));

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
            VideoRequest first;
            lock (_gate)
            {
                first = _pending!;
                _watch.Navigated(first.IsFile);
            }

            _host.Start(_userDataFolder, _siteFolder, VideoPage.UrlFor(first, _siteFolder), WebViewProfile.VideoFor(_siteFolder));
            ArmPageTimer();
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
                VideoWindow.Report(this, null);
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
            case WmTimer when wParam == PageTimer:
                KillTimer(hwnd, PageTimer);
                OnPageTimeout();
                return IntPtr.Zero;
            case WmClose:
                TerminalHandoff.ReturnFocus(hwnd);   // the terminal in front again when this window had it (2026-10-13, the user's ask)
                _chrome.RememberPosition(VideoWindow.Placed);
                _host?.Close();
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmDestroy:
                KillTimer(hwnd, PageTimer);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    // What waits for the page, sent once it can hear it: the newest video first, then the commands in their order. A command
    // the page gets before its player is ready is dropped there; the snapshot says what state it is in. A request of the other
    // kind than the page shown (2026-11-12: a file on the YouTube player, YouTube on the file page), or one after a page that never
    // came (2026-11-13), navigates first, the request and the commands left waiting; the new page's own "page" flushes them
    // (VideoPageWatch decides, covered there). The decision is made under _gate, the navigation after it.
    private void Flush()
    {
        if (_host is null)
        {
            return;
        }

        VideoRequest? request = null;
        string? src = null;
        VideoSound? sound = null;
        string[] commands = [];
        string? navigate = null;
        lock (_gate)
        {
            switch (_watch.Next(_pending))
            {
                case VideoPageStep.Wait:
                    return;
                case VideoPageStep.Navigate:
                    _watch.Navigated(_pending!.IsFile);
                    navigate = VideoPage.UrlFor(_pending, _siteFolder);
                    break;
                default:
                    request = _pending;
                    src = _pendingSrc;
                    sound = _pendingSound;
                    _pending = null;
                    _pendingSrc = null;
                    _pendingSound = null;
                    commands = [.. _outbox];
                    _outbox.Clear();
                    break;
            }
        }

        if (navigate is not null)
        {
            _host.Navigate(navigate);
            ArmPageTimer();
            return;
        }

        if (request is not null)
        {
            _host.Post(request.IsFile ? VideoMessages.LoadFile(request, src!, sound) : VideoMessages.Load(request));
            ShowTitle(request.FileName);
        }

        foreach (string command in commands)
        {
            _host.Post(command);
        }
    }

    // The page's wait starts (each navigation's): a page that never says it is ready is given up at PageWait.
    private void ArmPageTimer() => SetTimer(_hwnd, PageTimer, (uint)PageWait.TotalMilliseconds, IntPtr.Zero);

    // The page's wait ran out with no "page" from it (2026-11-13, the review's fix): what waited is dropped — the play may have gone to
    // the default app by now, and a page that loads late must not start a second copy — and the snapshot fails with the reason, so the
    // play's wait answers. The next play navigates again.
    private void OnPageTimeout()
    {
        VideoSnapshot snapshot;
        bool filePage;
        lock (_gate)
        {
            if (!_watch.TimedOut())
            {
                return;
            }

            filePage = _watch.ShowingFile;
            _pending = null;
            _pendingSrc = null;
            _pendingSound = null;
            _outbox.Clear();
            _snapshot = snapshot = _snapshot with { State = VideoState.Failed, Failure = VideoText.PageDidNotLoad, Version = VideoWindow.NextVersion() };
        }

        DiagnosticLog.Warn("Video", VideoText.PageDidNotLoadLogLine(filePage));
        VideoWindow.Report(this, snapshot);
    }

    private void OnPageMessage(string text)
    {
        VideoPageEvent kind;
        VideoSnapshot snapshot;
        VideoSnapshot before;
        string? filePath;
        bool asks = false;
        lock (_gate)
        {
            before = _snapshot;
            (kind, snapshot) = VideoMessages.Apply(_snapshot, text);
            if (kind is VideoPageEvent.State or VideoPageEvent.Error or VideoPageEvent.NoPicture)
            {
                snapshot = snapshot with { Version = VideoWindow.NextVersion() };
            }

            filePath = _filePath;

            // A file's error 2 or 4 waits for the disk's word (below) before it is the snapshot: a missing file and a dead share both read so.
            asks = kind == VideoPageEvent.Error && snapshot is { IsFile: true, Error: 2 or 4 } && filePath is not null;
            if (!asks)
            {
                _snapshot = snapshot;
            }
        }

        switch (kind)
        {
            case VideoPageEvent.Page:
            case VideoPageEvent.FilePage:
                // Only the page the view was navigated to: a late "page" from the one just left would flush the load into it.
                bool arrived;
                lock (_gate)
                {
                    arrived = _watch.Arrived(kind == VideoPageEvent.FilePage);
                }

                if (arrived)
                {
                    KillTimer(_hwnd, PageTimer);
                    Flush();
                }

                break;
            case VideoPageEvent.State:
                ShowTitle(snapshot.Title);
                VideoWindow.Report(this, snapshot);
                break;
            case VideoPageEvent.NoPicture:
                ShowTitle(snapshot.Title);
                DiagnosticLog.Info("Video", VideoText.NoPictureLogLine(snapshot.FileName));
                VideoWindow.Report(this, snapshot);
                break;
            case VideoPageEvent.Error when asks:
                AskDisk(before, snapshot, filePath!);
                break;
            case VideoPageEvent.Error:
                DiagnosticLog.Info("Video", snapshot.IsFile
                    ? VideoText.FileRefusedLogLine(snapshot.FileName, snapshot.Error)
                    : VideoText.YouTubeRefusedLogLine(snapshot.VideoId, snapshot.Error));
                VideoWindow.Report(this, snapshot);
                break;
        }
    }

    // A file's error 2 or 4 worded by the disk (2026-11-13, the review's fix): gone, unreadable, or as the page said (VideoFile.GoneIfMissing).
    // The stat runs off the window's thread, which a dead share would hang, and its answer counts only while nothing newer replaced the
    // snapshot the error came on; VideoWindow.Report is safe from any thread.
    private void AskDisk(VideoSnapshot before, VideoSnapshot errored, string filePath)
    {
        _ = Task.Run(() =>
        {
            int code = VideoFile.GoneIfMissing(errored.Error ?? 4, filePath);
            VideoSnapshot? current = null;
            lock (_gate)
            {
                if (ReferenceEquals(_snapshot, before))
                {
                    _snapshot = current = errored with { Error = code };
                }
            }

            DiagnosticLog.Info("Video", VideoText.FileRefusedLogLine(errored.FileName, code));
            if (current is not null)
            {
                VideoWindow.Report(this, current);
            }
        });
    }

    private void OnPopup(string url)
    {
        if (!VideoPage.IsWebLink(url))
        {
            DiagnosticLog.Warn("Video", $"The video page asked to open {url}, which is not a web address; refused.");
            return;
        }

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
            _snapshot = snapshot = _snapshot with { State = VideoState.Failed, Failure = VideoText.Failed(step.ToString(), hr), Version = VideoWindow.NextVersion() };
        }

        VideoWindow.Report(this, snapshot);
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
