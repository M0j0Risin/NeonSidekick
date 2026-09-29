using NeonSidekick.Speech;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>One file of a <see cref="EmbeddedModel"/>: its name in the repository, its exact size and its SHA-256 (lowercase hex).</summary>
public sealed record EmbeddedFile(string Name, long Bytes, string Sha256);

/// <summary>The sampling a model's card recommends; passed to <c>llama-server</c> as its defaults, which a request's own values still override.</summary>
public sealed record EmbeddedSampling(double Temperature, double TopP, int TopK);

/// <summary>
/// One model the app can download and run itself: its catalog <paramref name="Id"/> (what <c>LLM model</c> saves and
/// <c>llama-server</c> is told to call itself), a <paramref name="Display"/> name, the quantisation, the Hugging Face
/// repository pinned to a commit, the weights, the vision projector (<c>mmproj</c>: every model has one — a text-only
/// model was allowed for a few hours on 2026-09-29, until Qwen3.8 left the catalog) and the recommended sampling.
/// </summary>
public sealed record EmbeddedModel(string Id, string Display, string Quant, string Repository, string Revision, EmbeddedFile Model, EmbeddedFile Mmproj, EmbeddedSampling Sampling);

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
/// 12B's four builds share the one repository and projector; each keeps a copy in its own folder.</para>
///
/// <para>Sampling, from each card: Google's Gemma 4 temperature 1.0, top-p 0.95, top-k 64 (HauhauCS's E2B/E4B the same);
/// HauhauCS's 12B Balanced 0.6, 0.9, 64 (its min-p 0.05 is llama.cpp's default). Q4_K_P is
/// HauhauCS's own per-model quantisation profile, not a llama.cpp type name, but it uses standard GGUF tensor types and
/// loads in any llama.cpp build.</para>
/// </summary>
public static class EmbeddedModelCatalog
{
    private static readonly EmbeddedSampling Gemma4 = new(1.0, 0.95, 64);
    private static readonly EmbeddedSampling HauhauBalanced = new(0.6, 0.9, 64);

    /// <summary>The projector of <c>unsloth/gemma-4-12b-it-GGUF</c> at the pinned commit, which its four builds share.</summary>
    private static readonly EmbeddedFile Gemma12bMmproj = new("mmproj-F16.gguf", 175_115_840, "91f086971e56d7a7d8d39e271873fccdb49541bd259d6e02c401a4f1cb7a219e");

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
            Gemma4),
        new(
            "gemma-4-12b-q5",
            "Gemma 4 12B",
            "UD-Q5_K_XL",
            "unsloth/gemma-4-12b-it-GGUF",
            "fc034cfff751157913579611efad8462ac1be606",
            new EmbeddedFile("gemma-4-12b-it-UD-Q5_K_XL.gguf", 8_606_021_440, "d9d55dbb17c73cf274cc1e701a1564a5b35ec03ffde474e7c6c93c3562d8d825"),
            Gemma12bMmproj,
            Gemma4),
        new(
            "gemma-4-12b-q6",
            "Gemma 4 12B",
            "UD-Q6_K_XL",
            "unsloth/gemma-4-12b-it-GGUF",
            "fc034cfff751157913579611efad8462ac1be606",
            new EmbeddedFile("gemma-4-12b-it-UD-Q6_K_XL.gguf", 10_685_012_800, "70d04059c74be85c5e709921f05acac412b8b8f24f3ee7dd07e91ddc5f4d4de8"),
            Gemma12bMmproj,
            Gemma4),
        new(
            "gemma-4-12b-bf16",
            "Gemma 4 12B",
            "BF16",
            "unsloth/gemma-4-12b-it-GGUF",
            "fc034cfff751157913579611efad8462ac1be606",
            new EmbeddedFile("gemma-4-12b-it-BF16.gguf", 23_832_066_656, "5a5eefea73350705c6753105b725a51301d653e8d6646173db29a7e8da8e6efd"),
            Gemma12bMmproj,
            Gemma4),
        new(
            "gemma-4-12b-qat",
            "Gemma 4 12B QAT",
            "UD-Q4_K_XL",
            "unsloth/gemma-4-12B-it-qat-GGUF",
            "980b060c40a8539ac159e0501a3e0f66a6365af3",
            new EmbeddedFile("gemma-4-12B-it-qat-UD-Q4_K_XL.gguf", 6_716_356_800, "90fd44e29e0d7cffeb0fd00dc73cfdab9ed0b0e95306ecf7821ea634c940c370"),
            new EmbeddedFile("mmproj-F16.gguf", 175_115_840, "ecc4e93128da8363b7dbf2193eab98cf1142353f52ceaa0c95c0872997aaadd3"),
            Gemma4),
        new(
            "gemma-4-12b-qat-uncensored",
            "Gemma 4 12B QAT Uncensored",
            "Q4_K_M",
            "HauhauCS/Gemma4-12B-QAT-Uncensored-HauhauCS-Balanced",
            "ae8045ac2bd216293ca49a3065da2c942dde4b68",
            new EmbeddedFile("Gemma4-12B-QAT-Uncensored-HauhauCS-Balanced-Q4_K_M.gguf", 7_381_381_760, "59656d7494d6376ca97e9e20b64ea2e16cd97f12ec6d47bfccba91cb785b5134"),
            new EmbeddedFile("mmproj-Gemma4-12B-QAT-Uncensored-HauhauCS-Balanced-BF16.gguf", 175_115_264, "b59e815479b7e5f0665bd29e6784c104a368092bcbc63120148c606f9276ab8e"),
            HauhauBalanced),
        new(
            "gemma-4-e2b",
            "Gemma 4 E2B",
            "UD-Q4_K_XL",
            "unsloth/gemma-4-E2B-it-GGUF",
            "0314792d7f1f7e229411f620751375812bb9faf2",
            new EmbeddedFile("gemma-4-E2B-it-UD-Q4_K_XL.gguf", 3_184_496_736, "b52f438017efaec5debf1c0d8be690571e212a07c312f1102bbce927258cfc32"),
            new EmbeddedFile("mmproj-F16.gguf", 985_654_080, "140be8d7849741f88c50757d529b84373ee8e27052cc2236855b537f4a8215fa"),
            Gemma4),
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
            Gemma4),
        new(
            "gemma-4-e4b-qat",
            "Gemma 4 E4B QAT",
            "UD-Q4_K_XL",
            "unsloth/gemma-4-E4B-it-qat-GGUF",
            "8c5a9e4fd5482e2be20fe0bf013b4c262a8f4265",
            new EmbeddedFile("gemma-4-E4B-it-qat-UD-Q4_K_XL.gguf", 4_215_695_776, "df0fd4ee07072c607c29a0a1cb4f98918426cca12f45a2776bdd6ee6d09a4de3"),
            new EmbeddedFile("mmproj-F16.gguf", 990_372_672, "6a255159ee4b01b304f633a57f017dd7d5a69d30fff52abb2614bf0813cef034"),
            Gemma4),
        new(
            "gemma-4-e4b-uncensored",
            "Gemma 4 E4B Uncensored",
            "Q4_K_P",
            "HauhauCS/Gemma-4-E4B-Uncensored-HauhauCS-Aggressive",
            "45b6a334b4bcd1d7f37179df58b3b1d66a184e5d",
            new EmbeddedFile("Gemma-4-E4B-Uncensored-HauhauCS-Aggressive-Q4_K_P.gguf", 5_369_246_688, "05146429870f4ec4c16882f44bec29c51e4797463ad7080044a5c748cabb2486"),
            new EmbeddedFile("mmproj-Gemma-4-E4B-Uncensored-HauhauCS-Aggressive-f16.gguf", 990_288_832, "debad39ab9c1152ab67695a674fb35e8375b2320c57bfd5075835d3ccb16c7db"),
            Gemma4),
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

    /// <summary>What an install downloads: the weights and the vision projector.</summary>
    public static long TotalBytes(EmbeddedModel model) => model.Model.Bytes + model.Mmproj.Bytes;

    /// <summary>The weights as a <see cref="ModelStore"/> spec: pinned, resumable, GGUF.</summary>
    public static ModelSpec WeightsSpec(string embeddedModelsDirectory, EmbeddedModel model) => Spec(embeddedModelsDirectory, model, model.Model, model.Display);

    /// <summary>The vision projector as a <see cref="ModelStore"/> spec: pinned, resumable, GGUF.</summary>
    public static ModelSpec MmprojSpec(string embeddedModelsDirectory, EmbeddedModel model) => Spec(embeddedModelsDirectory, model, model.Mmproj, model.Display + " vision");

    private static ModelSpec Spec(string embeddedModelsDirectory, EmbeddedModel model, EmbeddedFile file, string display) =>
        new(display, Path.Combine(Folder(embeddedModelsDirectory, model), file.Name), Url(model, file), file.Bytes, ModelFormat.Gguf, file.Sha256, Resumable: true);
}
