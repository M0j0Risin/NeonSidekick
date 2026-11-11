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

/// <summary>
/// What a clear of the chosen containers did: the ones stopped, a sentence per one that would not stop, and the engine's failure when it
/// answered nothing (Docker Desktop not running: then nothing of it holds the GPU).
/// </summary>
public sealed record DockerStopAll(IReadOnlyList<string> Stopped, IReadOnlyList<string> Errors, string? EngineError = null);

/// <summary>
/// A clear of the chosen containers (2026-10-26, the review of 12729c54, the user's call: one clear, one engine list under the switcher's
/// gate, where an owned leave and a leftover sweep had listed apart): the names to stop (the profile's, and the profile's before a switch),
/// the settings their stops go by, whether the post-stop delay is waited after (a model loads next), and what is spared — names (the
/// container a switch goes to, the one serving the main session) and the port of a server on this computer, whose container may be it.
/// </summary>
public sealed record DockerClear(IReadOnlyList<string> Names, AppSettingsData Settings, bool Settle)
{
    /// <summary>Names never stopped.</summary>
    public IReadOnlyCollection<string> SpareNames { get; init; } = [];

    /// <summary>A server on this computer's port: the container publishing it, or on the <c>host</c> network (which publishes none), is spared.</summary>
    public int? SparePort { get; init; }

    /// <summary>
    /// Every stop asked at once, then all awaited (2026-11-10, the app's exit): with <c>Docker server one at a time</c> off several may run,
    /// and the exit's budget covers one stop. The engine finishes a stop it has received even if the caller gives up. Else one after another,
    /// each reported as it stops. Since 2026-11-11 (the review of the one-local-model change) a together clear reports at the end, tells no
    /// phase and takes no gate: a window closed while a container loads still asks every stop (the engine takes a second stop of one
    /// container in its stride), and a cancel inside one stop is that stop's <see cref="DockerServerText.StopAsked"/>, so the ones that
    /// stopped are still reported.
    /// </summary>
    public bool Together { get; init; }
}

/// <summary>
/// The chosen containers a clear stopped though this run never started them (2026-10-26, the user's report: one left running by an
/// earlier run sat beside the embedded model on the GPU): their names, and whether it was for an embedded model (else a server on this
/// computer). The screen and headless say each once (<c>LlmSession.TakeLeftoverStopped</c>).
/// </summary>
public sealed record DockerLeftover(IReadOnlyList<string> Names, bool Embedded);

/// <summary>The Docker servers' switcher (2026-10-02): a seam, so the session's tests run over a fake (<c>FakeDockerServers</c>).</summary>
public interface IDockerServers : IDisposable
{
    /// <summary>The chosen containers as the engine lists them (the <c>/server</c> rows), asked nothing else.</summary>
    Task<DockerServerList> ListAsync(AppSettingsData effective, CancellationToken cancellationToken);

    /// <summary>
    /// Stops every other chosen container (with <c>Docker server one at a time</c> on, 2026-11-10; off, they keep running, and a target that
    /// would bind a host port one of them publishes is refused), starts <paramref name="name"/> and waits for its API; <paramref name="phase"/>
    /// is told each step. <c>Docker server post-stop delay</c> is waited when it stopped any, or (2026-11-11, the review) when
    /// <paramref name="freed"/> says the caller just freed the GPU (the embedded model stopped, a profile switch's clear) and the target
    /// loads a model: neither running nor paused, since an unpause loads nothing.
    /// </summary>
    Task<DockerSwitch> SwitchToAsync(string name, AppSettingsData effective, bool freed, Action<string>? phase, CancellationToken cancellationToken);

    /// <summary>
    /// Stops every active container <paramref name="request"/> names but those it spares (another server picked, an embedded model about to
    /// load, a profile switch, the app's exit), from one list of the engine's, under the switch's gate; each name told to
    /// <paramref name="stoppedOne"/> as it stops, so a cancel keeps what happened. A <see cref="DockerClear.Together"/> clear (the exit's)
    /// takes no gate and reports at the end. With <see cref="DockerClear.Settle"/> and any stopped, <c>Docker server post-stop delay</c> is
    /// waited after, as a switch waits it.
    /// </summary>
    Task<DockerStopAll> ClearAsync(DockerClear request, Action<string>? phase, Action<string>? stoppedOne, CancellationToken cancellationToken);
}

/// <summary>
/// The switch between the chosen Docker containers (2026-10-02, the user's ask, after their tray app
/// DockerLlmPicker, whose algorithm this carries): one snapshot of the containers; with <c>Docker server one at a time</c> on (the
/// default; off since 2026-11-10, the user's ask, they run side by side, and since 2026-11-11 a target binding a host port another
/// active chosen container publishes is refused before the engine's bind error) every other chosen one that holds memory
/// (running, paused or restarting) stopped in the chosen order — a paused one unpaused first —, each waited for until it has
/// exited; then, when any was stopped or the caller freed the GPU, <c>Docker server post-stop delay</c> for the GPU's memory to settle; then the target
/// started (a 304 is "already running"). A stop that fails leaves the target unstarted: two models may not fit the GPU, and with the
/// setting on the user's call is one at a time. A clear (<see cref="ClearAsync"/>) stops chosen containers without starting one; the
/// exit's asks every stop together (<see cref="DockerClear.Together"/>). Then what DockerLlmPicker never did: the container's published TCP ports are read (inspect,
/// the running ones first, the asked-for ones else; every-address binds mean loopback) and each port's <c>/v1/models</c> is asked
/// once a second, through the endpoint probe, until one answers — a model takes minutes to load — while every
/// <see cref="InspectEvery"/> the container is looked at: exited is a failure with its last log lines, restarting a phase of its
/// own; past <c>Docker server ready timeout</c> it is a failure too, the container left running. Only chosen containers are ever
/// touched. Every act is audited (<see cref="DockerServerText.By"/>) by <see cref="DockerSession.ActOneAsync"/>. One switch or clear at a
/// time (the exit's together clear excepted); nothing throws but the caller's own cancellation. Starts no process: the engine's API alone.
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

    public async Task<DockerSwitch> SwitchToAsync(string name, AppSettingsData effective, bool freed, Action<string>? phase, CancellationToken cancellationToken)
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

            // With Docker server one at a time off (2026-11-10, the user's ask) the others keep running: none stopped, so no settle either.
            var stopped = new List<string>();
            foreach (var other in effective.DockerServerOneAtATime ? OthersToStop(all, names, except: [name]) : [])
            {
                phase?.Invoke(DockerServerText.Stopping(other.Name));
                if (await StopOneAsync(other, effective, cancellationToken).ConfigureAwait(false) is { } failed)
                {
                    return DockerSwitch.Failed(DockerServerText.StopFailed(other.Name, name, failed), stopped);
                }

                stopped.Add(other.Name);
            }

            // Side by side (2026-11-11, the review): a host port another active chosen container publishes would fail the start with the
            // engine's raw bind error; say which. A paused target holds its ports already, a running one serves.
            bool loads = !target.Running && !target.Paused;
            if (!effective.DockerServerOneAtATime && loads && await PortTakenAsync(target, all, names, cancellationToken).ConfigureAwait(false) is { } taken)
            {
                return DockerSwitch.Failed(taken, stopped);
            }

            await SettleAsync(stopped.Count > 0 || (freed && loads), effective, phase, cancellationToken).ConfigureAwait(false);
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

    public async Task<DockerStopAll> ClearAsync(DockerClear request, Action<string>? phase, Action<string>? stoppedOne, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Names.Count == 0)
        {
            return new DockerStopAll([], []);
        }

        if (request.Together)
        {
            return await ClearTogetherAsync(request, stoppedOne, cancellationToken).ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (all, error) = await _docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
            if (all is null)
            {
                return new DockerStopAll([], [], error);
            }

            // One after another (2026-11-11, the review: back to the loop it was), each spare check, phase, stop and report in turn, so an Esc
            // keeps the stops before it.
            var stopped = new List<string>();
            var errors = new List<string>();
            foreach (var other in OthersToStop(all, request.Names, request.SpareNames))
            {
                if (request.SparePort is { } port && await MayServeAsync(other, port, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                phase?.Invoke(DockerServerText.Stopping(other.Name));
                if (await StopOneAsync(other, request.Settings, cancellationToken).ConfigureAwait(false) is { } failed)
                {
                    errors.Add(other.Name + ": " + failed);
                }
                else
                {
                    stopped.Add(other.Name);
                    stoppedOne?.Invoke(other.Name);
                }
            }

            if (request.Settle)
            {
                await SettleAsync(stopped.Count > 0, request.Settings, phase, cancellationToken).ConfigureAwait(false);
            }

            return new DockerStopAll(stopped, errors);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The exit's clear (2026-11-10; since 2026-11-11, the review): every stop asked at once, then all awaited and reported in the chosen
    /// order. No gate, so a switch mid-load can't hold the window's close; no phase, since none is shown one at a time. A cancel inside one
    /// stop is its <see cref="DockerServerText.StopAsked"/>, not a throw: the engine has the stop, and the ones that did stop are reported.
    /// </summary>
    private async Task<DockerStopAll> ClearTogetherAsync(DockerClear request, Action<string>? stoppedOne, CancellationToken cancellationToken)
    {
        var (all, error) = await _docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
        if (all is null)
        {
            return new DockerStopAll([], [], error);
        }

        var targets = new List<DockerContainer>();
        foreach (var other in OthersToStop(all, request.Names, request.SpareNames))
        {
            if (request.SparePort is not { } port || !await MayServeAsync(other, port, cancellationToken).ConfigureAwait(false))
            {
                targets.Add(other);
            }
        }

        var outcomes = await Task.WhenAll(targets.Select(StopAskedAsync)).ConfigureAwait(false);
        var stopped = new List<string>();
        var errors = new List<string>();
        for (int i = 0; i < targets.Count; i++)
        {
            if (outcomes[i] is { } failed)
            {
                errors.Add(targets[i].Name + ": " + failed);
            }
            else
            {
                stopped.Add(targets[i].Name);
                stoppedOne?.Invoke(targets[i].Name);
            }
        }

        if (request.Settle)
        {
            await SettleAsync(stopped.Count > 0, request.Settings, null, cancellationToken).ConfigureAwait(false);
        }

        return new DockerStopAll(stopped, errors);

        async Task<string?> StopAskedAsync(DockerContainer other)
        {
            try
            {
                return await StopOneAsync(other, request.Settings, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return DockerServerText.StopAsked;
            }
        }
    }

    /// <summary>
    /// Why <paramref name="target"/> can't start beside the others (2026-11-11, the review): <see cref="DockerServerText.PortTaken"/> when an
    /// active chosen container publishes a host port the target binds (its inspect's <see cref="DockerInspected.Bound"/>, there while it is
    /// stopped); null when none does, or the inspect answered nothing (the start then says what the engine says).
    /// </summary>
    private async Task<string?> PortTakenAsync(DockerContainer target, IReadOnlyList<DockerContainer> all, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var others = OthersToStop(all, names, except: [target.Name]).ToList();
        if (others.Count == 0)
        {
            return null;
        }

        var (inspected, _) = await _docker.InspectAsync(target.Id, cancellationToken).ConfigureAwait(false);
        if (inspected is null)
        {
            return null;
        }

        foreach (int port in inspected.Bound.Select(b => b.HostPort).Distinct())
        {
            if (others.FirstOrDefault(o => HostPortsOf(o).Contains(port)) is { } holder)
            {
                return DockerServerText.PortTaken(target.Name, port, holder.Name);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="container"/> may be the server on this computer's <paramref name="port"/>: it publishes the port, or it is
    /// on the <c>host</c> network, which publishes none to compare. One the list shows no ports for (restarting, 2026-10-26, the review's R5)
    /// is judged by its inspect's bindings, the ones it asks for.
    /// </summary>
    private async Task<bool> MayServeAsync(DockerContainer container, int port, CancellationToken cancellationToken)
    {
        if (container.Networks.Contains("host", StringComparer.Ordinal))
        {
            return true;
        }

        var ports = HostPortsOf(container);
        if (ports.Count > 0)
        {
            return ports.Contains(port);
        }

        var (inspected, _) = await _docker.InspectAsync(container.Id, cancellationToken).ConfigureAwait(false);
        return inspected is not null && (string.Equals(inspected.NetworkMode, "host", StringComparison.Ordinal)
            || inspected.Published.Concat(inspected.Bound).Any(b => b.HostPort == port));
    }

    /// <summary>When the GPU was just freed (a container stopped, or the caller's own stop), <c>Docker server post-stop delay</c> for its memory to settle before the next model loads.</summary>
    private async Task SettleAsync(bool freed, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        if (freed && effective.DockerServerPostStopDelaySeconds > 0)
        {
            phase?.Invoke(DockerServerText.Settling);
            await _delay(TimeSpan.FromSeconds(Math.Min(effective.DockerServerPostStopDelaySeconds, AppSettingsData.MaxDockerServerPostStopDelaySeconds)), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The chosen containers not named in <paramref name="except"/> that hold memory, in the chosen order.</summary>
    private static IEnumerable<DockerContainer> OthersToStop(IReadOnlyList<DockerContainer> all, IReadOnlyList<string> names, IReadOnlyCollection<string> except) =>
        names.Where(n => !except.Contains(n, StringComparer.Ordinal))
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
                    return DockerSwitch.Failed(DockerServerText.Exited(target.Name, looked.ExitCode, logs.Lines, SideBySide(effective)), stopped);
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
                return DockerSwitch.Failed(DockerServerText.NotReady(target.Name, seconds, SideBySide(effective)), stopped);
            }

            await _delay(ReadyPoll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether other models may run beside a loading container (2026-11-11, the review): either one-model setting off.</summary>
    private static bool SideBySide(AppSettingsData effective) => !effective.LlmOneLocalModel || !effective.DockerServerOneAtATime;

    public void Dispose()
    {
        _gate.Dispose();
        _docker.Dispose();
    }
}
