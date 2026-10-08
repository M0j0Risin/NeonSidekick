using System.Net;
using System.Net.Http.Headers;
using NeonSidekick.Diagnostics;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Settings;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="EmbeddedModels"/> over a tiny catalog of fake GGUF files with real checksums, served by a stub handler, and
/// <see cref="EmbeddedLlmService"/> over it and a <see cref="FakeLlamaServerHost"/> (2026-09-29).
/// </summary>
public class EmbeddedModelsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly StubHttpMessageHandler _http = new();
    private readonly byte[] _weights = FakeModelFiles.GgufBytes(30_000);
    private readonly byte[] _mmproj = FakeModelFiles.GgufBytes(10_000)[..9_000];
    private readonly byte[] _runtimeZip = FakeModelFiles.Zip(("llama-server.exe", "server"), ("ggml-cuda.dll", "cuda"));
    private readonly byte[] _vulkanZip = FakeModelFiles.Zip(("llama-server.exe", "server"), ("ggml-vulkan.dll", "vulkan"));
    private readonly EmbeddedModel _model;
    private readonly EmbeddedModels _files;

    public EmbeddedModelsTests()
    {
        _model = new EmbeddedModel(
            "tiny-model",
            "Tiny Model",
            "Q4",
            "test/tiny",
            "abc",
            new EmbeddedFile("tiny.gguf", _weights.Length, FakeModelFiles.Sha256(_weights)),
            new EmbeddedFile("mmproj-tiny.gguf", _mmproj.Length, FakeModelFiles.Sha256(_mmproj)),
            new EmbeddedSampling(1.0, 0.95, 64));
        _files = new EmbeddedModels(ModelsDir, LlamaDir, new HttpClient(_http), [_model], Runtime);
        Serve(EmbeddedModelCatalog.Url(_model, _model.Model).AbsoluteUri, _weights);
        Serve(EmbeddedModelCatalog.Url(_model, _model.Mmproj!).AbsoluteUri, _mmproj);
        Serve("https://example.test/cuda.zip", _runtimeZip);
        Serve("https://example.test/vulkan.zip", _vulkanZip);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    private string ModelsDir => Path.Combine(_home, "models", "llm");

    private string LlamaDir => Path.Combine(_home, "llama");

    private string SharedDir => EmbeddedModelCatalog.SharedFolder(ModelsDir);

    private ArchiveSetSpec Runtime(LlamaBackend backend)
    {
        var zip = backend == LlamaBackend.Vulkan ? _vulkanZip : _runtimeZip;
        string name = backend == LlamaBackend.Vulkan ? "vulkan.zip" : "cuda.zip";
        return new ArchiveSetSpec(
            EmbeddedLlmText.RuntimeDisplay(backend),
            LlamaRelease.Folder(LlamaDir, backend),
            [new ArchivePart(name, new Uri("https://example.test/" + name), zip.Length, FakeModelFiles.Sha256(zip))],
            [LlamaRelease.ServerExecutable]);
    }

    private void Serve(string url, byte[] body) =>
        _http.Map(url, (request, _) =>
        {
            if (request.Headers.Range?.Ranges.FirstOrDefault()?.From is { } from)
            {
                var rest = new ByteArrayContent(body[(int)from..]);
                rest.Headers.ContentRange = new ContentRangeHeaderValue(from, body.Length - 1, body.Length);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = rest });
            }

            return Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, body, "application/octet-stream"));
        });

    // ── EmbeddedModels ─────────────────────────────────────────────────────────

    [Fact]
    public void State_IsAbsent_ThenPartial_ThenInstalled()
    {
        Assert.Equal(EmbeddedModelState.Absent, _files.State(_model));

        string partial = ModelStore.PartialPath(_files.WeightsPath(_model));
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        File.WriteAllBytes(partial, _weights[..19_500]);
        Assert.Equal(new EmbeddedModelState(EmbeddedModelStateKind.Partial, 50), _files.State(_model));   // 19500 of 39000

        File.WriteAllBytes(_files.WeightsPath(_model), _weights);
        File.Delete(partial);
        Directory.CreateDirectory(SharedDir);
        File.WriteAllBytes(_files.MmprojPath(_model)!, _mmproj);
        Assert.Equal(EmbeddedModelState.Installed, _files.State(_model));
        Assert.Equal([_model], _files.Installed());
        Assert.Equal(39_000, _files.InstalledBytes());
    }

    [Fact]
    public void State_CountsTheParts_OfAParallelDownload()
    {
        // 2026-09-30 (Embedded HF download type parallel): a download cut short leaves parts, not a .partial.
        string weights = _files.WeightsPath(_model);
        Directory.CreateDirectory(Path.GetDirectoryName(weights)!);
        File.WriteAllBytes(ParallelDownload.PartPath(weights, 0, 8), new byte[9_000]);
        File.WriteAllBytes(ParallelDownload.PartPath(weights, 5, 8), new byte[10_500]);

        Assert.Equal(new EmbeddedModelState(EmbeddedModelStateKind.Partial, 50), _files.State(_model));   // 19500 of 39000
    }

    [Fact]
    public async Task Install_FetchesTheWeightsThenTheProjector_Checked()
    {
        var labels = new List<string>();

        var result = await _files.InstallAsync(_model, labels.Add, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(EmbeddedLlmText.Installed(_model), result.Detail);
        Assert.Equal(_weights, File.ReadAllBytes(Path.Combine(ModelsDir, "tiny-model", "tiny.gguf")));
        Assert.Equal(_mmproj, File.ReadAllBytes(EmbeddedModelCatalog.SharedPath(ModelsDir, _model.Mmproj)));   // in _shared since 2026-10-06
        Assert.False(File.Exists(Path.Combine(ModelsDir, "tiny-model", "mmproj-tiny.gguf")));
        Assert.True(_files.State(_model).IsInstalled);
        Assert.Contains("verifying Tiny Model…", labels);
        Assert.Contains("verifying Tiny Model vision…", labels);
        Assert.Contains(labels, l => l.StartsWith("downloading Tiny Model (30 KB)…", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Install_AProjectorThatFailsItsChecksum_LeavesTheModelUninstalled()
    {
        var bad = _model with { Mmproj = _model.Mmproj! with { Sha256 = new string('1', 64) } };
        var files = new EmbeddedModels(ModelsDir, LlamaDir, new HttpClient(_http), [bad], Runtime);

        var result = await files.InstallAsync(bad, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ModelStore.ChecksumError("Tiny Model vision"), result.Detail);
        Assert.Equal(EmbeddedModelStateKind.Partial, files.State(bad).Kind);   // the weights are whole; the projector is not
    }

    [Fact]
    public async Task Runtime_IsInstalledOnce_AndCounted()
    {
        Assert.False(_files.RuntimeInstalled(LlamaBackend.Cuda));
        Assert.Equal(_runtimeZip.Length, _files.RuntimeBytesToDownload(LlamaBackend.Cuda));

        var result = await _files.EnsureRuntimeAsync(LlamaBackend.Cuda, null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.True(_files.RuntimeInstalled(LlamaBackend.Cuda));
        Assert.Equal(0, _files.RuntimeBytesToDownload(LlamaBackend.Cuda));
        Assert.Equal(Path.Combine(LlamaDir, "b11258-cuda", "llama-server.exe"), _files.Executable(LlamaBackend.Cuda));
        Assert.Equal("present", (await _files.EnsureRuntimeAsync(LlamaBackend.Cuda, null, CancellationToken.None)).Detail);
    }

    [Fact]
    public void Remove_DeletesTheFolder_PartialsAndAll()
    {
        Directory.CreateDirectory(Path.Combine(ModelsDir, "tiny-model"));
        File.WriteAllBytes(ModelStore.PartialPath(_files.WeightsPath(_model)), [1, 2, 3]);

        Assert.Null(_files.Remove(_model));

        Assert.False(Directory.Exists(Path.Combine(ModelsDir, "tiny-model")));
        Assert.Null(_files.Remove(_model));   // nothing there: still fine
    }

    [Fact]
    public void PruneOldRuntimes_DeletesOtherBuilds_AndNothingElse()
    {
        foreach (var name in new[] { "b10000-cuda", "b11000-vulkan", LlamaRelease.Tag + "-cuda", "notes", "bx-cpu" })
        {
            Directory.CreateDirectory(Path.Combine(LlamaDir, name));
        }

        _files.PruneOldRuntimes();

        Assert.Equal(["b11258-cuda", "bx-cpu", "notes"], Directory.EnumerateDirectories(LlamaDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    // ── Shared projectors and drafters (2026-10-06) ──────────────────────────

    /// <summary>
    /// Two builds of one repository: their own weights, the same projector and drafter (the catalog's E2B and E4B builds'
    /// shape), and an EmbeddedModels over both.
    /// </summary>
    private (EmbeddedModel A, EmbeddedModel B, EmbeddedModels Files, byte[] Drafter, byte[] WeightsB) TwoBuilds()
    {
        var (a, _, drafter) = WithDrafter();
        byte[] weightsB = FakeModelFiles.GgufBytes(20_000);
        var b = a with { Id = "tiny-model-q8", Quant = "Q8", Model = new EmbeddedFile("tiny-q8.gguf", weightsB.Length, FakeModelFiles.Sha256(weightsB)) };
        Serve(EmbeddedModelCatalog.Url(b, b.Model).AbsoluteUri, weightsB);
        return (a, b, new EmbeddedModels(ModelsDir, LlamaDir, new HttpClient(_http), [a, b], Runtime), drafter, weightsB);
    }

    private int Fetches(string name) => _http.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/" + name, StringComparison.Ordinal));

    [Fact]
    public async Task ASecondBuild_DownloadsItsWeightsAlone_TheSharedFilesOnce()
    {
        var (a, b, files, drafter, _) = TwoBuilds();
        Assert.Equal(EmbeddedModelCatalog.TotalBytes(b), files.BytesToDownload(b));

        Assert.True((await files.InstallAsync(a, null, CancellationToken.None)).Ok);
        Assert.Equal(b.Model.Bytes, files.BytesToDownload(b));   // the projector and drafter are already there
        Assert.True((await files.InstallAsync(b, null, CancellationToken.None)).Ok);

        Assert.Equal((1, 1), (Fetches("mmproj-tiny.gguf"), Fetches("mtp-tiny.gguf")));
        Assert.True(files.State(a).IsInstalled && files.State(b).IsInstalled);
        Assert.Equal(files.MmprojPath(a), files.MmprojPath(b));
        Assert.Equal(drafter, File.ReadAllBytes(files.DrafterPath(b)!));
        Assert.Equal(2, Directory.GetFiles(SharedDir).Length);
        Assert.Equal(["tiny-q8.gguf"], Directory.GetFiles(EmbeddedModelCatalog.Folder(ModelsDir, b)).Select(Path.GetFileName));
        Assert.Equal(a.Model.Bytes + b.Model.Bytes + a.Mmproj.Bytes + a.Drafter!.Bytes, files.InstalledBytes());   // each shared file once
        Assert.Equal(0, files.BytesToDownload(b));
    }

    [Fact]
    public async Task Remove_KeepsTheSharedFiles_WhileAnotherBuildHoldsThem()
    {
        var (a, b, files, _, weightsB) = TwoBuilds();
        Assert.True((await files.InstallAsync(a, null, CancellationToken.None)).Ok);
        Directory.CreateDirectory(EmbeddedModelCatalog.Folder(ModelsDir, b));
        File.WriteAllBytes(ModelStore.PartialPath(files.WeightsPath(b)), weightsB[..100]);   // B part-way down still holds them
        Assert.Equal(a.Model.Bytes, files.FreedBytes(a));

        Assert.Null(files.Remove(a));

        Assert.False(Directory.Exists(EmbeddedModelCatalog.Folder(ModelsDir, a)));
        Assert.True(File.Exists(EmbeddedModelCatalog.SharedPath(ModelsDir, a.Mmproj)));
        Assert.True(File.Exists(EmbeddedModelCatalog.SharedPath(ModelsDir, a.Drafter!)));

        // The last build to go takes them with it.
        Assert.Equal(EmbeddedModelCatalog.TotalBytes(b), files.FreedBytes(b));
        Assert.Null(files.Remove(b));
        Assert.Empty(Directory.GetFiles(SharedDir));
    }

    /// <summary>The copies earlier installs kept in each folder: one moves into _shared, the other goes; both builds stay installed.</summary>
    [Fact]
    public void Merge_FoldsTheOldPerFolderCopies_IntoOne()
    {
        var (a, b, files, drafter, weightsB) = TwoBuilds();
        foreach (var (model, weights) in new[] { (a, _weights), (b, weightsB) })
        {
            string folder = EmbeddedModelCatalog.Folder(ModelsDir, model);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, model.Model.Name), weights);
            File.WriteAllBytes(Path.Combine(folder, model.Mmproj.Name), _mmproj);
            File.WriteAllBytes(Path.Combine(folder, model.Drafter!.Name), drafter);
        }

        Assert.True(files.State(a).IsInstalled);   // the first read merges

        Assert.True(files.State(b).IsInstalled);
        Assert.Equal(_mmproj, File.ReadAllBytes(EmbeddedModelCatalog.SharedPath(ModelsDir, a.Mmproj)));
        Assert.Equal(drafter, File.ReadAllBytes(EmbeddedModelCatalog.SharedPath(ModelsDir, a.Drafter!)));
        Assert.Equal(EmbeddedModelCatalog.SharedPath(ModelsDir, a.Mmproj), files.MmprojPath(b));
        foreach (var model in new[] { a, b })
        {
            Assert.Equal([model.Model.Name], Directory.GetFiles(EmbeddedModelCatalog.Folder(ModelsDir, model)).Select(Path.GetFileName));
        }
    }

    /// <summary>A copy a running server has open is left where it is and read there; a later merge folds it in.</summary>
    [WindowsFact]
    public void Merge_LeavesACopyInUse_ReadWhereItIs_UntilTheNextMerge()
    {
        string folder = EmbeddedModelCatalog.Folder(ModelsDir, _model);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(_files.WeightsPath(_model), _weights);
        string legacy = Path.Combine(folder, _model.Mmproj.Name);
        File.WriteAllBytes(legacy, _mmproj);

        using (new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read))   // as llama-server maps it
        {
            Assert.True(_files.State(_model).IsInstalled);
            Assert.Equal(legacy, _files.MmprojPath(_model));
        }

        _files.MergeShared();

        Assert.False(File.Exists(legacy));
        Assert.Equal(EmbeddedModelCatalog.SharedPath(ModelsDir, _model.Mmproj), _files.MmprojPath(_model));
        Assert.True(_files.State(_model).IsInstalled);
    }

    /// <summary>An install while a copy is in use copies it into _shared rather than downloading it again.</summary>
    [Fact]
    public async Task Install_CopiesASharedFileInUse_RatherThanDownloadingIt()
    {
        var (a, b, files, drafter, _) = TwoBuilds();
        string folder = EmbeddedModelCatalog.Folder(ModelsDir, a);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, a.Model.Name), _weights);
        string legacy = Path.Combine(folder, a.Mmproj.Name);
        File.WriteAllBytes(legacy, _mmproj);
        File.WriteAllBytes(Path.Combine(folder, a.Drafter!.Name), drafter);

        using (new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True((await files.InstallAsync(b, null, CancellationToken.None)).Ok);
        }

        Assert.Equal((0, 0), (Fetches("mmproj-tiny.gguf"), Fetches("mtp-tiny.gguf")));
        Assert.True(files.State(b).IsInstalled);
        Assert.Equal(_mmproj, File.ReadAllBytes(EmbeddedModelCatalog.SharedPath(ModelsDir, a.Mmproj)));
    }

    /// <summary>A part-way download in a model's folder moves to _shared with it, and the next install resumes it.</summary>
    [Fact]
    public async Task Merge_MovesAPartWayDownload_AndTheInstallResumesIt()
    {
        string folder = EmbeddedModelCatalog.Folder(ModelsDir, _model);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(_files.WeightsPath(_model), _weights);
        File.WriteAllBytes(ModelStore.PartialPath(Path.Combine(folder, _model.Mmproj.Name)), _mmproj[..4_000]);

        Assert.Equal(EmbeddedModelStateKind.Partial, _files.State(_model).Kind);
        Assert.True(File.Exists(ModelStore.PartialPath(EmbeddedModelCatalog.SharedPath(ModelsDir, _model.Mmproj))));
        Assert.True((await _files.InstallAsync(_model, null, CancellationToken.None)).Ok);

        Assert.Contains(_http.Requests, r => r.Uri.AbsolutePath.EndsWith("/mmproj-tiny.gguf", StringComparison.Ordinal));
        Assert.True(_files.State(_model).IsInstalled);
        Assert.Empty(Directory.GetFiles(folder, "*.partial*"));
    }

    /// <summary>The background install and a start's drafter fetch at once: one download of the shared drafter.</summary>
    [Fact]
    public async Task TwoFetchesOfOneSharedFile_DownloadItOnce()
    {
        byte[] drafter = FakeModelFiles.GgufBytes(6_000);
        byte[] weightsB = FakeModelFiles.GgufBytes(20_000);
        var a = _model with { Drafter = new EmbeddedFile("mtp-slow.gguf", drafter.Length, FakeModelFiles.Sha256(drafter)) };
        var b = a with { Id = "tiny-model-q8", Quant = "Q8", Model = new EmbeddedFile("tiny-q8.gguf", weightsB.Length, FakeModelFiles.Sha256(weightsB)) };
        var release = new TaskCompletionSource();
        _http.Map(EmbeddedModelCatalog.Url(a, a.Drafter!).AbsoluteUri, async (_, _) =>
        {
            await release.Task;
            return StubHttpMessageHandler.Bytes(HttpStatusCode.OK, drafter, "application/octet-stream");
        });
        Serve(EmbeddedModelCatalog.Url(b, b.Model).AbsoluteUri, weightsB);
        var files = new EmbeddedModels(ModelsDir, LlamaDir, new HttpClient(_http), [a, b], Runtime);
        Directory.CreateDirectory(EmbeddedModelCatalog.Folder(ModelsDir, a));
        File.WriteAllBytes(files.WeightsPath(a), _weights);

        var fetch = files.EnsureDrafterAsync(a, null, CancellationToken.None);
        var install = files.InstallAsync(b, null, CancellationToken.None);
        for (int i = 0; i < 500 && Fetches("mtp-slow.gguf") == 0; i++)
        {
            await Task.Delay(10);
        }

        await Task.Delay(100);   // the install's turn at the drafter, if the gate let it through
        release.SetResult();
        Assert.True((await install).Ok);
        Assert.True((await fetch)!.Value.Ok);

        Assert.Equal(1, Fetches("mtp-slow.gguf"));
        Assert.Equal(drafter, File.ReadAllBytes(files.DrafterPath(a)!));
        Assert.Equal(weightsB, File.ReadAllBytes(files.WeightsPath(b)));
    }

    [Fact]
    public void TheSharedPath_IsDistinctPerFile_AndNoCatalogIdIsTheSharedFolder()
    {
        var one = new EmbeddedFile("mmproj-F16.gguf", 1, new string('a', 64));
        var two = new EmbeddedFile("mmproj-F16.gguf", 1, new string('b', 64));
        Assert.NotEqual(EmbeddedModelCatalog.SharedPath(ModelsDir, one), EmbeddedModelCatalog.SharedPath(ModelsDir, two));
        Assert.Equal(Path.Combine(SharedDir, "aaaaaaaaaaaaaaaa-mmproj-F16.gguf"), EmbeddedModelCatalog.SharedPath(ModelsDir, one));
        Assert.DoesNotContain(EmbeddedModelCatalog.Models, m => string.Equals(m.Id, EmbeddedModelCatalog.SharedFolderName, StringComparison.OrdinalIgnoreCase));

        // The real catalog: the E2B builds share one projector and one drafter.
        var e2b = EmbeddedModelCatalog.Models.Where(m => m.Id.StartsWith("gemma-4-e2b", StringComparison.Ordinal) && m.Repository.StartsWith("unsloth/", StringComparison.Ordinal)).ToList();
        Assert.True(e2b.Count >= 5);
        Assert.Single(e2b.Select(m => EmbeddedModelCatalog.SharedPath(ModelsDir, m.Mmproj)).Distinct());
    }

    // ── EmbeddedLlmService ─────────────────────────────────────────────────────

    private void InstallByHand()
    {
        Directory.CreateDirectory(Path.Combine(ModelsDir, "tiny-model"));
        Directory.CreateDirectory(SharedDir);
        File.WriteAllBytes(_files.WeightsPath(_model), _weights);
        File.WriteAllBytes(_files.MmprojPath(_model)!, _mmproj);
    }

    private static BackendChoice Cuda(string? _) => new(LlamaBackend.Cuda, "test driver");

    [Fact]
    public async Task Start_InstallsTheRuntime_AndLaunchesUnderTheSettings()
    {
        InstallByHand();
        var host = new FakeLlamaServerHost();
        await using var service = new EmbeddedLlmService(_files, host, Cuda);
        var settings = new AppSettingsData { EmbeddedContextSize = 8192, EmbeddedGpuLayers = "all" };

        var info = await service.StartAsync(_model, settings, null, CancellationToken.None);

        Assert.Equal("tiny-model", info.ModelId);
        var launch = Assert.Single(host.Launches);
        Assert.Equal(_files.Executable(LlamaBackend.Cuda), launch.Executable);
        Assert.Equal(_files.WeightsPath(_model), launch.ModelPath);
        Assert.Equal(Files.ImageCodecs.Available ? _files.MmprojPath(_model) : null, launch.MmprojPath);   // no projector where pictures cannot be read (2026-10-07)
        Assert.Equal("tiny-model", launch.Alias);
        Assert.Equal(8192, launch.ContextSize);
        Assert.Equal("all", launch.GpuLayers);
        Assert.True(launch.MayFallBack);
        Assert.True(_files.RuntimeInstalled(LlamaBackend.Cuda));

        // The same settings again: the same launch, which the real host reuses.
        await service.StartAsync(_model, settings, null, CancellationToken.None);
        Assert.Equal(host.Launches[0], host.Launches[1]);
    }

    [Fact]
    public async Task Start_WithVisionOff_LeavesTheProjectorOut()
    {
        InstallByHand();
        var host = new FakeLlamaServerHost();
        await using var service = new EmbeddedLlmService(_files, host, Cuda);

        var info = await service.StartAsync(_model, new AppSettingsData { EmbeddedVision = false }, null, CancellationToken.None);

        Assert.Null(host.Launches[0].MmprojPath);
        Assert.False(info.Vision);
    }

    [Fact]
    public async Task StartExtra_RunsOnAHostOfItsOwn_UnderTheBotsSettings_TheMainUntouched()
    {
        // A multi-server botchat's extra (later on 2026-09-29): its own host, the bot's profile's launch, reused while it runs.
        InstallByHand();
        var main = new FakeLlamaServerHost();
        var hosts = new List<FakeLlamaServerHost>();
        await using var service = new EmbeddedLlmService(_files, main, Cuda, () => 8L << 30, () =>
        {
            var host = new FakeLlamaServerHost { Port = 59990 - hosts.Count };
            hosts.Add(host);
            return host;
        });
        await service.StartAsync(_model, new AppSettingsData(), null, CancellationToken.None);

        var extra = await service.StartExtraAsync(_model, new AppSettingsData { EmbeddedContextSize = 4096, EmbeddedVramBudget = 90 }, null, CancellationToken.None);
        var again = await service.StartExtraAsync(_model, new AppSettingsData { EmbeddedContextSize = 8192 }, null, CancellationToken.None);

        var host = Assert.Single(hosts);
        Assert.Equal(59990, extra.Port);
        Assert.Same(extra, again);   // the running extra, whatever the second bot's settings
        var launch = Assert.Single(host.Launches);
        Assert.Equal(4096, launch.ContextSize);
        Assert.Equal(819, launch.FitTargetMiB);   // the bot's own VRAM budget
        Assert.Equal(0, main.Stops);
        Assert.Equal("tiny-model", Assert.Single(service.Extras).ModelId);
        Assert.NotNull(service.Running);

        // Stopping the extras leaves the main server; a model's removal stops an extra that has it loaded.
        Assert.Equal("tiny-model", Assert.Single(service.StopExtras()).ModelId);
        Assert.Empty(service.Extras);
        Assert.Empty(service.StopExtras());
        Assert.NotNull(service.Running);
        await service.StartExtraAsync(_model, new AppSettingsData(), null, CancellationToken.None);
        Assert.Single(hosts);   // the stopped host is started again, not a new one
        service.Remove(_model);
        Assert.Empty(service.Extras);
        await service.DisposeAsync();
        Assert.True(host.Disposed);
    }

    [Fact]
    public async Task StartExtra_OfAModelNotInstalled_Throws_AndMakesNoHost()
    {
        int made = 0;
        await using var service = new EmbeddedLlmService(_files, new FakeLlamaServerHost(), Cuda, () => null, () =>
        {
            made++;
            return new FakeLlamaServerHost();
        });

        await Assert.ThrowsAsync<EmbeddedLlmException>(() => service.StartExtraAsync(_model, new AppSettingsData(), null, CancellationToken.None));
        Assert.Equal(0, made);
    }

    [Fact]
    public async Task Start_WithAVramBudget_LeavesTheRestFree_OnAGpuBackendOnly()
    {
        // Later on 2026-09-29 (the user's ask): 90 % of an 8 GiB card leaves 819 MiB to --fit-target.
        InstallByHand();
        var host = new FakeLlamaServerHost();
        await using var service = new EmbeddedLlmService(_files, host, Cuda, () => 8L << 30);

        await service.StartAsync(_model, new AppSettingsData { EmbeddedVramBudget = 90 }, null, CancellationToken.None);
        await service.StartAsync(_model, new AppSettingsData { EmbeddedVramBudget = 95 }, null, CancellationToken.None);
        await service.StartAsync(_model, new AppSettingsData { EmbeddedVramBudget = EmbeddedVramBudget.Off }, null, CancellationToken.None);
        await service.StartAsync(_model, new AppSettingsData(), null, CancellationToken.None);   // 91 %, the default since 2026-09-30
        Assert.Equal(new int?[] { 819, 410, null, 737 }, host.Launches.Select(l => l.FitTargetMiB));
        Assert.NotEqual(host.Launches[0], host.Launches[1]);   // a budget change restarts the server

        // No GPU memory read: llama.cpp's own margin. The CPU backend: no fit target at all.
        var unread = new FakeLlamaServerHost();
        await using (var blind = new EmbeddedLlmService(_files, unread, Cuda, () => null))
        {
            await blind.StartAsync(_model, new AppSettingsData { EmbeddedVramBudget = 90 }, null, CancellationToken.None);
        }

        var cpu = new FakeLlamaServerHost();
        await using (var onCpu = new EmbeddedLlmService(_files, cpu, _ => new BackendChoice(LlamaBackend.Cpu, "test"), () => 8L << 30))
        {
            await onCpu.StartAsync(_model, new AppSettingsData { EmbeddedVramBudget = 90, EmbeddedVramOnly = false }, null, CancellationToken.None);   // VRAM only refuses the CPU
        }

        Assert.Null(unread.Launches.Single().FitTargetMiB);
        Assert.Null(cpu.Launches.Single().FitTargetMiB);
    }

    // ── MTP (2026-09-29) ─────────────────────────────────────────────────────

    private (EmbeddedModel Model, EmbeddedModels Files, byte[] Drafter) WithDrafter()
    {
        byte[] drafter = FakeModelFiles.GgufBytes(5_000);
        var model = _model with { Drafter = new EmbeddedFile("mtp-tiny.gguf", drafter.Length, FakeModelFiles.Sha256(drafter)) };
        Serve(EmbeddedModelCatalog.Url(model, model.Drafter!).AbsoluteUri, drafter);
        return (model, new EmbeddedModels(ModelsDir, LlamaDir, new HttpClient(_http), [model], Runtime), drafter);
    }

    [Fact]
    public async Task Install_FetchesTheDrafterLast_ButStateNeverWaitsForIt()
    {
        var (model, files, drafter) = WithDrafter();
        var labels = new List<string>();

        var result = await files.InstallAsync(model, labels.Add, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(drafter, File.ReadAllBytes(files.DrafterPath(model)!));
        Assert.Contains("verifying Tiny Model MTP…", labels);

        // A model installed before its drafter joined the catalog is still installed: the start fetches it.
        File.Delete(files.DrafterPath(model)!);
        Assert.True(files.State(model).IsInstalled);
        Assert.Null(files.DrafterPath(_model));
    }

    [Fact]
    public async Task Install_WithEmbeddedDrafterOff_LeavesTheDrafterOut_TheModelStillInstalled()
    {
        // Embedded drafter (2026-09-29, the user's ask): off, an install fetches the weights and the projector only.
        var (model, files, _) = WithDrafter();
        var host = new FakeLlamaServerHost();
        await using var service = new EmbeddedLlmService(files, host, Cuda);

        var result = await service.InstallAsync(model, new AppSettingsData { EmbeddedDrafter = false }, null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.True(files.State(model).IsInstalled);
        Assert.False(File.Exists(files.DrafterPath(model)!));
    }

    [Fact]
    public async Task Start_WithMtpOn_FetchesAMissingDrafter_AndDraftsWithIt()
    {
        var (model, files, drafter) = WithDrafter();
        Directory.CreateDirectory(EmbeddedModelCatalog.Folder(ModelsDir, model));
        Directory.CreateDirectory(SharedDir);
        File.WriteAllBytes(files.WeightsPath(model), _weights);
        File.WriteAllBytes(files.MmprojPath(model), _mmproj);
        var host = new FakeLlamaServerHost();
        await using var service = new EmbeddedLlmService(files, host, Cuda);

        await service.StartAsync(model, new AppSettingsData(), null, CancellationToken.None);

        Assert.Equal(drafter, File.ReadAllBytes(files.DrafterPath(model)!));
        Assert.Equal((files.DrafterPath(model), true), (host.Launches[0].DrafterPath, host.Launches[0].Mtp));

        // Embedded drafter off: neither, so the launch differs and the real host restarts the server.
        await service.StartAsync(model, new AppSettingsData { EmbeddedDrafter = false }, null, CancellationToken.None);
        Assert.Equal((null, false), (host.Launches[1].DrafterPath, host.Launches[1].Mtp));
    }

    [Fact]
    public async Task Start_WithADrafterThatWillNotDownload_RunsWithoutMtp()
    {
        var (model, files, _) = WithDrafter();
        var broken = model with { Drafter = model.Drafter! with { Sha256 = new string('2', 64) } };
        files = new EmbeddedModels(ModelsDir, LlamaDir, new HttpClient(_http), [broken], Runtime);
        Directory.CreateDirectory(EmbeddedModelCatalog.Folder(ModelsDir, broken));
        Directory.CreateDirectory(SharedDir);
        File.WriteAllBytes(files.WeightsPath(broken), _weights);
        File.WriteAllBytes(files.MmprojPath(broken), _mmproj);
        var host = new FakeLlamaServerHost();
        await using var service = new EmbeddedLlmService(files, host, Cuda);
        var warnings = new List<string>();
        void Heard(DiagnosticEvent entry)
        {
            if (entry.Level == DiagnosticLevel.Warning)
            {
                warnings.Add(entry.Message);
            }
        }

        DiagnosticLog.Emitted += Heard;
        try
        {
            await service.StartAsync(broken, new AppSettingsData(), null, CancellationToken.None);
        }
        finally
        {
            DiagnosticLog.Emitted -= Heard;
        }

        Assert.Equal((null, false), (host.Launches[0].DrafterPath, host.Launches[0].Mtp));
        Assert.Contains(warnings, w => w.StartsWith("Tiny Model's MTP drafter could not be downloaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_OfAModelWithAHead_DraftsWithNoFile()
    {
        InstallByHand();
        var headed = _model with { MtpHead = true };
        var host = new FakeLlamaServerHost();
        await using var service = new EmbeddedLlmService(new EmbeddedModels(ModelsDir, LlamaDir, new HttpClient(_http), [headed], Runtime), host, Cuda);

        await service.StartAsync(headed, new AppSettingsData(), null, CancellationToken.None);
        await service.StartAsync(_model, new AppSettingsData(), null, CancellationToken.None);

        Assert.Equal((null, true), (host.Launches[0].DrafterPath, host.Launches[0].Mtp));
        Assert.Equal((null, false), (host.Launches[1].DrafterPath, host.Launches[1].Mtp));   // no MTP at all: nothing
    }

    [Fact]
    public async Task Start_OfAModelNotInstalled_IsRefused()
    {
        await using var service = new EmbeddedLlmService(_files, new FakeLlamaServerHost(), Cuda);

        var ex = await Assert.ThrowsAsync<EmbeddedLlmException>(() => service.StartAsync(_model, new AppSettingsData(), null, CancellationToken.None));

        Assert.Equal(EmbeddedLlmText.NotInstalled(_model), ex.Message);
    }

    [Fact]
    public async Task AutoCuda_ThatFailsToStart_FallsBackToVulkan_AndRemembers()
    {
        InstallByHand();
        var host = new FakeLlamaServerHost { Fail = l => l.Backend == LlamaBackend.Cuda ? new EmbeddedLlmException("no CUDA device") : null };
        // A GPU of its own (8 GiB), not the machine's: Embedded VRAM only is on by default, and on Vulkan it refuses a GPU
        // with little or no dedicated memory, which is what a CI runner's probe reads (the v0.4.0 release run, 2026-10-02).
        await using var service = new EmbeddedLlmService(_files, host, Cuda, () => 8L << 30);

        var info = await service.StartAsync(_model, new AppSettingsData(), null, CancellationToken.None);

        Assert.Equal(LlamaBackend.Vulkan, info.Backend);
        Assert.Equal([LlamaBackend.Cuda, LlamaBackend.Vulkan], host.Launches.Select(l => l.Backend));
        Assert.True(_files.RuntimeInstalled(LlamaBackend.Vulkan));
        Assert.Equal(new BackendChoice(LlamaBackend.Vulkan, "CUDA failed to start earlier"), service.Backend(new AppSettingsData()));

        await service.StartAsync(_model, new AppSettingsData(), null, CancellationToken.None);
        Assert.Equal(LlamaBackend.Vulkan, host.Launches[^1].Backend);   // straight to Vulkan now
        Assert.Equal(3, host.Launches.Count);
    }

    /// <summary>
    /// The kill switch (Ctrl+Alt+X, 2026-10-01, the user's ask) during a load: the start's model unloaded without waiting for
    /// it, the start failing as the kill's — no Vulkan fallback, CUDA not marked failed — and nothing left to kill after.
    /// </summary>
    [Fact]
    public async Task Kill_DuringALoad_EndsTheStartAsTheKills_WithoutAFallback()
    {
        InstallByHand();
        var release = new TaskCompletionSource();
        var host = new FakeLlamaServerHost { StartGate = _ => release.Task };
        await using var service = new EmbeddedLlmService(_files, host, Cuda, () => 8L << 30);

        var start = service.StartAsync(_model, new AppSettingsData(), null, CancellationToken.None);
        for (int i = 0; i < 500 && host.Launches.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(service.Starting);
        Assert.Equal(["tiny-model"], service.Kill());
        release.SetResult();

        var ex = await Assert.ThrowsAsync<EmbeddedLlmException>(() => start);
        Assert.True(ex.Killed);
        Assert.Equal(EmbeddedLlmText.KilledStart(_model), ex.Message);
        Assert.Equal(LlamaBackend.Cuda, Assert.Single(host.Launches).Backend);   // no Vulkan after it
        Assert.Equal(new BackendChoice(LlamaBackend.Cuda, "test driver"), service.Backend(new AppSettingsData()));
        Assert.False(service.Starting);
        Assert.Empty(service.Kill());
    }

    /// <summary>The kill switch with servers up (2026-10-01): the main one and a botchat's extra, both killed; the extra's host is kept for a later chat.</summary>
    [Fact]
    public async Task Kill_StopsTheMainServerAndTheExtras()
    {
        InstallByHand();
        var main = new FakeLlamaServerHost();
        var hosts = new List<FakeLlamaServerHost>();
        await using var service = new EmbeddedLlmService(_files, main, Cuda, () => 8L << 30, () =>
        {
            var host = new FakeLlamaServerHost { Port = 59990 - hosts.Count };
            hosts.Add(host);
            return host;
        });
        await service.StartAsync(_model, new AppSettingsData(), null, CancellationToken.None);
        await service.StartExtraAsync(_model, new AppSettingsData(), null, CancellationToken.None);

        Assert.Equal(["tiny-model"], service.Kill());

        Assert.Null(service.Running);
        Assert.Empty(service.Extras);
        Assert.Equal((1, 1), (main.Kills, hosts.Single().Kills));
        Assert.Equal(0, main.Stops);
        await service.StartExtraAsync(_model, new AppSettingsData(), null, CancellationToken.None);
        Assert.Single(hosts);
    }

    [Fact]
    public async Task ACudaRuntimeThatWillNotDownload_IsNoReasonToFallBack()
    {
        InstallByHand();
        var files = new EmbeddedModels(ModelsDir, LlamaDir, new HttpClient(_http), [_model], b => b == LlamaBackend.Cuda
            ? Runtime(b) with { Parts = [new ArchivePart("cuda.zip", new Uri("https://example.test/gone.zip"), 10, new string('0', 64))] }
            : Runtime(b));
        var host = new FakeLlamaServerHost();
        await using var service = new EmbeddedLlmService(files, host, Cuda);

        var ex = await Assert.ThrowsAsync<EmbeddedLlmException>(() => service.StartAsync(_model, new AppSettingsData(), null, CancellationToken.None));

        Assert.True(ex.RuntimeMissing);
        Assert.StartsWith("the llama.cpp runtime could not be installed: ", ex.Message);
        Assert.Empty(host.Launches);
        Assert.Equal(LlamaBackend.Cuda, service.Backend(new AppSettingsData()).Backend);   // CUDA is still the choice
        Assert.False(files.RuntimeInstalled(LlamaBackend.Vulkan));
    }

    [WindowsFact]   // "cuda" is no backend on a Mac, where it reads as auto (2026-10-07)
    public async Task ForcedCuda_ThatFailsToStart_Fails()
    {
        InstallByHand();
        var host = new FakeLlamaServerHost { Fail = _ => new EmbeddedLlmException("no CUDA device") };
        await using var service = new EmbeddedLlmService(_files, host, s => new BackendChoice(LlamaBackend.Cuda, "forced in settings"));

        var ex = await Assert.ThrowsAsync<EmbeddedLlmException>(() => service.StartAsync(_model, new AppSettingsData { EmbeddedBackend = "cuda" }, null, CancellationToken.None));

        Assert.Equal("no CUDA device", ex.Message);
        Assert.Single(host.Launches);
    }

    [Fact]
    public async Task Start_WithVramOnly_PutsEveryLayerOnTheGpu_AndRefusesTheCpu()
    {
        // 2026-10-01 (the user's ask): -ngl all whatever Embedded GPU layers says, and the launch carries the check.
        InstallByHand();
        var host = new FakeLlamaServerHost();
        await using var service = new EmbeddedLlmService(_files, host, Cuda);

        await service.StartAsync(_model, new AppSettingsData { EmbeddedVramOnly = true, EmbeddedGpuLayers = "20" }, null, CancellationToken.None);
        await service.StartAsync(_model, new AppSettingsData { EmbeddedVramOnly = false, EmbeddedGpuLayers = "20" }, null, CancellationToken.None);
        await service.StartAsync(_model, new AppSettingsData { EmbeddedGpuLayers = "20" }, null, CancellationToken.None);   // on by default since later on 2026-10-01
        Assert.Equal([("all", true), ("20", false), ("all", true)], host.Launches.Select(l => (l.GpuLayers, l.VramOnly)));

        // The CPU backend has no VRAM to stay in: refused before anything starts.
        var cpu = new FakeLlamaServerHost();
        await using var onCpu = new EmbeddedLlmService(_files, cpu, _ => new BackendChoice(LlamaBackend.Cpu, "test"));
        var ex = await Assert.ThrowsAsync<EmbeddedLlmException>(() => onCpu.StartAsync(_model, new AppSettingsData { EmbeddedVramOnly = true }, null, CancellationToken.None));
        Assert.Equal(EmbeddedLlmText.VramOnlyOnCpu, ex.Message);
        Assert.True(ex.VramOnlyRefused);
        Assert.Empty(cpu.Launches);
    }

    [Fact]
    public async Task VramOnly_OnVulkan_RefusesAGpuWithNoVramOfItsOwn()
    {
        // The review's finding (2026-10-01, the user's call: a clear refusal): an integrated GPU's every allocation is
        // shared memory, so the spill check would refuse every load with advice that cannot help.
        InstallByHand();
        static BackendChoice Vulkan(string? _) => new(LlamaBackend.Vulkan, "test");
        var on = new AppSettingsData { EmbeddedVramOnly = true };
        foreach (Func<long?> dedicated in new Func<long?>[] { () => 128L << 20, () => null, () => ((long)EmbeddedLlmService.MinDedicatedVramMiB << 20) - 1 })
        {
            var host = new FakeLlamaServerHost();
            await using var service = new EmbeddedLlmService(_files, host, Vulkan, dedicated);
            var ex = await Assert.ThrowsAsync<EmbeddedLlmException>(() => service.StartAsync(_model, on, null, CancellationToken.None));
            Assert.Equal(EmbeddedLlmText.VramOnlyNoVram, ex.Message);
            Assert.True(ex.VramOnlyRefused);
            Assert.Empty(host.Launches);
        }

        // A discrete card starts; so does a small one off VRAM only; CUDA is never an integrated GPU.
        var big = new FakeLlamaServerHost();
        await using (var service = new EmbeddedLlmService(_files, big, Vulkan, () => 8L << 30))
        {
            await service.StartAsync(_model, on, null, CancellationToken.None);
        }

        var small = new FakeLlamaServerHost();
        await using (var service = new EmbeddedLlmService(_files, small, Vulkan, () => 128L << 20))
        {
            await service.StartAsync(_model, new AppSettingsData { EmbeddedVramOnly = false }, null, CancellationToken.None);
        }

        var cuda = new FakeLlamaServerHost();
        await using (var service = new EmbeddedLlmService(_files, cuda, Cuda, () => 128L << 20))
        {
            await service.StartAsync(_model, on, null, CancellationToken.None);
        }

        Assert.Equal([true, false, true], new[] { big, small, cuda }.Select(h => h.Launches.Single().VramOnly));
    }

    [Fact]
    public async Task AVramOnlyRefusal_IsNoReasonToFallBack()
    {
        // A model that does not fit on CUDA does not fit on Vulkan either, and CUDA is not broken: no fallback, nothing remembered.
        InstallByHand();
        var host = new FakeLlamaServerHost { Fail = _ => new EmbeddedLlmException(EmbeddedLlmText.StartFailed(EmbeddedLlmText.VramDidNotFit), vramOnlyRefused: true) };
        await using var service = new EmbeddedLlmService(_files, host, Cuda);

        var ex = await Assert.ThrowsAsync<EmbeddedLlmException>(() => service.StartAsync(_model, new AppSettingsData { EmbeddedVramOnly = true }, null, CancellationToken.None));

        Assert.True(ex.VramOnlyRefused);
        Assert.Equal([LlamaBackend.Cuda], host.Launches.Select(l => l.Backend));
        Assert.Equal(LlamaBackend.Cuda, service.Backend(new AppSettingsData()).Backend);
    }

    [Fact]
    public async Task Install_PutsTheRuntimeInFirst_ThenTheModel()
    {
        var host = new FakeLlamaServerHost();
        await using var service = new EmbeddedLlmService(_files, host, Cuda);
        Assert.Equal(_runtimeZip.Length, service.RuntimeBytesToDownload(new AppSettingsData()));

        var result = await service.InstallAsync(_model, new AppSettingsData(), null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.True(service.State(_model).IsInstalled);
        Assert.Equal(0, service.RuntimeBytesToDownload(new AppSettingsData()));
        Assert.Empty(host.Launches);   // an install starts nothing
    }

    [Fact]
    public async Task Remove_StopsTheServer_WhenItHasTheModelLoaded()
    {
        InstallByHand();
        var host = new FakeLlamaServerHost();
        await using var service = new EmbeddedLlmService(_files, host, Cuda);
        await service.StartAsync(_model, new AppSettingsData(), null, CancellationToken.None);

        Assert.Null(service.Remove(_model));

        Assert.Equal(1, host.Stops);
        Assert.False(service.State(_model).IsInstalled);
    }
}
