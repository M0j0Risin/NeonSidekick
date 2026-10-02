using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.AI;
using NeonSidekick.Camera;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using NeonSidekick.Viewer;

namespace NeonSidekick.App;

// ── The camera (2026-10-02) ───────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The shared camera stream the shutter pane, the live view, a botchat and watch mode hold (<see cref="CameraSession"/>).</summary>
    private readonly CameraSession _camera;

    /// <summary>Photos taken and saved into the working directory's <c>camera</c> folder.</summary>
    private readonly CameraCapture _cameraCapture;

    private readonly CameraMenu _cameraMenu;

    /// <summary><c>camera_capture</c>, built once; offered while <see cref="CameraOffered"/> says so.</summary>
    private readonly IReadOnlyList<AIFunction> _cameraTools;

    /// <summary>The picture viewer's live picture (<see cref="PictureWindow.ShowLive"/>) in the app on Windows; null in tests and elsewhere.</summary>
    private readonly Func<string, Action, ILiveView>? _liveView;

    /// <summary>The picture viewer on a shot without the keyboard (<c>PictureWindow.OpenAt(path, false)</c>) in the app; null in tests and elsewhere.</summary>
    private readonly Action<string>? _showShot;

    /// <summary>Watch mode (<c>/camera watch</c>): off until asked for, never at startup.</summary>
    private readonly CameraWatch _cameraWatch;

    /// <summary>The camera's photos by full path, so one put on the input line is still known as the camera's when it is sent (the session keeps a placeholder).</summary>
    private readonly ConcurrentDictionary<string, byte> _cameraPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Allow for this session" on the allow pane (the <c>model</c> shutter); forgotten with the session.</summary>
    private bool _cameraAllowed;

    /// <summary>A photo declined or denied in the running turn: the turn's later calls are answered without asking.</summary>
    private bool _cameraDeclined;

    /// <summary>A shot for the input line, replayed by the idle loop as the paste of its path (the path rule makes it an <c>[Image #N]</c>).</summary>
    private IReadOnlyList<InputEvent>? _attachReplay;

    /// <summary><c>/camera live</c>'s hold: the lease and the preview, until the window closes or <c>/camera off</c>.</summary>
    private CameraLease? _liveLease;

    private CameraPreviewRun? _livePreview;

    /// <summary>When watch mode last started a turn by itself (<c>Camera watch speaks up</c>).</summary>
    private DateTimeOffset _lastWatchNudge = DateTimeOffset.MinValue;

    /// <summary>Set by a kept watch frame while <c>Camera watch speaks up</c> is on; the idle loop decides whether a turn starts.</summary>
    private volatile bool _watchNudge;

    /// <summary>The camera tool (2026-10-02): <c>camera_capture</c> over <paramref name="shoot"/> (null where nothing can ask, so every call is refused).</summary>
    public static IReadOnlyList<AIFunction> CameraTools(Func<string, CancellationToken, Task<CameraAnswer>>? shoot) => [new CameraCaptureTool(shoot)];

    /// <summary>
    /// Whether <c>camera_capture</c> is offered (2026-10-02): the setting <c>Camera tool</c> on, a camera layer here, the pane to
    /// ask on, and a model that reads pictures (an embedded one without its vision projector does not). Pure.
    /// </summary>
    public static bool CameraOffered(AppSettingsData effective, bool available, bool pane, bool blind) =>
        effective.CameraTools && available && pane && !blind;

    private bool CameraOffered(AppSettingsData effective) =>
        CameraOffered(effective, _camera.Available, _pane.Enabled, _session.EmbeddedServer is { Vision: false });

    /// <summary>The hint row's strip with the camera's glyph first while the camera is open (privacy before preference: drawn whatever else the row shows).</summary>
    public static string CameraStrip(bool live, string strip) =>
        !live ? strip : strip.Length == 0 ? CameraText.Glyph : CameraText.Glyph + GlyphSeparator + strip;

    /// <summary>
    /// The model's <c>camera_capture</c>, on the turn task: a photo declined earlier in the turn is not asked for again; under
    /// the <c>user</c> shutter the shutter pane, under <c>model</c> the allow pane (unless allowed for the session) and the app's
    /// own shot. A sent photo's path is remembered as the camera's.
    /// </summary>
    private async Task<CameraAnswer> ShootForModelAsync(string prompt, CancellationToken turnToken)
    {
        if (_cameraDeclined)
        {
            return new CameraAnswer.AlreadyDeclined();
        }

        if (!_pane.Enabled)
        {
            return new CameraAnswer.NoScreen();
        }

        var answer = CameraShutterMode.Resolve(_effective()) == CameraShutter.User
            ? await RunCameraPaneAsync(token => ShutterAsync(CameraText.ModelPaneTitle, prompt, CameraText.SendRow, token), new CameraAnswer.NoScreen(), turnToken).ConfigureAwait(false)
            : await ModelShutterAsync(prompt, turnToken).ConfigureAwait(false);
        if (answer is CameraAnswer.Declined or CameraAnswer.Denied)
        {
            _cameraDeclined = true;
        }

        if (answer is CameraAnswer.Shot shot)
        {
            _cameraPaths[shot.Photo.FullPath] = 0;
        }

        DiagnosticLog.Info(CameraCategory, "camera_capture: " + answer.GetType().Name);
        return answer;
    }

    private const string CameraCategory = "Camera";

    /// <summary>
    /// A camera pane mid-turn (the <see cref="AskUserAsync"/> shape): the body runs on the watcher with the keys, under a token
    /// linked to the turn's and the pane-close signal; <paramref name="fallback"/> when no watcher could run it (never asked)
    /// or the pane was closed under it.
    /// </summary>
    private async Task<T> RunCameraPaneAsync<T>(Func<CancellationToken, Task<T>> body, T fallback, CancellationToken turnToken)
    {
        var paneToken = _paneClose?.Token ?? CancellationToken.None;
        T result = fallback;
        var request = _keys.RequestPaneAsync(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(paneToken, turnToken);
            try
            {
                result = await body(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                result = fallback;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !linked.IsCancellationRequested)
            {
                DiagnosticLog.Error(ScreenPane.Category, "The camera pane failed: " + Llm.Assistant.Explain(ex), ex);
            }
        });
        try
        {
            await request.WaitAsync(turnToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!turnToken.IsCancellationRequested)
        {
            return fallback;
        }

        return result;
    }

    /// <summary>
    /// The shutter pane (the <c>user</c> shutter and <c>/camera</c>): the camera held from the pane's open, so the exposure
    /// settles while the user frames; Space, R or the take row takes a photo (a retake deletes the one before), the send row
    /// uses it, ESC declines and deletes it. A camera failure is the pane's status line; ESC after one is that failure.
    /// </summary>
    private async Task<CameraAnswer> ShutterAsync(string title, string prompt, string sendRow, CancellationToken cancellationToken)
    {
        CameraLease lease;
        try
        {
            lease = _camera.Acquire("shutter");
        }
        catch (CameraException e)
        {
            return new CameraAnswer.Failed(e.Message);
        }

        using var held = lease;
        using var preview = new CameraPreviewRun(this, CameraPreviewMode.Resolve(_effective()));
        preview.Start();
        CameraShot? shot = null;
        string? error = null;
        string? notice = null;
        try
        {
            while (true)
            {
                string status = shot is null ? CameraText.Waiting(_camera.Device?.Name) : CameraText.ShotStatus(shot, TimeZoneInfo.ConvertTime(shot.Frame.At, _time.LocalTimeZone));
                var view = new CameraPaneView(title, prompt, notice is null ? status : status + " " + notice, shot is not null, sendRow, error);
                notice = null;
                var action = await _cameraMenu.NextAsync(view, cancellationToken).ConfigureAwait(false);
                if (action == CameraPaneAction.Send && shot is not null)
                {
                    var kept = shot;
                    shot = null;
                    return new CameraAnswer.Shot(kept);
                }

                if (action == CameraPaneAction.Cancel)
                {
                    return error is not null && shot is null ? new CameraAnswer.Failed(error) : new CameraAnswer.Declined();
                }

                preview.Snapping();
                try
                {
                    var taken = await _cameraCapture.SnapAsync(lease, cancellationToken).ConfigureAwait(false);
                    _cameraCapture.Discard(shot);
                    shot = taken;
                    error = null;
                    preview.Shot(taken);
                    notice = _camera.TakeNotice();
                }
                catch (CameraException e)
                {
                    error = e.Message;
                }
            }
        }
        finally
        {
            _cameraCapture.Discard(shot);
            _cameraMenu.Close();
        }
    }

    /// <summary>
    /// The <c>model</c> shutter: the allow pane (Deny / once / the session, skipped once the session allowed it), then the
    /// app's own shot through the preview mode in force; the live view holds the shot a moment after.
    /// </summary>
    private async Task<CameraAnswer> ModelShutterAsync(string prompt, CancellationToken turnToken)
    {
        if (!_cameraAllowed)
        {
            var allow = await RunCameraPaneAsync(token => _cameraMenu.AllowAsync(prompt, token), null, turnToken).ConfigureAwait(false);
            if (allow is not { } choice)
            {
                return new CameraAnswer.NoScreen();
            }

            if (choice == CameraAllow.Deny)
            {
                return new CameraAnswer.Denied();
            }

            _cameraAllowed = choice == CameraAllow.Session;
        }

        CameraPreviewRun? preview = null;
        try
        {
            using var lease = _camera.Acquire("shutter");
            preview = new CameraPreviewRun(this, CameraPreviewMode.Resolve(_effective()));
            preview.Start();
            var shot = await _cameraCapture.SnapAsync(lease, turnToken).ConfigureAwait(false);
            preview.Shot(shot);
            return new CameraAnswer.Shot(shot);
        }
        catch (CameraException e)
        {
            return new CameraAnswer.Failed(e.Message);
        }
        finally
        {
            preview?.Hold(CameraPreviewRun.HeldFor, _time);
        }
    }

    /// <summary>A shot onto the input line: remembered as the camera's, its path replayed as a paste by the idle loop (an <c>[Image #N]</c> and a space).</summary>
    private void AttachShot(CameraShot shot)
    {
        _cameraPaths[shot.FullPath] = 0;
        // The path as a paste (the drop rule makes it the token), then a space, so what the user types next is a word apart.
        _attachReplay = [new InputEvent.Paste("\"" + shot.FullPath + "\""), new InputEvent.Key(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false))];
        _transcript.Notice(CameraText.Attached(shot.RelativePath));
    }

    /// <summary>The message's pictures with the camera's own marked as such (one attached from the line was loaded from its file).</summary>
    private IReadOnlyList<ImageAttachment> MarkCameraImages(IReadOnlyList<ImageAttachment> images) =>
        _cameraPaths.IsEmpty || !images.Any(i => _cameraPaths.ContainsKey(i.Path)) ? images
            : images.Select(i => _cameraPaths.ContainsKey(i.Path) ? i with { Camera = true } : i).ToList();

    /// <summary>
    /// <c>/camera [word …]</c> (2026-10-02): bare, the shutter pane, the photo onto the input line (a snap straight away without a
    /// pane); <c>snap</c>, <c>list</c>, <c>use</c>, <c>live</c>, <c>watch</c>, <c>off</c> (<see cref="CameraCommand"/>). The user's own
    /// hand: <c>Camera tool</c> never judges it.
    /// </summary>
    private async Task HandleCameraAsync(string args, CancellationToken cancellationToken)
    {
        var parsed = CameraCommand.Parse(args);
        switch (parsed.Verb)
        {
            case CameraVerb.Shutter when _pane.Enabled:
            {
                var answer = await ShutterAsync(CameraText.PaneTitle, CameraText.OwnPrompt, CameraText.AttachRow, cancellationToken).ConfigureAwait(false);
                if (answer is CameraAnswer.Shot shot)
                {
                    AttachShot(shot.Photo);
                }
                else if (answer is CameraAnswer.Failed failed)
                {
                    _transcript.Error(failed.Message);
                }

                return;
            }

            case CameraVerb.Shutter or CameraVerb.Snap:
                await SnapToLineAsync(cancellationToken).ConfigureAwait(false);
                return;
            case CameraVerb.List:
                await ListCamerasAsync(_transcript, cancellationToken).ConfigureAwait(false);
                return;
            case CameraVerb.Use:
                await UseCameraAsync(parsed.Argument, cancellationToken).ConfigureAwait(false);
                return;
            case CameraVerb.Live:
                StartLive();
                return;
            case CameraVerb.Watch:
                HandleWatch(parsed.Argument);
                return;
            case CameraVerb.Off:
                _transcript.Notice(CameraText.Off(CameraOff()));
                return;
            default:
                _transcript.Error(parsed.Error ?? CameraText.Usage);
                return;
        }
    }

    /// <summary><c>/camera snap</c>: a photo straight onto the input line, under a spinner.</summary>
    private async Task SnapToLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var lease = _camera.Acquire("snap");
            var shot = await _transcript.WithSpinnerAsync(CameraText.Taking, () => _cameraCapture.SnapAsync(lease, cancellationToken)).ConfigureAwait(false);
            if (CameraPreviewMode.Resolve(_effective()) != CameraPreview.Disabled)
            {
                ShowShotQuietly(shot.FullPath);
            }

            AttachShot(shot);
        }
        catch (CameraException e)
        {
            _transcript.Error(e.Message);
        }
    }

    /// <summary><c>/camera list</c>: the cameras Windows lists, numbered, the chosen one marked.</summary>
    private async Task ListCamerasAsync(INoticeSink sink, CancellationToken cancellationToken)
    {
        var lines = await CameraCommand.ListAsync(_camera, _effective().CameraDevice, cancellationToken).ConfigureAwait(false);
        foreach (var (text, error) in lines)
        {
            if (error)
            {
                sink.Error(text);
            }
            else
            {
                sink.Notice(text);
            }
        }
    }

    /// <summary><c>/camera use &lt;n|name&gt;</c>: the camera by its number in the list or its name, saved as <c>Camera device</c>; the open stream follows at its next open.</summary>
    private async Task UseCameraAsync(string what, CancellationToken cancellationToken)
    {
        IReadOnlyList<CameraDevice> devices;
        try
        {
            devices = await Task.Run(_camera.List, cancellationToken).ConfigureAwait(false);
        }
        catch (CameraException e)
        {
            _transcript.Error(e.Message);
            return;
        }

        if (CameraCommand.Find(devices, what) is not { } device)
        {
            _transcript.Error(CameraText.NoSuchCamera(what));
            return;
        }

        _settings.Update(d => d.CameraDevice = device.Name);
        _transcript.Notice(CameraText.Using(device.Name));
    }

    /// <summary><c>/camera live</c>: the camera live in the picture viewer until the window closes or <c>/camera off</c>.</summary>
    private void StartLive()
    {
        if (_liveView is null)
        {
            _transcript.Error(CameraText.NoViewer);
            return;
        }

        if (_liveLease is not null)
        {
            _transcript.Notice(CameraText.LiveOn);
            return;
        }

        try
        {
            _liveLease = _camera.Acquire("live", StopLive);
        }
        catch (CameraException e)
        {
            _transcript.Error(e.Message);
            return;
        }

        _livePreview = new CameraPreviewRun(this, CameraPreview.Live, StopLive);
        _livePreview.Start();
        _transcript.Notice(CameraText.LiveOn);
    }

    /// <summary>Ends <c>/camera live</c> (its window closed, or <c>/camera off</c>): the preview and the lease let go.</summary>
    private void StopLive()
    {
        var preview = Interlocked.Exchange(ref _livePreview, null);
        var lease = Interlocked.Exchange(ref _liveLease, null);
        preview?.Dispose();
        lease?.Dispose();
    }

    /// <summary><c>/camera off</c> (and the strip glyph's click): the live view and watch mode let go, the watch folder cleared; how many holds were.</summary>
    private int CameraOff()
    {
        int released = _liveLease is null ? 0 : 1;
        StopLive();
        released += StopWatch() ? 1 : 0;
        return released;
    }

    /// <summary>Watch mode stopped, any nudge forgotten, and the watch folder (<see cref="CameraWatch.FolderFor"/>) cleared; true when it was running.</summary>
    private bool StopWatch()
    {
        bool stopped = _cameraWatch.Stop();
        _watchNudge = false;
        ClearWatchFolder();
        return stopped;
    }

    /// <summary>The watch folder (<see cref="CameraWatch.FolderFor"/> the Camera output folder) as a full path; null when the sandbox refuses it (the temp folder is used then).</summary>
    private string? WatchFolderPath() =>
        _files.Resolve(CameraWatch.FolderFor(_effective().CameraOutputFolder), forWrite: true, out string full) == FileOutcome.Ok ? full : null;

    /// <summary>
    /// Deletes the watch folder (<see cref="CameraWatch.FolderFor"/>) and the watch pictures a double-click wrote there (2026-10-02,
    /// the user's call): when watch mode stops and when a profile loads. Nothing to do without one; a failure (a picture held open
    /// elsewhere) is only logged, and the next clear tries again.
    /// </summary>
    private void ClearWatchFolder()
    {
        if (WatchFolderPath() is not { } folder || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            Directory.Delete(folder, recursive: true);
            DiagnosticLog.Debug(CameraCategory, "Cleared " + folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(CameraCategory, $"Could not clear {folder}: {ex.Message}");
        }
    }

    /// <summary><c>/camera watch [seconds | off]</c>: watch mode on at the interval given (or the setting's), or off.</summary>
    private void HandleWatch(string argument)
    {
        var effective = _effective();
        if (string.Equals(argument, "off", StringComparison.OrdinalIgnoreCase))
        {
            _transcript.Notice(StopWatch() ? CameraText.WatchOff : CameraText.WatchNotOn);
            return;
        }

        int seconds = Math.Clamp(effective.CameraWatchSeconds, AppSettingsData.MinCameraWatchSeconds, AppSettingsData.MaxCameraWatchSeconds);
        if (argument.Length > 0)
        {
            if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) || seconds < AppSettingsData.MinCameraWatchSeconds || seconds > AppSettingsData.MaxCameraWatchSeconds)
            {
                _transcript.Error(CameraText.BadSeconds(argument));
                return;
            }
        }

        try
        {
            _cameraWatch.Start(seconds);
        }
        catch (CameraException e)
        {
            _transcript.Error(e.Message);
            return;
        }

        _transcript.Notice(CameraText.WatchOn(seconds, Math.Clamp(effective.CameraWatchThreshold, AppSettingsData.MinCameraWatchThreshold, AppSettingsData.MaxCameraWatchThreshold), effective.CameraWatchUnprompted));
    }

    /// <summary>A kept watch frame (on a pool thread): under <c>Camera watch speaks up</c> the idle read is nudged so the loop may show it to the model.</summary>
    private void OnWatchChanged()
    {
        if (_effective().CameraWatchUnprompted)
        {
            _watchNudge = true;
            SignalAlert();
        }
    }

    /// <summary>
    /// The unprompted watch turn's picture, when one may start now (the idle loop's top): a nudge set, <c>Camera watch speaks
    /// up</c> still on, the least gap passed, nothing on the input line and nothing speaking. Null otherwise; the frame then waits
    /// for the next message.
    /// </summary>
    private (ImageAttachment Image, DateTimeOffset At)? TakeWatchNudge()
    {
        if (!_watchNudge)
        {
            return null;
        }

        var effective = _effective();
        var now = _time.GetUtcNow();
        int gap = Math.Clamp(effective.CameraWatchMinGapSeconds, AppSettingsData.MinCameraWatchMinGapSeconds, AppSettingsData.MaxCameraWatchMinGapSeconds);
        if (!effective.CameraWatchUnprompted || now - _lastWatchNudge < TimeSpan.FromSeconds(gap) || _input.Chat.Text.Length > 0 || _speech.Playing is not null
            || _session.EmbeddedServer is { Vision: false })
        {
            _watchNudge = false;
            return null;
        }

        _watchNudge = false;
        if (_cameraWatch.TakePending() is not { } pending)
        {
            return null;
        }

        _lastWatchNudge = now;
        return pending;
    }

    /// <summary>The camera for a botchat (<c>Botchat camera</c>): held with a notice, or null with the failure's (the chat goes on without it).</summary>
    private CameraLease? AcquireBotCamera()
    {
        if (_session.EmbeddedServer is { Vision: false })
        {
            _transcript.Warning(CameraText.NoVision);
            return null;
        }

        try
        {
            var lease = _camera.Acquire("botchat");
            _transcript.Notice(CameraText.BotChatOn);
            return lease;
        }
        catch (CameraException e)
        {
            _transcript.Warning(CameraText.BotChatFailed(e.Message));
            return null;
        }
    }

    /// <summary>
    /// A bot's turn's picture of the user: a settled frame within <see cref="BotChat.CameraWait"/>, encoded at
    /// <see cref="BotChat.CameraMaxSide"/>, in memory only. A failure is told once and lets the camera go for the rest of the chat.
    /// </summary>
    private async Task<ImageAttachment?> BotCameraPictureAsync(CameraLease lease, CancellationToken cancellationToken)
    {
        try
        {
            var frame = await lease.NextFrameAsync(settled: true, cancellationToken).WaitAsync(BotChat.CameraWait, _time, cancellationToken).ConfigureAwait(false);
            return CameraJpeg.Attachment(frame, "the user's camera", BotChat.CameraMaxSide);
        }
        catch (Exception e) when (e is CameraException or TimeoutException)
        {
            _transcript.Warning(CameraText.BotChatFailed(e is CameraException ? e.Message : CameraText.Failure(CameraFailure.NoFrames, null)));
            lease.Dispose();
            return null;
        }
    }

    /// <summary>The picture viewer on a shot without the keyboard; a failure only logged.</summary>
    private void ShowShotQuietly(string path)
    {
        try
        {
            _showShot?.Invoke(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn(CameraCategory, "The picture viewer did not open on the shot: " + ex.Message);
        }
    }

    /// <summary>
    /// One preview of the camera for a shutter pane, a model's shot or <c>/camera live</c> (2026-10-02, <c>Camera preview</c>):
    /// <c>live</c> puts the frames in the picture viewer, mirrored as a mirror is, at most <see cref="LiveViewState.Fps"/> a
    /// second and <see cref="LiveViewState.MaxSide"/> on the longer side, and holds a shot taken still (as it is sent, not
    /// mirrored) until the next is taken; <c>post</c> opens the viewer on each shot taken; <c>disabled</c> shows nothing. The
    /// viewer never takes the keyboard. Disposing it ends the live picture.
    /// </summary>
    private sealed class CameraPreviewRun(ChatScreen screen, CameraPreview mode, Action? closed = null) : IDisposable
    {
        /// <summary>How long the live view holds the app's own shot (the <c>model</c> shutter) before it closes.</summary>
        public static readonly TimeSpan HeldFor = TimeSpan.FromSeconds(3);

        private ILiveView? _window;
        private IDisposable? _watch;
        private long _lastPost;
        private volatile bool _frozen;
        private string? _titled;
        private int _disposed;

        public void Start()
        {
            if (mode != CameraPreview.Live || screen._liveView is not { } open)
            {
                return;
            }

            try
            {
                _window = open(CameraText.LiveTitle(screen._camera.Device?.Name), () => closed?.Invoke());
                _watch = screen._camera.Watch(OnFrame);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DiagnosticLog.Warn(CameraCategory, "The live preview did not open: " + ex.Message);
            }
        }

        private void OnFrame(CameraFrame frame)
        {
            if (_window is not { Open: true } window || _frozen)
            {
                return;
            }

            long now = screen._time.GetTimestamp();
            if (_lastPost != 0 && !LiveViewState.Due(screen._time.GetElapsedTime(_lastPost, now)))
            {
                return;
            }

            _lastPost = now;
            if (screen._camera.Device?.Name is { } device && _titled != device)
            {
                _titled = device;
                window.Resume(CameraText.LiveTitle(device));
            }

            var (width, height) = CameraPixels.Fit(frame.Width, frame.Height, LiveViewState.MaxSide);
            var buffer = window.Rent(width * height * CameraPixels.BytesPerPixel);
            CameraPixels.ScaleInto(frame, width, height, mirror: true, buffer);
            window.Post(new ViewerBitmap(width, height, buffer));
        }

        /// <summary>A shot about to be taken: the live picture again (a retake over a held one).</summary>
        public void Snapping()
        {
            if (_window is { } window && _frozen)
            {
                _frozen = false;
                window.Resume(CameraText.LiveTitle(screen._camera.Device?.Name));
            }
        }

        /// <summary>A shot taken: held still in the live view, or the viewer opened on it.</summary>
        public void Shot(CameraShot shot)
        {
            switch (mode)
            {
                case CameraPreview.Live when _window is { } window:
                    _frozen = true;
                    var (width, height) = CameraPixels.Fit(shot.Frame.Width, shot.Frame.Height, LiveViewState.MaxSide);
                    var still = new byte[width * height * CameraPixels.BytesPerPixel];
                    CameraPixels.ScaleInto(shot.Frame, width, height, mirror: false, still);
                    window.Freeze(new ViewerBitmap(width, height, still), CameraText.HeldTitle(shot.Device));
                    break;
                case CameraPreview.Post:
                    screen.ShowShotQuietly(shot.FullPath);
                    break;
            }
        }

        /// <summary>Ends the preview after <paramref name="delay"/> (the held shot stays that long), at once without a window.</summary>
        public void Hold(TimeSpan delay, TimeProvider time)
        {
            _watch?.Dispose();
            _watch = null;
            if (_window is null)
            {
                Dispose();
                return;
            }

            _ = Task.Delay(delay, time).ContinueWith(_ => Dispose(), TaskScheduler.Default);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _watch?.Dispose();
            _window?.Dispose();
        }
    }
}
