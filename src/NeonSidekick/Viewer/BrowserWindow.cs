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
/// class, title, place (<see cref="Position"/>) and profile (<see cref="BrowserPage.UserDataFolder"/>, kept between runs).
///
/// <para>Two uses. A web tool's bot check (<see cref="IBrowserWindow.SolveAsync"/>): the window opens on the address and comes to the
/// front, the user solves the check, and once the page has settled with no check showing (<see cref="BrowserWatch"/>) its DOM, the
/// address it ended on, its cookies (DevTools' <c>Network.getAllCookies</c>, for the two hosts) and, for a PDF, the page printed by
/// WebView2 itself go back; then the window closes. <c>/browser</c> (<see cref="IBrowserWindow.BrowseAsync"/>): the window is the
/// user's to browse — sign in somewhere, say — and when they close it, the cookies for every host it went to go to the web tools.
/// A check met while <c>/browser</c>'s window is open is solved in it, and it stays open.</para>
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

    /// <summary>The app's home (<c>Program</c>): the window's WebView2 profile lives in <c>&lt;home&gt;\webview2-browser</c>.</summary>
    public static string? Home { get; set; }

    /// <summary>The window as an <see cref="IBrowserWindow"/> (<c>Program</c> hands it to the app on Windows).</summary>
    public static IBrowserWindow Window { get; } = new WindowsBrowserWindow();

    /// <summary>The open window, else a new one started; throws <see cref="InvalidOperationException"/> with the reason when none can be had.</summary>
    internal static BrowserWindowThread Open()
    {
        lock (s_startGate)
        {
            lock (s_gate)
            {
                if (s_open is { Alive: true } open)
                {
                    return open;
                }
            }

            var identity = CheckRuntime();
            string home = Home ?? Path.Combine(Path.GetTempPath(), "NeonSidekick");
            var window = new BrowserWindowThread(BrowserPage.UserDataFolder(home), identity);
            window.Start();
            lock (s_gate)
            {
                s_open = window;
            }

            return window;
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
        public async Task<BotCheckResult> SolveAsync(BotCheckRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
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

        public async Task<BrowserVisit> BrowseAsync(Uri? url)
        {
            var window = await Task.Run(Open).ConfigureAwait(false);
            return await window.BrowseAsync(url).ConfigureAwait(false);
        }

        public bool Close() => BrowserWindow.Close();
    }
}

/// <summary>The Browser window and the thread that pumps its messages and owns its WebView2 (<see cref="BrowserWindow"/>).</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class BrowserWindowThread
{
    private const uint RunMessage = WmApp + 1;
    private static readonly IntPtr PollTimer = 1;
    private static readonly IntPtr HarvestTimer = 2;
    private static readonly IntPtr LingerTimer = 3;

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

    public BrowserWindowThread(string userDataFolder, BrowserIdentity identity)
    {
        _userDataFolder = userDataFolder;
        _identity = identity;
    }

    public bool Alive => _alive;

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

            _hwnd = CreateWindowExW(0, s_className, IntPtr.Zero, WsOverlappedWindow, CwUseDefault, CwUseDefault, CwUseDefault, CwUseDefault, IntPtr.Zero, IntPtr.Zero, instance, GCHandle.ToIntPtr(self));
            if (_hwnd == IntPtr.Zero)
            {
                _failure = $"CreateWindowExW failed ({Marshal.GetLastPInvokeError()})";
                return;
            }

            // Where it last closed first, so the size below is the DPI of the monitor it opens on.
            _chrome.Window = _hwnd;
            _chrome.RestorePosition(BrowserWindow.Position);
            _chrome.SizeForDpi(BrowserPage.DefaultWidth, BrowserPage.DefaultHeight);
            ApplyStyle();   // before it is shown: the bar is never light first
            ShowTitle();

            Directory.CreateDirectory(_userDataFolder);
            _host = new WebViewHost(_hwnd)
            {
                Created = OnCreated,
                Failed = OnFailed,
                Key = OnKey,
                NavigationDone = OnNavigationDone,
                TitleChanged = OnTitleChanged,
                SourceChanged = OnSourceChanged,
                DownloadStarting = OnDownloadStarting,
            };
            _host.Start(_userDataFolder, null, BrowserPage.Blank, WebViewProfile.Browser);
            DiagnosticLog.Info("Browser", "Browser window opened.");

            _started = true;
            _ready.Set();
            PostMessageW(_hwnd, RunMessage, IntPtr.Zero, IntPtr.Zero);   // what was asked for while it started

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
            DiagnosticLog.Error("Browser", "The Browser window's thread failed.", ex);
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

        SettleVisits(_failure is { } why ? BrowserVisit.Empty with { Failure = why } : BrowserVisit.Empty);
    }

    private IntPtr Handle(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case RunMessage:
                Flush();
                return IntPtr.Zero;
            case WmTimer when wParam == PollTimer:
                Tick();
                return IntPtr.Zero;
            case WmTimer when wParam == HarvestTimer:
                KillTimer(hwnd, HarvestTimer);
                EndVisit([]);
                return IntPtr.Zero;
            case WmTimer when wParam == LingerTimer:
                KillTimer(hwnd, LingerTimer);
                if (_job is null && _visited is null)
                {
                    PostMessageW(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                }

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
                if (StartHarvest())
                {
                    return IntPtr.Zero;   // the cookies first; the close comes again once they are read
                }

                TerminalHandoff.ReturnFocus(hwnd);
                _chrome.RememberPosition(BrowserWindow.Placed);
                if (_job is { } job)
                {
                    Finish(job, BotCheckResult.Ended(BotCheckEnd.GaveUp, job.Request.Url.AbsoluteUri), close: false);
                }

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
        if (_waitingUrl is { } url)
        {
            _waitingUrl = null;
            _host!.Navigate(url);
        }
    }

    private void Go(string url)
    {
        if (_created)
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

        DiagnosticLog.Info("Browser", $"The Browser window on {url?.AbsoluteUri ?? BrowserPage.Blank} for the user (/browser).");
        Go(url?.AbsoluteUri ?? BrowserPage.Blank);
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
        if (_job is { } job && BrowserPage.HostOf(_host?.Source) is not null)
        {
            job.Watch.Navigated(ok, status);
        }
    }

    private void OnTitleChanged(string title)
    {
        _pageTitle = title;
        ShowTitle();
    }

    private void OnSourceChanged(string source)
    {
        if (_visited is not null && BrowserPage.HostOf(source) is { } host)
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

    // A key the window has: Ctrl+W closes, F5 reloads, Alt+← goes back; everything else is the page's (Ctrl+C, Ctrl+V, Esc).
    private bool OnKey(int virtualKey)
    {
        bool control = GetKeyState(VkControl) < 0;
        bool alt = GetKeyState(VkMenu) < 0;
        switch (BrowserKeys.ActionFor(virtualKey, control, alt))
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
            default:
                return false;
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
            _chrome.ApplyCaption(ViewerStyle.For(NeonSidekick.UI.Theme.Current, _chrome.Themed()));
        }
    }
}
