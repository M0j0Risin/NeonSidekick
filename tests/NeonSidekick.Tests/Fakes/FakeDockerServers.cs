using NeonSidekick.Docker;
using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The Docker servers' switcher for the session and screen tests (2026-10-02): no engine, every call recorded in order
/// (<c>list</c>, <c>switch &lt;name&gt;</c>, <c>stop-all[ except &lt;name&gt;][ settle]</c>), each switch answering <see cref="Answer"/> after telling the phase
/// <c>starting &lt;name&gt;</c>.
/// </summary>
internal sealed class FakeDockerServers : IDockerServers
{
    public static readonly Uri LiveUrl = new("http://127.0.0.1:30000/v1");

    public List<string> Calls { get; } = [];

    public DockerServerList List { get; set; } = new([], [], null);

    public Func<string, DockerSwitch> Answer { get; set; } = _ => Ready("sglang-model");

    public DockerStopAll Stopped { get; set; } = new(["sglang_a"], []);

    /// <summary>A switch that came up on <see cref="LiveUrl"/> listing <paramref name="models"/>, each with a 32k window.</summary>
    public static DockerSwitch Ready(params string[] models) =>
        new(true, null, LiveUrl, 30000, new ProbeResult(true, models, models.Length + " chat models", "sglang",
            "{\"data\":[" + string.Join(",", models.Select(m => "{\"id\":\"" + m + "\",\"max_model_len\":32768}")) + "]}"), []);

    public Task<DockerServerList> ListAsync(AppSettingsData effective, CancellationToken cancellationToken)
    {
        Calls.Add("list");
        return Task.FromResult(List);
    }

    public Task<DockerSwitch> SwitchToAsync(string name, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        Calls.Add("switch " + name);
        phase?.Invoke(DockerServerText.Starting(name));
        return Task.FromResult(Answer(name));
    }

    /// <summary>The chosen names each <see cref="StopAllAsync"/> was asked with, in order: whose list the stop went by.</summary>
    public List<IReadOnlyList<string>> StopNames { get; } = [];

    public Task<DockerStopAll> StopAllAsync(AppSettingsData effective, string? except, bool settle, Action<string>? phase, CancellationToken cancellationToken)
    {
        Calls.Add("stop-all" + (except is null ? "" : " except " + except) + (settle ? " settle" : ""));
        StopNames.Add(DockerEndpoint.ChosenNames(effective));
        return Task.FromResult(Stopped);
    }

    public void Dispose()
    {
    }
}
