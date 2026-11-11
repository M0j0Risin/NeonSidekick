using NeonSidekick.Docker;
using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The Docker servers' switcher for the session and screen tests (2026-10-02): no engine, every call recorded in order
/// (<c>list</c>, <c>switch &lt;name&gt;[ freed]</c>, <c>clear[ except &lt;a,b&gt;][ port &lt;n&gt;][ settle][ together]</c>: the spared names comma-joined), each switch answering <see cref="Answer"/> after telling the phase
/// <c>starting &lt;name&gt;</c>. Since 2026-10-26 (the review's R40) a clear acts as the engine would: what runs is <see cref="List"/>'s active chosen
/// containers and the ones switched to (a switch stops the others while <c>DockerServerOneAtATime</c> is on, and since 2026-11-11 reports them in its
/// <see cref="DockerSwitch.Stopped"/>, as the host does), less what a clear stopped; a clear stops those it names, spares the names and the port's
/// container (publishing it, or on the <c>host</c> network) and reports each stop; <see cref="List"/>'s error is the engine's. A together clear
/// (2026-11-11, the review) stops them all first, tells no phase, and then reports them; a cancel inside one stop is that name's
/// <see cref="DockerServerText.StopAsked"/>, as the host's.
/// </summary>
internal sealed class FakeDockerServers : IDockerServers
{
    public static readonly Uri LiveUrl = new("http://127.0.0.1:30000/v1");

    public List<string> Calls { get; } = [];

    public DockerServerList List { get; set; } = new([], [], null);

    public Func<string, DockerSwitch> Answer { get; set; } = _ => Ready("sglang-model");

    /// <summary>Awaited by a clear before each stop (2026-10-26): a test cancels mid-clear here.</summary>
    public Func<string, CancellationToken, Task>? StopGate { get; set; }

    /// <summary>Names whose stop fails, with this error.</summary>
    public HashSet<string> WontStop { get; } = new(StringComparer.Ordinal);

    private readonly HashSet<string> _switched = new(StringComparer.Ordinal);

    private readonly HashSet<string> _stopped = new(StringComparer.Ordinal);

    /// <summary>Awaited by each switch after its phase, before it answers (2026-10-24): a test holds the load here, as <see cref="FakeEmbeddedLlm.StartGate"/> does.</summary>
    public Func<CancellationToken, Task>? Gate { get; set; }

    /// <summary>A switch that came up on <see cref="LiveUrl"/> listing <paramref name="models"/>, each with a 32k window.</summary>
    public static DockerSwitch Ready(params string[] models) =>
        new(true, null, LiveUrl, 30000, new ProbeResult(true, models, models.Length + " chat models", "sglang",
            "{\"data\":[" + string.Join(",", models.Select(m => "{\"id\":\"" + m + "\",\"max_model_len\":32768}")) + "]}"), []);

    public Task<DockerServerList> ListAsync(AppSettingsData effective, CancellationToken cancellationToken)
    {
        Calls.Add("list");
        return Task.FromResult(List);
    }

    public async Task<DockerSwitch> SwitchToAsync(string name, AppSettingsData effective, bool freed, Action<string>? phase, CancellationToken cancellationToken)
    {
        Calls.Add("switch " + name + (freed ? " freed" : ""));
        var stoppedNow = new List<string>();
        if (effective.DockerServerOneAtATime)
        {
            stoppedNow.AddRange(DockerEndpoint.ChosenNames(effective).Where(other => other != name && IsRunning(other)));
            // The others stop first only with Docker server one at a time on (2026-11-10), as the host's switch.
            _switched.RemoveWhere(other => DockerEndpoint.ChosenNames(effective).Contains(other, StringComparer.Ordinal));
            foreach (string other in DockerEndpoint.ChosenNames(effective))
            {
                _stopped.Add(other);
            }
        }

        _stopped.Remove(name);
        _switched.Add(name);
        phase?.Invoke(DockerServerText.Starting(name));
        if (Gate is { } gate)
        {
            await gate(cancellationToken);
        }

        var answer = Answer(name);
        return stoppedNow.Count == 0 ? answer : answer with { Stopped = [.. answer.Stopped, .. stoppedNow] };
    }

    /// <summary>The names each <see cref="ClearAsync"/> was asked with, in order: whose lists the clear went by.</summary>
    public List<IReadOnlyList<string>> ClearNames { get; } = [];

    /// <summary>Each <see cref="ClearAsync"/>'s request, in order (2026-11-11): the settings its stops go by.</summary>
    public List<DockerClear> Requests { get; } = [];

    /// <summary>Whether <paramref name="name"/> runs now, as the engine would say.</summary>
    public bool IsRunning(string name) => !_stopped.Contains(name)
        && (_switched.Contains(name) || List.Chosen.Any(c => c.Name == name && DockerServerHost.Active(c)));

    public async Task<DockerStopAll> ClearAsync(DockerClear request, Action<string>? phase, Action<string>? stoppedOne, CancellationToken cancellationToken)
    {
        Calls.Add("clear" + (request.SpareNames.Count == 0 ? "" : " except " + string.Join(",", request.SpareNames))
            + (request.SparePort is { } port ? " port " + port.ToString(System.Globalization.CultureInfo.InvariantCulture) : "") + (request.Settle ? " settle" : "") + (request.Together ? " together" : ""));
        ClearNames.Add(request.Names);
        Requests.Add(request);
        if (List.Error is { } down)
        {
            return new DockerStopAll([], [], down);
        }

        var stopped = new List<string>();
        var errors = new List<string>();
        if (request.Together)
        {
            return await ClearTogetherAsync(request, stoppedOne, cancellationToken);
        }

        foreach (string name in request.Names)
        {
            if (!IsRunning(name) || request.SpareNames.Contains(name, StringComparer.Ordinal) || MayServe(name, request.SparePort))
            {
                continue;
            }

            phase?.Invoke(DockerServerText.Stopping(name));
            if (StopGate is { } gate)
            {
                await gate(name, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (WontStop.Contains(name))
            {
                errors.Add(name + ": it would not stop");
                continue;
            }

            _stopped.Add(name);
            _switched.Remove(name);
            stopped.Add(name);
            stoppedOne?.Invoke(name);
        }

        if (request.Settle && stopped.Count > 0)
        {
            phase?.Invoke(DockerServerText.Settling);
        }

        return new DockerStopAll(stopped, errors);
    }

    /// <summary>The host's together clear, as the engine would see it: every stop first, each failure or cancel its name's error, then the reports.</summary>
    private async Task<DockerStopAll> ClearTogetherAsync(DockerClear request, Action<string>? stoppedOne, CancellationToken cancellationToken)
    {
        var stopped = new List<string>();
        var errors = new List<string>();
        foreach (string name in request.Names.Where(n => IsRunning(n) && !request.SpareNames.Contains(n, StringComparer.Ordinal) && !MayServe(n, request.SparePort)).ToList())
        {
            try
            {
                if (StopGate is { } gate)
                {
                    await gate(name, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                errors.Add(name + ": " + DockerServerText.StopAsked);
                continue;
            }

            if (WontStop.Contains(name))
            {
                errors.Add(name + ": it would not stop");
                continue;
            }

            _stopped.Add(name);
            _switched.Remove(name);
            stopped.Add(name);
        }

        foreach (string name in stopped)
        {
            stoppedOne?.Invoke(name);
        }

        return new DockerStopAll(stopped, errors);
    }

    private bool MayServe(string name, int? port) =>
        port is { } p && List.Chosen.FirstOrDefault(c => c.Name == name) is { } container
        && (container.Networks.Contains("host", StringComparer.Ordinal) || DockerServerHost.HostPortsOf(container).Contains(p));

    public void Dispose()
    {
    }
}
