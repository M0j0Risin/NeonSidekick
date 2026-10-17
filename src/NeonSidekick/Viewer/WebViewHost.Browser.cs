using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.ViewerNative;
using static NeonSidekick.Viewer.WebViewNative;

namespace NeonSidekick.Viewer;

// ── The smoke's Browser window probe (2026-10-13) ───────────────────────────

internal sealed unsafe partial class WebViewHost
{
    /// <summary>The address the probe sets a cookie for: the reserved <c>.example</c> name, never anyone's site, never asked for.</summary>
    private const string ProbeCookieUrl = "https://probe.neonsidekick.example/";

    /// <summary>A download started from the blank page: a blob of its own, so no network.</summary>
    private const string ProbeDownloadScript =
        "(() => { const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob(['neon'], { type: 'application/octet-stream' })); " +
        "a.download = 'probe.bin'; document.body.appendChild(a); a.click(); return 'sent'; })()";

    [ThreadStatic]
    private static BrowserProbeRun? t_browserProbe;

    /// <summary>
    /// <c>browser:webview2</c>: the Browser window's chain in the published binary against a hidden window and a temp user data folder of
    /// its own, with <see cref="WebViewProfile.Browser"/> — the blank page's navigation completed (the new event, its status read through
    /// the second arguments interface), its address read, its DOM read as the window reads a page, a cookie set and read back over the
    /// DevTools protocol with the DevTools window off (the window's cookie read, parsed as the web tools parse the headless browser's),
    /// the page printed by WebView2 (PrintToPdf, the fourth handler shape), a navigation to <c>file:</c> refused, and a download started
    /// and cancelled. Since the navigation bar (later the same day): the bar's address box (a stock EDIT child) and a tooltip made in the
    /// probe's window, the box's text set and read back, and a history entry (<c>location.hash</c>) seen through <c>HistoryChanged</c>,
    /// gone back from, and the way forward seen; then the history cleared through the profile (<c>ICoreWebView2_13</c>,
    /// <c>ICoreWebView2Profile2.ClearBrowsingData</c>, the fifth handler shape; later still). No network: every step stays on the blank page. Skipped (and passed) where no WebView2
    /// Runtime is installed; a missing loader fails. Since <c>/view a.pdf</c> (2026-10-14, the PDF plan's phase 4a, gap 87's
    /// <c>browser:pdf-view</c>): the PDF the probe printed is staged as <see cref="PdfView"/> stages one, in a folder mapped to
    /// <see cref="PdfView.HostName"/>, and opened last, its navigation completed and its document read as <c>application/pdf</c> — WebView2's
    /// own viewer showed it, where a policy that downloads PDFs instead would have started a download (gap 61).
    /// </summary>
    public static (bool Ok, string Detail) ProbeBrowser(TimeSpan timeout)
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

        string root = Path.Combine(Path.GetTempPath(), "NeonSidekick-webview2-browser-" + Guid.NewGuid().ToString("N"));
        var result = (Ok: false, Detail: "the probe's thread did not finish");
        var thread = new Thread(() => result = ProbeBrowserOnThread(root, version, timeout)) { IsBackground = true, Name = "WebView2 browser probe" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(timeout + TimeSpan.FromSeconds(15));
        DeleteProbeFolder(root);
        return result;
    }

    private static (bool Ok, string Detail) ProbeBrowserOnThread(string root, string version, TimeSpan timeout)
    {
        SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        int ole = OleInitialize(IntPtr.Zero);
        IntPtr hwnd = IntPtr.Zero;
        string site = Path.Combine(root, "pdfview");
        var run = new BrowserProbeRun(version, timeout, Path.Combine(root, "probe.pdf"), site);
        t_browserProbe = run;
        try
        {
            IntPtr instance = GetModuleHandle(IntPtr.Zero);
            if (!EnsureProbeClass(instance, out string? failure))
            {
                return (false, failure!);
            }

            hwnd = CreateWindowExW(0, s_probeClass, IntPtr.Zero, WsOverlappedWindow, 0, 0, 640, 480, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                return (false, $"CreateWindowExW failed ({Marshal.GetLastPInvokeError()})");
            }

            if (ProbeBar(hwnd, instance) is { } barFailure)
            {
                return (false, barFailure);
            }

            Directory.CreateDirectory(site);
            run.Host = new WebViewHost(hwnd)
            {
                NavigationDone = run.OnNavigationDone,
                HistoryChanged = run.OnHistoryChanged,
                Refused = run.OnRefused,
                DownloadStarting = run.OnDownloadStarting,
                Failed = (step, hr) => run.Finish(false, $"failed at {step} (0x{hr.ToString("X8", CultureInfo.InvariantCulture)})"),
            };
            SetTimer(hwnd, 1, 100, IntPtr.Zero);
            if (run.Host.Start(Path.Combine(root, BrowserPage.FolderName), site, BrowserPage.Blank, WebViewProfile.Browser))
            {
                PumpBrowserProbe(() => run.Done);
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
                run.AwaitExit();
                host.Close(run.OnBrowserExited);
                PumpBrowserProbe(() => run.Exited);
            }

            if (hwnd != IntPtr.Zero)
            {
                DestroyWindow(hwnd);
            }

            t_browserProbe = null;
            if (ole >= 0)
            {
                OleUninitialize();
            }
        }
    }

    // The bar's own natives in the probe's window: the address box made, its text set and read back, and a tooltip given a tool. Null when
    // all went well, else what did not.
    private static string? ProbeBar(IntPtr hwnd, IntPtr instance)
    {
        const string Text = "neon probe";
        IntPtr edit = BrowserBarNative.CreateWindowExW(0, "EDIT", null, BrowserBarNative.WsChild | WsVisible | BrowserBarNative.EsAutoHScroll, 0, 0, 200, 24, hwnd, (IntPtr)BrowserBarNative.AddressId, instance, IntPtr.Zero);
        if (edit == IntPtr.Zero)
        {
            return $"the address box (EDIT) was not made ({Marshal.GetLastPInvokeError()})";
        }

        SetWindowTextW(edit, Text);
        string read = BrowserBarNative.TextOf(edit);
        if (read != Text)
        {
            return $"the address box read back '{read}'";
        }

        IntPtr tooltip = BrowserBarNative.MakeTooltip(hwnd, instance);
        if (tooltip == IntPtr.Zero || !BrowserBarNative.AddTool(tooltip, hwnd, 1, new Rect { Right = 32, Bottom = 32 }, BrowserText.BackName))
        {
            return "the bar's tooltip (comctl32's tooltips_class32) was not made";
        }

        DestroyWindow(tooltip);
        DestroyWindow(edit);
        return null;
    }

    // The thread's messages pumped until done; the probe class's timer ticks the video probe's run, so this one's ticks are its own.
    private static void PumpBrowserProbe(Func<bool> done)
    {
        Msg msg;
        while (!done() && GetMessageW(&msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WmTimer)
            {
                t_browserProbe?.Tick();
            }

            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    /// <summary>The Browser probe's steps, run on its thread as WebView2 answers, each starting the next.</summary>
    private sealed class BrowserProbeRun(string version, TimeSpan timeout, string pdfPath, string site)
    {
        /// <summary>The staged copy's random folder: fixed here, the probe's site folder being its own.</summary>
        private const string StageName = "0123456789abcdef";

        private const string StagedFile = "probe page.pdf";   // a space, escaped in the address as a real name would be
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Stopwatch? _exitClock;
        private string _step = "the blank page's navigation";
        private bool _navigated;
        private bool _historyAsked;
        private bool _wentBack;
        private bool _cookieStarted;
        private bool _refused;
        private int _cookies;
        private long _pdfBytes;
        private int _frameBytes;
        private bool _viewPending;
        private bool _viewing;
        private bool _viewed;

        public WebViewHost? Host { get; set; }

        public bool Done { get; private set; }

        public bool Ok { get; private set; }

        public string Detail { get; private set; } = "no answer";

        public bool Exited { get; private set; }

        public void OnNavigationDone(bool ok, int status)
        {
            if (_viewing)
            {
                OnPdfViewed(ok, status);
                return;
            }

            if (_navigated || Done)
            {
                return;
            }

            _navigated = true;
            if (!ok)
            {
                Finish(false, "the blank page's navigation failed");
                return;
            }

            if (Host!.Source != BrowserPage.Blank)
            {
                Finish(false, $"the view's address read {Host.Source ?? "nothing"}");
                return;
            }

            _step = "the DOM read";
            Host.RunScript(Web.HeadlessBrowser.PageExpression, (hr, json) =>
            {
                if (hr < 0 || !BrowserAnswers.TryReadPage(json, out _))
                {
                    Finish(false, $"the DOM read answered {json} (0x{hr.ToString("X8", CultureInfo.InvariantCulture)})");
                    return;
                }

                MakeHistory();
            });
        }

        // A same-page history entry (no network, no new document): HistoryChanged says it can go back, back it goes, and then forward.
        private void MakeHistory()
        {
            _step = "the history";
            _historyAsked = true;
            Host!.RunScript("location.hash = 'neon'; 'sent'", (hr, _) =>
            {
                if (hr < 0)
                {
                    Finish(false, $"the history entry's script failed (0x{hr.ToString("X8", CultureInfo.InvariantCulture)})");
                }
            });
        }

        public void OnHistoryChanged()
        {
            if (!_historyAsked || Done || _cookieStarted)
            {
                return;
            }

            if (!_wentBack)
            {
                if (Host!.CanGoBack)
                {
                    _wentBack = true;
                    Host.GoBack();
                }

                return;
            }

            if (Host!.CanGoForward)
            {
                _cookieStarted = true;
                ClearHistory();
            }
        }

        private void ClearHistory()
        {
            _step = "the history's clear";
            if (!Host!.ClearBrowsingData(BrowserClear.History, hr =>
            {
                if (hr < 0)
                {
                    Finish(false, $"ClearBrowsingData answered 0x{hr.ToString("X8", CultureInfo.InvariantCulture)}");
                    return;
                }

                SetCookie();
            }))
            {
                Finish(false, "ClearBrowsingData could not start (no ICoreWebView2_13 or ICoreWebView2Profile2)");
            }
        }

        private void SetCookie()
        {
            _step = "the DevTools cookie";
            bool sent = Host!.CallDevTools("Network.setCookie", "{\"name\":\"neon\",\"value\":\"probe\",\"url\":\"" + ProbeCookieUrl + "\"}", (hr, json) =>
            {
                if (hr < 0)
                {
                    Finish(false, $"Network.setCookie failed (0x{hr.ToString("X8", CultureInfo.InvariantCulture)}): {json}");
                    return;
                }

                Host.CallDevTools("Network.getAllCookies", "{}", (got, cookies) =>
                {
                    var read = BrowserAnswers.CookiesFor(cookies, [new Uri(ProbeCookieUrl).Host]);
                    _cookies = read.Count;
                    if (got < 0 || read.Count != 1 || read[0].Value != "probe")
                    {
                        Finish(false, $"Network.getAllCookies gave {read.Count.ToString(CultureInfo.InvariantCulture)} of the probe's cookie (0x{got.ToString("X8", CultureInfo.InvariantCulture)})");
                        return;
                    }

                    Remote();
                });
            });
            if (!sent)
            {
                Finish(false, "CallDevToolsProtocolMethod was refused");
            }
        }

        // The Web UI's live view of a bot check (2026-10-17): the visible part read, a JPEG of it, and a mouse move sent, all over DevTools.
        private void Remote()
        {
            _step = "the live view";
            bool sent = Host!.CallDevTools("Page.getLayoutMetrics", "{}", (hr, json) =>
            {
                if (hr < 0 || !BrowserAnswers.TryReadViewport(json, out var viewport))
                {
                    Finish(false, $"Page.getLayoutMetrics gave no visible part (0x{hr.ToString("X8", CultureInfo.InvariantCulture)})");
                    return;
                }

                Host.CallDevTools("Page.captureScreenshot", BrowserRemote.ScreenshotParameters(viewport, 640, 1), (shot, picture) =>
                {
                    if (shot < 0 || !BrowserAnswers.TryReadScreenshot(picture, out byte[] jpeg) || BrowserRemote.JpegWidth(jpeg) is null)
                    {
                        Finish(false, $"Page.captureScreenshot gave no JPEG (0x{shot.ToString("X8", CultureInfo.InvariantCulture)})");
                        return;
                    }

                    _frameBytes = jpeg.Length;
                    var (method, move) = BrowserRemote.Commands(new BrowserInput(BrowserInputKind.Scroll, 0.5, 0.5, 0), viewport)[0];
                    Host.CallDevTools(method, move, (moved, _) =>
                    {
                        if (moved < 0)
                        {
                            Finish(false, $"Input.dispatchMouseEvent failed (0x{moved.ToString("X8", CultureInfo.InvariantCulture)})");
                            return;
                        }

                        Print();
                    });
                });
            });
            if (!sent)
            {
                Finish(false, "CallDevToolsProtocolMethod was refused");
            }
        }

        private void Print()
        {
            _step = "the PDF";
            bool started = Host!.PrintToPdf(pdfPath, (hr, ok) =>
            {
                try
                {
                    var file = new FileInfo(pdfPath);
                    _pdfBytes = file.Exists ? file.Length : 0;
                    using var stream = file.Exists ? file.OpenRead() : null;
                    var head = new byte[4];
                    bool pdf = stream is not null && stream.Read(head, 0, 4) == 4 && head[0] == '%' && head[1] == 'P' && head[2] == 'D' && head[3] == 'F';
                    if (hr < 0 || !ok || !pdf)
                    {
                        Finish(false, $"PrintToPdf answered {ok} (0x{hr.ToString("X8", CultureInfo.InvariantCulture)}), {(pdf ? "a PDF" : "no PDF")} written");
                        return;
                    }

                    stream!.Dispose();
                    string staged = Path.Combine(site, StageName);
                    Directory.CreateDirectory(staged);
                    File.Copy(pdfPath, Path.Combine(staged, StagedFile), overwrite: true);
                }
                catch (IOException ex)
                {
                    Finish(false, "the PDF could not be read: " + ex.Message);
                    return;
                }

                _step = "the file: navigation";
                Host.Navigate("file:///C:/");   // a page's own location.href to file: never reaches the filter: Chromium blocks it first
            });
            if (!started)
            {
                Finish(false, "PrintToPdf could not start (no ICoreWebView2_7)");
            }
        }

        public void OnRefused(string uri)
        {
            if (_refused || !uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _refused = true;
            _step = "the download";
            Host!.RunScript(ProbeDownloadScript, (_, _) => { });
        }

        public bool OnDownloadStarting(string? source)
        {
            if (_viewing)
            {
                // A policy that downloads PDFs (AlwaysOpenPdfExternally) rather than show them: /view a.pdf would download too.
                Finish(false, "the staged PDF was downloaded, not shown (a policy that downloads PDFs?)");
                return true;
            }

            // The PDF's view is started from the next tick, out of this handler.
            _step = "the staged PDF's view";
            _viewPending = true;
            return true;
        }

        private void ViewPdf()
        {
            _viewing = true;
            if (!Host!.Navigate(PdfView.UrlOf(StageName, StagedFile).AbsoluteUri))
            {
                Finish(false, "the staged PDF's navigation could not start");
            }
        }

        private void OnPdfViewed(bool ok, int status)
        {
            if (_viewed || Done)
            {
                return;
            }

            _viewed = true;
            if (!ok)
            {
                Finish(false, $"the staged PDF's navigation failed (status {status.ToString(CultureInfo.InvariantCulture)})");
                return;
            }

            if (!PdfView.IsOurs(Host!.Source))
            {
                Finish(false, $"the view's address read {Host.Source ?? "nothing"} after the staged PDF");
                return;
            }

            Host.RunScript("document.contentType", (hr, json) =>
            {
                if (hr < 0 || json != "\"application/pdf\"")
                {
                    Finish(false, $"the staged PDF's document read as {json ?? "nothing"} (0x{hr.ToString("X8", CultureInfo.InvariantCulture)})");
                    return;
                }

                Finish(true, $"runtime {version}; the bar's address box and a tooltip; the blank page's navigation and address, its DOM, a history entry gone back from and cleared, {_cookies.ToString(CultureInfo.InvariantCulture)} cookie set and read over DevTools, a picture of {_frameBytes.ToString(CultureInfo.InvariantCulture)} B and a mouse move over DevTools, a PDF of {_pdfBytes.ToString(CultureInfo.InvariantCulture)} B, a file: navigation refused, a download canceled, the PDF staged and shown on {PdfView.HostName}");
            });
        }

        public void AwaitExit() => _exitClock = Stopwatch.StartNew();

        public void OnBrowserExited() => Exited = true;

        public void Tick()
        {
            if (_viewPending && !Done)
            {
                _viewPending = false;
                ViewPdf();
            }

            if (!Done && _clock.Elapsed > timeout)
            {
                Finish(false, $"{_step} did not answer within {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s");
            }

            if (_exitClock is { Elapsed.TotalSeconds: > 10 } && !Exited)
            {
                DiagnosticLog.Info("Browser", "The WebView2 browser probe's browser did not exit within 10 s of its close.");
                Exited = true;
            }
        }

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
