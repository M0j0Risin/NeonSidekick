namespace NeonSidekick.Comfy;

/// <summary>
/// The model family a ComfyUI workflow's checkpoint belongs to (2026-09-24, the user's ask: "send descriptions to the
/// model and have it translate that into prompts for Pony Diffusion XL or other model types"). The family picks the
/// prompt dialect the tool description teaches (<see cref="ComfyFamilies.StyleGuide"/>) and the defaults a template's
/// numeric placeholders fall back to (<see cref="ComfyFamilies.Defaults"/>). Named in a workflow's sidecar
/// (<c>family: pony</c>), else guessed from its file name (<see cref="ComfyFamilies.Guess"/>).
/// </summary>
public enum ComfyFamily
{
    Other,
    Pony,
    Sdxl,
    Flux,
    Sd15,

    /// <summary>Illustrious XL and its offspring (NoobAI): SDXL underneath, prompted with Danbooru tags under its own quality tags (later on 2026-09-24, the user's ask). Last, so the others keep their numbers.</summary>
    Illustrious,

    /// <summary>Juggernaut XL: a photoreal SDXL fine-tune prompted like a photograph, at a low CFG with DPM++ 2M SDE Karras (later on 2026-09-24, the user's ask). Last, so the others keep their numbers.</summary>
    Juggernaut,

    // The six below (later still on 2026-09-24, the user's ask), their defaults read off the templates their ComfyUI ships. Appended, so the others keep their numbers.

    /// <summary>FLUX.2 [dev]: the 32B model, Mistral Small 3.1 as its text encoder, sampled through <c>BasicGuider</c> + <c>FluxGuidance</c> (4), 20 steps, no negative.</summary>
    Flux2,

    /// <summary>FLUX.2 Klein (4B, 9B): the small FLUX.2, Qwen3 as its text encoder, sampled through <c>CFGGuider</c> — the distilled model at CFG 1 and 4 steps (the defaults), the base at CFG 5 and 20 with a negative.</summary>
    Flux2Klein,

    /// <summary>Krea 2 (Turbo): <c>KSampler</c> at CFG 1, 8 steps, euler / simple, the negative zeroed out; the template expands short prompts into a rich paragraph.</summary>
    Krea2,

    /// <summary>Z-Image (Turbo): Tongyi's 6B, Qwen3 4B as its text encoder, <c>KSampler</c> at CFG 1, 8 steps, res_multistep / simple, no negative.</summary>
    ZImage,

    /// <summary>Qwen Image (2512): the 20B MMDiT known for rendering text, Qwen2.5-VL 7B as its encoder, 1328², 50 steps, CFG 4, with a negative.</summary>
    QwenImage,

    /// <summary>Stable Diffusion 3.5 (Large): a single checkpoint, <c>KSampler</c> at CFG 4, 20 steps, euler / sgm_uniform, with a negative.</summary>
    Sd35,

    // Five more (later still on 2026-09-24, the user's ask), again off the templates their ComfyUI ships. Appended.

    /// <summary>Ernie Image (Turbo): Baidu's, Ministral 3B as its text encoder over the FLUX.2 VAE; <c>KSampler</c> at CFG 1, 8 steps, no negative (the base: 20 steps, CFG 4, with one).</summary>
    Ernie,

    /// <summary>Boogu Image (Turbo): Qwen3-VL 8B as its text encoder; <c>KSampler</c> at CFG 1, 4 steps, lcm / sgm_uniform, no negative.</summary>
    Boogu,

    /// <summary>LongCat Image: Meituan's, Qwen2.5-VL 7B as its encoder, <c>FluxGuidance</c> on both prompts; <c>KSampler</c> at CFG 4, 20 steps, with a negative.</summary>
    LongCat,

    /// <summary>HiDream I1 (Full): four text encoders (CLIP-L, CLIP-G, T5-XXL, Llama 3.1 8B); <c>KSampler</c> at CFG 5, 50 steps, uni_pc, with a negative (Dev: 28 at CFG 1, Fast: 16 at CFG 1, both lcm / normal).</summary>
    HiDream,

    /// <summary>Ideogram 4: a JSON-structured prompt, <c>SamplerCustomAdvanced</c> with a <c>DualModelGuider</c> (CFG 7) and <c>Ideogram4Scheduler</c> (20 steps by its Default preset), no negative.</summary>
    Ideogram4,
}

/// <summary>What a family's template falls back to for a placeholder the call leaves out.</summary>
public sealed record ComfyDefaults(int Width, int Height, int Steps, double Cfg, string Negative);

/// <summary>The families' words, defaults and prompt guides. Pure; the words and guides are pinned.</summary>
public static class ComfyFamilies
{
    /// <summary>The sidecar's words, in <see cref="ComfyFamily"/> order.</summary>
    public static readonly IReadOnlyList<string> Names = ["other", "pony", "sdxl", "flux", "sd15", "illustrious", "juggernaut", "flux2", "flux2klein", "krea2", "zimage", "qwenimage", "sd35", "ernie", "boogu", "longcat", "hidream", "ideogram4"];

    public static string Name(ComfyFamily family) => Names[(int)family];

    /// <summary>A sidecar's <c>family:</c> value: one of <see cref="Names"/> in any case, plus the spellings people write (<c>sd1.5</c>, <c>pdxl</c>, <c>xl</c>).</summary>
    public static bool TryParse(string? text, out ComfyFamily family)
    {
        family = ComfyFamily.Other;
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "other":
                return true;
            case "pony" or "ponyxl" or "pony-xl" or "pdxl" or "pony diffusion xl" or "pony diffusion":
                family = ComfyFamily.Pony;
                return true;
            case "illustrious" or "illustrious-xl" or "illustriousxl" or "ilxl" or "noobai" or "noob":
                family = ComfyFamily.Illustrious;
                return true;
            case "juggernaut" or "juggernautxl" or "juggernaut-xl" or "jugg":
                family = ComfyFamily.Juggernaut;
                return true;
            case "flux2" or "flux.2" or "flux-2" or "flux 2" or "flux2-dev" or "flux.2 dev":
                family = ComfyFamily.Flux2;
                return true;
            case "flux2klein" or "klein" or "flux2-klein" or "flux-2-klein" or "flux.2 klein" or "flux 2 klein":
                family = ComfyFamily.Flux2Klein;
                return true;
            case "krea2" or "krea-2" or "krea 2" or "krea2-turbo":
                family = ComfyFamily.Krea2;
                return true;
            case "zimage" or "z-image" or "z_image" or "z-image-turbo":
                family = ComfyFamily.ZImage;
                return true;
            case "qwenimage" or "qwen-image" or "qwen_image" or "qwen":
                family = ComfyFamily.QwenImage;
                return true;
            case "sd35" or "sd3.5" or "sd3" or "sd-3.5":
                family = ComfyFamily.Sd35;
                return true;
            case "ernie" or "ernie-image" or "ernie_image" or "ernie-image-turbo":
                family = ComfyFamily.Ernie;
                return true;
            case "boogu" or "boogu-image" or "boogu_image":
                family = ComfyFamily.Boogu;
                return true;
            case "longcat" or "longcat-image" or "longcat_image":
                family = ComfyFamily.LongCat;
                return true;
            case "hidream" or "hidream-i1" or "hidream_i1" or "hi-dream":
                family = ComfyFamily.HiDream;
                return true;
            case "ideogram4" or "ideogram" or "ideogram-4" or "ideogram 4" or "ideogram v4":
                family = ComfyFamily.Ideogram4;
                return true;
            case "sdxl" or "xl" or "sd-xl":
                family = ComfyFamily.Sdxl;
                return true;
            case "flux" or "flux1" or "flux.1":
                family = ComfyFamily.Flux;
                return true;
            case "sd15" or "sd1.5" or "sd 1.5" or "sd1" or "sd":
                family = ComfyFamily.Sd15;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The family a workflow's file name suggests when its sidecar names none: <c>illustrious</c> / <c>ilxl</c> / <c>noob</c>
    /// first (later on 2026-09-24: a Pony-and-Illustrious merge is prompted the Illustrious way), then <c>pony</c> (a Pony
    /// model is an SDXL one, and the dialect differs), then <c>flux</c>, <c>sd15</c> / <c>sd1.5</c>, <c>sdxl</c> / <c>xl</c>; else
    /// <see cref="ComfyFamily.Other"/>. Case-insensitive. Later still that day the six newer families, each ahead of the
    /// broader word it contains: Krea 2, Z-Image and Qwen Image first (their files are <c>krea2_…</c>, <c>z_image_…</c>,
    /// <c>qwen_image_…</c>), Klein ahead of FLUX.2 (<c>flux-2-klein-4b</c>), FLUX.2 ahead of <c>flux</c> (Flux 1 — and
    /// <c>flux1_krea_dev</c> is a Flux 1, not Krea 2), SD3.5 ahead of SD 1.5 and <c>xl</c>. The name read is the workflow's,
    /// the checkpoint's or the <c>unet_name</c>'s — never a VAE's, so Krea 2's <c>qwen_image_vae</c> misleads nothing.
    /// </summary>
    public static ComfyFamily Guess(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string n = name.ToLowerInvariant();
        return n.Contains("illustrious", StringComparison.Ordinal) || n.Contains("ilxl", StringComparison.Ordinal) || n.Contains("noob", StringComparison.Ordinal) ? ComfyFamily.Illustrious
            : n.Contains("ernie", StringComparison.Ordinal) ? ComfyFamily.Ernie
            : n.Contains("boogu", StringComparison.Ordinal) ? ComfyFamily.Boogu
            : n.Contains("longcat", StringComparison.Ordinal) ? ComfyFamily.LongCat
            : n.Contains("hidream", StringComparison.Ordinal) ? ComfyFamily.HiDream
            : n.Contains("ideogram", StringComparison.Ordinal) ? ComfyFamily.Ideogram4
            : Any(n, "krea2", "krea-2", "krea_2") ? ComfyFamily.Krea2
            : Any(n, "z_image", "z-image", "zimage") ? ComfyFamily.ZImage
            : Any(n, "qwen_image", "qwen-image", "qwenimage") ? ComfyFamily.QwenImage
            : n.Contains("klein", StringComparison.Ordinal) ? ComfyFamily.Flux2Klein
            : Any(n, "flux2", "flux.2", "flux-2", "flux_2") ? ComfyFamily.Flux2
            : n.Contains("juggernaut", StringComparison.Ordinal) ? ComfyFamily.Juggernaut
            : n.Contains("pony", StringComparison.Ordinal) ? ComfyFamily.Pony
            : n.Contains("flux", StringComparison.Ordinal) ? ComfyFamily.Flux
            : Any(n, "sd3.5", "sd35", "sd3_5", "sd3") ? ComfyFamily.Sd35
            // v1-5 / v1.5: the base SD 1.5 checkpoint's own file name (v1-5-pruned-emaonly), which read as other until later still on 2026-09-24.
            : Any(n, "sd15", "sd1.5", "sd-1.5", "v1-5", "v1.5") ? ComfyFamily.Sd15
            : n.Contains("xl", StringComparison.Ordinal) ? ComfyFamily.Sdxl
            : ComfyFamily.Other;
    }

    /// <summary>
    /// Whether the family is run with a negative prompt at all (later still on 2026-09-24): false for the ones sampled at CFG 1
    /// or through a guidance node, where a negative does nothing — Flux, FLUX.2 dev and Klein, Krea 2, Z-Image, Ernie Turbo,
    /// Boogu, Ideogram 4 — so the model adds no reinforcing tags there.
    /// </summary>
    public static bool UsesNegative(ComfyFamily family) => family is not (ComfyFamily.Flux or ComfyFamily.Flux2 or ComfyFamily.Flux2Klein
        or ComfyFamily.Krea2 or ComfyFamily.ZImage or ComfyFamily.Ernie or ComfyFamily.Boogu or ComfyFamily.Ideogram4);

    private static bool Any(string name, params string[] words) => words.Any(w => name.Contains(w, StringComparison.Ordinal));

    /// <summary>Qwen Image's negative (later still on 2026-09-24): the one its ComfyUI template ships, in Chinese there, in English here — the model reads both. Pinned.</summary>
    public const string QwenImageNegative = "low resolution, low quality, deformed limbs, deformed fingers, oversaturated, waxy, faces without detail, overly smooth, AI look, chaotic composition, blurry or distorted text";

    /// <summary>
    /// LongCat Image's negative (later still on 2026-09-24): its template's, less what only suited the template's own
    /// sample (cheap jewelry; cartoon, anime, illustration — a style, not a fault; text — LongCat is known for rendering it). Pinned.
    /// </summary>
    public const string LongCatNegative = "blurry, low resolution, oversaturated, harsh lighting, messy composition, distorted face, extra fingers, bad anatomy, plastic texture, watermark, logo";

    /// <summary>HiDream I1's negative: the one its templates ship. Pinned.</summary>
    public const string HiDreamNegative = "bad ugly jpeg artifacts";

    /// <summary>The negative prompt Pony Diffusion XL is usually run with: the low score tags and the common faults. Pinned.</summary>
    public const string PonyNegative = "score_6, score_5, score_4, worst quality, low quality, blurry, jpeg artifacts, watermark, signature, text";

    /// <summary>The negative Illustrious XL is usually run with (later on 2026-09-24). Pinned.</summary>
    public const string IllustriousNegative = "worst quality, low quality, bad anatomy, bad hands, extra digits, jpeg artifacts, watermark, signature, text";

    /// <summary>The negative Juggernaut XL is run with here (later on 2026-09-24): the generic faults, and away from drawings. Pinned.</summary>
    public const string JuggernautNegative = "worst quality, low quality, blurry, deformed, bad anatomy, extra fingers, watermark, signature, text, cartoon, anime, illustration";

    /// <summary>The generic negative for the SDXL and SD 1.5 families. Pinned.</summary>
    public const string GenericNegative = "worst quality, low quality, blurry, deformed, bad anatomy, extra fingers, watermark, signature, text";

    /// <summary>
    /// A family's fallbacks: the native size (1024² for the XL and Flux families, 512² for SD 1.5), a middling step
    /// count, the CFG the family is sampled at (Flux runs its KSampler at 1 — its guidance is a node of its own), and
    /// the negative prompt (none for Flux, which ignores it, or for an unknown family).
    /// </summary>
    public static ComfyDefaults Defaults(ComfyFamily family) => family switch
    {
        ComfyFamily.Pony => new(1024, 1024, 25, 7.0, PonyNegative),
        ComfyFamily.Sdxl => new(1024, 1024, 30, 6.5, GenericNegative),
        ComfyFamily.Flux => new(1024, 1024, 20, 1.0, ""),
        ComfyFamily.Sd15 => new(512, 512, 25, 7.0, GenericNegative),
        ComfyFamily.Illustrious => new(1024, 1024, 28, 5.5, IllustriousNegative),
        ComfyFamily.Juggernaut => new(1024, 1024, 30, 4.5, JuggernautNegative),
        ComfyFamily.Flux2 => new(1024, 1024, 20, 4.0, ""),
        ComfyFamily.Flux2Klein => new(1024, 1024, 4, 1.0, ""),
        ComfyFamily.Krea2 => new(1024, 1024, 8, 1.0, ""),
        ComfyFamily.ZImage => new(1024, 1024, 8, 1.0, ""),
        ComfyFamily.QwenImage => new(1328, 1328, 50, 4.0, QwenImageNegative),
        ComfyFamily.Sd35 => new(1024, 1024, 20, 4.0, GenericNegative),
        ComfyFamily.Ernie => new(1024, 1024, 8, 1.0, ""),
        ComfyFamily.Boogu => new(1024, 1024, 4, 1.0, ""),
        ComfyFamily.LongCat => new(1024, 1024, 20, 4.0, LongCatNegative),
        ComfyFamily.HiDream => new(1024, 1024, 50, 5.0, HiDreamNegative),
        ComfyFamily.Ideogram4 => new(1024, 1024, 20, 7.0, ""),
        _ => new(1024, 1024, 25, 7.0, ""),
    };

    /// <summary>
    /// How to write a prompt for the family: one paragraph each, for the <c>generate_image</c> description. Pony's is
    /// the score-tag prefix, a source tag and a rating tag, then booru tags; Flux's is prose. Pinned.
    /// </summary>
    public static string StyleGuide(ComfyFamily family) => family switch
    {
        // Later still on 2026-09-24, the user's ask: no rating_ tag unless they ask for one (it was always added), and exact
        // Danbooru tags weighted where it helps, a phrase only where no tag is specific enough (tags and phrases were one list).
        ComfyFamily.Pony =>
            "pony (Pony Diffusion XL): start with \"score_9, score_8_up, score_7_up\", then one source tag (source_anime, source_cartoon, source_furry, source_pony); " +
            "no rating_ tag unless the user asks for one. Then comma-separated, well-known Danbooru tags — subject and count (1girl, solo), hair, eyes, body, clothing, " +
            "pose, expression, setting, lighting, composition, art style — the ones that matter most weighted up, (tag:1.2) to (tag:1.4), and the ones to keep faint " +
            "weighted down, (tag:0.8). A short descriptive phrase only where no Danbooru tag is specific enough (a particular object, pattern, colour combination or " +
            "arrangement), never full sentences.",
        ComfyFamily.Sdxl =>
            "sdxl: a short phrase for the subject and scene, then comma-separated details — medium, style, lighting, composition, " +
            "quality (masterpiece, highly detailed, sharp focus); (term:1.2) to weight.",
        ComfyFamily.Flux =>
            "flux: plain natural-language sentences, like a detailed caption — subject, setting, lighting, camera and style; text to render in \"quotes\"; " +
            "no tag lists, no weights, no negative prompt.",
        ComfyFamily.Juggernaut =>
            "juggernaut (Juggernaut XL): describe it like a photograph, in plain sentences or short phrases — subject, setting, lighting, lens or camera " +
            "(85mm, f/1.8, shallow depth of field), mood and film or photo style; no score_ or quality-tag stacks, no Danbooru tags; (term:1.2) sparingly.",
        ComfyFamily.Illustrious =>
            "illustrious (Illustrious XL, NoobAI): start with \"masterpiece, best quality, amazing quality, very aesthetic, absurdres\", then comma-separated Danbooru tags — " +
            "subject and count (1girl, solo), features, clothing, pose, expression, setting, lighting; add photorealistic, realistic for a photo merge; " +
            "no score_ or source_ tags, no sentences; (tag:1.2) to weight.",
        ComfyFamily.Flux2 =>
            "flux2 (FLUX.2 dev): detailed natural-language prose, as long and structured as the scene needs — subject first, then setting, composition, lighting, camera and style; " +
            "exact colours as hex (#1E90FF); text to render in \"quotes\"; a JSON-structured prompt is fine for a complex scene; no tag lists, no weights, no negative.",
        ComfyFamily.Flux2Klein =>
            "flux2klein (FLUX.2 Klein): clear natural-language prose, a few sentences — subject, setting, lighting, camera and style; text to render in \"quotes\"; " +
            "no tag lists, no weights; no negative (the distilled model ignores it).",
        ComfyFamily.Krea2 =>
            "krea2 (Krea 2): one rich descriptive paragraph — subject, style and medium, composition, lighting, colour and mood spelled out; art-style words welcome; " +
            "no tag lists, no weights, no negative (turbo, CFG 1).",
        ComfyFamily.ZImage =>
            "zimage (Z-Image Turbo): a concise natural-language description, English or Chinese — subject, scene, light and camera; text to render in \"quotes\"; " +
            "no tag stacks, no negative (turbo, CFG 1).",
        ComfyFamily.QwenImage =>
            "qwenimage (Qwen Image): a long, detailed natural-language description, English or Chinese; it renders text well — put the exact words in \"quotes\" " +
            "and say where they go, and describe the layout of a poster or sign; a negative prompt is used.",
        ComfyFamily.Sd35 =>
            "sd35 (Stable Diffusion 3.5): natural sentences for the scene, then short style and quality phrases; text to render in \"quotes\"; " +
            "a negative prompt helps; (term:1.2) to weight, sparingly.",
        ComfyFamily.Ernie =>
            "ernie (Ernie Image Turbo): a rich natural-language visual description, English or Chinese — its template's enhancer expands short prompts into one; " +
            "good at text and infographics: the exact words in \"quotes\" and where they go; no tag lists, no weights, no negative (turbo).",
        ComfyFamily.Boogu =>
            "boogu (Boogu Image Turbo): descriptive natural-language sentences — subject, style, lighting, camera or effect; no tag lists, no weights, no negative (turbo, CFG 1).",
        ComfyFamily.LongCat =>
            "longcat (LongCat Image): a detailed natural-language description, English or Chinese; it renders text: the exact words in \"quotes\"; a negative prompt is used.",
        ComfyFamily.HiDream =>
            "hidream (HiDream I1): detailed natural language, long prompts welcome (its Llama text encoder reads them) — subject, setting, style and medium named; " +
            "a negative prompt is used on Full.",
        ComfyFamily.Ideogram4 =>
            "ideogram4 (Ideogram 4): a JSON object — \"high_level_description\"; \"style_description\" with \"aesthetics\", \"lighting\", \"photo\", \"medium\" and " +
            "\"color_palette\" (hex colours); \"compositional_deconstruction\" with a \"background\" and \"elements\" (each a \"type\", a \"bbox\" and a \"desc\"; text elements " +
            "quote their exact words). Plain prose works too (its magic prompt expands it). Strong at typography; no negative.",
        ComfyFamily.Sd15 =>
            "sd15 (Stable Diffusion 1.5): comma-separated tags and short phrases, the most important first, quality tags (masterpiece, best quality), " +
            "(term:1.2) to weight; under about 75 tokens.",
        _ =>
            "other: a clear comma-separated description — subject, setting, style, lighting, quality.",
    };
}
