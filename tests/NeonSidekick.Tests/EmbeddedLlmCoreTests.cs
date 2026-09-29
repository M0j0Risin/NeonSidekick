using NeonSidekick.Llm;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Speech;

namespace NeonSidekick.Tests;

/// <summary>
/// The embedded model's pure parts (2026-09-29): the sentinel endpoint, the catalog and the llama.cpp pins, the backend
/// choice, the command line, the settings' ranges and the wording.
/// </summary>
public class EmbeddedLlmCoreTests
{
    // ── The sentinel ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("embedded", true)]
    [InlineData(" EMBEDDED ", true)]
    [InlineData("http://embedded-llm.invalid/v1", true)]
    [InlineData("http://EMBEDDED-LLM.INVALID/v1/", true)]
    [InlineData("http://127.0.0.1:8080/v1", false)]
    [InlineData("local", false)]                        // the name of 2026-09-29 before the rename, no synonym kept
    [InlineData("http://local-llm.invalid/v1", false)]
    [InlineData("localhost", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEmbedded_KnowsTheAliasAndTheSentinel(string? url, bool embedded)
    {
        Assert.Equal(embedded, EmbeddedEndpoint.IsEmbedded(url));
    }

    [Fact]
    public void TheSentinel_IsPinned_AndTheAliasNormalisesToIt()
    {
        Assert.Equal("http://embedded-llm.invalid/v1", EmbeddedEndpoint.BaseUrl.AbsoluteUri);
        Assert.Equal(EmbeddedEndpoint.BaseUrl, LlmEndpoint.NormalizeBaseUrl("embedded"));
        Assert.Equal(EmbeddedEndpoint.BaseUrl, LlmEndpoint.NormalizeBaseUrl(" Embedded "));
        Assert.Equal("Embedded", EmbeddedEndpoint.ServerName);
        Assert.True(EmbeddedEndpoint.ServerName.Length <= App.SettingsMenu.ServerNameWidth);
        Assert.False(EmbeddedEndpoint.IsEmbedded((Uri?)null));
    }

    [Fact]
    public void AnEndpoint_PostsToItsLiveUrl_WhenItHasOne()
    {
        var plain = new LlmEndpoint(new Uri("http://127.0.0.1:1234/v1"), "m", "k", "configured");
        Assert.Equal(plain.BaseUrl, plain.WireUrl);

        var embedded = new LlmEndpoint(EmbeddedEndpoint.BaseUrl, "gemma-4-e2b", "k", "embedded") { LiveUrl = new Uri("http://127.0.0.1:5000/v1") };
        Assert.Equal(new Uri("http://127.0.0.1:5000/v1"), embedded.WireUrl);
        Assert.Equal(embedded.LiveUrl, (embedded with { ModelId = "x" }).LiveUrl);   // a bot's borrowed copy keeps it

        using var client = new OpenAICompatibleChatClient(embedded, TimeSpan.FromSeconds(5));
        Assert.Equal(EmbeddedEndpoint.BaseUrl, client.Endpoint.BaseUrl);          // what the user sees stays the sentinel
        Assert.Equal(new Uri("http://127.0.0.1:5000/v1"), client.Endpoint.WireUrl);
    }

    // ── The catalog ─────────────────────────────────────────────────────────

    [Fact]
    public void TheCatalog_IsTheUsersEleven_InOrder_PinnedToACommit()
    {
        // Alphabetical by name, so each model's builds sit together (2026-09-29, the user's call); one name's builds by size.
        Assert.Equal(
            ["gemma-4-12b", "gemma-4-12b-q5", "gemma-4-12b-q6", "gemma-4-12b-bf16", "gemma-4-12b-qat", "gemma-4-12b-qat-uncensored", "gemma-4-e2b", "gemma-4-e2b-uncensored", "gemma-4-e4b", "gemma-4-e4b-qat", "gemma-4-e4b-uncensored"],
            EmbeddedModelCatalog.Models.Select(m => m.Id));
        foreach (var name in EmbeddedModelCatalog.Models.GroupBy(m => m.Display))
        {
            Assert.Equal(name.Select(m => m.Model.Bytes).Order(), name.Select(m => m.Model.Bytes));
        }

        Assert.Equal(EmbeddedModelCatalog.Models.Select(m => m.Display).Order(StringComparer.Ordinal), EmbeddedModelCatalog.Models.Select(m => m.Display));
        foreach (var model in EmbeddedModelCatalog.Models)
        {
            Assert.Matches("^[a-z0-9.-]+$", model.Id);
            Assert.Matches("^[0-9a-f]{40}$", model.Revision);
            Assert.Matches("^[0-9a-f]{64}$", model.Model.Sha256);
            Assert.EndsWith(".gguf", model.Model.Name);
            Assert.True(model.Model.Bytes > 1_000_000_000, model.Model.Name);
            Assert.Matches("^[0-9a-f]{64}$", model.Mmproj.Sha256);   // every model reads images (Qwen3.8, text only, left the same day)
            Assert.EndsWith(".gguf", model.Mmproj.Name);
            Assert.Contains("mmproj", model.Mmproj.Name, StringComparison.OrdinalIgnoreCase);
            Assert.True(model.Mmproj.Bytes > 100_000_000, model.Mmproj.Name);
            Assert.Same(model, EmbeddedModelCatalog.Find(model.Id.ToUpperInvariant()));
        }

        Assert.All(EmbeddedModelCatalog.Models.Where(m => m.Id.StartsWith("gemma-4-", StringComparison.Ordinal) && m.Id != "gemma-4-12b-qat-uncensored"), m => Assert.Equal(new EmbeddedSampling(1.0, 0.95, 64), m.Sampling));
        Assert.Equal(new EmbeddedSampling(0.6, 0.9, 64), EmbeddedModelCatalog.Find("gemma-4-12b-qat-uncensored")!.Sampling);   // HauhauCS Balanced's card
        Assert.Null(EmbeddedModelCatalog.Find("qwen3.8-9b"));   // left the catalog (2026-09-29, the user's call)

        Assert.Null(EmbeddedModelCatalog.Find("gemma-3"));
        Assert.Null(EmbeddedModelCatalog.Find(null));
    }

    [Fact]
    public void TheCatalogsUrls_AndSizes_ArePinned()
    {
        var e4b = EmbeddedModelCatalog.Find("gemma-4-e4b-qat")!;
        Assert.Equal("https://huggingface.co/unsloth/gemma-4-E4B-it-qat-GGUF/resolve/8c5a9e4fd5482e2be20fe0bf013b4c262a8f4265/gemma-4-E4B-it-qat-UD-Q4_K_XL.gguf", EmbeddedModelCatalog.Url(e4b, e4b.Model).AbsoluteUri);
        Assert.Equal("https://huggingface.co/unsloth/gemma-4-E4B-it-qat-GGUF/resolve/8c5a9e4fd5482e2be20fe0bf013b4c262a8f4265/mmproj-F16.gguf", EmbeddedModelCatalog.Url(e4b, e4b.Mmproj).AbsoluteUri);
        Assert.Equal(4_215_695_776L + 990_372_672L, EmbeddedModelCatalog.TotalBytes(e4b));
        Assert.Equal("5.2 GB", ModelStore.SizeLabel(EmbeddedModelCatalog.TotalBytes(e4b)));

        var uncensored = EmbeddedModelCatalog.Find("gemma-4-e4b-uncensored")!;
        Assert.Equal("https://huggingface.co/HauhauCS/Gemma-4-E4B-Uncensored-HauhauCS-Aggressive/resolve/45b6a334b4bcd1d7f37179df58b3b1d66a184e5d/Gemma-4-E4B-Uncensored-HauhauCS-Aggressive-Q4_K_P.gguf", EmbeddedModelCatalog.Url(uncensored, uncensored.Model).AbsoluteUri);
        Assert.Equal("Q4_K_P", uncensored.Quant);
        Assert.Equal("6.4 GB", ModelStore.SizeLabel(EmbeddedModelCatalog.TotalBytes(uncensored)));

        // The ones of later on 2026-09-29, the 12B's three more builds last.
        Assert.Equal(["6.1 GB", "7.5 GB", "6.9 GB", "7.6 GB", "8.8 GB", "10.9 GB", "24 GB"],
            new[] { "gemma-4-e4b", "gemma-4-12b", "gemma-4-12b-qat", "gemma-4-12b-qat-uncensored", "gemma-4-12b-q5", "gemma-4-12b-q6", "gemma-4-12b-bf16" }.Select(id => ModelStore.SizeLabel(EmbeddedModelCatalog.TotalBytes(EmbeddedModelCatalog.Find(id)!))));
        var balanced = EmbeddedModelCatalog.Find("gemma-4-12b-qat-uncensored")!;
        Assert.Equal("https://huggingface.co/HauhauCS/Gemma4-12B-QAT-Uncensored-HauhauCS-Balanced/resolve/ae8045ac2bd216293ca49a3065da2c942dde4b68/mmproj-Gemma4-12B-QAT-Uncensored-HauhauCS-Balanced-BF16.gguf", EmbeddedModelCatalog.Url(balanced, balanced.Mmproj).AbsoluteUri);
        var bf16 = EmbeddedModelCatalog.Find("gemma-4-12b-bf16")!;
        Assert.Equal("https://huggingface.co/unsloth/gemma-4-12b-it-GGUF/resolve/fc034cfff751157913579611efad8462ac1be606/gemma-4-12b-it-BF16.gguf", EmbeddedModelCatalog.Url(bf16, bf16.Model).AbsoluteUri);
        Assert.Equal(("BF16", "UD-Q5_K_XL", "UD-Q6_K_XL"), (bf16.Quant, EmbeddedModelCatalog.Find("gemma-4-12b-q5")!.Quant, EmbeddedModelCatalog.Find("gemma-4-12b-q6")!.Quant));
        Assert.All(EmbeddedModelCatalog.Models.Where(m => m.Repository == "unsloth/gemma-4-12b-it-GGUF"), m => Assert.Equal(EmbeddedModelCatalog.Find("gemma-4-12b")!.Mmproj, m.Mmproj));   // one repository, one projector
    }

    [Fact]
    public void EachModel_HasItsOwnFolder_SoTheTwoMmprojF16sNeverCollide()
    {
        string dir = Path.Combine("C:", "home", "models", "llm");
        var paths = EmbeddedModelCatalog.Models.SelectMany(m => new[] { EmbeddedModelCatalog.WeightsSpec(dir, m).Path, EmbeddedModelCatalog.MmprojSpec(dir, m).Path }).ToList();
        Assert.Equal(22, paths.Count);   // eleven weights, eleven projectors (the 12B's four builds share one projector's name and bytes, each in its own folder)
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var e2b = EmbeddedModelCatalog.Find("gemma-4-e2b")!;
        var weights = EmbeddedModelCatalog.WeightsSpec(dir, e2b);
        Assert.Equal(Path.Combine(dir, "gemma-4-e2b", "gemma-4-E2B-it-UD-Q4_K_XL.gguf"), weights.Path);
        Assert.Equal(ModelFormat.Gguf, weights.Format);
        Assert.True(weights.Resumable);
        Assert.Equal(e2b.Model.Sha256, weights.Sha256);
        Assert.Equal(e2b.Model.Bytes, weights.ApproxBytes);
        Assert.Equal("Gemma 4 E2B vision", EmbeddedModelCatalog.MmprojSpec(dir, e2b)!.Display);
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
        Assert.Equal("llama.cpp b11258 (vulkan)", EmbeddedLlmText.RuntimeDisplay(LlamaBackend.Vulkan));

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
        Assert.True(EmbeddedBackends.IsValid(setting));
    }

    [Fact]
    public void AnUnknownBackend_ReadsAsAuto()
    {
        Assert.Null(EmbeddedBackends.Forced("rocm"));
        Assert.False(EmbeddedBackends.IsValid("rocm"));
        Assert.Equal(LlamaBackend.Cuda, Choose("rocm", true, "32.0.16.1088", true).Backend);
        Assert.Equal(["auto", "cuda", "vulkan", "cpu"], EmbeddedBackends.Names);
    }

    // ── The command line ────────────────────────────────────────────────────

    private static LlamaLaunch Launch(string? mmproj = @"C:\m\mmproj-F16.gguf", int context = 32768, string layers = "auto") =>
        new(@"C:\llama\b11258-cuda\llama-server.exe", LlamaBackend.Cuda, @"C:\m\model.gguf", mmproj, "gemma-4-e2b", context, layers, new EmbeddedSampling(1.0, 0.95, 64));

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
        Assert.Equal(expected, EmbeddedGpuLayers.Normalize(setting));
        Assert.Equal(expected ?? "auto", EmbeddedGpuLayers.Effective(setting));
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
        Assert.Equal(valid, EmbeddedContextSize.IsValid(value));
        Assert.Equal(valid ? value : EmbeddedContextSize.Default, EmbeddedContextSize.Effective(value));
    }

    // ── The wording ─────────────────────────────────────────────────────────

    [Fact]
    public void Wording_IsPinned()
    {
        var e2b = EmbeddedModelCatalog.Find("gemma-4-e2b")!;
        // The · and the size in one column down the list (2026-09-29, the user's ask); a paused share last.
        Assert.Equal("installed · 4.2 GB", EmbeddedLlmText.RowDetail(EmbeddedModelState.Installed, 4_170_150_816));
        Assert.Equal("download  · 4.2 GB", EmbeddedLlmText.RowDetail(EmbeddedModelState.Absent, 4_170_150_816));
        Assert.Equal("paused    · 4.2 GB · 42%", EmbeddedLlmText.RowDetail(new EmbeddedModelState(EmbeddedModelStateKind.Partial, 42), 4_170_150_816));
        Assert.Equal(9, EmbeddedLlmText.StateWidth);   // "installed", the longest word
        Assert.Equal("embedded llama.cpp b11258 cuda on 127.0.0.1:53121", EmbeddedLlmText.Source(new EmbeddedServerInfo(new Uri("http://127.0.0.1:53121/v1"), 53121, "k", LlamaBackend.Cuda, "m", true)));
        Assert.Equal("Gemma 4 E2B is not installed. Install it now (download 4.2 GB + llama.cpp runtime 577 MB)?", EmbeddedLlmText.InstallQuestion(e2b, LlamaRelease.Bytes(LlamaBackend.Cuda)));
        Assert.Equal("download 4.2 GB", EmbeddedLlmText.InstallCost(e2b, 0));
        Assert.Equal("2 of 4 installed (9.4 GB)", EmbeddedLlmText.ModelsRowValue(2, 4, 9_400_000_000));
        Assert.Equal("none of 4 installed", EmbeddedLlmText.ModelsRowValue(0, 4, 0));
        Assert.Equal("auto (cuda: NVIDIA driver 610.88)", EmbeddedLlmText.BackendRowValue("auto", new BackendChoice(LlamaBackend.Cuda, "NVIDIA driver 610.88")));
        Assert.Equal("llama-server exited with code 1 before it was ready: error: failed to load model", EmbeddedLlmText.ExitedEarly(1, "error: failed to load model"));
        Assert.Equal("llama-server exited with code 3 before it was ready", EmbeddedLlmText.ExitedEarly(3, " "));
        Assert.Equal("llama-server was not ready after 300 s", EmbeddedLlmText.StartTimeout(TimeSpan.FromMinutes(5)));
        Assert.Equal("embedded (llama.cpp)", EmbeddedLlmText.UrlDisplay);
    }
}
