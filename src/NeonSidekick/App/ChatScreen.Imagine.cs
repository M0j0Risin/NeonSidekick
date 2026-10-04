using NeonSidekick.Comfy;
using NeonSidekick.Files;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── /imagine (2026-09-24; behind the input line since 2026-10-04) ─────────

internal sealed partial class ChatScreen
{
    // What /imagine made since the last message (2026-09-24): the result lines and the pictures, handed to the model with the next one.
    private readonly List<string> _imagineNotes = [];
    private readonly List<ImageAttachment> _imagineImages = [];

    /// <summary>
    /// The <c>/imagine</c> generations left behind the input line (2026-10-04), oldest first. The loop's alone: added by
    /// <see cref="HandleImagineAsync"/>, drawn and removed by <see cref="DrainImagine"/>, read by <see cref="ImagineReady"/>;
    /// the pool only nudges the read when one ends.
    /// </summary>
    private readonly List<(ComfyRequest Request, Task<ComfyGeneration> Job)> _imagining = [];

    /// <summary>
    /// <c>/imagine [workflow] &lt;prompt&gt; [-- &lt;negative&gt;] [--seed N] …</c> (2026-09-24, the user's ask: their own prompts —
    /// <c>score_9, score_8_up, …</c> — sent as typed, no model in between): the prompt straight to ComfyUI through the same
    /// engine as <c>generate_image</c> (<see cref="ComfyStudio"/>), the picture drawn as large as the window allows (several as a
    /// strip), the result line a notice. The result and the pictures ride with the next message (<see cref="TakeImagineNotes"/>),
    /// so the model knows what was made and can look at it. Refused mid-turn (it waits for the idle line); not headless.
    ///
    /// <para>Behind the input line since 2026-10-04 (the user's report: while it ran, most keys and clicks waited for it — it ran
    /// under a spinner whose watcher held every line, command and chord for the idle line). The generation is counted on the hint
    /// row's strip (<see cref="PendingPictures"/>, the 🖼️ / 🎨 with its count, as a <c>/botchat</c> picture is) and given
    /// <see cref="BackgroundJobs.Grace"/>, <see cref="BackgroundJobs"/>' rule: one done by then is drawn here, as before (a
    /// ComfyUI that refuses at once still answers at once); a longer one leaves the line to the user with
    /// <see cref="ComfyText.ImagineInBackground"/>, and <see cref="DrainImagine"/> draws it at the loop top when it ends, in the
    /// order they were sent. ESC no longer reaches it (the idle line owns ESC); the strip's double-click
    /// (<see cref="DrainPictures"/>) cancels it with every other picture being made. A message sent before it ends goes without
    /// it; it rides the one after.</para>
    /// </summary>
    private async Task HandleImagineAsync(string args, CancellationToken cancellationToken)
    {
        var (request, error) = ComfyImagine.Parse(args, _comfy.Catalog.Workflows, ComfyStudio.MaxCountOf(_effective()));
        if (request is null)
        {
            _transcript.Error(error!);
            return;
        }

        var job = _pendingPictures.TrackAsync(request.ImageCount, () => _comfy.GenerateAsync(request, cancellationToken));
        if (!job.IsCompleted)
        {
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Task.WhenAny(job, Task.Delay(BackgroundJobs.Grace, _time, grace.Token)).ConfigureAwait(false);
            grace.Cancel();
        }

        // In line only with nothing ahead of it: an earlier one still running keeps its place.
        if (job.IsCompleted && _imagining.Count == 0)
        {
            ShowImagineEnd(request, job);
            DrainDiagnostics();
            return;
        }

        if (!job.IsCompleted)
        {
            _transcript.Notice(ComfyText.ImagineInBackground);
        }

        _imagining.Add((request, job));
        // The pool only nudges the idle read; the loop top draws (DrainImagine). Ended already, it runs at once.
        _ = job.ContinueWith(static (_, state) => ((ChatScreen)state!).SignalAlert(), this, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Whether the oldest background <c>/imagine</c> has ended: the idle read's arm-time check, so one that ended before the read armed is not left waiting.</summary>
    private bool ImagineReady => _imagining.Count > 0 && _imagining[0].Job.IsCompleted;

    /// <summary>
    /// The background <c>/imagine</c>s that have ended, drawn oldest first at the loop top (2026-10-04); one still running holds
    /// back those after it, so they keep the order they were sent in (<see cref="ShowReadyBotPictures"/>' rule).
    /// </summary>
    private void DrainImagine()
    {
        while (ImagineReady)
        {
            var (request, job) = _imagining[0];
            _imagining.RemoveAt(0);
            ShowImagineEnd(request, job);
        }
    }

    /// <summary>
    /// An ended <c>/imagine</c>'s lines: its result (<see cref="ShowImagineResult"/>), or a failure as an error. A cancelled one
    /// says nothing: the strip's drain (<see cref="DrainPictures"/>) said so itself, and the app's end needs no line.
    /// </summary>
    private void ShowImagineEnd(ComfyRequest request, Task<ComfyGeneration> job)
    {
        if (job.IsCompletedSuccessfully)
        {
            _ = ShowImagineResult(request, job.Result, loopBase: null);
        }
        else if (job.IsFaulted)
        {
            _transcript.Error("Error: " + (job.Exception!.InnerExceptions.Count == 1 ? job.Exception.InnerException! : job.Exception).Message);
        }
    }

    /// <summary>
    /// One looped <c>/imagine</c> pass (2026-09-25, the user's ask: <c>/loop infinite 1s /imagine …</c> with no model in
    /// between): the generation in the foreground under a spinner ESC cancels, so the loop knows each pass's end. Under the
    /// reply's own watch since 2026-10-04 (<see cref="UnderWatchAsync{T}"/>; the user's report, with the plain one's: the watch
    /// held every line, command and chord until the loop stopped): a pane opens, a message is queued for after the loop, a
    /// quick command runs at the pass's end, and Ctrl+Alt+X cancels as ESC does.
    ///
    /// <para><paramref name="loopBase"/> is how much was queued when the loop began: each pass trims the queue back to it first, so
    /// the next message carries the last pass's pictures alone, never an infinite loop's pile. Every picture is still drawn,
    /// saved and put in the strip. The outcome tells the loop whether to go on.</para>
    /// </summary>
    private async Task<LoopPass> ImaginePassAsync(string args, (int Notes, int Images) loopBase, CancellationToken cancellationToken)
    {
        var (request, error) = ComfyImagine.Parse(args, _comfy.Catalog.Workflows, ComfyStudio.MaxCountOf(_effective()));
        if (request is null)
        {
            _transcript.Error(error!);
            return LoopPass.Failed;
        }

        ComfyGeneration? generation;
        bool cancelled;
        try
        {
            (generation, cancelled) = await UnderWatchAsync(ComfyText.GeneratingLabelFor(request.ImageCount), token => _comfy.GenerateAsync(request, token), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The hint row's double-click (2026-09-28, ComfyStudio.Drain): the watch's own cancel is caught inside, and the drain's notice says so.
            return LoopPass.Cancelled;
        }

        if (cancelled || generation is null)
        {
            _transcript.Notice(ComfyText.Cancelled);
            return LoopPass.Cancelled;
        }

        return ShowImagineResult(request, generation, loopBase);
    }

    /// <summary>
    /// A generation's lines (2026-09-24): a failure as an error, else the result lines, the picture strip, the pictures, and the
    /// note queued for the next message — trimmed back to <paramref name="loopBase"/> first under <c>/loop</c>.
    /// </summary>
    private LoopPass ShowImagineResult(ComfyRequest request, ComfyGeneration generation, (int Notes, int Images)? loopBase)
    {
        if (!generation.Ok)
        {
            _transcript.Error(generation.Text);
            return LoopPass.Failed;
        }

        ComfyLines(generation.Text, line => _transcript.Notice(ComfyText.GlyphFor(request.ImageCount) + line));
        // The strip first: the picture's window box then leaves its rows.
        AddToPictureStrip(generation.Images);
        ShowPictures(generation.Images);
        if (loopBase is { } keep)
        {
            // The last looped pass's batch goes; what was queued before the loop stays.
            TrimTo(_imagineNotes, keep.Notes);
            TrimTo(_imagineImages, keep.Images);
        }

        _imagineNotes.Add(ComfyText.ImagineNote(generation.Text));
        _imagineImages.AddRange(generation.Images);
        return LoopPass.Ok;

        static void TrimTo<T>(List<T> list, int count)
        {
            if (list.Count > count)
            {
                list.RemoveRange(count, list.Count - count);
            }
        }
    }

    /// <summary>
    /// The box of a picture as large as the transcript allows: <see cref="ThumbnailSize.Fit"/> over the console's size less
    /// the pane's rows. <c>/view</c>'s, a lone <c>/imagine</c> picture's, and every thumbnail's under <c>fullsize</c> (2026-09-24).
    /// </summary>
    private ThumbnailBox WindowBox() =>
        ThumbnailSize.Fit(_pane.Profile.Width, _pane.Profile.Height, _pane.Enabled ? ScreenPane.PaneRows + _pane.InputRows + _pane.ToolbarRows + _pane.PerfRows + _pane.StripRows : 0);

    /// <summary>One picture as large as the window allows (the <c>/view</c> box), several as a thumbnail strip.</summary>
    private void ShowPictures(IReadOnlyList<ImageAttachment> images)
    {
        if (images.Count == 1)
        {
            var box = WindowBox();
            if (ImageThumbnail.Read(images[0], box.Columns, box.MaxRows) is { } picture)
            {
                _transcript.Picture(picture, RegisterPicture(images[0], sandbox: true));
            }

            return;
        }

        var (tiles, ids) = ReadThumbnails(images, ThumbnailSize.Resolve(_effective(), WindowBox()), sandbox: true);
        _transcript.Images(tiles, ids);
    }

    /// <summary>The message as the model gets it (2026-09-24): the <c>/imagine</c> notes since the last one ahead of the text, their pictures after the user's own; then the notes are spent.</summary>
    private (string Text, IReadOnlyList<ImageAttachment> Images) TakeImagineNotes(string text, IReadOnlyList<ImageAttachment> images)
    {
        if (_imagineNotes.Count == 0)
        {
            return (text, images);
        }

        string notes = string.Join("\n", _imagineNotes);
        IReadOnlyList<ImageAttachment> all = [.. images, .. _imagineImages];
        _imagineNotes.Clear();
        _imagineImages.Clear();
        return (notes + "\n\n" + text, all);
    }

    /// <summary>
    /// The wait between two passes of a looped command (2026-10-04, the user's report: the pointer watch held every line, command
    /// and chord until the loop stopped): the delay on the screen's clock under the reply's own watch (<see cref="StartReplyWatch"/>)
    /// — a pane opens, a message is queued for after the loop, a quick command runs at the wait's end, a cancelling one
    /// (<c>/clear</c>, <c>/new</c>) stops the loop and then runs. ESC, Ctrl+C and Ctrl+Alt+X stop it. True when it was cancelled
    /// that way, not by <paramref name="cancellationToken"/>.
    /// </summary>
    private async Task<bool> LoopWaitAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stop = new CancellationTokenSource();
        using var killScope = BeginKillScope(cts);
        var watcher = StartReplyWatch(cts, stop.Token, cancellationToken);
        try
        {
            await Task.Delay(wait, _time, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            stop.Cancel();
            await watcher.ConfigureAwait(false);
            await EndTurnAsync(closePane: cts.IsCancellationRequested, cancellationToken).ConfigureAwait(false);
            DrainDiagnostics();
        }

        return cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
    }
}
