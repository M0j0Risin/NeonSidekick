using NeonSidekick.Speech;

namespace NeonSidekick.LocalLlm;

/// <summary>One file of a <see cref="LocalModel"/>: its name in the repository, its exact size and its SHA-256 (lowercase hex).</summary>
public sealed record LocalFile(string Name, long Bytes, string Sha256);

/// <summary>The sampling a model's card recommends; passed to <c>llama-server</c> as its defaults, which a request's own values still override.</summary>
public sealed record LocalSampling(double Temperature, double TopP, int TopK);

/// <summary>
/// One model the app can download and run itself: its catalog <paramref name="Id"/> (what <c>LLM model</c> saves and
/// <c>llama-server</c> is told to call itself), a <paramref name="Display"/> name, the quantisation, the Hugging Face
/// repository pinned to a commit, the weights, the vision projector (<c>mmproj</c>) and the recommended sampling.
/// </summary>
public sealed record LocalModel(string Id, string Display, string Quant, string Repository, string Revision, LocalFile Model, LocalFile Mmproj, LocalSampling Sampling);

/// <summary>
/// The models the local server offers (2026-09-29, the user's four: Gemma 4 E4B and E2B, Unsloth's instruct builds and
/// HauhauCS's uncensored fine-tunes). Each is pinned to a Hugging Face commit, not <c>main</c>, with the exact size and
/// SHA-256 the API published for it at that commit, so an upstream re-upload can never turn an install into a
/// checksum failure — nor slip a different file past one. To add or update a model:
/// <c>GET https://huggingface.co/api/models/&lt;repo&gt;</c> gives the commit (<c>sha</c>) and whether it is
/// <c>gated</c> (it must not be: the download carries no token); <c>GET …/tree/&lt;commit&gt;</c> gives each file's
/// <c>size</c> and <c>lfs.oid</c>.
///
/// <para>Every file lives under <c>&lt;models&gt;/llm/&lt;id&gt;/</c>: the Unsloth repositories both name their
/// projector <c>mmproj-F16.gguf</c>, different files under one name. The projector is the F16 one, the only
/// precision HauhauCS publishes and half the size of F32 at no visible cost.</para>
///
/// <para>Sampling: Google's Gemma 4 card and HauhauCS's both recommend temperature 1.0, top-p 0.95, top-k 64.
/// Q4_K_P is HauhauCS's own per-model quantisation profile, not a llama.cpp type name, but it uses standard GGUF
/// tensor types and loads in any llama.cpp build.</para>
/// </summary>
public static class LocalModelCatalog
{
    private static readonly LocalSampling Gemma4 = new(1.0, 0.95, 64);

    public static readonly IReadOnlyList<LocalModel> Models =
    [
        new(
            "gemma-4-e4b-qat",
            "Gemma 4 E4B QAT",
            "UD-Q4_K_XL",
            "unsloth/gemma-4-E4B-it-qat-GGUF",
            "8c5a9e4fd5482e2be20fe0bf013b4c262a8f4265",
            new LocalFile("gemma-4-E4B-it-qat-UD-Q4_K_XL.gguf", 4_215_695_776, "df0fd4ee07072c607c29a0a1cb4f98918426cca12f45a2776bdd6ee6d09a4de3"),
            new LocalFile("mmproj-F16.gguf", 990_372_672, "6a255159ee4b01b304f633a57f017dd7d5a69d30fff52abb2614bf0813cef034"),
            Gemma4),
        new(
            "gemma-4-e2b",
            "Gemma 4 E2B",
            "UD-Q4_K_XL",
            "unsloth/gemma-4-E2B-it-GGUF",
            "0314792d7f1f7e229411f620751375812bb9faf2",
            new LocalFile("gemma-4-E2B-it-UD-Q4_K_XL.gguf", 3_184_496_736, "b52f438017efaec5debf1c0d8be690571e212a07c312f1102bbce927258cfc32"),
            new LocalFile("mmproj-F16.gguf", 985_654_080, "140be8d7849741f88c50757d529b84373ee8e27052cc2236855b537f4a8215fa"),
            Gemma4),
        new(
            "gemma-4-e4b-uncensored",
            "Gemma 4 E4B Uncensored",
            "Q4_K_P",
            "HauhauCS/Gemma-4-E4B-Uncensored-HauhauCS-Aggressive",
            "45b6a334b4bcd1d7f37179df58b3b1d66a184e5d",
            new LocalFile("Gemma-4-E4B-Uncensored-HauhauCS-Aggressive-Q4_K_P.gguf", 5_369_246_688, "05146429870f4ec4c16882f44bec29c51e4797463ad7080044a5c748cabb2486"),
            new LocalFile("mmproj-Gemma-4-E4B-Uncensored-HauhauCS-Aggressive-f16.gguf", 990_288_832, "debad39ab9c1152ab67695a674fb35e8375b2320c57bfd5075835d3ccb16c7db"),
            Gemma4),
        new(
            "gemma-4-e2b-uncensored",
            "Gemma 4 E2B Uncensored",
            "Q4_K_P",
            "HauhauCS/Gemma-4-E2B-Uncensored-HauhauCS-Aggressive",
            "da8593c3e407afcd3e7da94ff2d69d77e2a28a48",
            new LocalFile("Gemma-4-E2B-Uncensored-HauhauCS-Aggressive-Q4_K_P.gguf", 3_450_277_824, "aa866c1e514468f3d0f33971679d63c11b7c9c47acddd1cc5785fc467e52c21d"),
            new LocalFile("mmproj-Gemma-4-E2B-Uncensored-HauhauCS-Aggressive-f16.gguf", 985_570_240, "628b7e999f89beef70b32396ae84f59c096e867747d7901f0134064ff672e290"),
            Gemma4),
    ];

    /// <summary>Where every file is fetched from: <c>https://huggingface.co/&lt;repo&gt;/resolve/&lt;commit&gt;/&lt;file&gt;</c>.</summary>
    public const string HuggingFaceBase = "https://huggingface.co/";

    /// <summary>The model whose id is <paramref name="id"/> (any case, trimmed), or null.</summary>
    public static LocalModel? Find(string? id, IReadOnlyList<LocalModel>? models = null)
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
    public static Uri Url(LocalModel model, LocalFile file)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(file);
        return new Uri($"{HuggingFaceBase}{model.Repository}/resolve/{model.Revision}/{Uri.EscapeDataString(file.Name)}");
    }

    /// <summary>The folder <paramref name="model"/>'s files live in: <c>&lt;localModelsDirectory&gt;/&lt;id&gt;</c>.</summary>
    public static string Folder(string localModelsDirectory, LocalModel model) => Path.Combine(localModelsDirectory, model.Id);

    /// <summary>Both files' bytes: what an install downloads.</summary>
    public static long TotalBytes(LocalModel model) => model.Model.Bytes + model.Mmproj.Bytes;

    /// <summary>The weights as a <see cref="ModelStore"/> spec: pinned, resumable, GGUF.</summary>
    public static ModelSpec WeightsSpec(string localModelsDirectory, LocalModel model) => Spec(localModelsDirectory, model, model.Model, model.Display);

    /// <summary>The vision projector as a <see cref="ModelStore"/> spec: pinned, resumable, GGUF.</summary>
    public static ModelSpec MmprojSpec(string localModelsDirectory, LocalModel model) => Spec(localModelsDirectory, model, model.Mmproj, model.Display + " vision");

    private static ModelSpec Spec(string localModelsDirectory, LocalModel model, LocalFile file, string display) =>
        new(display, Path.Combine(Folder(localModelsDirectory, model), file.Name), Url(model, file), file.Bytes, ModelFormat.Gguf, file.Sha256, Resumable: true);
}
