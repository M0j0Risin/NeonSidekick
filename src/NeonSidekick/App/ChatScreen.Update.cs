using System.Collections.Concurrent;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Speech;
using NeonSidekick.UI;
using NeonSidekick.Updates;
using Spectre.Console;

namespace NeonSidekick.App;

// ── /update (2026-11-04, self-update) ───────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>A start's answer is used this long; after it <c>/update</c> asks again, so a session left open can't install a release pulled since, or miss a newer one.</summary>
    internal static readonly TimeSpan UpdateAnswerKept = TimeSpan.FromHours(1);

    /// <summary>How long <c>/update cancel</c> waits for the work to stop. It had no limit (2026-11-04, review P3-7): a stuck disk or scanner left the command waiting with the watch's keys held.</summary>
    internal static readonly TimeSpan UpdateCancelWait = TimeSpan.FromSeconds(10);

    /// <summary>A wait at the idle line longer than this drops the keys typed under it, before a pane with buttons opens: the user has gone on typing a message, and its <c>i</c> would press Install (2026-11-04, review P3-8). A shorter wait keeps a quick typist's type-ahead.</summary>
    internal static readonly TimeSpan UpdateTypedKeysGrace = TimeSpan.FromMilliseconds(300);

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
    private readonly IAnsiConsole _updateConsole;
    private int _updateAsked;
    private int _updateAcknowledged;

    // The stage's last word, in a slot of its own (2026-11-04, review P4-12): one queued just as /update cancel ran printed "ready" under "Update canceled.". A cancel empties it.
    private UpdateLine? _updateFinalLine;

    private sealed record UpdateLine(string Text, bool Error);

    private sealed record UpdateRollbackAsk(string? Reason, string? Target, IReadOnlyList<string> Docs);

    /// <summary>The marker on the lower rule. The "is out" one follows the current profile's setting, the only state that reads it (2026-11-04, review P1-20: every redraw copied the settings for nothing); what's under way always shows.</summary>
    private string UpdateMarkerNow()
    {
        var state = Volatile.Read(ref _updateState);
        return UpdateText.Marker(state, state is UpdateScreenState.Offered && _effective().CheckForUpdatesAtStart);
    }

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

            // By compare-exchange (2026-11-04, review P6-6): a stage begun since the read above mustn't lose its marker, and an "is out" under it would say the wrong thing.
            if (state is UpdateScreenState.None or UpdateScreenState.Offered && !ReferenceEquals(Interlocked.CompareExchange(ref _updateState, new UpdateScreenState.Offered(version), state), state))
            {
                return;
            }

            _updateLines.Enqueue((state is UpdateScreenState.GoingBack ? UpdateText.IsOutWhileGoingBack(version) : UpdateText.IsOutLine(version), false));
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

        if (Interlocked.Exchange(ref _updateFinalLine, null) is { } final)
        {
            if (final.Error)
            {
                _transcript.Error(final.Text);
            }
            else
            {
                _transcript.Notice(final.Text);
            }
        }

        if (_updater is not { } updater)
        {
            return;
        }

        // Only now does what the start's line came from go (2026-11-04, review P5-3), with or without a line, or a stale journal would never go.
        if (Interlocked.Exchange(ref _updateAcknowledged, 1) == 0)
        {
            updater.AcknowledgeStart();
        }

        RefreshUpdateFromDisk(updater);   // the marker follows another copy's stage or cancel at each idle line (review P5-7), not only at the next /update
    }

    /// <summary>What a quit says of work still under way, read before the screen ends it, or null. Written by the screen after the alternate buffer has gone, since anything earlier goes with it.</summary>
    private string? UpdateQuitLine()
    {
        var state = Volatile.Read(ref _updateState);
        return state is UpdateScreenState.Downloading or UpdateScreenState.Checking ? UpdateText.QuitMidStage(UpdateText.VersionOf(state)) : null;
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
        _updateWorkStop?.Dispose();
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
        Interlocked.CompareExchange(ref _updateState, next, state);
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

        // Only the bare word (2026-11-04, review P1-13): /update notes at once dropped the start's late answer, and the marker never came.
        if (word.Length == 0)
        {
            Volatile.Write(ref _updateAsked, 1);
        }

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

        try
        {
            switch (word)
            {
                case UpdateText.NotesWord:
                    ShowUpdateNotes(updater, sink);
                    break;
                case UpdateText.RollbackWord:
                    await UpdateRollbackAsync(updater, sink, onWatcher, cancellationToken).ConfigureAwait(false);
                    break;
                case UpdateText.CancelWord:
                    await UpdateCancelAsync(updater, sink).ConfigureAwait(false);
                    break;
                default:
                    await UpdateOpenAsync(updater, sink, onWatcher, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (onWatcher && cancellationToken.IsCancellationRequested)
        {
            // The reply ended under the wait (2026-11-04, review P7-2): left to the watcher, the command came back on the row as type-ahead.
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
        foreach (string line in notes.Split('\n'))
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

    private async Task<UpdateCheckResult?> AskReleaseAsync(IUpdater updater, bool onWatcher, CancellationToken cancellationToken)
    {
        var answer = await UpdateWaitAsync(UpdateText.CheckingSpinner, updater.CheckAsync, onWatcher, cancellationToken).ConfigureAwait(false);
        if (answer is { Kind: UpdateCheckKind.Newer })
        {
            _updateAnswer = answer;
            _updateAnsweredAt = _time.GetUtcNow();
        }
        else if (answer is { Kind: UpdateCheckKind.Latest })
        {
            // A release pulled since (2026-11-04, review P1-14): the marker and the kept answer went on offering it for the hour.
            _updateAnswer = null;
            if (Volatile.Read(ref _updateState) is UpdateScreenState.Offered offered)
            {
                Interlocked.CompareExchange(ref _updateState, UpdateScreenState.Nothing, offered);
            }
        }

        return answer;
    }

    // A wait for an answer, up to the check's own 10 s limit, with Esc stopping it. Null when stopped.
    //
    // At the idle line on the spinner row, as /net does. Keys typed under a wait that took a while are dropped before a pane with buttons opens.
    // Under a reply (2026-11-04, reviews P2-11, P3-8, P7-1) under a short-lived pane with no buttons: the watch's poll is paused for a line's hook, so a wait with no reader left every key, Esc too, stuck for its length. The answer closes the pane by its token. With no pane to read the keys, it just waits.
    private async Task<T?> UpdateWaitAsync<T>(string label, Func<CancellationToken, Task<T>> work, bool onWatcher, CancellationToken cancellationToken)
        where T : class
    {
        if (!onWatcher)
        {
            T? answer = null;
            long began = _time.GetTimestamp();
            bool stopped = await WaitUnderWatchAsync(token => _transcript.WithSpinnerAsync(label, async () =>
            {
                answer = await work(token).ConfigureAwait(false);
                return true;
            }), KeySource.IsTurnCancel, cancellationToken).ConfigureAwait(false);
            if (stopped)
            {
                return null;
            }

            if (_time.GetElapsedTime(began) > UpdateTypedKeysGrace)
            {
                _keys.DropTypedKeys();
            }

            return answer;
        }

        if (!_pane.Enabled)
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }

        using var workStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var paneStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = CloseWhenDoneAsync();
        if (!running.IsCompleted)
        {
            var tab = new InfoTab(UpdateText.PaneTab, () => new Text(label));
            await _info.ShowAsync(UpdateText.PaneLabel, [tab], 0, paneStop.Token, noFind: true).ConfigureAwait(false);
            if (!running.IsCompleted)
            {
                await workStop.CancelAsync().ConfigureAwait(false);   // Esc, or the × at its corner
            }
        }

        try
        {
            return await running.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (workStop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        async Task<T> CloseWhenDoneAsync()
        {
            try
            {
                return await work(workStop.Token).ConfigureAwait(false);
            }
            finally
            {
                await paneStop.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task OfferUpdateInstallAsync(IUpdater updater, UpdateRelease release, INoticeSink sink, CancellationToken cancellationToken)
    {
        string version = release.Version.ToString();
        string size = ModelStore.SizeLabel(release.ArchiveBytes);
        string notes = release.Notes is { Length: > 0 } raw ? UntrustedText.Scrub(raw).Trim() : UpdateText.NoNotesInRelease;

        // From an older Offered too (2026-11-04, review P6-5): the pane said 0.5.8 over a marker saying 0.5.7.
        var now = Volatile.Read(ref _updateState);
        if (now is UpdateScreenState.None || (now is UpdateScreenState.Offered older && older.Version != version))
        {
            Interlocked.CompareExchange(ref _updateState, new UpdateScreenState.Offered(version), now);
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

        string page = UntrustedText.Scrub(release.PageUrl);
        string body = UpdateText.PaneHeading(version, size) + "\n\n" + notes + "\n\n" + page;
        var tab = new InfoTab(UpdateText.PaneTab, () => new Spectre.Console.Text(body))
        {
            LineDoubleClicked = line => OpenUpdatePage(page, line, sink),
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

        // The last stage's has ended, or this one couldn't begin: a stage already under way sends /update to the cancel offer.
        _updateWorkStop?.Dispose();
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
            var result = await updater.StageAsync(release, progress, cancellationToken).ConfigureAwait(false);
            progress.Close();
            if (result.Ok)
            {
                Volatile.Write(ref _updateState, new UpdateScreenState.Ready(version));
                Volatile.Write(ref _updateFinalLine, new UpdateLine(UpdateText.ReadyLine(version), false));
            }
            else
            {
                Volatile.Write(ref _updateState, new UpdateScreenState.Offered(version));
                Volatile.Write(ref _updateFinalLine, new UpdateLine(UpdateText.StageFailedLine(result.Reason ?? UpdateText.ProbeFailed), true));
            }

            WebNudge();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // /update cancel, or the screen ending: whoever canceled says what's next. A cancel whose wait ran out has gone on without this, so the marker can't stay on a download that stopped.
            progress.Close();
            Volatile.Write(ref _updateState, new UpdateScreenState.Offered(version));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // One nobody asked for, a request's own timeout included (2026-11-04, review P7-7), is a failure: read as a cancel, it left the marker at 42% for good.
            progress.Close();
            Volatile.Write(ref _updateState, new UpdateScreenState.Offered(version));
            Volatile.Write(ref _updateFinalLine, new UpdateLine(UpdateText.StageFailedLine(ex.Message), true));
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
                    try
                    {
                        await work.WaitAsync(UpdateCancelWait).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        sink.Error(UpdateText.CancelWaitRanOut);
                        return;
                    }
                }

                Interlocked.Exchange(ref _updateFinalLine, null);
                Volatile.Write(ref _updateState, new UpdateScreenState.Offered(UpdateText.VersionOf(state)));   // even if the drop is refused (review P1-12): the marker can't stay at 42% over work that stopped
                ReportUpdateDrop(updater, sink, alsoParts: true, UpdateText.VersionOf(state));
                break;
            case UpdateScreenState.Ready ready:
                Interlocked.Exchange(ref _updateFinalLine, null);
                ReportUpdateDrop(updater, sink, alsoParts: false, ready.Version);
                break;
            case UpdateScreenState.GoingBack:
                if (updater.CancelRollback() is { } kept)
                {
                    sink.Error(UpdateText.StageFailedLine(kept));
                    break;
                }

                // A stage the rollback left on disk comes back into view (review P3-13).
                Volatile.Write(ref _updateState, UpdateScreenState.Nothing);
                RefreshUpdateFromDisk(updater);
                sink.Notice(UpdateText.CanceledLine);
                break;
            default:
                // A kept partial, or a stage whose test run failed (reviews P4-11, P5-9): nothing else could delete them.
                if (updater.ReadDisk().Leftovers)
                {
                    ReportUpdateDrop(updater, sink, alsoParts: true, UpdateText.VersionOf(state));
                }
                else
                {
                    sink.Notice(UpdateText.NothingToCancel);
                }

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

        if (version.Length > 0)
        {
            Volatile.Write(ref _updateState, new UpdateScreenState.Offered(version));
        }

        sink.Notice(UpdateText.CanceledLine);
    }

    private async Task UpdateRollbackAsync(IUpdater updater, INoticeSink sink, bool onWatcher, CancellationToken cancellationToken)
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
        var asked = await UpdateWaitAsync(
            UpdateText.CheckingEarlierSpinner,
            token => Task.Run(() => new UpdateRollbackAsk(updater.AskRollback(token, out string? to, out var docs), to, docs), token),
            onWatcher,
            cancellationToken).ConfigureAwait(false);
        if (asked is null)
        {
            return;   // stopped with Esc
        }

        var (reason, target, staying) = asked;
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
        sink.Notice(UpdateText.GoingBackLine(target, staying));
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
