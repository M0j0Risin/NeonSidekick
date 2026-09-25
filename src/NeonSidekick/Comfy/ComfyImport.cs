using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NeonSidekick.Comfy;

/// <summary>What an import found: the placeholdered graph, where each placeholder went, and the values it replaced — the new workflow's defaults.</summary>
public sealed record ComfyImportResult(
    string GraphJson,
    IReadOnlyList<string> Found,
    string Negative,
    int? Width,
    int? Height,
    int? Steps,
    double? Cfg,
    double? Denoise,
    string Checkpoint,
    int ImageCount)
{
    /// <summary>Whether the graph loads a picture.</summary>
    public bool TakesImage => ImageCount > 0;
}

/// <summary>
/// A workflow exported from ComfyUI with <b>Export (API)</b> made into a template (later on 2026-09-24, the user's ask:
/// the add-workflow wizard's import path). Later still that day (FLUX.2's graphs) a <c>SamplerCustomAdvanced</c> is read too:
/// the seed on the <c>RandomNoise</c> its <c>noise</c> leads to, the steps (and a <c>Flux2Scheduler</c>'s size) on the scheduler
/// its <c>sigmas</c> leads to, the CFG on its <c>CFGGuider</c> — or, behind a <c>BasicGuider</c>, the <c>FluxGuidance</c> on the
/// prompt's path — and the prompts through the guider's <c>positive</c> / <c>negative</c> / <c>conditioning</c>. A value fed by a
/// primitive (<c>PrimitiveInt</c>, <c>PrimitiveStringMultiline</c> …) gets its placeholder on the primitive; one set by any other
/// node (a switch, a resolution picker) is left as exported and said so; a prompt fed by another node (Krea 2's prompt
/// expander) is cut loose and made <c>{{prompt}}</c>. From the first <c>KSampler</c> / <c>KSamplerAdvanced</c>: its <c>positive</c>
/// and <c>negative</c> links are followed back — through up to four conditioning nodes (a LoRA or ControlNet apply, a
/// combine) — to a <c>CLIPTextEncode</c>, whose <c>text</c> becomes <c>{{prompt}}</c> / <c>{{negative}}</c>; its
/// <c>seed</c> (or <c>noise_seed</c>), <c>steps</c> and <c>cfg</c> become placeholders, and <c>denoise</c> too when the
/// graph loads an image; every empty-latent node's <c>width</c> / <c>height</c> and every <c>LoadImage</c>'s <c>image</c>
/// likewise. The values replaced come back as the defaults, the negative text as the default negative. Anything else —
/// LoRAs, upscalers, ControlNets — is left as exported. Since later still that day (the face swap) the <c>LoadImage</c>
/// nodes are numbered — <c>{{image}}</c>, <c>{{image2}}</c>, <c>{{image3}}</c> in node-id order — and a graph with no
/// sampler that loads a picture (ReActor, an upscale) is taken too, only its pictures placeholdered. Pure.
/// </summary>
public static class ComfyImport
{
    /// <summary>The problems, pinned: the wizard shows them under the file slot.</summary>
    public const string NoSamplerProblem = "has no KSampler, KSamplerAdvanced or SamplerCustomAdvanced node to take the prompt, seed and steps";
    public const string NoPromptNodeProblem = "has no CLIPTextEncode feeding the sampler's positive input, so there is nowhere for the prompt to go";

    private static readonly JsonDocumentOptions Tolerant = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>The template, or the problem (<see cref="ComfyWorkflow.UiFormatProblem"/> for a UI save, …).</summary>
    public static (ComfyImportResult? Result, string? Problem) Placehold(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonObject? graph;
        try
        {
            graph = JsonNode.Parse(json, documentOptions: Tolerant) as JsonObject;
        }
        catch (JsonException)
        {
            return (null, ComfyWorkflow.NotJsonProblem);
        }

        if (graph is null || graph.Count == 0)
        {
            return (null, ComfyWorkflow.NotApiFormatProblem);
        }

        if (graph.ContainsKey("nodes") && graph.ContainsKey("links"))
        {
            return (null, ComfyWorkflow.UiFormatProblem);
        }

        if (graph.Any(p => p.Value is not JsonObject n || TypeOf(n) is null))
        {
            return (null, ComfyWorkflow.NotApiFormatProblem);
        }

        // The workflow's own {{…}} texts first (later still on 2026-09-24: Ideogram 4's StringReplace searches for a literal
        // {{width}}): escaped to {{!…}}, which Fill writes back as they were, so no placeholder of ours ever lands in them.
        int escaped = Escape(graph);
        var nodes = graph.OrderBy(p => Order(p.Key)).Select(p => (Id: p.Key, Node: (JsonObject)p.Value!)).ToList();
        var sampler = nodes.FirstOrDefault(n => TypeOf(n.Node) is "KSampler" or "KSamplerAdvanced" or "SamplerCustomAdvanced");
        var found = new List<string>();
        bool loadsImage = nodes.Any(n => TypeOf(n.Node) == "LoadImage");
        if (sampler.Node is null)
        {
            if (!loadsImage)
            {
                return (null, NoSamplerProblem);
            }

            // No sampler but pictures in (later still on 2026-09-24, the user's ask: a ReActor face swap, a plain upscale):
            // only the pictures become placeholders; there is no prompt, seed or size to take.
            int only = TakeImages(nodes, found);
            AddKept(found, escaped);
            return (new ComfyImportResult(graph.ToJsonString(ComfyGraphs.Indented), found, "", null, null, null, null, null, "", only), null);
        }

        var inputs = Inputs(sampler.Node);

        // Where each value lives: on the sampler itself (KSampler), or on the nodes a custom sampler's inputs lead to
        // (later still on 2026-09-24, FLUX.2's graph: RandomNoise, a scheduler, a guider).
        (string Id, JsonObject Node) seedNode = sampler, stepsNode = sampler, cfgNode = sampler;
        string seedKey = inputs.ContainsKey("seed") ? "seed" : "noise_seed";
        string cfgKey = "cfg";
        JsonNode? positiveLink = inputs["positive"], negativeLink = inputs["negative"];
        if (TypeOf(sampler.Node) == "SamplerCustomAdvanced")
        {
            if (Linked(graph, inputs["noise"]) is { } noise)
            {
                seedNode = noise;
                seedKey = Inputs(noise.Node).ContainsKey("noise_seed") ? "noise_seed" : "seed";
            }

            if (Linked(graph, inputs["sigmas"]) is { } scheduler)
            {
                stepsNode = scheduler;
            }

            if (Linked(graph, inputs["guider"]) is { } guider)
            {
                var g = Inputs(guider.Node);
                cfgNode = guider;
                positiveLink = g["positive"] ?? g["conditioning"];
                negativeLink = g["negative"];
                if (!g.ContainsKey("cfg") && FluxGuidance(graph, positiveLink) is { } guidance)
                {
                    // BasicGuider (FLUX.2 dev): the guidance on the prompt's path is what {{cfg}} sets.
                    cfgNode = guidance;
                    cfgKey = "guidance";
                }
            }
        }

        var positive = TextNode(graph, positiveLink);
        if (positive is null)
        {
            return (null, NoPromptNodeProblem);
        }

        TakeText(graph, positive.Value, "prompt", found, out _);
        string negativeText = "";
        if (TextNode(graph, negativeLink) is { } negative && negative.Id != positive.Value.Id)
        {
            TakeText(graph, negative, "negative", found, out negativeText);
        }

        Take(graph, seedNode, seedKey, "seed", found, out _);
        int? steps = Take(graph, stepsNode, "steps", "steps", found, out var s) ? (int?)s : null;
        double? cfg = Take(graph, cfgNode, cfgKey, "cfg", found, out var c) ? c : null;
        double? denoise = null;
        if (loadsImage && Take(graph, stepsNode, "denoise", "denoise", found, out var d))
        {
            denoise = d;
        }

        int? width = null, height = null;
        // The latents, and a scheduler that takes the size too (Flux2Scheduler): each place the size is set gets the placeholder.
        var sized = nodes.Where(n => (TypeOf(n.Node) ?? "").StartsWith("Empty", StringComparison.Ordinal) && (TypeOf(n.Node) ?? "").Contains("Latent", StringComparison.Ordinal)).ToList();
        if (stepsNode.Node != sampler.Node && Inputs(stepsNode.Node).ContainsKey("width"))
        {
            sized.Add(stepsNode);
        }

        foreach (var node in sized)
        {
            if (Take(graph, node, "width", "width", found, out var w))
            {
                width ??= (int)w;
            }

            if (Take(graph, node, "height", "height", found, out var h))
            {
                height ??= (int)h;
            }
        }

        int images = TakeImages(nodes, found);
        AddKept(found, escaped);
        string checkpoint = nodes.Select(n => Inputs(n.Node)["ckpt_name"] ?? Inputs(n.Node)["unet_name"])
            .OfType<JsonValue>().Where(v => v.GetValueKind() == JsonValueKind.String).Select(v => v.GetValue<string>()).FirstOrDefault() ?? "";
        return (new ComfyImportResult(graph.ToJsonString(ComfyGraphs.Indented), found, negativeText, width, height, steps, cfg, denoise, checkpoint, images), null);
    }

    /// <summary>
    /// Each <c>LoadImage</c>'s <c>image</c> made a placeholder in node-id order (later still on 2026-09-24, the face swap; every
    /// one was <c>{{image}}</c> before, so a two-picture graph got the same picture twice): the first <c>{{image}}</c>, the
    /// next <c>{{image2}}</c>, then <c>{{image3}}</c>; any past <see cref="ComfyWorkflow.MaxInputImages"/> is left as exported
    /// and said so. How many became placeholders.
    /// </summary>
    private static int TakeImages(List<(string Id, JsonObject Node)> nodes, List<string> found)
    {
        int count = 0;
        foreach (var load in nodes.Where(n => TypeOf(n.Node) == "LoadImage"))
        {
            if (count == ComfyWorkflow.MaxInputImages)
            {
                found.Add(ImageLeftAsExported(load.Id));
                continue;
            }

            string key = ComfyWorkflow.ImageKeys[count++];
            Inputs(load.Node)["image"] = "{{" + key + "}}";
            found.Add(Where(key, load.Id, load.Node, "image"));
        }

        return count;
    }

    private static void AddKept(List<string> found, int escaped)
    {
        if (escaped > 0)
        {
            found.Add(KeptOwnTexts(escaped));
        }
    }

    /// <summary>The found-list's line for a <c>LoadImage</c> past the third. Pinned.</summary>
    public static string ImageLeftAsExported(string id) =>
        "LoadImage node " + id + " left as exported: a workflow takes at most " + ComfyWorkflow.MaxInputImages.ToString(CultureInfo.InvariantCulture) + " input pictures";

    /// <summary>The found-list's line for a workflow that had {{…}} texts of its own. Pinned.</summary>
    public static string KeptOwnTexts(int count) =>
        "kept " + count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " of the workflow's own {{…}} texts as it was" : " of the workflow's own {{…}} texts as they were");

    /// <summary>Every <c>{{name}}</c> inside a string of <paramref name="node"/> rewritten to <c>{{!name}}</c>; how many.</summary>
    private static int Escape(JsonNode node)
    {
        int count = 0;
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(p => p.Key).ToList())
                {
                    if (obj[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                    {
                        string text = value.GetValue<string>();
                        string done = EscapeText(text, ref count);
                        if (!ReferenceEquals(done, text))
                        {
                            obj[key] = done;
                        }
                    }
                    else if (obj[key] is { } inner)
                    {
                        count += Escape(inner);
                    }
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                    {
                        string text = value.GetValue<string>();
                        string done = EscapeText(text, ref count);
                        if (!ReferenceEquals(done, text))
                        {
                            array[i] = done;
                        }
                    }
                    else if (array[i] is { } inner)
                    {
                        count += Escape(inner);
                    }
                }

                break;
        }

        return count;
    }

    /// <summary><paramref name="text"/> with each <c>{{name}}</c> (letters, digits, underscores) made <c>{{!name}}</c>; the same instance when there is none.</summary>
    private static string EscapeText(string text, ref int count)
    {
        if (!text.Contains("{{", StringComparison.Ordinal))
        {
            return text;
        }

        var sb = new System.Text.StringBuilder(text.Length + 8);
        int at = 0;
        bool any = false;
        while (at < text.Length)
        {
            int open = text.IndexOf("{{", at, StringComparison.Ordinal);
            int close = open < 0 ? -1 : text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (open < 0 || close < 0)
            {
                sb.Append(text, at, text.Length - at);
                break;
            }

            string name = text[(open + 2)..close].Trim();
            sb.Append(text, at, open - at);
            if (name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                sb.Append("{{!").Append(name).Append("}}");
                count++;
                any = true;
            }
            else
            {
                sb.Append(text, open, close + 2 - open);
            }

            at = close + 2;
        }

        return any ? sb.ToString() : text;
    }

    /// <summary>The node a link <c>[id, output]</c> points at, or null.</summary>
    private static (string Id, JsonObject Node)? Linked(JsonObject graph, JsonNode? link)
    {
        if (link is not JsonArray { Count: 2 } pair || pair[0] is not JsonValue idValue)
        {
            return null;
        }

        string id = idValue.GetValueKind() == JsonValueKind.String ? idValue.GetValue<string>() : idValue.ToJsonString();
        return graph[id] is JsonObject node ? (id, node) : null;
    }

    /// <summary>A <c>FluxGuidance</c> on the prompt's path (up to five hops back), or null.</summary>
    private static (string Id, JsonObject Node)? FluxGuidance(JsonObject graph, JsonNode? link)
    {
        for (int hop = 0; hop < 5 && Linked(graph, link) is { } node; hop++)
        {
            if (TypeOf(node.Node) == "FluxGuidance")
            {
                return node;
            }

            var ins = Inputs(node.Node);
            link = ins.FirstOrDefault(p => p.Key.Contains("conditioning", StringComparison.OrdinalIgnoreCase) && p.Value is JsonArray).Value;
        }

        return null;
    }

    /// <summary>Whether a node is one of ComfyUI's primitives (PrimitiveInt, PrimitiveFloat, PrimitiveStringMultiline …): a value its <c>value</c> input holds.</summary>
    private static bool IsPrimitive(JsonObject node) => (TypeOf(node) ?? "").StartsWith("Primitive", StringComparison.Ordinal) && Inputs(node).ContainsKey("value");

    /// <summary>
    /// The prompt (or negative) text of <paramref name="encoder"/> made a placeholder, handing back the text it held: on the
    /// encoder itself, on a primitive that feeds it, or — fed by anything else (a prompt expander, a switch) — on the encoder,
    /// the link replaced, so the text sent is the call's (later still on 2026-09-24).
    /// </summary>
    private static void TakeText(JsonObject graph, (string Id, JsonObject Node) encoder, string placeholder, List<string> found, out string held)
    {
        held = "";
        var inputs = Inputs(encoder.Node);
        if (inputs["text"] is JsonArray link && Linked(graph, link) is { } source)
        {
            if (IsPrimitive(source.Node))
            {
                held = Inputs(source.Node)["value"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";
                Inputs(source.Node)["value"] = "{{" + placeholder + "}}";
                found.Add(Where(placeholder, source.Id, source.Node, "value"));
                return;
            }

            inputs["text"] = "{{" + placeholder + "}}";
            found.Add(Where(placeholder, encoder.Id, encoder.Node, "text") + " (replacing the link from node " + source.Id + " " + TypeOf(source.Node) + ")");
            return;
        }

        held = inputs["text"] is JsonValue text && text.GetValueKind() == JsonValueKind.String ? text.GetValue<string>() : "";
        inputs["text"] = "{{" + placeholder + "}}";
        found.Add(Where(placeholder, encoder.Id, encoder.Node, "text"));
    }

    /// <summary>The <c>CLIPTextEncode</c> a link leads back to, through up to four nodes: each hop follows the node's first input that is a link.</summary>
    private static (string Id, JsonObject Node)? TextNode(JsonObject graph, JsonNode? link)
    {
        for (int hop = 0; hop < 5 && link is JsonArray { Count: 2 } pair && pair[0] is JsonValue idValue; hop++)
        {
            string id = idValue.GetValueKind() == JsonValueKind.String ? idValue.GetValue<string>() : idValue.ToJsonString();
            if (graph[id] is not JsonObject node)
            {
                return null;
            }

            if (TypeOf(node) is "CLIPTextEncode")
            {
                // Its text a literal, or linked from a primitive or a prompt node: TakeText sorts out which.
                return (id, node);
            }

            // Through a conditioning node: its conditioning input first, else its first link.
            var ins = Inputs(node);
            link = ins.FirstOrDefault(p => p.Key.Contains("conditioning", StringComparison.OrdinalIgnoreCase) && p.Value is JsonArray).Value
                ?? ins.FirstOrDefault(p => p.Value is JsonArray).Value;
        }

        return null;
    }

    /// <summary>
    /// Replaces a numeric input with its placeholder, handing back the number it held: on the node, or on the primitive that
    /// feeds it (later still on 2026-09-24). Fed by any other node (a switch, a resolution picker) it is left as exported and
    /// the found-list says so; false then, and when the input is absent.
    /// </summary>
    private static bool Take(JsonObject graph, (string Id, JsonObject Node) node, string input, string placeholder, List<string> found, out double value)
    {
        value = 0;
        var inputs = Inputs(node.Node);
        if (inputs[input] is JsonArray link && Linked(graph, link) is { } source)
        {
            if (IsPrimitive(source.Node) && Inputs(source.Node)["value"] is JsonValue done && done.GetValueKind() == JsonValueKind.String && done.GetValue<string>() == "{{" + placeholder + "}}")
            {
                // The same primitive feeds another input too (Klein's size: the latent and the scheduler): placeholdered already.
                return false;
            }

            if (IsPrimitive(source.Node) && Inputs(source.Node)["value"] is JsonValue primitive && primitive.GetValueKind() == JsonValueKind.Number)
            {
                value = primitive.GetValue<double>();
                Inputs(source.Node)["value"] = "{{" + placeholder + "}}";
                found.Add(Where(placeholder, source.Id, source.Node, "value"));
                return true;
            }

            found.Add(LeftAsExported(placeholder, source.Id, source.Node));
            return false;
        }

        if (inputs[input] is not JsonValue held || held.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        value = held.GetValue<double>();
        inputs[input] = "{{" + placeholder + "}}";
        found.Add(Where(placeholder, node.Id, node.Node, input));
        return true;
    }

    /// <summary>A value another node sets (a switch, a resolution picker), left as the workflow's author built it. Pinned.</summary>
    public static string LeftAsExported(string placeholder, string id, JsonObject node) =>
        "{{" + placeholder + "}}: set by node " + id + " " + TypeOf(node) + ", left as exported";

    private static string Where(string placeholder, string id, JsonObject node, string input) =>
        "{{" + placeholder + "}} → node " + id + " " + TypeOf(node) + "." + input;

    private static JsonObject Inputs(JsonObject node) => node["inputs"] as JsonObject ?? (JsonObject)(node["inputs"] = new JsonObject());

    internal static string? TypeOf(JsonObject node) => node["class_type"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static int Order(string id) => int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue;
}
