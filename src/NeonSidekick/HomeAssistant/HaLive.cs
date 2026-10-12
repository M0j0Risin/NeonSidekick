using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.HomeAssistant;

/// <summary>The home bar's live feed (<see cref="HaLive"/>); the seam the screen holds.</summary>
public interface IHaLive : IDisposable
{
    /// <summary>The feed as it stands; read without locking, on every pane tick.</summary>
    HaLiveSnapshot Snapshot { get; }

    /// <summary>
    /// What the bar shows: the entity ids to keep current, or null when the bar is hidden (the connection closes after
    /// <see cref="HaLive.Linger"/>). An empty set keeps the connection, and the registries, with nothing subscribed. Cheap
    /// when nothing changed; never blocks.
    /// </summary>
    void Want(IReadOnlyCollection<string>? entityIds);

    /// <summary>The settings may have changed (the URL, the token): look again now rather than at the next ping.</summary>
    void Nudge();

    /// <summary>Calls a service: over the socket while it is live, else through the REST door. Never throws but the caller's cancellation.</summary>
    Task<HaReply> CallAsync(HaServiceCall call, CancellationToken cancellationToken);

    /// <summary>
    /// The registries and every state, for the picker: over the live socket, else one opened for it, else the REST snapshot's
    /// lesser catalog (<see cref="HaCatalog.FromSnapshot"/>). The failure as an <c>Error:</c> sentence.
    /// </summary>
    Task<(HaCatalog? Catalog, string? Error)> CatalogAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The home bar's live feed (2026-10-12, the user's ask: a bar of Home Assistant devices under the perf bar, kept current):
/// one WebSocket to Home Assistant's own API, opened only while the bar wants it. Signed in with the token
/// <see cref="HaSession"/> decrypts, it reads the registries (areas, floors, devices, entities, labels: the area chips'
/// members) and subscribes with <c>subscribe_entities</c> to the bar's entities alone, so a change arrives the moment it
/// happens and nothing else does. A change of the wanted set subscribes the new set before dropping the old, so nothing is
/// missed between. A <c>ping</c> every <see cref="PingEvery"/> finds a dead connection a TCP stack would not; a dropped one
/// is opened again after <see cref="Backoff"/>; a refused token waits for the settings to change (one warning, never the
/// token). Hidden for <see cref="Linger"/>, the connection closes; a quick show-hide-show keeps it.
///
/// <para>One supervisor task owns the connection; the pane reads <see cref="Snapshot"/>, an immutable record swapped whole,
/// and <see cref="Want"/> swaps a set and wakes it — nothing the pane calls waits on the network. Service calls ride the
/// socket while it is live and fall back to <see cref="HaSession.CallAsync"/>. No new process, no native library:
/// <see cref="ClientWebSocket"/> ships under AOT already (<see cref="Web.DevToolsClient"/>).</para>
/// </summary>
public sealed class HaLive : IHaLive
{
    /// <summary>How long the connection outlives the bar being hidden.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(15);

    /// <summary>How often a live connection is pinged.</summary>
    public static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(30);

    /// <summary>How long a pong may take before the connection counts as dead.</summary>
    public static readonly TimeSpan PongWithin = TimeSpan.FromSeconds(10);

    /// <summary>The waits before each retry; the last repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30)];

    private readonly HaSession _session;
    private readonly Func<Uri, CancellationToken, Task<IHaWire>> _connect;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, HaLiveEntity> _entities = new(StringComparer.Ordinal);
    private volatile HaLiveSnapshot _snapshot = HaLiveSnapshot.Empty;
    private HashSet<string>? _wanted;
    private TaskCompletionSource _wake = NewWake();
    private HaConnection? _live;
    private HaCatalog? _catalog;
    private string? _refusedKey;
    private Task? _loop;
    private long _version;
    private bool _disposed;

    // Failed connections in a row: the backoff's step. Reset only once a connection proved itself (a subscription taken, a pong
    // answered), so one that signs in and then fails every time still backs off (the review's finding, 2026-10-12).
    private int _failures;

    /// <param name="session">The settings and the REST door (calls and the catalog fall back to it).</param>
    /// <param name="connect">Opens a socket to a server; null = <see cref="HaWebSocketWire.ConnectAsync"/>. Tests pass a scripted server.</param>
    /// <param name="time">The clock the waits run on; null = the session's.</param>
    public HaLive(HaSession session, Func<Uri, CancellationToken, Task<IHaWire>>? connect = null, TimeProvider? time = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connect = connect ?? HaWebSocketWire.ConnectAsync;
        _time = time ?? session.Time;
    }

    public HaLiveSnapshot Snapshot => _snapshot;

    /// <summary>The supervisor's task, for the tests to wait on after <see cref="Dispose"/>; null before the first <see cref="Want"/>.</summary>
    internal Task? Loop => _loop;

    public void Want(IReadOnlyCollection<string>? entityIds)
    {
        HashSet<string>? next = entityIds is null ? null : new HashSet<string>(entityIds, StringComparer.Ordinal);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (Same(_wanted, next))
            {
                if (next is not null)
                {
                    EnsureStarted();
                }

                return;
            }

            _wanted = next;
            if (next is not null)
            {
                EnsureStarted();
            }
        }

        Wake();
    }

    public void Nudge() => Wake();

    public async Task<HaReply> CallAsync(HaServiceCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (HaProtocol.CallService(0, call, out string? error) is null)
        {
            return HaReply.Failed(error ?? HaText.BadData);
        }

        if (Volatile.Read(ref _live) is { Closed.IsCompleted: false } live)
        {
            IReadOnlyList<string> logged = call.AreaId is { } area ? [.. call.EntityIds, "area " + area] : call.EntityIds;
            DiagnosticLog.Info(HaText.Category, HaText.CallLog(call.Domain, call.Service, logged));
            try
            {
                using var result = await live.RequestAsync(id => HaProtocol.CallService(id, call, out _)!, _session.Timeout, cancellationToken).ConfigureAwait(false);
                _session.Invalidate();
                return HaProtocol.Succeeded(result.RootElement)
                    ? HaReply.Success(result.RootElement.TryGetProperty("result", out var answer) ? answer.GetRawText() : "")
                    : HaReply.Failed(HaProtocol.ErrorOf(result.RootElement, call.Name));
            }
            catch (HaNotSentException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Never sent: the REST door is safe to try.
                DiagnosticLog.Debug(HaText.Category, "The live call could not be sent, trying REST: " + ex.Message);
            }
            catch (Exception ex) when (Lost(ex) && !cancellationToken.IsCancellationRequested)
            {
                // Sent, and no answer: Home Assistant may have run it, so it is not sent again (a toggle twice is no toggle).
                _session.Invalidate();
                DiagnosticLog.Debug(HaText.Category, "The live call went unanswered: " + ex.Message);
                return HaReply.Failed(HomeBarText.Unconfirmed(call.Name));
            }
        }

        return await _session.CallAsync(call.Domain, call.Service, call.EntityIds, WithArea(call), cancellationToken).ConfigureAwait(false);
    }

    public async Task<(HaCatalog? Catalog, string? Error)> CatalogAsync(CancellationToken cancellationToken)
    {
        var effective = _session.Effective;
        if (HaSession.ServerOf(effective) is not { } server || HaSession.TokenOf(effective) is not { } token)
        {
            return (null, HaText.NotConfigured);
        }

        try
        {
            if (Volatile.Read(ref _live) is { Closed.IsCompleted: false } live)
            {
                return (Adopt(await ReadCatalogAsync(live, withStates: true, cancellationToken).ConfigureAwait(false)), null);
            }

            if (!string.Equals(_refusedKey, KeyOf(effective), StringComparison.Ordinal))
            {
                await using var own = await HaConnection.OpenAsync(_connect, server, token, _session.Timeout, _time, null, cancellationToken).ConfigureAwait(false);
                return (Adopt(await ReadCatalogAsync(own, withStates: true, cancellationToken).ConfigureAwait(false)), null);
            }
        }
        catch (HaAuthRefusedException ex)
        {
            return (null, HomeBarText.Refused(ex.Message));
        }
        catch (Exception ex) when (Lost(ex) && !cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Debug(HaText.Category, "The registries could not be read over the socket, trying REST: " + ex.Message);
        }

        var (snapshot, error) = await _session.SnapshotAsync(cancellationToken, fresh: true).ConfigureAwait(false);
        return snapshot is null ? (null, error) : (HaCatalog.FromSnapshot(snapshot), null);
    }

    /// <summary>
    /// A catalog the picker read becomes the bar's too (the review's finding, 2026-10-12): a room made or a light moved in Home
    /// Assistant since the connection opened is on the bar as soon as the picker shows it, not at the next reconnect.
    /// </summary>
    private HaCatalog Adopt(HaCatalog catalog)
    {
        if (catalog.HasRegistry)
        {
            lock (_gate)
            {
                _catalog = catalog;
                PublishLocked(_snapshot.Status, _snapshot.Error);
            }
        }

        return catalog;
    }

    /// <summary>The call's data with its area as <c>area_id</c>, for the REST door (whose body is the target and the data in one). Pure.</summary>
    internal static string? WithArea(HaServiceCall call)
    {
        if (call.AreaId is not { Length: > 0 } area)
        {
            return call.Data;
        }

        using var data = string.IsNullOrWhiteSpace(call.Data) ? null : JsonDocument.Parse(call.Data);
        return HaJson.Object(w =>
        {
            w.WriteString("area_id", area);
            if (data is not null)
            {
                foreach (var member in data.RootElement.EnumerateObject())
                {
                    if (!string.Equals(member.Name, "area_id", StringComparison.Ordinal))
                    {
                        member.WriteTo(w);
                    }
                }
            }
        });
    }

    // ─── the supervisor ─────────────────────────────────────────────────────────

    private void EnsureStarted()
    {
        // Under _gate.
        _loop ??= Task.Run(() => RunAsync(_stop.Token));
    }

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var wake = WakeTask();
            var wanted = Wanted();
            var effective = _session.Effective;
            if (wanted is null || HaSession.ServerOf(effective) is not { } server || HaSession.TokenOf(effective) is not { } token)
            {
                ClearEntities();
                Publish(HaLiveStatus.Off, wanted is null ? null : HaText.NotConfigured);
                _failures = 0;
                await WaitAsync(wake, null, stop).ConfigureAwait(false);
                continue;
            }

            string key = KeyOf(effective);
            if (string.Equals(_refusedKey, key, StringComparison.Ordinal))
            {
                Publish(HaLiveStatus.Unauthorized, _snapshot.Error);
                await WaitAsync(wake, null, stop).ConfigureAwait(false);
                continue;
            }

            Publish(HaLiveStatus.Connecting, null);
            try
            {
                await using var connection = await HaConnection.OpenAsync(_connect, server, token, _session.Timeout, _time, OnEvent, stop).ConfigureAwait(false);
                await ServeAsync(connection, key, stop).ConfigureAwait(false);
                ClearEntities();
                Publish(HaLiveStatus.Off, null);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (HaAuthRefusedException ex)
            {
                _refusedKey = key;
                DiagnosticLog.Warn(HaText.Category, HomeBarText.RefusedLog);
                Publish(HaLiveStatus.Unauthorized, HomeBarText.Refused(ex.Message));
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                var wait = Backoff[Math.Min(_failures, Backoff.Count - 1)];
                _failures++;
                DiagnosticLog.Debug(HaText.Category, "The live connection failed (" + ex.GetType().Name + ": " + ex.Message + "); trying again in " + wait.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s.");
                Publish(HaLiveStatus.Retrying, HomeBarText.Lost(ex.Message));
                await WaitAsync(wake, wait, stop).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _live, null);
            }
        }
    }

    /// <summary>One connection's life: the registries, then the subscription kept to the wanted set, pings, until it should close.</summary>
    private async Task ServeAsync(HaConnection connection, string key, CancellationToken stop)
    {
        _catalog = await ReadCatalogAsync(connection, withStates: false, stop).ConfigureAwait(false);
        Volatile.Write(ref _live, connection);
        Publish(HaLiveStatus.Live, null);

        var subscribed = new HashSet<string>(StringComparer.Ordinal);
        int? subscription = null;
        DateTimeOffset? hiddenSince = null;
        var nextPing = _time.GetUtcNow() + PingEvery;
        while (true)
        {
            var wake = WakeTask();
            if (!string.Equals(KeyOf(_session.Effective), key, StringComparison.Ordinal))
            {
                return;
            }

            var now = _time.GetUtcNow();
            var wanted = Wanted();
            if (wanted is null)
            {
                hiddenSince ??= now;
                if (now - hiddenSince.Value >= Linger)
                {
                    return;
                }
            }
            else
            {
                hiddenSince = null;
                if (!wanted.SetEquals(subscribed))
                {
                    int? next = null;
                    if (wanted.Count > 0)
                    {
                        int id = 0;
                        using var result = await connection.RequestAsync(i => HaProtocol.SubscribeEntities(id = i, wanted), _session.Timeout, stop).ConfigureAwait(false);
                        if (!HaProtocol.Succeeded(result.RootElement))
                        {
                            throw new IOException(HaProtocol.ErrorOf(result.RootElement, "subscribe_entities"));
                        }

                        next = id;
                        _failures = 0;   // subscribed: the connection works
                    }

                    if (subscription is { } old)
                    {
                        using var _ = await connection.RequestAsync(i => HaProtocol.Unsubscribe(i, old), _session.Timeout, stop).ConfigureAwait(false);
                    }

                    subscription = next;
                    subscribed = wanted;
                    Prune(wanted);
                }
            }

            if (now >= nextPing)
            {
                using var _ = await connection.RequestAsync(i => HaProtocol.Command(i, HaProtocol.Ping), PongWithin, stop).ConfigureAwait(false);
                nextPing = _time.GetUtcNow() + PingEvery;
                _failures = 0;   // a pong: the connection works
                continue;
            }

            var until = nextPing - now;
            if (hiddenSince is { } since && since + Linger - now < until)
            {
                until = since + Linger - now;
            }

            await WaitAsync(wake, until, stop, connection.Closed).ConfigureAwait(false);
            if (connection.Closed.IsCompleted)
            {
                throw new IOException(HomeBarText.Dropped);
            }
        }
    }

    private async Task<HaCatalog> ReadCatalogAsync(HaConnection connection, bool withStates, CancellationToken cancellationToken)
    {
        var timeout = _session.Timeout;
        string[] lists = withStates
            ? [HaProtocol.FloorList, HaProtocol.AreaList, HaProtocol.DeviceList, HaProtocol.EntityList, HaProtocol.LabelList, HaProtocol.GetStates]
            : [HaProtocol.FloorList, HaProtocol.AreaList, HaProtocol.DeviceList, HaProtocol.EntityList, HaProtocol.LabelList];
        var asked = lists.Select(type => connection.RequestAsync(i => HaProtocol.Command(i, type), timeout, cancellationToken)).ToArray();
        var answers = await Task.WhenAll(asked).ConfigureAwait(false);
        try
        {
            // A list an older server lacks (floors, labels) answers unsuccessfully: none of it, rather than no catalog.
            var results = answers.Select(a => HaProtocol.Succeeded(a.RootElement) && a.RootElement.TryGetProperty("result", out var r) ? r : default).ToArray();
            return HaCatalog.Parse(results[0], results[1], results[2], results[3], results[4], withStates ? results[5] : default);
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }
    }

    private void OnEvent(JsonElement change)
    {
        lock (_gate)
        {
            if (HaEntityDiff.Apply(_entities, change))
            {
                PublishLocked(_snapshot.Status, _snapshot.Error);
            }
        }
    }

    private void Prune(HashSet<string> wanted)
    {
        lock (_gate)
        {
            foreach (string id in _entities.Keys.Where(id => !wanted.Contains(id)).ToList())
            {
                _entities.Remove(id);
            }

            PublishLocked(_snapshot.Status, _snapshot.Error);
        }
    }

    private void ClearEntities()
    {
        lock (_gate)
        {
            _entities.Clear();
        }
    }

    private void Publish(HaLiveStatus status, string? error)
    {
        lock (_gate)
        {
            PublishLocked(status, error);
        }
    }

    private void PublishLocked(HaLiveStatus status, string? error) =>
        _snapshot = new HaLiveSnapshot(status, new Dictionary<string, HaLiveEntity>(_entities, StringComparer.Ordinal), _catalog, ++_version, error);

    private HashSet<string>? Wanted()
    {
        lock (_gate)
        {
            return _wanted;
        }
    }

    private Task WakeTask()
    {
        lock (_gate)
        {
            return _wake.Task;
        }
    }

    private void Wake()
    {
        TaskCompletionSource woken;
        lock (_gate)
        {
            woken = _wake;
            _wake = NewWake();
        }

        woken.TrySetResult();
    }

    /// <summary>Waits for a wake, the wait (none = only a wake), or <paramref name="closed"/>, whichever is first; never throws but on <paramref name="stop"/>.</summary>
    private async Task WaitAsync(Task wake, TimeSpan? wait, CancellationToken stop, Task? closed = null)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var waits = new List<Task> { wake, Task.Delay(wait ?? Timeout.InfiniteTimeSpan, _time, timer.Token) };
        if (closed is not null)
        {
            waits.Add(closed);
        }

        await Task.WhenAny(waits).ConfigureAwait(false);
        await timer.CancelAsync().ConfigureAwait(false);
        stop.ThrowIfCancellationRequested();
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The settings that make a connection: the URL and the token as stored (never decrypted for this).</summary>
    private static string KeyOf(Settings.AppSettingsData effective) => (effective.HomeAssistantUrl ?? "").Trim() + "\n" + (effective.HomeAssistantToken ?? "");

    private static bool Same(HashSet<string>? a, HashSet<string>? b) => a is null ? b is null : b is not null && a.SetEquals(b);

    /// <summary>A failure that means the socket is gone or never answered: the REST door is worth trying.</summary>
    private static bool Lost(Exception ex) => ex is IOException or WebSocketException or TimeoutException or OperationCanceledException or HttpRequestException or JsonException or InvalidOperationException;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _stop.Cancel();
        if (_loop is { } loop)
        {
            _ = loop.ContinueWith(_ => _stop.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else
        {
            _stop.Dispose();
        }
    }

    // ─── one connection ─────────────────────────────────────────────────────────

    /// <summary>The token was refused: <c>auth_invalid</c>, its message.</summary>
    private sealed class HaAuthRefusedException(string message) : Exception(message);

    /// <summary>A request that never left: the connection was closed, or the send itself failed. Safe to try elsewhere.</summary>
    private sealed class HaNotSentException(string message, Exception? inner = null) : IOException(message, inner);

    /// <summary>
    /// One signed-in socket: requests matched to their answers by id (sends one at a time), every event handed to the
    /// owner's callback, a reader task whose end is <see cref="Closed"/>.
    /// </summary>
    private sealed class HaConnection : IAsyncDisposable
    {
        private readonly IHaWire _wire;
        private readonly TimeProvider _time;
        private readonly Action<JsonElement>? _onEvent;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>> _pending = new();
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly CancellationTokenSource _reading = new();
        private int _nextId;

        private HaConnection(IHaWire wire, TimeProvider time, Action<JsonElement>? onEvent)
        {
            _wire = wire;
            _time = time;
            _onEvent = onEvent;
            Closed = Task.CompletedTask;
        }

        /// <summary>Done when the reader stopped: the other side closed, or the connection broke.</summary>
        public Task Closed { get; private set; }

        /// <summary>Opens a socket and signs in (<c>auth_required</c> → <c>auth</c> → <c>auth_ok</c>), each step within <paramref name="timeout"/>.</summary>
        public static async Task<HaConnection> OpenAsync(Func<Uri, CancellationToken, Task<IHaWire>> connect, Uri server, string token, TimeSpan timeout, TimeProvider time, Action<JsonElement>? onEvent, CancellationToken cancellationToken)
        {
            var wire = await connect(server, cancellationToken).WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false);
            try
            {
                var hello = await NextAsync(wire, timeout, time, cancellationToken).ConfigureAwait(false);
                if (hello != HaMessageKind.AuthRequired)
                {
                    throw new IOException(HomeBarText.Unexpected(hello.ToString()));
                }

                await wire.SendAsync(HaProtocol.Auth(token), cancellationToken).ConfigureAwait(false);
                string? text = await wire.ReceiveAsync(cancellationToken).WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false)
                    ?? throw new IOException(HomeBarText.Dropped);
                using (var answer = JsonDocument.Parse(text))
                {
                    switch (HaProtocol.Classify(answer.RootElement))
                    {
                        case HaMessageKind.AuthOk:
                            break;
                        case HaMessageKind.AuthInvalid:
                            throw new HaAuthRefusedException(HaProtocol.AuthMessage(answer.RootElement));
                        case var other:
                            throw new IOException(HomeBarText.Unexpected(other.ToString()));
                    }
                }

                var connection = new HaConnection(wire, time, onEvent);
                connection.Closed = Task.Run(connection.ReadAsync);
                return connection;
            }
            catch
            {
                await wire.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private static async Task<HaMessageKind> NextAsync(IHaWire wire, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
        {
            string text = await wire.ReceiveAsync(cancellationToken).WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException(HomeBarText.Dropped);
            using var message = JsonDocument.Parse(text);
            return HaProtocol.Classify(message.RootElement);
        }

        /// <summary>Sends the message <paramref name="build"/> makes for the next id and waits for its answer (a <c>result</c> or a <c>pong</c>), the caller's to dispose.</summary>
        public async Task<JsonDocument> RequestAsync(Func<int, string> build, TimeSpan timeout, CancellationToken cancellationToken)
        {
            int id = Interlocked.Increment(ref _nextId);
            var answer = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = answer;
            try
            {
                if (Closed.IsCompleted)
                {
                    throw new HaNotSentException(HomeBarText.Dropped);
                }

                await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _wire.SendAsync(build(id), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or WebSocketException or InvalidOperationException or ObjectDisposedException)
                {
                    throw new HaNotSentException(ex.Message, ex);
                }
                finally
                {
                    _send.Release();
                }

                return await answer.Task.WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        private async Task ReadAsync()
        {
            try
            {
                while (await _wire.ReceiveAsync(_reading.Token).ConfigureAwait(false) is { } text)
                {
                    JsonDocument message;
                    try
                    {
                        message = JsonDocument.Parse(text);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    var root = message.RootElement;
                    switch (HaProtocol.Classify(root))
                    {
                        case HaMessageKind.Result or HaMessageKind.Pong when HaProtocol.IdOf(root) is { } id && _pending.TryRemove(id, out var waiting):
                            waiting.TrySetResult(message);
                            continue;
                        case HaMessageKind.Event when root.TryGetProperty("event", out var change):
                            _onEvent?.Invoke(change);
                            break;
                    }

                    message.Dispose();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DiagnosticLog.Debug(HaText.Category, "The live connection's reader stopped: " + ex.Message);
            }
            finally
            {
                foreach (var waiting in _pending.Values)
                {
                    waiting.TrySetException(new IOException(HomeBarText.Dropped));
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _reading.CancelAsync().ConfigureAwait(false);
            await _wire.DisposeAsync().ConfigureAwait(false);
            try
            {
                await Closed.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The reader logs its own end.
            }

            _reading.Dispose();
            _send.Dispose();
        }
    }
}
