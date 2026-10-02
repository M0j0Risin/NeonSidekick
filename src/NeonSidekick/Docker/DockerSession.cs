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
    private IReadOnlyList<DockerContainer>? _last;
    private DateTimeOffset _lastTaken;

    /// <param name="effective">The settings, read at every call.</param>
    /// <param name="clientFactory">Makes the client for a pipe; null = <see cref="DockerClient"/> over the pipe itself. Tests pass one over a stub handler.</param>
    /// <param name="time">The clock (the log's <c>since</c>, an image's age, the list's age); null = the system's.</param>
    public DockerSession(Func<AppSettingsData> effective, Func<string, DockerClient>? clientFactory = null, TimeProvider? time = null)
    {
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _clientFactory = clientFactory ?? (pipe => new DockerClient(pipe));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The settings as they stand now.</summary>
    public AppSettingsData Effective => _effective();

    /// <summary>The clock.</summary>
    public TimeProvider Time => _time;

    /// <summary>The pipe's bare name as the settings stand.</summary>
    public static string PipeOf(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return DockerPipe.Normalize(effective.DockerEnginePipe);
    }

    /// <summary>The client for the pipe in force, made again when the setting changed.</summary>
    public DockerClient Client()
    {
        string pipe = PipeOf(_effective());
        lock (_gate)
        {
            if (_client is null || !string.Equals(_client.Pipe, pipe, StringComparison.OrdinalIgnoreCase))
            {
                _client?.Dispose();
                _client = _clientFactory(pipe);
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
        var client = Client();
        bool timed = action is "stop" or "restart";
        int seconds = Math.Clamp(stopSeconds ?? DefaultStopSeconds, 0, MaxStopSeconds);
        var timeout = timed ? TimeSpan.FromSeconds(seconds + 20) : ReadTimeout;
        var lines = new List<string>(targets.Count);
        int done = 0;
        try
        {
            foreach (var container in targets)
            {
                string path = "/containers/" + Uri.EscapeDataString(container.Id) + "/" + action + (timed ? "?t=" + seconds.ToString(CultureInfo.InvariantCulture) : "");
                var reply = await client.PostAsync(path, timeout, cancellationToken).ConfigureAwait(false);
                string line = reply.Ok ? DockerText.Done(action, container, reply.Already) : reply.Error!;
                DiagnosticLog.Info(DockerText.Category, DockerText.AuditLine(who, action + " " + container.Name, reply.Ok ? (reply.Already ? "already so" : "done") : line));
                done += reply.Ok ? 1 : 0;
                lines.Add(line);
            }
        }
        finally
        {
            lock (_gate)
            {
                _last = null;
            }
        }

        return targets.Count == 1 ? lines[0] : DockerText.ActHeader(action, done, targets.Count) + "\n" + string.Join('\n', lines.Select(l => "  " + l));
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
/// group while <c>Docker tools</c> is on, on Windows — the engine being up is not asked, so the model can say Docker
/// Desktop is down; a change only while <c>Docker writes</c> is on, checked again at every call, then the user's yes.
/// </summary>
public static class DockerPolicy
{
    /// <summary>Whether a turn offers the Docker group.</summary>
    public static bool IsOffered(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.DockerTools && OperatingSystem.IsWindows();
    }

    /// <summary>The refusal of a model's change: <see cref="DockerText.WritesOff"/> while <c>Docker writes</c> is off, else null.</summary>
    public static string? WriteRefusal(bool writesOn) => writesOn ? null : DockerText.WritesOff;
}
