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
    [InlineData("http://embedded.localhost/v1", true)]
    [InlineData("http://EMBEDDED.LOCALHOST/v1/", true)]
    [InlineData("http://127.0.0.1:8080/v1", false)]
    [InlineData("local", false)]                        // the name of 2026-09-29 before the rename, no synonym kept
    [InlineData("http://local-llm.invalid/v1", false)]
    [InlineData("http://embedded-llm.invalid/v1", false)]  // the sentinel until later on 2026-09-29, no synonym kept either
    [InlineData("http://localhost/v1", false)]
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
        Assert.Equal("http://embedded.localhost/v1", EmbeddedEndpoint.BaseUrl.AbsoluteUri);
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
    public void TheCatalog_IsTheUsersFortyOne_InOrder_PinnedToACommit()
    {
        // Alphabetical by name, so each model's builds sit together (2026-09-29, the user's call); one name's builds by size.
        // The eleven of the morning, then the 26B A4B and 31B builds and the Qwens of later that day (the user's picks), then
        // esatapedico's NVFP4 tiers, in their size order (which is their tiers' order); Unsloth's Muse Glimmer 30B pair on 2026-09-30.
        Assert.Equal(
            ["gemma-4-12b", "gemma-4-12b-q5", "gemma-4-12b-q6", "gemma-4-12b-bf16", "gemma-4-12b-qat", "gemma-4-12b-qat-uncensored",
             "gemma-4-26b-a4b", "gemma-4-26b-a4b-q5", "gemma-4-26b-a4b-q6", "gemma-4-26b-a4b-qat", "gemma-4-26b-a4b-qat-uncensored",
             "gemma-4-26b-a4b-uncensored", "gemma-4-26b-a4b-uncensored-q5", "gemma-4-26b-a4b-uncensored-q6",
             "gemma-4-31b", "gemma-4-31b-q5", "gemma-4-31b-qat", "gemma-4-31b-qat-uncensored",
             "gemma-4-e2b", "gemma-4-e2b-uncensored", "gemma-4-e4b", "gemma-4-e4b-qat", "gemma-4-e4b-uncensored",
             "muse-glimmer-30b", "muse-glimmer-30b-q5",
             "qwen3.6-35b-a3b", "qwen3.6-35b-a3b-q5", "qwen3.6-35b-a3b-uncensored",
             "qwen3.8-27b", "qwen3.8-27b-q5", "qwen3.8-27b-q6",
             "qwen3.8-27b-nvfp4-very-low", "qwen3.8-27b-nvfp4-compact-low", "qwen3.8-27b-nvfp4-low", "qwen3.8-27b-nvfp4-medium", "qwen3.8-27b-nvfp4-mid-high", "qwen3.8-27b-nvfp4-high", "qwen3.8-27b-nvfp4-very-high", "qwen3.8-27b-nvfp4-highest",
             "qwen3.8-27b-uncensored", "qwen3.8-27b-uncensored-q5"],
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

        string[] qatBalanced = ["gemma-4-12b-qat-uncensored", "gemma-4-26b-a4b-qat-uncensored", "gemma-4-31b-qat-uncensored"];
        Assert.All(EmbeddedModelCatalog.Models.Where(m => m.Id.StartsWith("gemma-4-", StringComparison.Ordinal) && !qatBalanced.Contains(m.Id)), m => Assert.Equal(new EmbeddedSampling(1.0, 0.95, 64), m.Sampling));
        Assert.All(qatBalanced, id => Assert.Equal(new EmbeddedSampling(0.6, 0.9, 64), EmbeddedModelCatalog.Find(id)!.Sampling));   // HauhauCS QAT Balanced's cards
        Assert.All(EmbeddedModelCatalog.Models.Where(m => m.Id.StartsWith("qwen", StringComparison.Ordinal)), m => Assert.Equal(new EmbeddedSampling(1.0, 0.95, 20), m.Sampling));   // Qwen's "thinking, general"
        Assert.All(EmbeddedModelCatalog.Models.Where(m => m.Id.StartsWith("muse-", StringComparison.Ordinal)), m => Assert.Equal(new EmbeddedSampling(1.0, 0.95, 64), m.Sampling));   // Meta's card
        Assert.Null(EmbeddedModelCatalog.Find("qwen3.8-9b"));   // left the catalog (2026-09-29, the user's call); the 27B came back later that day

        // MTP (2026-09-29, "using the drafters where available"): a drafter per Gemma 4 repository that ships one, the
        // Qwen3.8 builds' head in their weights, nothing for the Aggressive E2B/E4B, the 26B A4B Balanced or Qwen3.6.
        Assert.Equal(
            ["gemma-4-26b-a4b-uncensored", "gemma-4-26b-a4b-uncensored-q5", "gemma-4-26b-a4b-uncensored-q6", "gemma-4-e2b-uncensored", "gemma-4-e4b-uncensored", "qwen3.6-35b-a3b", "qwen3.6-35b-a3b-q5", "qwen3.6-35b-a3b-uncensored"],
            EmbeddedModelCatalog.Models.Where(m => !m.HasMtp).Select(m => m.Id));
        Assert.Equal(["qwen3.8-27b", "qwen3.8-27b-q5", "qwen3.8-27b-q6", "qwen3.8-27b-nvfp4-very-low", "qwen3.8-27b-nvfp4-compact-low", "qwen3.8-27b-nvfp4-low", "qwen3.8-27b-nvfp4-medium", "qwen3.8-27b-nvfp4-mid-high", "qwen3.8-27b-nvfp4-high", "qwen3.8-27b-nvfp4-very-high", "qwen3.8-27b-nvfp4-highest", "qwen3.8-27b-uncensored", "qwen3.8-27b-uncensored-q5"], EmbeddedModelCatalog.Models.Where(m => m.MtpHead).Select(m => m.Id));
        Assert.All(EmbeddedModelCatalog.Models.Where(m => m.MtpHead), m => Assert.Null(m.Drafter));   // one or the other
        foreach (var model in EmbeddedModelCatalog.Models.Where(m => m.Drafter is not null && m.Draft == DraftKind.Mtp))
        {
            Assert.StartsWith("mtp-gemma-4-", model.Drafter!.Name, StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{64}$", model.Drafter.Sha256);
            Assert.InRange(model.Drafter.Bytes, 50_000_000, 600_000_000);
        }

        // DFlash (2026-09-30): Muse Glimmer's block-diffusion drafter, a file of its own, never a head.
        Assert.Equal(["muse-glimmer-30b", "muse-glimmer-30b-q5"], EmbeddedModelCatalog.Models.Where(m => m.Draft == DraftKind.DFlash).Select(m => m.Id));
        foreach (var model in EmbeddedModelCatalog.Models.Where(m => m.Draft == DraftKind.DFlash))
        {
            Assert.False(model.MtpHead);
            Assert.StartsWith("dflash-", model.Drafter!.Name, StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{64}$", model.Drafter.Sha256);
            Assert.InRange(model.Drafter.Bytes, 1_000_000_000, 2_000_000_000);
        }

        Assert.Null(EmbeddedModelCatalog.Find("gemma-3"));
        Assert.Null(EmbeddedModelCatalog.Find(null));
    }

    [Fact]
    public void TheCatalogsUrls_AndSizes_ArePinned()
    {
        var e4b = EmbeddedModelCatalog.Find("gemma-4-e4b-qat")!;
        Assert.Equal("https://huggingface.co/unsloth/gemma-4-E4B-it-qat-GGUF/resolve/8c5a9e4fd5482e2be20fe0bf013b4c262a8f4265/gemma-4-E4B-it-qat-UD-Q4_K_XL.gguf", EmbeddedModelCatalog.Url(e4b, e4b.Model).AbsoluteUri);
        Assert.Equal("https://huggingface.co/unsloth/gemma-4-E4B-it-qat-GGUF/resolve/8c5a9e4fd5482e2be20fe0bf013b4c262a8f4265/mmproj-F16.gguf", EmbeddedModelCatalog.Url(e4b, e4b.Mmproj).AbsoluteUri);
        Assert.Equal("https://huggingface.co/unsloth/gemma-4-E4B-it-qat-GGUF/resolve/8c5a9e4fd5482e2be20fe0bf013b4c262a8f4265/mtp-gemma-4-E4B-it.gguf", EmbeddedModelCatalog.Url(e4b, e4b.Drafter!).AbsoluteUri);
        Assert.Equal(4_215_695_776L + 990_372_672L + 59_678_016L, EmbeddedModelCatalog.TotalBytes(e4b));   // the drafter counts (2026-09-29)
        Assert.Equal("5.3 GB", ModelStore.SizeLabel(EmbeddedModelCatalog.TotalBytes(e4b)));

        var uncensored = EmbeddedModelCatalog.Find("gemma-4-e4b-uncensored")!;
        Assert.Equal("https://huggingface.co/HauhauCS/Gemma-4-E4B-Uncensored-HauhauCS-Aggressive/resolve/45b6a334b4bcd1d7f37179df58b3b1d66a184e5d/Gemma-4-E4B-Uncensored-HauhauCS-Aggressive-Q4_K_P.gguf", EmbeddedModelCatalog.Url(uncensored, uncensored.Model).AbsoluteUri);
        Assert.Equal("Q4_K_P", uncensored.Quant);
        Assert.Equal("6.4 GB", ModelStore.SizeLabel(EmbeddedModelCatalog.TotalBytes(uncensored)));

        // The ones of later on 2026-09-29, the 12B's three more builds last, each with its drafter.
        Assert.Equal(["6.2 GB", "8 GB", "7.1 GB", "7.8 GB", "9.2 GB", "11.3 GB", "24.5 GB"],
            new[] { "gemma-4-e4b", "gemma-4-12b", "gemma-4-12b-qat", "gemma-4-12b-qat-uncensored", "gemma-4-12b-q5", "gemma-4-12b-q6", "gemma-4-12b-bf16" }.Select(id => ModelStore.SizeLabel(EmbeddedModelCatalog.TotalBytes(EmbeddedModelCatalog.Find(id)!))));
        // The big ones of later still: the 26B A4B and 31B with drafters, the 26B A4B Balanced and the Qwens without a file.
        Assert.Equal(["18.7 GB", "15.7 GB", "18.2 GB", "18.1 GB", "20.5 GB", "20.2 GB", "23.3 GB", "24.3 GB", "18.5 GB", "18.9 GB"],
            new[] { "gemma-4-26b-a4b", "gemma-4-26b-a4b-qat", "gemma-4-26b-a4b-qat-uncensored", "gemma-4-26b-a4b-uncensored", "gemma-4-31b", "gemma-4-31b-qat-uncensored", "qwen3.6-35b-a3b", "qwen3.6-35b-a3b-uncensored", "qwen3.8-27b", "qwen3.8-27b-uncensored" }.Select(id => ModelStore.SizeLabel(EmbeddedModelCatalog.TotalBytes(EmbeddedModelCatalog.Find(id)!))));
        var qwen = EmbeddedModelCatalog.Find("qwen3.8-27b-uncensored")!;
        Assert.Equal("https://huggingface.co/HauhauCS/Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-MTP-GGUF/resolve/993a5971fda8f30dd1b7eb2654792ba4415c7460/Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-Q4_K_P.gguf", EmbeddedModelCatalog.Url(qwen, qwen.Model).AbsoluteUri);
        // esatapedico's NVFP4 tiers (later still): the repository's tier word as the quant, one shared BF16 projector.
        Assert.Equal(["15.8 GB", "17.3 GB", "24.1 GB"],
            new[] { "qwen3.8-27b-nvfp4-very-low", "qwen3.8-27b-nvfp4-medium", "qwen3.8-27b-nvfp4-highest" }.Select(id => ModelStore.SizeLabel(EmbeddedModelCatalog.TotalBytes(EmbeddedModelCatalog.Find(id)!))));
        var nvfp4 = EmbeddedModelCatalog.Find("qwen3.8-27b-nvfp4-compact-low")!;
        Assert.Equal(("Qwen3.8 27B NVFP4", "COMPACT-LOW"), (nvfp4.Display, nvfp4.Quant));
        Assert.Equal("https://huggingface.co/esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF/resolve/bcd7a7d3e251d4ec0fd15c72584b5eb9e0981383/Qwen3.8-27B-NVFP4-MTP-COMPACT-LOW.gguf", EmbeddedModelCatalog.Url(nvfp4, nvfp4.Model).AbsoluteUri);
        Assert.Equal("https://huggingface.co/esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF/resolve/bcd7a7d3e251d4ec0fd15c72584b5eb9e0981383/mmproj-BF16.gguf", EmbeddedModelCatalog.Url(nvfp4, nvfp4.Mmproj).AbsoluteUri);
        Assert.All(EmbeddedModelCatalog.Models.Where(m => m.Repository == nvfp4.Repository), m => Assert.Equal(nvfp4.Mmproj, m.Mmproj));
        var gemma31 = EmbeddedModelCatalog.Find("gemma-4-31b-qat-uncensored")!;
        Assert.Equal("https://huggingface.co/HauhauCS/Gemma4-31B-QAT-Uncensored-HauhauCS-Balanced-MTP/resolve/9654466e82d83f5ebfe1518a369bc5900873abb1/mtp-gemma-4-31B-it.gguf", EmbeddedModelCatalog.Url(gemma31, gemma31.Drafter!).AbsoluteUri);
        Assert.All(EmbeddedModelCatalog.Models.Where(m => m.Repository == "unsloth/gemma-4-26B-A4B-it-GGUF"), m => Assert.Equal(EmbeddedModelCatalog.Find("gemma-4-26b-a4b")!.Drafter, m.Drafter));   // one repository, one drafter
        var balanced = EmbeddedModelCatalog.Find("gemma-4-12b-qat-uncensored")!;
        Assert.Equal("https://huggingface.co/HauhauCS/Gemma4-12B-QAT-Uncensored-HauhauCS-Balanced/resolve/ae8045ac2bd216293ca49a3065da2c942dde4b68/mmproj-Gemma4-12B-QAT-Uncensored-HauhauCS-Balanced-BF16.gguf", EmbeddedModelCatalog.Url(balanced, balanced.Mmproj).AbsoluteUri);
        var bf16 = EmbeddedModelCatalog.Find("gemma-4-12b-bf16")!;
        Assert.Equal("https://huggingface.co/unsloth/gemma-4-12b-it-GGUF/resolve/fc034cfff751157913579611efad8462ac1be606/gemma-4-12b-it-BF16.gguf", EmbeddedModelCatalog.Url(bf16, bf16.Model).AbsoluteUri);
        Assert.Equal(("BF16", "UD-Q5_K_XL", "UD-Q6_K_XL"), (bf16.Quant, EmbeddedModelCatalog.Find("gemma-4-12b-q5")!.Quant, EmbeddedModelCatalog.Find("gemma-4-12b-q6")!.Quant));
        Assert.All(EmbeddedModelCatalog.Models.Where(m => m.Repository == "unsloth/gemma-4-12b-it-GGUF"), m => Assert.Equal(EmbeddedModelCatalog.Find("gemma-4-12b")!.Mmproj, m.Mmproj));   // one repository, one projector

        // Muse Glimmer 30B (2026-09-30): Unsloth's Q4/Q5 with the Q8_0 projector and the DFlash drafter, all three counted.
        var muse = EmbeddedModelCatalog.Find("muse-glimmer-30b")!;
        const string museBase = "https://huggingface.co/unsloth/Muse-Glimmer-30B-GGUF/resolve/faa5b025c584459c13febfa5c59883516710ae39/";
        Assert.Equal(museBase + "Muse-Glimmer-30B-UD-Q4_K_XL.gguf", EmbeddedModelCatalog.Url(muse, muse.Model).AbsoluteUri);
        Assert.Equal(museBase + "mmproj-Muse-Glimmer-30B-Q8_0.gguf", EmbeddedModelCatalog.Url(muse, muse.Mmproj).AbsoluteUri);
        Assert.Equal(museBase + "dflash-kquant.gguf", EmbeddedModelCatalog.Url(muse, muse.Drafter!).AbsoluteUri);
        Assert.Equal(15_878_222_368L + 2_051_685_088L + 1_631_205_312L, EmbeddedModelCatalog.TotalBytes(muse));
        Assert.Equal(["19.6 GB", "25.5 GB"],
            new[] { "muse-glimmer-30b", "muse-glimmer-30b-q5" }.Select(id => ModelStore.SizeLabel(EmbeddedModelCatalog.TotalBytes(EmbeddedModelCatalog.Find(id)!))));
        Assert.Equal(("Muse Glimmer 30B", "UD-Q5_K_XL"), (EmbeddedModelCatalog.Find("muse-glimmer-30b-q5")!.Display, EmbeddedModelCatalog.Find("muse-glimmer-30b-q5")!.Quant));
        Assert.All(EmbeddedModelCatalog.Models.Where(m => m.Repository == muse.Repository), m => Assert.Equal((muse.Mmproj, muse.Drafter), (m.Mmproj, m.Drafter)));   // one repository, one projector and drafter
    }

    [Fact]
    public void EachModel_HasItsOwnFolder_SoTheTwoMmprojF16sNeverCollide()
    {
        string dir = Path.Combine("C:", "home", "models", "llm");
        var paths = EmbeddedModelCatalog.Models.SelectMany(m => new[] { EmbeddedModelCatalog.WeightsSpec(dir, m).Path, EmbeddedModelCatalog.MmprojSpec(dir, m).Path, EmbeddedModelCatalog.DrafterSpec(dir, m)?.Path }).OfType<string>().ToList();
        Assert.Equal(41 + 41 + 20, paths.Count);   // weights, projectors and the twenty drafters (Muse Glimmer's two DFlash ones since 2026-09-30) (one repository's builds share one projector's and drafter's name and bytes, each in its own folder)
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var e2b = EmbeddedModelCatalog.Find("gemma-4-e2b")!;
        var weights = EmbeddedModelCatalog.WeightsSpec(dir, e2b);
        Assert.Equal(Path.Combine(dir, "gemma-4-e2b", "gemma-4-E2B-it-UD-Q4_K_XL.gguf"), weights.Path);
        Assert.Equal(ModelFormat.Gguf, weights.Format);
        Assert.True(weights.Resumable);
        Assert.Equal(e2b.Model.Sha256, weights.Sha256);
        Assert.Equal(e2b.Model.Bytes, weights.ApproxBytes);
        Assert.Equal("Gemma 4 E2B vision", EmbeddedModelCatalog.MmprojSpec(dir, e2b)!.Display);
        var drafter = EmbeddedModelCatalog.DrafterSpec(dir, e2b)!;
        Assert.Equal(Path.Combine(dir, "gemma-4-e2b", "mtp-gemma-4-E2B-it.gguf"), drafter.Path);
        Assert.Equal(("Gemma 4 E2B MTP", e2b.Drafter!.Sha256, true), (drafter.Display, drafter.Sha256, drafter.Resumable));
        Assert.Null(EmbeddedModelCatalog.DrafterSpec(dir, EmbeddedModelCatalog.Find("qwen3.8-27b")!));   // its head is in the weights
        var muse = EmbeddedModelCatalog.DrafterSpec(dir, EmbeddedModelCatalog.Find("muse-glimmer-30b")!)!;
        Assert.Equal((Path.Combine(dir, "muse-glimmer-30b", "dflash-kquant.gguf"), "Muse Glimmer 30B DFlash"), (muse.Path, muse.Display));
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
    public void Arguments_WithMtp_DraftWithTheFile_OrTheWeightsOwnHead()
    {
        // A Gemma 4 drafter (2026-09-29): -md and the type, right after the projector.
        var drafted = LlamaArguments.Build(Launch() with { DrafterPath = @"C:\m\mtp-gemma-4-E2B-it.gguf", Mtp = true }, 1, "k");
        Assert.Equal(["-m", @"C:\m\model.gguf", "--mmproj", @"C:\m\mmproj-F16.gguf", "-md", @"C:\m\mtp-gemma-4-E2B-it.gguf", "--spec-type", "draft-mtp", "--alias"], drafted.Take(9));

        // Qwen3.8's head in its weights: the type alone, llama-server drafts on the target's own weights.
        var headed = LlamaArguments.Build(Launch() with { Mtp = true }, 1, "k");
        Assert.DoesNotContain("-md", headed);
        Assert.Equal(["--spec-type", "draft-mtp"], headed.Skip(4).Take(2));

        // Off (or a model without MTP): neither, and a drafter path alone does nothing.
        Assert.DoesNotContain("--spec-type", LlamaArguments.Build(Launch(), 1, "k"));
        Assert.DoesNotContain("-md", LlamaArguments.Build(Launch() with { DrafterPath = @"C:\m\d.gguf" }, 1, "k"));

        // Toggling Embedded drafter changes the launch, so the server restarts.
        Assert.NotEqual(Launch(), Launch() with { Mtp = true });

        // Muse Glimmer's DFlash drafter (2026-09-30): -md and draft-dflash in the same place.
        var dflash = LlamaArguments.Build(Launch() with { DrafterPath = @"C:\m\dflash-kquant.gguf", Mtp = true, Draft = DraftKind.DFlash }, 1, "k");
        Assert.Equal(["-md", @"C:\m\dflash-kquant.gguf", "--spec-type", "draft-dflash", "--alias"], dflash.Skip(4).Take(5));
        Assert.Equal(("draft-mtp", "draft-dflash"), (LlamaArguments.SpecType(DraftKind.Mtp), LlamaArguments.SpecType(DraftKind.DFlash)));
        Assert.NotEqual(Launch() with { Mtp = true }, Launch() with { Mtp = true, Draft = DraftKind.DFlash });
    }

    [Fact]
    public void Arguments_WithoutVision_LeaveTheProjectorOut()
    {
        var args = LlamaArguments.Build(Launch(mmproj: null, context: 0, layers: "all"), 1, "k");
        Assert.DoesNotContain("--mmproj", args);
        Assert.DoesNotContain("-c", args);   // fit (2026-09-30): -c left out, since llama.cpp reads -c 0 as the model's whole window
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
        Assert.NotEqual(Launch(), Launch() with { FitTargetMiB = 819 });   // a budget change restarts the server (later on 2026-09-29)
    }

    [Fact]
    public void Arguments_WithAVramBudget_PassTheFitTarget_AfterTheLayers()
    {
        // Later on 2026-09-29 (the user's ask): the margin llama.cpp's fit leaves free, right after -ngl; none without a budget.
        var args = LlamaArguments.Build(Launch() with { FitTargetMiB = 819 }, 1, "k");
        int layers = args.ToList().IndexOf("-ngl");
        Assert.Equal(["-ngl", "auto", "--fit-target", "819", "--parallel"], args.Skip(layers).Take(5));
        Assert.DoesNotContain("--fit-target", LlamaArguments.Build(Launch(), 1, "k"));
    }

    [Fact]
    public void Arguments_WithVramOnly_TraceTheLoad_AndKeepTheDrafterOnTheGpu()
    {
        // 2026-10-01 (the user's ask): -lv 4 last, where the load lines are; -ngld all beside a drafter. The service passes -ngl all.
        var args = LlamaArguments.Build(Launch(layers: "all") with { VramOnly = true, DrafterPath = @"C:\m\mtp.gguf", Mtp = true }, 1, "k");
        Assert.Equal(["-md", @"C:\m\mtp.gguf", "-ngld", "all", "--spec-type"], args.Skip(4).Take(5));
        Assert.Equal(["-lv", "4"], args.TakeLast(2));
        Assert.Equal("4", LlamaArguments.VramOnlyVerbosity);

        // A head in the weights: no drafter file, so no -ngld. Off: neither.
        Assert.DoesNotContain("-ngld", LlamaArguments.Build(Launch() with { VramOnly = true, Mtp = true }, 1, "k"));
        var off = LlamaArguments.Build(Launch() with { DrafterPath = @"C:\m\mtp.gguf", Mtp = true }, 1, "k");
        Assert.DoesNotContain("-ngld", off);
        Assert.DoesNotContain("-lv", off);
        Assert.NotEqual(Launch(), Launch() with { VramOnly = true });   // a toggle restarts the server
    }

    /// <summary>b11258's load lines at -lv 4, as the 2026-10-01 spike logged them on an RTX 5090 (Qwen3.8 27B NVFP4, CUDA).</summary>
    private static readonly string[] CudaLoad =
    [
        "0.00.564.765 W common_fit_params: failed to fit params to free device memory: n_gpu_layers already set by user to -2, abort",
        "0.02.306.208 I load_tensors: offloaded 66/66 layers to GPU",
        "0.02.306.214 I load_tensors:   CPU_Mapped model buffer size =  2425.00 MiB",
        "0.02.306.215 I load_tensors:        CUDA0 model buffer size = 18865.37 MiB",
        "0.08.429.420 I llama_context:  CUDA_Host  output buffer size =     0.95 MiB",
        "0.08.446.050 I llama_kv_cache:      CUDA0 KV buffer size = 12288.00 MiB",
        "0.08.708.490 I sched_reserve:      CUDA0 compute buffer size =   314.02 MiB",
        "0.08.708.496 I sched_reserve:  CUDA_Host compute buffer size =   212.02 MiB",
    ];

    [Fact]
    public void ALoadReport_ReadsTheLayers_TheHostBuffers_AndAFailedAllocation()
    {
        var report = LlamaLoadReport.Parse(CudaLoad);
        Assert.Equal((66, 66, 0), (report.OffloadedLayers, report.TotalLayers, report.LayersOnCpu));
        Assert.Equal(212.97, report.HostBufferMiB, 2);   // the two CUDA_Host buffers, not CUDA0's or CPU_Mapped
        Assert.False(report.OutOfMemory);   // "failed to fit params" is fit giving up, not an allocation

        // A drafter's load adds its own line; a partial one leaves layers on the CPU. Vulkan's host buffers count the same.
        var partial = LlamaLoadReport.Parse(["load_tensors: offloaded 51/66 layers to GPU", "load_tensors: offloaded 4/4 layers to GPU", "llama_context: Vulkan_Host  output buffer size =     1.00 MiB"]);
        Assert.Equal((55, 70, 15), (partial.OffloadedLayers, partial.TotalLayers, partial.LayersOnCpu));
        Assert.Equal(1.0, partial.HostBufferMiB);

        // The two backends' failed allocations, as the spike logged them.
        Assert.True(LlamaLoadReport.Parse(["E ggml_backend_cuda_buffer_type_alloc_buffer: allocating 16384.00 MiB on device 0: cudaMalloc failed: out of memory"]).OutOfMemory);
        Assert.True(LlamaLoadReport.Parse(["ggml_vulkan: vk::Device::allocateMemory: ErrorOutOfDeviceMemory"]).OutOfMemory);
        Assert.True(LlamaLoadReport.Parse(["E alloc_tensor_range: failed to allocate Vulkan0 buffer of size 1073741824"]).OutOfMemory);

        var empty = LlamaLoadReport.Parse(["", "srv  llama_server: model loaded"]);
        empty.Add(null);
        Assert.Equal((0, 0, 0.0, false), (empty.TotalLayers, empty.LayersOnCpu, empty.HostBufferMiB, empty.OutOfMemory));
        Assert.True(empty.Loaded);
    }

    [Fact]
    public void ALoadReport_IsLoadedOnlyAtTheLastLine()
    {
        // The review's finding (2026-10-01): /health may say 200 before the pipe delivered the host buffers; the host waits for this.
        var report = LlamaLoadReport.Parse(CudaLoad);
        Assert.False(report.Loaded);
        report.Add("0.08.947.960 I srv  llama_server: model loaded");
        Assert.True(report.Loaded);
        Assert.False(LlamaLoadReport.Parse(["srv    load_model: loading model 'C:\\m\\model loaded.gguf'"]).Loaded);
    }

    [Fact]
    public void ALoadReport_CountsEachBufferOncePerLoad()
    {
        // The review's finding (2026-10-01): a second reserve of a buffer replaces it, so a repeated line cannot widen the allowance.
        var report = LlamaLoadReport.Parse([.. CudaLoad, "sched_reserve:  CUDA_Host compute buffer size =   212.02 MiB", "sched_reserve:  CUDA_Host compute buffer size =   180.00 MiB"]);
        Assert.Equal(180.95, report.HostBufferMiB, 2);
        Assert.Equal((66, 66), (report.OffloadedLayers, report.TotalLayers));

        // A drafter's load is a load of its own: its buffers add to the weights'.
        var drafted = LlamaLoadReport.Parse([.. CudaLoad, "load_tensors: offloaded 4/4 layers to GPU", "llama_context:  CUDA_Host  output buffer size =     0.95 MiB", "sched_reserve:  CUDA_Host compute buffer size =    20.00 MiB"]);
        Assert.Equal(233.92, drafted.HostBufferMiB, 2);
        Assert.Equal((70, 70, 0), (drafted.OffloadedLayers, drafted.TotalLayers, drafted.LayersOnCpu));

        // A buffer line before any load line still counts.
        Assert.Equal(3.0, LlamaLoadReport.Parse(["llama_context: Vulkan_Host  output buffer size =     3.00 MiB"]).HostBufferMiB);
    }

    [Theory]
    [InlineData("W ggml_cuda_host_malloc: failed to allocate 512.00 MiB of pinned memory: out of memory")]
    [InlineData("ggml_vulkan: Failed to allocate pinned memory (vk::Device::allocateMemory: ErrorOutOfHostMemory)")]
    [InlineData("W ggml_vulkan: failed to allocate pinned memory: ErrorOutOfDeviceMemory")]
    [InlineData("srv  operator(): failed to allocate a slot")]
    [InlineData("0.00.564.765 W common_fit_params: failed to fit params to free device memory: out of memory target")]
    public void ALoadReport_DoesNotTakeAFallbackWarning_ForAFailedAllocation(string line)
    {
        // The review's finding (2026-10-01): pinned memory that falls back to plain memory is no reason to say "does not fit".
        Assert.False(LlamaLoadReport.Parse([line]).OutOfMemory);
    }

    [Fact]
    public void AVramOnlyLoad_WithNoLayerLine_IsRefusedAsNotChecked()
    {
        // The review's finding (2026-10-01): no load line used to pass as 0 of 0 layers on the CPU.
        Assert.Equal(EmbeddedLlmText.VramNotChecked, LlamaServerHost.VramRefusal(new LlamaLoadReport(), 100L << 20));
        Assert.Equal(EmbeddedLlmText.VramNotChecked, LlamaServerHost.VramRefusal(LlamaLoadReport.Parse(["srv  llama_server: model loaded"]), null));

        var clean = LlamaLoadReport.Parse(CudaLoad);
        Assert.Null(LlamaServerHost.VramRefusal(clean, 344L << 20));
        Assert.Null(LlamaServerHost.VramRefusal(clean, null));
        Assert.Equal(EmbeddedLlmText.VramSpilled(new VramSpill(0, 66, 527)), LlamaServerHost.VramRefusal(clean, 740L << 20));
    }

    [Fact]
    public void TheSpillCheck_AllowsTheHostBuffers_AndASlack_ButNoLayerOnTheCpu()
    {
        // Measured 2026-10-01: a clean load 78-143 MiB over its host buffers, one past free VRAM 527 MiB over.
        Assert.Equal(256, VramSpill.SlackMiB);
        var report = LlamaLoadReport.Parse(CudaLoad);
        Assert.Null(VramSpill.Check(report, 344L << 20));   // the clean load's reading: 131 MiB over
        Assert.Null(VramSpill.Check(report, (213L + 256) << 20));   // the slack itself still passes
        Assert.Null(VramSpill.Check(report, null));   // not read: the layers alone decide
        Assert.Equal(new VramSpill(0, 66, 527), VramSpill.Check(report, 740L << 20));   // the spill's reading
        Assert.Equal(new VramSpill(0, 66, 527), VramSpill.Check(report, 740L << 20, slackMiB: 100));

        var partial = LlamaLoadReport.Parse(["load_tensors: offloaded 51/66 layers to GPU"]);
        Assert.Equal(new VramSpill(15, 66, 0), VramSpill.Check(partial, null));
        Assert.Equal(new VramSpill(15, 66, 0), VramSpill.Check(partial, 100L << 20));
    }

    [Theory]
    [InlineData("off", 0)]
    [InlineData(" OFF ", 0)]
    [InlineData("0", 0)]
    [InlineData("50", 50)]
    [InlineData("99", 99)]
    [InlineData("92 %", 92)]
    [InlineData("92%", 92)]
    [InlineData("49", null)]
    [InlineData("100", null)]
    [InlineData("-5", null)]
    [InlineData("half", null)]
    [InlineData("", null)]
    public void VramBudget_HasItsRange(string text, int? expected)
    {
        Assert.Equal(expected, EmbeddedVramBudget.Parse(text));
        if (expected is { } value)
        {
            Assert.True(EmbeddedVramBudget.IsValid(value));
            Assert.Equal(value, EmbeddedVramBudget.Effective(value));
        }
    }

    [Fact]
    public void VramBudget_IsAMarginOfTheRest_InMiB()
    {
        Assert.Equal(91, new Settings.AppSettingsData().EmbeddedVramBudget);   // 91 by default since 2026-09-30, the user's call (off until then)
        Assert.Equal(91, EmbeddedVramBudget.Default);
        Assert.Equal(819, EmbeddedVramBudget.FitTargetMiB(8L << 30, 90));   // 8 GiB at 90 %: 819.2 MiB left free
        Assert.Equal(1229, EmbeddedVramBudget.FitTargetMiB(24L << 30, 95));
        Assert.Equal(6144, EmbeddedVramBudget.FitTargetMiB(12L << 30, 50));
        Assert.Equal(1966, EmbeddedVramBudget.FitTargetMiB(24L << 30, 92));
        Assert.Equal(0, EmbeddedVramBudget.Effective(49));
        Assert.Equal("must be off or a whole percent from 50 to 99", EmbeddedVramBudget.Error);
        Assert.Equal("off", EmbeddedVramBudget.OffWord);
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

    [Fact]
    public void ContextSize_FitsByDefault()
    {
        // 0 — fit, the largest the VRAM budget holds — since later on 2026-09-29 (the user's call; 32768 until then).
        Assert.Equal(0, EmbeddedContextSize.Default);
        Assert.Equal(0, new Settings.AppSettingsData().EmbeddedContextSize);
        Assert.Equal("must be 0 (fit) or a whole number from 512 to 262144", EmbeddedContextSize.Error);
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
        Assert.Equal("Gemma 4 E2B is not installed. Install it now (download 4.3 GB + llama.cpp runtime 577 MB)?", EmbeddedLlmText.InstallQuestion(e2b, LlamaRelease.Bytes(LlamaBackend.Cuda)));   // its MTP drafter counts since 2026-09-29
        Assert.Equal("download 4.3 GB", EmbeddedLlmText.InstallCost(e2b, 0));
        Assert.Equal("Gemma 4 E2B's MTP drafter could not be downloaded, so it starts without MTP: timed out", EmbeddedLlmText.DrafterFailed(e2b, "timed out"));
        Assert.Equal("Muse Glimmer 30B's DFlash drafter could not be downloaded, so it starts without DFlash: timed out", EmbeddedLlmText.DrafterFailed(EmbeddedModelCatalog.Find("muse-glimmer-30b")!, "timed out"));
        Assert.Equal("the embedded LLM is off; turn Embedded LLM server enabled on in /settings › Embedded to use it", EmbeddedLlmText.SwitchedOffError);
        Assert.Equal("2 of 4 installed (9.4 GB)", EmbeddedLlmText.ModelsRowValue(2, 4, 9_400_000_000));
        Assert.Equal("none of 4 installed", EmbeddedLlmText.ModelsRowValue(0, 4, 0));
        Assert.Equal("auto (cuda: NVIDIA driver 610.88)", EmbeddedLlmText.BackendRowValue("auto", new BackendChoice(LlamaBackend.Cuda, "NVIDIA driver 610.88")));
        Assert.Equal("llama-server exited with code 1 before it was ready: error: failed to load model", EmbeddedLlmText.ExitedEarly(1, "error: failed to load model"));
        Assert.Equal("llama-server exited with code 3 before it was ready", EmbeddedLlmText.ExitedEarly(3, " "));
        Assert.Equal("llama-server was not ready after 300 s", EmbeddedLlmText.StartTimeout(TimeSpan.FromMinutes(5)));
        Assert.Equal("embedded (llama.cpp)", EmbeddedLlmText.UrlDisplay);
        // The spinner's two lines (2026-09-29, the user's wording): the llama, no "on llama.cpp", no ellipsis.
        Assert.Equal("🦙 starting Gemma 4 E2B", EmbeddedLlmText.StartingLabel(e2b));
        Assert.Equal("🦙 loading Gemma 4 E2B", EmbeddedLlmText.LoadingLabel(e2b));

        // Embedded VRAM only's refusals (2026-10-01): what spilled, then what to lower.
        const string Advice = "set Embedded context size to 0 (fit) or lower it, lower Embedded VRAM budget, close what else uses the GPU, or pick a smaller model";
        Assert.Equal("it spilled about 527 MiB of shared GPU memory into system RAM, and Embedded VRAM only is on; " + Advice, EmbeddedLlmText.VramSpilled(new VramSpill(0, 66, 527)));
        Assert.Equal("it spilled 15 of 66 layers and about 40 MiB of shared GPU memory into system RAM, and Embedded VRAM only is on; " + Advice, EmbeddedLlmText.VramSpilled(new VramSpill(15, 66, 40)));
        Assert.Equal("it does not fit in VRAM with every layer on the GPU, and Embedded VRAM only is on; " + Advice, EmbeddedLlmText.VramDidNotFit);
        Assert.Equal("Embedded VRAM only is on, but the backend is the CPU; choose CUDA or Vulkan in Embedded backend, or turn Embedded VRAM only off", EmbeddedLlmText.VramOnlyOnCpu);
        Assert.Equal("Embedded VRAM only is on, but the GPU has little or no VRAM of its own (an integrated GPU shares system RAM); turn Embedded VRAM only off to run it there", EmbeddedLlmText.VramOnlyNoVram);
        Assert.Equal("llama-server did not say where it put the model's layers, so Embedded VRAM only could not check the load; turn Embedded VRAM only off to start it", EmbeddedLlmText.VramNotChecked);
        Assert.Equal(1024, EmbeddedLlmService.MinDedicatedVramMiB);

        // The check's log lines, moved out of the host (the same day's review).
        Assert.Equal("Embedded VRAM only: 66/66 layers on the GPU, host buffers 213 MiB, shared GPU memory 344 MiB.", EmbeddedLlmText.VramCheckSummary(LlamaLoadReport.Parse(CudaLoad), 344L << 20));
        Assert.EndsWith("shared GPU memory unknown.", EmbeddedLlmText.VramCheckSummary(new LlamaLoadReport(), null));
        Assert.Equal("Embedded VRAM only: no \"model loaded\" line within 10 s; checking the lines that came.", EmbeddedLlmText.VramLoadLineLate(TimeSpan.FromSeconds(10)));
        Assert.StartsWith("Embedded VRAM only: the server's shared GPU memory was not read", EmbeddedLlmText.VramSharedNotRead);
    }
}
