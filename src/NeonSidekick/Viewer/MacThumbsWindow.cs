using System.Collections.Concurrent;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Images;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The thumbnail browser on a Mac (2026-10-07, Stage 2 phase 2) — <see cref="ThumbsWindow"/>'s calls land here when
/// <see cref="OperatingSystem.IsMacOS"/>. One per process, as on Windows. Its grid is the shared <see cref="ThumbsState"/> (laid out in
/// points, as Windows' is in pixels at 96 DPI) and its reads the shared <see cref="ThumbCache"/>, each tile decoded at the bucket the
/// tile needs at the screen's scale (twice its points on Retina, so a tile is sharp). The view draws itself with CoreGraphics
/// (<c>drawRect:</c>): the tiles as CGImages over the cache's own pixels, the names with AppKit's text drawing, and the same thin scroll
/// bar as on Windows. A click shows the picture in the viewer (<see cref="ThumbsWindow.Picked"/>), a double-click or Enter opens it
/// there, a right-click (or Control-click) is the picture menu (<see cref="MacPictureMenu"/>), and the wheel, a two-finger swipe, ⌘ or
/// Ctrl with the wheel, a pinch and + − scroll and size. Main thread only past the facade; excluded from coverage with the AppKit layer,
/// proven by the smoke's <c>viewer:thumbs</c> and by hand.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacThumbsWindows
{
    private static volatile MacThumbsWindow? s_open;   // set on the main thread; read elsewhere only to choose a post over a wait
    private static int s_registered;

    /// <summary>The browser on <paramref name="folder"/> with <paramref name="select"/> selected: opened, or the open one pointed at it and brought forward.</summary>
    public static void Open(string folder, string? select)
    {
        RegisterEnd();
        if (s_open is { Alive: true } && AppKitHost.Post(() => OpenOnMain(folder, select)))
        {
            return;
        }

        if (!AppKitHost.Invoke(() => OpenOnMain(folder, select), out _))
        {
            throw new InvalidOperationException("the window did not start");
        }
    }

    private static bool OpenOnMain(string folder, string? select)
    {
        if (s_open is { Alive: true } open)
        {
            open.Retarget(folder, select);
            return true;
        }

        var window = new MacThumbsWindow();
        window.Start(folder, select);
        s_open = window;
        return true;
    }

    /// <summary>The open browser's selection moved to <paramref name="picture"/> quietly (<see cref="ThumbsWindow.Follow"/>).</summary>
    public static void Follow(string picture) => AppKitHost.Post(() => s_open?.FollowTo(picture));

    /// <summary>The browser closed; true when one was open.</summary>
    public static bool Close() =>
        AppKitHost.IsRunning && AppKitHost.Invoke(() =>
        {
            var open = s_open;
            s_open = null;
            bool alive = open is { Alive: true };
            open?.Close();
            return alive;
        }, out bool closed) && closed;

    /// <summary>
    /// <c>viewer:thumbs</c> on a Mac: a hidden window with the app's drawn view, drawn into a bitmap through its <c>drawRect:</c> on the
    /// main thread — a tile's CGImage and a caption with AppKit's text drawing — and let go. Nothing is shown.
    /// </summary>
    public static (bool Ok, string Detail) Probe()
    {
        if (!AppKitHost.IsEnabled)
        {
            return (true, HasWindowServer() ? "skipped: no AppKit host here (the app's main thread runs it)" : "skipped: no window server");
        }

        if (!AppKitHost.Invoke(MacThumbsWindow.Probe, out (bool Ok, string Detail) result))
        {
            return (false, "the main thread did not answer");
        }

        return result;
    }

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

/// <summary>One Mac thumbnail browser (<see cref="MacThumbsWindows"/>); main thread only.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacThumbsWindow : AppKitWindow
{
    /// <summary>The window's size in points before the user changes it.</summary>
    public const double DefaultWidth = 1024;
    public const double DefaultHeight = 768;

    /// <summary>The captions' text size in points (Windows' 9-point Segoe UI is about this tall on a Mac's system font).</summary>
    public const double CaptionPoints = 11;

    /// <summary>A pinch's magnification that is one size step.</summary>
    public const double PinchStep = 0.2;

    private static int s_probeDraws;

    private readonly ThumbsState _state = new();
    private readonly ThumbCache _cache = new(ThumbCache.DefaultBudget);
    private readonly ConcurrentQueue<Change> _changes = new();
    private readonly ConcurrentQueue<Decoded> _decoded = new();
    private readonly Dictionary<string, int> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stale = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxInFlight = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
    private readonly MainTimer _debounce;
    private readonly MainTimer _deleteArm;
    private FileSystemWatcher? _watcher;
    private int _generation;
    private CancellationTokenSource? _decodes;
    private ThumbsStyle _style = ThumbsStyle.Black;
    private double _captionHeight;
    private double _lineHeight;
    private int? _thumbGrab;
    private int _wheel;
    private double _scrollRest;
    private double _zoomRest;
    private double _pinch;

    private enum ChangeKind
    {
        Created,
        Deleted,
        Renamed,
        Changed,
    }

    private sealed record Change(int Generation, ChangeKind Kind, string Path, string? OldPath);

    private sealed record Decoded(int Generation, string Path, int Version, int Bucket, ViewerBitmap? Bitmap);

    public MacThumbsWindow()
    {
        _debounce = new MainTimer(ReadTouchedAgain);
        _deleteArm = new MainTimer(DisarmDelete);
    }

    protected override Action<int, int>? Placed => ThumbsWindow.Placed;

    /// <summary>The window made and brought forward on <paramref name="folder"/>, <paramref name="select"/> selected.</summary>
    public void Start(string folder, string? select)
    {
        Create(DefaultWidth, DefaultHeight, ThumbsWindow.Position, drawn: true);
        MeasureFont();
        ApplyStyle();
        Show(folder);
        Select(select);
        ShowWindow(activate: true);
        DiagnosticLog.Info("Viewer", $"Thumbnail browser opened on {folder}.");
    }

    /// <summary>Pointed at <paramref name="folder"/> (<paramref name="select"/> selected) and brought forward.</summary>
    public void Retarget(string folder, string? select)
    {
        if (!string.Equals(folder, _state.Folder, StringComparison.OrdinalIgnoreCase))
        {
            Show(folder);
        }

        Select(select);
        ApplyStyle();
        ShowWindow(activate: true);
    }

    /// <summary>The viewer's or the strip's picture: on this folder only, never brought forward, never told back.</summary>
    public void FollowTo(string picture)
    {
        if (Alive && string.Equals(Path.GetDirectoryName(picture), _state.Folder, StringComparison.OrdinalIgnoreCase) && File.Exists(picture))
        {
            Select(picture);
        }
    }

    protected override void OnClosed()
    {
        _watcher?.Dispose();
        _watcher = null;
        _decodes?.Cancel();
        _debounce.Stop();
        _deleteArm.Stop();
        _cache.Clear();
        DiagnosticLog.Info("Viewer", "Thumbnail browser closed.");
    }

    // ---- keys and the mouse ----

    internal override bool KeyDown(MacKeyEvent key)
    {
        var action = MacKeys.ThumbsAction(key.KeyCode, key.Flags, FullScreen);
        if (action != ThumbsAction.Delete)
        {
            DisarmDelete();   // any key but Del disarms a first Del, mapped or not
        }

        if (action == ThumbsAction.Delete && key.Repeat)
        {
            return true;   // a held Del is one press
        }

        if (action == ThumbsAction.None)
        {
            return false;
        }

        Do(action);
        return true;
    }

    internal override void MouseDown(nint e)
    {
        DisarmDelete();
        var (x, y) = PointOf(e);
        if (ControlClick(e))
        {
            MenuAt(e, x, y);
            return;
        }

        if (x >= BarLeft())
        {
            PressBar((int)y);
            return;
        }

        var index = _state.HitTest((int)x, (int)y);
        if (SendLong(e, Sel("clickCount")) >= 2)
        {
            if (index is int tile)
            {
                _state.SelectIndex(tile);
                Refresh();
                OpenInViewer();
            }
            else
            {
                Do(ThumbsAction.ToggleFullScreen);
            }

            return;
        }

        if (index is int i && _state.SelectIndex(i))
        {
            Refresh();
            NotifyPicked();
        }
    }

    internal override void MouseDragged(nint e)
    {
        if (_thumbGrab is { } grab)
        {
            DragThumb((int)PointOf(e).Y - grab);
        }
    }

    internal override void MouseUp(nint e)
    {
        if (_thumbGrab is not null)
        {
            _thumbGrab = null;
            Redraw();
        }
    }

    internal override void RightMouseDown(nint e)
    {
        DisarmDelete();
        var (x, y) = PointOf(e);
        MenuAt(e, x, y);
    }

    // The wheel or two fingers scroll (a trackpad's points as they come, a wheel's notches as Windows' are); with ⌘ or Ctrl held they
    // size the tiles instead, a step a notch (or a swipe's 40 points).
    internal override void ScrollWheel(nint e)
    {
        double dy = SendDouble(e, Sel("scrollingDeltaY"));
        bool precise = SendBool(e, Sel("hasPreciseScrollingDeltas")) != 0;
        ulong flags = SendULong(e, Sel("modifierFlags"));
        if ((flags & (MacKeys.CommandFlag | MacKeys.ControlFlag)) != 0)
        {
            if (SendULong(e, Sel("momentumPhase")) != 0)
            {
                return;
            }

            _zoomRest += precise ? dy / 40 : dy;
            int steps = (int)_zoomRest;
            _zoomRest -= steps;
            if (steps != 0 && _state.Zoom(steps))
            {
                Refresh();
            }

            return;
        }

        int pixels;
        if (precise)
        {
            _scrollRest -= dy;
            pixels = (int)_scrollRest;
            _scrollRest -= pixels;
        }
        else
        {
            pixels = ThumbsState.WheelPixels(ref _wheel, (int)Math.Round(dy * 120), _state.PitchY);
        }

        if (pixels != 0 && _state.ScrollBy(pixels))
        {
            Refresh();
        }
    }

    // A pinch sizes the tiles, a step a fifth of a magnification.
    internal override void Magnify(nint e)
    {
        _pinch += SendDouble(e, Sel("magnification"));
        int steps = (int)(_pinch / PinchStep);
        _pinch -= steps * PinchStep;
        if (steps != 0 && _state.Zoom(steps))
        {
            Refresh();
        }
    }

    internal override void Resized() => Layout();

    internal override void BecameKey() => ApplyStyle();

    internal override void ScaleChanged() => Layout();

    // ---- what the window does ----

    private void Do(ThumbsAction action)
    {
        switch (action)
        {
            case ThumbsAction.ToggleFullScreen:
                SetFullScreen(!FullScreen);
                Layout();
                break;
            case ThumbsAction.LeaveFullScreen:
                SetFullScreen(false);
                Layout();
                break;
            case ThumbsAction.Close:
                Close();
                break;
            case ThumbsAction.Open:
                OpenInViewer();
                break;
            case ThumbsAction.Delete:
                DeleteSelected();
                break;
            case ThumbsAction.Refresh:
            {
                string? selected = _state.SelectedPath;
                Show(_state.Folder);
                Select(selected);
                break;
            }

            case ThumbsAction.ZoomIn or ThumbsAction.ZoomOut:
                if (_state.Zoom(action == ThumbsAction.ZoomIn ? 1 : -1))
                {
                    Refresh();
                }

                break;
            case ThumbsAction.Menu:
                if (_state.Selected is int index)
                {
                    var (x, y, side) = _state.TileRect(index);
                    OpenMenu(0, new CGPoint(x + side / 2.0, y + side / 2.0));
                }

                break;
            default:
                if (_state.Apply(action))
                {
                    Refresh();
                    NotifyPicked();
                }

                break;
        }
    }

    // A right-click on a tile: it selected (the viewer following), then the menu at the mouse.
    private void MenuAt(nint e, double x, double y)
    {
        if (x >= BarLeft() || _state.HitTest((int)x, (int)y) is not int index)
        {
            return;
        }

        if (_state.SelectIndex(index))
        {
            Refresh();
            NotifyPicked();
        }

        OpenMenu(e, default);
    }

    // The picture menu on the selected picture: the path kept now; the row chosen run once the menu is gone.
    private void OpenMenu(nint mouse, CGPoint at)
    {
        if (_state.SelectedPath is not { } path)
        {
            return;
        }

        if (MacPictureMenu.Show(ContentView, mouse, at, thumbs: true, path) is { } command && Alive)
        {
            RunCommand(command, path);
        }
    }

    private void RunCommand(PictureCommand command, string path)
    {
        if (PictureActions.IsEdit(command))
        {
            PictureMenu.Edit(path, command, outcome => AppKitHost.Post(() => TakeEdited(outcome)));
            return;
        }

        switch (command)
        {
            case PictureCommand.OpenInViewer:
                OpenAt(path);
                break;
            case PictureCommand.Delete:
                if (PictureMenu.Delete(path))
                {
                    Removed(path);
                }

                break;
            default:
                PictureMenu.RunFileAction(path, command);
                break;
        }
    }

    // An edit's end: a new picture added and selected (the viewer following), one replaced read again, a converted one in its source's place.
    private void TakeEdited(PictureEditOutcome outcome)
    {
        if (!Alive || outcome is not { Failed: false, Written: { } written })
        {
            return;
        }

        if (outcome.Replaced)
        {
            Touch(written);
            ReadTouchedAgain();
            return;
        }

        if (outcome.Removed is { } removed)
        {
            _cache.Remove(removed);
            _state.Remove(removed);
        }

        if (string.Equals(Path.GetDirectoryName(written), _state.Folder, StringComparison.OrdinalIgnoreCase))
        {
            _state.Select(written, DateTime.UtcNow);
            Refresh();
            NotifyPicked();
        }
    }

    // Del: the first arms the selected picture (the title says so, a timer disarms it), a second in time deletes it for good.
    private void DeleteSelected()
    {
        string? path = _state.PressDelete(Environment.TickCount64);
        if (path is null)
        {
            SetTitle(_state.Title());
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
            SetTitle(_state.Title());
            return;
        }

        DiagnosticLog.Info("Viewer", $"Deleted {path} from the thumbnail browser.");
        Removed(path);
    }

    private void DisarmDelete()
    {
        _deleteArm.Stop();
        if (_state.Disarm() && Alive)
        {
            SetTitle(_state.Title());
        }
    }

    private void Removed(string path)
    {
        _cache.Remove(path);
        _state.Remove(path);
        Refresh();
        NotifyPicked();
    }

    private void OpenInViewer()
    {
        if (_state.SelectedPath is { } path)
        {
            OpenAt(path);
        }
    }

    private static void OpenAt(string path)
    {
        try
        {
            PictureWindow.OpenAt(path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or ArgumentException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not open the viewer on {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private void NotifyPicked()
    {
        if (_state.SelectedPath is not { } path || ThumbsWindow.Picked is not { } picked)
        {
            return;
        }

        try
        {
            picked(path);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Viewer", $"Could not tell the viewer about {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    // A folder's pictures listed and watched, the tiles fitted; the old watcher's events and reads are dropped by generation.
    private void Show(string folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        _decodes?.Cancel();
        _decodes = new CancellationTokenSource();
        int generation = ++_generation;
        _cache.Clear();
        _inFlight.Clear();
        _versions.Clear();
        _touched.Clear();
        _stale.Clear();
        var pictures = new List<ThumbEntry>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (ImageFile.IsImagePath(path))
                {
                    pictures.Add(new ThumbEntry(path, File.GetCreationTimeUtc(path)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not list {folder}: {ex.Message}");
        }

        _state.Reset(folder, pictures);
        Layout();
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            watcher.Created += (_, e) => Post(new Change(generation, ChangeKind.Created, e.FullPath, null));
            watcher.Deleted += (_, e) => Post(new Change(generation, ChangeKind.Deleted, e.FullPath, null));
            watcher.Renamed += (_, e) => Post(new Change(generation, ChangeKind.Renamed, e.FullPath, e.OldFullPath));
            watcher.Changed += (_, e) => Post(new Change(generation, ChangeKind.Changed, e.FullPath, null));
            watcher.Error += (_, e) => DiagnosticLog.Warn("Viewer", $"Watching {folder} failed: {e.GetException().Message}");
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("Viewer", $"Could not watch {folder}: {ex.Message}");
        }

        Refresh();
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

        _state.Select(path, created);
        Refresh();
    }

    private void Post(Change change)
    {
        _changes.Enqueue(change);
        AppKitHost.Post(Drain);
    }

    // The folder's changes: arrivals appended, a rename kept in place, a deletion closing up, a write read again after a pause.
    private void Drain()
    {
        if (!Alive)
        {
            return;
        }

        bool any = false;
        while (_changes.TryDequeue(out var change))
        {
            if (change.Generation != _generation)
            {
                continue;
            }

            bool image = ImageFile.IsImagePath(change.Path);
            switch (change.Kind)
            {
                case ChangeKind.Deleted:
                    if (_state.Remove(change.Path))
                    {
                        _cache.Remove(change.Path);
                        any = true;
                    }

                    break;
                case ChangeKind.Renamed when change.OldPath is { } old && _state.IndexOf(old) >= 0:
                    if (!image)
                    {
                        _state.Remove(old);
                        _cache.Remove(old);
                    }
                    else if (_state.Rename(old, change.Path, DateTime.UtcNow) == ThumbChange.Renamed)
                    {
                        _cache.Rename(old, change.Path);
                    }
                    else
                    {
                        _cache.Remove(old);
                        Touch(change.Path);
                    }

                    any = true;
                    break;
                case ChangeKind.Changed when _state.IndexOf(change.Path) >= 0:
                    Touch(change.Path);
                    break;
                default:
                    if (image && change.Kind != ChangeKind.Deleted && File.Exists(change.Path))
                    {
                        if (_state.Add(change.Path, DateTime.UtcNow) == ThumbChange.Changed)
                        {
                            Touch(change.Path);
                        }

                        any = true;
                    }

                    break;
            }
        }

        if (any)
        {
            Refresh();
        }
    }

    private void Touch(string path)
    {
        _touched.Add(path);
        _debounce.Start(TimeSpan.FromMilliseconds(ThumbsWindowThread.DebounceMilliseconds));
    }

    private void ReadTouchedAgain()
    {
        foreach (string path in _touched)
        {
            _versions[path] = _versions.GetValueOrDefault(path) + 1;
            _stale.Add(path);
        }

        _touched.Clear();
        Refresh();
    }

    // The tiles in view (then a page above and below) that lack a read at the bucket the size needs on this screen, started up to
    // the cap; the cache trimmed past its budget, never of those.
    private void Pump()
    {
        if (_decodes is not { } decodes || _state.Count == 0)
        {
            return;
        }

        int bucket = ThumbsState.Bucket((int)Math.Ceiling(_state.Tile * Scale));
        var (first, last) = _state.Visible();
        var (aheadFirst, aheadLast) = _state.Visible(_state.PageRows);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = aheadFirst; i <= aheadLast; i++)
        {
            keep.Add(_state.Entries[i].Path);
        }

        var order = Enumerable.Range(first, Math.Max(0, last - first + 1))
            .Concat(Enumerable.Range(last + 1, Math.Max(0, aheadLast - last)))
            .Concat(Enumerable.Range(aheadFirst, Math.Max(0, first - aheadFirst)));
        foreach (int i in order)
        {
            if (_inFlight.Count >= _maxInFlight)
            {
                break;
            }

            string path = _state.Entries[i].Path;
            if (_inFlight.ContainsKey(path) || _touched.Contains(path) || (!_stale.Contains(path) && _cache.Satisfies(path, bucket)))
            {
                continue;
            }

            StartDecode(path, bucket, decodes.Token);
        }

        _cache.Trim(keep);
    }

    private void StartDecode(string path, int bucket, CancellationToken token)
    {
        int generation = _generation;
        int version = _versions.GetValueOrDefault(path);
        _inFlight[path] = bucket;
        _ = Task.Run(() => ViewerImage.LoadThenAsync(path, token, bitmap =>
        {
            _decoded.Enqueue(new Decoded(generation, path, version, bucket, token.IsCancellationRequested ? null : bitmap));
            AppKitHost.Post(TakeDecoded);
        }, (bytes, name) => ViewerImage.DecodeThumbnail(bytes, name, bucket)));
    }

    private void TakeDecoded()
    {
        if (!Alive)
        {
            return;
        }

        bool any = false;
        while (_decoded.TryDequeue(out var done))
        {
            if (done.Generation != _generation)
            {
                continue;
            }

            _inFlight.Remove(done.Path);
            if (done.Version != _versions.GetValueOrDefault(done.Path) || _state.IndexOf(done.Path) < 0)
            {
                continue;
            }

            _cache.Put(done.Path, done.Bucket, done.Bitmap);
            _stale.Remove(done.Path);
            any = true;
        }

        if (any)
        {
            Redraw();
        }

        Pump();
    }

    private void Refresh()
    {
        if (!Alive)
        {
            return;
        }

        SetTitle(_state.Title());
        Redraw();
        Pump();
    }

    // The view's size handed to the state, in points (which fits the tiles again unless zoomed).
    private void Layout()
    {
        if (!Alive)
        {
            return;
        }

        var (width, height) = ClientSize;
        _state.Relayout((int)width, (int)height, 96, (int)Math.Ceiling(_captionHeight));
        Refresh();
    }

    private void ApplyStyle()
    {
        bool themed = PictureWindow.Themed();
        var palette = NeonSidekick.UI.Theme.Current;
        ApplyChrome(ViewerStyle.For(palette, themed));
        var style = ThumbsStyle.For(palette, themed);
        if (style != _style)
        {
            _style = style;
            Redraw();
        }
    }

    // The captions' line measured from the system font: its height plus Windows' 8 points of room.
    private void MeasureFont()
    {
        nint font = SendFont(Class("NSFont"), Sel("systemFontOfSize:"), CaptionPoints);
        _lineHeight = Math.Ceiling(SendDouble(font, Sel("ascender")) - SendDouble(font, Sel("descender")) + SendDouble(font, Sel("leading")));
        _captionHeight = _lineHeight + 8;
    }

    private double BarLeft() => ClientSize.Width - _state.BarPixels;

    private (int Offset, int Length, int Track) ThumbNow()
    {
        int height = (int)ClientSize.Height;
        var (offset, length) = LogViewState.Thumb(_state.ContentHeight, height, _state.ScrollTop, height, 24);
        return (offset, length, height);
    }

    private void PressBar(int y)
    {
        if (_state.MaxScroll <= 0)
        {
            return;
        }

        var (offset, length, _) = ThumbNow();
        if (y >= offset && y < offset + length)
        {
            _thumbGrab = y - offset;
            Redraw();
            return;
        }

        int page = Math.Max(1, _state.ClientHeight - _state.PitchY / 2);
        if (_state.ScrollBy(y < offset ? -page : page))
        {
            Refresh();
        }
    }

    private void DragThumb(int offset)
    {
        var (_, length, track) = ThumbNow();
        if (_state.ScrollTo((int)LogViewState.TopForThumb(offset, _state.ContentHeight, _state.ClientHeight, track, length)))
        {
            Refresh();
        }
    }

    // ---- drawing ----

    internal override void Draw(nint context, CGRect dirty)
    {
        var (width, height) = ClientSize;
        Fill(context, _style.Background);
        CGContextFillRect(context, new CGRect(0, 0, width, height));
        CGContextSetInterpolationQuality(context, InterpolationHigh);
        if (_state.Count == 0)
        {
            DrawText(ThumbsText.Empty(_state.Folder), new CGRect(0, (height - _lineHeight) / 2, width - _state.BarPixels, _lineHeight), _style.Text);
        }
        else
        {
            var (first, last) = _state.Visible();
            for (int i = first; i <= last; i++)
            {
                DrawTile(context, i);
            }
        }

        // The scroll bar: its track the whole height, the thumb on it while the grid is taller than the window.
        double barLeft = width - _state.BarPixels;
        Fill(context, _style.Track);
        CGContextFillRect(context, new CGRect(barLeft, 0, _state.BarPixels, height));
        if (_state.MaxScroll > 0)
        {
            var (offset, length, _) = ThumbNow();
            Fill(context, _thumbGrab is null ? _style.Thumb : _style.ThumbActive);
            CGContextFillRect(context, new CGRect(barLeft + 2, offset, _state.BarPixels - 4, length));
        }
    }

    // One tile: the placeholder square, the picture fitted in it (or the unreadable mark), the selection's frame, the name under it.
    private void DrawTile(nint context, int index)
    {
        var entry = _state.Entries[index];
        var (x, y, side) = _state.TileRect(index);
        bool selected = _state.Selected == index;
        if (selected)
        {
            const double frame = 3;
            Fill(context, _style.Selected);
            CGContextFillRect(context, new CGRect(x - frame, y - frame, side + 2 * frame, side + _captionHeight + 2 * frame));
        }

        var (bitmap, failed) = _cache.Get(entry.Path);
        if (bitmap is null)
        {
            Fill(context, _style.Placeholder);
            CGContextFillRect(context, new CGRect(x, y, side, side));
            if (failed)
            {
                DrawText(ThumbsText.Unreadable, new CGRect(x, y + (side - _lineHeight) / 2, side, _lineHeight), _style.Text);
            }
        }
        else
        {
            Fill(context, selected ? _style.Selected : _style.Background);
            CGContextFillRect(context, new CGRect(x, y, side, side));

            // Fitted in points at the picture's own shape: the bitmap has the screen's pixels for it.
            double fit = Math.Min((double)side / bitmap.Width, (double)side / bitmap.Height);
            double w = bitmap.Width * fit, h = bitmap.Height * fit;
            nint image = CGImage(bitmap.Bgrx, bitmap.Width, bitmap.Height, BitmapBgrx);
            if (image != 0)
            {
                DrawImageFlipped(context, new CGRect(x + (side - w) / 2, y + (side - h) / 2, w, h), image);
                CGImageRelease(image);
            }
        }

        DrawText(Path.GetFileName(entry.Path), new CGRect(x + 2, y + side + (_captionHeight - _lineHeight) / 2, side - 4, _lineHeight), selected ? _style.SelectedText : _style.Caption);
    }

    // A line of text centred in rect, cut with an ellipsis when it is too long, in the system font.
    private static void DrawText(string text, CGRect rect, uint color)
    {
        nint font = SendFont(Class("NSFont"), Sel("systemFontOfSize:"), CaptionPoints);
        nint paragraph = Send(Send(Send(Class("NSMutableParagraphStyle"), Sel("alloc")), Sel("init")), Sel("autorelease"));
        SendVoidLong(paragraph, Sel("setAlignment:"), 1);         // NSTextAlignmentCenter on Apple Silicon (2 on Intel)
        SendVoidULong(paragraph, Sel("setLineBreakMode:"), 4);    // NSLineBreakByTruncatingTail
        Span<nint> keys = [NSString("NSFont"), NSString("NSColor"), NSString("NSParagraphStyle")];
        Span<nint> values = [font, NSColor(color), paragraph];
        unsafe
        {
            fixed (nint* k = keys, v = values)
            {
                nint attributes = SendCount(Class("NSDictionary"), Sel("dictionaryWithObjects:forKeys:count:"), (nint)v, (nint)k, 3);
                SendVoidRectNint(NSString(text), Sel("drawInRect:withAttributes:"), rect, attributes);
            }
        }
    }

    /// <summary>The smoke's drawing proof (<see cref="MacThumbsWindows.Probe"/>), on the main thread.</summary>
    internal static (bool Ok, string Detail) Probe()
    {
        var probe = new ProbeWindow();
        try
        {
            return probe.Run();
        }
        finally
        {
            probe.Close();
        }
    }

    // A hidden window with the drawn view, drawn into a bitmap: drawRect: reached through the runtime, a tile and a caption drawn.
    private sealed class ProbeWindow : AppKitWindow
    {
        protected override Action<int, int>? Placed => null;

        public (bool Ok, string Detail) Run()
        {
            Create(64, 48, null, drawn: true);
            nint view = ContentView;
            var bounds = SendRect(view, Sel("bounds"));
            nint rep = SendInitRect(view, Sel("bitmapImageRepForCachingDisplayInRect:"), bounds);
            int before = s_probeDraws;
            SendVoidRectNint(view, Sel("cacheDisplayInRect:toBitmapImageRep:"), bounds, rep);
            bool drew = s_probeDraws > before;
            bool hidden = SendBool(Window, Sel("isVisible")) == 0;
            return (drew && hidden, $"a hidden window's drawn view drew a tile and a caption into a bitmap through drawRect:{(drew ? "" : " (it was never called)")}");
        }

        internal override void Draw(nint context, CGRect dirty)
        {
            nint image = CGImage(new byte[4 * 4 * 4], 4, 4, BitmapBgrx);
            DrawImageFlipped(context, new CGRect(4, 4, 16, 16), image);
            CGImageRelease(image);
            DrawText("0001.png", new CGRect(0, 24, 64, 16), 0x00FFFFFF);
            s_probeDraws++;
        }

        protected override void OnClosed()
        {
        }
    }
}
