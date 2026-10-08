using System.Runtime.Versioning;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The video window on a Mac (2026-10-07, Stage 2: YouTube playback on macOS) — <see cref="VideoWindow"/>'s twin over the AppKit
/// host and WebKit (<see cref="MacWebKit"/>), the same <c>player.html</c> through <see cref="WebKitPage"/>'s bridge. One window per
/// process: a second <see cref="Play"/> switches the open one in place. It shows without taking the keyboard, opens where it last
/// closed (<see cref="Position"/>, the same <c>VideoWindowLeft</c>/<c>VideoWindowTop</c> as Windows'), wears the theme on its bar, and
/// closes with the app (<see cref="AppKitHost.AtEnd"/>: the media paused and the view let go while the loop still runs, so quitting
/// leaves no sound). <see cref="Player"/> is all of it as an <see cref="IVideoPlayer"/>; <c>Program</c> passes it on macOS 14 or later
/// with a window server (the data store needs 14).
///
/// <para>The rules are <see cref="VideoWindow"/>'s, kept here as they are there (its review's catches): <c>s_gate</c> guards only
/// which window is open and is never held while anything waits, so <see cref="Snapshot"/> and <see cref="Send"/> never block and never
/// wait on the main thread; <c>s_playGate</c> lines up the plays across a new window's start; versions come from one count for the
/// process (<see cref="NextVersion"/>), and a window another has replaced reports nothing more, its close included. Every WebKit call
/// is on the main thread: a play or a command off it queues under the window's own lock and posts the main thread, which flushes
/// once the page can hear. <see cref="Changed"/> is raised on the main thread (the voice pause's handler takes only its own lock).</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacVideoWindows
{
    private static readonly Lock s_gate = new();
    private static readonly Lock s_playGate = new();
    private static MacVideoWindow? s_open;
    private static long s_version;
    private static int s_registered;

    /// <summary>Where the window was when it last closed (<c>Program</c>: the profile's <c>VideoWindowLeft</c> / <c>VideoWindowTop</c>).</summary>
    public static Func<(int X, int Y)?>? Position { get; set; }

    /// <summary>Told the window's corner as it closes, on the main thread. It must not block.</summary>
    public static Action<int, int>? Placed { get; set; }

    /// <summary>The app's home (<c>Program</c>): the window's data store is the one named for it (<see cref="WebKitPage.StoreId"/>).</summary>
    public static string? Home { get; set; }

    /// <summary>
    /// Where a link the player opens in a new window goes (the YouTube logo, "Watch on YouTube"), on the main thread: the default
    /// browser through NSWorkspace (no process-start site of the app's).
    /// </summary>
    public static Action<string> OpenLink { get; set; } = static url =>
    {
        if (!MacWebKit.OpenInBrowser(url))
        {
            DiagnosticLog.Warn("Video", $"macOS did not open {url} in the browser.");
        }
    };

    /// <summary>The window as an <see cref="IVideoPlayer"/> (<c>Program</c> hands it to the app on a Mac).</summary>
    public static IVideoPlayer Player { get; } = new MacVideoPlayer();

    /// <summary>Whether a window can be had here: the AppKit host runs (a window server, the interactive screen) on macOS 14 or later.</summary>
    public static bool IsAvailable => AppKitHost.IsEnabled && OperatingSystem.IsMacOSVersionAtLeast(14);

    internal static event Action<VideoSnapshot?>? Changed;

    /// <summary>The window on <paramref name="request"/>'s video: the open one switched to it, else a new one. Throws with the reason when none can be had.</summary>
    public static void Play(VideoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (s_playGate)
        {
            MacVideoWindow? open;
            lock (s_gate)
            {
                open = s_open;
            }

            if (open is { Alive: true } && open.Play(request))
            {
                return;
            }

            if (!IsAvailable || !AppKitHost.IsRunning)
            {
                throw new InvalidOperationException(VideoText.UnavailableMac);
            }

            RegisterEnd();
            string home = Home ?? Path.Combine(Path.GetTempPath(), "NeonSidekick");
            var window = new MacVideoWindow(request, WebKitPage.StoreId(home));
            lock (s_gate)
            {
                s_open = window;
            }

            window.Start();   // outside s_gate: a snapshot read or a command never waits on a window starting
        }
    }

    /// <summary>The next report's <see cref="VideoSnapshot.Version"/>: one count for every window of the process.</summary>
    internal static long NextVersion() => Interlocked.Increment(ref s_version);

    public static bool Send(VideoCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (s_gate)
        {
            return s_open is { Alive: true } open && open.Send(VideoMessages.Command(command));
        }
    }

    /// <summary>What the open window last reported; null with none open, but a failed window's snapshot is kept until the next play.</summary>
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

    /// <summary>The open window closed and waited for (Esc, <c>youtube_control close</c>, the app's end); true when one was open.</summary>
    public static bool Close()
    {
        MacVideoWindow? open;
        lock (s_gate)
        {
            open = s_open;
            s_open = null;
        }

        bool alive = open is { Alive: true };
        if (alive)
        {
            AppKitHost.Invoke(() =>
            {
                open!.Close();
                return true;
            }, out _);
        }

        return alive;
    }

    /// <summary><paramref name="from"/>'s report (null as it closes) raised, unless another window has replaced it (<see cref="VideoWindow.Report"/>'s rule).</summary>
    internal static void Report(MacVideoWindow from, VideoSnapshot? snapshot)
    {
        bool current;
        lock (s_gate)
        {
            current = s_open == from || (snapshot is null && s_open is null or { Alive: false });
        }

        if (!current)
        {
            return;
        }

        try
        {
            Changed?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Video", "A video window listener failed.", ex);
        }
    }

    /// <summary>
    /// <c>video:webkit</c> (the smoke): a WKWebView made on the main thread over a store that keeps nothing, a tiny page loaded with the
    /// https base, its first message read back through the bridge and the script handler (the origin checked), a message sent in and
    /// echoed, a navigation off the host refused, all let go. No network, nothing shown. Skipped with no window server.
    /// </summary>
    public static (bool Ok, string Detail) Probe(TimeSpan timeout)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            return (true, "skipped: macOS 14 or later needed");
        }

        if (!AppKitHost.IsEnabled)
        {
            return (true, AppKitNative.HasWindowServer() ? "skipped: no AppKit host here (the app's main thread runs it)" : "skipped: no window server");
        }

        var sink = new ProbeSink();
        long started = Environment.TickCount64;
        if (!AppKitHost.Invoke(() => sink.Start(), out bool made) || !made)
        {
            return (false, "the main thread did not make the web view");
        }

        try
        {
            if (!sink.Page.Wait(timeout))
            {
                return (false, sink.Failure ?? "the page's first message did not come back through the script handler");
            }

            long pageMs = Environment.TickCount64 - started;
            AppKitHost.Post(() => MacWebKit.Run(sink.Web, WebKitPage.DeliverScript("{\"cmd\":\"echo\",\"value\":\"smoke\"}")));
            if (!sink.Echo.Wait(timeout))
            {
                return (false, sink.Failure ?? "the page did not answer the app's message");
            }

            if (!sink.Refused.Wait(timeout))
            {
                return (false, "the page's navigation off the host was not refused");
            }

            return sink.EchoOrigin == "https://" + VideoPage.HostName
                ? (true, $"WebKit: the page on {VideoPage.HostName} (a secure origin from the base URL) in {pageMs} ms; messages both ways through the bridge, a navigation off the host refused")
                : (false, $"the page's origin was {sink.EchoOrigin ?? "missing"}, not https://{VideoPage.HostName}");
        }
        finally
        {
            AppKitHost.Invoke(() =>
            {
                sink.Stop();
                return true;
            }, out _);
        }
    }

    // The app's end closes the window while the loop still runs: its media paused, its place kept.
    private static void RegisterEnd()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 0)
        {
            AppKitHost.AtEnd(() =>
            {
                MacVideoWindow? open;
                lock (s_gate)
                {
                    open = s_open;
                    s_open = null;
                }

                open?.Close();
            });
        }
    }

    private sealed class MacVideoPlayer : IVideoPlayer
    {
        public VideoSnapshot? Snapshot => MacVideoWindows.Snapshot;

        public event Action<VideoSnapshot?>? Changed
        {
            add => MacVideoWindows.Changed += value;
            remove => MacVideoWindows.Changed -= value;
        }

        public void Play(VideoRequest request) => MacVideoWindows.Play(request);

        public bool Send(VideoCommand command) => MacVideoWindows.Send(command);

        public bool Close() => MacVideoWindows.Close();
    }

    /// <summary>The smoke's page and what it heard; main thread but for the events.</summary>
    private sealed class ProbeSink : IWebPageSink
    {
        private const string Html =
            "<!doctype html><html><head><meta charset=\"utf-8\"></head><body><script>\n" +
            "chrome.webview.addEventListener('message', e => {\n" +
            "  if (e.data && e.data.cmd === 'echo') {\n" +
            "    chrome.webview.postMessage(JSON.stringify({ ev: 'echo', value: e.data.value, origin: location.origin }));\n" +
            "    location.href = 'https://elsewhere.neonsidekick.example/';\n" +
            "  }\n" +
            "});\n" +
            "chrome.webview.postMessage(JSON.stringify({ ev: 'page' }));\n" +
            "</script></body></html>";

        private nint _delegate;

        public nint Web { get; private set; }

        public ManualResetEventSlim Page { get; } = new();

        public ManualResetEventSlim Echo { get; } = new();

        public ManualResetEventSlim Refused { get; } = new();

        public string? EchoOrigin { get; private set; }

        public string? Failure { get; private set; }

        public bool Start()
        {
            _delegate = MacWebKit.NewDelegate(this);
            Web = MacWebKit.CreateWebView(64, 48, _delegate, storeId: null);
            MacWebKit.LoadPage(Web, Html);
            return true;
        }

        public void Stop()
        {
            MacWebKit.Release(Web, _delegate);
            Web = 0;
            _delegate = 0;
        }

        public void PageMessage(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                switch (root.TryGetProperty("ev", out var ev) ? ev.GetString() : null)
                {
                    case "page":
                        Page.Set();
                        break;
                    case "echo" when root.TryGetProperty("value", out var value) && value.GetString() == "smoke":
                        EchoOrigin = root.TryGetProperty("origin", out var origin) ? origin.GetString() : null;
                        Echo.Set();
                        break;
                }
            }
            catch (JsonException)
            {
                Failure = "the page sent something that is not JSON";
            }
        }

        public bool AllowNavigation(string? url, bool mainFrame)
        {
            if (mainFrame && !VideoPage.IsOurs(url))
            {
                Refused.Set();
                return false;
            }

            return true;
        }

        public void NewWindow(string? url)
        {
        }

        public void ContentEnded() => Failure = "the web content process ended";
    }
}

/// <summary>The video window on a Mac (<see cref="MacVideoWindows"/>): main thread but for <see cref="Play"/>, <see cref="Send"/> and <see cref="Snapshot"/>.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacVideoWindow : AppKitWindow, IWebPageSink
{
    /// <summary>The window's size in points before the user changes it: 16:9, Windows' 960 × 540.</summary>
    public const double DefaultWidth = 960;
    public const double DefaultHeight = 540;

    private readonly Lock _gate = new();
    private readonly Queue<string> _outbox = new();
    private readonly string _storeId;
    private VideoRequest? _pending;
    private VideoSnapshot _snapshot;
    private volatile bool _alive = true;

    // Main thread only.
    private nint _web;
    private nint _delegate;
    private bool _pageReady;
    private string? _shownTitle;
    private readonly VideoLoadSettle _settle = new();

    public MacVideoWindow(VideoRequest first, string storeId)
    {
        _pending = first;
        _snapshot = VideoSnapshot.Opening(first.VideoId) with { Version = MacVideoWindows.NextVersion() };
        _storeId = storeId;
    }

    /// <summary>Whether the window is open (starting counts): any thread.</summary>
    public new bool Alive => _alive;

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

    protected override Action<int, int>? Placed => MacVideoWindows.Placed;

    /// <summary>The window and its web view made on the main thread (waited for); throws with the reason when it could not be.</summary>
    public void Start()
    {
        Exception? failure = null;
        bool done = AppKitHost.Invoke(() =>
        {
            try
            {
                StartOnMain();
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
                Close();
                return false;
            }
        }, out bool started);
        if (!done || !started)
        {
            _alive = false;
            throw new InvalidOperationException(failure?.Message ?? "the window did not start");
        }
    }

    /// <summary>Switches the open window to <paramref name="request"/> (the newest wins); false when the window is gone.</summary>
    public bool Play(VideoRequest request)
    {
        lock (_gate)
        {
            _pending = request;
            _snapshot = VideoSnapshot.Opening(request.VideoId) with { Version = MacVideoWindows.NextVersion() };
        }

        return Post(() =>
        {
            Flush();
            if (SendBool(Window, Sel("isMiniaturized")) != 0)
            {
                SendVoid(Window, Sel("deminiaturize:"), 0);
            }

            ShowWindow(activate: false, raise: true);
        });
    }

    /// <summary>A command's message for the page, sent once the page can hear it; false when the window is gone.</summary>
    public bool Send(string json)
    {
        lock (_gate)
        {
            _outbox.Enqueue(json);
        }

        return Post(Flush);
    }

    private bool Post(Action work) => _alive && AppKitHost.Post(() =>
    {
        if (_alive && base.Alive)
        {
            work();
        }
    });

    private void StartOnMain()
    {
        Create(DefaultWidth, DefaultHeight, MacVideoWindows.Position);
        ApplyStyle();
        WithoutAnimation(() =>
        {
            nint black = CGColor(0);
            SendVoid(RootLayer, Sel("setBackgroundColor:"), black);
            CGColorRelease(black);
        });
        ShowTitle(null);

        var (width, height) = ClientSize;
        _delegate = MacWebKit.NewDelegate(this);
        _web = MacWebKit.CreateWebView(width, height, _delegate, _storeId);
        SendVoid(View, Sel("addSubview:"), _web);
        MacWebKit.LoadPage(_web, VideoPage.Text());
        ShowWindow(activate: false, raise: true);
        DiagnosticLog.Info("Video", "Video window opened.");
    }

    // What waits for the page, sent once it can hear it: the newest video first, then the commands in their order (VideoWindow's Flush).
    private void Flush()
    {
        if (!_pageReady || _web == 0)
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
            _settle.Loaded();
            MacWebKit.Run(_web, WebKitPage.DeliverScript(VideoMessages.Load(request)));
            ShowTitle(null);
        }

        foreach (string command in commands)
        {
            MacWebKit.Run(_web, WebKitPage.DeliverScript(command));
        }
    }

    public void PageMessage(string text)
    {
        VideoPageEvent kind;
        VideoSnapshot snapshot;
        lock (_gate)
        {
            (kind, snapshot) = VideoMessages.Apply(_snapshot, text);
            if (!_settle.Accept(kind, snapshot))
            {
                return;   // the old video stopping under the new one's id (VideoLoadSettle)
            }

            if (kind is VideoPageEvent.State or VideoPageEvent.Error)
            {
                snapshot = snapshot with { Version = MacVideoWindows.NextVersion() };
            }

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
                MacVideoWindows.Report(this, snapshot);
                break;
            case VideoPageEvent.Error:
                DiagnosticLog.Info("Video", $"YouTube refused video {snapshot.VideoId} in the player (error {snapshot.Error}).");
                MacVideoWindows.Report(this, snapshot);
                break;
        }
    }

    // Only the page's own host in the window's frame (Windows' NavigationStarting rule); the frames inside it go where they go.
    public bool AllowNavigation(string? url, bool mainFrame)
    {
        if (!mainFrame || VideoPage.IsOurs(url))
        {
            return true;
        }

        DiagnosticLog.Debug("Video", $"The video page's navigation to {url} was refused.");
        return false;
    }

    public void NewWindow(string? url)
    {
        if (!VideoPage.IsWebLink(url))
        {
            DiagnosticLog.Warn("Video", $"The video page asked to open {url}, which is not a web address; refused.");
            return;
        }

        try
        {
            MacVideoWindows.OpenLink(url!);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn("Video", $"Could not open {url} in the browser: {ex.Message}");
        }
    }

    // The page's process ended: the snapshot says so, and the window, which could only ever be blank, closes (Windows' OnFailed).
    public void ContentEnded()
    {
        VideoSnapshot snapshot;
        lock (_gate)
        {
            _snapshot = snapshot = _snapshot with { State = VideoState.Failed, Failure = VideoText.ContentEnded, Version = MacVideoWindows.NextVersion() };
        }

        DiagnosticLog.Warn("Video", "The video window's web content process ended.");
        MacVideoWindows.Report(this, snapshot);
        Close();
    }

    // Esc, F11 and the Mac's extras the window's; TAB and the chat's chords the terminal's; the rest the player's (Space, ←, →, K, M…).
    internal override bool KeyDown(MacKeyEvent key)
    {
        switch (MacKeys.VideoAction(key.KeyCode, key.Flags, FullScreen))
        {
            case VideoKeyAction.ToggleFullScreen:
                SetFullScreen(!FullScreen);
                return true;
            case VideoKeyAction.LeaveFullScreen:
                SetFullScreen(false);
                return true;
            case VideoKeyAction.Close:
                Close();
                return true;
            default:
                return TerminalHandoff.TakeMac(key);
        }
    }

    internal override void BecameKey() => ApplyStyle();   // a /theme change reaches the window the next time it is focused

    protected override void OnClosed()
    {
        _alive = false;
        nint web = _web, webDelegate = _delegate;
        _web = 0;
        _delegate = 0;
        MacWebKit.Release(web, webDelegate);
        MacVideoWindows.Report(this, null);
        DiagnosticLog.Info("Video", "Video window closed.");
    }

    private void ShowTitle(string? videoTitle)
    {
        string title = VideoText.Title(videoTitle);
        if (title != _shownTitle)
        {
            _shownTitle = title;
            SetTitle(title);
        }
    }

    private void ApplyStyle() => ApplyChrome(ViewerStyle.For(NeonSidekick.UI.Theme.Current, PictureWindow.Themed()));
}
