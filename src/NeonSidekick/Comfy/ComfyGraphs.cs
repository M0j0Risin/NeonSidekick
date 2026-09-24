using System.Text.Json.Nodes;

namespace NeonSidekick.Comfy;

/// <summary>
/// The standard workflows the add-workflow wizard builds (later on 2026-09-24, the user's ask: a wizard "similar to how
/// we did it for SQL connections"): ComfyUI's text-to-image graph in the API format — checkpoint, the two prompts, an
/// empty latent, the sampler, the decode, the save — and its image-to-image twin, where a <c>LoadImage</c> of
/// <c>{{image}}</c> is encoded into the latent the sampler repaints by <c>{{denoise}}</c>. The same shape as the two
/// starter workflows first run on the user's server. A CLIP skip puts a <c>CLIPSetLastLayer</c> between the checkpoint
/// and both prompts (Pony Diffusion XL wants 2). Every value the calls change is a placeholder. Pure.
/// </summary>
public static class ComfyGraphs
{
    /// <summary>The samplers each family is usually run with: the wizard's cursor starts there.</summary>
    public static string DefaultSampler(ComfyFamily family) => family switch
    {
        ComfyFamily.Pony or ComfyFamily.Illustrious => "euler_ancestral",
        ComfyFamily.Sdxl or ComfyFamily.Juggernaut => "dpmpp_2m_sde",
        ComfyFamily.ZImage => "res_multistep",
        ComfyFamily.Boogu => "lcm",
        ComfyFamily.HiDream => "uni_pc",
        _ => "euler",
    };

    /// <summary>The scheduler each family is usually run with.</summary>
    public static string DefaultScheduler(ComfyFamily family) => family switch
    {
        ComfyFamily.Sdxl or ComfyFamily.Juggernaut => "karras",
        ComfyFamily.Sd35 or ComfyFamily.Boogu => "sgm_uniform",
        ComfyFamily.Ernie or ComfyFamily.LongCat or ComfyFamily.HiDream or ComfyFamily.Ideogram4 => "simple",
        ComfyFamily.Flux or ComfyFamily.Flux2 or ComfyFamily.Flux2Klein or ComfyFamily.Krea2 or ComfyFamily.ZImage or ComfyFamily.QwenImage => "simple",
        _ => "normal",
    };

    /// <summary>
    /// Whether the family's models usually load as split files — a diffusion model, a text encoder and a VAE — rather than
    /// one checkpoint (later still on 2026-09-24): the wizard's Build wires a checkpoint, so for these it points at Import.
    /// </summary>
    public static bool UsuallySplit(ComfyFamily family) =>
        family is ComfyFamily.Flux or ComfyFamily.Flux2 or ComfyFamily.Flux2Klein or ComfyFamily.Krea2 or ComfyFamily.ZImage or ComfyFamily.QwenImage
            or ComfyFamily.Ernie or ComfyFamily.Boogu or ComfyFamily.LongCat or ComfyFamily.HiDream or ComfyFamily.Ideogram4;

    /// <summary>The CLIP skip each family is usually run with: 2 for Pony and Illustrious, none otherwise.</summary>
    public static int DefaultClipSkip(ComfyFamily family) => family is ComfyFamily.Pony or ComfyFamily.Illustrious ? 2 : 0;

    /// <summary>
    /// The graph as JSON text: text → image, or image → image with <paramref name="fromImage"/>. <paramref name="clipSkip"/>
    /// 0 or 1 adds no <c>CLIPSetLastLayer</c>; 2 or more stops at layer −n. The save's prefix is <c>neon/&lt;name&gt;</c>.
    /// </summary>
    public static string Build(string name, string checkpoint, string sampler, string scheduler, int clipSkip, bool fromImage)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(scheduler);
        bool skip = clipSkip >= 2;
        JsonArray Clip() => skip ? Link("10", 0) : Link("4", 1);
        var graph = new JsonObject
        {
            ["3"] = Node("KSampler", new JsonObject
            {
                ["seed"] = "{{seed}}",
                ["steps"] = "{{steps}}",
                ["cfg"] = "{{cfg}}",
                ["sampler_name"] = sampler,
                ["scheduler"] = scheduler,
                ["denoise"] = fromImage ? "{{denoise}}" : JsonValue.Create(1),
                ["model"] = Link("4", 0),
                ["positive"] = Link("6", 0),
                ["negative"] = Link("7", 0),
                ["latent_image"] = fromImage ? Link("12", 0) : Link("5", 0),
            }),
            ["4"] = Node("CheckpointLoaderSimple", new JsonObject { ["ckpt_name"] = checkpoint }),
        };
        if (skip)
        {
            graph["10"] = Node("CLIPSetLastLayer", new JsonObject { ["stop_at_clip_layer"] = -clipSkip, ["clip"] = Link("4", 1) });
        }

        if (fromImage)
        {
            graph["11"] = Node("LoadImage", new JsonObject { ["image"] = "{{image}}" });
            graph["12"] = Node("VAEEncode", new JsonObject { ["pixels"] = Link("11", 0), ["vae"] = Link("4", 2) });
        }
        else
        {
            graph["5"] = Node("EmptyLatentImage", new JsonObject { ["width"] = "{{width}}", ["height"] = "{{height}}", ["batch_size"] = 1 });
        }

        graph["6"] = Node("CLIPTextEncode", new JsonObject { ["text"] = "{{prompt}}", ["clip"] = Clip() });
        graph["7"] = Node("CLIPTextEncode", new JsonObject { ["text"] = "{{negative}}", ["clip"] = Clip() });
        graph["8"] = Node("VAEDecode", new JsonObject { ["samples"] = Link("3", 0), ["vae"] = Link("4", 2) });
        graph["9"] = Node("SaveImage", new JsonObject { ["filename_prefix"] = "neon/" + name, ["images"] = Link("8", 0) });
        return graph.ToJsonString(Indented);
    }

    internal static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true, TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine() };

    private static JsonObject Node(string type, JsonObject inputs) => new() { ["class_type"] = type, ["inputs"] = inputs };

    // The params constructor, not a collection expression: that one binds JsonArray.Add<T>, which AOT flags (IL2026/IL3050).
    private static JsonArray Link(string node, int output) => new(JsonValue.Create(node), JsonValue.Create(output));
}
