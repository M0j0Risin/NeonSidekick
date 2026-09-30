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
        Assert.Equal(_mmproj, File.ReadAllBytes(Path.Combine(ModelsDir, "tiny-model", "mmproj-tiny.gguf")));
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

    // ── EmbeddedLlmService ─────────────────────────────────────────────────────

    private void InstallByHand()
    {
        Directory.CreateDirectory(Path.Combine(ModelsDir, "tiny-model"));
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
        Assert.Equal(_files.MmprojPath(_model), launch.MmprojPath);
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
        await service.StartAsync(_model, new AppSettingsData(), null, CancellationToken.None);
        Assert.Equal(new int?[] { 819, 410, null }, host.Launches.Select(l => l.FitTargetMiB));
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
            await onCpu.StartAsync(_model, new AppSettingsData { EmbeddedVramBudget = 90 }, null, CancellationToken.None);
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
        await using var service = new EmbeddedLlmService(_files, host, Cuda);

        var info = await service.StartAsync(_model, new AppSettingsData(), null, CancellationToken.None);

        Assert.Equal(LlamaBackend.Vulkan, info.Backend);
        Assert.Equal([LlamaBackend.Cuda, LlamaBackend.Vulkan], host.Launches.Select(l => l.Backend));
        Assert.True(_files.RuntimeInstalled(LlamaBackend.Vulkan));
        Assert.Equal(new BackendChoice(LlamaBackend.Vulkan, "CUDA failed to start earlier"), service.Backend(new AppSettingsData()));

        await service.StartAsync(_model, new AppSettingsData(), null, CancellationToken.None);
        Assert.Equal(LlamaBackend.Vulkan, host.Launches[^1].Backend);   // straight to Vulkan now
        Assert.Equal(3, host.Launches.Count);
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

    [Fact]
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
