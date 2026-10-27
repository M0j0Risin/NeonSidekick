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

    /// <summary>The kept file's path in the images folder; set under <see cref="HomeCameraFetches"/>' lock.</summary>
    public string? Saved { get; set; }
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

    /// <summary>The token the fetches run under now: cancelled by the next <see cref="Forget"/>.</summary>
    public CancellationToken Token
    {
        get
        {
            lock (_gate)
            {
                return _cts.Token;
            }
        }
    }

    /// <summary><paramref name="entityId"/>'s still: the fetch running, a picture under <see cref="Reuse"/> old, or a new fetch.</summary>
    public Task<HomeCameraStill> Get(string entityId)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        lock (_gate)
        {
            if (_fetches.TryGetValue(entityId, out var known)
                && (!known.IsCompleted || (known.IsCompletedSuccessfully && known.Result.Image is not null && time.GetUtcNow() - known.Result.At < Reuse)))
            {
                return known;
            }

            var token = _cts.Token;
            var started = Task.Run(() => FetchAsync(entityId, token), token);
            _fetches[entityId] = started;
            return started;
        }
    }

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
    public string? SaveOnce(HomeCameraStill still, Func<string?> save, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(still);
        ArgumentNullException.ThrowIfNull(save);
        lock (_gate)
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
    /// caller's (the watcher's token ends with the reply). Returns at once. The task never touches the screen: it queues a
    /// <see cref="HomeCameraShow"/> and wakes it (<see cref="WebNudge"/>).
    /// </summary>
    private void StartHomeCamera(HomeChip chip, string id)
    {
        lock (_homeGate)
        {
            if (_homePending.TryGetValue(chip.Reference, out var pending) && !pending.Failed)
            {
                return;
            }

            _homePending[chip.Reference] = new HomePending(_time.GetUtcNow(), [], [], Failed: false, Fetch: true);
            _homeModel++;
        }

        int generation = Volatile.Read(ref _homeCameraGeneration);
        _ = RunHomeCameraAsync(chip.Reference, chip.Name, id, generation, _homeCameras.Token);
    }

    private async Task RunHomeCameraAsync(string reference, string name, string id, int generation, CancellationToken token)
    {
        try
        {
            var still = await _homeCameras.Get(id).WaitAsync(token).ConfigureAwait(false);
            if (still.Image is not { } image)
            {
                HomeCameraFailed(reference, name, still.Error ?? "", generation);
                return;
            }

            string? error = null;
            string? saved = _homeCameras.SaveOnce(still, () =>
            {
                if (token.IsCancellationRequested)
                {
                    return null;
                }

                var save = HaCamera.Save(_images, image, id, _time.GetLocalNow());
                error = save.Error;
                if (save.Saved is { } path)
                {
                    _cameraPaths[path] = 0;   // a camera photo, should it reach the model through a mention
                }

                return save.Saved;
            }, path => _images.Resolve(path, forWrite: false, out string full) == FileOutcome.Ok && File.Exists(full));
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
            HomeCameraFailed(reference, name, ex.Message, generation);
        }
    }

    /// <summary>A still that didn't come: the chip marked failed (it clears after <see cref="HomeFailedShows"/>), the words flashed and logged; with the pane off, an error line.</summary>
    private void HomeCameraFailed(string reference, string name, string error, int generation)
    {
        if (Volatile.Read(ref _homeCameraGeneration) != generation)
        {
            return;
        }

        DiagnosticLog.Info(HaText.Category, "The home bar's still of " + name + " failed: " + error);
        lock (_homeGate)
        {
            if (_homePending.ContainsKey(reference))
            {
                _homePending[reference] = new HomePending(_time.GetUtcNow(), [], [], Failed: true, Fetch: true);
                _homeModel++;
            }
        }

        string words = HomeBarText.Failed(name, error);
        _pane.Flash(words);
        if (!_pane.Enabled)
        {
            // The flash shows nothing with the pane off: a typed /homebar tap would fail without a word.
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
                later.Add(show with { Notice = null });
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
