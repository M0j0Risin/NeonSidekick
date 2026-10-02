using System.Globalization;

namespace NeonSidekick.Docker;

/// <summary>
/// The Docker servers' words (2026-10-02): the switch's phase labels (the connect spinner's), the <c>/server</c> rows' details,
/// the refusals and failures, the endpoint's source phrase and the log lines. Pure; pinned by tests.
/// </summary>
public static class DockerServerText
{
    /// <summary>The audit's word for the switch: neither the model nor <c>/docker</c>.</summary>
    public const string By = "server";

    // ─── phases ─────────────────────────────────────────────────────────────────

    public static string Stopping(string name) => $"stopping {name}";

    public const string Settling = "letting the GPU's memory settle";

    public static string Starting(string name) => $"starting {name}";

    public static string Loading(string name) => $"loading the model in {name}";

    public static string Restarting(string name) => $"{name} is restarting";

    // ─── rows and the endpoint ──────────────────────────────────────────────────

    /// <summary>A <c>/server</c> row's detail: <c>running · vllm/vllm-openai:latest · :8000</c>, <c>exited · lmsysorg/sglang:latest</c>.</summary>
    public static string RowDetail(DockerContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        string ports = string.Join(", ", DockerServerHost.HostPortsOf(container).Select(p => ":" + p.ToString(CultureInfo.InvariantCulture)));
        return (container.State.Length == 0 ? "unknown" : container.State) + " · " + container.Image + (ports.Length > 0 ? " · " + ports : "");
    }

    /// <summary>The detail of a chosen container the engine no longer lists.</summary>
    public const string MissingDetail = "missing: the engine has no container of this name";

    /// <summary>The endpoint's source phrase (the connected line's): <c>docker sglang_qwen :30000</c>.</summary>
    public static string Source(string name, int port) => $"docker {name} :{port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The source phrase of a container's endpoint whose switch failed. Pinned.</summary>
    public const string NotRunningSource = "docker, not running";

    /// <summary>A container's URL as the settings and the picker show it: <c>docker:sglang_qwen</c>.</summary>
    public static string UrlDisplay(string name) => DockerEndpoint.AliasPrefix + name;

    /// <summary>The log line when a container answers.</summary>
    public static string Ready(string name, Uri url, string detail) => $"{name} answers on {url} ({detail}).";

    /// <summary>The log line when the others are stopped on leaving the containers.</summary>
    public static string Left(IReadOnlyList<string> stopped) => "Stopped the Docker server" + (stopped.Count == 1 ? " " : "s ") + string.Join(", ", stopped) + ": another server was picked.";

    // ─── refusals and failures ──────────────────────────────────────────────────

    public static string NotChosen(string name) => $"{name} is not one of the Docker server containers; tick it on the Docker tab of /settings.";

    public static string NotFound(string name) => $"There is no Docker container named {name}; untick it on the Docker tab of /settings, or create it again.";

    public static string StopFailed(string name, string target, string detail) =>
        $"Could not stop {name}, so {target} was not started (two models may not fit the GPU): {DockerText.Single(detail)}";

    public static string StopTimedOut(string name, int seconds) => $"{name} was still running {seconds.ToString(CultureInfo.InvariantCulture)} s after the stop";

    public static string StartFailed(string name, string detail) => $"Could not start {name}: {DockerText.Single(detail)}";

    public static string NoPublishedPort(string name) =>
        $"{name} publishes no TCP port, so its API cannot be reached; publish it (for example -p 8000:8000). It is left running.";

    public static string Exited(string name, int exitCode, IReadOnlyList<string> tail)
    {
        ArgumentNullException.ThrowIfNull(tail);
        string head = $"{name} exited (code {exitCode.ToString(CultureInfo.InvariantCulture)}) before its model answered";
        return tail.Count == 0 ? head + "." : head + "; its last lines: " + string.Join(" | ", tail.Select(l => DockerText.Clip(DockerText.Single(l), 200)));
    }

    public static string NotReady(string name, int seconds) =>
        $"{name} did not answer /v1/models within {seconds.ToString(CultureInfo.InvariantCulture)} s; it is left running (raise Docker server ready timeout if its model needs longer).";

    public const string SwitchedOffWarning =
        "The LLM URL names a Docker container that is not a Docker server (Docker servers is off, or the container is unticked); finding a server as for a blank URL.";

    public const string SwitchedOffError = "Docker servers is off; turn it on on the Docker tab of /settings.";

    public const string NoneChosenError = "No Docker server containers are chosen; tick some on the Docker tab of /settings.";

    public const string Unavailable = "Docker servers need Windows (Docker Desktop's engine pipe).";

    /// <summary>A <c>/botchat</c> bot whose profile names another container than the one running: one at a time, so it sits out.</summary>
    public static string BotOtherContainer(string wanted, string running) =>
        $"its server is the Docker container {wanted}, but {running} is the one running (one at a time)";

    public const string BotNoneRunning = "its server is a Docker container, and none is running; a botchat never starts one";
}
