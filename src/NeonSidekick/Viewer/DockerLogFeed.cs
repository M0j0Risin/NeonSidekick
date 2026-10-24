using System.Text.Json;
using System.Threading.Channels;
using NeonSidekick.Diagnostics;
using NeonSidekick.Docker;
using NeonSidekick.Shell;

namespace NeonSidekick.Viewer;

/// <summary>
/// A container's log as a line feed (2026-10-23, the user's ask: <c>/docker logs</c> tails the log in a window of its own,
/// <see cref="DockerLogWindow"/>). One loop on the pool, its lines in an <see cref="OutputBuffer"/> (5,000 kept), stderr in the
/// warning color through <see cref="ProcessFeed.Copy"/>:
/// <list type="number">
/// <item>Every follow begins with one inspect by id, which records the id and <c>State.StartedAt</c>: what an end is compared with.</item>
/// <item>The follow: the source's tail the first time, then <c>since</c> the last stamp. The engine's <c>since</c> is inclusive,
/// so the lines that carried the last stamp are counted and exactly that many lines stamped the same are skipped.</item>
/// <item>A stream's end doesn't prove a stop, so nothing is written until the engine's events are opened (a start in between is
/// then never missed) and the container inspected by name. Running with the same start: followed again with no marker. Started
/// again: a <c>restarted</c> marker. A new id under the name (compose's down and up): a marker and the source's tail. Not running
/// or gone: a marker, then the events are waited on, acting only on an exact name (the engine's container filter matches by
/// prefix). Events rather than a poll: Docker Desktop's Resource Saver stops its VM while nothing runs, and a poll every few
/// seconds for hours might keep it awake.</item>
/// <item>A refusal (a logging driver that cannot be read) is a warning line and the end; a failure (the engine down) a warning
/// once per spell and a try again after <see cref="RetryDelay"/>; a follow that ends quickly twice in a row while the container
/// runs waits that long too, so a misbehaving engine never spins the loop.</item>
/// </list>
/// The client is taken through <see cref="DockerSession.TryClient"/> at every attempt: a changed pipe is picked up, and a disposed
/// session (the screen's teardown) ends the loop quietly. Nothing it reads leaves the window: no conversation, no session, no log.
/// </summary>
internal sealed class DockerLogFeed : ILineFeed
{
    /// <summary>How long a failure, or a second quick end in a row, waits before the next try.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>A follow that ends sooner than this after it began counts as a quick end.</summary>
    public static readonly TimeSpan QuickEnd = TimeSpan.FromSeconds(2);

    private readonly DockerLogSource _source;
    private readonly TimeProvider _time;
    private readonly OutputBuffer _buffer = new();
    private readonly List<OutputLine> _scratch = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _closed = new();
    private volatile DockerLogState _state = DockerLogState.Following;
    private volatile bool _disposed;
    private string _id;
    private string? _startedAt;
    private DockerStamp? _lastStamp;
    private int _lastStampCount;
    private DockerStamp? _skipStamp;
    private int _skipLeft;
    private bool _failing;

    public DockerLogFeed(DockerLogSource source, TimeProvider time)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _id = source.Id;
        _buffer.Appended += Raise;
        var token = _closed.Token;
        Loop = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    public event Action? Appended;

    /// <summary>The loop: done when the feed is disposed, the engine refused the log, or the session went. For tests.</summary>
    internal Task Loop { get; }

    /// <summary>The container's name as the command resolved it.</summary>
    public string Name => _source.Name;

    /// <summary>The earlier lines asked for.</summary>
    public int Tail => _source.Tail;

    /// <summary>Where the feed stands.</summary>
    public DockerLogState State => _state;

    /// <summary>Whether the engine refused the log and the feed stopped: running the command again tries afresh.</summary>
    public bool Refused => _state == DockerLogState.Refused;

    public long CopySince(long from, List<LogLine> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        lock (_gate)
        {
            return ProcessFeed.Copy(_buffer, from, _scratch, into);
        }
    }

    public string Title(bool following) => DockerLogWindowText.Title(_source.Name, _state, following);

    public string Empty => DockerLogWindowText.Empty;

    public bool Key(int virtualKey, bool control, bool repeat) => false;

    /// <summary>The <c>/events</c> filter for <paramref name="name"/>: its starts and removals. The name goes through <see cref="JsonEncodedText"/>. Pure.</summary>
    internal static string EventsFilter(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "{\"type\":[\"container\"],\"container\":[\"" + JsonEncodedText.Encode(name).ToString() + "\"],\"event\":[\"start\",\"destroy\"]}";
    }

    // ── the loop ─────────────────────────────────────────────────────────────

    /// <summary>The next follow: the container (its inspect, or null to inspect it first) and whether it starts fresh (the source's tail, no since).</summary>
    private readonly record struct Next(string Id, DockerInspected? Inspected, bool Fresh, bool Quiet);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var next = new Next(_id, null, Fresh: true, Quiet: false);
            int quickEnds = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_source.Docker.TryClient(out var client))
                {
                    return;   // the session went (the screen's teardown): no line, no new client
                }

                var target = next.Inspected;
                if (target is null)
                {
                    var look = await InspectAsync(client, next.Id, cancellationToken).ConfigureAwait(false);
                    if (look.Error is { } error)
                    {
                        await FailedAsync(error, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (look.Inspected is null)
                    {
                        // Gone before this follow began: wait for a new one of the name.
                        var settled = await SettleAsync(client, cancellationToken).ConfigureAwait(false);
                        if (settled is { } found)
                        {
                            next = found;
                        }

                        continue;
                    }

                    target = look.Inspected;
                    if (!target.Running && _state is DockerLogState.Waiting or DockerLogState.Gone)
                    {
                        // Still stopped while waiting (the engine dropped the events): back to the wait, no follow, no second marker.
                        _failing = false;
                        if (await SettleAsync(client, cancellationToken).ConfigureAwait(false) is { } found)
                        {
                            next = found;
                        }

                        continue;
                    }
                }

                _failing = false;
                _id = target.Id;
                _startedAt = target.StartedAt;
                SetState(DockerLogState.Following);
                bool fresh = next.Fresh;
                if (!fresh)
                {
                    _skipStamp = _lastStamp;
                    _skipLeft = _lastStampCount;
                }

                var began = _time.GetUtcNow();
                var end = await client.FollowLogsAsync(_id, fresh ? _source.Tail : null, fresh ? null : _lastStamp, OnLine, DockerSession.ReadTimeout, cancellationToken).ConfigureAwait(false);
                _skipStamp = null;
                switch (end.Outcome)
                {
                    case DockerFollowOutcome.Refused:
                        Warn(end.Error ?? "");
                        SetState(DockerLogState.Refused);
                        return;
                    case DockerFollowOutcome.Failed:
                        await FailedAsync(end.Error ?? "", cancellationToken).ConfigureAwait(false);
                        // Again from the start: an inspect by id, then a follow with since (the source's tail if nothing came yet).
                        next = new Next(_id, null, Fresh: fresh && _lastStamp is null, Quiet: false);
                        continue;
                }

                var after = await SettleAsync(client, cancellationToken).ConfigureAwait(false);
                if (after is not { } settledNext)
                {
                    next = new Next(_id, null, Fresh: false, Quiet: false);
                    continue;
                }

                next = settledNext;
                quickEnds = next.Quiet && _time.GetUtcNow() - began < QuickEnd ? quickEnds + 1 : 0;
                if (quickEnds >= 2)
                {
                    DiagnosticLog.Debug(DockerText.Category, $"The log of {_source.Name} ended quickly twice; waiting before the next follow.");
                    quickEnds = 0;
                    await Task.Delay(RetryDelay, _time, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The window closed.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn(DockerText.Category, $"The Docker log window's feed for {_source.Name} stopped: " + ex.Message);
        }
    }

    /// <summary>
    /// After an end (or an inspect that found nothing): the events opened first, one inspect by name, then what it says acted on
    /// or the events waited on. The next follow, or null after a failure (said and waited out here; the caller tries again).
    /// </summary>
    private async Task<Next?> SettleAsync(DockerClient client, CancellationToken cancellationToken)
    {
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var events = Channel.CreateUnbounded<DockerEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        string name = _source.Name;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watching = client.WatchEventsAsync(EventsFilter(name), line =>
        {
            // The engine's filter matches by prefix (mysql lets mysql_dev2 through): only the exact name counts.
            if (DockerJson.Event(line) is { } e && string.Equals(e.Name, name, StringComparison.Ordinal))
            {
                events.Writer.TryWrite(e);
            }

            return true;
        }, DockerSession.ReadTimeout, watch.Token, () => opened.TrySetResult());
        try
        {
            // The events' 200 in before the inspect: a start between the two then reaches the wait, never falls between them.
            if (await Task.WhenAny(opened.Task, watching).ConfigureAwait(false) == watching && !opened.Task.IsCompleted)
            {
                var refused = await watching.ConfigureAwait(false);
                await FailedAsync(refused.Error ?? DockerText.BadAnswer("/events"), cancellationToken).ConfigureAwait(false);
                return null;
            }

            var look = await InspectAsync(client, name, cancellationToken).ConfigureAwait(false);
            if (look.Error is { } error)
            {
                await FailedAsync(error, cancellationToken).ConfigureAwait(false);
                return null;
            }

            string clock = DockerLogWindowText.Clock(_time.GetLocalNow());
            if (look.Inspected is { Running: true } running)
            {
                if (string.Equals(running.Id, _id, StringComparison.Ordinal))
                {
                    if (string.Equals(running.StartedAt, _startedAt, StringComparison.Ordinal))
                    {
                        return new Next(running.Id, running, Fresh: false, Quiet: true);
                    }

                    Mark(DockerLogWindowText.RestartedMarker(clock));
                    return new Next(running.Id, running, Fresh: false, Quiet: false);
                }

                Mark(DockerLogWindowText.NewContainerMarker(clock, name));
                return new Next(running.Id, running, Fresh: true, Quiet: false);
            }

            // A marker only for a change: a second look after the engine dropped the events, the container still stopped, adds none.
            if (look.Inspected is null)
            {
                if (_state != DockerLogState.Gone)
                {
                    Mark(DockerLogWindowText.GoneMarker(clock, name));
                }

                SetState(DockerLogState.Gone);
            }
            else
            {
                if (_state != DockerLogState.Waiting)
                {
                    Mark(DockerLogWindowText.NotRunningMarker(clock));
                }

                SetState(DockerLogState.Waiting);
            }

            while (true)
            {
                // Under the wait's own token, so a read left behind when the events end is canceled in the finally below.
                var read = events.Reader.ReadAsync(watch.Token).AsTask();
                if (await Task.WhenAny(read, watching).ConfigureAwait(false) == watching && !read.IsCompleted)
                {
                    var ended = await watching.ConfigureAwait(false);
                    if (ended.Outcome == DockerFollowOutcome.Ended)
                    {
                        // The engine closed the events (a restart of its own): wait a moment and look again.
                        await Task.Delay(RetryDelay, _time, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await FailedAsync(ended.Error ?? "", cancellationToken).ConfigureAwait(false);
                    }

                    return null;
                }

                var e = await read.ConfigureAwait(false);
                if (string.Equals(e.Action, "start", StringComparison.Ordinal))
                {
                    if (string.Equals(e.Id, _id, StringComparison.Ordinal))
                    {
                        return new Next(e.Id, null, Fresh: false, Quiet: false);
                    }

                    Mark(DockerLogWindowText.NewContainerMarker(DockerLogWindowText.Clock(_time.GetLocalNow()), name));
                    return new Next(e.Id, null, Fresh: true, Quiet: false);
                }

                if (string.Equals(e.Action, "destroy", StringComparison.Ordinal) && _state != DockerLogState.Gone)
                {
                    Mark(DockerLogWindowText.GoneMarker(DockerLogWindowText.Clock(_time.GetLocalNow()), name));
                    SetState(DockerLogState.Gone);
                }
            }
        }
        finally
        {
            await watch.CancelAsync().ConfigureAwait(false);
            try
            {
                await watching.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The wait's own cancel.
            }
        }
    }

    /// <summary>An inspect by id or name: the container, null for a 404, or the failure's sentence.</summary>
    private static async Task<(DockerInspected? Inspected, string? Error)> InspectAsync(DockerClient client, string idOrName, CancellationToken cancellationToken)
    {
        string path = "/containers/" + Uri.EscapeDataString(idOrName) + "/json";
        var reply = await client.GetAsync(path, DockerSession.ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (reply.Status == 404)
        {
            return (null, null);
        }

        if (!reply.Ok)
        {
            return (null, reply.Error ?? DockerText.BadAnswer(path));
        }

        return DockerJson.Inspected(reply.Body) is { } inspected ? (inspected, null) : (null, DockerText.BadAnswer(path));
    }

    /// <summary>A failure: its sentence as a warning line once a spell, the title says so, and a wait before the next try.</summary>
    private async Task FailedAsync(string error, CancellationToken cancellationToken)
    {
        if (!_failing)
        {
            _failing = true;
            Warn(error);
            DiagnosticLog.Debug(DockerText.Category, $"The Docker log window's feed for {_source.Name} lost the engine: {error}");
        }

        SetState(DockerLogState.WaitingForDocker);
        await Task.Delay(RetryDelay, _time, cancellationToken).ConfigureAwait(false);
    }

    // On the reading thread: one line in, the engine's inclusive since's repeats skipped.
    private void OnLine(DockerLogLine line)
    {
        if (line.Stamp is { } stamp)
        {
            if (_skipLeft > 0 && _skipStamp == stamp)
            {
                _skipLeft--;
                return;
            }

            if (_lastStamp == stamp)
            {
                _lastStampCount++;
            }
            else
            {
                _lastStamp = stamp;
                _lastStampCount = 1;
            }
        }

        _buffer.Append(line.Text, line.IsError);
    }

    private void Mark(string marker) => _buffer.Append(marker, isError: false);

    private void Warn(string sentence) => _buffer.Append(sentence, isError: true);

    private void SetState(DockerLogState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        DiagnosticLog.Debug(DockerText.Category, $"The Docker log window on {_source.Name}: {DockerLogWindowText.StateWords(state)}.");
        Raise();
    }

    private void Raise()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Appended?.Invoke();
        }
        catch
        {
            // A reader that fails must not take the loop down with it.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _buffer.Appended -= Raise;
        _closed.Cancel();
        _ = Loop.ContinueWith(_ => _closed.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
