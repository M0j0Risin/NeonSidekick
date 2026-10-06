using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Speech;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>Where a catalog model stands on disk.</summary>
public enum EmbeddedModelStateKind
{
    Absent,
    Partial,
    Installed,
}

/// <summary>A model's state and, when part-way, how far its download got (whole percent of both files).</summary>
public readonly record struct EmbeddedModelState(EmbeddedModelStateKind Kind, int Percent = 0)
{
    public static readonly EmbeddedModelState Absent = new(EmbeddedModelStateKind.Absent);
    public static readonly EmbeddedModelState Installed = new(EmbeddedModelStateKind.Installed, 100);

    public bool IsInstalled => Kind == EmbeddedModelStateKind.Installed;
}

/// <summary>
/// The embedded model's files (2026-09-29): which catalog models are installed, installing one (its weights, then its
/// vision projector, then its MTP drafter when it has one — each resumable and SHA-256-checked through
/// <see cref="ModelStore"/>), installing the llama.cpp
/// runtime a backend needs, removing a model, and pruning runtimes of builds no longer pinned. Knows nothing of
/// processes; <see cref="EmbeddedLlmService"/> puts this and <see cref="ILlamaServerHost"/> together.
///
/// <para>The catalog and the runtime specs are constructor inputs so the tests can install tiny fake files with real
/// checksums from a stubbed HTTP handler; the app passes <see cref="EmbeddedModelCatalog.Models"/> and
/// <see cref="LlamaRelease.Spec"/>.</para>
///
/// <para>Projectors and drafters live once in <c>_shared</c> (2026-10-06, the code review's catch, the user's call;
/// <see cref="EmbeddedModelCatalog.SharedPath"/>): a build whose repository's projector and drafter are already there
/// downloads its weights alone, and a removal deletes a shared file only when no other model holds it. The copies earlier
/// installs left in each model's folder are folded in by <see cref="MergeShared"/>, and read where they are until then.
/// One download at a time per shared file: the background install and a start's drafter fetch can want the same one.</para>
/// </summary>
public sealed class EmbeddedModels
{
    private const string Category = "EmbeddedLlm";

    private readonly ModelStore _store;
    private readonly Func<LlamaBackend, ArchiveSetSpec> _runtime;
    private readonly Lock _gate = new();
    private readonly Lock _mergeLock = new();
    private readonly Dictionary<string, SemaphoreSlim> _fileGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private int _merged;

    /// <param name="embeddedModelsDirectory"><c>&lt;home&gt;/models/llm</c>: one folder per model.</param>
    /// <param name="llamaDirectory"><c>&lt;home&gt;/llama</c>: one folder per build and backend.</param>
    /// <param name="http">The download client (no timeout; a stub in tests).</param>
    /// <param name="catalog">The models offered; the app's <see cref="EmbeddedModelCatalog.Models"/> when null.</param>
    /// <param name="runtime">A backend's runtime spec; <see cref="LlamaRelease.Spec"/> over <paramref name="llamaDirectory"/> when null.</param>
    public EmbeddedModels(string embeddedModelsDirectory, string llamaDirectory, HttpClient http, IReadOnlyList<EmbeddedModel>? catalog = null, Func<LlamaBackend, ArchiveSetSpec>? runtime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddedModelsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(llamaDirectory);
        ModelsDirectory = embeddedModelsDirectory;
        LlamaDirectory = llamaDirectory;
        _store = new ModelStore(embeddedModelsDirectory, http, Category);
        Catalog = catalog ?? EmbeddedModelCatalog.Models;
        _runtime = runtime ?? (backend => LlamaRelease.Spec(llamaDirectory, backend));
    }

    public string ModelsDirectory { get; }

    public string LlamaDirectory { get; }

    public IReadOnlyList<EmbeddedModel> Catalog { get; }

    /// <summary>The model's weights on disk (present or not).</summary>
    public string WeightsPath(EmbeddedModel model) => EmbeddedModelCatalog.WeightsSpec(ModelsDirectory, model).Path;

    /// <summary>
    /// The model's vision projector on disk (present or not): the shared copy, else a whole copy an earlier install left in the
    /// model's folder (not yet merged, or in use as the merge ran), else where the shared one goes.
    /// </summary>
    public string MmprojPath(EmbeddedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return SharedOnDisk(model, model.Mmproj);
    }

    /// <summary>The model's MTP drafter on disk (present or not), as <see cref="MmprojPath"/>; null for a model without one.</summary>
    public string? DrafterPath(EmbeddedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Drafter is { } drafter ? SharedOnDisk(model, drafter) : null;
    }

    private string SharedOnDisk(EmbeddedModel model, EmbeddedFile file)
    {
        string shared = EmbeddedModelCatalog.SharedPath(ModelsDirectory, file);
        if (IsWhole(shared, file.Bytes))
        {
            return shared;
        }

        string legacy = EmbeddedModelCatalog.LegacyPath(ModelsDirectory, model, file);
        return IsWhole(legacy, file.Bytes) ? legacy : shared;
    }

    /// <summary>
    /// Where <paramref name="model"/> stands: the weights and the projector whole is installed; any bytes of either on
    /// disk is partial (with how far); nothing is absent. The MTP drafter does not count (2026-09-29): the models
    /// installed before drafters joined the catalog stay installed, and the start fetches a missing one
    /// (<see cref="EnsureDrafterAsync"/>).
    /// </summary>
    public EmbeddedModelState State(EmbeddedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        MergeOnce();
        long weights = HeldBytes(WeightsPath(model), model.Model.Bytes);
        long mmproj = HeldBytes(MmprojPath(model), model.Mmproj.Bytes);
        if (weights == model.Model.Bytes && mmproj == model.Mmproj.Bytes)
        {
            return EmbeddedModelState.Installed;
        }

        long held = weights + mmproj;
        return held == 0
            ? EmbeddedModelState.Absent
            : new EmbeddedModelState(EmbeddedModelStateKind.Partial, (int)Math.Min(99, held * 100 / (model.Model.Bytes + model.Mmproj.Bytes)));
    }

    /// <summary>The catalog models that are installed, in catalog order.</summary>
    public IReadOnlyList<EmbeddedModel> Installed() => Catalog.Where(m => State(m).IsInstalled).ToList();

    /// <summary>What the installed models take on disk: each one's weights, and every shared file once however many use it.</summary>
    public long InstalledBytes()
    {
        var installed = Installed();
        return installed.Sum(m => m.Model.Bytes)
            + installed.SelectMany(EmbeddedModelCatalog.SharedFiles).DistinctBy(f => f.Sha256.ToLowerInvariant()).Sum(f => f.Bytes);
    }

    /// <summary>What installing <paramref name="model"/> would download: each of its files not already whole on disk (2026-10-06: a shared projector or drafter another build brought counts nothing).</summary>
    public long BytesToDownload(EmbeddedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        long bytes = IsWhole(WeightsPath(model), model.Model.Bytes) ? 0 : model.Model.Bytes;
        foreach (var file in EmbeddedModelCatalog.SharedFiles(model))
        {
            bytes += IsWhole(SharedOnDisk(model, file), file.Bytes) ? 0 : file.Bytes;
        }

        return bytes;
    }

    /// <summary>What removing <paramref name="model"/> frees: its weights, and each shared file no other model holds.</summary>
    public long FreedBytes(EmbeddedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Model.Bytes + EmbeddedModelCatalog.SharedFiles(model).Where(f => !HeldByAnother(model, f)).Sum(f => f.Bytes);
    }

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
            () => phase?.Invoke(EmbeddedLlmText.VerifyingLabel(spec.Display)),
            () => phase?.Invoke(EmbeddedLlmText.UnpackingLabel(spec.Display)),
            cancellationToken).ConfigureAwait(false);
        if (result.Ok && result.Detail != "present")
        {
            PruneOldRuntimes();
        }

        return result;
    }

    /// <summary>
    /// Installs <paramref name="model"/>: the weights, then the vision projector, then the MTP drafter when it has one and
    /// <paramref name="drafter"/> is on (Embedded drafter; off, a later start with it on fetches it), each resumable and
    /// checked. A cancel keeps what arrived; the next install resumes it. <paramref name="connections"/> (2026-09-30, Embedded HF
    /// download type) is how many ranged connections each file comes down over; 1 is one stream.
    /// </summary>
    public async Task<ModelResult> InstallAsync(EmbeddedModel model, Action<string>? phase, CancellationToken cancellationToken, bool drafter = true, int connections = 1)
    {
        ArgumentNullException.ThrowIfNull(model);
        await Task.Run(() => MergeShared(copyInUse: true), cancellationToken).ConfigureAwait(false);   // a copy of an in-use file takes seconds
        var result = await EnsureAsync(EmbeddedModelCatalog.WeightsSpec(ModelsDirectory, model) with { Connections = connections }, phase, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return result;
        }

        var shared = new List<ModelSpec> { EmbeddedModelCatalog.MmprojSpec(ModelsDirectory, model) };
        if (drafter && EmbeddedModelCatalog.DrafterSpec(ModelsDirectory, model) is { } drafterSpec)
        {
            shared.Add(drafterSpec);
        }

        foreach (var spec in shared)
        {
            result = await EnsureSharedAsync(spec with { Connections = connections }, phase, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                return result;
            }
        }

        return new ModelResult(true, EmbeddedModelCatalog.Folder(ModelsDirectory, model), EmbeddedLlmText.Installed(model));
    }

    /// <summary>
    /// Makes sure <paramref name="model"/>'s MTP drafter is on disk (2026-09-29): a model installed before its drafter
    /// joined the catalog fetches it here, at its next start with Embedded drafter on. Null for a model without a drafter.
    /// </summary>
    public async Task<ModelResult?> EnsureDrafterAsync(EmbeddedModel model, Action<string>? phase, CancellationToken cancellationToken, int connections = 1)
    {
        ArgumentNullException.ThrowIfNull(model);
        return EmbeddedModelCatalog.DrafterSpec(ModelsDirectory, model) is { } spec
            ? await EnsureSharedAsync(spec with { Connections = connections }, phase, cancellationToken).ConfigureAwait(false)
            : null;
    }

    // A shared file's download, one at a time per file: a second caller waits, then finds it present. The path is marked in
    // flight meanwhile, so a removal or a merge leaves what is being written alone.
    private async Task<ModelResult> EnsureSharedAsync(ModelSpec spec, Action<string>? phase, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate;
        lock (_gate)
        {
            if (_fileGates.TryGetValue(spec.Path, out var existing))
            {
                gate = existing;
            }
            else
            {
                gate = new SemaphoreSlim(1, 1);
                _fileGates[spec.Path] = gate;
            }
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _inFlight.Add(spec.Path);
            }

            Directory.CreateDirectory(EmbeddedModelCatalog.SharedFolder(ModelsDirectory));
            return await EnsureAsync(spec, phase, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(spec.Path);
            }

            gate.Release();
        }
    }

    private Task<ModelResult> EnsureAsync(ModelSpec spec, Action<string>? phase, CancellationToken cancellationToken) =>
        _store.EnsureAsync(
            spec,
            VoiceSession.Progress(phase, spec.Display, spec.ApproxBytes),
            () => phase?.Invoke(EmbeddedLlmText.VerifyingLabel(spec.Display)),
            cancellationToken);

    /// <summary>
    /// Deletes <paramref name="model"/>'s folder, partial downloads and all, then each of its shared files that no other model
    /// holds (2026-10-06): one another model's weights are on disk for, whole or part-way, or one being downloaded, stays. The
    /// caller stops a server that has the model loaded first: Windows keeps a memory-mapped file open, and the delete would
    /// fail; a shared file another model's server has open is one that model holds, so it is never in the way. Null on
    /// success, else why not.
    /// </summary>
    public string? Remove(EmbeddedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        MergeShared();
        string folder = EmbeddedModelCatalog.Folder(ModelsDirectory, model);
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            foreach (var file in EmbeddedModelCatalog.SharedFiles(model))
            {
                string shared = EmbeddedModelCatalog.SharedPath(ModelsDirectory, file);
                if (!HeldByAnother(model, file) && !InFlight(shared))
                {
                    DeleteWithPartials(shared);
                }
            }

            DiagnosticLog.Info(Category, $"Removed {model.Display} ({folder}).");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not remove {model.Display}'s files: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>
    /// Folds the projectors and drafters earlier installs kept in each model's folder into <c>_shared</c> (2026-10-06): a
    /// whole copy moves there when the shared one is missing (pinned files reach their place only after their hash matched,
    /// so the length is proof, as <see cref="ModelStore"/> holds), and is deleted when it is already there; a part-way
    /// download moves too, so it resumes, or goes once the shared file is whole. Best effort and logged: a file a running
    /// <c>llama-server</c> has open is left, read where it is (<see cref="MmprojPath"/>), and tried again by the next merge.
    /// <paramref name="copyInUse"/> (an install, never a menu's read) copies such a file into <c>_shared</c> instead, so the
    /// install does not download it again. Runs before the first state read, and at each install and removal.
    /// </summary>
    public void MergeShared(bool copyInUse = false)
    {
        Volatile.Write(ref _merged, 1);
        lock (_mergeLock)
        {
            foreach (var model in Catalog)
            {
                foreach (var file in EmbeddedModelCatalog.SharedFiles(model))
                {
                    MergeOne(model, file, copyInUse);
                }
            }
        }
    }

    private void MergeOnce()
    {
        if (Volatile.Read(ref _merged) == 0)
        {
            MergeShared();
        }
    }

    private void MergeOne(EmbeddedModel model, EmbeddedFile file, bool copyInUse)
    {
        string legacy = EmbeddedModelCatalog.LegacyPath(ModelsDirectory, model, file);
        string shared = EmbeddedModelCatalog.SharedPath(ModelsDirectory, file);
        if (!Directory.Exists(Path.GetDirectoryName(legacy)) || InFlight(shared))
        {
            return;
        }

        try
        {
            if (IsWhole(legacy, file.Bytes))
            {
                if (IsWhole(shared, file.Bytes))
                {
                    File.Delete(legacy);
                    DiagnosticLog.Info(Category, $"Deleted {legacy}: the same file is in {EmbeddedModelCatalog.SharedFolderName}.");
                }
                else
                {
                    Directory.CreateDirectory(EmbeddedModelCatalog.SharedFolder(ModelsDirectory));
                    DeleteWithPartials(shared);   // a wrong-sized leftover or a part-way download of the same file
                    try
                    {
                        File.Move(legacy, shared);
                        DiagnosticLog.Info(Category, $"Moved {legacy} to {shared}.");
                    }
                    catch (IOException) when (copyInUse)
                    {
                        string copy = shared + ".copy";
                        File.Copy(legacy, copy, overwrite: true);
                        File.Move(copy, shared);
                        DiagnosticLog.Info(Category, $"Copied {legacy} (in use) to {shared}; the copy left goes at a later merge.");
                    }
                }
            }

            var parts = Partials(legacy);
            if (parts.Count > 0)
            {
                if (IsWhole(shared, file.Bytes))
                {
                    parts.ForEach(File.Delete);
                }
                else if (!File.Exists(shared) && Partials(shared).Count == 0)
                {
                    Directory.CreateDirectory(EmbeddedModelCatalog.SharedFolder(ModelsDirectory));
                    string from = Path.GetFileName(legacy);
                    string to = Path.GetFileName(shared);
                    foreach (string part in parts)
                    {
                        File.Move(part, Path.Combine(Path.GetDirectoryName(shared)!, to + Path.GetFileName(part)[from.Length..]));
                    }

                    DiagnosticLog.Info(Category, $"Moved the part-way download of {legacy} to {shared}.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Info(Category, $"Left {legacy} where it is for now: {ex.Message}");
        }
    }

    // Whether a model other than this one holds the file: its weights are on disk, whole or part-way, or it is being fetched.
    private bool HeldByAnother(EmbeddedModel model, EmbeddedFile file) =>
        Catalog.Any(other => !string.Equals(other.Id, model.Id, StringComparison.OrdinalIgnoreCase)
            && EmbeddedModelCatalog.Uses(other, file)
            && HeldBytes(WeightsPath(other), other.Model.Bytes) > 0);

    private bool InFlight(string path)
    {
        lock (_gate)
        {
            return _inFlight.Contains(path);
        }
    }

    // The file and every part-way download of it: its .partial and a parallel download's parts (ModelStore.PartialPath, ParallelDownload).
    private static void DeleteWithPartials(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        Partials(path).ForEach(File.Delete);
    }

    private static List<string> Partials(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory))
        {
            return [];
        }

        string partial = ModelStore.PartialPath(path);
        return Directory.EnumerateFiles(directory, Path.GetFileName(partial) + "*")
            .Where(f => f.Length == partial.Length || f[partial.Length] == '.')
            .ToList();
    }

    private static bool IsWhole(string path, long exact)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length == exact;
        }
        catch (IOException)
        {
            return false;
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

    /// <summary>The bytes of a file on disk: the whole file when its length is exact, else what its partial download holds (one stream's or a parallel one's parts, <see cref="ModelStore.PartialBytes"/>).</summary>
    private static long HeldBytes(string path, long exact)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == exact)
            {
                return exact;
            }

            return Math.Min(exact, ModelStore.PartialBytes(path));
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
