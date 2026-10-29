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
    // Each starts with the whale (2026-10-29, the user's ask: as the embedded model's spinner starts with 🦙), loading and
    // unloading alike: the spinner, the Web UI's status row and headless's notices. Pinned.

    public static string Stopping(string name) => $"{DockerText.Glyph} stopping {name}";

    public const string Settling = DockerText.Glyph + " letting the GPU's memory settle";

    /// <summary>Whether <paramref name="phase"/> is a clear's step (<see cref="Stopping"/> or <see cref="Settling"/>): what headless says outside a switch.</summary>
    public static bool IsClearStep(string phase) =>
        phase.StartsWith(DockerText.Glyph + " stopping ", StringComparison.Ordinal) || string.Equals(phase, Settling, StringComparison.Ordinal);

    public static string Starting(string name) => $"{DockerText.Glyph} starting {name}";

    public static string Loading(string name) => $"{DockerText.Glyph} loading the model in {name}";

    public static string Restarting(string name) => $"{DockerText.Glyph} {name} is restarting";

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

    /// <summary>The log line when the one model a container serves is saved as <c>LLM model</c> (2026-10-03). Pinned.</summary>
    public static string ModelSaved(string name, string model) => $"LLM model set to {model}, the one model {name} serves.";

    /// <summary>The log line when the others are stopped on leaving the containers.</summary>
    public static string Left(IReadOnlyList<string> stopped) => "Stopped the Docker server" + (stopped.Count == 1 ? " " : "s ") + string.Join(", ", stopped) + ": another server was picked.";

    /// <summary>
    /// The transcript's line (and the log's) when a clear stopped chosen containers this run never started (2026-10-26, the user's
    /// report and call): one left from an earlier run, or started by hand. <see cref="Left"/>'s "another server was picked" reads wrong
    /// at startup, so it says what for: the embedded model (<paramref name="embedded"/>, the main session's or a bot's) or a server on this
    /// computer. Past tense (the review's R38): a bot's load, or a cancel after the stop, loads nothing next.
    /// </summary>
    public static string LeftForThisComputer(IReadOnlyList<string> stopped, bool embedded)
    {
        ArgumentNullException.ThrowIfNull(stopped);
        return "Stopped the Docker server" + (stopped.Count == 1 ? " " : "s ") + string.Join(", ", stopped)
            + (embedded ? " to free the GPU for the embedded model." : " to free the GPU for a server on this computer.");
    }

    /// <summary>The debug line when a clear found the engine not answering (2026-10-26): nothing of it holds the GPU, and it goes unasked a minute.</summary>
    public static string EngineDownLogLine(string error) => "Docker servers, clearing the GPU: " + error;

    // ─── refusals and failures ──────────────────────────────────────────────────

    public static string NotChosen(string name) => $"{name} is not one of the Docker server containers; tick it on /settings › Servers › Docker.";

    public static string NotFound(string name) => $"There is no Docker container named {name}; untick it on /settings › Servers › Docker, or create it again.";

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
        "The LLM URL names a Docker container that is not a Docker server (Docker servers is off, or the container is unticked); looking for a server instead.";

    public const string SwitchedOffError = "Docker servers is off; switch it on from /settings › Servers › Docker.";

    /// <summary>What the Connect a model page's Docker row says once containers are chosen (2026-10-05). Pinned.</summary>
    public const string ConnectHint = "Docker servers is on: /server lists the chosen containers, and /server docker:<name> starts one.";

    public const string NoneChosenError = "No Docker server containers are chosen; tick some on /settings › Servers › Docker.";

    public const string Unavailable = "Docker servers need Windows (Docker Desktop's engine pipe).";

    /// <summary>
    /// Docker servers on a Mac (2026-10-11, the user's call after measuring): the Docker tools reach a Mac's engine, but Docker Desktop
    /// runs containers in a Linux VM with no GPU passed through (its <c>/info</c> lists only the runc runtimes, no devices), and the
    /// vLLM and SGLang images are CUDA builds, arm64's too. The embedded model is a Mac's local server, on Metal. Pinned.
    /// </summary>
    public const string UnavailableOnMac = "Docker servers need Windows: Docker Desktop on a Mac passes no GPU to containers, so vLLM and SGLang cannot run there; the embedded model runs on Metal instead.";

    /// <summary>The refusal of a Docker server on this system: <see cref="UnavailableOnMac"/> on a Mac, else <see cref="Unavailable"/>.</summary>
    public static string UnavailableHere => OperatingSystem.IsMacOS() ? UnavailableOnMac : Unavailable;

    /// <summary>A <c>/botchat</c> bot whose profile names another container than the one running: one at a time, so it sits out.</summary>
    public static string BotOtherContainer(string wanted, string running) =>
        $"its server is the Docker container {wanted}, but {running} is the one running (one at a time)";

    public const string BotNoneRunning = "its server is a Docker container, and none is running; a botchat never starts one";
}
