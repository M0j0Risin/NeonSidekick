using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Docker;

/// <summary>
/// The Docker door the tools, <c>/docker</c> and its pane share (2026-10-02, <see cref="HomeAssistant.HaSession"/>'s shape): the
/// client for the pipe in force (made again when <c>Docker engine pipe</c> changes), the per-call ceilings, the last
/// container list read (for <c>/docker</c>'s completion, which never reaches the engine), and the lifecycle acts with their
/// audit line — the one place a container is started, stopped, restarted, paused or unpaused, by the model or the user.
/// </summary>
public sealed class DockerSession : IDisposable
{
    /// <summary>A read's ceiling: the lists, an inspect, a log.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A stats read's ceiling: the engine waits for a second reading (about a second) before it answers.</summary>
    public static readonly TimeSpan StatsTimeout = TimeSpan.FromSeconds(20);

    /// <summary>A pull's ceiling: a large image over a slow line.</summary>
    public static readonly TimeSpan PullTimeout = TimeSpan.FromMinutes(15);

    /// <summary>A remove's or a prune's ceiling: the engine frees the disk before it answers.</summary>
    public static readonly TimeSpan CleanupTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The seconds a stop or restart gives a container before the engine kills it, when none is given (the engine's own 10).</summary>
    public const int DefaultStopSeconds = 10;

    /// <summary>The most seconds a stop may give.</summary>
    public const int MaxStopSeconds = 120;

    /// <summary>How long the kept list is offered to completion before a quiet refresh.</summary>
    public static readonly TimeSpan SnapshotAge = TimeSpan.FromSeconds(10);

    private readonly Func<AppSettingsData> _effective;
    private readonly Func<string, DockerClient> _clientFactory;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private DockerClient? _client;
    private string? _clientFor;
    private IReadOnlyList<DockerContainer>? _last;
    private DateTimeOffset _lastTaken;

    /// <param name="effective">The settings, read at every call.</param>
    /// <param name="clientFactory">Makes the client for a pipe; null = <see cref="DockerClient"/> over the pipe itself. Tests pass one over a stub handler.</param>
    /// <param name="time">The clock (the log's <c>since</c>, an image's age, the list's age); null = the system's.</param>
    /// <param name="environment">
    /// The variables (<see cref="EnvironmentOverrides.System"/>) the default client's socket look reads on a Mac (<c>DOCKER_HOST</c>,
    /// the CLI's context; 2026-10-11); null sees none. Windows never reads them.
    /// </param>
    public DockerSession(Func<AppSettingsData> effective, Func<string, DockerClient>? clientFactory = null, TimeProvider? time = null, Func<string, string?>? environment = null)
    {
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _clientFactory = clientFactory ?? (pipe => new DockerClient(pipe, environment: environment));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The settings as they stand now.</summary>
    public AppSettingsData Effective => _effective();

    /// <summary>The clock.</summary>
    public TimeProvider Time => _time;

    /// <summary>
    /// The engine's address as the settings stand: the pipe's bare name on Windows; on a Mac (2026-10-11) the socket's path,
    /// <c>""</c> for "find it" (<see cref="DockerSocket.Normalize"/>).
    /// </summary>
    public static string PipeOf(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return DockerSocket.Here == DockerTransport.Socket ? DockerSocket.Normalize(effective.DockerEnginePipe) : DockerPipe.Normalize(effective.DockerEnginePipe);
    }

    /// <summary>The client for the pipe in force, made again when the setting changed.</summary>
    public DockerClient Client()
    {
        string pipe = PipeOf(_effective());
        lock (_gate)
        {
            // Keyed by the address it was made for (2026-10-11), not the client's own reading of it: a factory's client may read
            // it in the other transport's way (a test's pipe-worded stub on a Mac), and was then made again at every call.
            if (_client is null || !string.Equals(_clientFor, pipe, StringComparison.OrdinalIgnoreCase))
            {
                _client?.Dispose();
                _client = _clientFactory(pipe);
                _clientFor = pipe;
                _last = null;
            }

            return _client;
        }
    }

    /// <summary>The last container list read, however old; null before the first. For completion, which never waits on the engine.</summary>
    public IReadOnlyList<DockerContainer>? Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    /// <summary>Whether <see cref="Last"/> is missing or older than <see cref="SnapshotAge"/>.</summary>
    public bool LastIsStale
    {
        get
        {
            lock (_gate)
            {
                return _last is null || _time.GetUtcNow() - _lastTaken > SnapshotAge;
            }
        }
    }

    /// <summary>Every container, stopped ones too, read afresh (and kept as <see cref="Last"/>); or the failure.</summary>
    public async Task<(IReadOnlyList<DockerContainer>? Containers, string? Error)> ContainersAsync(CancellationToken cancellationToken)
    {
        var reply = await Client().GetAsync("/containers/json?all=1", ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok)
        {
            return (null, reply.Error);
        }

        if (DockerJson.Containers(reply.Body) is not { } containers)
        {
            return (null, DockerText.BadAnswer("/containers/json"));
        }

        lock (_gate)
        {
            _last = containers;
            _lastTaken = _time.GetUtcNow();
        }

        return (containers, null);
    }

    /// <summary>
    /// <paramref name="action"/> (<c>start</c>, <c>stop</c>, <c>restart</c>, <c>pause</c>, <c>unpause</c>) on each of
    /// <paramref name="targets"/> in turn — no policy here, the caller judged it — each one's outcome audited under
    /// <paramref name="who"/>. One container: its line, or its failure. Several: <see cref="DockerText.ActHeader"/> and a line
    /// each, a failure among them said in its place.
    /// </summary>
    public async Task<string> ActAsync(string action, IReadOnlyList<DockerContainer> targets, int? stopSeconds, string who, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(targets);
        var lines = new List<string>(targets.Count);
        int done = 0;
        foreach (var container in targets)
        {
            var reply = await ActOneAsync(action, container, stopSeconds, who, cancellationToken).ConfigureAwait(false);
            lines.Add(reply.Ok ? DockerText.Done(action, container, reply.Already) : reply.Error!);
            done += reply.Ok ? 1 : 0;
        }

        return targets.Count == 1 ? lines[0] : DockerText.ActHeader(action, done, targets.Count) + "\n" + string.Join('\n', lines.Select(l => "  " + l));
    }

    /// <summary>
    /// <paramref name="action"/> on one container (2026-10-02, the Docker servers' switch shares it with <see cref="ActAsync"/>):
    /// the engine's reply, the outcome audited under <paramref name="who"/>, the kept list dropped. A stop or restart gives the
    /// container <paramref name="stopSeconds"/> (clamped to <see cref="MaxStopSeconds"/>) before the engine kills it.
    /// </summary>
    public async Task<DockerReply> ActOneAsync(string action, DockerContainer target, int? stopSeconds, string who, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(target);
        bool timed = action is "stop" or "restart";
        int seconds = Math.Clamp(stopSeconds ?? DefaultStopSeconds, 0, MaxStopSeconds);
        var timeout = timed ? TimeSpan.FromSeconds(seconds + 20) : ReadTimeout;
        string path = "/containers/" + Uri.EscapeDataString(target.Id) + "/" + action + (timed ? "?t=" + seconds.ToString(CultureInfo.InvariantCulture) : "");
        try
        {
            var reply = await Client().PostAsync(path, timeout, cancellationToken).ConfigureAwait(false);
            DiagnosticLog.Info(DockerText.Category, DockerText.AuditLine(who, action + " " + target.Name, reply.Ok ? (reply.Already ? "already so" : "done") : reply.Error!));
            return reply;
        }
        finally
        {
            lock (_gate)
            {
                _last = null;
            }
        }
    }

    /// <summary><c>GET /containers/{id}/json</c> read into what a switch needs (<see cref="DockerInspected"/>): the state, the exit code and the ports; or the failure.</summary>
    public async Task<(DockerInspected? Inspected, string? Error)> InspectAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        var reply = await Client().GetAsync("/containers/" + Uri.EscapeDataString(id) + "/json", ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok)
        {
            return (null, reply.Error);
        }

        return DockerJson.Inspected(reply.Body) is { } inspected ? (inspected, null) : (null, DockerText.BadAnswer("/containers/" + id + "/json"));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
        }
    }
}

/// <summary>
/// When the Docker tools are offered and when a change may run (2026-10-02, the <see cref="Unc.UncAccess"/> shape): the
/// group while <c>Docker tools</c> is on, on Windows or (2026-10-11) a Mac — the engine being up is not asked, so the model can say Docker
/// Desktop is down; a change only while <c>Docker writes</c> is on, checked again at every call, then the user's yes.
/// </summary>
public static class DockerPolicy
{
    /// <summary>Whether a turn offers the Docker group.</summary>
    public static bool IsOffered(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.DockerTools && DockerSocket.Here != DockerTransport.None;
    }

    /// <summary>The refusal of a model's change: <see cref="DockerText.WritesOff"/> while <c>Docker writes</c> is off, else null.</summary>
    public static string? WriteRefusal(bool writesOn) => writesOn ? null : DockerText.WritesOff;
}
