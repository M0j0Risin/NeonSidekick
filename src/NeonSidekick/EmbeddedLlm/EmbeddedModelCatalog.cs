using NeonSidekick.Speech;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>
/// How uncensored a model is (later on 2026-09-29, the user's ask, for the model lists' fourth column): not at all, an
/// uncensored build, or an aggressive one — HauhauCS's own word in the repository's name ("Aggressive", beside "Balanced").
/// </summary>
public enum UncensoredKind
{
    None,
    Uncensored,
    Aggressive,
}

/// <summary>
/// How a model drafts (2026-09-30, when Muse Glimmer joined): <see cref="Mtp"/> — Gemma 4's <c>mtp-*.gguf</c> heads or
/// Qwen3.8's NextN head in the weights, <c>--spec-type draft-mtp</c> — or <see cref="DFlash"/>, Muse Glimmer's
/// block-diffusion drafter file (an architecture-<c>dflash</c> GGUF that proposes 16 tokens a pass),
/// <c>--spec-type draft-dflash</c>. The target verifies every drafted token either way.
/// </summary>
public enum DraftKind
{
    Mtp,
    DFlash,
}

/// <summary>One file of a <see cref="EmbeddedModel"/>: its name in the repository, its exact size and its SHA-256 (lowercase hex).</summary>
public sealed record EmbeddedFile(string Name, long Bytes, string Sha256);

/// <summary>The sampling a model's card recommends; passed to <c>llama-server</c> as its defaults, which a request's own values still override.</summary>
public sealed record EmbeddedSampling(double Temperature, double TopP, int TopK);

/// <summary>
/// One model the app can download and run itself: its catalog <paramref name="Id"/> (what <c>LLM model</c> saves and
/// <c>llama-server</c> is told to call itself), a <paramref name="Display"/> name, the quantisation, the Hugging Face
/// repository pinned to a commit, the weights, the vision projector (<c>mmproj</c>: every model has one — a text-only
/// model was allowed for a few hours on 2026-09-29, until empero-ai's Qwen3.8 9B distill left the catalog) and the
/// recommended sampling.
/// Its MTP (multi-token prediction, speculative decoding; 2026-09-29, the user's ask: "using the drafters where
/// available") comes one of two ways: a separate small <paramref name="Drafter"/> GGUF (Gemma 4's <c>gemma4-assistant</c>
/// heads, passed as <c>-md</c>), or an <paramref name="MtpHead"/> the weights carry themselves (Qwen3.8's NextN layer,
/// <c>qwen35.nextn_predict_layers = 1</c>, which llama.cpp runs on the target's own weights when no <c>-md</c> is given).
/// Neither is a model without MTP. <paramref name="ToolCalls"/> (2026-09-29, the user's ask, for the 🛠️ column): the
/// model's chat template carries tool calls — every model in the catalog does, from its card, and <c>--jinja</c> is what
/// makes <c>llama-server</c> honour them (<see cref="LlamaArguments"/>); false is for a model that would not.
/// <paramref name="Draft"/> (2026-09-30): which kind of drafting the drafter or head does — MTP for every model but
/// Muse Glimmer, whose <paramref name="Drafter"/> is a DFlash one; it picks <c>--spec-type</c>.
/// </summary>
public sealed record EmbeddedModel(
    string Id,
    string Display,
    string Quant,
    string Repository,
    string Revision,
    EmbeddedFile Model,
    EmbeddedFile Mmproj,
    EmbeddedSampling Sampling,
    EmbeddedFile? Drafter = null,
    bool MtpHead = false,
    bool ToolCalls = true,
    DraftKind Draft = DraftKind.Mtp)
{
    /// <summary>Whether the model can draft for itself at all: a drafter file (MTP or DFlash) or a built-in head.</summary>
    public bool HasMtp => Drafter is not null || MtpHead;

    /// <summary>
    /// Whether the model reads images (2026-09-29, the user's ask, for the 👁️ column): every model carries its vision
    /// projector (<see cref="Mmproj"/>), so every one does — a text-only model would make the projector optional and this
    /// <c>Mmproj is not null</c>. What the model can do, whatever <c>Embedded vision</c> says.
    /// </summary>
    public bool Vision => true;

    /// <summary>
    /// Whether the model is an uncensored build (later on 2026-09-29, for the model lists' <c>uncensored</c> filter): its name
    /// says so — every HauhauCS build in the catalog carries "Uncensored" in <see cref="Display"/>, Gemma and Qwen alike, and
    /// no other does. A new uncensored model keeps the word in its name.
    /// </summary>
    public bool Uncensored => Display.Contains("Uncensored", StringComparison.Ordinal);

    /// <summary>
    /// Which uncensored build it is (later on 2026-09-29, the user's ask: "based on the original name"): <see cref="UncensoredKind.Aggressive"/>
    /// when the repository's name says "Aggressive" (HauhauCS's E2B/E4B, Qwen3.6 35B A3B and Qwen3.8 27B), else
    /// <see cref="UncensoredKind.Uncensored"/> for an uncensored one (the "Balanced" builds), else none.
    /// </summary>
    public UncensoredKind UncensoredKind =>
        !Uncensored ? UncensoredKind.None
        : Repository.Contains("Aggressive", StringComparison.Ordinal) ? UncensoredKind.Aggressive
        : UncensoredKind.Uncensored;
}

/// <summary>
/// The models the embedded server offers (2026-09-29, the user's picks: the four Gemma 4 E2B/E4B builds first, the E4B
/// instruct, the two 12Bs and HauhauCS's 12B QAT Balanced later that day, then the 12B instruct in three more
/// quantisations — UD-Q5_K_XL, UD-Q6_K_XL and BF16 — as empero-ai's Qwen3.8 9B distill left: "everything will be Gemma
/// for now", every model with vision), in the alphabetical order of their names (ordinal: "Gemma 4 12B" before "Gemma 4
/// E2B"), the user's call later that day so each model's builds sit together in <c>/server</c> and the catalog; builds of
/// one name follow their size, smallest first. "The first installed" follows the order when <c>LLM model</c> is blank. Each is pinned to a Hugging Face commit,
/// not <c>main</c>, with the exact size and SHA-256 the API published for it at that commit, so an upstream re-upload can
/// never turn an install into a checksum failure — nor slip a different file past one. To add or update a model:
/// <c>GET https://huggingface.co/api/models/&lt;repo&gt;</c> gives the commit (<c>sha</c>) and whether it is
/// <c>gated</c> (it must not be: the download carries no token), <c>?expand[]=gguf</c> the architecture (the pinned
/// llama.cpp must know it); <c>GET …/tree/&lt;commit&gt;</c> gives each file's <c>size</c> and <c>lfs.oid</c>.
///
/// <para>Every file lives under <c>&lt;models&gt;/llm/&lt;id&gt;/</c>: the Unsloth repositories all name their
/// projector <c>mmproj-F16.gguf</c>, different files under one name. The projector is the F16 one where there is a
/// choice — half the size of F32 at no visible cost — and BF16 for HauhauCS's 12B, the only one it publishes. The
/// 12B's four builds share the one repository, projector and drafter; each keeps a copy in its own folder.</para>
///
/// <para>Later on 2026-09-29 (the user's picks) the big ones joined: Gemma 4 26B A4B (the MoE: 4B active of 26B) in
/// UD-Q4/Q5/Q6_K_XL, its QAT build, HauhauCS's 26B A4B QAT Balanced and 26B A4B Balanced (Q4/Q5/Q6_K_P), Gemma 4 31B in
/// UD-Q4/Q5_K_XL, its QAT build and HauhauCS's 31B QAT Balanced; then Qwen came back — Qwen3.6 35B A3B (UD-Q4/Q5_K_XL),
/// HauhauCS's 35B A3B Aggressive, Qwen3.8 27B (UD-Q4/Q5/Q6_K_XL) and HauhauCS's 27B Aggressive (Q4/Q5_K_P). The pinned
/// llama.cpp (b11258) knows every architecture: <c>gemma4</c>, <c>qwen35</c>, <c>qwen35moe</c>.</para>
///
/// <para>MTP drafters, the same day: every Unsloth Gemma 4 repository and HauhauCS's 12B/26B/31B QAT ones ship a root
/// <c>mtp-*.gguf</c> (a <c>gemma4-assistant</c> model, 60–515 MB) that pairs with any quantisation of its model, so the
/// builds of one repository share one; HauhauCS's E2B/E4B Aggressive and 26B A4B Balanced ship none. The existing
/// pins already had theirs — no commit moved. Qwen3.8's weights carry their own NextN head (<see cref="EmbeddedModel.MtpHead"/>),
/// so two files are left out on purpose: Unsloth's <c>MTP/mtp-Qwen3.8-27B-Q4_0.gguf</c> (1.37 GB, that same head again)
/// and HauhauCS's <c>…-FastMTP-32K.gguf</c> (its card says it needs HauhauCS's own llama.cpp patch; the card's
/// "embedded MTP" path is the one an upstream build runs). Qwen3.6 35B A3B has neither head nor drafter.</para>
///
/// <para>Still later that day (the user's pick) esatapedico's Qwen3.8 27B NVFP4 joined in eight of its nine tiers,
/// VERY-LOW to HIGHEST (the Quant column carries the repository's own tier word; ORIG, 33 GB of mostly BF16, left out).
/// The tiers are not whole-model quantisations: VERY-LOW to VERY-HIGH share one NVFP4 backbone (GGML type 40, every
/// block's attention and MLP) and differ only in the output head, the token embedding and the NextN head
/// (<see cref="EmbeddedModel.MtpHead"/>, in the weights like Unsloth's); HIGHEST keeps the MLP of layers 0–55 in NVFP4 and
/// the rest in Q8_0/BF16. NVFP4 wants the CUDA build on a Blackwell GPU (sm_120; the pinned CUDA 13.4 build covers it,
/// <see cref="LlamaRelease"/>) — nothing gates the rows by GPU, the README says so. Its projector is Unsloth's BF16 one,
/// byte for byte. Sampling as Qwen's card (the repository's card repeats it).</para>
///
/// <para>On 2026-09-30 (the user's pick) Meta's Muse Glimmer 30B joined — a dense 30B with its own perception encoder —
/// in Unsloth's UD-Q4_K_XL and UD-Q5_K_XL. b11258 knows its architecture (<c>muse-glimmer</c>), its projector and its
/// <c>&lt;atem:function_calls&gt;</c> tool calls. The repository has no F16 projector, so it is the Q8_0 one (2 GB; BF16 is
/// 3.85 GB, Meta's own Q4_K_M 1.4 GB), the user's pick. Its drafter is not MTP but DFlash (<see cref="DraftKind.DFlash"/>):
/// Unsloth's <c>dflash-kquant.gguf</c>, 1.6 GB, which Meta's card measured at 3.1× on an RTX 5090 in llama.cpp. Sampling
/// as Meta's card (1.0, 0.95, 64; esatapedico's card says 0.7, 0.95, 40, not the model's author). esatapedico's
/// Muse Glimmer 30B NVFP4 tiers were asked for too and wait: its card says they need a fix that is not in llama.cpp
/// (ggml-org/llama.cpp#27178, still open that day, the fix only in gabrielcosi's fork, which ships no builds) and that a
/// stock build loads them but generates garbage; they come in with a llama.cpp pin past the fix, with a projector and
/// drafter from another repository (theirs carries neither).</para>
///
/// <para>Sampling, from each card: Google's Gemma 4 temperature 1.0, top-p 0.95, top-k 64 (HauhauCS's E2B/E4B and 26B A4B
/// Balanced the same); HauhauCS's 12B/26B/31B QAT Balanced 0.6, 0.9, 64 (their min-p 0.05 is llama.cpp's default; their
/// repeat penalty 1.1 is not carried); Qwen's "thinking, general" line 1.0, 0.95, 20 (its presence penalty is not
/// carried either); Muse Glimmer's 1.0, 0.95, 64. Q4_K_P is HauhauCS's own per-model quantisation profile, not a llama.cpp type name, but it uses
/// standard GGUF tensor types and loads in any llama.cpp build.</para>
/// </summary>
public static class EmbeddedModelCatalog
{
    private static readonly EmbeddedSampling Gemma4 = new(1.0, 0.95, 64);
    private static readonly EmbeddedSampling HauhauBalanced = new(0.6, 0.9, 64);
    private static readonly EmbeddedSampling Qwen = new(1.0, 0.95, 20);
    private static readonly EmbeddedSampling Muse = new(1.0, 0.95, 64);   // Gemma 4's values, from Meta's own card

    // The files one repository's builds share, each at the pinned commit.
    private static readonly EmbeddedFile Gemma12bMmproj = new("mmproj-F16.gguf", 175_115_840, "91f086971e56d7a7d8d39e271873fccdb49541bd259d6e02c401a4f1cb7a219e");
    private static readonly EmbeddedFile Gemma12bDrafter = new("mtp-gemma-4-12b-it.gguf", 465_109_248, "145db9094bc0f85f1701e255a2ed216dcc9800fc8bc8631ad00905b456bd451b");
    private static readonly EmbeddedFile Gemma26bMmproj = new("mmproj-F16.gguf", 1_193_058_784, "418a6d8723067cd712235facbbc5cba6c8fbbd413fc1292d2aace5a027d5a42f");
    private static readonly EmbeddedFile Gemma26bDrafter = new("mtp-gemma-4-26B-A4B-it.gguf", 461_766_816, "6326fb9f5e487aa8dcdd313a091e3c67724cb2a666ec3b7d2895b5b26d93ed1b");
    private static readonly EmbeddedFile Hauhau26bMmproj = new("mmproj-Gemma4-26B-A4B-Uncensored-HauhauCS-Balanced-f16.gguf", 1_193_058_368, "9422eeb070cc7f6e412d34d03e2fb4693bb90dcdba4a0294150de28a04f4e12c");
    private static readonly EmbeddedFile Gemma31bMmproj = new("mmproj-F16.gguf", 1_198_957_024, "6edcca228213c28d3567a35d22f849eea52d8360875093851959adf5d2f270eb");
    private static readonly EmbeddedFile Gemma31bDrafter = new("mtp-gemma-4-31B-it.gguf", 514_687_104, "5ae8b0117bed601e8924c6305bd5b0585de361d51f0e77091bcb4252cf1f27de");
    private static readonly EmbeddedFile Qwen36Mmproj = new("mmproj-F16.gguf", 899_283_680, "8971ee4f331ff0a4c609374f32984b3d4e6dc086c0aa35f1d637fad1829e887f");
    private static readonly EmbeddedFile Qwen38Mmproj = new("mmproj-F16.gguf", 927_607_488, "cbb841a9ee0636b2ec172f5bb8df2ea8dfeb01e90fe7c6126581d662a0b4e43e");
    private static readonly EmbeddedFile Nvfp4Mmproj = new("mmproj-BF16.gguf", 931_146_432, "83ee4f4f205fa514161778c41df1ea14144faa0f713510893b63c2395f5c2d53");
    private static readonly EmbeddedFile Hauhau38Mmproj = new("mmproj-Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-BF16.gguf", 931_146_624, "5681b690bcb8eb10cd28d62d078cb4e01521a3ea4880a3fc7d54de72de2dd142");
    private static readonly EmbeddedFile MuseMmproj = new("mmproj-Muse-Glimmer-30B-Q8_0.gguf", 2_051_685_088, "01ff73c95108e1754a4c145176c6d3ba44338942285cb87dcac7f4f193192ea2");
    private static readonly EmbeddedFile MuseDrafter = new("dflash-kquant.gguf", 1_631_205_312, "27d9a805fa29b943cfb6ad4843367cd4eaaaf06bd452d8cc3e00a2cd18a677bc");

    public static readonly IReadOnlyList<EmbeddedModel> Models =
    [
        new(
            "gemma-4-12b",
            "Gemma 4 12B",
            "UD-Q4_K_XL",
            "unsloth/gemma-4-12b-it-GGUF",
            "fc034cfff751157913579611efad8462ac1be606",
            new EmbeddedFile("gemma-4-12b-it-UD-Q4_K_XL.gguf", 7_366_423_360, "90fd944d227e9d9b68e7e2c7d5b57b79d4c66ed521b0919fbbd932cf834f6f8e"),
            Gemma12bMmproj,
            Gemma4,
            Gemma12bDrafter),
        new(
            "gemma-4-12b-q5",
            "Gemma 4 12B",
            "UD-Q5_K_XL",
            "unsloth/gemma-4-12b-it-GGUF",
            "fc034cfff751157913579611efad8462ac1be606",
            new EmbeddedFile("gemma-4-12b-it-UD-Q5_K_XL.gguf", 8_606_021_440, "d9d55dbb17c73cf274cc1e701a1564a5b35ec03ffde474e7c6c93c3562d8d825"),
            Gemma12bMmproj,
            Gemma4,
            Gemma12bDrafter),
        new(
            "gemma-4-12b-q6",
            "Gemma 4 12B",
            "UD-Q6_K_XL",
            "unsloth/gemma-4-12b-it-GGUF",
            "fc034cfff751157913579611efad8462ac1be606",
            new EmbeddedFile("gemma-4-12b-it-UD-Q6_K_XL.gguf", 10_685_012_800, "70d04059c74be85c5e709921f05acac412b8b8f24f3ee7dd07e91ddc5f4d4de8"),
            Gemma12bMmproj,
            Gemma4,
            Gemma12bDrafter),
        new(
            "gemma-4-12b-bf16",
            "Gemma 4 12B",
            "BF16",
            "unsloth/gemma-4-12b-it-GGUF",
            "fc034cfff751157913579611efad8462ac1be606",
            new EmbeddedFile("gemma-4-12b-it-BF16.gguf", 23_832_066_656, "5a5eefea73350705c6753105b725a51301d653e8d6646173db29a7e8da8e6efd"),
            Gemma12bMmproj,
            Gemma4,
            Gemma12bDrafter),
        new(
            "gemma-4-12b-qat",
            "Gemma 4 12B QAT",
            "UD-Q4_K_XL",
            "unsloth/gemma-4-12B-it-qat-GGUF",
            "980b060c40a8539ac159e0501a3e0f66a6365af3",
            new EmbeddedFile("gemma-4-12B-it-qat-UD-Q4_K_XL.gguf", 6_716_356_800, "90fd44e29e0d7cffeb0fd00dc73cfdab9ed0b0e95306ecf7821ea634c940c370"),
            new EmbeddedFile("mmproj-F16.gguf", 175_115_840, "ecc4e93128da8363b7dbf2193eab98cf1142353f52ceaa0c95c0872997aaadd3"),
            Gemma4,
            new EmbeddedFile("mtp-gemma-4-12B-it.gguf", 253_708_800, "fcb35dea42c71333db904cee11baac525c9ef872818ee3753f6cb156f3c6f4f6")),
        new(
            "gemma-4-12b-qat-uncensored",
            "Gemma 4 12B QAT Uncensored",
            "Q4_K_M",
            "HauhauCS/Gemma4-12B-QAT-Uncensored-HauhauCS-Balanced",
            "ae8045ac2bd216293ca49a3065da2c942dde4b68",
            new EmbeddedFile("Gemma4-12B-QAT-Uncensored-HauhauCS-Balanced-Q4_K_M.gguf", 7_381_381_760, "59656d7494d6376ca97e9e20b64ea2e16cd97f12ec6d47bfccba91cb785b5134"),
            new EmbeddedFile("mmproj-Gemma4-12B-QAT-Uncensored-HauhauCS-Balanced-BF16.gguf", 175_115_264, "b59e815479b7e5f0665bd29e6784c104a368092bcbc63120148c606f9276ab8e"),
            HauhauBalanced,
            new EmbeddedFile("mtp-gemma-4-12B-it.gguf", 253_707_328, "c50c91c35f04903815b2e8930cbb8c8c5bee0e1aa00748c30a7b8ff05d2310b4")),
        new(
            "gemma-4-26b-a4b",
            "Gemma 4 26B A4B",
            "UD-Q4_K_XL",
            "unsloth/gemma-4-26B-A4B-it-GGUF",
            "c099eb48e663fd284577b04978a94ffccb261841",
            new EmbeddedFile("gemma-4-26B-A4B-it-UD-Q4_K_XL.gguf", 17_010_980_576, "ef728c8e0c337fd1067b947af006e38a9ef2419e56feced4fd29b4bf0636e30c"),
            Gemma26bMmproj,
            Gemma4,
            Gemma26bDrafter),
        new(
            "gemma-4-26b-a4b-q5",
            "Gemma 4 26B A4B",
            "UD-Q5_K_XL",
            "unsloth/gemma-4-26B-A4B-it-GGUF",
            "c099eb48e663fd284577b04978a94ffccb261841",
            new EmbeddedFile("gemma-4-26B-A4B-it-UD-Q5_K_XL.gguf", 21_217_769_184, "d7553b64566bcb0b8b7d712e12e80213f3f02e756ab17099628925ce2e39e597"),
            Gemma26bMmproj,
            Gemma4,
            Gemma26bDrafter),
        new(
            "gemma-4-26b-a4b-q6",
            "Gemma 4 26B A4B",
            "UD-Q6_K_XL",
            "unsloth/gemma-4-26B-A4B-it-GGUF",
            "c099eb48e663fd284577b04978a94ffccb261841",
            new EmbeddedFile("gemma-4-26B-A4B-it-UD-Q6_K_XL.gguf", 23_295_391_456, "b01ee10a1423c17f9c4384f1fc569726b8782c5403557ff138ceb9468ca49d6b"),
            Gemma26bMmproj,
            Gemma4,
            Gemma26bDrafter),
        new(
            "gemma-4-26b-a4b-qat",
            "Gemma 4 26B A4B QAT",
            "UD-Q4_K_XL",
            "unsloth/gemma-4-26B-A4B-it-qat-GGUF",
            "7b92b5b28818151e8669af2e45e88d6086f490dd",
            new EmbeddedFile("gemma-4-26B-A4B-it-qat-UD-Q4_K_XL.gguf", 14_249_047_104, "a7c5bc715f5ff8e99a3e8901ce7d2b42b402c669bf24f7c5250747633d0f5891"),
            new EmbeddedFile("mmproj-F16.gguf", 1_193_058_784, "d00f211a7d4f7fb19bd9b75d8e9342eccffb5920b08fd9562167560fdfcc5dd1"),
            Gemma4,
            new EmbeddedFile("mtp-gemma-4-26B-A4B-it.gguf", 251_939_328, "7272d97595f0d4c74bd7b623492b7dbdaafd8b7c72f329a8270ba4eca68f768a")),
        new(
            "gemma-4-26b-a4b-qat-uncensored",
            "Gemma 4 26B A4B QAT Uncensored",
            "Q4_K_M",
            "HauhauCS/Gemma4-26B-A4B-QAT-Uncensored-HauhauCS-Balanced-MTP",
            "f9093662a2e7ae0503f637088bc96f77a1a70c83",
            new EmbeddedFile("Gemma4-26B-A4B-QAT-Uncensored-HauhauCS-Balanced-Q4_K_M.gguf", 16_796_015_520, "3c13133469e431312fffb8b1d9c85ae42199e6bb5746ea1da84e8ddf2097d73c"),
            new EmbeddedFile("mmproj-Gemma4-26B-A4B-QAT-Uncensored-HauhauCS-Balanced-BF16.gguf", 1_194_827_776, "b5346e5bfd906f5e16878c2d0b8243e948ca7410fa28ea35be9b0c54a0ac10b7"),
            HauhauBalanced,
            new EmbeddedFile("mtp-gemma-4-26B-A4B-it.gguf", 251_937_728, "62bd3af7f66c9308de9a5454233852f8c7324c93767e8dfb824ed45b9179864a")),
        new(
            "gemma-4-26b-a4b-uncensored",
            "Gemma 4 26B A4B Uncensored",
            "Q4_K_P",
            "HauhauCS/Gemma4-26B-A4B-Uncensored-HauhauCS-Balanced",
            "96c11c22b1128c3c8c655b21557b409f307c557f",
            new EmbeddedFile("Gemma4-26B-A4B-Uncensored-HauhauCS-Balanced-Q4_K_P.gguf", 16_916_915_296, "295121f61edeedaa8604bcaf3171831981c546c3a10a210cea87dc992eb429ae"),
            Hauhau26bMmproj,
            Gemma4),
        new(
            "gemma-4-26b-a4b-uncensored-q5",
            "Gemma 4 26B A4B Uncensored",
            "Q5_K_P",
            "HauhauCS/Gemma4-26B-A4B-Uncensored-HauhauCS-Balanced",
            "96c11c22b1128c3c8c655b21557b409f307c557f",
            new EmbeddedFile("Gemma4-26B-A4B-Uncensored-HauhauCS-Balanced-Q5_K_P.gguf", 19_317_146_976, "11bbd396b30cccfcf16eb76383ad9e743d68b3983742daa4b2b2731f77cafa15"),
            Hauhau26bMmproj,
            Gemma4),
        new(
            "gemma-4-26b-a4b-uncensored-q6",
            "Gemma 4 26B A4B Uncensored",
            "Q6_K_P",
            "HauhauCS/Gemma4-26B-A4B-Uncensored-HauhauCS-Balanced",
            "96c11c22b1128c3c8c655b21557b409f307c557f",
            new EmbeddedFile("Gemma4-26B-A4B-Uncensored-HauhauCS-Balanced-Q6_K_P.gguf", 22_758_955_104, "e468cbb7ec1a58dd341475eccb9614d3bc2423aadca40ac6caf887bcfc637349"),
            Hauhau26bMmproj,
            Gemma4),
        new(
            "gemma-4-31b",
            "Gemma 4 31B",
            "UD-Q4_K_XL",
            "unsloth/gemma-4-31B-it-GGUF",
            "c1ac76e99d5513b141e8adde7288b85c3f9c32ec",
            new EmbeddedFile("gemma-4-31B-it-UD-Q4_K_XL.gguf", 18_822_970_304, "9e92cb6236044c6a9870af406029c74a76e0571c157a6f95df724dcc8c7a1575"),
            Gemma31bMmproj,
            Gemma4,
            Gemma31bDrafter),
        new(
            "gemma-4-31b-q5",
            "Gemma 4 31B",
            "UD-Q5_K_XL",
            "unsloth/gemma-4-31B-it-GGUF",
            "c1ac76e99d5513b141e8adde7288b85c3f9c32ec",
            new EmbeddedFile("gemma-4-31B-it-UD-Q5_K_XL.gguf", 21_890_429_888, "abd37a1692ed4a6e7387a886cce871a8d4205359ce098de43f144146cf6455a3"),
            Gemma31bMmproj,
            Gemma4,
            Gemma31bDrafter),
        new(
            "gemma-4-31b-qat",
            "Gemma 4 31B QAT",
            "UD-Q4_K_XL",
            "unsloth/gemma-4-31B-it-qat-GGUF",
            "43cc1aeb31adf47ec06a854507ce552cd9862e6f",
            new EmbeddedFile("gemma-4-31B-it-qat-UD-Q4_K_XL.gguf", 17_287_670_048, "00b5a7c497f0c8934033088c10a7fa9a4c015e46ee6d89e9c6890650ba5d0e71"),
            new EmbeddedFile("mmproj-F16.gguf", 1_198_957_024, "4775e1bf6ef6f8df94caed2d672c74432f0722565e5225a170c72949ebe4cf23"),
            Gemma4,
            new EmbeddedFile("mtp-gemma-4-31B-it.gguf", 279_955_968, "3a5e99fd8d0b23afb1fccd1ee0c9ebd1f571d00399c2dae2292d217feeec0f6b")),
        new(
            "gemma-4-31b-qat-uncensored",
            "Gemma 4 31B QAT Uncensored",
            "Q4_K_M",
            "HauhauCS/Gemma4-31B-QAT-Uncensored-HauhauCS-Balanced-MTP",
            "9654466e82d83f5ebfe1518a369bc5900873abb1",
            new EmbeddedFile("Gemma4-31B-QAT-Uncensored-HauhauCS-Balanced-Q4_K_M.gguf", 18_687_062_176, "71667f9e601a4b914a98425c59150b731f6e15d260d661dbd1f1ee07469fc7db"),
            new EmbeddedFile("mmproj-Gemma4-31B-QAT-Uncensored-HauhauCS-Balanced-BF16.gguf", 1_200_726_016, "7bef0d0fb3e85fc2941ec5f1c375febf3742645f158132a43ced557093aea841"),
            HauhauBalanced,
            new EmbeddedFile("mtp-gemma-4-31B-it.gguf", 279_954_368, "b5c4e583fc5982439080114bbc1b7edaec361f9d4c9193d6bed606a3de401b62")),
        new(
            "gemma-4-e2b",
            "Gemma 4 E2B",
            "UD-Q4_K_XL",
            "unsloth/gemma-4-E2B-it-GGUF",
            "0314792d7f1f7e229411f620751375812bb9faf2",
            new EmbeddedFile("gemma-4-E2B-it-UD-Q4_K_XL.gguf", 3_184_496_736, "b52f438017efaec5debf1c0d8be690571e212a07c312f1102bbce927258cfc32"),
            new EmbeddedFile("mmproj-F16.gguf", 985_654_080, "140be8d7849741f88c50757d529b84373ee8e27052cc2236855b537f4a8215fa"),
            Gemma4,
            new EmbeddedFile("mtp-gemma-4-E2B-it.gguf", 97_817_664, "9eba819938efccfd6044f8af84e3bbfddc639a2bcf32ebc36420e6a649191919")),
        new(
            "gemma-4-e2b-uncensored",
            "Gemma 4 E2B Uncensored",
            "Q4_K_P",
            "HauhauCS/Gemma-4-E2B-Uncensored-HauhauCS-Aggressive",
            "da8593c3e407afcd3e7da94ff2d69d77e2a28a48",
            new EmbeddedFile("Gemma-4-E2B-Uncensored-HauhauCS-Aggressive-Q4_K_P.gguf", 3_450_277_824, "aa866c1e514468f3d0f33971679d63c11b7c9c47acddd1cc5785fc467e52c21d"),
            new EmbeddedFile("mmproj-Gemma-4-E2B-Uncensored-HauhauCS-Aggressive-f16.gguf", 985_570_240, "628b7e999f89beef70b32396ae84f59c096e867747d7901f0134064ff672e290"),
            Gemma4),
        new(
            "gemma-4-e4b",
            "Gemma 4 E4B",
            "UD-Q4_K_XL",
            "unsloth/gemma-4-E4B-it-GGUF",
            "bfc15c382204943c3a8fff0c750b94ae2364d7a3",
            new EmbeddedFile("gemma-4-E4B-it-UD-Q4_K_XL.gguf", 5_126_306_944, "3cf61de12daa015ee0f7b68e7b7c541405bf220e1e942bad8b47cab827d7df80"),
            new EmbeddedFile("mmproj-F16.gguf", 990_372_672, "ddf46c21d7078e95338cfc22306b19b276a29a5ad089023449dd54d4b6170a51"),
            Gemma4,
            new EmbeddedFile("mtp-gemma-4-E4B-it.gguf", 98_653_248, "b6a723115efa510d3b3215db1e26790dae84cd08c2134a764f3d194f1f0c3376")),
        new(
            "gemma-4-e4b-qat",
            "Gemma 4 E4B QAT",
            "UD-Q4_K_XL",
            "unsloth/gemma-4-E4B-it-qat-GGUF",
            "8c5a9e4fd5482e2be20fe0bf013b4c262a8f4265",
            new EmbeddedFile("gemma-4-E4B-it-qat-UD-Q4_K_XL.gguf", 4_215_695_776, "df0fd4ee07072c607c29a0a1cb4f98918426cca12f45a2776bdd6ee6d09a4de3"),
            new EmbeddedFile("mmproj-F16.gguf", 990_372_672, "6a255159ee4b01b304f633a57f017dd7d5a69d30fff52abb2614bf0813cef034"),
            Gemma4,
            new EmbeddedFile("mtp-gemma-4-E4B-it.gguf", 59_678_016, "423074e537504b4f9ec5eafed5c639fac82c96631626efccacdd3c4039b20605")),
        new(
            "gemma-4-e4b-uncensored",
            "Gemma 4 E4B Uncensored",
            "Q4_K_P",
            "HauhauCS/Gemma-4-E4B-Uncensored-HauhauCS-Aggressive",
            "45b6a334b4bcd1d7f37179df58b3b1d66a184e5d",
            new EmbeddedFile("Gemma-4-E4B-Uncensored-HauhauCS-Aggressive-Q4_K_P.gguf", 5_369_246_688, "05146429870f4ec4c16882f44bec29c51e4797463ad7080044a5c748cabb2486"),
            new EmbeddedFile("mmproj-Gemma-4-E4B-Uncensored-HauhauCS-Aggressive-f16.gguf", 990_288_832, "debad39ab9c1152ab67695a674fb35e8375b2320c57bfd5075835d3ccb16c7db"),
            Gemma4),
        new(
            "muse-glimmer-30b",
            "Muse Glimmer 30B",
            "UD-Q4_K_XL",
            "unsloth/Muse-Glimmer-30B-GGUF",
            "faa5b025c584459c13febfa5c59883516710ae39",
            new EmbeddedFile("Muse-Glimmer-30B-UD-Q4_K_XL.gguf", 15_878_222_368, "82bece304887a313ece08400bc030f6066c7bff5b906b0cd40308ec8a409fd38"),
            MuseMmproj,
            Muse,
            MuseDrafter,
            Draft: DraftKind.DFlash),
        new(
            "muse-glimmer-30b-q5",
            "Muse Glimmer 30B",
            "UD-Q5_K_XL",
            "unsloth/Muse-Glimmer-30B-GGUF",
            "faa5b025c584459c13febfa5c59883516710ae39",
            new EmbeddedFile("Muse-Glimmer-30B-UD-Q5_K_XL.gguf", 21_789_618_976, "97a66c4b41d9e778af7cdfa43508e08dbf765fb5049b740c69ad815e5191c637"),
            MuseMmproj,
            Muse,
            MuseDrafter,
            Draft: DraftKind.DFlash),
        new(
            "qwen3.6-35b-a3b",
            "Qwen3.6 35B A3B",
            "UD-Q4_K_XL",
            "unsloth/Qwen3.6-35B-A3B-GGUF",
            "a483e9e6cbd595906af30beda3187c2663a1118c",
            new EmbeddedFile("Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf", 22_360_456_160, "707a55a8a4397ecde44de0c499d3e68c1ad1d240d1da65826b4949d1043f4450"),
            Qwen36Mmproj,
            Qwen),
        new(
            "qwen3.6-35b-a3b-q5",
            "Qwen3.6 35B A3B",
            "UD-Q5_K_XL",
            "unsloth/Qwen3.6-35B-A3B-GGUF",
            "a483e9e6cbd595906af30beda3187c2663a1118c",
            new EmbeddedFile("Qwen3.6-35B-A3B-UD-Q5_K_XL.gguf", 26_592_508_896, "25233af7642e3a91bd52cc4aeefdbd4a117479088e06cf1aea5b6bedb443c506"),
            Qwen36Mmproj,
            Qwen),
        new(
            "qwen3.6-35b-a3b-uncensored",
            "Qwen3.6 35B A3B Uncensored",
            "Q4_K_P",
            "HauhauCS/Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive",
            "f12a584fecbeb5f20001130d8ecd66c9327ae685",
            new EmbeddedFile("Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive-Q4_K_P.gguf", 23_424_536_704, "8d344a4336d8ea7da0cbfc12792d1471e568be7abe8930c52260698bfd01d731"),
            new EmbeddedFile("mmproj-Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive-f16.gguf", 899_283_072, "c8e702344a81f8c226a914aa980ed6e1f604bce9374f1fed8e65c896908af414"),
            Qwen),
        new(
            "qwen3.8-27b",
            "Qwen3.8 27B",
            "UD-Q4_K_XL",
            "unsloth/Qwen3.8-27B-GGUF",
            "4ca720788d1e01f1bff70c033e0d0028fd02e502",
            new EmbeddedFile("Qwen3.8-27B-UD-Q4_K_XL.gguf", 17_559_178_144, "3f227079003add2511437e5b1e94812e363385225bf6a9b47b0054a72bc8b01e"),
            Qwen38Mmproj,
            Qwen,
            MtpHead: true),
        new(
            "qwen3.8-27b-q5",
            "Qwen3.8 27B",
            "UD-Q5_K_XL",
            "unsloth/Qwen3.8-27B-GGUF",
            "4ca720788d1e01f1bff70c033e0d0028fd02e502",
            new EmbeddedFile("Qwen3.8-27B-UD-Q5_K_XL.gguf", 20_876_938_144, "8601193d3d5760c37fb8ce1b43afebc69df5fb24e1fbc5a547c32e2200305276"),
            Qwen38Mmproj,
            Qwen,
            MtpHead: true),
        new(
            "qwen3.8-27b-q6",
            "Qwen3.8 27B",
            "UD-Q6_K_XL",
            "unsloth/Qwen3.8-27B-GGUF",
            "4ca720788d1e01f1bff70c033e0d0028fd02e502",
            new EmbeddedFile("Qwen3.8-27B-UD-Q6_K_XL.gguf", 25_299_061_664, "701d8fa9ed214ab21bfc130cd2a7df19ca89bbef7713e2dfb19f3c63696aa917"),
            Qwen38Mmproj,
            Qwen,
            MtpHead: true),
        new(
            "qwen3.8-27b-nvfp4-very-low",
            "Qwen3.8 27B NVFP4",
            "VERY-LOW",
            "esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF",
            "bcd7a7d3e251d4ec0fd15c72584b5eb9e0981383",
            new EmbeddedFile("Qwen3.8-27B-NVFP4-MTP-VERY-LOW.gguf", 14_862_277_984, "74ea17ea05e0e0241af8d5b29cdea38b3f4509f66d9b96c1ab05f0e1f0e537d9"),
            Nvfp4Mmproj,
            Qwen,
            MtpHead: true),
        new(
            "qwen3.8-27b-nvfp4-compact-low",
            "Qwen3.8 27B NVFP4",
            "COMPACT-LOW",
            "esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF",
            "bcd7a7d3e251d4ec0fd15c72584b5eb9e0981383",
            new EmbeddedFile("Qwen3.8-27B-NVFP4-MTP-COMPACT-LOW.gguf", 15_160_261_920, "ac0ef9c5eceb5a5dc9b266eacc9372508158713c6c35618cf735675451fdd3ac"),
            Nvfp4Mmproj,
            Qwen,
            MtpHead: true),
        new(
            "qwen3.8-27b-nvfp4-low",
            "Qwen3.8 27B NVFP4",
            "LOW",
            "esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF",
            "bcd7a7d3e251d4ec0fd15c72584b5eb9e0981383",
            new EmbeddedFile("Qwen3.8-27B-NVFP4-MTP-LOW.gguf", 15_534_575_072, "ce66a629d4a3516bba27ca91de29372f086f90f72ddb92fe298de67b8bb88bbc"),
            Nvfp4Mmproj,
            Qwen,
            MtpHead: true),
        new(
            "qwen3.8-27b-nvfp4-medium",
            "Qwen3.8 27B NVFP4",
            "MEDIUM",
            "esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF",
            "bcd7a7d3e251d4ec0fd15c72584b5eb9e0981383",
            new EmbeddedFile("Qwen3.8-27B-NVFP4-MTP-MEDIUM.gguf", 16_378_863_040, "f0b4c538c75037f026bde3b650f0ca639d382c128a4572769dce1183db86253a"),
            Nvfp4Mmproj,
            Qwen,
            MtpHead: true),
        new(
            "qwen3.8-27b-nvfp4-mid-high",
            "Qwen3.8 27B NVFP4",
            "MID-HIGH",
            "esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF",
            "bcd7a7d3e251d4ec0fd15c72584b5eb9e0981383",
            new EmbeddedFile("Qwen3.8-27B-NVFP4-MTP-MID-HIGH.gguf", 16_912_387_392, "79b032f7a118fb34f1445c4d7ae50bc7b304c74161035c3df8bd5526c12899b9"),
            Nvfp4Mmproj,
            Qwen,
            MtpHead: true),
        new(
            "qwen3.8-27b-nvfp4-high",
            "Qwen3.8 27B NVFP4",
            "HIGH",
            "esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF",
            "bcd7a7d3e251d4ec0fd15c72584b5eb9e0981383",
            new EmbeddedFile("Qwen3.8-27B-NVFP4-MTP-HIGH.gguf", 17_570_799_040, "d57008707b0558bde05ce61d7402e4e668ffd45a4c97d03d2ad97db73f98d403"),
            Nvfp4Mmproj,
            Qwen,
            MtpHead: true),
        new(
            "qwen3.8-27b-nvfp4-very-high",
            "Qwen3.8 27B NVFP4",
            "VERY-HIGH",
            "esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF",
            "bcd7a7d3e251d4ec0fd15c72584b5eb9e0981383",
            new EmbeddedFile("Qwen3.8-27B-NVFP4-MTP-VERY-HIGH.gguf", 19_694_390_752, "3e52d6280ee650520a2d901002121c11cf9d23ac75f23c52a362bf285d561d81"),
            Nvfp4Mmproj,
            Qwen,
            MtpHead: true),
        new(
            "qwen3.8-27b-nvfp4-highest",
            "Qwen3.8 27B NVFP4",
            "HIGHEST",
            "esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF",
            "bcd7a7d3e251d4ec0fd15c72584b5eb9e0981383",
            new EmbeddedFile("Qwen3.8-27B-NVFP4-MTP-HIGHEST.gguf", 23_185_001_824, "6a202c2faf67f79d4c8c61ec940da7a62bd59a87608508fe8048131630cc4ba6"),
            Nvfp4Mmproj,
            Qwen,
            MtpHead: true),
        new(
            "qwen3.8-27b-uncensored",
            "Qwen3.8 27B Uncensored",
            "Q4_K_P",
            "HauhauCS/Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-MTP-GGUF",
            "993a5971fda8f30dd1b7eb2654792ba4415c7460",
            new EmbeddedFile("Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-Q4_K_P.gguf", 17_923_393_664, "ba36dc3c2b2ff5e0aa5d71092a8894546996a6a119ae391803dda07cdc08516d"),
            Hauhau38Mmproj,
            Qwen,
            MtpHead: true),
        new(
            "qwen3.8-27b-uncensored-q5",
            "Qwen3.8 27B Uncensored",
            "Q5_K_P",
            "HauhauCS/Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-MTP-GGUF",
            "993a5971fda8f30dd1b7eb2654792ba4415c7460",
            new EmbeddedFile("Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-Q5_K_P.gguf", 20_218_177_664, "a21e22af885bd2f7c430a72b550a78f5445966e6a72d3143b0950baa0bd6d411"),
            Hauhau38Mmproj,
            Qwen,
            MtpHead: true),
    ];

    /// <summary>Where every file is fetched from: <c>https://huggingface.co/&lt;repo&gt;/resolve/&lt;commit&gt;/&lt;file&gt;</c>.</summary>
    public const string HuggingFaceBase = "https://huggingface.co/";

    /// <summary>The model whose id is <paramref name="id"/> (any case, trimmed), or null.</summary>
    public static EmbeddedModel? Find(string? id, IReadOnlyList<EmbeddedModel>? models = null)
    {
        string wanted = (id ?? "").Trim();
        foreach (var model in models ?? Models)
        {
            if (string.Equals(model.Id, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }
        }

        return null;
    }

    /// <summary>The pinned download URL of <paramref name="file"/> in <paramref name="model"/>'s repository.</summary>
    public static Uri Url(EmbeddedModel model, EmbeddedFile file)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(file);
        return new Uri($"{HuggingFaceBase}{model.Repository}/resolve/{model.Revision}/{Uri.EscapeDataString(file.Name)}");
    }

    /// <summary>The folder <paramref name="model"/>'s files live in: <c>&lt;embeddedModelsDirectory&gt;/&lt;id&gt;</c>.</summary>
    public static string Folder(string embeddedModelsDirectory, EmbeddedModel model) => Path.Combine(embeddedModelsDirectory, model.Id);

    /// <summary>What an install downloads: the weights, the vision projector and the drafter (MTP or DFlash) when there is one.</summary>
    public static long TotalBytes(EmbeddedModel model) => model.Model.Bytes + model.Mmproj.Bytes + (model.Drafter?.Bytes ?? 0);

    /// <summary>The weights as a <see cref="ModelStore"/> spec: pinned, resumable, GGUF.</summary>
    public static ModelSpec WeightsSpec(string embeddedModelsDirectory, EmbeddedModel model) => Spec(embeddedModelsDirectory, model, model.Model, model.Display);

    /// <summary>The vision projector as a <see cref="ModelStore"/> spec: pinned, resumable, GGUF.</summary>
    public static ModelSpec MmprojSpec(string embeddedModelsDirectory, EmbeddedModel model) => Spec(embeddedModelsDirectory, model, model.Mmproj, model.Display + " vision");

    /// <summary>The drafter as a <see cref="ModelStore"/> spec, named for its kind (MTP or DFlash), or null for a model without one.</summary>
    public static ModelSpec? DrafterSpec(string embeddedModelsDirectory, EmbeddedModel model) =>
        model.Drafter is { } drafter
            ? Spec(embeddedModelsDirectory, model, drafter, model.Display + " " + EmbeddedLlmText.DraftName(model.Draft))
            : null;

    private static ModelSpec Spec(string embeddedModelsDirectory, EmbeddedModel model, EmbeddedFile file, string display) =>
        new(display, Path.Combine(Folder(embeddedModelsDirectory, model), file.Name), Url(model, file), file.Bytes, ModelFormat.Gguf, file.Sha256, Resumable: true);
}
