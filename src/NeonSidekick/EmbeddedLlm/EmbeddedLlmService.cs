using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Speech;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>
/// The embedded model as the rest of the app sees it (2026-09-29): the catalog and each model's state, installing and
/// removing, and the one server — started for a model under the settings, reused while nothing it depends on changed,
/// stopped when another server is chosen. <see cref="EmbeddedLlmService"/> is the real one; the tests fake it.
/// </summary>
public interface IEmbeddedLlm : IAsyncDisposable
{
    IReadOnlyList<EmbeddedModel> Catalog { get; }

    /// <summary><c>&lt;home&gt;/models/llm</c>, for <c>/about</c>.</summary>
    string ModelsDirectory { get; }

    /// <summary><c>&lt;home&gt;/llama</c>, for <c>/about</c>.</summary>
    string LlamaDirectory { get; }

    EmbeddedModelState State(EmbeddedModel model);

    /// <summary>The running server; null when none is up.</summary>
    EmbeddedServerInfo? Running { get; }

    /// <summary>The backend a start under <paramref name="effective"/> would use, and why.</summary>
    BackendChoice Backend(AppSettingsData effective);

    /// <summary>What a start under <paramref name="effective"/> would download for the llama.cpp runtime first: 0 when it is installed.</summary>
    long RuntimeBytesToDownload(AppSettingsData effective);

    /// <summary>Installs the runtime <paramref name="effective"/> needs and then <paramref name="model"/>; a result, never a throw but the caller's cancel.</summary>
    Task<ModelResult> InstallAsync(EmbeddedModel model, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken);

    /// <summary>The server for <paramref name="model"/> under <paramref name="effective"/>, started if need be. Throws <see cref="EmbeddedLlmException"/>.</summary>
    Task<EmbeddedServerInfo> StartAsync(EmbeddedModel model, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken);

    /// <summary>Deletes <paramref name="model"/>'s files, stopping the server first when it has them loaded. Null on success, else why not.</summary>
    string? Remove(EmbeddedModel model);

    /// <summary>Stops the server, if any.</summary>
    void Stop();
}

/// <summary>
/// <see cref="IEmbeddedLlm"/> over <see cref="EmbeddedModels"/> and a <see cref="ILlamaServerHost"/>. A start installs the
/// runtime its backend needs (small, pinned, checked; never the model — that is the user's choice to download), builds
/// the <see cref="LlamaLaunch"/> from the settings, and asks the host. When <c>auto</c> chose CUDA and CUDA does not
/// start (a driver present but broken, a GPU the build does not know), it says so and tries Vulkan — and remembers,
/// so later starts in this run go to Vulkan directly instead of failing CUDA again first. A CUDA runtime that could not
/// be downloaded is not such a failure: it is reported as it is, and the next start tries CUDA again.
/// </summary>
public sealed class EmbeddedLlmService : IEmbeddedLlm
{
    private const string Category = "EmbeddedLlm";

    private readonly EmbeddedModels _files;
    private readonly ILlamaServerHost _host;
    private readonly Func<string?, BackendChoice> _choose;
    private bool _cudaFailed;

    /// <param name="files">The files: catalog, installs, runtimes.</param>
    /// <param name="host">The process host.</param>
    /// <param name="choose">The backend for a <c>Embedded backend</c> setting; <see cref="LlamaBackendDetect.Choose(string?)"/> when null.</param>
    public EmbeddedLlmService(EmbeddedModels files, ILlamaServerHost host, Func<string?, BackendChoice>? choose = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _choose = choose ?? LlamaBackendDetect.Choose;
    }

    /// <summary>The app's service: the real catalog and runtimes under the two folders, a download client with no timeout (gigabytes), the real host.</summary>
    public static EmbeddedLlmService Create(string embeddedModelsDirectory, string llamaDirectory) =>
        new(new EmbeddedModels(embeddedModelsDirectory, llamaDirectory, new HttpClient { Timeout = Timeout.InfiniteTimeSpan }), new LlamaServerHost());

    public IReadOnlyList<EmbeddedModel> Catalog => _files.Catalog;

    public string ModelsDirectory => _files.ModelsDirectory;

    public string LlamaDirectory => _files.LlamaDirectory;

    public EmbeddedServerInfo? Running => _host.Running;

    public EmbeddedModelState State(EmbeddedModel model) => _files.State(model);

    public BackendChoice Backend(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var choice = _choose(effective.EmbeddedBackend);
        return _cudaFailed && choice.Backend == LlamaBackend.Cuda && EmbeddedBackends.Forced(effective.EmbeddedBackend) is null
            ? new BackendChoice(LlamaBackend.Vulkan, "CUDA failed to start earlier")
            : choice;
    }

    public long RuntimeBytesToDownload(AppSettingsData effective) => _files.RuntimeBytesToDownload(Backend(effective).Backend);

    public async Task<ModelResult> InstallAsync(EmbeddedModel model, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var runtime = await _files.EnsureRuntimeAsync(Backend(effective).Backend, phase, cancellationToken).ConfigureAwait(false);
        if (!runtime.Ok)
        {
            return ModelResult.Failed(runtime.Path, EmbeddedLlmText.RuntimeFailed(runtime.Detail));
        }

        var installed = await _files.InstallAsync(model, phase, cancellationToken).ConfigureAwait(false);
        return installed.Ok ? installed : ModelResult.Failed(installed.Path, EmbeddedLlmText.InstallFailed(model, installed.Detail));
    }

    public async Task<EmbeddedServerInfo> StartAsync(EmbeddedModel model, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(effective);
        if (!_files.State(model).IsInstalled)
        {
            throw new EmbeddedLlmException(EmbeddedLlmText.NotInstalled(model));
        }

        var choice = Backend(effective);
        bool auto = EmbeddedBackends.Forced(effective.EmbeddedBackend) is null;
        DiagnosticLog.Info(Category, $"Backend {LlamaRelease.Name(choice.Backend)} ({choice.Reason}).");
        try
        {
            return await StartOnAsync(choice.Backend, model, effective, auto, phase, cancellationToken).ConfigureAwait(false);
        }
        catch (EmbeddedLlmException ex) when (auto && choice.Backend == LlamaBackend.Cuda && !ex.RuntimeMissing)
        {
            _cudaFailed = true;
            DiagnosticLog.Warn(Category, EmbeddedLlmText.CudaFallback(ex.Message));
            return await StartOnAsync(LlamaBackend.Vulkan, model, effective, auto, phase, cancellationToken).ConfigureAwait(false);
        }
    }

    public string? Remove(EmbeddedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_host.Running is { } running && string.Equals(running.ModelId, model.Id, StringComparison.OrdinalIgnoreCase))
        {
            _host.Stop();
        }

        return _files.Remove(model);
    }

    public void Stop() => _host.Stop();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<EmbeddedServerInfo> StartOnAsync(LlamaBackend backend, EmbeddedModel model, AppSettingsData effective, bool auto, Action<string>? phase, CancellationToken cancellationToken)
    {
        var runtime = await _files.EnsureRuntimeAsync(backend, phase, cancellationToken).ConfigureAwait(false);
        if (!runtime.Ok)
        {
            throw new EmbeddedLlmException(EmbeddedLlmText.RuntimeFailed(runtime.Detail), runtimeMissing: true);
        }

        // The projector with Embedded vision on and the file there.
        string mmproj = _files.MmprojPath(model);
        var launch = new LlamaLaunch(
            _files.Executable(backend),
            backend,
            _files.WeightsPath(model),
            effective.EmbeddedVision && File.Exists(mmproj) ? mmproj : null,
            model.Id,
            EmbeddedContextSize.Effective(effective.EmbeddedContextSize),
            EmbeddedGpuLayers.Effective(effective.EmbeddedGpuLayers),
            model.Sampling,
            auto);
        phase?.Invoke(EmbeddedLlmText.StartingLabel(model));
        return await _host.EnsureRunningAsync(launch, model, phase, cancellationToken).ConfigureAwait(false);
    }
}
