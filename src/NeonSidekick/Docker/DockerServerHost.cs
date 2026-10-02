using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.Docker;

/// <summary>The chosen containers as the engine lists them now, in the chosen order; the chosen names it does not list; or the engine's failure.</summary>
public sealed record DockerServerList(IReadOnlyList<DockerContainer> Chosen, IReadOnlyList<string> Missing, string? Error);

/// <summary>A switch's outcome: the live URL the container answered on, its port and its model list; or the failure. The containers it stopped either way.</summary>
public sealed record DockerSwitch(bool Ok, string? Error, Uri? LiveUrl, int Port, ProbeResult Models, IReadOnlyList<string> Stopped)
{
    public static DockerSwitch Failed(string error, IReadOnlyList<string> stopped) => new(false, error, null, 0, default, stopped);
}

/// <summary>What a stop of the chosen containers did: the ones stopped, and a sentence per one that would not stop.</summary>
public sealed record DockerStopAll(IReadOnlyList<string> Stopped, IReadOnlyList<string> Errors);

/// <summary>The Docker servers' switcher (2026-10-02): a seam, so the session's tests run over a fake (<c>FakeDockerServers</c>).</summary>
public interface IDockerServers : IDisposable
{
    /// <summary>The chosen containers as the engine lists them (the <c>/server</c> rows), asked nothing else.</summary>
    Task<DockerServerList> ListAsync(AppSettingsData effective, CancellationToken cancellationToken);

    /// <summary>Stops every other chosen container, starts <paramref name="name"/> and waits for its API; <paramref name="phase"/> is told each step.</summary>
    Task<DockerSwitch> SwitchToAsync(string name, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken);

    /// <summary>Stops every running chosen container but <paramref name="except"/> (another server picked, the app's exit).</summary>
    Task<DockerStopAll> StopAllAsync(AppSettingsData effective, string? except, Action<string>? phase, CancellationToken cancellationToken);
}

/// <summary>
/// The one-at-a-time switch between the chosen Docker containers (2026-10-02, the user's ask, after their tray app
/// DockerLlmPicker, whose algorithm this carries): one snapshot of the containers; every other chosen one that holds memory
/// (running, paused or restarting) stopped in the chosen order — a paused one unpaused first —, each waited for until it has
/// exited; then, when any was stopped, <c>Docker server post-stop delay</c> for the GPU's memory to settle; then the target
/// started (a 304 is "already running"). A stop that fails leaves the target unstarted: two models may not fit the GPU, and the
/// user's call was one at a time. Then what DockerLlmPicker never did: the container's published TCP ports are read (inspect,
/// the running ones first, the asked-for ones else; every-address binds mean loopback) and each port's <c>/v1/models</c> is asked
/// once a second, through the endpoint probe, until one answers — a model takes minutes to load — while every
/// <see cref="InspectEvery"/> the container is looked at: exited is a failure with its last log lines, restarting a phase of its
/// own; past <c>Docker server ready timeout</c> it is a failure too, the container left running. Only chosen containers are ever
/// touched. Every act is audited (<see cref="DockerServerText.By"/>) by <see cref="DockerSession.ActOneAsync"/>. One switch at a
/// time; nothing throws but the caller's own cancellation. Starts no process: the engine's API alone.
/// </summary>
public sealed class DockerServerHost : IDockerServers
{
    /// <summary>How often a stopping container is looked at.</summary>
    public static readonly TimeSpan StopPoll = TimeSpan.FromMilliseconds(500);

    /// <summary>The time after a stop's own timeout a container may take to be gone.</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(15);

    /// <summary>How often the ports are asked while the model loads.</summary>
    public static readonly TimeSpan ReadyPoll = TimeSpan.FromSeconds(1);

    /// <summary>How often the loading container itself is looked at (exited? restarting?).</summary>
    public static readonly TimeSpan InspectEvery = TimeSpan.FromSeconds(5);

    /// <summary>How many of an exited container's last log lines its failure carries.</summary>
    public const int ExitTailLines = 5;

    private readonly DockerSession _docker;
    private readonly Func<Uri, CancellationToken, Task<ProbeResult>> _probe;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="docker">The engine door; owned (disposed with the host).</param>
    /// <param name="probe">Asks a base URL for its model list (<see cref="LlmEndpointProbe.ProbeAsync"/>): never throws.</param>
    /// <param name="time">The clock the waits are measured on.</param>
    /// <param name="delay">How a wait is waited; null = <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> on <paramref name="time"/>. Tests advance a manual clock instead.</param>
    public DockerServerHost(DockerSession docker, Func<Uri, CancellationToken, Task<ProbeResult>> probe, TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((span, ct) => Task.Delay(span, _time, ct));
    }

    /// <summary>A container's published TCP host ports, as the list gives them (none while it is stopped), distinct, in order.</summary>
    public static IReadOnlyList<int> HostPortsOf(DockerContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return container.Ports.Where(p => p.Public is not null && string.Equals(p.Type, "tcp", StringComparison.OrdinalIgnoreCase)).Select(p => p.Public!.Value).Distinct().ToList();
    }

    /// <summary>Whether a listed container holds what a running one holds: running, paused or restarting.</summary>
    public static bool Active(DockerContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return container.State is "running" or "paused" or "restarting";
    }

    /// <summary>
    /// The base URLs a container's API may answer on, one per TCP binding (the running container's published ones, else
    /// the ones it asks for): an every-address bind (blank, <c>0.0.0.0</c>, <c>::</c>) is <c>127.0.0.1</c>, <c>::1</c> is
    /// <c>[::1]</c>, any other address itself. Distinct, in order. Pure.
    /// </summary>
    public static IReadOnlyList<Uri> BaseUrlsOf(DockerInspected inspected)
    {
        ArgumentNullException.ThrowIfNull(inspected);
        var bindings = inspected.Published.Count > 0 ? inspected.Published : inspected.Bound;
        return bindings.Select(b =>
        {
            string ip = (b.HostIp ?? "").Trim();
            string host = ip is "" or "0.0.0.0" or "::" ? "127.0.0.1" : ip.Contains(':', StringComparison.Ordinal) ? "[" + ip + "]" : ip;
            return new Uri("http://" + host + ":" + b.HostPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/v1");
        }).Distinct().ToList();
    }

    public async Task<DockerServerList> ListAsync(AppSettingsData effective, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var names = DockerEndpoint.ChosenNames(effective);
        if (names.Count == 0)
        {
            return new DockerServerList([], [], null);
        }

        var (all, error) = await _docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
        if (all is null)
        {
            return new DockerServerList([], [], error);
        }

        var chosen = names.Select(n => all.FirstOrDefault(c => string.Equals(c.Name, n, StringComparison.Ordinal))).OfType<DockerContainer>().ToList();
        var missing = names.Where(n => !all.Any(c => string.Equals(c.Name, n, StringComparison.Ordinal))).ToList();
        return new DockerServerList(chosen, missing, null);
    }

    public async Task<DockerSwitch> SwitchToAsync(string name, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(effective);
        var names = DockerEndpoint.ChosenNames(effective);
        if (!names.Contains(name, StringComparer.Ordinal))
        {
            return DockerSwitch.Failed(DockerServerText.NotChosen(name), []);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (all, error) = await _docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
            if (all is null)
            {
                return DockerSwitch.Failed(error!, []);
            }

            var target = all.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
            if (target is null)
            {
                return DockerSwitch.Failed(DockerServerText.NotFound(name), []);
            }

            var stopped = new List<string>();
            foreach (var other in OthersToStop(all, names, except: name))
            {
                phase?.Invoke(DockerServerText.Stopping(other.Name));
                if (await StopOneAsync(other, effective, cancellationToken).ConfigureAwait(false) is { } failed)
                {
                    return DockerSwitch.Failed(DockerServerText.StopFailed(other.Name, name, failed), stopped);
                }

                stopped.Add(other.Name);
            }

            if (stopped.Count > 0 && effective.DockerServerPostStopDelaySeconds > 0)
            {
                phase?.Invoke(DockerServerText.Settling);
                await _delay(TimeSpan.FromSeconds(Math.Min(effective.DockerServerPostStopDelaySeconds, AppSettingsData.MaxDockerServerPostStopDelaySeconds)), cancellationToken).ConfigureAwait(false);
            }

            phase?.Invoke(DockerServerText.Starting(name));
            if (!target.Running)
            {
                var started = await _docker.ActOneAsync(target.Paused ? "unpause" : "start", target, null, DockerServerText.By, cancellationToken).ConfigureAwait(false);
                if (!started.Ok)
                {
                    return DockerSwitch.Failed(DockerServerText.StartFailed(name, started.Error!), stopped);
                }
            }

            return await WaitReadyAsync(target, effective, phase, stopped, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DockerStopAll> StopAllAsync(AppSettingsData effective, string? except, Action<string>? phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var names = DockerEndpoint.ChosenNames(effective);
        if (names.Count == 0)
        {
            return new DockerStopAll([], []);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (all, error) = await _docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
            if (all is null)
            {
                return new DockerStopAll([], [error!]);
            }

            var stopped = new List<string>();
            var errors = new List<string>();
            foreach (var other in OthersToStop(all, names, except))
            {
                phase?.Invoke(DockerServerText.Stopping(other.Name));
                if (await StopOneAsync(other, effective, cancellationToken).ConfigureAwait(false) is { } failed)
                {
                    errors.Add(other.Name + ": " + failed);
                }
                else
                {
                    stopped.Add(other.Name);
                }
            }

            return new DockerStopAll(stopped, errors);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The chosen containers other than <paramref name="except"/> that hold memory, in the chosen order.</summary>
    private static IEnumerable<DockerContainer> OthersToStop(IReadOnlyList<DockerContainer> all, IReadOnlyList<string> names, string? except) =>
        names.Where(n => !string.Equals(n, except, StringComparison.Ordinal))
            .Select(n => all.FirstOrDefault(c => string.Equals(c.Name, n, StringComparison.Ordinal)))
            .OfType<DockerContainer>()
            .Where(Active)
            .ToList();

    /// <summary>Stops one container and waits until it has gone (unpaused first when paused): null when it has, else why not.</summary>
    private async Task<string?> StopOneAsync(DockerContainer container, AppSettingsData effective, CancellationToken cancellationToken)
    {
        int seconds = Math.Clamp(effective.DockerServerStopTimeoutSeconds, AppSettingsData.MinDockerServerStopTimeoutSeconds, AppSettingsData.MaxDockerServerStopTimeoutSeconds);
        if (container.Paused)
        {
            var unpaused = await _docker.ActOneAsync("unpause", container, null, DockerServerText.By, cancellationToken).ConfigureAwait(false);
            if (!unpaused.Ok)
            {
                return unpaused.Error;
            }
        }

        var reply = await _docker.ActOneAsync("stop", container, seconds, DockerServerText.By, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok)
        {
            return reply.Error;
        }

        var deadline = _time.GetUtcNow() + TimeSpan.FromSeconds(seconds) + StopGrace;
        while (true)
        {
            var (inspected, error) = await _docker.InspectAsync(container.Id, cancellationToken).ConfigureAwait(false);
            if (inspected is null)
            {
                return error;
            }

            if (!inspected.Active)
            {
                return null;
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return DockerServerText.StopTimedOut(container.Name, seconds + (int)StopGrace.TotalSeconds);
            }

            await _delay(StopPoll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The ports read, then asked once a second until one answers, the container looked at every <see cref="InspectEvery"/>.</summary>
    private async Task<DockerSwitch> WaitReadyAsync(DockerContainer target, AppSettingsData effective, Action<string>? phase, IReadOnlyList<string> stopped, CancellationToken cancellationToken)
    {
        var (inspected, error) = await _docker.InspectAsync(target.Id, cancellationToken).ConfigureAwait(false);
        if (inspected is null)
        {
            return DockerSwitch.Failed(error!, stopped);
        }

        var urls = BaseUrlsOf(inspected);
        if (urls.Count == 0)
        {
            return DockerSwitch.Failed(DockerServerText.NoPublishedPort(target.Name), stopped);
        }

        int seconds = Math.Clamp(effective.DockerServerReadyTimeoutSeconds, AppSettingsData.MinDockerServerReadyTimeoutSeconds, AppSettingsData.MaxDockerServerReadyTimeoutSeconds);
        var start = _time.GetUtcNow();
        var lastLook = start;
        string label = DockerServerText.Loading(target.Name);
        phase?.Invoke(label);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var answers = await Task.WhenAll(urls.Select(u => _probe(u, cancellationToken))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < answers.Length; i++)
            {
                if (answers[i].Exists)
                {
                    DiagnosticLog.Info(DockerText.Category, DockerServerText.Ready(target.Name, urls[i], answers[i].Detail));
                    return new DockerSwitch(true, null, urls[i], urls[i].Port, answers[i], stopped);
                }
            }

            var now = _time.GetUtcNow();
            if (now - lastLook >= InspectEvery)
            {
                lastLook = now;
                var (looked, _) = await _docker.InspectAsync(target.Id, cancellationToken).ConfigureAwait(false);
                if (looked is { Gone: true })
                {
                    var logs = await _docker.Client().LogsAsync(target.Id, ExitTailLines, null, timestamps: false, DockerLogStreams.Both, DockerSession.ReadTimeout, cancellationToken).ConfigureAwait(false);
                    return DockerSwitch.Failed(DockerServerText.Exited(target.Name, looked.ExitCode, logs.Lines), stopped);
                }

                string next = looked is { Status: "restarting" } ? DockerServerText.Restarting(target.Name) : DockerServerText.Loading(target.Name);
                if (!string.Equals(next, label, StringComparison.Ordinal))
                {
                    label = next;
                    phase?.Invoke(label);
                }
            }

            if (now - start >= TimeSpan.FromSeconds(seconds))
            {
                return DockerSwitch.Failed(DockerServerText.NotReady(target.Name, seconds), stopped);
            }

            await _delay(ReadyPoll, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _docker.Dispose();
    }
}
