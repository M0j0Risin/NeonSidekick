using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Speech;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>
/// The embedded model as the rest of the app sees it (2026-09-29): the catalog and each model's state, installing and
/// removing, and the one server — started for a model under the settings, reused while nothing it depends on changed,
/// stopped when another server is chosen. <see cref="EmbeddedLlmService"/> is the real one; the tests fake it. Beside it,
/// later on 2026-09-29 (the user's ask: <c>Botchat multi-embedded</c> <c>multi-server</c>), the extra servers a botchat starts
/// for the bots' other embedded models, one per model (<see cref="StartExtraAsync"/>, <see cref="StopExtras"/>).
/// </summary>
public interface IEmbeddedLlm : IAsyncDisposable
{
    IReadOnlyList<EmbeddedModel> Catalog { get; }

    /// <summary><c>&lt;home&gt;/models/llm</c>, for <c>/about</c>.</summary>
    string ModelsDirectory { get; }

    /// <summary><c>&lt;home&gt;/llama</c>, for <c>/about</c>.</summary>
    string LlamaDirectory { get; }

    EmbeddedModelState State(EmbeddedModel model);

    /// <summary>What installing <paramref name="model"/> would download: its files not already on disk (2026-10-06: a shared projector or drafter counts nothing).</summary>
    long BytesToDownload(EmbeddedModel model);

    /// <summary>What removing <paramref name="model"/> frees: its weights and the shared files no other model holds.</summary>
    long FreedBytes(EmbeddedModel model);

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

    /// <summary>The extra servers running, in the order they started (later on 2026-09-29); empty when none is.</summary>
    IReadOnlyList<EmbeddedServerInfo> Extras { get; }

    /// <summary>
    /// An extra server for <paramref name="model"/> under <paramref name="effective"/> (a bot's own profile), beside the main
    /// one: the running extra of that model whatever its launch, else one started on a host of its own. Throws
    /// <see cref="EmbeddedLlmException"/>, <see cref="EmbeddedLlmText.NotInstalled"/> for a model not on disk.
    /// </summary>
    Task<EmbeddedServerInfo> StartExtraAsync(EmbeddedModel model, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken);

    /// <summary>Stops every extra server, never the main one; the ones that were running.</summary>
    IReadOnlyList<EmbeddedServerInfo> StopExtras();

    /// <summary>Whether a start, main or extra, is in progress — the runtime's download included (2026-10-01, for the kill switch).</summary>
    bool Starting { get; }

    /// <summary>
    /// The kill switch (Ctrl+Alt+X, 2026-10-01, the user's ask: "immediately unload an embedded model"): every server, the
    /// main one and the extras, killed at once without waiting for a start in progress, which then throws with
    /// <see cref="EmbeddedLlmException.Killed"/> — never a CUDA to Vulkan fallback. The ids of the models it unloaded, running
    /// or loading; empty when there was nothing, which the screen reads as "another server: do nothing".
    /// </summary>
    IReadOnlyList<string> Kill();
}

/// <summary>
/// <see cref="IEmbeddedLlm"/> over <see cref="EmbeddedModels"/> and a <see cref="ILlamaServerHost"/>. A start installs the
/// runtime its backend needs (small, pinned, checked; never the model — that is the user's choice to download), builds
/// the <see cref="LlamaLaunch"/> from the settings, and asks the host. When <c>auto</c> chose CUDA and CUDA does not
/// start (a driver present but broken, a GPU the build does not know), it says so and tries Vulkan — and remembers,
/// so later starts in this run go to Vulkan directly instead of failing CUDA again first. A CUDA runtime that could not
/// be downloaded is not such a failure: it is reported as it is, and the next start tries CUDA again. Nor is a load
/// Embedded VRAM only refused (2026-10-01): the model did not fit, which Vulkan would not change. Nor is a start the kill
/// switch took (Ctrl+Alt+X, later that day, <see cref="Kill"/>): whatever such a start throws is the kill's
/// (<see cref="EmbeddedLlmException.Killed"/>), and CUDA is not marked failed by it.
/// </summary>
public sealed class EmbeddedLlmService : IEmbeddedLlm
{
    private const string Category = "EmbeddedLlm";

    /// <summary>
    /// The least dedicated memory a GPU may report for Embedded VRAM only on Vulkan, in MiB (2026-10-01): an Intel integrated
    /// GPU reports about 128 MiB and an AMD APU its default carve-out of 512 MiB, both refused; an APU carve-out the user
    /// raised past it is real dedicated memory and passes. Pinned.
    /// </summary>
    public const int MinDedicatedVramMiB = 1024;

    private readonly EmbeddedModels _files;
    private readonly ILlamaServerHost _host;
    private readonly Func<string?, BackendChoice> _choose;
    private readonly Func<long?> _totalVram;
    private readonly Func<ILlamaServerHost> _extraHost;
    private readonly List<(string ModelId, ILlamaServerHost Host)> _extras = [];
    private readonly Lock _extrasLock = new();
    private readonly SemaphoreSlim _extrasGate = new(1, 1);
    private bool _cudaFailed;

    // The kill switch (2026-10-01): the starts in progress, by model id, and how many kills there were — a start that saw
    // one since it began is the kill's, whatever it then threw.
    private readonly List<string> _starting = [];
    private readonly Lock _startingLock = new();
    private int _kills;

    /// <param name="files">The files: catalog, installs, runtimes.</param>
    /// <param name="host">The process host.</param>
    /// <param name="choose">The backend for a <c>Embedded backend</c> setting; <see cref="LlamaBackendDetect.Choose(string?)"/> when null.</param>
    /// <param name="totalVram">The biggest GPU's dedicated memory in bytes, for the VRAM budget (later on 2026-09-29); <see cref="Perf.GpuMemory.DedicatedBytes"/> when null.</param>
    /// <param name="extraHost">A host for an extra server (later on 2026-09-29, multi-server botchats); a new <see cref="LlamaServerHost"/> when null.</param>
    public EmbeddedLlmService(EmbeddedModels files, ILlamaServerHost host, Func<string?, BackendChoice>? choose = null, Func<long?>? totalVram = null, Func<ILlamaServerHost>? extraHost = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _choose = choose ?? LlamaBackendDetect.Choose;
        _totalVram = totalVram ?? Perf.GpuMemory.DedicatedBytes;
        _extraHost = extraHost ?? (() => new LlamaServerHost());
    }

    /// <summary>The app's service: the real catalog and runtimes under the two folders, a download client with no timeout (gigabytes), the real host.</summary>
    public static EmbeddedLlmService Create(string embeddedModelsDirectory, string llamaDirectory) =>
        new(new EmbeddedModels(embeddedModelsDirectory, llamaDirectory, new HttpClient { Timeout = Timeout.InfiniteTimeSpan }), new LlamaServerHost());

    public IReadOnlyList<EmbeddedModel> Catalog => _files.Catalog;

    public string ModelsDirectory => _files.ModelsDirectory;

    public string LlamaDirectory => _files.LlamaDirectory;

    public EmbeddedServerInfo? Running => _host.Running;

    public EmbeddedModelState State(EmbeddedModel model) => _files.State(model);

    public long BytesToDownload(EmbeddedModel model) => _files.BytesToDownload(model);

    public long FreedBytes(EmbeddedModel model) => _files.FreedBytes(model);

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

        var installed = await _files.InstallAsync(model, phase, cancellationToken, drafter: effective.EmbeddedDrafter, connections: EmbeddedHfDownloadTypes.Connections(effective)).ConfigureAwait(false);
        return installed.Ok ? installed : ModelResult.Failed(installed.Path, EmbeddedLlmText.InstallFailed(model, installed.Detail));
    }

    public Task<EmbeddedServerInfo> StartAsync(EmbeddedModel model, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(effective);
        if (!_files.State(model).IsInstalled)
        {
            throw new EmbeddedLlmException(EmbeddedLlmText.NotInstalled(model));
        }

        return StartOnHostAsync(_host, model, effective, phase, cancellationToken);
    }

    public IReadOnlyList<EmbeddedServerInfo> Extras
    {
        get
        {
            lock (_extrasLock)
            {
                return _extras.Select(e => e.Host.Running).OfType<EmbeddedServerInfo>().ToList();
            }
        }
    }

    public async Task<EmbeddedServerInfo> StartExtraAsync(EmbeddedModel model, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(effective);
        if (!_files.State(model).IsInstalled)
        {
            throw new EmbeddedLlmException(EmbeddedLlmText.NotInstalled(model));
        }

        await _extrasGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ILlamaServerHost host;
            lock (_extrasLock)
            {
                var known = _extras.FirstOrDefault(e => string.Equals(e.ModelId, model.Id, StringComparison.OrdinalIgnoreCase));
                if (known.Host?.Running is { } up)
                {
                    return up;   // a second bot on the model, or a later botchat: the running extra, whatever its launch
                }

                host = known.Host ?? _extraHost();
                if (known.Host is null)
                {
                    _extras.Add((model.Id, host));
                }
            }

            DiagnosticLog.Info(Category, $"Starting an extra llama-server for {model.Id} (a multi-server botchat).");
            return await StartOnHostAsync(host, model, effective, phase, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _extrasGate.Release();
        }
    }

    public IReadOnlyList<EmbeddedServerInfo> StopExtras()
    {
        var stopped = new List<EmbeddedServerInfo>();
        _extrasGate.Wait();
        try
        {
            List<ILlamaServerHost> hosts;
            lock (_extrasLock)
            {
                hosts = _extras.Select(e => e.Host).ToList();
            }

            foreach (var host in hosts)
            {
                if (host.Running is { } running)
                {
                    host.Stop();
                    stopped.Add(running);
                    DiagnosticLog.Info(Category, $"Stopped the extra llama-server for {running.ModelId}.");
                }
            }
        }
        finally
        {
            _extrasGate.Release();
        }

        return stopped;
    }

    /// <summary>
    /// A start on <paramref name="host"/> — the main one or an extra: the backend <paramref name="effective"/> picks, and the
    /// CUDA to Vulkan fallback when <c>auto</c> chose CUDA and it does not start.
    /// </summary>
    private async Task<EmbeddedServerInfo> StartOnHostAsync(ILlamaServerHost host, EmbeddedModel model, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        int kills = Volatile.Read(ref _kills);
        lock (_startingLock)
        {
            _starting.Add(model.Id);
        }

        try
        {
            var choice = Backend(effective);
            bool auto = EmbeddedBackends.Forced(effective.EmbeddedBackend) is null;
            DiagnosticLog.Info(Category, $"Backend {LlamaRelease.Name(choice.Backend)} ({choice.Reason}).");
            try
            {
                return await StartOnAsync(host, choice.Backend, model, effective, auto, phase, kills, cancellationToken).ConfigureAwait(false);
            }
            catch (EmbeddedLlmException ex) when (auto && choice.Backend == LlamaBackend.Cuda && !ex.RuntimeMissing && !ex.VramOnlyRefused && !ex.Killed
                && !cancellationToken.IsCancellationRequested && !KilledSince(kills))
            {
                _cudaFailed = true;
                DiagnosticLog.Warn(Category, EmbeddedLlmText.CudaFallback(ex.Message));
                return await StartOnAsync(host, LlamaBackend.Vulkan, model, effective, auto, phase, kills, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (EmbeddedLlmException ex) when (!ex.Killed && KilledSince(kills))
        {
            // The kill switch took the start (2026-10-01): whatever the start then threw is the kill's.
            throw new EmbeddedLlmException(EmbeddedLlmText.KilledStart(model), killed: true);
        }
        finally
        {
            lock (_startingLock)
            {
                _starting.Remove(model.Id);
            }
        }
    }

    /// <summary>Whether the kill switch was pressed since a start read <paramref name="kills"/>.</summary>
    private bool KilledSince(int kills) => Volatile.Read(ref _kills) != kills;

    public string? Remove(EmbeddedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_host.Running is { } running && string.Equals(running.ModelId, model.Id, StringComparison.OrdinalIgnoreCase))
        {
            _host.Stop();
        }

        // An extra server with the model loaded goes too (later on 2026-09-29): its files are memory-mapped until it does.
        List<ILlamaServerHost> extras;
        lock (_extrasLock)
        {
            extras = _extras.Where(e => string.Equals(e.ModelId, model.Id, StringComparison.OrdinalIgnoreCase)).Select(e => e.Host).ToList();
        }

        extras.ForEach(host => host.Stop());
        return _files.Remove(model);
    }

    public void Stop() => _host.Stop();

    public bool Starting
    {
        get
        {
            lock (_startingLock)
            {
                return _starting.Count > 0;
            }
        }
    }

    public IReadOnlyList<string> Kill()
    {
        Interlocked.Increment(ref _kills);
        var ids = new List<string>();
        lock (_startingLock)
        {
            ids.AddRange(_starting);
        }

        // The extras' list is read, never cleared: a later botchat reuses their hosts, as after StopExtras.
        List<ILlamaServerHost> hosts;
        lock (_extrasLock)
        {
            hosts = [_host, .. _extras.Select(e => e.Host)];
        }

        foreach (var host in hosts)
        {
            string? id = host.Running?.ModelId;
            if (host.Kill() && id is not null)
            {
                ids.Add(id);
            }
        }

        var unloaded = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unloaded.Count > 0)
        {
            DiagnosticLog.Info(Category, EmbeddedLlmText.KilledLog(unloaded));
        }

        return unloaded;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync().ConfigureAwait(false);
        List<ILlamaServerHost> extras;
        lock (_extrasLock)
        {
            extras = _extras.Select(e => e.Host).ToList();
            _extras.Clear();
        }

        foreach (var host in extras)
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }

        _extrasGate.Dispose();
    }

    private async Task<EmbeddedServerInfo> StartOnAsync(ILlamaServerHost host, LlamaBackend backend, EmbeddedModel model, AppSettingsData effective, bool auto, Action<string>? phase, int kills, CancellationToken cancellationToken)
    {
        // Embedded VRAM only (2026-10-01, the user's ask): every layer on the GPU whatever Embedded GPU layers says, and
        // no start at all on the CPU backend, which has no VRAM to stay in — nor, the same day (the review's finding, the
        // user's call), on Vulkan over a GPU with little or no memory of its own: an integrated GPU's allocations are all
        // shared system memory, so every load would read as a spill. CUDA is never an integrated GPU here.
        bool vramOnly = effective.EmbeddedVramOnly;
        if (vramOnly && backend == LlamaBackend.Cpu)
        {
            throw new EmbeddedLlmException(EmbeddedLlmText.VramOnlyOnCpu, vramOnlyRefused: true);
        }

        if (vramOnly && backend == LlamaBackend.Vulkan && (_totalVram() is not { } dedicated || dedicated < (long)MinDedicatedVramMiB << 20))
        {
            throw new EmbeddedLlmException(EmbeddedLlmText.VramOnlyNoVram, vramOnlyRefused: true);
        }

        var runtime = await _files.EnsureRuntimeAsync(backend, phase, cancellationToken).ConfigureAwait(false);
        if (!runtime.Ok)
        {
            throw new EmbeddedLlmException(EmbeddedLlmText.RuntimeFailed(runtime.Detail), runtimeMissing: true);
        }

        // The projector with Embedded vision on and the file there.
        string mmproj = _files.MmprojPath(model);
        var (drafter, mtp) = await MtpAsync(model, effective, phase, cancellationToken).ConfigureAwait(false);
        var launch = new LlamaLaunch(
            _files.Executable(backend),
            backend,
            _files.WeightsPath(model),
            effective.EmbeddedVision && File.Exists(mmproj) ? mmproj : null,
            model.Id,
            EmbeddedContextSize.Effective(effective.EmbeddedContextSize),
            vramOnly ? EmbeddedGpuLayers.All : EmbeddedGpuLayers.Effective(effective.EmbeddedGpuLayers),
            model.Sampling,
            auto,
            drafter,
            mtp,
            FitTarget(backend, effective),
            model.Draft,
            vramOnly);
        if (KilledSince(kills))
        {
            // Killed while the runtime or the drafter downloaded (2026-10-01): no process to kill yet, so none is started.
            throw new EmbeddedLlmException(EmbeddedLlmText.KilledStart(model), killed: true);
        }

        phase?.Invoke(EmbeddedLlmText.StartingLabel(model));
        return await host.EnsureRunningAsync(launch, model, phase, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The <c>--fit-target</c> of a launch (later on 2026-09-29, the user's ask: Embedded VRAM budget): null — llama.cpp's
    /// own 1 GiB margin — with the budget off, on the CPU backend, or when no GPU memory is read (a warning then); else the
    /// MiB that leaves the budget's share of the biggest adapter to fill. Per backend, so a CUDA start that falls back to
    /// Vulkan keeps it.
    /// </summary>
    private int? FitTarget(LlamaBackend backend, AppSettingsData effective)
    {
        int budget = EmbeddedVramBudget.Effective(effective.EmbeddedVramBudget);
        if (budget == EmbeddedVramBudget.Off || backend == LlamaBackend.Cpu)
        {
            return null;
        }

        if (_totalVram() is not { } total || total <= 0)
        {
            DiagnosticLog.Warn(Category, string.Create(CultureInfo.InvariantCulture, $"Embedded VRAM budget {budget}% not applied: no GPU memory was read, so llama.cpp keeps its own 1 GiB margin."));
            return null;
        }

        int margin = EmbeddedVramBudget.FitTargetMiB(total, budget);
        DiagnosticLog.Info(Category, string.Create(CultureInfo.InvariantCulture, $"Embedded VRAM budget {budget}% of {total / 1_048_576} MiB: fit target {margin} MiB."));
        return margin;
    }

    /// <summary>
    /// The MTP half of a launch (2026-09-29): nothing with Embedded drafter off or for a model that cannot draft; a
    /// built-in head (Qwen3.8) is on with no file; a drafter (Gemma 4's MTP, Muse Glimmer's DFlash) is fetched first when
    /// a model installed before drafters joined the catalog lacks it. A drafter that cannot be fetched is logged and the model starts without
    /// MTP — slower, never broken; the next start tries again.
    /// </summary>
    private async Task<(string? Drafter, bool Mtp)> MtpAsync(EmbeddedModel model, AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        if (!effective.EmbeddedDrafter || !model.HasMtp)
        {
            return (null, false);
        }

        if (_files.DrafterPath(model) is not { } drafter)
        {
            return (null, true);
        }

        if (!File.Exists(drafter))
        {
            var fetched = await _files.EnsureDrafterAsync(model, phase, cancellationToken, EmbeddedHfDownloadTypes.Connections(effective)).ConfigureAwait(false);
            if (fetched is not { Ok: true })
            {
                DiagnosticLog.Warn(Category, EmbeddedLlmText.DrafterFailed(model, fetched?.Detail ?? ""));
                return (null, false);
            }
        }

        return (drafter, true);
    }
}
