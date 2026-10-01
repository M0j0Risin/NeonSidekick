using System.Globalization;
using NeonSidekick.Speech;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>
/// The embedded model's words (2026-09-29): spinner labels, <c>/server</c> row details, the settings rows' values,
/// notices and errors. Pure statics so the tests can pin them; invariant culture.
/// </summary>
public static class EmbeddedLlmText
{
    /// <summary>How the <c>LLM URL</c> row shows the sentinel (<see cref="EmbeddedEndpoint.BaseUrl"/>).</summary>
    public const string UrlDisplay = "embedded (llama.cpp)";

    /// <summary>The one row of a model list whose filters pass nothing (later on 2026-09-29). Pinned.</summary>
    public const string NoFilterMatch = "no model matches the filter";

    /// <summary>
    /// The refusal when no catalog model is installed and none was named, and <c>/server embedded</c>'s when it has no row to
    /// list (2026-09-29: <c>/server</c> lists the installed models alone since then, the user's ask; "pick one in /server to
    /// download it" until then).
    /// </summary>
    public const string NoneInstalled = "no embedded model is installed; install one in /settings › Embedded › Embedded models";

    /// <summary>The note after a cancelled download: what is on disk stays.</summary>
    public const string PausedNotice = "download paused; pick the model again to resume where it stopped";

    /// <summary>The refusal when the embedded model is asked for while <c>Embedded LLM server enabled</c> is off (2026-09-29).</summary>
    public const string SwitchedOffError = "the embedded LLM is off; turn Embedded LLM server enabled on in /settings › Embedded to use it";

    /// <summary>The log line when the saved LLM URL names the embedded model but its switch is off: the URL is read as blank.</summary>
    public const string SwitchedOffWarning = "The LLM URL names the embedded model, which is switched off (Embedded LLM server enabled); looking for a server as with no URL.";

    /// <summary>The refusal when an image is sent to an embedded model running without its vision projector.</summary>
    public const string NoVisionError = "the embedded model is running without its vision projector; turn Embedded vision on in /settings › Embedded to send images";

    /// <summary>A model's row detail in <c>/server</c> and the catalog: <see cref="RowDetail"/> over its download.</summary>
    public static string ModelDetail(EmbeddedModel model, EmbeddedModelState state) =>
        RowDetail(state, EmbeddedModelCatalog.TotalBytes(model));

    /// <summary>
    /// The drafter column's mark (2026-09-29, the user's ask): the right-most column of the catalog and of <c>/server</c>'s
    /// embedded rows shows it for a model that can draft for itself (<see cref="EmbeddedModel.HasMtp"/> — a drafter file or
    /// the head in its weights), whatever <c>Embedded drafter</c> says; a model without one leaves the column blank. Two cells.
    /// </summary>
    public const string DrafterGlyph = "⚡";

    /// <summary>The vision column's mark (2026-09-29, the user's ask): a model that reads images (<see cref="EmbeddedModel.Vision"/>), whatever <c>Embedded vision</c> says. Two cells.</summary>
    public const string VisionGlyph = "👁️";

    /// <summary>The tools column's mark (2026-09-29, the user's ask): a model that calls tools (<see cref="EmbeddedModel.ToolCalls"/>); the toolbar's /tools glyph. Two cells.</summary>
    public const string ToolsGlyph = "🛠️";

    /// <summary>
    /// The uncensored column's mark for an uncensored build (later on 2026-09-29, the user's pick; <see cref="UncensoredKind.Uncensored"/>):
    /// the broken chain, a ZWJ sequence Windows Terminal draws as one glyph in two cells (<see cref="UI.TextCells"/> counts it so).
    /// </summary>
    public const string UncensoredGlyph = "⛓️‍💥";

    /// <summary>The uncensored column's mark for an aggressive build (later on 2026-09-29, the user's pick; <see cref="UncensoredKind.Aggressive"/>). Two cells.</summary>
    public const string AggressiveGlyph = "💢";

    /// <summary>
    /// The capability columns that follow <paramref name="detail"/> (the drafter's, 2026-09-29; vision's and tools' later
    /// that day, the user's ask; the uncensored one later still): the padding to <paramref name="detailWidth"/> cells, then a
    /// slot each for <see cref="DrafterGlyph"/>, <see cref="VisionGlyph"/>, <see cref="ToolsGlyph"/> and
    /// <see cref="UncensoredGlyph"/> or <see cref="AggressiveGlyph"/> — two blanks and the glyph, or four blanks, so each mark
    /// keeps its column down the list — with the trailing blanks cut; empty for a row that is no embedded model (a scanned
    /// server's) or one that has none of them.
    /// </summary>
    public static string CapabilityColumns(EmbeddedModel? model, string detail, int detailWidth)
    {
        if (model is null)
        {
            return "";
        }

        string uncensored = model.UncensoredKind switch
        {
            UncensoredKind.Aggressive => AggressiveGlyph,
            UncensoredKind.Uncensored => UncensoredGlyph,
            _ => "",
        };
        string slots = Slot(model.HasMtp, DrafterGlyph) + Slot(model.Vision, VisionGlyph) + Slot(model.ToolCalls, ToolsGlyph) + Slot(uncensored.Length > 0, uncensored);
        slots = slots.TrimEnd(' ');
        return slots.Length == 0 ? "" : new string(' ', Math.Max(0, detailWidth - UI.TextCells.Width(detail ?? ""))) + slots;

        static string Slot(bool on, string glyph) => "  " + (on ? glyph : "  ");
    }

    /// <summary>What an install costs to download: the model and its vision projector, plus the llama.cpp runtime when that is missing too.</summary>
    public static string InstallCost(EmbeddedModel model, long runtimeBytes) =>
        runtimeBytes > 0
            ? $"download {ModelStore.SizeLabel(EmbeddedModelCatalog.TotalBytes(model))} + llama.cpp runtime {ModelStore.SizeLabel(runtimeBytes)}"
            : $"download {ModelStore.SizeLabel(EmbeddedModelCatalog.TotalBytes(model))}";

    /// <summary>The question before a download nobody picked in <c>/server</c> (the saved model is missing at connect).</summary>
    public static string InstallQuestion(EmbeddedModel model, long runtimeBytes) => $"{model.Display} is not installed. Install it now ({InstallCost(model, runtimeBytes)})?";

    /// <summary>The llama.cpp runtime's display name: <c>llama.cpp b11258 (cuda)</c>.</summary>
    public static string RuntimeDisplay(LlamaBackend backend) => $"llama.cpp {LlamaRelease.Tag} ({LlamaRelease.Name(backend)})";

    public static string VerifyingLabel(string display) => $"verifying {display}…";

    public static string UnpackingLabel(string display) => $"unpacking {display}…";

    /// <summary>The spinner while <c>llama-server</c> starts: <c>🦙 starting Gemma 4 12B</c> (2026-09-29, the user's wording; was <c>starting … on llama.cpp…</c>).</summary>
    public static string StartingLabel(EmbeddedModel model) => $"🦙 starting {model.Display}";

    /// <summary>The spinner while the started server loads the weights: <c>🦙 loading Gemma 4 12B</c> (same day, same call).</summary>
    public static string LoadingLabel(EmbeddedModel model) => $"🦙 loading {model.Display}";

    /// <summary>
    /// A <c>/server</c> row's detail (dim, after the model's name): installed, not yet, or part-way, the word padded to
    /// <see cref="StateWidth"/> so the <c>·</c> and the size line up down the list (2026-09-29, the user's ask:
    /// <c>download  · 6.9 GB</c> over <c>installed · 7.6 GB</c>); a paused download's share goes last, where its width
    /// moves nothing.
    /// </summary>
    public static string RowDetail(EmbeddedModelState state, long bytes) => state.Kind switch
    {
        EmbeddedModelStateKind.Installed => Detail("installed", bytes),
        EmbeddedModelStateKind.Partial => Detail("paused", bytes) + string.Create(CultureInfo.InvariantCulture, $" · {state.Percent}%"),
        _ => Detail("download", bytes),
    };

    /// <summary>The state column's width: <c>installed</c>, the longest word.</summary>
    public const int StateWidth = 9;

    private static string Detail(string state, long bytes) => state.PadRight(StateWidth) + " · " + ModelStore.SizeLabel(bytes);

    /// <summary>The endpoint's source phrase once running: <c>embedded llama.cpp b11258 cuda on 127.0.0.1:53121</c>.</summary>
    public static string Source(EmbeddedServerInfo info) =>
        string.Create(CultureInfo.InvariantCulture, $"embedded llama.cpp {LlamaRelease.Tag} {LlamaRelease.Name(info.Backend)} on 127.0.0.1:{info.Port}");

    /// <summary>The <c>Embedded models</c> row's value: <c>2 of 4 installed (9.4 GB)</c>.</summary>
    public static string ModelsRowValue(int installed, int offered, long bytes) =>
        installed == 0
            ? string.Create(CultureInfo.InvariantCulture, $"none of {offered} installed")
            : string.Create(CultureInfo.InvariantCulture, $"{installed} of {offered} installed ({ModelStore.SizeLabel(bytes)})");

    /// <summary>The <c>Embedded backend</c> row's value: the setting and what it resolves to, <c>auto (cuda: NVIDIA driver 610.88)</c>.</summary>
    public static string BackendRowValue(string setting, BackendChoice choice) =>
        $"{setting} ({LlamaRelease.Name(choice.Backend)}: {choice.Reason})";

    public static string NotInstalled(EmbeddedModel model) => $"{model.Display} is not installed; pick it in /server to download it";

    public static string UnknownModel(string id) => $"'{id}' is not an embedded model; /model lists the installed ones";

    public static string StartFailed(string detail) => $"the embedded model did not start: {detail}";

    public static string RuntimeFailed(string detail) => $"the llama.cpp runtime could not be installed: {detail}";

    public static string InstallFailed(EmbeddedModel model, string detail) => $"{model.Display} could not be installed: {detail}";

    /// <summary>
    /// The log line when a model's drafter could not be fetched at its start: it runs without drafting this time. Named for
    /// its kind (2026-09-30): MTP, or DFlash for Muse Glimmer's.
    /// </summary>
    public static string DrafterFailed(EmbeddedModel model, string detail)
    {
        string kind = DraftName(model.Draft);
        return $"{model.Display}'s {kind} drafter could not be downloaded, so it starts without {kind}" + Tail(detail);
    }

    /// <summary>What a kind of drafting is called: "MTP" or "DFlash".</summary>
    public static string DraftName(DraftKind draft) => draft == DraftKind.DFlash ? "DFlash" : "MTP";

    public static string Installed(EmbeddedModel model) => $"{model.Display} installed";

    public static string Removed(EmbeddedModel model) => $"{model.Display} removed";

    public static string RemoveQuestion(EmbeddedModel model) => $"Remove {model.Display} ({ModelStore.SizeLabel(EmbeddedModelCatalog.TotalBytes(model))})?";

    /// <summary>The question before a partly downloaded model's files go (2026-09-29, the user's ask). Pinned.</summary>
    public static string RemovePartialQuestion(EmbeddedModel model) => $"Remove the partial download of {model.Display}?";

    /// <summary>A start that ended before the server was ready: the exit code and llama.cpp's last lines.</summary>
    public static string ExitedEarly(int code, string tail) =>
        string.Create(CultureInfo.InvariantCulture, $"llama-server exited with code {code} before it was ready") + Tail(tail);

    /// <summary>The server stopped on its own while in use.</summary>
    public static string Exited(int code, string tail) =>
        string.Create(CultureInfo.InvariantCulture, $"llama-server stopped unexpectedly (code {code}); the next message restarts it") + Tail(tail);

    public static string StartTimeout(TimeSpan waited) =>
        string.Create(CultureInfo.InvariantCulture, $"llama-server was not ready after {waited.TotalSeconds:0} s");

    public static string CudaFallback(string detail) => $"CUDA did not start ({detail}); trying Vulkan";

    /// <summary>A start the kill switch (Ctrl+Alt+X, 2026-10-01) stopped mid-load: what the connect logs, at Info. Pinned.</summary>
    public static string KilledStart(EmbeddedModel model) => $"{model.Display} was unloaded (Ctrl+Alt+X) before it finished loading";

    /// <summary>
    /// The screen's notice after the kill switch (Ctrl+Alt+X, 2026-10-01, the user's ask): the models it unloaded, by their
    /// display names (a botchat's extras too), and the way back. Pinned.
    /// </summary>
    public static string KilledNotice(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return $"Embedded model unloaded: {string.Join(", ", names)}. /server loads it again.";
    }

    /// <summary>The log line of a kill (Ctrl+Alt+X, 2026-10-01): the model ids whose llama-server it stopped.</summary>
    public static string KilledLog(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return $"Kill switch (Ctrl+Alt+X): stopped llama-server for {string.Join(", ", ids)}.";
    }

    /// <summary>
    /// Embedded VRAM only's refusal of a load that came up with part of the model in system memory (2026-10-01, the user's
    /// ask): what went there — layers llama.cpp left on the CPU, shared memory the driver placed — and what to lower.
    /// </summary>
    public static string VramSpilled(VramSpill spill)
    {
        ArgumentNullException.ThrowIfNull(spill);
        var parts = new List<string>(2);
        if (spill.LayersOnCpu > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{spill.LayersOnCpu} of {spill.TotalLayers} layers"));
        }

        if (spill.SharedMiB > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"about {spill.SharedMiB} MiB of shared GPU memory"));
        }

        return $"it spilled {string.Join(" and ", parts)} into system RAM, and Embedded VRAM only is on; {VramAdvice}";
    }

    /// <summary>Embedded VRAM only's refusal of a load whose allocation failed: with every layer on the GPU, it does not fit (2026-10-01). Pinned.</summary>
    public const string VramDidNotFit = "it does not fit in VRAM with every layer on the GPU, and Embedded VRAM only is on; " + VramAdvice;

    /// <summary>What to lower when a VRAM-only load does not fit.</summary>
    private const string VramAdvice = "set Embedded context size to 0 (fit) or lower it, lower Embedded VRAM budget, close what else uses the GPU, or pick a smaller model";

    /// <summary>Embedded VRAM only's refusal on the CPU backend, which has no VRAM to stay in (2026-10-01). Pinned.</summary>
    public const string VramOnlyOnCpu = "Embedded VRAM only is on, but the backend is the CPU; choose CUDA or Vulkan in Embedded backend, or turn Embedded VRAM only off";

    /// <summary>
    /// Embedded VRAM only's refusal on a GPU with little or no memory of its own (2026-10-01, the review's finding and the
    /// user's call: a clear refusal): an integrated GPU's every allocation is shared system memory, so the spill check would
    /// refuse every load with advice that cannot help. Pinned.
    /// </summary>
    public const string VramOnlyNoVram = "Embedded VRAM only is on, but the GPU has little or no VRAM of its own (an integrated GPU shares system RAM); turn Embedded VRAM only off to run it there";

    /// <summary>
    /// Embedded VRAM only's refusal of a load whose layer line never came (2026-10-01, the review's finding): the runtime is
    /// pinned, so no line means the check could not run, not that the load was clean. Pinned.
    /// </summary>
    public const string VramNotChecked = "llama-server did not say where it put the model's layers, so Embedded VRAM only could not check the load; turn Embedded VRAM only off to start it";

    /// <summary>The warning when the server's shared GPU memory was not read: only its layers were checked (2026-10-01).</summary>
    public const string VramSharedNotRead = "Embedded VRAM only: the server's shared GPU memory was not read, so only its layers were checked.";

    /// <summary>The warning when a VRAM-only load's last line did not come through in time; the check runs on what came (2026-10-01).</summary>
    public static string VramLoadLineLate(TimeSpan waited) =>
        string.Create(CultureInfo.InvariantCulture, $"Embedded VRAM only: no \"model loaded\" line within {waited.TotalSeconds:0} s; checking the lines that came.");

    /// <summary>The log line of what Embedded VRAM only's check read from a load (2026-10-01).</summary>
    public static string VramCheckSummary(LlamaLoadReport report, long? sharedBytes)
    {
        ArgumentNullException.ThrowIfNull(report);
        string shared = sharedBytes is { } bytes ? (bytes / 1_048_576).ToString(CultureInfo.InvariantCulture) + " MiB" : "unknown";
        return string.Create(CultureInfo.InvariantCulture, $"Embedded VRAM only: {report.OffloadedLayers}/{report.TotalLayers} layers on the GPU, host buffers {report.HostBufferMiB:0} MiB, shared GPU memory {shared}.");
    }

    private static string Tail(string tail) => string.IsNullOrWhiteSpace(tail) ? "" : ": " + tail.Trim();
}
