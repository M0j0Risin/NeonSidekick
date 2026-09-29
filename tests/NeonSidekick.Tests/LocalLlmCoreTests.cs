using NeonSidekick.Llm;
using NeonSidekick.LocalLlm;
using NeonSidekick.Speech;

namespace NeonSidekick.Tests;

/// <summary>
/// The local model's pure parts (2026-09-29): the sentinel endpoint, the catalog and the llama.cpp pins, the backend
/// choice, the command line, the settings' ranges and the wording.
/// </summary>
public class LocalLlmCoreTests
{
    // ── The sentinel ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("local", true)]
    [InlineData(" LOCAL ", true)]
    [InlineData("http://local-llm.invalid/v1", true)]
    [InlineData("http://LOCAL-LLM.INVALID/v1/", true)]
    [InlineData("http://127.0.0.1:8080/v1", false)]
    [InlineData("localhost", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLocal_KnowsTheAliasAndTheSentinel(string? url, bool local)
    {
        Assert.Equal(local, LocalEndpoint.IsLocal(url));
    }

    [Fact]
    public void TheSentinel_IsPinned_AndTheAliasNormalisesToIt()
    {
        Assert.Equal("http://local-llm.invalid/v1", LocalEndpoint.BaseUrl.AbsoluteUri);
        Assert.Equal(LocalEndpoint.BaseUrl, LlmEndpoint.NormalizeBaseUrl("local"));
        Assert.Equal(LocalEndpoint.BaseUrl, LlmEndpoint.NormalizeBaseUrl(" Local "));
        Assert.Equal("Local", LocalEndpoint.ServerName);
        Assert.True(LocalEndpoint.ServerName.Length <= App.SettingsMenu.ServerNameWidth);
        Assert.False(LocalEndpoint.IsLocal((Uri?)null));
    }

    [Fact]
    public void AnEndpoint_PostsToItsLiveUrl_WhenItHasOne()
    {
        var plain = new LlmEndpoint(new Uri("http://127.0.0.1:1234/v1"), "m", "k", "configured");
        Assert.Equal(plain.BaseUrl, plain.WireUrl);

        var local = new LlmEndpoint(LocalEndpoint.BaseUrl, "gemma-4-e2b", "k", "local") { LiveUrl = new Uri("http://127.0.0.1:5000/v1") };
        Assert.Equal(new Uri("http://127.0.0.1:5000/v1"), local.WireUrl);
        Assert.Equal(local.LiveUrl, (local with { ModelId = "x" }).LiveUrl);   // a bot's borrowed copy keeps it

        using var client = new OpenAICompatibleChatClient(local, TimeSpan.FromSeconds(5));
        Assert.Equal(LocalEndpoint.BaseUrl, client.Endpoint.BaseUrl);          // what the user sees stays the sentinel
        Assert.Equal(new Uri("http://127.0.0.1:5000/v1"), client.Endpoint.WireUrl);
    }

    // ── The catalog ─────────────────────────────────────────────────────────

    [Fact]
    public void TheCatalog_IsTheUsersFour_PinnedToACommit()
    {
        Assert.Equal(["gemma-4-e4b-qat", "gemma-4-e2b", "gemma-4-e4b-uncensored", "gemma-4-e2b-uncensored"], LocalModelCatalog.Models.Select(m => m.Id));
        foreach (var model in LocalModelCatalog.Models)
        {
            Assert.Matches("^[a-z0-9-]+$", model.Id);
            Assert.Matches("^[0-9a-f]{40}$", model.Revision);
            foreach (var file in new[] { model.Model, model.Mmproj })
            {
                Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
                Assert.EndsWith(".gguf", file.Name);
                Assert.True(file.Bytes > 900_000_000, file.Name);
            }

            Assert.Contains("mmproj", model.Mmproj.Name, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new LocalSampling(1.0, 0.95, 64), model.Sampling);
            Assert.Same(model, LocalModelCatalog.Find(model.Id.ToUpperInvariant()));
        }

        Assert.Null(LocalModelCatalog.Find("gemma-3"));
        Assert.Null(LocalModelCatalog.Find(null));
    }

    [Fact]
    public void TheCatalogsUrls_AndSizes_ArePinned()
    {
        var e4b = LocalModelCatalog.Find("gemma-4-e4b-qat")!;
        Assert.Equal("https://huggingface.co/unsloth/gemma-4-E4B-it-qat-GGUF/resolve/8c5a9e4fd5482e2be20fe0bf013b4c262a8f4265/gemma-4-E4B-it-qat-UD-Q4_K_XL.gguf", LocalModelCatalog.Url(e4b, e4b.Model).AbsoluteUri);
        Assert.Equal("https://huggingface.co/unsloth/gemma-4-E4B-it-qat-GGUF/resolve/8c5a9e4fd5482e2be20fe0bf013b4c262a8f4265/mmproj-F16.gguf", LocalModelCatalog.Url(e4b, e4b.Mmproj).AbsoluteUri);
        Assert.Equal(4_215_695_776L + 990_372_672L, LocalModelCatalog.TotalBytes(e4b));
        Assert.Equal("5.2 GB", ModelStore.SizeLabel(LocalModelCatalog.TotalBytes(e4b)));

        var uncensored = LocalModelCatalog.Find("gemma-4-e4b-uncensored")!;
        Assert.Equal("https://huggingface.co/HauhauCS/Gemma-4-E4B-Uncensored-HauhauCS-Aggressive/resolve/45b6a334b4bcd1d7f37179df58b3b1d66a184e5d/Gemma-4-E4B-Uncensored-HauhauCS-Aggressive-Q4_K_P.gguf", LocalModelCatalog.Url(uncensored, uncensored.Model).AbsoluteUri);
        Assert.Equal("Q4_K_P", uncensored.Quant);
        Assert.Equal("6.4 GB", ModelStore.SizeLabel(LocalModelCatalog.TotalBytes(uncensored)));
    }

    [Fact]
    public void EachModel_HasItsOwnFolder_SoTheTwoMmprojF16sNeverCollide()
    {
        string dir = Path.Combine("C:", "home", "models", "llm");
        var paths = LocalModelCatalog.Models.SelectMany(m => new[] { LocalModelCatalog.WeightsSpec(dir, m).Path, LocalModelCatalog.MmprojSpec(dir, m).Path }).ToList();
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var e2b = LocalModelCatalog.Find("gemma-4-e2b")!;
        var weights = LocalModelCatalog.WeightsSpec(dir, e2b);
        Assert.Equal(Path.Combine(dir, "gemma-4-e2b", "gemma-4-E2B-it-UD-Q4_K_XL.gguf"), weights.Path);
        Assert.Equal(ModelFormat.Gguf, weights.Format);
        Assert.True(weights.Resumable);
        Assert.Equal(e2b.Model.Sha256, weights.Sha256);
        Assert.Equal(e2b.Model.Bytes, weights.ApproxBytes);
        Assert.Equal("Gemma 4 E2B vision", LocalModelCatalog.MmprojSpec(dir, e2b).Display);
    }

    // ── The llama.cpp pins ──────────────────────────────────────────────────

    [Fact]
    public void TheRelease_IsPinned_PerBackend()
    {
        Assert.Matches("^b[0-9]+$", LlamaRelease.Tag);
        Assert.Equal(["llama-b11258-bin-win-cuda-13.4-x64.zip", "cudart-llama-bin-win-cuda-13.4-x64.zip"], LlamaRelease.Assets(LlamaBackend.Cuda).Select(a => a.Name));
        Assert.Equal(["llama-b11258-bin-win-vulkan-x64.zip"], LlamaRelease.Assets(LlamaBackend.Vulkan).Select(a => a.Name));
        Assert.Equal(["llama-b11258-bin-win-cpu-x64.zip"], LlamaRelease.Assets(LlamaBackend.Cpu).Select(a => a.Name));
        foreach (var backend in Enum.GetValues<LlamaBackend>())
        {
            Assert.All(LlamaRelease.Assets(backend), a => Assert.Matches("^[0-9a-f]{64}$", a.Sha256));
            Assert.Contains(LlamaRelease.ServerExecutable, LlamaRelease.RequiredFiles(backend));
            Assert.Contains("mtmd.dll", LlamaRelease.RequiredFiles(backend));
        }

        Assert.Contains("cudart64_13.dll", LlamaRelease.RequiredFiles(LlamaBackend.Cuda));
        Assert.Contains("ggml-vulkan.dll", LlamaRelease.RequiredFiles(LlamaBackend.Vulkan));
        Assert.Equal("https://github.com/ggml-org/llama.cpp/releases/download/b11258/llama-b11258-bin-win-vulkan-x64.zip", LlamaRelease.Url(LlamaRelease.Assets(LlamaBackend.Vulkan)[0]).AbsoluteUri);
        Assert.Equal(Path.Combine("C:", "home", "llama", "b11258-cuda"), LlamaRelease.Folder(Path.Combine("C:", "home", "llama"), LlamaBackend.Cuda));
        Assert.Equal("577 MB", ModelStore.SizeLabel(LlamaRelease.Bytes(LlamaBackend.Cuda)));
        Assert.Equal("llama.cpp b11258 (vulkan)", LocalLlmText.RuntimeDisplay(LlamaBackend.Vulkan));

        var spec = LlamaRelease.Spec(Path.Combine("C:", "llama"), LlamaBackend.Cuda);
        Assert.Equal(2, spec.Parts.Count);
        Assert.Equal(LlamaRelease.RequiredFiles(LlamaBackend.Cuda), spec.RequiredFiles);
    }

    // ── The backend ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("32.0.16.1088", 610, 88)]
    [InlineData("32.0.15.6094", 560, 94)]
    [InlineData("32.0.15.8000", 580, 0)]
    [InlineData("31.0.15.3623", 536, 23)]
    public void DriverVersion_ReadsNvcudasFileVersion(string fileVersion, int major, int minor)
    {
        Assert.Equal((major, minor), LlamaBackendDetect.DriverVersion(fileVersion));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("32.0.16")]
    [InlineData("a.b.c.d")]
    public void DriverVersion_IsNull_ForAnythingElse(string? fileVersion)
    {
        Assert.Null(LlamaBackendDetect.DriverVersion(fileVersion));
    }

    private static BackendChoice Choose(string setting, bool nvidia, string? version, bool vulkan) =>
        LlamaBackendDetect.Choose(
            setting,
            path => path.EndsWith("nvcuda.dll", StringComparison.Ordinal) ? nvidia : path.EndsWith("vulkan-1.dll", StringComparison.Ordinal) && vulkan,
            _ => version,
            @"C:\Windows\System32");

    [Fact]
    public void Auto_PicksCuda_ForANewEnoughNvidiaDriver()
    {
        Assert.Equal(new BackendChoice(LlamaBackend.Cuda, "NVIDIA driver 610.88"), Choose("auto", true, "32.0.16.1088", true));
    }

    [Fact]
    public void Auto_FallsToVulkan_ForAnOldDriver_OrNoNvidia()
    {
        Assert.Equal(new BackendChoice(LlamaBackend.Vulkan, "NVIDIA driver 560.94 is older than 580"), Choose("auto", true, "32.0.15.6094", true));
        Assert.Equal(new BackendChoice(LlamaBackend.Vulkan, "no NVIDIA driver; Vulkan present"), Choose("auto", false, null, true));
        Assert.Equal(new BackendChoice(LlamaBackend.Vulkan, "NVIDIA driver version unreadable"), Choose("auto", true, null, true));
    }

    [Fact]
    public void Auto_FallsToTheCpu_WithNoGpuDriver()
    {
        Assert.Equal(new BackendChoice(LlamaBackend.Cpu, "no GPU driver found"), Choose("auto", false, null, false));
        Assert.Equal(LlamaBackend.Cpu, Choose("auto", true, "32.0.15.6094", false).Backend);
    }

    [Theory]
    [InlineData("cuda", LlamaBackend.Cuda)]
    [InlineData(" Vulkan ", LlamaBackend.Vulkan)]
    [InlineData("CPU", LlamaBackend.Cpu)]
    public void AForcedBackend_Wins_WhateverTheMachine(string setting, LlamaBackend backend)
    {
        Assert.Equal(new BackendChoice(backend, "forced in settings"), Choose(setting, false, null, false));
        Assert.True(LocalBackends.IsValid(setting));
    }

    [Fact]
    public void AnUnknownBackend_ReadsAsAuto()
    {
        Assert.Null(LocalBackends.Forced("rocm"));
        Assert.False(LocalBackends.IsValid("rocm"));
        Assert.Equal(LlamaBackend.Cuda, Choose("rocm", true, "32.0.16.1088", true).Backend);
        Assert.Equal(["auto", "cuda", "vulkan", "cpu"], LocalBackends.Names);
    }

    // ── The command line ────────────────────────────────────────────────────

    private static LlamaLaunch Launch(string? mmproj = @"C:\m\mmproj-F16.gguf", int context = 32768, string layers = "auto") =>
        new(@"C:\llama\b11258-cuda\llama-server.exe", LlamaBackend.Cuda, @"C:\m\model.gguf", mmproj, "gemma-4-e2b", context, layers, new LocalSampling(1.0, 0.95, 64));

    [Fact]
    public void Arguments_ArePinned_WithVision()
    {
        Assert.Equal(
            ["-m", @"C:\m\model.gguf", "--mmproj", @"C:\m\mmproj-F16.gguf", "--alias", "gemma-4-e2b", "--host", "127.0.0.1", "--port", "51234", "--api-key", "secret",
             "--jinja", "-c", "32768", "-ngl", "auto", "--parallel", "1", "--no-webui", "--log-colors", "off", "--temp", "1", "--top-p", "0.95", "--top-k", "64"],
            LlamaArguments.Build(Launch(), 51234, "secret"));
    }

    [Fact]
    public void Arguments_WithoutVision_LeaveTheProjectorOut()
    {
        var args = LlamaArguments.Build(Launch(mmproj: null, context: 0, layers: "all"), 1, "k");
        Assert.DoesNotContain("--mmproj", args);
        Assert.Equal("0", args[args.ToList().IndexOf("-c") + 1]);
        Assert.Equal("all", args[args.ToList().IndexOf("-ngl") + 1]);
        Assert.False(Launch(mmproj: null).Vision);
        Assert.True(Launch().Vision);
        Assert.Equal(@"C:\llama\b11258-cuda", Launch().WorkingDirectory);
    }

    [Fact]
    public void ALaunch_EqualsTheSameLaunch_SoTheServerIsReused()
    {
        Assert.Equal(Launch(), Launch());
        Assert.NotEqual(Launch(), Launch(context: 8192));
        Assert.NotEqual(Launch(), Launch(mmproj: null));
    }

    // ── The settings' ranges ────────────────────────────────────────────────

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData(" ALL ", "all")]
    [InlineData("0", "0")]
    [InlineData("040", "40")]
    [InlineData("999", "999")]
    [InlineData("1000", null)]
    [InlineData("-1", null)]
    [InlineData("half", null)]
    [InlineData("", null)]
    public void GpuLayers_Normalise(string setting, string? expected)
    {
        Assert.Equal(expected, LocalGpuLayers.Normalize(setting));
        Assert.Equal(expected ?? "auto", LocalGpuLayers.Effective(setting));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(512, true)]
    [InlineData(262_144, true)]
    [InlineData(511, false)]
    [InlineData(262_145, false)]
    [InlineData(-1, false)]
    public void ContextSize_HasItsRange(int value, bool valid)
    {
        Assert.Equal(valid, LocalContextSize.IsValid(value));
        Assert.Equal(valid ? value : LocalContextSize.Default, LocalContextSize.Effective(value));
    }

    // ── The wording ─────────────────────────────────────────────────────────

    [Fact]
    public void Wording_IsPinned()
    {
        var e2b = LocalModelCatalog.Find("gemma-4-e2b")!;
        Assert.Equal("installed · 4.2 GB", LocalLlmText.RowDetail(LocalModelState.Installed, 4_170_150_816));
        Assert.Equal("download 4.2 GB", LocalLlmText.RowDetail(LocalModelState.Absent, 4_170_150_816));
        Assert.Equal("paused 42% · 4.2 GB", LocalLlmText.RowDetail(new LocalModelState(LocalModelStateKind.Partial, 42), 4_170_150_816));
        Assert.Equal("local llama.cpp b11258 cuda on 127.0.0.1:53121", LocalLlmText.Source(new LocalServerInfo(new Uri("http://127.0.0.1:53121/v1"), 53121, "k", LlamaBackend.Cuda, "m", true)));
        Assert.Equal("Gemma 4 E2B is not installed. Install it now (download 4.2 GB + llama.cpp runtime 577 MB)?", LocalLlmText.InstallQuestion(e2b, LlamaRelease.Bytes(LlamaBackend.Cuda)));
        Assert.Equal("download 4.2 GB", LocalLlmText.InstallCost(e2b, 0));
        Assert.Equal("2 of 4 installed (9.4 GB)", LocalLlmText.ModelsRowValue(2, 4, 9_400_000_000));
        Assert.Equal("none of 4 installed", LocalLlmText.ModelsRowValue(0, 4, 0));
        Assert.Equal("auto (cuda: NVIDIA driver 610.88)", LocalLlmText.BackendRowValue("auto", new BackendChoice(LlamaBackend.Cuda, "NVIDIA driver 610.88")));
        Assert.Equal("llama-server exited with code 1 before it was ready: error: failed to load model", LocalLlmText.ExitedEarly(1, "error: failed to load model"));
        Assert.Equal("llama-server exited with code 3 before it was ready", LocalLlmText.ExitedEarly(3, " "));
        Assert.Equal("llama-server was not ready after 300 s", LocalLlmText.StartTimeout(TimeSpan.FromMinutes(5)));
        Assert.Equal("local (embedded llama.cpp)", LocalLlmText.UrlDisplay);
    }
}
