using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.ViewerNative;
using static NeonSidekick.Viewer.WebViewNative;

namespace NeonSidekick.Viewer;

/// <summary>The step of <see cref="WebViewHost"/>'s chain that failed.</summary>
internal enum WebViewStep
{
    /// <summary><see cref="WebViewNative.LoaderFileName"/> is not beside the exe.</summary>
    Loader,
    Environment,
    Controller,
    View,
    Settings,
    VirtualHost,
    Subscribe,
    Navigate,
}

/// <summary>
/// One WebView2 inside a window of ours (2026-10-05, the video window): the asynchronous chain WebView2 is built by —
/// environment, then a controller parented to the window, then the view, its settings locked down
/// (<see cref="WebViewNative.LockDown"/>), the page's folder mapped to <see cref="VideoPage.HostName"/>, the three events
/// subscribed, and the page navigated to — and the calls the window makes on it afterwards (bounds, a moved parent, a web
/// message, a script, close).
///
/// <para>Thread: WebView2 belongs to the thread that made its environment, an STA thread pumping messages; every completion
/// and event arrives on it, from inside that thread's <c>GetMessageW</c>. So this class is made and used on the window's
/// own thread and nowhere else (each call checks); what another thread wants, it posts to the window.</para>
///
/// <para>Navigation: <c>NavigationStarting</c> sees top-level navigations only, and cancels each one off the page's host
/// (<see cref="VideoPage.IsOurs"/>): a script's <c>location.href</c>, a link out of the player. The embed's iframe and the
/// <c>iframe_api</c> script are not top-level (they would come through <c>FrameNavigationStarting</c>, not subscribed), so the
/// filter never gets in the player's way. A link the player opens in a new window (the YouTube logo, "Watch on YouTube")
/// becomes <see cref="Popup"/>, handled so no browser window of WebView2's opens; the caller gives it to the default browser.
/// No watch-page fallback for an embed YouTube refuses (the user's call): that would mean letting youtube.com, its consent and
/// sign-in pages through the filter.</para>
///
/// <para>Focus: nothing here moves focus (no <c>MoveFocus</c>); the window shows without activation, and the keyboard stays
/// with the terminal until the user clicks into the video (Phase 0: the foreground stayed on Windows Terminal throughout).</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WebViewHost
{
    private readonly nint _parent;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private string _siteFolder = "";
    private string _url = "";
    private nint _environment;
    private nint _controller;
    private nint _webView;
    private nint _webView3;
    private bool _closed;

    public WebViewHost(nint parent) => _parent = parent;

    /// <summary>The view is made, its page navigating.</summary>
    public Action? Created { get; set; }

    /// <summary>A string message from the page (<c>chrome.webview.postMessage</c>).</summary>
    public Action<string>? Message { get; set; }

    /// <summary>A new window the page asked for, its URL; already handled, so nothing opened.</summary>
    public Action<string>? Popup { get; set; }

    /// <summary>A top-level navigation off the page's host, cancelled.</summary>
    public Action<string>? Refused { get; set; }

    /// <summary>
    /// A key going down while the view has the keyboard, its virtual-key code: true when the window took it (the view then
    /// never sees it). Only accelerators come here — a key that types nothing, or one with Ctrl or Alt held, Escape always —
    /// so Space, the arrows and the letters stay the player's.
    /// </summary>
    public Func<int, bool>? Key { get; set; }

    /// <summary>The page entered (true) or left (false) full screen — the player's own button, a double-click on the video.</summary>
    public Action<bool>? FullScreen { get; set; }

    /// <summary>A step of the chain failed with its HRESULT; the host is closed.</summary>
    public Action<WebViewStep, int>? Failed { get; set; }

    public bool IsCreated => _webView != 0;

    /// <summary>
    /// Starts the chain: the environment over <paramref name="userDataFolder"/>, then everything else as WebView2 answers,
    /// ending on <see cref="Created"/> or <see cref="Failed"/>. Returns false when it failed at once (no loader, no runtime).
    /// </summary>
    public bool Start(string userDataFolder, string siteFolder, string url)
    {
        OnThread();
        _siteFolder = siteFolder;
        _url = url;
        nint done = WebViewHandler.Completed(IidEnvironmentCompleted, OnEnvironment);
        nint options = WebViewOptions.Create(WebViewOptions.BrowserArguments);
        try
        {
            int hr = CreateCoreWebView2EnvironmentWithOptions(null, userDataFolder, options, done);
            return hr >= 0 || Fail(WebViewStep.Environment, hr);
        }
        catch (DllNotFoundException)
        {
            return Fail(WebViewStep.Loader, unchecked((int)0x8007007E));   // HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND)
        }
        finally
        {
            WebViewOptions.Release(options);
            WebViewHandler.Release(done);
        }
    }

    /// <summary>Fits the view to the window's client area (on <c>WM_SIZE</c>).</summary>
    public void Resize()
    {
        OnThread();
        if (_controller != 0)
        {
            Rect client;
            GetClientRect(_parent, &client);
            PutBounds(_controller, client);
        }
    }

    /// <summary>Tells the view its window moved (on <c>WM_MOVE</c>), so its popups and IME follow.</summary>
    public void ParentMoved()
    {
        OnThread();
        if (_controller != 0)
        {
            NotifyParentWindowPositionChanged(_controller);
        }
    }

    /// <summary>Sends the page a message; it arrives as the parsed object (<c>e.data</c>). False before the view is made.</summary>
    public bool Post(string json)
    {
        OnThread();
        return _webView != 0 && PostWebMessageAsJson(_webView, json) >= 0;
    }

    /// <summary>Runs a script in the page; <paramref name="done"/> gets its result as JSON. False before the view is made.</summary>
    public bool RunScript(string script, Action<int, string?> done)
    {
        OnThread();
        if (_webView == 0)
        {
            return false;
        }

        nint handler = WebViewHandler.Script(IidExecuteScriptCompleted, done);
        try
        {
            return ExecuteScript(_webView, script, handler) >= 0;
        }
        finally
        {
            WebViewHandler.Release(handler);
        }
    }

    /// <summary>
    /// Closes the view and lets every interface go; a completion still on its way finds the host closed. With
    /// <paramref name="browserExited"/>, the environment is held through <c>ICoreWebView2Environment5</c> until the browser's
    /// processes have exited and let the user data folder go (<c>BrowserProcessExited</c>, which arrives on this thread while it
    /// keeps pumping messages); then it is released and the callback runs. That is the documented way to delete a user data
    /// folder: the probe's temp one stayed locked for seconds after a plain close. Where the event cannot be had (closed already,
    /// an older runtime), the callback runs at once.
    /// </summary>
    public void Close(Action? browserExited = null)
    {
        OnThread();
        if (_closed)
        {
            browserExited?.Invoke();
            return;
        }

        _closed = true;
        nint environment5 = 0;
        if (browserExited is not null && _environment != 0 && QueryInterface(_environment, IidEnvironment5, out environment5) >= 0)
        {
            bool fired = false;
            nint handler = WebViewHandler.Event(IidBrowserProcessExited, (_, _) =>
            {
                if (!fired)
                {
                    fired = true;
                    WebViewNative.Release(environment5);
                    browserExited();
                }
            });
            try
            {
                if (AddBrowserProcessExited(environment5, handler) < 0)
                {
                    WebViewNative.Release(environment5);
                    environment5 = 0;
                }
            }
            finally
            {
                WebViewHandler.Release(handler);
            }
        }

        if (_controller != 0)
        {
            ControllerClose(_controller);
        }

        WebViewNative.Release(_webView3);
        WebViewNative.Release(_webView);
        WebViewNative.Release(_controller);
        WebViewNative.Release(_environment);
        _webView3 = _webView = _controller = _environment = 0;
        if (browserExited is not null && environment5 == 0)
        {
            browserExited();
        }
    }

    private void OnEnvironment(int hr, nint environment)
    {
        if (_closed)
        {
            return;
        }

        if (hr < 0 || environment == 0)
        {
            Fail(WebViewStep.Environment, hr);
            return;
        }

        AddRef(environment);
        _environment = environment;
        nint done = WebViewHandler.Completed(IidControllerCompleted, OnController);
        try
        {
            int made = CreateController(environment, _parent, done);
            if (made < 0)
            {
                Fail(WebViewStep.Controller, made);
            }
        }
        finally
        {
            WebViewHandler.Release(done);
        }
    }

    private void OnController(int hr, nint controller)
    {
        if (_closed)
        {
            return;
        }

        if (hr < 0 || controller == 0)
        {
            Fail(WebViewStep.Controller, hr);
            return;
        }

        AddRef(controller);
        _controller = controller;
        Resize();
        int step = GetCoreWebView2(controller, out _webView);
        if (step < 0)
        {
            Fail(WebViewStep.View, step);
            return;
        }

        step = GetSettings(_webView, out nint settings);
        if (step >= 0)
        {
            step = LockDown(settings);
            WebViewNative.Release(settings);
        }

        if (step < 0)
        {
            Fail(WebViewStep.Settings, step);
            return;
        }

        step = QueryInterface(_webView, IidWebView2_3, out _webView3);
        step = step < 0 ? step : SetVirtualHostNameToFolderMapping(_webView3, VideoPage.HostName, _siteFolder, HostAccessDeny);
        if (step < 0)
        {
            Fail(WebViewStep.VirtualHost, step);
            return;
        }

        step = Subscribe(_webView, IidNavigationStarting, OnNavigationStarting, AddNavigationStarting);
        step = step < 0 ? step : Subscribe(_webView, IidWebMessageReceived, OnWebMessage, AddWebMessageReceived);
        step = step < 0 ? step : Subscribe(_webView, IidNewWindowRequested, OnNewWindow, AddNewWindowRequested);
        step = step < 0 ? step : Subscribe(_webView, IidContainsFullScreenElementChanged, OnFullScreen, AddContainsFullScreenElementChanged);
        step = step < 0 ? step : Subscribe(controller, IidAcceleratorKeyPressed, OnKey, AddAcceleratorKeyPressed);
        if (step < 0)
        {
            Fail(WebViewStep.Subscribe, step);
            return;
        }

        step = Navigate(_webView, _url);
        if (step < 0)
        {
            Fail(WebViewStep.Navigate, step);
            return;
        }

        Created?.Invoke();
    }

    private static int Subscribe(nint source, Guid iid, Action<nint, nint> invoke, Func<nint, nint, int> add)
    {
        nint handler = WebViewHandler.Event(iid, invoke);
        try
        {
            return add(source, handler);
        }
        finally
        {
            WebViewHandler.Release(handler);
        }
    }

    private void OnNavigationStarting(nint sender, nint args)
    {
        string? uri = NavigationUri(args);
        if (!VideoPage.IsOurs(uri))
        {
            CancelNavigation(args);
            DiagnosticLog.Info("Video", $"A navigation to {uri} was refused: the video window shows its own page only.");
            if (!_closed)
            {
                Refused?.Invoke(uri ?? "");
            }
        }
    }

    private void OnWebMessage(nint sender, nint args)
    {
        if (!_closed && WebMessageString(args) is { } text)
        {
            Message?.Invoke(text);
        }
    }

    private void OnKey(nint sender, nint args)
    {
        if (!_closed && KeyGoingDown(args) is { } key && Key?.Invoke(key) == true)
        {
            HandleKey(args);
        }
    }

    private void OnFullScreen(nint sender, nint args)
    {
        if (!_closed && _webView != 0)
        {
            FullScreen?.Invoke(ContainsFullScreenElement(_webView));
        }
    }

    /// <summary>Takes the page out of its own full screen (the window's Esc while the player fills it); nothing when it is not.</summary>
    public void LeavePageFullScreen() =>
        RunScript("if (document.fullscreenElement) { document.exitFullscreen(); }", (_, _) => { });

    private void OnNewWindow(nint sender, nint args)
    {
        string? uri = NewWindowUri(args);
        HandleNewWindow(args);
        if (!_closed && !string.IsNullOrEmpty(uri))
        {
            Popup?.Invoke(uri);
        }
    }

    private bool Fail(WebViewStep step, int hr)
    {
        DiagnosticLog.Warn("Video", $"WebView2 failed at {step} (0x{hr.ToString("X8", CultureInfo.InvariantCulture)}){(hr == EInvalidState ? ": its user data folder is in use by a browser started with other options" : "")}.");
        Close();
        Failed?.Invoke(step, hr);
        return false;
    }

    private void OnThread()
    {
        if (Environment.CurrentManagedThreadId != _thread)
        {
            throw new InvalidOperationException("A WebView2 is used from the thread that made it only.");
        }
    }

    // ── The smoke's probe ────────────────────────────────────────────────────

    private const string ProbeEcho = "neon";
    private static readonly Lock s_probeGate = new();
    private static IntPtr s_probeClass;
    private static ushort s_probeAtom;

    [ThreadStatic]
    private static ProbeRun? t_probe;

    /// <summary>
    /// <c>video:webview2</c>: the whole chain in the published binary against a hidden window — the runtime's version read,
    /// the environment, controller and view made over a temp user data folder of its own (never the user's: one folder, one
    /// browser, so a probe beside a running app would fail or join its browser), the page written and served from the virtual
    /// host, its first message received, an echo posted and answered (web messages both ways), a script's answer read (the
    /// third handler shape), and a script's <c>location.href</c> off the host refused. No network: the page speaks before it
    /// loads YouTube's script. Skipped (and passed) where no WebView2 Runtime is installed, which the app says in a sentence;
    /// a missing loader fails.
    /// </summary>
    public static (bool Ok, string Detail) Probe(TimeSpan timeout)
    {
        string? version;
        int hr;
        try
        {
            version = RuntimeVersion(out hr);
        }
        catch (DllNotFoundException)
        {
            return (false, LoaderFileName + " did not load");
        }

        if (version is null)
        {
            return hr == ENoRuntime
                ? (true, "skipped: no WebView2 Runtime installed")
                : (false, $"the runtime's version was not read (0x{hr.ToString("X8", CultureInfo.InvariantCulture)})");
        }

        string root = Path.Combine(Path.GetTempPath(), "NeonSidekick-webview2-" + Guid.NewGuid().ToString("N"));
        var result = (Ok: false, Detail: "the probe's thread did not finish");
        var thread = new Thread(() => result = ProbeOnThread(root, version, timeout)) { IsBackground = true, Name = "WebView2 probe" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(timeout + TimeSpan.FromSeconds(15));   // the run, then up to 10 s for the browser to exit
        DeleteProbeFolder(root);
        return result;
    }

    private static (bool Ok, string Detail) ProbeOnThread(string root, string version, TimeSpan timeout)
    {
        SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        int ole = OleInitialize(IntPtr.Zero);
        IntPtr hwnd = IntPtr.Zero;
        var run = new ProbeRun(version, timeout);
        t_probe = run;
        try
        {
            IntPtr instance = GetModuleHandle(IntPtr.Zero);
            if (!EnsureProbeClass(instance, out string? failure))
            {
                return (false, failure!);
            }

            hwnd = CreateWindowExW(0, s_probeClass, IntPtr.Zero, WsOverlappedWindow, 0, 0, 320, 180, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                return (false, $"CreateWindowExW failed ({Marshal.GetLastPInvokeError()})");
            }

            string site = Path.Combine(root, VideoPage.SiteFolderName);
            VideoPage.Write(site);
            run.Host = new WebViewHost(hwnd);
            run.Host.Message = run.OnMessage;
            run.Host.Refused = run.OnRefused;
            run.Host.Failed = (step, hr) => run.Finish(false, $"failed at {step} (0x{hr.ToString("X8", CultureInfo.InvariantCulture)})");
            SetTimer(hwnd, 1, 100, IntPtr.Zero);
            if (run.Host.Start(Path.Combine(root, VideoPage.FolderName), site, VideoPage.Url))
            {
                Pump(() => run.Done);
            }

            return (run.Ok, run.Detail);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (run.Host is { } host)
            {
                // The browser lets the temp profile go only after it exits; its event comes through this thread's queue.
                run.AwaitExit();
                host.Close(run.OnBrowserExited);
                Pump(() => run.Exited);
            }

            if (hwnd != IntPtr.Zero)
            {
                DestroyWindow(hwnd);
            }

            t_probe = null;
            if (ole >= 0)
            {
                OleUninitialize();
            }
        }
    }

    /// <summary>Pumps the thread's messages until <paramref name="done"/>; the probe's 100 ms timer keeps <c>GetMessageW</c> waking.</summary>
    private static void Pump(Func<bool> done)
    {
        Msg msg;
        while (!done() && GetMessageW(&msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    private static bool EnsureProbeClass(IntPtr instance, out string? failure)
    {
        failure = null;
        lock (s_probeGate)
        {
            if (s_probeAtom != 0)
            {
                return true;
            }

            if (s_probeClass == IntPtr.Zero)
            {
                s_probeClass = Marshal.StringToHGlobalUni("NeonSidekick.WebViewProbe");
            }

            var wc = new WndClassEx
            {
                cbSize = (uint)sizeof(WndClassEx),
                lpfnWndProc = &ProbeProcedure,
                hInstance = instance,
                lpszClassName = s_probeClass,
            };
            s_probeAtom = RegisterClassExW(&wc);
            if (s_probeAtom == 0)
            {
                failure = $"RegisterClassExW failed ({Marshal.GetLastPInvokeError()})";
                return false;
            }

            return true;
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr ProbeProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == WmTimer)
            {
                t_probe?.Tick();
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Video", "The WebView2 probe's window failed.", ex);
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void DeleteProbeFolder(string root)
    {
        // The browser's processes let the folder go a moment after the view closes.
        for (int attempt = 0; attempt < 20 && Directory.Exists(root); attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(250);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Info("Video", $"The WebView2 probe's temp folder {root} was not removed: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>The probe's steps, run on its thread as the page answers.</summary>
    private sealed class ProbeRun(string version, TimeSpan timeout)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Stopwatch? _exitClock;
        private long _pageAt = -1;
        private bool _echoed;
        private bool _scripted;

        public WebViewHost? Host { get; set; }

        public bool Done { get; private set; }

        public bool Ok { get; private set; }

        public string Detail { get; private set; } = "no answer";

        public void OnMessage(string text)
        {
            using var doc = JsonDocument.Parse(text);
            string? ev = doc.RootElement.TryGetProperty("ev", out var e) ? e.GetString() : null;
            if (ev == "page" && _pageAt < 0)
            {
                _pageAt = _clock.ElapsedMilliseconds;
                Host!.Post("{\"cmd\":\"echo\",\"value\":\"" + ProbeEcho + "\"}");
            }
            else if (ev == "echo" && !_echoed)
            {
                _echoed = doc.RootElement.TryGetProperty("value", out var v) && v.GetString() == ProbeEcho;
                if (!_echoed)
                {
                    Finish(false, "the echo came back wrong: " + text);
                    return;
                }

                Host!.RunScript("location.host", (hr, json) =>
                {
                    if (hr < 0 || json != "\"" + VideoPage.HostName + "\"")
                    {
                        Finish(false, $"the script answered {json} (0x{hr.ToString("X8", CultureInfo.InvariantCulture)})");
                        return;
                    }

                    _scripted = true;
                    Host!.RunScript("location.href = 'https://example.com/'; 'sent'", (_, _) => { });
                });
            }
        }

        public void OnRefused(string uri)
        {
            if (_scripted && uri.StartsWith("https://example.com", StringComparison.Ordinal))
            {
                Finish(true, $"runtime {version}; the page on {VideoPage.HostName} in {_pageAt.ToString(CultureInfo.InvariantCulture)} ms; web messages both ways, a script's answer, a navigation off the host refused");
            }
        }

        /// <summary>The browser has exited, or the wait for it gave up.</summary>
        public bool Exited { get; private set; }

        public void AwaitExit() => _exitClock = Stopwatch.StartNew();

        public void OnBrowserExited() => Exited = true;

        public void Tick()
        {
            if (!Done && _clock.Elapsed > timeout)
            {
                Finish(false, _pageAt < 0 ? $"the page did not speak within {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s"
                    : _echoed ? "the navigation off the host was not refused" : "the echo did not come back");
            }

            if (_exitClock is { Elapsed.TotalSeconds: > 10 } && !Exited)
            {
                DiagnosticLog.Info("Video", "The WebView2 probe's browser did not exit within 10 s of its close.");
                Exited = true;
            }
        }

        /// <summary>Records the outcome; the pump stops at its next message (the timer's within 100 ms), no quit message posted.</summary>
        public void Finish(bool ok, string detail)
        {
            if (Done)
            {
                return;
            }

            Ok = ok;
            Detail = detail;
            Done = true;
        }
    }
}
