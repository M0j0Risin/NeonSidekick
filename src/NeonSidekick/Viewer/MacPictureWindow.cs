using System.Collections.Concurrent;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The picture viewer on a Mac (2026-10-07, Stage 2: the app's own windows over AppKit) — <see cref="PictureWindow"/>'s public
/// calls land here when <see cref="OperatingSystem.IsMacOS"/>. One window per process, as on Windows: a second open points it at
/// the folder and brings it forward. Its decisions are the shared ones (<see cref="ViewerState"/>, <see cref="ViewerImage"/>'s
/// decode through ImageIO, <see cref="ViewerNav"/>'s arrows, <see cref="ViewerText"/>'s words), so this is only the window: the
/// picture a CGImage over the decode's own pixels (no copy) as the contents of a layer that scales it to fit on the GPU — sharp
/// on a Retina screen with no code of its own — and the arrows two layers whose opacity Core Animation fades. Every call from
/// the app goes through <see cref="AppKitHost"/> to the main thread. The picture menu, the drag out and the camera's live view
/// come in later rounds. Main thread only past the facade; excluded from coverage with the AppKit layer, proven by the smoke's
/// <c>viewer:window</c> and by hand.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacPictureWindows
{
    private static volatile MacPictureWindow? s_open;   // set on the main thread; read elsewhere only to choose a post over a wait
    private static int s_registered;

    /// <summary>The viewer on <paramref name="folder"/>, held on <paramref name="select"/>: opened, or the open one pointed at it. Throws when it could not be.</summary>
    public static void Open(string folder, string? select, bool activate, bool raise = true)
    {
        RegisterEnd();

        // An open window is only posted to, as Windows posts its message: the caller never waits on the main thread for it.
        if (s_open is { Alive: true } && AppKitHost.Post(() => OpenOnMain(folder, select, activate, raise)))
        {
            return;
        }

        if (!AppKitHost.Invoke(() => OpenOnMain(folder, select, activate, raise), out _))
        {
            throw new InvalidOperationException("the window did not start");
        }
    }

    private static bool OpenOnMain(string folder, string? select, bool activate, bool raise)
    {
        if (s_open is { Alive: true } open)
        {
            open.Retarget(folder, select, activate, raise);
            return true;
        }

        var window = new MacPictureWindow();
        window.Start(folder, select, activate);
        s_open = window;
        return true;
    }

    /// <summary>The open window moved to <paramref name="picture"/> quietly, only on its folder (<see cref="PictureWindow.Follow"/>).</summary>
    public static void Follow(string picture) => AppKitHost.Post(() => s_open?.FollowTo(picture));

    /// <summary><see cref="PictureWindow.ShowQuietly"/>: followed on the shown folder, pointed at another quietly, or opened quietly.</summary>
    public static void ShowQuietly(string picture, string folder)
    {
        RegisterEnd();
        if (s_open is { Alive: true } && AppKitHost.Post(() => ShowQuietlyOnMain(picture, folder)))
        {
            return;
        }

        if (!AppKitHost.Invoke(() => ShowQuietlyOnMain(picture, folder), out _))
        {
            throw new InvalidOperationException("the window did not start");
        }
    }

    private static bool ShowQuietlyOnMain(string picture, string folder)
    {
        if (s_open is { Alive: true } open)
        {
            if (string.Equals(open.Folder, folder, StringComparison.OrdinalIgnoreCase))
            {
                open.FollowTo(picture);
            }
            else
            {
                open.Retarget(folder, picture, activate: false, raise: false);
            }

            return true;
        }

        var window = new MacPictureWindow();
        window.Start(folder, picture, activate: false);
        s_open = window;
        return true;
    }

    /// <summary>The viewer closed; true when one was open.</summary>
    public static bool CloseViewer() =>
        AppKitHost.Invoke(() =>
        {
            var open = s_open;
            s_open = null;
            bool alive = open is { Alive: true };
            open?.Close();
            return alive;
        }, out bool closed) && closed;

    /// <summary>
    /// <c>viewer:window</c> on a Mac: the app's window and view classes made, a window of them created without being shown, a
    /// method of ours answering through <c>objc_msgSend</c>, a picture made a CGImage and set on its layer, the window closed and
    /// let go — on the main thread through the host, so the published exe's main-thread design is proven too. Nothing is shown.
    /// </summary>
    public static (bool Ok, string Detail) Probe()
    {
        if (!AppKitHost.IsEnabled)
        {
            return (true, AppKitNative.HasWindowServer() ? "skipped: no AppKit host here (the app's main thread runs it)" : "skipped: no window server");
        }

        if (!AppKitHost.Invoke(ProbeOnMain, out (bool Ok, string Detail) result))
        {
            return (false, "the main thread did not answer");
        }

        return result;
    }

    private static (bool Ok, string Detail) ProbeOnMain()
    {
        if (!AppKitHost.OnMainThread)
        {
            return (false, "the probe ran off the main thread");
        }

        nint window = SendInitWindow(Send(AppKitClasses.WindowClass, Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"), new CGRect(-10000, -10000, 64, 48), StyleStandard, BackingBuffered, 1);
        if (window == 0)
        {
            return (false, "NSWindow could not be made");
        }

        try
        {
            SendVoidBool(window, Sel("setReleasedWhenClosed:"), 0);
            long answer = (long)Send(window, Sel("neonProbe"));
            nint view = SendInitRect(Send(AppKitClasses.ViewClass, Sel("alloc")), Sel("initWithFrame:"), new CGRect(0, 0, 64, 48));
            SendVoidBool(view, Sel("setWantsLayer:"), 1);
            SendVoid(window, Sel("setContentView:"), view);
            SendVoid(view, Sel("release"));
            nint image = CGImage(new byte[2 * 2 * 4], 2, 2, BitmapBgrx);
            bool picture = image != 0;
            if (picture)
            {
                SendVoid(Send(view, Sel("layer")), Sel("setContents:"), image);
                CGImageRelease(image);
            }

            bool hidden = SendBool(window, Sel("isVisible")) == 0;
            SendVoid(window, Sel("close"));
            bool ok = answer == AppKitClasses.ProbeAnswer && picture && hidden;
            return (ok, $"AppKit on the main thread: a hidden window of the app's class answered 0x{answer:X}, a CGImage set on its layer{(hidden ? "" : ", but the window showed")}");
        }
        finally
        {
            SendVoid(window, Sel("release"));
        }
    }

    // The app's end closes the viewer while the loop still runs (its place kept).
    private static void RegisterEnd()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 0)
        {
            AppKitHost.AtEnd(() =>
            {
                var open = s_open;
                s_open = null;
                open?.Close();
            });
        }
    }
}

/// <summary>One Mac picture viewer window (<see cref="MacPictureWindows"/>); main thread only.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacPictureWindow : AppKitWindow
{
    /// <summary>The window's size in points before the user changes it (the Windows one's at 96 DPI).</summary>
    public const double DefaultWidth = 1024;
    public const double DefaultHeight = 768;

    private const double TextHeight = 22;

    private readonly ViewerState _state = new();
    private readonly ConcurrentQueue<Change> _changes = new();
    private readonly MainTimer _debounce;
    private readonly MainTimer _deleteArm;
    private readonly MainTimer _slides;
    private FileSystemWatcher? _watcher;
    private int _generation;
    private int _loadVersion;
    private CancellationTokenSource? _load;
    private nint _picture;   // the picture's layer
    private nint _text;      // the line with no picture
    private nint _newer;     // the arrows' layers
    private nint _older;
    private nint _image;     // the CGImage shown, retained here too
    private string? _unreadable;
    private ViewerStyle? _style;
    private (int Side, uint Fill, uint Ink, double Scale) _arrowKey;
    private bool _hover;
    private ViewerAction _hot;
    private int _wheel;

    private enum ChangeKind
    {
        Created,
        Deleted,
        Renamed,
        Changed,
    }

    private sealed record Change(int Generation, ChangeKind Kind, string Path, string? OldPath);

    public MacPictureWindow()
    {
        _debounce = new MainTimer(LoadCurrent);
        _deleteArm = new MainTimer(() =>
        {
            _state.Disarm();
            UpdateTitle();
        });
        _slides = new MainTimer(() =>
        {
            // A picture armed for deleting holds the show until it is deleted or disarmed.
            if (!_state.DeleteArmed && _state.NextSlide(Random.Shared))
            {
                UpdateTitle();
                LoadCurrent();
            }

            RestartSlides();
        });
    }

    /// <summary>The folder shown.</summary>
    public string Folder => _state.Folder;

    protected override Action<int, int>? Placed => PictureWindow.Placed;

    /// <summary>The window made and shown on <paramref name="folder"/>, held on <paramref name="select"/>.</summary>
    public void Start(string folder, string? select, bool activate)
    {
        Create(DefaultWidth, DefaultHeight, PictureWindow.Position);
        MakeLayers();
        ApplyStyle();   // before it is shown: the bar is never light first
        Show(folder);
        Select(select);
        ShowWindow(activate);
        DiagnosticLog.Info("Viewer", $"Picture viewer opened on {folder}.");
    }

    /// <summary>Pointed at <paramref name="folder"/> (held on <paramref name="select"/>) and brought forward, or shown quietly.</summary>
    public void Retarget(string folder, string? select, bool activate, bool raise)
    {
        if (!string.Equals(folder, _state.Folder, StringComparison.OrdinalIgnoreCase))
        {
            Show(folder);
        }

        // The same folder still moves to the clicked picture: a double-click on an older one jumps to it.
        Select(select);
        ApplyStyle();
        ShowWindow(activate, raise);
    }

    /// <summary>The strip's picture: on this folder only, never a retarget and never brought forward.</summary>
    public void FollowTo(string picture)
    {
        if (Alive && string.Equals(Path.GetDirectoryName(picture), _state.Folder, StringComparison.OrdinalIgnoreCase) && File.Exists(picture))
        {
            Select(picture);
            RestartSlides();
        }
    }

    protected override void OnClosed()
    {
        _watcher?.Dispose();
        _watcher = null;
        _load?.Cancel();
        _debounce.Stop();
        _deleteArm.Stop();
        _slides.Stop();
        if (_image != 0)
        {
            CGImageRelease(_image);
            _image = 0;
        }

        foreach (nint layer in (ReadOnlySpan<nint>)[_picture, _text, _newer, _older])
        {
            if (layer != 0)
            {
                SendVoid(layer, Sel("release"));
            }
        }

        _picture = _text = _newer = _older = 0;
        DiagnosticLog.Info("Viewer", "Picture viewer closed.");
    }

    // ---- keys and the mouse ----

    internal override bool KeyDown(MacKeyEvent key)
    {
        var action = MacKeys.ViewerAction(key.KeyCode, key.Flags, FullScreen, _state.SlideShow);
        if (action == ViewerAction.Menu)
        {
            action = ViewerAction.None;   // the picture menu comes in the next round
        }

        // Any key but Del disarms a first Del, mapped or not.
        if (action != ViewerAction.Delete && _state.Disarm())
        {
            _deleteArm.Stop();
            UpdateTitle();
        }

        if (action == ViewerAction.Delete && key.Repeat)
        {
            return true;   // a held Del is one press: the second must be a key of its own
        }

        if (action == ViewerAction.None)
        {
            return false;
        }

        Do(action);
        return true;
    }

    internal override void MouseDown(nint e)
    {
        var point = PointOf(e);
        var nav = NavAt(point);
        if (SendLong(e, Sel("clickCount")) >= 2)
        {
            // A quick second click on an arrow is another step, not full screen.
            if (nav != ViewerAction.None)
            {
                Step(nav);
            }
            else
            {
                Do(ViewerAction.ToggleFullScreen);
            }

            return;
        }

        if (nav != ViewerAction.None)
        {
            Step(nav);
        }
    }

    internal override void MouseEntered(nint e)
    {
        _hover = true;
        Hover(PointOf(e));
    }

    internal override void MouseMoved(nint e) => Hover(PointOf(e));

    internal override void MouseExited(nint e)
    {
        _hover = false;
        _hot = ViewerAction.None;
        UpdateArrows();
        SetHand(false);
    }

    // The wheel browses: a notch away newer, toward the user older, as the keys do. The trackpad's momentum after a flick is
    // not the user's: it would run on through the folder.
    internal override void ScrollWheel(nint e)
    {
        if (SendULong(e, Sel("momentumPhase")) != 0)
        {
            return;
        }

        double dy = SendDouble(e, Sel("scrollingDeltaY"));
        if (SendBool(e, Sel("isDirectionInvertedFromDevice")) != 0)
        {
            dy = -dy;   // natural scrolling: the hand's direction, as the wheel's notch is on Windows
        }

        bool precise = SendBool(e, Sel("hasPreciseScrollingDeltas")) != 0;
        int steps = ViewerState.WheelSteps(ref _wheel, (int)Math.Round(dy * (precise ? 3 : 120)));
        if (steps != 0)
        {
            WheelBrowse(steps);
        }
    }

    internal override void Resized() => Layout();

    internal override void BecameKey() => ApplyStyle();   // a /theme change reaches an open window the next time it is focused

    internal override void ScaleChanged() => Layout();

    // ---- what the window does ----

    private void Do(ViewerAction action)
    {
        switch (action)
        {
            case ViewerAction.ToggleFullScreen:
                SetFullScreen(!FullScreen);
                Layout();
                break;
            case ViewerAction.LeaveFullScreen:
                SetFullScreen(false);
                Layout();
                break;
            case ViewerAction.Close:
                Close();
                break;
            case ViewerAction.Delete:
                DeleteShown();
                break;
            case ViewerAction.ToggleSlideShow or ViewerAction.StopSlideShow or ViewerAction.ToggleShuffle or ViewerAction.LongerSlides or ViewerAction.ShorterSlides:
                if (_state.Slides(action))
                {
                    UpdateTitle();
                    RestartSlides();
                }

                break;
            default:
                if (_state.Browse(action))
                {
                    UpdateTitle();
                    LoadCurrent();
                    NotifyBrowsed();
                    RestartSlides();   // a picture browsed to during the show gets a whole slide's time
                }

                break;
        }
    }

    private void WheelBrowse(int steps)
    {
        if (_state.Disarm())
        {
            _deleteArm.Stop();
            UpdateTitle();
        }

        bool moved = false;
        for (int i = 0; i < Math.Abs(steps); i++)
        {
            moved |= _state.Browse(steps > 0 ? ViewerAction.Newer : ViewerAction.Older);
        }

        if (moved)
        {
            UpdateTitle();
            LoadCurrent();
            NotifyBrowsed();
            RestartSlides();
            UpdateArrows();
        }
    }

    // An arrow's click: exactly its key — a first Del disarmed, then the step.
    private void Step(ViewerAction action)
    {
        if (_state.Disarm())
        {
            _deleteArm.Stop();
            UpdateTitle();
        }

        Do(action);
        UpdateArrows();
    }

    private void RestartSlides()
    {
        if (_state.SlideShow)
        {
            _slides.Start(TimeSpan.FromSeconds(_state.SlideSeconds));
        }
        else
        {
            _slides.Stop();
        }
    }

    // Del: the first arms the shown picture (the title says so, a timer disarms it), a second on the same picture in time deletes
    // it for good and the next one is shown without waiting for the watcher.
    private void DeleteShown()
    {
        string? path = _state.PressDelete(Environment.TickCount64);
        if (path is null)
        {
            UpdateTitle();
            if (_state.DeleteArmed)
            {
                _deleteArm.Start(TimeSpan.FromMilliseconds(ViewerState.DeleteArmMilliseconds));
            }

            return;
        }

        _deleteArm.Stop();
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("Viewer", ViewerText.DeleteFailed(Path.GetFileName(path), ex.Message));
            UpdateTitle();
            return;
        }

        DiagnosticLog.Info("Viewer", $"Deleted {path}.");
        _state.Remove(path);
        UpdateTitle();
        LoadCurrent();
        RestartSlides();
        NotifyBrowsed();
    }

    private void NotifyBrowsed()
    {
        if (_state.Current is not { } path || PictureWindow.Browsed is not { } browsed)
        {
            return;
        }

        try
        {
            browsed(path);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Viewer", $"Could not tell the strip about {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    // A folder's pictures listed and watched, the newest shown; the old watcher's events are dropped by generation.
    private void Show(string folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        int generation = ++_generation;
        var pictures = new List<ViewerEntry>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (ImageFile.IsImagePath(path))
                {
                    pictures.Add(new ViewerEntry(path, File.GetCreationTimeUtc(path)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not list {folder}: {ex.Message}");
        }

        _state.Reset(folder, pictures);
        RestartSlides();   // a new folder stops the show
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            watcher.Created += (_, e) => Post(new Change(generation, ChangeKind.Created, e.FullPath, null));
            watcher.Changed += (_, e) => Post(new Change(generation, ChangeKind.Changed, e.FullPath, null));
            watcher.Deleted += (_, e) => Post(new Change(generation, ChangeKind.Deleted, e.FullPath, null));
            watcher.Renamed += (_, e) => Post(new Change(generation, ChangeKind.Renamed, e.FullPath, e.OldFullPath));
            watcher.Error += (_, e) => DiagnosticLog.Warn("Viewer", $"Watching {folder} failed: {e.GetException().Message}");
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not watch {folder}: {ex.Message}");
        }

        UpdateTitle();
        LoadCurrent();
    }

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

        if (_state.Select(path, created))
        {
            UpdateTitle();
            LoadCurrent();
        }
    }

    // From the watcher's thread: queued, and the main thread told (the state is only touched there).
    private void Post(Change change)
    {
        _changes.Enqueue(change);
        AppKitHost.Post(Drain);
    }

    private void Drain()
    {
        if (!Alive)
        {
            return;
        }

        bool shownChanged = false;
        bool any = false;
        while (_changes.TryDequeue(out var change))
        {
            if (change.Generation != _generation)
            {
                continue;
            }

            // A write to a listed picture leaves it in its place and reads it again when it is the shown one; only a new name
            // goes on the end.
            if (change.Kind == ChangeKind.Changed)
            {
                shownChanged |= _state.Contains(change.Path) && _state.Touched(change.Path);
                continue;
            }

            any = true;
            if (change.OldPath is { } old && ImageFile.IsImagePath(old))
            {
                shownChanged |= _state.Remove(old);
            }

            if (change.Kind == ChangeKind.Deleted)
            {
                shownChanged |= _state.Remove(change.Path);
            }
            else if (change.Kind == ChangeKind.Renamed && _state.Contains(change.Path))
            {
                shownChanged |= _state.Touched(change.Path);
            }
            else if (ImageFile.IsImagePath(change.Path))
            {
                shownChanged |= _state.Add(change.Path, DateTime.UtcNow);
            }
        }

        if (!any && !shownChanged)
        {
            return;
        }

        UpdateTitle();
        if (shownChanged)
        {
            // Restarted by every change: a burst is one read, the last picture's.
            _debounce.Start(TimeSpan.FromMilliseconds(PictureWindowThread.DebounceMilliseconds));
        }
    }

    private void UpdateTitle() => SetTitle(_state.Title());

    // The shown picture read off the main thread (made a CGImage there too, CoreGraphics being thread-safe); an older read still
    // running is cancelled, and its answer dropped by version.
    private void LoadCurrent()
    {
        int version = ++_loadVersion;
        _load?.Cancel();
        _load = null;
        string? path = _state.Current;
        if (path is null)
        {
            ShowImage(0, null);
            return;
        }

        var load = new CancellationTokenSource();
        _load = load;
        var token = load.Token;
        _ = Task.Run(() => ViewerImage.LoadThenAsync(path, token, bitmap =>
        {
            nint image = bitmap is null ? 0 : CGImage(bitmap.Bgrx, bitmap.Width, bitmap.Height, BitmapBgrx);
            if (!AppKitHost.Post(() => TakeLoaded(version, path, image)) && image != 0)
            {
                CGImageRelease(image);
            }
        }));
    }

    private void TakeLoaded(int version, string path, nint image)
    {
        if (version != _loadVersion || !Alive)
        {
            if (image != 0)
            {
                CGImageRelease(image);
            }

            return;
        }

        ShowImage(image, image == 0 ? Path.GetFileName(path) : null);
    }

    // The picture's layer given its new image (the old one let go), or none with the middle line.
    private void ShowImage(nint image, string? unreadable)
    {
        nint old = _image;
        _image = image;
        _unreadable = unreadable;
        WithoutAnimation(() =>
        {
            SendVoid(_picture, Sel("setContents:"), image);
            UpdateText();
        });
        if (old != 0)
        {
            CGImageRelease(old);
        }
    }

    // ---- the layers ----

    private void MakeLayers()
    {
        var (width, height) = ClientSize;
        _picture = Send(Send(Class("CALayer"), Sel("alloc")), Sel("init"));
        SendVoidRect(_picture, Sel("setFrame:"), new CGRect(0, 0, width, height));
        SendVoidUInt(_picture, Sel("setAutoresizingMask:"), LayerSizable);
        SendVoid(_picture, Sel("setContentsGravity:"), NSString("resizeAspect"));
        SendVoid(_picture, Sel("setMinificationFilter:"), NSString("trilinear"));
        SendVoid(RootLayer, Sel("addSublayer:"), _picture);

        _text = Send(Send(Class("CATextLayer"), Sel("alloc")), Sel("init"));
        SendVoid(_text, Sel("setAlignmentMode:"), NSString("center"));
        SendVoid(_text, Sel("setTruncationMode:"), NSString("middle"));
        SendVoid(_text, Sel("setFont:"), SendFont(Class("NSFont"), Sel("systemFontOfSize:"), 13));
        SendVoidDouble(_text, Sel("setFontSize:"), 13);
        SendVoid(RootLayer, Sel("addSublayer:"), _text);

        _newer = Send(Send(Class("CALayer"), Sel("alloc")), Sel("init"));
        _older = Send(Send(Class("CALayer"), Sel("alloc")), Sel("init"));
        foreach (nint arrow in (ReadOnlySpan<nint>)[_newer, _older])
        {
            SendVoidFloat(arrow, Sel("setOpacity:"), 0);
            SendVoid(RootLayer, Sel("addSublayer:"), arrow);
        }

        Layout();
    }

    // The middle line and the arrows placed for the view's size and scale (the picture's layer follows the view by itself).
    private void Layout()
    {
        if (!Alive || _text == 0)
        {
            return;
        }

        var (width, height) = ClientSize;
        double scale = Scale;
        WithoutAnimation(() =>
        {
            SendVoidRect(_text, Sel("setFrame:"), new CGRect(12, Math.Max(0, (height - TextHeight) / 2), Math.Max(0, width - 24), TextHeight));
            SendVoidDouble(_text, Sel("setContentsScale:"), scale);
            if (ViewerNav.Layout((int)width, (int)height, 96) is { } squares)
            {
                // The view is flipped for the mouse; the squares are centred top to bottom, so the layers' y is the same either way.
                SendVoidRect(_newer, Sel("setFrame:"), new CGRect(squares.Newer.X, squares.Newer.Y, squares.Newer.Side, squares.Newer.Side));
                SendVoidRect(_older, Sel("setFrame:"), new CGRect(squares.Older.X, squares.Older.Y, squares.Older.Side, squares.Older.Side));
                ArrowImages(squares.Newer.Side, scale);
            }
        });
        UpdateArrows();
    }

    // The arrows' pixels at the screen's scale (sharp on Retina), made again for a new size, style or scale.
    private void ArrowImages(int side, double scale)
    {
        uint fill = _style?.Caption ?? ViewerStyle.Black.Caption;
        uint ink = _style?.CaptionText ?? ViewerStyle.Black.CaptionText;
        if (_arrowKey == (side, fill, ink, scale))
        {
            return;
        }

        _arrowKey = (side, fill, ink, scale);
        int pixels = Math.Max(1, (int)Math.Round(side * scale));
        foreach (var (layer, left) in (ReadOnlySpan<(nint, bool)>)[(_newer, true), (_older, false)])
        {
            uint[] bgra = ViewerNav.Pixels(pixels, left, fill, ink);
            byte[] bytes = new byte[bgra.Length * 4];
            Buffer.BlockCopy(bgra, 0, bytes, 0, bytes.Length);
            nint image = CGImage(bytes, pixels, pixels, BitmapPremultipliedBgra);
            SendVoid(layer, Sel("setContents:"), image);
            if (image != 0)
            {
                CGImageRelease(image);
            }
        }
    }

    // The arrows' opacity: shown while the mouse is over the window and there is a picture that way, the one under it brighter.
    // Core Animation fades between (a tenth of a second, the Windows fade's length).
    private void UpdateArrows()
    {
        if (_newer == 0)
        {
            return;
        }

        bool fits = ViewerNav.Layout((int)ClientSize.Width, (int)ClientSize.Height, 96) is not null;
        nint transaction = Class("CATransaction");
        SendVoid(transaction, Sel("begin"));
        SendVoidDouble(transaction, Sel("setAnimationDuration:"), 0.1);
        SendVoidFloat(_newer, Sel("setOpacity:"), fits && _hover && _state.CanNewer ? ViewerNav.Alpha(255, _hot == ViewerAction.Newer) / 255f : 0);
        SendVoidFloat(_older, Sel("setOpacity:"), fits && _hover && _state.CanOlder ? ViewerNav.Alpha(255, _hot == ViewerAction.Older) / 255f : 0);
        SendVoid(transaction, Sel("commit"));
    }

    private void Hover((double X, double Y) point)
    {
        var hot = NavAt(point);
        SetHand(hot != ViewerAction.None);
        if (hot != _hot || !_hover)
        {
            _hover = true;
            _hot = hot;
        }

        UpdateArrows();
    }

    private static void SetHand(bool hand) =>
        SendVoid(Send(Class("NSCursor"), Sel(hand ? "pointingHandCursor" : "arrowCursor")), Sel("set"));

    private ViewerAction NavAt((double X, double Y) point)
    {
        var (width, height) = ClientSize;
        return ViewerNav.At((int)point.X, (int)point.Y, ViewerNav.Layout((int)width, (int)height, 96), _state.CanNewer, _state.CanOlder);
    }

    private void UpdateText()
    {
        string text = _image != 0 ? "" : _unreadable is { } name ? ViewerText.Unreadable(name) : _state.Count == 0 ? ViewerText.Waiting(_state.Folder) : "";
        SendVoid(_text, Sel("setString:"), NSString(text));
    }

    // The theme in force round the picture, on the bar, in the middle line and on the arrows; nothing when unchanged.
    private void ApplyStyle()
    {
        var style = ViewerStyle.For(NeonSidekick.UI.Theme.Current, PictureWindow.Themed());
        if (_style == style)
        {
            return;
        }

        _style = style;
        ApplyChrome(style);
        nint background = CGColor(style.Background);
        nint text = CGColor(style.Text);
        WithoutAnimation(() =>
        {
            SendVoid(RootLayer, Sel("setBackgroundColor:"), background);
            SendVoid(_text, Sel("setForegroundColor:"), text);
        });
        CGColorRelease(background);
        CGColorRelease(text);
        Layout();
    }
}
