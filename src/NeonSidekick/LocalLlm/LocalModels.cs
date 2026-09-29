using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Speech;

namespace NeonSidekick.LocalLlm;

/// <summary>Where a catalog model stands on disk.</summary>
public enum LocalModelStateKind
{
    Absent,
    Partial,
    Installed,
}

/// <summary>A model's state and, when part-way, how far its download got (whole percent of both files).</summary>
public readonly record struct LocalModelState(LocalModelStateKind Kind, int Percent = 0)
{
    public static readonly LocalModelState Absent = new(LocalModelStateKind.Absent);
    public static readonly LocalModelState Installed = new(LocalModelStateKind.Installed, 100);

    public bool IsInstalled => Kind == LocalModelStateKind.Installed;
}

/// <summary>
/// The local model's files (2026-09-29): which catalog models are installed, installing one (its weights, then its
/// vision projector — both resumable and SHA-256-checked through <see cref="ModelStore"/>), installing the llama.cpp
/// runtime a backend needs, removing a model, and pruning runtimes of builds no longer pinned. Knows nothing of
/// processes; <see cref="LocalLlmService"/> puts this and <see cref="ILlamaServerHost"/> together.
///
/// <para>The catalog and the runtime specs are constructor inputs so the tests can install tiny fake files with real
/// checksums from a stubbed HTTP handler; the app passes <see cref="LocalModelCatalog.Models"/> and
/// <see cref="LlamaRelease.Spec"/>.</para>
/// </summary>
public sealed class LocalModels
{
    private const string Category = "LocalLlm";

    private readonly ModelStore _store;
    private readonly Func<LlamaBackend, ArchiveSetSpec> _runtime;

    /// <param name="localModelsDirectory"><c>&lt;home&gt;/models/llm</c>: one folder per model.</param>
    /// <param name="llamaDirectory"><c>&lt;home&gt;/llama</c>: one folder per build and backend.</param>
    /// <param name="http">The download client (no timeout; a stub in tests).</param>
    /// <param name="catalog">The models offered; the app's <see cref="LocalModelCatalog.Models"/> when null.</param>
    /// <param name="runtime">A backend's runtime spec; <see cref="LlamaRelease.Spec"/> over <paramref name="llamaDirectory"/> when null.</param>
    public LocalModels(string localModelsDirectory, string llamaDirectory, HttpClient http, IReadOnlyList<LocalModel>? catalog = null, Func<LlamaBackend, ArchiveSetSpec>? runtime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localModelsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(llamaDirectory);
        ModelsDirectory = localModelsDirectory;
        LlamaDirectory = llamaDirectory;
        _store = new ModelStore(localModelsDirectory, http, Category);
        Catalog = catalog ?? LocalModelCatalog.Models;
        _runtime = runtime ?? (backend => LlamaRelease.Spec(llamaDirectory, backend));
    }

    public string ModelsDirectory { get; }

    public string LlamaDirectory { get; }

    public IReadOnlyList<LocalModel> Catalog { get; }

    /// <summary>The model's weights on disk (present or not).</summary>
    public string WeightsPath(LocalModel model) => LocalModelCatalog.WeightsSpec(ModelsDirectory, model).Path;

    /// <summary>The model's vision projector on disk (present or not).</summary>
    public string MmprojPath(LocalModel model) => LocalModelCatalog.MmprojSpec(ModelsDirectory, model).Path;

    /// <summary>Where <paramref name="model"/> stands: both files whole is installed; any bytes of either on disk is partial (with how far); nothing is absent.</summary>
    public LocalModelState State(LocalModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        long weights = HeldBytes(WeightsPath(model), model.Model.Bytes);
        long mmproj = HeldBytes(MmprojPath(model), model.Mmproj.Bytes);
        if (weights == model.Model.Bytes && mmproj == model.Mmproj.Bytes)
        {
            return LocalModelState.Installed;
        }

        long held = weights + mmproj;
        return held == 0
            ? LocalModelState.Absent
            : new LocalModelState(LocalModelStateKind.Partial, (int)Math.Min(99, held * 100 / LocalModelCatalog.TotalBytes(model)));
    }

    /// <summary>The catalog models that are installed, in catalog order.</summary>
    public IReadOnlyList<LocalModel> Installed() => Catalog.Where(m => State(m).IsInstalled).ToList();

    /// <summary>What the installed models take on disk.</summary>
    public long InstalledBytes() => Installed().Sum(LocalModelCatalog.TotalBytes);

    /// <summary>Whether <paramref name="backend"/>'s runtime is installed and complete.</summary>
    public bool RuntimeInstalled(LlamaBackend backend)
    {
        var spec = _runtime(backend);
        return ModelStore.IsCompleteModelDirectory(spec.Path, spec.RequiredFiles);
    }

    /// <summary>What installing <paramref name="backend"/>'s runtime would download: 0 when it is installed.</summary>
    public long RuntimeBytesToDownload(LlamaBackend backend) =>
        RuntimeInstalled(backend) ? 0 : _runtime(backend).Parts.Sum(p => p.Bytes);

    /// <summary>The server executable of <paramref name="backend"/>'s runtime.</summary>
    public string Executable(LlamaBackend backend) => Path.Combine(_runtime(backend).Path, LlamaRelease.ServerExecutable);

    /// <summary>Makes sure <paramref name="backend"/>'s runtime is installed, labelling the spinner through <paramref name="phase"/>.</summary>
    public async Task<ModelResult> EnsureRuntimeAsync(LlamaBackend backend, Action<string>? phase, CancellationToken cancellationToken)
    {
        var spec = _runtime(backend);
        var result = await _store.EnsureArchiveSetAsync(
            spec,
            VoiceSession.Progress(phase, spec.Display, spec.Parts.Sum(p => p.Bytes)),
            () => phase?.Invoke(LocalLlmText.VerifyingLabel(spec.Display)),
            () => phase?.Invoke(LocalLlmText.UnpackingLabel(spec.Display)),
            cancellationToken).ConfigureAwait(false);
        if (result.Ok && result.Detail != "present")
        {
            PruneOldRuntimes();
        }

        return result;
    }

    /// <summary>
    /// Installs <paramref name="model"/>: the weights, then the vision projector, each resumable and checked. A
    /// cancel keeps what arrived; the next install resumes it.
    /// </summary>
    public async Task<ModelResult> InstallAsync(LocalModel model, Action<string>? phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        foreach (var spec in new[] { LocalModelCatalog.WeightsSpec(ModelsDirectory, model), LocalModelCatalog.MmprojSpec(ModelsDirectory, model) })
        {
            var result = await _store.EnsureAsync(
                spec,
                VoiceSession.Progress(phase, spec.Display, spec.ApproxBytes),
                () => phase?.Invoke(LocalLlmText.VerifyingLabel(spec.Display)),
                cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                return result;
            }
        }

        return new ModelResult(true, LocalModelCatalog.Folder(ModelsDirectory, model), LocalLlmText.Installed(model));
    }

    /// <summary>
    /// Deletes <paramref name="model"/>'s folder, partial downloads and all. The caller stops a server that has it
    /// loaded first: Windows keeps a memory-mapped file open, and the delete would fail. Null on success, else why not.
    /// </summary>
    public string? Remove(LocalModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        string folder = LocalModelCatalog.Folder(ModelsDirectory, model);
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            DiagnosticLog.Info(Category, $"Removed {model.Display} ({folder}).");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not remove {folder}: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>
    /// Deletes the runtime folders of builds other than <see cref="LlamaRelease.Tag"/> (best effort, logged): after the
    /// pin moves, the old build's hundreds of megabytes serve nothing. Only folders shaped <c>b&lt;digits&gt;-&lt;backend&gt;</c>.
    /// </summary>
    public void PruneOldRuntimes()
    {
        if (!Directory.Exists(LlamaDirectory))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(LlamaDirectory))
        {
            string name = Path.GetFileName(folder);
            int dash = name.IndexOf('-', StringComparison.Ordinal);
            if (dash < 2 || name[0] != 'b' || !name[1..dash].All(char.IsAsciiDigit) || name.StartsWith(LlamaRelease.Tag + "-", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                Directory.Delete(folder, recursive: true);
                DiagnosticLog.Info(Category, $"Removed the old llama.cpp runtime {folder}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn(Category, $"Could not remove the old llama.cpp runtime {folder}: {ex.Message}");
            }
        }
    }

    /// <summary>The bytes of a file on disk: the whole file when its length is exact, else what its partial download holds.</summary>
    private static long HeldBytes(string path, long exact)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == exact)
            {
                return exact;
            }

            string partial = ModelStore.PartialPath(path);
            return File.Exists(partial) ? Math.Min(exact, new FileInfo(partial).Length) : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
