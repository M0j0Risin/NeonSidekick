using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using NeonSidekick.Web;
using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The Browser window (2026-10-13, the user's ask: the YouTube work's WebView2 used to let a person past a bot check the headless
/// browser cannot clear): a real browser page in a window of the app's own, a plain Win32 window on its own thread hosting WebView2
/// (<see cref="WebViewHost"/> with <see cref="WebViewProfile.Browser"/>), the video window's twin and kept apart from it — its own
/// class, title, place (<see cref="Position"/>) and profile (<see cref="BrowserPage.UserDataFolder"/>, kept between runs; the loaded
/// app profile's own since later on 2026-10-13, <see cref="ProfileDirectory"/>).
///
/// <para>Two uses. A web tool's bot check (<see cref="IBrowserWindow.SolveAsync"/>): the window opens on the address and comes to the
/// front, the user solves the check, and once the page has settled with no check showing (<see cref="BrowserWatch"/>) its DOM, the
/// address it ended on, its cookies (DevTools' <c>Network.getAllCookies</c>, for the two hosts) and, for a PDF, the page printed by
/// WebView2 itself go back; then the window closes. <c>/browser</c> (<see cref="IBrowserWindow.BrowseAsync"/>): the window is the
/// user's to browse — sign in somewhere, say — and when they close it, the cookies for every host it went to go to the web tools.
/// A check met while <c>/browser</c>'s window is open is solved in it, and it stays open.</para>
///
/// <para>Its navigation bar (later the same day, the user's ask): back, forward, reload or stop, home, the address box with its padlock
/// and Go, and open in the default browser (<see cref="BrowserToolbar"/>, <c>BrowserWindow.Bar.cs</c>), in both uses. Typed words
/// search with the app's <c>Web search method</c> (<see cref="SearchUrl"/>); home is <c>Web browser home page</c> (<see cref="HomePage"/>).</para>
///
/// <para>One window per process. No process-start site: WebView2's runtime starts its own <c>msedgewebview2.exe</c> processes, as
/// for the video window. Windows-only, so <c>Program</c> reaches it behind <c>OperatingSystem.IsWindows()</c> and passes null
/// elsewhere.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class BrowserWindow
{
    private static readonly Lock s_gate = new();
    private static readonly Lock s_startGate = new();
    private static BrowserWindowThread? s_open;

    /// <summary>Where the window was when it last closed (<c>Program</c>: the profile's <c>BrowserWindowLeft</c> / <c>BrowserWindowTop</c>); null is Windows' own place.</summary>
    public static Func<(int X, int Y)?>? Position { get; set; }

    /// <summary>Told the window's corner as it closes, on its thread. It must not block.</summary>
    public static Action<int, int>? Placed { get; set; }

    /// <summary>
    /// The loaded app profile's folder (<c>Program</c>), read at each open: the window's WebView2 profile lives in
    /// <c>&lt;profile&gt;\webview2-browser</c> (the home's until later on 2026-10-13, the user's pick: each profile its own history,
    /// cookies, sign-ins and solved checks; the home's old folder is left as it was), its list of visits in
    /// <c>&lt;profile&gt;\configs\browser-history.json</c>. Null (the smoke, a test) is a temp folder.
    /// </summary>
    public static Func<string>? ProfileDirectory { get; set; }

    /// <summary>The setting <c>Web browser privacy</c> as it is now (<c>Program</c>), read when it acts; <c>clear-history</c> until it is set.</summary>
    public static Func<BrowserPrivacyKind>? Privacy { get; set; }

    /// <summary>The privacy in force now.</summary>
    internal static BrowserPrivacyKind PrivacyNow() => Privacy?.Invoke() ?? BrowserPrivacyKind.ClearHistory;

    private static string ProfileNow() => ProfileDirectory?.Invoke() ?? Path.Combine(Path.GetTempPath(), "NeonSidekick");

    /// <summary>Whether <paramref name="profile"/> is the loaded profile's folder.</summary>
    internal static bool IsLoaded(string profile) =>
        string.Equals(Path.GetFullPath(profile), Path.GetFullPath(ProfileNow()), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The results page for words typed in the address box (<c>Program</c>: <see cref="BrowserAddress.SearchUrl"/> over the settings in
    /// force, so a <c>Web search method</c> change counts at once); DuckDuckGo's until it is set. Called on the window's thread.
    /// </summary>
    public static Func<string, Uri> SearchUrl { get; set; } = BrowserAddress.DuckDuckGo;

    /// <summary>The setting <c>Web browser home page</c> as it is now (<c>Program</c>); null or empty is the blank page.</summary>
    public static Func<string?>? HomePage { get; set; }

    /// <summary>
    /// What the bar's open button hands the page to: the user's default browser (<see cref="Llm.PersonaFile.OpenInBrowser"/>, the
    /// counted process-start site, as <see cref="VideoWindow.OpenLink"/>), web addresses only. Called on the window's thread. Never follows
    /// <c>Web open links in</c> (later still on 2026-10-13): under <c>browser-window</c> it would hand the page back to this window.
    /// </summary>
    public static Action<string> OpenLink { get; set; } = Llm.PersonaFile.OpenInBrowser;

    /// <summary>
    /// What the open button hands a staged PDF's original to (2026-10-14, the PDF plan's gap 60): its own program, as <c>open</c> and
    /// <c>/open</c> do (<see cref="Llm.PersonaFile.OpenInEditor"/>, the same counted site). The staged address names no real host.
    /// </summary>
    public static Action<string> OpenFile { get; set; } = Llm.PersonaFile.OpenInEditor;

    /// <summary>The home page's address now (<see cref="BrowserAddress.Home"/>): the setting's page, else the blank page.</summary>
    public static string HomeAddress() => BrowserAddress.Home(HomePage?.Invoke())?.AbsoluteUri ?? BrowserPage.Blank;

    /// <summary>How long the Web UI's live view waits on the window for a picture or an input's answer (2026-10-17): DevTools calls have no timeout of their own.</summary>
    public static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The window as an <see cref="IBrowserWindow"/> (<c>Program</c> hands it to the app on Windows).</summary>
    public static IBrowserWindow Window { get; } = new WindowsBrowserWindow();

    /// <summary>The open window, else a new one started; throws <see cref="InvalidOperationException"/> with the reason when none can be had.</summary>
    internal static BrowserWindowThread Open() => Open(forClear: false).Window;

    /// <summary>
    /// The loaded profile's open window, else a new one started (<paramref name="forClear"/>: for a clear alone, hidden, no clear of its
    /// own as it opens), and whether it was started now. A window open on another profile's folder (a race with the profile switch,
    /// which closes it) is closed first, nothing handed over. Throws <see cref="InvalidOperationException"/> when none can be had.
    /// </summary>
    internal static (BrowserWindowThread Window, bool Started) Open(bool forClear)
    {
        lock (s_startGate)
        {
            string profile = ProfileNow();
            string folder = BrowserPage.UserDataFolder(profile);
            BrowserWindowThread? stale = null;
            lock (s_gate)
            {
                if (s_open is { Alive: true } open)
                {
                    if (string.Equals(open.UserDataFolder, folder, StringComparison.OrdinalIgnoreCase))
                    {
                        return (open, false);
                    }

                    stale = open;
                    s_open = null;
                }
            }

            stale?.Close();
            var identity = CheckRuntime();
            var window = new BrowserWindowThread(folder, identity, BrowserHistory.PathFor(profile), forClear, profile);
            window.Start();
            lock (s_gate)
            {
                s_open = window;
            }

            return (window, true);
        }
    }

    /// <summary>The window open now, never one started for the asking (the saved links' calls, later on 2026-10-13); null with none.</summary>
    private static BrowserWindowThread? OpenNow()
    {
        lock (s_gate)
        {
            return s_open is { Alive: true } open ? open : null;
        }
    }

    /// <summary>The open window closed, nothing handed over, and waited for briefly (the app's exit); true when one was open.</summary>
    public static bool Close()
    {
        BrowserWindowThread? open;
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
    /// The runtime, before a window is made for nothing, and the browser the window is as the web requests claim it: Edge at the
    /// runtime's major version (WebView2's user agent is Edge's).
    /// </summary>
    private static BrowserIdentity CheckRuntime()
    {
        string? version;
        int hr;
        try
        {
            version = WebViewNative.RuntimeVersion(out hr);
        }
        catch (DllNotFoundException)
        {
            throw new InvalidOperationException(BrowserText.NoLoader);
        }

        if (version is null)
        {
            throw new InvalidOperationException(hr == WebViewNative.ENoRuntime ? BrowserText.NoRuntime : BrowserText.Failed("runtime", hr));
        }

        return BrowserPage.IdentityOf(version);
    }

    private sealed class WindowsBrowserWindow : IBrowserWindow
    {
        // The bot checks under way (later still on 2026-10-13): counted here, around the whole solve, rather than read off the window
        // thread's job, so the gap between the call and the job's start counts too.
        private int _solving;

        // Why the runtime could not be had, once found: a runtime does not arrive mid-run, so it is not asked again.
        private string? _runtimeFailure;

        public async Task<BotCheckResult> SolveAsync(BotCheckRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            Interlocked.Increment(ref _solving);
            try
            {
                BrowserWindowThread window;
                try
                {
                    // Off the caller's thread: a new window's start waits up to 10 s for its thread.
                    window = await Task.Run(Open, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    return BotCheckResult.Ended(BotCheckEnd.Unavailable, request.Url.AbsoluteUri, ex.Message);
                }

                return await window.SolveAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _solving);
            }
        }

        public string? Unavailable()
        {
            if (Volatile.Read(ref _solving) > 0)
            {
                return BrowserText.BotCheckWaiting;
            }

            if (Volatile.Read(ref _runtimeFailure) is { } failure)
            {
                return failure;
            }

            if (OpenNow() is not null)
            {
                return null;
            }

            try
            {
                CheckRuntime();
                return null;
            }
            catch (InvalidOperationException ex)
            {
                Volatile.Write(ref _runtimeFailure, ex.Message);
                return ex.Message;
            }
        }

        public async Task<BrowserVisit> BrowseAsync(Uri? url)
        {
            var window = await Task.Run(Open).ConfigureAwait(false);
            return await window.BrowseAsync(url).ConfigureAwait(false);
        }

        public bool Close() => BrowserWindow.Close();

        public Task<(string Url, string? Title)?> CurrentPageAsync() =>
            OpenNow() is { } window ? window.CurrentPageAsync() : Task.FromResult<(string Url, string? Title)?>(null);

        public void LinksChanged() => OpenNow()?.LinksChanged();

        // The Web UI's live view (2026-10-17): the open window only, never one started for it; a view that does not answer in RemoteTimeout is none.
        public async Task<BrowserFrame?> FrameAsync(int maxWidth, CancellationToken cancellationToken)
        {
            if (OpenNow() is not { } window)
            {
                return null;
            }

            try
            {
                return await window.FrameAsync(maxWidth).WaitAsync(RemoteTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        public async Task<bool> InputAsync(BrowserInput input, CancellationToken cancellationToken)
        {
            if (OpenNow() is not { } window)
            {
                return false;
            }

            try
            {
                return await window.InputAsync(input).WaitAsync(RemoteTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public async Task<string?> ClearAsync(BrowserClear kind)
        {
            BrowserWindowThread window;
            bool started;
            try
            {
                (window, started) = await Task.Run(() => Open(forClear: true)).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // No window to clear Chromium's data with: the profile's list of visits goes all the same.
                string file = BrowserHistory.PathFor(ProfileNow());
                if (File.Exists(file))
                {
                    BrowserHistory.Delete(file);
                }

                return ex.Message;
            }

            return await window.ClearAsync(kind, closeAfter: started).ConfigureAwait(false);
        }
    }
}

/// <summary>The Browser window and the thread that pumps its messages and owns its WebView2 (<see cref="BrowserWindow"/>).</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class BrowserWindowThread
{
    private const uint RunMessage = WmApp + 1;
    private static readonly IntPtr PollTimer = 1;
    private static readonly IntPtr HarvestTimer = 2;
    private static readonly IntPtr LingerTimer = 3;
    private static readonly IntPtr HistorySaveTimer = 4;
    private static readonly IntPtr CloseClearTimer = 5;
    private static readonly IntPtr OpenClearTimer = 6;

    /// <summary>
    /// How long a bot check's window stays, hidden, after its check is through before it closes: a turn's next check (another site, a
    /// search after a fetch) reuses it rather than meeting a window on its way out.
    /// </summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(3);

    /// <summary>How long a <c>/browser</c> window's close waits for its cookies before it closes without them.</summary>
    public static readonly TimeSpan HarvestTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The most hosts a <c>/browser</c> visit hands cookies over for.</summary>
    public const int MaxVisitedHosts = 50;

    /// <summary>The brush behind the view before its page paints: white, as a browser's.</summary>
    private const int WhiteBrush = 0;

    private static readonly Lock s_classGate = new();
    private static IntPtr s_className;
    private static ushort s_atom;

    private readonly WindowChrome _chrome = new("browser window");
    private BrowserFullScreen _full;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Lock _gate = new();
    private readonly Queue<Action> _inbox = new();
    private readonly List<TaskCompletionSource<BotCheckResult>> _pendingSolves = [];
    private readonly List<TaskCompletionSource<BrowserVisit>> _pendingVisits = [];
    private readonly string _userDataFolder;
    private readonly BrowserIdentity _identity;
    private WebViewHost? _host;
    private bool _created;
    private string? _waitingUrl;
    private SolveJob? _job;
    private HashSet<string>? _visited;
    private bool _harvested;
    private string? _pageTitle;
    private string? _shownTitle;
    private Thread? _thread;
    private IntPtr _hwnd;
    private string? _failure;
    private bool _started;
    private bool _ole;
    private volatile bool _noHarvest;
    private volatile bool _alive = true;

    /// <param name="linksDirectory">The profile folder whose saved links the ☆ menu shows (later on 2026-10-13), and whose privacy the window keeps
    /// (2026-10-14); null: none (the smoke).</param>
    public BrowserWindowThread(string userDataFolder, BrowserIdentity identity, string? historyFile = null, bool forClear = false, string? linksDirectory = null)
    {
        _userDataFolder = userDataFolder;
        _identity = identity;
        _historyFile = historyFile;
        _forClear = forClear;
        _profile = linksDirectory;
        _privacy = BrowserWindow.PrivacyNow();
        _links = linksDirectory is null ? null : new BrowserLinks(linksDirectory);
    }

    public bool Alive => _alive;

    /// <summary>The WebView2 user data folder the window runs on: its profile's.</summary>
    public string UserDataFolder => _userDataFolder;

    /// <summary>The thread started and the window made (the WebView follows on its own); throws with the reason when it could not be.</summary>
    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Browser window" };
        _thread.SetApartmentState(ApartmentState.STA);   // WebView2 wants an STA that pumps messages
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)) || !_started)
        {
            throw new InvalidOperationException(_failure ?? "the Browser window did not start");
        }
    }

    /// <summary>See <see cref="IBrowserWindow.SolveAsync"/>.</summary>
    public Task<BotCheckResult> SolveAsync(BotCheckRequest request, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<BotCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pendingSolves.Add(done);
        }

        if (!Post(() => BeginSolve(request, done)))
        {
            Settle(done, BotCheckResult.Ended(BotCheckEnd.Unavailable, request.Url.AbsoluteUri, _failure ?? BrowserText.Closed));
        }

        // Not awaited here (the class is unsafe, which await is not allowed in): the registration goes with the answer.
        var cancel = cancellationToken.Register(() =>
        {
            if (!Post(() => CancelSolve(done)))
            {
                Settle(done, BotCheckResult.Ended(BotCheckEnd.GaveUp, request.Url.AbsoluteUri));
            }
        });
        _ = done.Task.ContinueWith(_ => cancel.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return done.Task;
    }

    /// <summary>See <see cref="IBrowserWindow.BrowseAsync"/>.</summary>
    public Task<BrowserVisit> BrowseAsync(Uri? url)
    {
        var done = new TaskCompletionSource<BrowserVisit>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pendingVisits.Add(done);
        }

        if (!Post(() => BeginBrowse(url)))
        {
            SettleVisits(BrowserVisit.Empty with { Failure = _failure ?? BrowserText.Closed });
        }

        return done.Task;
    }

    /// <summary>The window asked to close with nothing handed over (once it has started, if it is starting), and its thread waited for a moment.</summary>
    public void Close()
    {
        _noHarvest = true;
        _ready.Wait(TimeSpan.FromSeconds(10));
        if (_alive && PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero))
        {
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
    }

    // Work for the window's thread. Before the window is up there is no handle to post to: the queue waits for the first flush.
    private bool Post(Action work)
    {
        if (!_alive)
        {
            return false;
        }

        lock (_gate)
        {
            _inbox.Enqueue(work);
        }

        return !_ready.IsSet || PostMessageW(_hwnd, RunMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private void Settle(TaskCompletionSource<BotCheckResult> done, BotCheckResult result)
    {
        lock (_gate)
        {
            _pendingSolves.Remove(done);
        }

        done.TrySetResult(result);
    }

    private void SettleVisits(BrowserVisit visit)
    {
        TaskCompletionSource<BrowserVisit>[] waiting;
        lock (_gate)
        {
            waiting = [.. _pendingVisits];
            _pendingVisits.Clear();
        }

        foreach (var done in waiting)
        {
            done.TrySetResult(visit);
        }
    }

    // The window class, registered once per process: the procedure below, the app's icon, white behind the view.
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
                s_className = Marshal.StringToHGlobalUni("NeonSidekick.Browser");
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
                hbrBackground = GetStockObject(WhiteBrush),
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
            if (user != IntPtr.Zero && GCHandle.FromIntPtr(user).Target is BrowserWindowThread window)
            {
                return window.Handle(hwnd, message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // Nothing may cross back into user32: an exception here would take the process down.
            DiagnosticLog.Error("Browser", $"The Browser window failed on message 0x{message:X}.", ex);
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

            _hwnd = CreateWindowExW(0, s_className, IntPtr.Zero, WsOverlappedWindow | WsClipChildren, CwUseDefault, CwUseDefault, CwUseDefault, CwUseDefault, IntPtr.Zero, IntPtr.Zero, instance, GCHandle.ToIntPtr(self));
            if (_hwnd == IntPtr.Zero)
            {
                _failure = $"CreateWindowExW failed ({Marshal.GetLastPInvokeError()})";
                return;
            }

            // Where it last closed first, so the size below is the DPI of the monitor it opens on.
            _chrome.Window = _hwnd;
            _chrome.RestorePosition(BrowserWindow.Position);
            _chrome.SizeForDpi(BrowserPage.DefaultWidth, BrowserPage.DefaultHeight);
            MakeBar(instance);
            ApplyStyle();   // before it is shown: the bar is never light first
            ShowTitle();

            Directory.CreateDirectory(_userDataFolder);
            _menu = new ContextMenuWindow(_hwnd, MenuChosenMessage);
            LoadHistory();
            _host = new WebViewHost(_hwnd)
            {
                Created = OnCreated,
                Failed = OnFailed,
                Key = OnKey,
                FullScreen = OnPageFullScreen,
                NavigationDone = OnNavigationDone,
                TitleChanged = OnTitleChanged,
                SourceChanged = OnSourceChanged,
                DownloadStarting = OnDownloadStarting,
                HistoryChanged = ReadHistory,
                NavigationBegan = OnNavigationBegan,
                TopInset = _layout?.Height ?? 0,
            };
            // The staged PDFs' folder on its virtual host (2026-10-14, the PDF plan's phase 4a), made and mapped for every window, one
            // opened for a clear alone too (gap 62), so a /view a.pdf that finds this window open can be shown in it.
            string? staging = null;
            try
            {
                Directory.CreateDirectory(PdfView.Folder);
                staging = PdfView.Folder;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn("Browser", "The staged PDFs' folder could not be made; a PDF will open in its own program.", ex);
            }

            _host.Start(_userDataFolder, staging, BrowserPage.Blank, WebViewProfile.Browser);
            DiagnosticLog.Info("Browser", "Browser window opened.");

            _started = true;
            _ready.Set();
            PostMessageW(_hwnd, RunMessage, IntPtr.Zero, IntPtr.Zero);   // what was asked for while it started

            Msg msg;
            while (GetMessageW(&msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (PreKey(&msg))
                {
                    continue;
                }

                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
        catch (Exception ex)
        {
            _failure ??= $"{ex.GetType().Name}: {ex.Message}";
            DiagnosticLog.Error("Browser", "The Browser window's thread failed.", ex);
        }
        finally
        {
            _alive = false;
            _menu?.Close();
            _host?.Close();
            if (!_started && _hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
            }

            FreeBar();
            if (_ole)
            {
                OleUninitialize();
            }

            self.Free();
            _ready.Set();
            EndAll();
            if (_started)
            {
                DiagnosticLog.Info("Browser", "Browser window closed.");
            }
        }
    }

    // Whatever still waits on the window ends with it: a check given up (or why the window could not be had), a visit with nothing.
    private void EndAll()
    {
        TaskCompletionSource<BotCheckResult>[] solves;
        lock (_gate)
        {
            solves = [.. _pendingSolves];
            _pendingSolves.Clear();
            _inbox.Clear();
        }

        foreach (var done in solves)
        {
            done.TrySetResult(_failure is { } failure
                ? BotCheckResult.Ended(BotCheckEnd.Unavailable, "", failure)
                : BotCheckResult.Ended(BotCheckEnd.GaveUp, ""));
        }

        EndClears(_failure ?? BrowserText.Closed);
        EndPages();
        EndRemote();

        SettleVisits(_failure is { } why ? BrowserVisit.Empty with { Failure = why } : BrowserVisit.Empty);
    }

    private IntPtr Handle(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (HandleBar(hwnd, message, wParam, lParam, out IntPtr answered))
        {
            return answered;
        }

        switch (message)
        {
            case RunMessage:
                Flush();
                return IntPtr.Zero;
            case WmTimer when wParam == PollTimer:
                Tick();
                return IntPtr.Zero;
            case WmTimer when wParam == ReleaseTimer:
                ReleaseHeld(_job);
                return IntPtr.Zero;
            case WmTimer when wParam == HarvestTimer:
                KillTimer(hwnd, HarvestTimer);
                EndVisit([]);
                return IntPtr.Zero;
            case WmTimer when wParam == HistorySaveTimer:
                KillTimer(hwnd, HistorySaveTimer);
                SaveHistory();
                return IntPtr.Zero;
            case WmTimer when wParam == CloseClearTimer:
                EndCloseClear("did not answer in time");
                return IntPtr.Zero;
            case WmTimer when wParam == OpenClearTimer:
                EndOpenClear();
                return IntPtr.Zero;
            case MenuChosenMessage:
                RunMenuCommand((int)wParam);
                return IntPtr.Zero;
            case WmTimer when wParam == LingerTimer:
                KillTimer(hwnd, LingerTimer);
                if (_job is null && _visited is null)
                {
                    PostMessageW(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                }

                return IntPtr.Zero;
            case WmSize:
                _menu?.Close();
                LayOutBar();
                _host?.Resize();
                return IntPtr.Zero;
            case WmMove:
                _menu?.Close();
                _host?.ParentMoved();
                break;
            case WmActivate:
                if (((long)wParam & 0xFFFF) == 0)
                {
                    _menu?.Close();   // another window took the keyboard: the menu goes, as Windows' own does
                }

                ApplyStyle();   // a /theme change reaches the window the next time it is focused
                break;
            case WmClose:
                _menu?.Close();
                if (StartHarvest())
                {
                    return IntPtr.Zero;   // the cookies first; the close comes again once they are read
                }

                if (_job is { } job)
                {
                    Finish(job, BotCheckResult.Ended(BotCheckEnd.GaveUp, job.Request.Url.AbsoluteUri), close: false);
                }

                if (StartCloseClear())
                {
                    return IntPtr.Zero;   // then the history, under clear-history; the close comes again once it is cleared
                }

                TerminalHandoff.ReturnFocus(hwnd);
                _chrome.RememberPosition(BrowserWindow.Placed);
                KeepHistoryAtClose();
                _host?.Close();
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    // What other threads asked for, run here in their order.
    private void Flush()
    {
        while (true)
        {
            Action? work;
            lock (_gate)
            {
                if (!_inbox.TryDequeue(out work))
                {
                    return;
                }
            }

            work();
        }
    }

    private void OnCreated()
    {
        _created = true;
        if (!StartOpenClear())
        {
            BecomeUsable();
        }
    }

    // The view made and any clear as it opened done: the page asked for meanwhile, then the work that waited for a view.
    private void BecomeUsable()
    {
        if (_waitingUrl is { } url)
        {
            _waitingUrl = null;
            _host!.Navigate(url);
        }

        var waiting = _whenUsable.ToArray();
        _whenUsable.Clear();
        foreach (var work in waiting)
        {
            work();
        }
    }

    private void Go(string url)
    {
        if (Usable)
        {
            _host!.Navigate(url);
        }
        else
        {
            _waitingUrl = url;
        }
    }

    // The window up and in front: the user has to click in it.
    private void Show()
    {
        if (!IsWindowVisible(_hwnd))
        {
            ShowWindow(_hwnd, SwShow);
        }

        _chrome.BringForward();
    }

    // ── A bot check ──────────────────────────────────────────────────────────

    /// <summary>One bot check the window is on: the request, its answer to come, the watch and its clock, and what it found.</summary>
    private sealed class SolveJob(BotCheckRequest request, TaskCompletionSource<BotCheckResult> done, bool keepOpen)
    {
        public BotCheckRequest Request { get; } = request;
        public TaskCompletionSource<BotCheckResult> Done { get; } = done;
        public bool KeepOpen { get; } = keepOpen;
        public BrowserWatch Watch { get; } = new();
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public bool Busy;
        public bool Finishing;
        public bool PdfWritten;

        /// <summary>The visible part as the last look read it (the Web UI's live view, 2026-10-17); null before any.</summary>
        public BrowserViewport? Viewport;

        /// <summary>How many pixels a device-independent one came to in the last picture: the PC's display scale.</summary>
        public double PixelsPerDip = 1;

        /// <summary>Where the page's held button is down, in CSS pixels; null with none held.</summary>
        public (double X, double Y)? Held;
        public bool WasFile;
        public bool Truncated;
    }

    private void BeginSolve(BotCheckRequest request, TaskCompletionSource<BotCheckResult> done)
    {
        if (_job is { } older)
        {
            Finish(older, BotCheckResult.Ended(BotCheckEnd.GaveUp, older.Request.Url.AbsoluteUri), close: false);
        }

        KillTimer(_hwnd, LingerTimer);
        _job = new SolveJob(request, done, keepOpen: _visited is not null);
        DiagnosticLog.Info("Browser", $"A bot check on {request.Url.Host} for the user to solve ({request.Why}).");
        ShowTitle();
        Go(request.Url.AbsoluteUri);
        Show();
        SetTimer(_hwnd, PollTimer, (uint)DomSettle.PollInterval.TotalMilliseconds, IntPtr.Zero);
    }

    private void CancelSolve(TaskCompletionSource<BotCheckResult> done)
    {
        if (_job is { } job && job.Done == done)
        {
            Finish(job, BotCheckResult.Ended(BotCheckEnd.GaveUp, job.Request.Url.AbsoluteUri), close: !job.KeepOpen);
        }
        else
        {
            Settle(done, BotCheckResult.Ended(BotCheckEnd.GaveUp, ""));
        }
    }

    private void Finish(SolveJob job, BotCheckResult result, bool close)
    {
        if (_job == job)
        {
            _job = null;
            KillTimer(_hwnd, PollTimer);
        }

        ReleaseHeld(job);
        job.Finishing = true;
        Settle(job.Done, result);
        DiagnosticLog.Info("Browser", $"The bot check on {job.Request.Url.Host} ended: {result.End}{(result.Detail.Length > 0 ? " (" + result.Detail + ")" : "")}.");
        ShowTitle();
        if (close)
        {
            // Hidden at once, the terminal given the keyboard back; closed for good after Linger unless another check comes.
            TerminalHandoff.ReturnFocus(_hwnd);
            ShowWindow(_hwnd, SwHide);
            SetTimer(_hwnd, LingerTimer, (uint)Linger.TotalMilliseconds, IntPtr.Zero);
        }
    }

    // One look at the page while a check waits: its fingerprint, the DOM when that is due, then the watch's say.
    private void Tick()
    {
        if (_job is not { Busy: false, Finishing: false } job || _host is null || !job.Watch.Loaded)
        {
            return;
        }

        job.Busy = true;
        if (!_host.RunScript(HeadlessBrowser.PrintExpression, (hr, json) => OnPrint(job, hr, json)))
        {
            job.Busy = false;
        }
    }

    private void OnPrint(SolveJob job, int hr, string? json)
    {
        if (_job != job)
        {
            return;
        }

        if (hr < 0 || !BrowserAnswers.TryReadPrint(json, out string state, out var print))
        {
            job.Busy = false;   // mid-navigation: the next look tries again
            return;
        }

        if (job.Watch.WantsRead(job.Clock.Elapsed, print))
        {
            ReadDom(job, () => AfterRead(job, state, print));
        }
        else
        {
            AfterRead(job, state, print);
        }
    }

    private void AfterRead(SolveJob job, string state, (int, uint) print)
    {
        bool ready = job.Watch.Observe(job.Clock.Elapsed, state, print);
        if (ready && job.Watch.Stale(print))
        {
            ReadDom(job, () => Conclude(job));   // ready: the DOM as it is now, when the last read is older than the last change
        }
        else if (ready)
        {
            Conclude(job);
        }
        else
        {
            job.Busy = false;
        }
    }

    private void ReadDom(SolveJob job, Action then)
    {
        bool sent = _host!.RunScript(HeadlessBrowser.PageExpression, (hr, json) =>
        {
            if (_job != job)
            {
                return;
            }

            if (hr >= 0 && BrowserAnswers.TryReadPage(json, out string html))
            {
                if (html.Length > WebFetcher.MaxDownloadBytes)
                {
                    html = html[..WebFetcher.MaxDownloadBytes];
                    job.Truncated = true;
                }

                var url = Uri.TryCreate(_host.Source, UriKind.Absolute, out var at) && WebFetcher.IsHttp(at) ? at : job.Request.Url;
                job.Watch.Read(job.Clock.Elapsed, html, url);
            }

            then();
        });
        if (!sent)
        {
            job.Busy = false;
        }
    }

    private void Conclude(SolveJob job)
    {
        switch (job.Watch.Decide())
        {
            case BrowserWatchStep.Read:
                Complete(job);
                break;
            case BrowserWatchStep.Refused:
                string where = _host?.Source ?? job.Request.Url.AbsoluteUri;
                Finish(job, BotCheckResult.Ended(BotCheckEnd.Refused, where, job.Watch.LoadFailed ? BrowserText.DidNotLoad : "", job.Watch.Status), close: !job.KeepOpen);
                break;
            default:
                job.Busy = false;
                break;
        }
    }

    // Through: the PDF when one was asked for, then the cookies, then the answer.
    private void Complete(SolveJob job)
    {
        job.Finishing = true;
        string final = _host!.Source is { } source && Uri.TryCreate(source, UriKind.Absolute, out var at) && WebFetcher.IsHttp(at) ? at.AbsoluteUri : job.Request.Url.AbsoluteUri;
        if (job.Request.PdfPath is { } path && !job.WasFile)
        {
            bool started = _host.PrintToPdf(path, (hr, ok) =>
            {
                job.PdfWritten = hr >= 0 && ok && File.Exists(path);
                Harvest(job, final);
            });
            if (started)
            {
                return;
            }
        }

        Harvest(job, final);
    }

    private void Harvest(SolveJob job, string final)
    {
        var hosts = new[] { job.Request.Url.Host, BrowserPage.HostOf(final) ?? job.Request.Url.Host };
        void Done(IReadOnlyList<BrowserCookie> cookies) =>
            Finish(job, new BotCheckResult(BotCheckEnd.Cleared, job.WasFile ? null : job.Watch.Html, final, job.Watch.Status, cookies, _identity, "")
            {
                PdfWritten = job.PdfWritten,
                WasFile = job.WasFile,
                Truncated = job.Truncated,
            }, close: !job.KeepOpen);

        if (!_host!.CallDevTools("Network.getAllCookies", "{}", (hr, json) => Done(hr >= 0 ? BrowserAnswers.CookiesFor(json, hosts) : [])))
        {
            Done([]);
        }
    }

    // ── /browser ─────────────────────────────────────────────────────────────

    private void BeginBrowse(Uri? url)
    {
        _visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_job is { } job)
        {
            Finish(job, BotCheckResult.Ended(BotCheckEnd.GaveUp, job.Request.Url.AbsoluteUri), close: false);
        }

        string address = url?.AbsoluteUri ?? BrowserWindow.HomeAddress();
        DiagnosticLog.Info("Browser", $"The Browser window on {WebUi.WebUiAuth.Redacted(address)} for the user (/browser).");   // a Web UI sign-in link's token cut (later on 2026-10-15)
        Go(address);
        Show();
    }

    // A /browser visit's close: hidden, its cookies read, then closed for real. False when there is nothing to hand over.
    private bool StartHarvest()
    {
        if (_visited is null || _harvested)
        {
            return false;
        }

        _harvested = true;
        if (_noHarvest || _host is not { IsCreated: true } || _visited.Count == 0)
        {
            EndVisit([], close: false);
            return false;
        }

        TerminalHandoff.ReturnFocus(_hwnd);
        _chrome.RememberPosition(BrowserWindow.Placed);
        ShowWindow(_hwnd, SwHide);
        var hosts = _visited.Take(MaxVisitedHosts).ToArray();
        SetTimer(_hwnd, HarvestTimer, (uint)HarvestTimeout.TotalMilliseconds, IntPtr.Zero);
        if (!_host.CallDevTools("Network.getAllCookies", "{}", (hr, json) => EndVisit(hr >= 0 ? BrowserAnswers.CookiesFor(json, hosts) : [], close: true)))
        {
            EndVisit([], close: true);
        }

        return true;
    }

    private void EndVisit(IReadOnlyList<BrowserCookie> cookies, bool close = true)
    {
        KillTimer(_hwnd, HarvestTimer);
        var hosts = _visited is null ? [] : _visited.Take(MaxVisitedHosts).ToArray();
        SettleVisits(new BrowserVisit(cookies, hosts, _identity));
        if (close && _alive)
        {
            PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // ── Events ───────────────────────────────────────────────────────────────

    // A check's page completed. The blank page the view starts on is no answer: it may complete after the check began.
    private void OnNavigationDone(bool ok, int status)
    {
        SetBarState(_barState with { Loading = false });
        ReadHistory();
        if (ok)
        {
            RecordVisit();
            FillLinkName();
        }

        if (_job is { } job && BrowserPage.HostOf(_host?.Source) is not null)
        {
            job.Watch.Navigated(ok, status);
        }
    }

    private void OnTitleChanged(string title)
    {
        _pageTitle = title;
        RetitleVisit(title);
        ShowTitle();
    }

    private void OnNavigationBegan(string uri) => SetBarState(_barState with { Loading = true });

    private void OnSourceChanged(string source)
    {
        SetBarState(_barState with { Source = source });
        RefreshSaved();
        ShowAddress(source);
        // A staged PDF's host is no site (2026-10-14, the PDF plan's gap 59): no cookies to hand over for it.
        if (_visited is not null && !PdfView.IsOurs(source) && !WebUi.WebUiAuth.IsSignInUrl(source) && BrowserPage.HostOf(source) is { } host)
        {
            _visited.Add(host);
        }
    }

    // A file instead of a page: a check's is cancelled, the clearance's cookies being what the download wants; a /browser one goes ahead.
    private bool OnDownloadStarting(string? source)
    {
        if (_job is not { } job)
        {
            return false;
        }

        if (!job.Finishing)
        {
            job.WasFile = true;
            job.Busy = true;
            Complete(job);
        }

        return true;
    }

    // The window failed to start its browser: whatever waits learns why, and the window, which could only ever be blank, closes.
    private void OnFailed(WebViewStep step, int hr)
    {
        _failure = BrowserText.Failed(step.ToString(), hr);
        _harvested = true;
        if (_job is { } job)
        {
            Finish(job, BotCheckResult.Ended(BotCheckEnd.Unavailable, job.Request.Url.AbsoluteUri, _failure), close: false);
        }

        SettleVisits(BrowserVisit.Empty with { Failure = _failure });
        PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    // A key the window has (BrowserKeys): Ctrl+W closes, F5 or Ctrl+R reloads, Alt+← and Alt+→ move through the history, Ctrl+L,
    // Alt+D or F6 goes to the address box, Esc stops a load, Ctrl+H the history, Ctrl+D saves or unsaves the page, Ctrl+Shift+O the
    // saved links, F11 full screen and Esc out of it; everything else is the page's (Ctrl+C, Ctrl+V, Esc for its dialog). The box
    // and the menus' buttons are on the bar, which the page's own full screen puts aside: that is left first.
    private bool OnKey(int virtualKey)
    {
        bool control = GetKeyState(VkControl) < 0;
        bool alt = GetKeyState(VkMenu) < 0;
        bool shift = GetKeyState(VkShift) < 0;
        if (MenuKey(virtualKey))
        {
            return true;   // the history menu has the keys while it shows (from the page too); a chord goes on as usual
        }

        var action = BrowserKeys.ActionFor(virtualKey, control, alt, _barState.Loading, shift, _full.Covering);
        if (_full.Page && action is BrowserKeyAction.Address or BrowserKeyAction.History or BrowserKeyAction.Links)
        {
            ApplyFullScreen(_full with { Page = false });
        }

        switch (action)
        {
            case BrowserKeyAction.Close:
                PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                return true;
            case BrowserKeyAction.Reload:
                _host?.Reload();
                return true;
            case BrowserKeyAction.Back:
                _host?.GoBack();
                return true;
            case BrowserKeyAction.Forward:
                _host?.GoForward();
                return true;
            case BrowserKeyAction.Address:
                FocusAddress();
                return true;
            case BrowserKeyAction.Stop:
                _host?.Stop();
                return true;
            case BrowserKeyAction.History:
                OpenHistoryMenu(keyboard: true);
                return true;
            case BrowserKeyAction.SaveLink:
                ToggleSaved(_host?.Source, _host?.Title ?? _pageTitle);
                return true;
            case BrowserKeyAction.Links:
                OpenLinksMenu(keyboard: true);
                return true;
            case BrowserKeyAction.ToggleFullScreen:
                ApplyFullScreen(_full.Toggled());
                return true;
            case BrowserKeyAction.LeaveFullScreen:
                ApplyFullScreen(_full.Left());
                return true;
            default:
                return false;
        }
    }

    // The window's full screen made what next says: the page taken out of its own when that ends here (the window's key), the
    // state kept at once so the bar is back before the page's answer comes (it then finds nothing to change), the bar placed
    // again for the case the size stays (the page's full screen inside F11's).
    private void ApplyFullScreen(BrowserFullScreen next, bool leavePage = true)
    {
        if (leavePage && _full.Page && !next.Page)
        {
            _host?.LeavePageFullScreen();
        }

        _full = next;
        _chrome.SetFullScreen(next.Covering);
        LayOutBar();
    }

    // The page's own full screen (a video's button) takes the window with it, the bar put aside; F11's stays as it was.
    private void OnPageFullScreen(bool on)
    {
        if (on != _full.Page)
        {
            ApplyFullScreen(_full.PageChanged(on), leavePage: false);
        }
    }

    private void ShowTitle()
    {
        string title = BrowserText.Title(_pageTitle, _job is not null);
        if (title != _shownTitle && _hwnd != IntPtr.Zero)
        {
            _shownTitle = title;
            SetWindowTextW(_hwnd, title);
        }
    }

    private void ApplyStyle()
    {
        if (_hwnd != IntPtr.Zero)
        {
            var style = ViewerStyle.For(NeonSidekick.UI.Theme.Current, _chrome.Themed());
            _chrome.ApplyCaption(style);
            ApplyBarStyle(style);
        }
    }
}
