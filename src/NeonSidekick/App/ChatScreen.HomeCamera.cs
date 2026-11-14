using System.Collections.Concurrent;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Viewer;

namespace NeonSidekick.App;

// ── The home bar's cameras (2026-10-27, the ring-integration plan's step 4) ──────────────────────

/// <summary>
/// A camera's still as the home bar's chip (and, from step 5, the Web UI's sheet) share it: the stripped, fitted picture
/// (<see cref="HaCamera.ToAttachment"/>) or the failure's sentence, when it came, and the file the chip kept it in
/// (<see cref="HomeCameraFetches.SaveOnce"/>), so a second double-click inside <see cref="HomeCameraFetches.Reuse"/> opens that file
/// again rather than writing a <c>-2</c> copy of the same picture.
/// </summary>
internal sealed class HomeCameraStill(ImageAttachment? image, string? error, DateTimeOffset at)
{
    public ImageAttachment? Image { get; } = image;

    public string? Error { get; } = error;

    public DateTimeOffset At { get; } = at;

    /// <summary>The kept file's path in the images folder; set under <see cref="SaveGate"/>.</summary>
    public string? Saved { get; set; }

    /// <summary>
    /// The still's own lock for <see cref="HomeCameraFetches.SaveOnce"/> (2026-10-27, the review): the disk write runs under it, never under
    /// the fetches' lock, which the screen's task takes for every tap and the page's every ask.
    /// </summary>
    internal object SaveGate { get; } = new();
}

/// <summary>
/// One fetch per camera at a time (2026-10-27): whoever asks for a camera already being fetched shares that fetch, and a still that came
/// less than <see cref="Reuse"/> ago is answered again, so Home Assistant (and a Ring camera's ffmpeg) is asked once. The fetches run
/// under a token of their own, never a caller's, which <see cref="Forget"/> (a profile switch, the screen's end) cancels; each caller
/// waits with its own. The model's <c>ha_camera</c> fetches on its own, at the model's pace.
/// </summary>
internal sealed class HomeCameraFetches(Func<string, CancellationToken, Task<HaPictureReply>> fetch, TimeProvider time) : IDisposable
{
    /// <summary>How long a still is answered again rather than fetched afresh.</summary>
    public static readonly TimeSpan Reuse = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, Task<HomeCameraStill>> _fetches = new(StringComparer.Ordinal);
    private CancellationTokenSource _cts = new();

    /// <summary>Set by <see cref="Dispose"/>: every later ask answers cancelled, never with the disposed source's exception (2026-10-27, the review).</summary>
    private bool _disposed;

    private static readonly CancellationToken Cancelled = new(canceled: true);

    /// <summary>The token the fetches run under now: cancelled by the next <see cref="Forget"/>; a cancelled one once disposed.</summary>
    public CancellationToken Token
    {
        get
        {
            lock (_gate)
            {
                return _disposed ? Cancelled : _cts.Token;
            }
        }
    }

    /// <summary>
    /// <paramref name="entityId"/>'s still: the fetch running, a picture under <see cref="Reuse"/> old, or a new fetch; a cancelled task once
    /// disposed. A finished fetch past its reuse (or one that failed) is let go first, every camera's, so no still's bytes stay held for the
    /// session (2026-10-27, the review).
    /// </summary>
    public Task<HomeCameraStill> Get(string entityId)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromCanceled<HomeCameraStill>(Cancelled);
            }

            foreach (var spent in _fetches.Where(f => f.Value.IsCompleted && !Reusable(f.Value)).Select(f => f.Key).ToList())
            {
                _fetches.Remove(spent);
            }

            if (_fetches.TryGetValue(entityId, out var known))
            {
                return known;
            }

            var token = _cts.Token;
            var started = Task.Run(() => FetchAsync(entityId, token), token);
            _fetches[entityId] = started;
            return started;
        }
    }

    /// <summary>A finished fetch answered again: a picture under <see cref="Reuse"/> old.</summary>
    private bool Reusable(Task<HomeCameraStill> done) =>
        done.IsCompletedSuccessfully && done.Result.Image is not null && time.GetUtcNow() - done.Result.At < Reuse;

    private async Task<HomeCameraStill> FetchAsync(string entityId, CancellationToken token)
    {
        var reply = await fetch(entityId, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var (image, error) = HaCamera.ToAttachment(reply, entityId);
        return new HomeCameraStill(image, error, time.GetUtcNow());
    }

    /// <summary>
    /// The file <paramref name="still"/> is kept in: the one saved already, else <paramref name="save"/>'s (null for a save refused, tried
    /// again next time). A kept file <paramref name="exists"/> no longer finds is forgotten first (2026-10-27, <c>/ha stills purge</c>; also
    /// the picture viewer's delete or Explorer's), so a reuse saves a fresh copy rather than opening a deleted path; one check a reuse.
    /// </summary>
    public static string? SaveOnce(HomeCameraStill still, Func<string?> save, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(still);
        ArgumentNullException.ThrowIfNull(save);
        lock (still.SaveGate)
        {
            if (still.Saved is { } kept && exists is not null && !exists(kept))
            {
                still.Saved = null;
            }

            return still.Saved ??= save();
        }
    }

    /// <summary>Every fetch cancelled and forgotten: a still fetched for the last profile is never kept in the next one.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Cancelled before disposed: a fetch registering on the old token after this sees it cancelled and runs its callback at once.
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            _fetches.Clear();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            _cts.Dispose();
            _fetches.Clear();
        }
    }
}

internal sealed partial class ChatScreen
{
    /// <summary>The stills the chips and the page share (<see cref="HomeCameraFetches"/>).</summary>
    private readonly HomeCameraFetches _homeCameras;

    /// <summary>What a chip's fetch has for the screen, waiting for the screen's task (<see cref="ShowHomeCameras"/>).</summary>
    private readonly ConcurrentQueue<HomeCameraShow> _homeCameraShows = new();

    /// <summary>Moves with each <see cref="HomeCameraForget"/>: a show queued before a profile switch is dropped.</summary>
    private int _homeCameraGeneration;

    /// <summary>A chip's fetch done: the notice, or the failure's words (an error line with the pane off), the picture and the file to open.</summary>
    private sealed record HomeCameraShow(int Generation, string? Notice, string? Error, ImageAttachment? Picture, string? OpenPath);

    /// <summary>
    /// A camera chip's double-click, <c>/homebar tap</c> or <c>/homebar show</c> (2026-10-27): the chip marked pending (a camera already
    /// fetching is left alone) and its still fetched on a task of its own, under <see cref="HomeCameraFetches.Token"/> rather than the
    /// caller's (the watcher's token ends with the reply). Returns at once, false when the camera was fetching already. The task never touches
    /// the screen: it queues a <see cref="HomeCameraShow"/> and wakes it (<see cref="WebNudge"/>). The still is kept in the images folder of the
    /// profile loaded at the tap (2026-10-27, the review): a profile switch moves the settings before it forgets the fetches, and a save in
    /// between would otherwise land in the new profile.
    /// <para>Since 2026-11-13 <c>/ha camera</c> starts it too, for any camera (<paramref name="reference"/> its <c>entity:</c> reference, on the
    /// bar or not). <paramref name="comingLine"/> is the command's when no chip shows the wait: the <see cref="HomeBarText.CameraComing"/>
    /// notice is queued first, through the same queue as the still's, so "fetching" always prints before "saved" — unless the still is one
    /// reused from the last <see cref="HomeCameraFetches.Reuse"/> (the review of 2026-11-13: nothing is fetched then). Whether a failure is
    /// a line is decided when it fails (<see cref="HomeCameraFailed"/>), so a notice of either kind never goes unanswered.</para>
    /// </summary>
    private bool StartHomeCamera(string reference, string name, string id, bool comingLine)
    {
        lock (_homeGate)
        {
            if (_homePending.TryGetValue(reference, out var pending) && !pending.Failed)
            {
                return false;
            }

            _homePending[reference] = new HomePending(_time.GetUtcNow(), [], [], Failed: false, Fetch: true);
            _homeModel++;
        }

        int generation = Volatile.Read(ref _homeCameraGeneration);
        var fetch = _homeCameras.Get(id);
        if (comingLine && !(fetch.IsCompletedSuccessfully && fetch.Result.Image is not null))
        {
            QueueHomeCamera(new HomeCameraShow(generation, HomeBarText.CameraComing(name), null, null, null));
        }

        string root = _settings.ProfileImagesDirectory;
        var images = new WorkingDirectory(() => root, _time, WorkingDirectoryOptions.Images);
        _ = RunHomeCameraAsync(reference, name, id, fetch, images, generation, comingLine, _homeCameras.Token);
        return true;
    }

    /// <summary>The fetch's wait, save and notice. <paramref name="comingLine"/> as <see cref="StartHomeCamera"/>'s: its failure is a line too.</summary>
    private async Task RunHomeCameraAsync(string reference, string name, string id, Task<HomeCameraStill> fetch, WorkingDirectory images, int generation, bool comingLine, CancellationToken token)
    {
        try
        {
            var still = await fetch.WaitAsync(token).ConfigureAwait(false);
            if (still.Image is not { } image)
            {
                HomeCameraFailed(reference, name, still.Error ?? "", generation, comingLine);
                return;
            }

            string? error = null;
            string? saved = HomeCameraFetches.SaveOnce(still, () =>
            {
                if (token.IsCancellationRequested)
                {
                    return null;
                }

                var save = HaCamera.Save(images, image, id, _time.GetLocalNow());
                error = save.Error;
                if (save.Saved is not null)
                {
                    // A camera photo, should it reach the model through a mention: by the picture's own path, as ha_camera's still is kept
                    // (2026-11-13, the review; the images folder names it in full either way).
                    _cameraPaths[save.Image.Path] = 0;
                }

                return save.Saved;
            }, path => images.Resolve(path, forWrite: false, out string full) == FileOutcome.Ok && File.Exists(full));
            token.ThrowIfCancellationRequested();
            lock (_homeGate)
            {
                _homePending.Remove(reference);
                _homeModel++;
            }

            QueueHomeCamera(new HomeCameraShow(generation, HomeBarText.CameraShown(name, saved, error), null, saved is null ? image : image with { Path = saved }, saved));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Forgotten (a profile switch, the screen's end): nothing is kept or shown.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            HomeCameraFailed(reference, name, ex.Message, generation, comingLine);
        }
    }

    /// <summary>
    /// A still that didn't come, logged. Where a chip shows the wait now (<see cref="HomeChipShowsWait"/>), the chip marked failed (it clears
    /// after <see cref="HomeFailedShows"/>) and the words flashed; anywhere else an error line, no flash, and the mark taken off, since no
    /// chip would show it. Decided when it fails (2026-11-13, the review), not when it started: the pane off, a short window, a chip paged
    /// away meanwhile, a camera off the bar (<c>/ha camera</c>), or a command that joined a chip's fetch all get the line. One whose
    /// <see cref="HomeBarText.CameraComing"/> line was printed (<paramref name="comingLine"/>) always gets it (2026-11-14, the review's #41):
    /// a chip drawn meanwhile (the bar turned on) flashes too, but the line answers the line.
    /// </summary>
    private void HomeCameraFailed(string reference, string name, string error, int generation, bool comingLine)
    {
        if (Volatile.Read(ref _homeCameraGeneration) != generation)
        {
            return;
        }

        DiagnosticLog.Info(HaText.Category, "The still of " + name + " failed: " + error);
        bool shown = HomeChipShowsWait(reference);
        lock (_homeGate)
        {
            if (_homePending.ContainsKey(reference))
            {
                if (shown)
                {
                    _homePending[reference] = new HomePending(_time.GetUtcNow(), [], [], Failed: true, Fetch: true);
                }
                else
                {
                    _homePending.Remove(reference);
                }

                _homeModel++;
            }
        }

        string words = HomeBarText.Failed(name, error);
        if (shown)
        {
            _pane.Flash(words);
        }

        if (!shown || comingLine)
        {
            QueueHomeCamera(new HomeCameraShow(generation, null, words, null, null));
        }
    }

    private void QueueHomeCamera(HomeCameraShow show)
    {
        if (Volatile.Read(ref _homeCameraGeneration) != show.Generation)
        {
            return;
        }

        _homeCameraShows.Enqueue(show);
        WebNudge();
    }

    /// <summary>
    /// The chips' stills, on the screen's task (the idle line's top, <paramref name="idle"/>; the turn's act drain under a reply): each notice,
    /// the picture opened in the picture window where there is one, else drawn in the transcript — at the idle line only, so a full-height
    /// picture never wedges into a streaming reply; under a reply it waits, its notice written at once.
    /// </summary>
    private void ShowHomeCameras(bool idle)
    {
        if (_homeCameraShows.IsEmpty)
        {
            return;
        }

        int generation = Volatile.Read(ref _homeCameraGeneration);
        var later = new List<HomeCameraShow>();
        while (_homeCameraShows.TryDequeue(out var show))
        {
            if (show.Generation != generation)
            {
                continue;
            }

            if (show.Error is { } error)
            {
                _transcript.Error(error);
                continue;
            }

            if (show.Notice is { } notice)
            {
                _transcript.Notice(notice);
            }

            if (show.Picture is not { } picture)
            {
                continue;
            }

            if (_viewPicture is { } view && show.OpenPath is { } path)
            {
                try
                {
                    view(path);
                    PointAt(path);
                    continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or ArgumentException)
                {
                    _transcript.Error(ViewerText.Failed(path, ex.Message));
                }
            }

            if (idle)
            {
                ShowPictures([picture]);
            }
            else
            {
                // Its window is tried once: a viewer that threw isn't asked again at every drain (2026-10-27, the review).
                later.Add(show with { Notice = null, OpenPath = null });
            }
        }

        foreach (var show in later)
        {
            _homeCameraShows.Enqueue(show);
        }
    }

    /// <summary>
    /// A profile switch or the screen's end (2026-10-27): the fetches cancelled and forgotten, the shows waiting dropped, the chips' fetch
    /// marks cleared, so a still fetched for the last profile is never saved into the new one or shown in its transcript.
    /// </summary>
    private void HomeCameraForget()
    {
        Interlocked.Increment(ref _homeCameraGeneration);
        _homeCameras.Forget();
        _homeCameraShows.Clear();
        lock (_homeGate)
        {
            foreach (var reference in _homePending.Where(p => p.Value.Fetch).Select(p => p.Key).ToList())
            {
                _homePending.Remove(reference);
                _homeModel++;
            }
        }
    }
}
