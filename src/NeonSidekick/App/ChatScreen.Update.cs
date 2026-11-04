using System.Collections.Concurrent;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Speech;
using NeonSidekick.UI;
using NeonSidekick.Updates;

namespace NeonSidekick.App;

// ── /update (2026-11-04, self-update) ───────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>A start's answer is used this long; after it <c>/update</c> asks again, so a session left open can't install a release pulled since, or miss a newer one.</summary>
    internal static readonly TimeSpan UpdateAnswerKept = TimeSpan.FromHours(1);

    /// <summary>The door to the updater: <see cref="AppUpdater"/> in the app, a fake in tests, none in headless and check modes.</summary>
    private readonly IUpdater? _updater;

    // The check and the stage run in the background, so their lines wait here for the idle line: they never break into a streaming reply. A reply to /update typed mid-reply is a Quick command's notice instead.
    private readonly ConcurrentQueue<(string Text, bool Error)> _updateLines = new();
    private readonly CancellationTokenSource _updateStop = new();
    private UpdateScreenState _updateState = UpdateScreenState.Nothing;
    private UpdateCheckResult? _updateAnswer;
    private DateTimeOffset _updateAnsweredAt;
    private CancellationTokenSource? _updateWorkStop;
    private Task? _updateWork;
    private int _updateAsked;

    /// <summary>The marker on the lower rule. The "is out" one follows the current profile's setting; what's under way always shows.</summary>
    private string UpdateMarkerNow() => UpdateText.Marker(Volatile.Read(ref _updateState), _effective().CheckForUpdatesAtStart);

    /// <summary>The start: the step's line, what's on disk, and the check in the background when the profile loaded now asks for it. It never delays startup, and a failure stays silent.</summary>
    private void BeginUpdateAtStart()
    {
        if (_updater is not { } updater)
        {
            return;
        }

        if (updater.StartLine is { Length: > 0 } said)
        {
            foreach (string line in said.Split('\n'))
            {
                _updateLines.Enqueue((line, false));
            }
        }

        RefreshUpdateFromDisk(updater);
        if (_effective().CheckForUpdatesAtStart && updater.IsPackage)
        {
            _ = CheckAtStartAsync(updater, _updateStop.Token);
        }
    }

    private async Task CheckAtStartAsync(IUpdater updater, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            var found = await updater.CheckAsync(cancellationToken).ConfigureAwait(false);

            // One that answers after /update has begun is dropped: /update asked for itself, and a late "is out" can't land under a running download's 42%.
            if (Volatile.Read(ref _updateAsked) != 0)
            {
                return;
            }

            if (found.Kind != UpdateCheckKind.Newer || found.Release is not { } release)
            {
                DiagnosticLog.Info(UpdateJournal.Category, found.Reason ?? found.Kind.ToString());
                return;
            }

            var state = Volatile.Read(ref _updateState);
            if (state is UpdateScreenState.Ready ready && UpdateVersion.TryParse(ready.Version, out var staged) && release.Version <= staged)
            {
                return;   // what's staged is what's out: no line, and the marker already says "at quit"
            }

            _updateAnswer = found;
            _updateAnsweredAt = _time.GetUtcNow();
            string version = release.Version.ToString();
            if (state is UpdateScreenState.None or UpdateScreenState.Offered)
            {
                Volatile.Write(ref _updateState, new UpdateScreenState.Offered(version));
            }

            _updateLines.Enqueue((UpdateText.IsOutLine(version), false));
            WebNudge();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Info(UpdateJournal.Category, ex.Message);
        }
    }

    /// <summary>The queued lines, on the screen's task: the idle line's top alone, never under a reply.</summary>
    private void PrintUpdateLines()
    {
        while (_updateLines.TryDequeue(out var line))
        {
            if (line.Error)
            {
                _transcript.Error(line.Text);
            }
            else
            {
                _transcript.Notice(line.Text);
            }
        }
    }

    /// <summary>The screen's end: a running probe is killed and waited for and the download canceled with its partial file kept, so nothing installs and the next <c>/update</c> resumes it.</summary>
    private async Task EndUpdateWorkAsync()
    {
        await _updateStop.CancelAsync().ConfigureAwait(false);
        if (_updateWork is { } work)
        {
            try
            {
                await work.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                DiagnosticLog.Info(UpdateJournal.Category, ex.Message);
            }
        }

        _updateStop.Dispose();
    }

    // /update reads .update/ before anything else, so a copy already open when another staged or asked for a rollback sees it, updates its marker and never downloads the same version again.
    private void RefreshUpdateFromDisk(IUpdater updater)
    {
        var state = Volatile.Read(ref _updateState);
        if (state is UpdateScreenState.Downloading or UpdateScreenState.Checking)
        {
            return;
        }

        var disk = updater.ReadDisk();
        UpdateScreenState next = disk.RollbackTarget is { } target ? new UpdateScreenState.GoingBack(target)
            : disk.ReadyVersion is { } ready ? new UpdateScreenState.Ready(ready)
            : state switch
            {
                UpdateScreenState.Ready gone => new UpdateScreenState.Offered(gone.Version),
                UpdateScreenState.GoingBack => UpdateScreenState.Nothing,
                _ => state,
            };
        Volatile.Write(ref _updateState, next);
    }

    /// <summary><c>/update</c> and its forms. Never a tool, and never reachable by the model; on the page only <c>notes</c> runs (<see cref="WebCommands"/>).</summary>
    private async Task UpdateCommandAsync(string args, INoticeSink sink, CancellationToken cancellationToken, bool onWatcher = false)
    {
        string word = args.Trim().ToLowerInvariant();
        if (word.Length > 0 && word is not (UpdateText.NotesWord or UpdateText.RollbackWord or UpdateText.CancelWord))
        {
            sink.Error(UpdateText.Usage);
            return;
        }

        Volatile.Write(ref _updateAsked, 1);
        if (_updater is not { } updater || !updater.IsPackage)
        {
            if (word == UpdateText.NotesWord && _updater is { } other)
            {
                ShowUpdateNotes(other, sink);
                return;
            }

            sink.Notice(UpdateText.NotAPackage);
            return;
        }

        switch (word)
        {
            case UpdateText.NotesWord:
                ShowUpdateNotes(updater, sink);
                break;
            case UpdateText.RollbackWord:
                await UpdateRollbackAsync(updater, sink, cancellationToken).ConfigureAwait(false);
                break;
            case UpdateText.CancelWord:
                await UpdateCancelAsync(updater, sink).ConfigureAwait(false);
                break;
            default:
                await UpdateOpenAsync(updater, sink, onWatcher, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private static void ShowUpdateNotes(IUpdater updater, INoticeSink sink)
    {
        if (updater.RunningNotes() is not { Length: > 0 } notes)
        {
            sink.Notice(UpdateText.NoNotesSaved(updater.Running));
            return;
        }

        sink.Notice(UpdateText.NotesHeading(updater.Running));
        foreach (string line in UntrustedText.Scrub(notes).Split('\n'))
        {
            sink.Notice(line);
        }
    }

    private async Task UpdateOpenAsync(IUpdater updater, INoticeSink sink, bool onWatcher, CancellationToken cancellationToken)
    {
        RefreshUpdateFromDisk(updater);
        var state = Volatile.Read(ref _updateState);
        switch (state)
        {
            case UpdateScreenState.Downloading or UpdateScreenState.Checking or UpdateScreenState.GoingBack:
                await OfferUpdateCancelAsync(updater, state, sink, cancellationToken).ConfigureAwait(false);
                return;
            case UpdateScreenState.Ready ready:
                // Asks GitHub first. A newer release gets the usual pane and Install replaces the stage; otherwise, or with no network, it offers to cancel the stage.
                var asked = await AskReleaseAsync(updater, onWatcher, cancellationToken).ConfigureAwait(false);
                if (asked is { Kind: UpdateCheckKind.Newer, Release: { } newer } && UpdateVersion.TryParse(ready.Version, out var staged) && newer.Version > staged)
                {
                    await OfferUpdateInstallAsync(updater, newer, sink, cancellationToken).ConfigureAwait(false);
                }
                else if (asked is not null)
                {
                    await OfferUpdateCancelAsync(updater, state, sink, cancellationToken).ConfigureAwait(false);
                }

                return;
        }

        var answer = _updateAnswer is { } kept && _time.GetUtcNow() - _updateAnsweredAt < UpdateAnswerKept
            ? kept
            : await AskReleaseAsync(updater, onWatcher, cancellationToken).ConfigureAwait(false);
        switch (answer)
        {
            case null:
                return;   // stopped with Esc
            case { Kind: UpdateCheckKind.Newer, Release: { } release }:
                await OfferUpdateInstallAsync(updater, release, sink, cancellationToken).ConfigureAwait(false);
                return;
            case { Kind: UpdateCheckKind.Latest }:
                sink.Notice(UpdateText.LatestLine(updater.Running));
                return;
            case { Kind: UpdateCheckKind.Unsigned }:
                sink.Error(UpdateText.UnsignedLine(answer.Found.ToString()));
                return;
            case { Kind: UpdateCheckKind.NoPackage }:
                sink.Error(UpdateText.NoPackageLine(answer.Found.ToString()));
                return;
            default:
                sink.Error(UpdateText.CheckFailedLine(answer.Reason ?? UpdateText.AnswerUnreadable));
                return;
        }
    }

    // Up to the check's own 10 s limit, on the spinner row with Esc stopping it, as /net does. Under a reply the keys are the watch's. Null when stopped.
    private async Task<UpdateCheckResult?> AskReleaseAsync(IUpdater updater, bool onWatcher, CancellationToken cancellationToken)
    {
        UpdateCheckResult? answer = null;
        if (onWatcher)
        {
            answer = await updater.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            bool stopped = await WaitUnderWatchAsync(token => _transcript.WithSpinnerAsync(UpdateText.CheckingSpinner, async () =>
            {
                answer = await updater.CheckAsync(token).ConfigureAwait(false);
                return true;
            }), KeySource.IsTurnCancel, cancellationToken).ConfigureAwait(false);
            if (stopped)
            {
                return null;
            }
        }

        if (answer is { Kind: UpdateCheckKind.Newer })
        {
            _updateAnswer = answer;
            _updateAnsweredAt = _time.GetUtcNow();
        }

        return answer;
    }

    private async Task OfferUpdateInstallAsync(IUpdater updater, UpdateRelease release, INoticeSink sink, CancellationToken cancellationToken)
    {
        string version = release.Version.ToString();
        string size = ModelStore.SizeLabel(release.ArchiveBytes);
        string notes = release.Notes is { Length: > 0 } raw ? UntrustedText.Scrub(raw).Trim() : UpdateText.NoNotesInRelease;
        if (Volatile.Read(ref _updateState) is UpdateScreenState.None)
        {
            Volatile.Write(ref _updateState, new UpdateScreenState.Offered(version));
        }

        if (!_pane.Enabled)
        {
            // With no pane to ask on, ShowAsync returns at once with nothing pressed, which would read as Later. A stage ready from an earlier run still installs at exit.
            sink.Notice(UpdateText.PaneHeading(version, size));
            foreach (string line in notes.Split('\n'))
            {
                sink.Notice(line);
            }

            sink.Notice(UpdateText.CantAskHere);
            return;
        }

        string body = UpdateText.PaneHeading(version, size) + "\n\n" + notes + "\n\n" + release.PageUrl;
        var tab = new InfoTab(UpdateText.PaneTab, () => new Spectre.Console.Text(body))
        {
            LineDoubleClicked = line => OpenUpdatePage(release.PageUrl, line, sink),
        };
        await _info.ShowAsync(UpdateText.PaneLabel, [tab], 0, cancellationToken, [UpdateText.InstallAction, UpdateText.LaterAction], noFind: true).ConfigureAwait(false);
        if (_info.Pressed == UpdateText.InstallAction)
        {
            BeginUpdateStage(updater, release, sink);
        }
    }

    private void OpenUpdatePage(string url, string line, INoticeSink sink)
    {
        if (line.Trim() != url || !Uri.TryCreate(url, UriKind.Absolute, out var page))
        {
            return;
        }

        string answer = OpenInDefaultBrowser(page);
        if (answer.StartsWith(ErrorLead, StringComparison.Ordinal))
        {
            sink.Error(answer[ErrorLead.Length..]);
        }
    }

    private void BeginUpdateStage(IUpdater updater, UpdateRelease release, INoticeSink sink)
    {
        string version = release.Version.ToString();
        if (updater.Preflight(release.ArchiveBytes) is { } refused)
        {
            sink.Error(UpdateText.StageFailedLine(refused));
            return;
        }

        var stop = CancellationTokenSource.CreateLinkedTokenSource(_updateStop.Token);
        _updateWorkStop = stop;
        Volatile.Write(ref _updateState, new UpdateScreenState.Downloading(version, 0));
        sink.Notice(UpdateText.DownloadingLine(version, ModelStore.SizeLabel(release.ArchiveBytes)));
        var progress = new UpdateProgress(phase => Volatile.Write(ref _updateState, phase switch
        {
            UpdateStagePhase.Downloading downloading => new UpdateScreenState.Downloading(version, downloading.Percent),
            _ => new UpdateScreenState.Checking(version),
        }));
        _updateWork = Task.Run(() => RunUpdateStageAsync(updater, release, progress, stop.Token), CancellationToken.None);
    }

    private async Task RunUpdateStageAsync(IUpdater updater, UpdateRelease release, UpdateProgress progress, CancellationToken cancellationToken)
    {
        string version = release.Version.ToString();
        try
        {
            var result = await updater.StageAsync(release, progress, release.Notes, cancellationToken).ConfigureAwait(false);
            progress.Close();
            if (result.Ok)
            {
                Volatile.Write(ref _updateState, new UpdateScreenState.Ready(version));
                _updateLines.Enqueue((UpdateText.ReadyLine(version), false));
            }
            else
            {
                Volatile.Write(ref _updateState, new UpdateScreenState.Offered(version));
                _updateLines.Enqueue((UpdateText.StageFailedLine(result.Reason ?? UpdateText.ProbeFailed), true));
            }

            WebNudge();
        }
        catch (OperationCanceledException)
        {
            // /update cancel, or the screen ending: whoever canceled says what's next.
            progress.Close();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            progress.Close();
            Volatile.Write(ref _updateState, new UpdateScreenState.Offered(version));
            _updateLines.Enqueue((UpdateText.StageFailedLine(ex.Message), true));
            WebNudge();
        }
    }

    /// <summary>The offer to cancel, with no request: Cancel update (<c>c</c>) and Keep (<c>k</c>), its find off, and Esc means Keep. Where there's no pane to ask on, it names <c>/update cancel</c>.</summary>
    private async Task OfferUpdateCancelAsync(IUpdater updater, UpdateScreenState state, INoticeSink sink, CancellationToken cancellationToken)
    {
        string what = UpdateText.UnderWay(state);
        if (!_pane.Enabled)
        {
            sink.Notice(what + " " + UpdateText.CancelByCommand);
            return;
        }

        var tab = new InfoTab(UpdateText.PaneTab, () => new Spectre.Console.Text(what));
        await _info.ShowAsync(UpdateText.PaneLabel, [tab], 0, cancellationToken, [UpdateText.CancelAction, UpdateText.KeepAction], noFind: true).ConfigureAwait(false);
        if (_info.Pressed == UpdateText.CancelAction)
        {
            await UpdateCancelAsync(updater, sink).ConfigureAwait(false);
        }
    }

    private async Task UpdateCancelAsync(IUpdater updater, INoticeSink sink)
    {
        RefreshUpdateFromDisk(updater);
        var state = Volatile.Read(ref _updateState);
        switch (state)
        {
            case UpdateScreenState.Downloading or UpdateScreenState.Checking:
                // Canceling while it's checking kills the probe and waits for it, as a timeout does, before dropping the stage. Unlike quitting, this deletes the partial file too.
                _updateWorkStop?.Cancel();
                if (_updateWork is { } work)
                {
                    await work.ConfigureAwait(false);
                }

                ReportUpdateDrop(updater, sink, alsoParts: true, UpdateText.VersionOf(state));
                break;
            case UpdateScreenState.Ready ready:
                ReportUpdateDrop(updater, sink, alsoParts: false, ready.Version);
                break;
            case UpdateScreenState.GoingBack:
                updater.CancelRollback();
                Volatile.Write(ref _updateState, UpdateScreenState.Nothing);
                sink.Notice(UpdateText.CanceledLine);
                break;
            default:
                sink.Notice(UpdateText.NothingToCancel);
                break;
        }
    }

    private void ReportUpdateDrop(IUpdater updater, INoticeSink sink, bool alsoParts, string version)
    {
        if (updater.DropStage(alsoParts) is { } busy)
        {
            sink.Error(UpdateText.StageFailedLine(busy));
            return;
        }

        Volatile.Write(ref _updateState, new UpdateScreenState.Offered(version));
        sink.Notice(UpdateText.CanceledLine);
    }

    private async Task UpdateRollbackAsync(IUpdater updater, INoticeSink sink, CancellationToken cancellationToken)
    {
        RefreshUpdateFromDisk(updater);
        switch (Volatile.Read(ref _updateState))
        {
            case UpdateScreenState.Downloading or UpdateScreenState.Checking:
                sink.Notice(UpdateText.CancelDownloadFirst);
                return;
            case UpdateScreenState.GoingBack pending:
                sink.Notice(UpdateText.AlreadyGoingBack(pending.Target));
                return;
        }

        // It hashes the files that move, which is no work for the screen's task.
        var (reason, target) = await Task.Run(() => (updater.AskRollback(cancellationToken, out string? to), to), cancellationToken).ConfigureAwait(false);
        if (reason is not null || target is null)
        {
            if (reason == UpdateText.NothingToGoBackTo)
            {
                sink.Notice(reason);
            }
            else
            {
                sink.Error(UpdateText.RollbackRefusedLine(reason ?? UpdateText.NothingToGoBackTo));
            }

            return;
        }

        Volatile.Write(ref _updateState, new UpdateScreenState.GoingBack(target));
        sink.Warning(UpdateText.RollbackWarning);
        sink.Notice(UpdateText.GoingBackLine(target));
    }

    /// <summary>
    /// Progress on the reporter's own thread: <see cref="Progress{T}"/> posts to the pool, so a percentage could land after the stage has finished and put back a state it had moved on from.
    /// </summary>
    private sealed class UpdateProgress(Action<UpdateStagePhase> report) : IProgress<UpdateStagePhase>
    {
        private int _closed;

        public void Close() => Volatile.Write(ref _closed, 1);

        public void Report(UpdateStagePhase value)
        {
            if (Volatile.Read(ref _closed) == 0)
            {
                report(value);
            }
        }
    }
}
