using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using NeonSidekick.Web;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The Browser window on a Mac (2026-10-18, the Mac Browser plan, the user's ask: full parity), <see cref="BrowserWindow"/>'s twin over the
/// AppKit host and WebKit (<see cref="MacBrowserKit"/>): one window per process, an accessory's as the app's other windows (no Dock icon, no
/// menu bar), on the loaded profile's own WebKit data store (<see cref="MacBrowserRules.StoreIdFileName"/>), every call posted to the main
/// thread and awaited off it, nothing waiting on the main thread. <see cref="Window"/> is all of it as an <see cref="IBrowserWindow"/>;
/// <c>Program</c> passes it on macOS 14 or later with a window server, and sets the statics below as it does Windows'.
/// No process-start site, no native library, no package: WebKit's XPC processes are WebKit's, links and files open through NSWorkspace.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacBrowserWindows
{
    private static readonly Lock s_gate = new();
    private static MacBrowserWindow? s_open;   // written on the main thread, read anywhere under s_gate
    private static BrowserIdentity? s_identity;
    private static int s_registered;
    private static int s_solving;

    /// <summary>Where the window was when it last closed (<c>Program</c>: the profile's <c>BrowserWindowLeft</c> / <c>BrowserWindowTop</c>).</summary>
    public static Func<(int X, int Y)?>? Position { get; set; }

    /// <summary>Told the window's corner as it closes, on the main thread. It must not block.</summary>
    public static Action<int, int>? Placed { get; set; }

    /// <summary>The loaded app profile's folder (<c>Program</c>), read at each open: the store id and the lock live there; null is a temp folder.</summary>
    public static Func<string>? ProfileDirectory { get; set; }

    /// <summary>The setting <c>Web browser privacy</c> as it is now (<c>Program</c>), read when it acts.</summary>
    public static Func<BrowserPrivacyKind>? Privacy { get; set; }

    /// <summary>The results page for words typed in the address box (<c>Program</c>: <see cref="BrowserAddress.SearchUrl"/> over the settings in force).</summary>
    public static Func<string, Uri> SearchUrl { get; set; } = BrowserAddress.DuckDuckGo;

    /// <summary>The setting <c>Web browser home page</c> as it is now (<c>Program</c>).</summary>
    public static Func<string?>? HomePage { get; set; }

    /// <summary>What the bar's open button hands a web page to: the default browser through NSWorkspace. Main thread.</summary>
    public static Action<string> OpenLink { get; set; } = static url =>
    {
        if (!MacWebKit.OpenInBrowser(url))
        {
            DiagnosticLog.Warn("Browser", $"macOS did not open {url} in the browser.");
        }
    };

    /// <summary>What the open button hands a staged PDF's original to: its own program through NSWorkspace (Preview, as a rule). Main thread.</summary>
    public static Action<string> OpenFile { get; set; } = static path =>
    {
        if (!MacBrowserKit.OpenFile(path))
        {
            DiagnosticLog.Warn("Browser", $"macOS did not open {path}.");
        }
    };

    /// <summary>How long the Web UI's live view, a clear and a store's removal wait on the main thread (Windows' <c>RemoteTimeout</c>).</summary>
    public static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a button the page pressed stays down without its release (Windows' <c>HoldLimit</c>: press-and-hold checks want up to about 10 s).</summary>
    public static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(30);

    /// <summary>The window as an <see cref="IBrowserWindow"/> (<c>Program</c> hands it to the app on a Mac).</summary>
    public static IBrowserWindow Window { get; } = new MacBrowserFacade();

    /// <summary>Whether a window can be had here: the AppKit host runs (a window server, the interactive screen) on macOS 14 or later.</summary>
    public static bool IsAvailable => AppKitHost.IsEnabled && OperatingSystem.IsMacOSVersionAtLeast(14);

    internal static BrowserPrivacyKind PrivacyNow() => Privacy?.Invoke() ?? BrowserPrivacyKind.ClearHistory;

    internal static string ProfileNow() => ProfileDirectory?.Invoke() ?? Path.Combine(Path.GetTempPath(), "NeonSidekick");

    internal static bool IsLoaded(string profile) =>
        string.Equals(Path.GetFullPath(profile), Path.GetFullPath(ProfileNow()), StringComparison.Ordinal);

    /// <summary>The home page's address now: the setting's page, else the blank page.</summary>
    public static string HomeAddress() => BrowserAddress.Home(HomePage?.Invoke())?.AbsoluteUri ?? BrowserPage.Blank;

    /// <summary>
    /// Safari as the window claims it (pass 5): the version from Safari's own bundle through <c>NSBundle</c> (a binary plist, never parsed as
    /// text), else <see cref="BrowserIdentity.SafariFallbackVersion"/>. Read once. Main thread.
    /// </summary>
    internal static BrowserIdentity Identity => s_identity ??= BrowserIdentity.Safari(SafariVersion());

    /// <summary>Safari's <c>CFBundleShortVersionString</c> through <c>NSBundle</c>; null without Safari. Any thread (Foundation's bundle reads are).</summary>
    public static string? SafariVersion()
    {
        nint pool = objc_autoreleasePoolPush();
        try
        {
            nint bundle = Send(Class("NSBundle"), Sel("bundleWithPath:"), NSString("/Applications/Safari.app"));
            nint info = bundle == 0 ? 0 : Send(bundle, Sel("infoDictionary"));
            return info == 0 ? null : FromNSString(Send(info, Sel("objectForKey:"), NSString("CFBundleShortVersionString")));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>The open window, never one started for the asking; null with none.</summary>
    internal static MacBrowserWindow? OpenNow()
    {
        lock (s_gate)
        {
            return s_open is { Live: true } open ? open : null;
        }
    }

    /// <summary>
    /// The loaded profile's open window, else a new one (main thread): a window open on another profile's store is closed first, nothing
    /// handed over. Throws <see cref="InvalidOperationException"/> with the reason when none can be had.
    /// </summary>
    internal static MacBrowserWindow Open()
    {
        if (!IsAvailable || !AppKitHost.OnMainThread)
        {
            throw new InvalidOperationException(BrowserText.MacUnavailable);
        }

        string profile = ProfileNow();
        if (OpenNow() is { } open)
        {
            if (string.Equals(open.Profile, profile, StringComparison.Ordinal))
            {
                return open;
            }

            open.CloseQuietly();
        }

        RegisterEnd();
        var window = MacBrowserWindow.Make(profile, Identity);
        lock (s_gate)
        {
            s_open = window;
        }

        return window;
    }

    /// <summary>The window let go of as it closes (main thread).</summary>
    internal static void Closed(MacBrowserWindow window)
    {
        lock (s_gate)
        {
            if (s_open == window)
            {
                s_open = null;
            }
        }
    }

    /// <summary>The open window closed, nothing handed over (the app's exit, a profile switch); true when one was open.</summary>
    public static bool Close()
    {
        var open = OpenNow();
        if (open is null)
        {
            return false;
        }

        AppKitHost.Invoke(() =>
        {
            open.CloseQuietly();
            return true;
        }, out _);
        return true;
    }

    // The app's end closes the window while the loop still runs (a backstop for Program's own Close).
    private static void RegisterEnd()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 0)
        {
            AppKitHost.AtEnd(() => OpenNow()?.CloseQuietly());
        }
    }

    /// <summary><paramref name="work"/> on the main thread with the window opened there; throws <see cref="InvalidOperationException"/> with the reason.</summary>
    private static async Task<MacBrowserWindow> OpenAsync() => await Task.Run(() =>
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(BrowserText.MacUnavailable);
        }

        if (!AppKitHost.Invoke(Open, out var window))
        {
            throw new InvalidOperationException(BrowserText.MacFailed);
        }

        return window;
    }).ConfigureAwait(false);

    /// <summary>A main-thread answer, awaited; <paramref name="fallback"/> when the loop is not running.</summary>
    internal static Task<T> OnMain<T>(Action<TaskCompletionSource<T>> start, T fallback)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!AppKitHost.Post(() =>
        {
            try
            {
                start(done);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DiagnosticLog.Error("Browser", "The Browser window's work failed on the main thread.", ex);
                done.TrySetResult(fallback);
            }
        }))
        {
            done.TrySetResult(fallback);
        }

        return done.Task;
    }

    private sealed class MacBrowserFacade : IBrowserWindow
    {
        public async Task<BotCheckResult> SolveAsync(BotCheckRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            Interlocked.Increment(ref s_solving);
            try
            {
                MacBrowserWindow window;
                try
                {
                    window = await OpenAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    return BotCheckResult.Ended(BotCheckEnd.Unavailable, request.Url.AbsoluteUri, ex.Message);
                }
                catch (OperationCanceledException)
                {
                    return BotCheckResult.Ended(BotCheckEnd.GaveUp, request.Url.AbsoluteUri);
                }

                return await window.SolveAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref s_solving);
            }
        }

        public string? Unavailable() =>
            Volatile.Read(ref s_solving) > 0 ? BrowserText.BotCheckWaiting
            : !IsAvailable ? BrowserText.MacUnavailable
            : null;

        public async Task<BrowserVisit> BrowseAsync(Uri? url)
        {
            var window = await OpenAsync().ConfigureAwait(false);
            return await window.BrowseAsync(url).ConfigureAwait(false);
        }

        public bool Close() => MacBrowserWindows.Close();

        // Ctrl+B (2026-10-18): asked on the AppKit thread, which owns the window's check; a check under way counts before it reaches the window.
        public BrowserCloseOutcome CloseAsUser()
        {
            if (Volatile.Read(ref s_solving) > 0)
            {
                return BrowserCloseOutcome.BotCheck;
            }

            return OpenNow() is { } open && AppKitHost.Invoke(open.CloseAsUser, out var outcome) ? outcome : BrowserCloseOutcome.None;
        }

        public Task<(string Url, string? Title)?> CurrentPageAsync() =>
            OpenNow() is { } window ? window.CurrentPageAsync() : Task.FromResult<(string Url, string? Title)?>(null);

        public void LinksChanged() => OpenNow()?.LinksChanged();

        public async Task<BrowserFrame?> FrameAsync(int maxWidth, CancellationToken cancellationToken)
        {
            if (OpenNow() is not { } window)
            {
                return null;
            }

            try
            {
                return await window.FrameAsync(maxWidth).WaitAsync(MacBrowserWindows.RemoteTimeout, cancellationToken).ConfigureAwait(false);
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
                return await window.InputAsync(input).WaitAsync(MacBrowserWindows.RemoteTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        // With the window open on the loaded profile, it clears (it knows whether a check waits); with none, the profile's store and its
        // list of visits are cleared here with no window at all (WebKit's store answers without a view; Windows starts a hidden window).
        public async Task<string?> ClearAsync(BrowserClear kind)
        {
            string profile = ProfileNow();
            if (OpenNow() is { } window && string.Equals(window.Profile, profile, StringComparison.Ordinal))
            {
                return await window.ClearAsync(kind).ConfigureAwait(false);
            }

            string file = BrowserHistory.PathFor(profile);
            if (File.Exists(file))
            {
                BrowserHistory.Delete(file);
            }

            if (kind != BrowserClear.All)
            {
                return null;
            }

            string? storeId = MacBrowserRules.ReadStoreId(Path.Combine(profile, MacBrowserRules.StoreIdFileName));
            if (storeId is null)
            {
                return null;   // never opened on this profile: nothing kept
            }

            var cleared = OnMain<string?>(done =>
            {
                nint store = MacWebKit.Store(storeId);
                MacBrowserKit.RemoveAll(store, () => done.TrySetResult(null));
            }, BrowserText.MacUnavailable);
            try
            {
                return await cleared.WaitAsync(MacBrowserWindows.RemoteTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return BrowserText.MacClearTimedOut;
            }
        }

        // /profile delete (pass 6): the profile's WebKit store, which lives outside its folder, removed by the id its folder names; a window
        // open on it (a race: only another profile than the loaded one is deleted) closed first.
        public async Task ForgetProfileAsync(string profileDirectory)
        {
            ArgumentNullException.ThrowIfNull(profileDirectory);
            try
            {
                string? storeId = MacBrowserRules.ReadStoreId(Path.Combine(profileDirectory, MacBrowserRules.StoreIdFileName));
                if (storeId is null || !IsAvailable)
                {
                    return;
                }

                var removed = OnMain<string?>(done =>
                {
                    if (OpenNow() is { } open && string.Equals(open.Profile, profileDirectory, StringComparison.Ordinal))
                    {
                        open.CloseQuietly();
                    }

                    MacBrowserKit.RemoveStore(storeId, why => done.TrySetResult(why));
                }, BrowserText.MacUnavailable);
                string? failure = await removed.WaitAsync(MacBrowserWindows.RemoteTimeout).ConfigureAwait(false);
                DiagnosticLog.Info("Browser", failure is null
                    ? $"The deleted profile's WebKit data store {storeId} removed."
                    : $"The deleted profile's WebKit data store {storeId} was not removed: {failure}");
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn("Browser", "The deleted profile's WebKit data store was not removed: " + ex.Message);
            }
        }
    }
}

/// <summary>
/// The Browser window on a Mac (<see cref="MacBrowserWindows"/>): an <see cref="AppKitWindow"/> holding the bar and a WKWebView, carrying out
/// what <see cref="BrowserJobs"/> decides. Main thread but for the <c>…Async</c> calls, which post there; nothing on it waits.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacBrowserWindow : AppKitWindow, IBrowserViewSink
{
    /// <summary>The window's size in points before the user changes it (Windows' 1100 × 800 at 96 DPI).</summary>
    public const double DefaultWidth = BrowserPage.DefaultWidth;
    public const double DefaultHeight = BrowserPage.DefaultHeight;

    // NSWindowCollectionBehaviorMoveToActiveSpace | FullScreenAuxiliary (pass 22): a check opened over a full-screen terminal comes to that
    // Space instead of sending the user to the desktop's. Not FullScreenNone beside them (AppKitWindow's default): with it the live check
    // (2026-10-18) slid the user off iTerm2's full-screen Space to show the window.
    private const ulong Behavior = (1UL << 1) | (1UL << 8);

    private readonly BrowserJobs _jobs = new();
    private readonly List<TaskCompletionSource<BotCheckResult>> _pendingSolves = [];
    private readonly List<TaskCompletionSource<BrowserVisit>> _pendingVisits = [];
    private readonly List<HeldHandler> _held = [];
    private readonly BrowserIdentity _identity;
    private readonly string? _storeId;
    private readonly MainTimer _poll;
    private readonly MainTimer _linger;
    private FileStream? _lock;
    private nint _web;
    private nint _delegate;
    private nint _store;
    private nint _activity;
    private BrowserFullScreen _full;
    private string? _pageTitle;
    private string? _shownTitle;
    private int _mainStatus;
    private string? _errorShownFor;
    private int _contentEnds;
    private bool _handOver = true;
    private bool _closedHere;
    // The staged PDFs this window showed, each file's URL as WebKit spells it → its https address (2026-10-18: the box, the history and
    // Open treat it as Windows' virtual host; the filter admits these files and no other).
    private readonly Dictionary<string, string> _stagedFiles = new(StringComparer.Ordinal);

    private MacBrowserWindow(string profile, BrowserIdentity identity, string? storeId)
    {
        Profile = profile;
        _identity = identity;
        _storeId = storeId;
        _poll = new MainTimer(Tick);
        _linger = new MainTimer(LingerOut);
        _historySave = new MainTimer(SaveHistory);
        _release = new MainTimer(() => ReleaseHeld(_jobs.Check));
        _downloadTimer = new MainTimer(DownloadTick);
        _links = new BrowserLinks(profile);
        _privacy = MacBrowserWindows.PrivacyNow();
        _historyFile = BrowserHistory.PathFor(profile);
    }

    /// <summary>The profile folder whose store the window runs on.</summary>
    public string Profile { get; }

    /// <summary>Whether the window is open (made and not yet closed): any thread.</summary>
    public bool Live => !_closedHere && _alive;

    private volatile bool _alive = true;

    protected override Action<int, int>? Placed => _probe ? null : MacBrowserWindows.Placed;

    /// <summary>The page's address as the window shows it: a staged PDF's https address for its file, else the view's own.</summary>
    private string? Source
    {
        get
        {
            string? url = _web == 0 ? null : MacBrowserKit.Url(_web);
            return url is not null && _stagedFiles.TryGetValue(url, out var staged) ? staged : url;
        }
    }

    /// <summary>
    /// The window for <paramref name="profile"/> made on the main thread: its lock taken (a second copy of the app is refused, pass 22), its
    /// store id read or made, the window, the bar and the view built, the list of visits read; nothing shown yet. Throws
    /// <see cref="InvalidOperationException"/> with the reason.
    /// </summary>
    public static MacBrowserWindow Make(string profile, BrowserIdentity identity)
    {
        FileStream? held = null;
        try
        {
            Directory.CreateDirectory(profile);
            held = new FileStream(Path.Combine(profile, MacBrowserRules.LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            held?.Dispose();
            throw new InvalidOperationException(BrowserText.MacProfileInUse);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(BrowserText.MacNoStore);
        }

        string? storeId = MacBrowserRules.StoreId(profile);
        if (storeId is null)
        {
            held.Dispose();
            throw new InvalidOperationException(BrowserText.MacNoStore);
        }

        var window = new MacBrowserWindow(profile, identity, storeId) { _lock = held };
        try
        {
            window.Build();
            return window;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Error("Browser", "The Browser window could not be made.", ex);
            window._handOver = false;
            window.Close();
            window._lock?.Dispose();
            throw new InvalidOperationException(ex is InvalidOperationException ? ex.Message : BrowserText.MacFailed);
        }
    }

    private void Build()
    {
        Create(DefaultWidth, DefaultHeight, _probe ? null : MacBrowserWindows.Position);
        SendVoidULong(Window, Sel("setCollectionBehavior:"), Behavior);
        _delegate = MacBrowserKit.NewDelegate(this);
        var (width, height) = ClientSize;
        _web = MacBrowserKit.CreateWebView(width, height, _delegate, _storeId, _identity.UserAgent);
        _store = Send(Send(Send(_web, Sel("configuration")), Sel("websiteDataStore")), Sel("retain"));
        SendVoidULong(_web, Sel("setAutoresizingMask:"), 0);   // laid out by hand with the bar (LayOut)
        SendVoid(View, Sel("addSubview:"), _web);
        MakeBar();
        LayOut();
        ApplyStyle();
        ShowTitle();
        LoadHistory();
        if (MacBrowserWindows.PrivacyNow() == BrowserPrivacyKind.ClearHistory && File.Exists(_historyFile))
        {
            BrowserHistory.Delete(_historyFile);   // the backstop: a close that never finished left keep-history's list
            _history = [];
        }

        DiagnosticLog.Info("Browser", "Browser window opened.");
    }

    // ── Calls from other threads ──────────────────────────────────────────

    /// <summary>See <see cref="IBrowserWindow.SolveAsync"/>.</summary>
    public Task<BotCheckResult> SolveAsync(BotCheckRequest request, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<BotCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingSolves)
        {
            _pendingSolves.Add(done);
        }

        if (!Post(() => BeginSolve(request, done)))
        {
            Settle(done, BotCheckResult.Ended(BotCheckEnd.Unavailable, request.Url.AbsoluteUri, BrowserText.Closed));
        }

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
        lock (_pendingVisits)
        {
            _pendingVisits.Add(done);
        }

        if (!Post(() => BeginBrowse(url)))
        {
            SettleVisits(BrowserVisit.Empty with { Failure = BrowserText.Closed });
        }

        return done.Task;
    }

    /// <summary>Work for the main thread while the window lives; false when it is gone.</summary>
    private bool Post(Action work) => Live && AppKitHost.Post(() =>
    {
        if (Live && Alive)
        {
            work();
        }
    });

    /// <summary>
    /// See <see cref="IBrowserWindow.CloseAsUser"/> (2026-10-18, Ctrl+B): the window's own close (<see cref="MacBrowserAction.Close"/>'s, so the
    /// visit's cookies are handed over), unless a check waits in it; a window hidden in a check's linger (<c>orderOut:</c>) is none. Main thread.
    /// </summary>
    internal BrowserCloseOutcome CloseAsUser()
    {
        if (!Live || !Alive)
        {
            return BrowserCloseOutcome.None;
        }

        if (_jobs.Check is not null)
        {
            return BrowserCloseOutcome.BotCheck;
        }

        if (SendBool(Window, Sel("isVisible")) == 0)
        {
            return BrowserCloseOutcome.None;
        }

        Close();
        return BrowserCloseOutcome.Closing;
    }

    /// <summary>Closed with nothing handed over (the app's exit, a profile switch, a stale profile's window). Main thread.</summary>
    public void CloseQuietly()
    {
        _handOver = false;
        Close();
    }

    private void Settle(TaskCompletionSource<BotCheckResult> done, BotCheckResult result)
    {
        lock (_pendingSolves)
        {
            _pendingSolves.Remove(done);
        }

        done.TrySetResult(result);
    }

    private void SettleVisits(BrowserVisit visit)
    {
        TaskCompletionSource<BrowserVisit>[] waiting;
        lock (_pendingVisits)
        {
            waiting = [.. _pendingVisits];
            _pendingVisits.Clear();
        }

        foreach (var done in waiting)
        {
            done.TrySetResult(visit);
        }
    }

    // ── Going places ──────────────────────────────────────────────────────

    /// <summary>
    /// The window on <paramref name="url"/>: a staged PDF loaded as its file (its folder alone readable), a web address or the blank page
    /// loaded, anything else refused (pass 27: an address WebKit cannot take is a refusal, never a nil request).
    /// </summary>
    private void Go(string url)
    {
        if (_web == 0)
        {
            return;
        }

        _errorShownFor = null;
        if (PdfView.IsOurs(url) && PdfView.StagedFileOf(url) is { } staged)
        {
            if (MacBrowserKit.FileUrl(staged) is { } fileUrl)
            {
                _stagedFiles[fileUrl] = url;
            }

            if (!MacBrowserKit.LoadFile(_web, staged))
            {
                DiagnosticLog.Warn("Browser", "The staged PDF could not be shown in the Browser window.");
            }

            return;
        }

        if (!BrowserPage.Allows(url) || !MacBrowserKit.Load(_web, url))
        {
            DiagnosticLog.Info("Browser", BrowserText.Refused(WebUi.WebUiAuth.Redacted(url)) + ".");
        }
    }

    /// <summary>Up, in front and with the keyboard (the app activated over the terminal), back from the Dock if it was there.</summary>
    private void Show()
    {
        if (SendBool(Window, Sel("isMiniaturized")) != 0)
        {
            SendVoid(Window, Sel("deminiaturize:"), 0);
        }

        // On the active Space before the app is activated, so the activation finds a window here and has no reason to switch Spaces.
        SendVoid(Window, Sel("orderFrontRegardless"));
        ShowWindow(activate: true);
        FocusPage();
    }

    /// <summary>Hidden (a check's end), the keyboard handed back to whoever had it when this window is the app's only one up.</summary>
    private void Hide()
    {
        bool key = SendBool(Window, Sel("isKeyWindow")) != 0;
        SendVoid(Window, Sel("orderOut:"), 0);
        if (key)
        {
            GiveBack();
        }
    }

    private void FocusPage()
    {
        if (_web != 0 && !EditingInBar())
        {
            SendVoid(Window, Sel("makeFirstResponder:"), _web);
        }
    }

    // ── /browser ──────────────────────────────────────────────────────────

    private void BeginBrowse(Uri? url)
    {
        if (_jobs.BeginVisit() is { } check)
        {
            Finish(check, BotCheckResult.Ended(BotCheckEnd.GaveUp, check.Request.Url.AbsoluteUri), close: false);
        }

        _linger.Stop();
        string address = url?.AbsoluteUri ?? MacBrowserWindows.HomeAddress();
        DiagnosticLog.Info("Browser", $"The Browser window on {WebUi.WebUiAuth.Redacted(address)} for the user (/browser).");
        Go(address);
        Show();
    }

    // ── The view's events ─────────────────────────────────────────────────

    public MacNavigationPolicy DecideAction(string? url, bool mainFrame, bool download)
    {
        if (!mainFrame)
        {
            return MacNavigationPolicy.Allow;   // frames go where they go, as on Windows
        }

        if (url is not null && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (_stagedFiles.ContainsKey(url))
            {
                return MacNavigationPolicy.Allow;   // the staged PDF the window loaded itself (pass 19)
            }

            DiagnosticLog.Info("Browser", "A page's navigation to a file on this Mac was refused.");
            return MacNavigationPolicy.Cancel;
        }

        if (!BrowserPage.Allows(url))
        {
            DiagnosticLog.Info("Browser", BrowserText.Refused(url ?? "(none)") + ".");
            return MacNavigationPolicy.Cancel;
        }

        return download ? DownloadOrFile() : MacNavigationPolicy.Allow;
    }

    public MacNavigationPolicy DecideResponse(string? url, bool mainFrame, int status, string? mime, bool canShow, bool attachment)
    {
        if (!mainFrame)
        {
            return canShow ? MacNavigationPolicy.Allow : MacNavigationPolicy.Cancel;
        }

        _mainStatus = status;
        if (attachment || !canShow)
        {
            return DownloadOrFile();
        }

        // During a check a main-frame answer that is no page (a PDF, a picture) ends it as a file (pass 7): WebKit draws it as a plug-in
        // document the watch cannot read, and the fetcher's retry with the cookies is what Windows does with such answers anyway.
        if (_jobs.Check is { Finishing: false } && !IsPage(mime) && !(url is not null && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)))
        {
            EndAsFile();
            return MacNavigationPolicy.Cancel;
        }

        return MacNavigationPolicy.Allow;
    }

    private static bool IsPage(string? mime) =>
        mime is null || mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mime.Contains("html", StringComparison.OrdinalIgnoreCase) || mime.Contains("xml", StringComparison.OrdinalIgnoreCase);

    // A file instead of a page: a check's ends as a file, its cookies what the caller wants; a /browser one is downloaded.
    private MacNavigationPolicy DownloadOrFile()
    {
        if (_jobs.Check is not null)
        {
            EndAsFile();
            return MacNavigationPolicy.Cancel;
        }

        return MacNavigationPolicy.Download;
    }

    private void EndAsFile()
    {
        if (_jobs.FileDuringCheck() && _jobs.Check is { } check)
        {
            Complete(check);
        }
    }

    public void Started()
    {
        _mainStatus = 0;
        RefreshBar();
    }

    public void Finished()
    {
        _contentEnds = 0;
        RefreshBar();
        string? source = Source;
        if (_errorShownFor is not null)
        {
            return;   // the window's own error page: no visit, no answer
        }

        RecordVisit();
        FillLinkName();
        FocusPage();
        if (_jobs.Check is { } check && BrowserPage.HostOf(source) is not null)
        {
            check.Watch.Navigated(true, _mainStatus);
        }
    }

    public void Failed(bool provisional, string? domain, long code, string message, string? failingUrl)
    {
        RefreshBar();
        if (MacBrowserRules.IsQuietFailure(domain, code))
        {
            return;
        }

        DiagnosticLog.Info("Browser", $"A page did not load ({domain} {code.ToString(System.Globalization.CultureInfo.InvariantCulture)}): {message}");
        if (_jobs.Check is { } check)
        {
            check.Watch.Navigated(false, 0);
        }

        // WebKit draws no page of its own (pass 17): the window's, at the failed address, so the box keeps it and Reload retries.
        string at = failingUrl ?? Source ?? BrowserPage.Blank;
        if (provisional && _web != 0 && BrowserPage.Allows(at))
        {
            _errorShownFor = at;
            MacBrowserKit.LoadHtml(_web, BrowserText.ErrorPage(WebUi.WebUiAuth.Redacted(at), message), at);
        }
    }

    public void Changed(string key)
    {
        switch (key)
        {
            case "title":
                _pageTitle = MacBrowserKit.Title(_web);
                if (_errorShownFor is null && !string.IsNullOrEmpty(_pageTitle))
                {
                    RetitleVisit(_pageTitle);
                }

                ShowTitle();
                break;
            case "URL":
                string? source = Source;
                if (_errorShownFor is not null && !string.Equals(source, _errorShownFor, StringComparison.Ordinal))
                {
                    _errorShownFor = null;   // on to another page
                }

                _jobs.Navigated(source);
                RefreshBar();
                RefreshSaved();
                break;
            case "fullscreenState":
                bool page = MacBrowserKit.FullScreenState(_web) is 1 or 2;
                if (page != _full.Page)
                {
                    _full = _full.PageChanged(page);
                }

                break;
            default:
                RefreshBar();
                break;
        }
    }

    public void NewWindow(string? url)
    {
        if (BrowserPage.Allows(url))
        {
            Go(url!);   // in place, as Windows does
        }
    }

    // The page's process ended: once, the page is loaded again; twice in a row, the window says so.
    public void ContentEnded()
    {
        _contentEnds++;
        DiagnosticLog.Warn("Browser", "The Browser window's web content process ended.");
        if (_contentEnds == 1 && _web != 0)
        {
            SendVoid(_web, Sel("reload"));
            return;
        }

        string at = Source ?? BrowserPage.Blank;
        _errorShownFor = at;
        if (_web != 0 && BrowserPage.Allows(at))
        {
            MacBrowserKit.LoadHtml(_web, BrowserText.ErrorPage(at, BrowserText.ContentEnded), at);
        }
    }

    // ── The window's own events ───────────────────────────────────────────

    internal override bool KeyDown(MacKeyEvent key)
    {
        if (key.KeyCode == MacKeys.KeyEscape && EditingIn(_address))
        {
            return false;   // the box's own: its address back, the keyboard to the page (the bar's target hears cancelOperation:)
        }

        var action = MacKeys.BrowserAction(key.KeyCode, key.Flags, key.Characters, _barState.Loading, _full.Window, _finding);
        if (action == MacBrowserAction.None)
        {
            return false;
        }

        if (_full.Page && action is MacBrowserAction.Address or MacBrowserAction.History or MacBrowserAction.Links or MacBrowserAction.Find)
        {
            LeavePageFullScreen();
        }

        return Act(action);
    }

    internal override bool OtherMouseDown(long button) => MacKeys.BrowserButton(button) is var action && action != MacBrowserAction.None && Act(action);

    private bool Act(MacBrowserAction action)
    {
        switch (action)
        {
            case MacBrowserAction.Close:
                Close();
                return true;
            case MacBrowserAction.Reload:
                Reload();
                return true;
            case MacBrowserAction.Stop:
                SendVoid(_web, Sel("stopLoading"));
                return true;
            case MacBrowserAction.Back:
                SendVoid(_web, Sel("goBack"));
                return true;
            case MacBrowserAction.Forward:
                SendVoid(_web, Sel("goForward"));
                return true;
            case MacBrowserAction.Address:
                FocusAddress();
                return true;
            case MacBrowserAction.Home:
                Go(MacBrowserWindows.HomeAddress());
                return true;
            case MacBrowserAction.History:
                OpenHistoryMenu();
                return true;
            case MacBrowserAction.SaveLink:
                ToggleSaved(Source, _pageTitle);
                return true;
            case MacBrowserAction.Links:
                OpenLinksMenu();
                return true;
            case MacBrowserAction.ToggleFullScreen:
                _full = _full.Window ? _full with { Window = false } : _full with { Window = true };
                SetFullScreen(_full.Window);
                LayOut();
                return true;
            case MacBrowserAction.LeaveFullScreen:
                _full = _full with { Window = false };
                SetFullScreen(false);
                LayOut();
                return true;
            case MacBrowserAction.Find:
                OpenFind();
                return true;
            case MacBrowserAction.FindNext:
                FindAgain(backwards: false);
                return true;
            case MacBrowserAction.FindPrevious:
                FindAgain(backwards: true);
                return true;
            case MacBrowserAction.CloseFind:
                CloseFind();
                return true;
            case MacBrowserAction.Print:
                PrintPage(null);
                return true;
            case MacBrowserAction.ZoomIn:
                Zoom(1);
                return true;
            case MacBrowserAction.ZoomOut:
                Zoom(-1);
                return true;
            case MacBrowserAction.ZoomReset:
                Zoom(0);
                return true;
            case MacBrowserAction.Copy:
                return Edit("copy:");
            case MacBrowserAction.Cut:
                return Edit("cut:");
            case MacBrowserAction.Paste:
                return Edit("paste:");
            case MacBrowserAction.SelectAll:
                return Edit("selectAll:");
            case MacBrowserAction.Undo:
                return Edit("undo:");
            case MacBrowserAction.Redo:
                return Edit("redo:");
            default:
                return false;
        }
    }

    // The editing keys (the app has no menu bar, so nothing routes them): sent to whoever has the keyboard — the address box, the find
    // field or the page's own field (Phase 0: copy and paste in a page's <input>).
    private static bool Edit(string selector)
    {
        Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("sendAction:to:from:"), Sel(selector), 0, 0);
        return true;
    }

    private void Reload()
    {
        if (_errorShownFor is { } failed)
        {
            _errorShownFor = null;
            Go(failed);
            return;
        }

        SendVoid(_web, Sel("reload"));
    }

    private void LeavePageFullScreen()
    {
        _full = _full with { Page = false };
        MacBrowserKit.Run(_web, "document.fullscreenElement && document.exitFullscreen && document.exitFullscreen();");
    }

    internal override void Resized() => LayOut();

    internal override void BecameKey() => ApplyStyle();   // a /theme change reaches the window the next time it is focused (pass 20)

    protected override void OnClosed()
    {
        _alive = false;
        _closedHere = true;
        MacBrowserWindows.Closed(this);
        _poll.Stop();
        _linger.Stop();
        CloseMenusAndSheets();

        // A check still on is given up; a visit's cookies are read from the store after the view goes (it answers without one).
        if (_jobs.Check is { } check)
        {
            Finish(check, BotCheckResult.Ended(BotCheckEnd.GaveUp, check.Request.Url.AbsoluteUri), close: false);
        }

        TaskCompletionSource<BotCheckResult>[] solves;
        lock (_pendingSolves)
        {
            solves = [.. _pendingSolves];
            _pendingSolves.Clear();
        }

        foreach (var done in solves)
        {
            done.TrySetResult(BotCheckResult.Ended(BotCheckEnd.GaveUp, ""));   // asked for, never begun: the window went first
        }

        var step = _jobs.Closing(_handOver, viewAlive: _web != 0);
        KeepHistoryAtClose();
        CancelDownloads();
        EndRemote();
        EndPages();
        EndClears(BrowserText.Closed);
        foreach (var handler in _held.ToArray())
        {
            handler.Fallback();
        }

        _held.Clear();
        EndActivity(force: true);
        nint web = _web, webDelegate = _delegate;
        _web = 0;
        _delegate = 0;
        FreeBar();
        MacBrowserKit.Release(web, webDelegate);

        nint store = _store;
        _store = 0;
        if (step == BrowserCloseStep.Harvest && store != 0)
        {
            Harvest(store);
        }
        else
        {
            if (store != 0)
            {
                SendVoid(store, Sel("release"));
            }

            SettleVisits(_jobs.Visiting ? _jobs.Visit([], _identity) : BrowserVisit.Empty);
        }

        _lock?.Dispose();
        _lock = null;
        DiagnosticLog.Info("Browser", "Browser window closed.");
    }

    // A visit's close (the user's): the store's cookies for the hosts it went to, within HarvestTimeout, then the visit settled.
    private void Harvest(nint store)
    {
        var hosts = _jobs.HarvestHosts;
        bool settled = false;
        void Done(IReadOnlyList<BrowserCookie> cookies)
        {
            if (settled)
            {
                return;
            }

            settled = true;
            SendVoid(store, Sel("release"));
            SettleVisits(_jobs.Visit(cookies, _identity));
        }

        var timeout = new MainTimer(() => Done([]));
        timeout.Start(BrowserJobs.HarvestTimeout);
        MacBrowserKit.StoreCookies(store, all =>
        {
            timeout.Stop();
            Done(all.Where(c => hosts.Any(c.AppliesTo)).ToList());
        });
    }

    private void ShowTitle()
    {
        string title = BrowserText.Title(_pageTitle, _jobs.Check is not null);
        if (title != _shownTitle && Alive)
        {
            _shownTitle = title;
            SetTitle(title);
        }
    }

    private void ApplyStyle()
    {
        var style = ViewerStyle.For(NeonSidekick.UI.Theme.Current, PictureWindow.Themed());
        ApplyChrome(style);
        ApplyBarStyle(style);
    }

    /// <summary>An NSProcessInfo activity held while a check runs or a download does (pass 28): the process is not napped meanwhile.</summary>
    private void BeginActivity()
    {
        if (_activity == 0)
        {
            nint info = Send(Class("NSProcessInfo"), Sel("processInfo"));
            _activity = Send(Send(info, Sel("beginActivityWithOptions:reason:"), unchecked((nint)0x00FFFFFFL), NSString("A bot check or a download in the Browser window")), Sel("retain"));
        }
    }

    private void EndActivity(bool force = false)
    {
        if (_activity != 0 && (force || (_jobs.Check is null && !AnyDownloadRunning)))
        {
            SendVoid(Send(Class("NSProcessInfo"), Sel("processInfo")), Sel("endActivity:"), _activity);
            SendVoid(_activity, Sel("release"));
            _activity = 0;
        }
    }
}
