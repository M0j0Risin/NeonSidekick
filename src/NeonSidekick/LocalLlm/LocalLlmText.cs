using System.Globalization;
using NeonSidekick.Speech;

namespace NeonSidekick.LocalLlm;

/// <summary>
/// The local model's words (2026-09-29): spinner labels, <c>/server</c> row details, the settings rows' values,
/// notices and errors. Pure statics so the tests can pin them; invariant culture.
/// </summary>
public static class LocalLlmText
{
    /// <summary>How the <c>LLM URL</c> row shows the sentinel (<see cref="LocalEndpoint.BaseUrl"/>).</summary>
    public const string UrlDisplay = "local (embedded llama.cpp)";

    /// <summary>The refusal when no catalog model is installed and none was named.</summary>
    public const string NoneInstalled = "no local model is installed; pick one in /server to download it";

    /// <summary>The note after a cancelled download: what is on disk stays.</summary>
    public const string PausedNotice = "download paused; pick the model again to resume where it stopped";

    /// <summary>The refusal when an image is sent to a local model running without its vision projector.</summary>
    public const string NoVisionError = "the local model is running without its vision projector; turn Local vision on in /settings › Local model to send images";

    /// <summary>What an install costs to download: the model and its vision projector, plus the llama.cpp runtime when that is missing too.</summary>
    public static string InstallCost(LocalModel model, long runtimeBytes) =>
        runtimeBytes > 0
            ? $"download {ModelStore.SizeLabel(LocalModelCatalog.TotalBytes(model))} + llama.cpp runtime {ModelStore.SizeLabel(runtimeBytes)}"
            : $"download {ModelStore.SizeLabel(LocalModelCatalog.TotalBytes(model))}";

    /// <summary>The question before a download nobody picked in <c>/server</c> (the saved model is missing at connect).</summary>
    public static string InstallQuestion(LocalModel model, long runtimeBytes) => $"{model.Display} is not installed. Install it now ({InstallCost(model, runtimeBytes)})?";

    /// <summary>The llama.cpp runtime's display name: <c>llama.cpp b11258 (cuda)</c>.</summary>
    public static string RuntimeDisplay(LlamaBackend backend) => $"llama.cpp {LlamaRelease.Tag} ({LlamaRelease.Name(backend)})";

    public static string VerifyingLabel(string display) => $"verifying {display}…";

    /// <summary>The spinner's first word over an install, before the first download reports.</summary>
    public static string PreparingLabel(LocalModel model) => $"preparing {model.Display}…";

    public static string UnpackingLabel(string display) => $"unpacking {display}…";

    public static string StartingLabel(LocalModel model) => $"starting {model.Display} on llama.cpp…";

    public static string LoadingLabel(LocalModel model) => $"loading {model.Display}…";

    /// <summary>A <c>/server</c> row's detail (dim, after the model's name): installed, not yet, or part-way.</summary>
    public static string RowDetail(LocalModelState state, long bytes) => state.Kind switch
    {
        LocalModelStateKind.Installed => $"installed · {ModelStore.SizeLabel(bytes)}",
        LocalModelStateKind.Partial => string.Create(CultureInfo.InvariantCulture, $"paused {state.Percent}% · {ModelStore.SizeLabel(bytes)}"),
        _ => $"download {ModelStore.SizeLabel(bytes)}",
    };

    /// <summary>The endpoint's source phrase once running: <c>local llama.cpp b11258 cuda on 127.0.0.1:53121</c>.</summary>
    public static string Source(LocalServerInfo info) =>
        string.Create(CultureInfo.InvariantCulture, $"local llama.cpp {LlamaRelease.Tag} {LlamaRelease.Name(info.Backend)} on 127.0.0.1:{info.Port}");

    /// <summary>The <c>Local models</c> row's value: <c>2 of 4 installed (9.4 GB)</c>.</summary>
    public static string ModelsRowValue(int installed, int offered, long bytes) =>
        installed == 0
            ? string.Create(CultureInfo.InvariantCulture, $"none of {offered} installed")
            : string.Create(CultureInfo.InvariantCulture, $"{installed} of {offered} installed ({ModelStore.SizeLabel(bytes)})");

    /// <summary>The <c>Local backend</c> row's value: the setting and what it resolves to, <c>auto (cuda: NVIDIA driver 610.88)</c>.</summary>
    public static string BackendRowValue(string setting, BackendChoice choice) =>
        $"{setting} ({LlamaRelease.Name(choice.Backend)}: {choice.Reason})";

    public static string NotInstalled(LocalModel model) => $"{model.Display} is not installed; pick it in /server to download it";

    public static string UnknownModel(string id) => $"'{id}' is not a local model; /model lists the installed ones";

    public static string StartFailed(string detail) => $"the local model did not start: {detail}";

    public static string RuntimeFailed(string detail) => $"the llama.cpp runtime could not be installed: {detail}";

    public static string InstallFailed(LocalModel model, string detail) => $"{model.Display} could not be installed: {detail}";

    public static string Installed(LocalModel model) => $"{model.Display} installed";

    public static string Removed(LocalModel model) => $"{model.Display} removed";

    public static string RemoveQuestion(LocalModel model) => $"Remove {model.Display} ({ModelStore.SizeLabel(LocalModelCatalog.TotalBytes(model))})?";

    /// <summary>A start that ended before the server was ready: the exit code and llama.cpp's last lines.</summary>
    public static string ExitedEarly(int code, string tail) =>
        string.Create(CultureInfo.InvariantCulture, $"llama-server exited with code {code} before it was ready") + Tail(tail);

    /// <summary>The server stopped on its own while in use.</summary>
    public static string Exited(int code, string tail) =>
        string.Create(CultureInfo.InvariantCulture, $"llama-server stopped unexpectedly (code {code}); the next message restarts it") + Tail(tail);

    public static string StartTimeout(TimeSpan waited) =>
        string.Create(CultureInfo.InvariantCulture, $"llama-server was not ready after {waited.TotalSeconds:0} s");

    public static string CudaFallback(string detail) => $"CUDA did not start ({detail}); trying Vulkan";

    /// <summary><c>/botchat multi</c>'s refusal when a bot wants a different local model than the one loaded: one local server at a time.</summary>
    public static string OneModelAtATime(string running) => $"the local server is running {running}; one local model at a time, so a bot on the local server must use it too";

    private static string Tail(string tail) => string.IsNullOrWhiteSpace(tail) ? "" : ": " + tail.Trim();
}
