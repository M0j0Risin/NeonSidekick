using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NeonSidekick.Comfy;

/// <summary>One editable part of a graph: its value, or why it can't be changed here (<see cref="Why"/> null: editable).</summary>
/// <param name="Node">The node holding it, when there is one.</param>
/// <param name="ClassType">That node's <c>class_type</c>, for the server's list of choices.</param>
/// <param name="Input">The input holding it (<c>ckpt_name</c>, <c>sampler_name</c> …).</param>
public sealed record ComfyGraphPart(string? Value, string? Why, string? Node = null, string? ClassType = null, string? Input = null)
{
    public bool Editable => Why is null;
}

/// <summary>A LoRA on the model's path: the node, its file, its strengths, and whether its strength may change here (<see cref="Why"/> null).</summary>
/// <param name="SwitchedOff">On the branch a <c>ComfySwitchNode</c> leaves unused: shown, never run.</param>
public sealed record ComfyLora(string Node, string ClassType, string Name, double? StrengthModel, double? StrengthClip, bool SwitchedOff, string? Why)
{
    public bool Editable => Why is null;
}

/// <summary>The LoRAs on the model's path, nearest the loader first, and why one can't be added or removed (<see cref="Why"/> null: it can).</summary>
public sealed record ComfyLoraChain(IReadOnlyList<ComfyLora> Loras, string? Why)
{
    public bool Editable => Why is null;
}

/// <summary>What <see cref="ComfyGraphEdit.Read"/> found.</summary>
public sealed record ComfyGraphParts(
    ComfyGraphPart Sampler,
    ComfyGraphPart Scheduler,
    ComfyGraphPart Model,
    ComfyGraphPart Vae,
    ComfyGraphPart ClipSkip,
    ComfyLoraChain Loras,
    ComfyGraphPart OutputName,
    bool CheckpointVae);

/// <summary>
/// The parts of an API-format graph the workflow manager edits, read and changed in place (2026-11-25, the workflow manager's plan), for
/// built and imported workflows alike: the sampler and scheduler, the checkpoint or diffusion model, the VAE, the CLIP skip, the LoRAs and the
/// output name. A part is editable only with exactly one node that can hold it and a literal value there; otherwise the reason says why
/// ("3 samplers", "set by node 12 PrimitiveString"). <see cref="Read"/> never throws and never changes the graph.
///
/// <para>LoRAs: the <c>model</c> link is walked back from every sampler (a <c>SamplerCustomAdvanced</c>'s from its guider's), through
/// <c>LoraLoader</c> and <c>LoraLoaderModelOnly</c> nodes and any node with a <c>model</c> link (<c>ModelSampling…</c>), to the loader that
/// has a <c>ckpt_name</c> or <c>unet_name</c>. A <c>ComfySwitchNode</c> is followed along the branch its <c>switch</c> picks (a literal, or a
/// <c>PrimitiveBoolean</c>'s); the other branch's LoRAs are listed as switched off, and behind a switch only a strength changes. A node that
/// takes a literal <c>lora_name</c> but is no plain loader (<c>LoraLoaderBypass</c>, a custom loader) is listed read-only, never hidden. Any
/// other node ends the walk, read-only. Edits are minimal: a strength in place, a remove splices the node out, an add takes over the consumers
/// of the last LoRA's model output (else the loader's) and, on a checkpoint's CLIP, of the CLIP it sees, so it lands before a
/// <c>CLIPSetLastLayer</c>.</para>
/// </summary>
public static class ComfyGraphEdit
{
    public const string LoraLoader = "LoraLoader";
    public const string LoraLoaderModelOnly = "LoraLoaderModelOnly";
    public const string CheckpointLoader = "CheckpointLoaderSimple";
    public const string VaeLoader = "VAELoader";
    public const string ClipSetLastLayer = "CLIPSetLastLayer";
    public const string SaveImage = "SaveImage";
    public const string Switch = "ComfySwitchNode";

    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    /// <summary>
    /// The graph in <paramref name="json"/> for editing, or null with the reason its graph rows are read-only: a file with comments or trailing
    /// commas (a rewrite would lose them), duplicate keys, or no valid API graph.
    /// </summary>
    public static (JsonObject? Graph, string? Why) Load(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonNode.Parse(json, documentOptions: Strict) is JsonObject graph && graph.Count > 0 && graph.All(p => p.Value is JsonObject node && TypeOf(node) is not null)
                ? (graph, null)
                : (null, ComfyText.EditNotApiGraph);
        }
        catch (JsonException)
        {
            return (null, ComfyText.EditNotPlainJson);
        }
    }

    /// <summary>Whether two graphs are the same (<see cref="JsonNode.DeepEquals"/>).</summary>
    public static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    /// <summary>The graph's editable parts. Never throws, never changes <paramref name="graph"/>.</summary>
    public static ComfyGraphParts Read(JsonObject graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var nodes = Nodes(graph);
        var samplers = nodes.Where(n => TypeOf(n.Node) is "KSampler" or "KSamplerAdvanced").ToList();
        var custom = nodes.FirstOrDefault(n => TypeOf(n.Node) is "KSamplerSelect" or "SamplerCustom" or "SamplerCustomAdvanced");
        ComfyGraphPart SamplerPart(string input) =>
            samplers.Count == 1 ? Literal(graph, samplers[0], input)
            : samplers.Count > 1 ? new ComfyGraphPart(null, ComfyText.EditSeveral(samplers.Count, "samplers"))
            : custom.Node is not null ? new ComfyGraphPart(null, ComfyText.EditCustomSampler)
            : new ComfyGraphPart(null, ComfyText.EditNotInGraph);

        var walk = WalkAll(graph, nodes);
        var model = ModelPart(graph, nodes, walk);
        var (vae, checkpointVae) = VaePart(graph, nodes);
        return new ComfyGraphParts(SamplerPart("sampler_name"), SamplerPart("scheduler"), model, vae, ClipSkipPart(graph, nodes), new ComfyLoraChain(walk.Loras, walk.Why), OutputPart(graph, nodes), checkpointVae);
    }

    // ── Simple values ────────────────────────────────────────────────────────

    /// <summary>Sets an editable part's literal (<see cref="ComfyGraphPart.Node"/>.<see cref="ComfyGraphPart.Input"/>) to <paramref name="value"/>; null when done, else why not.</summary>
    public static string? SetText(JsonObject graph, ComfyGraphPart part, string value)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(value);
        if (part.Why is { } why)
        {
            return why;
        }

        if (part.Node is null || part.Input is null || InputsOf(graph[part.Node] as JsonObject) is not { } inputs)
        {
            return ComfyText.EditNotInGraph;
        }

        inputs[part.Input] = value;
        return null;
    }

    // ── VAE ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The VAE: a <c>VAELoader</c>'s <paramref name="name"/>, or the checkpoint's own with null (only where the graph loads a
    /// <c>CheckpointLoaderSimple</c>). Every <c>vae</c> link is rewired; a <c>VAELoader</c> nothing uses any more is removed.
    /// </summary>
    public static string? SetVae(JsonObject graph, string? name)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var parts = Read(graph);
        if (parts.Vae.Why is { } why)
        {
            return why;
        }

        var nodes = Nodes(graph);
        var links = VaeLinks(nodes);
        string? source = parts.Vae.Node;
        if (name is null)
        {
            if (nodes.FirstOrDefault(n => TypeOf(n.Node) == CheckpointLoader) is not { Node: not null } checkpoint)
            {
                return ComfyText.EditNoCheckpointVae;
            }

            foreach (var (inputs, key) in links)
            {
                inputs[key] = Link(checkpoint.Id, 2);
            }

            RemoveIfUnused(graph, source, VaeLoader);
            return null;
        }

        if (source is not null && graph[source] is JsonObject loader && TypeOf(loader) == VaeLoader)
        {
            InputsOf(loader)!["vae_name"] = name;
            return null;
        }

        string id = NewId(graph);
        graph[id] = Node(VaeLoader, new JsonObject { ["vae_name"] = name });
        foreach (var (inputs, key) in links)
        {
            inputs[key] = Link(id, 0);
        }

        return null;
    }

    // ── CLIP skip ────────────────────────────────────────────────────────────

    /// <summary>
    /// The CLIP skip on a checkpoint's CLIP: 2 or more adds or changes the one <c>CLIPSetLastLayer</c> (at −n), 0 or 1 splices it out. The new
    /// node takes the place of the CLIP the encoders see: the last <c>LoraLoader</c>'s, else the checkpoint's.
    /// </summary>
    public static string? SetClipSkip(JsonObject graph, int skip)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var parts = Read(graph);
        if (parts.ClipSkip.Why is { } why)
        {
            return why;
        }

        string? node = parts.ClipSkip.Node;
        if (skip >= 2)
        {
            if (node is not null)
            {
                InputsOf(graph[node] as JsonObject)!["stop_at_clip_layer"] = -skip;
                return null;
            }

            if (ClipSource(graph, parts) is not { } source)
            {
                return ComfyText.EditNoCheckpointClip;
            }

            var consumers = Consumers(graph, source, 1);
            if (consumers.Count == 0)
            {
                return ComfyText.EditNoCheckpointClip;
            }

            string id = NewId(graph);
            graph[id] = Node(ClipSetLastLayer, new JsonObject { ["stop_at_clip_layer"] = -skip, ["clip"] = Link(source, 1) });
            foreach (var (inputs, key) in consumers)
            {
                inputs[key] = Link(id, 0);
            }

            return null;
        }

        if (node is null)
        {
            return null;
        }

        var self = graph[node] as JsonObject;
        var into = InputsOf(self)?["clip"]?.DeepClone();
        if (into is not JsonArray)
        {
            return ComfyText.EditNoCheckpointClip;
        }

        foreach (var (inputs, key) in Consumers(graph, node, 0))
        {
            inputs[key] = into.DeepClone();
        }

        graph.Remove(node);
        return null;
    }

    // ── LoRAs ────────────────────────────────────────────────────────────────

    /// <summary>A LoRA's strength, model and CLIP both (where it has a CLIP side), in place.</summary>
    public static string? SetLoraStrength(JsonObject graph, string node, double strength)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);
        if (Read(graph).Loras.Loras.FirstOrDefault(l => l.Node == node) is not { } lora)
        {
            return ComfyText.EditNotInGraph;
        }

        if (lora.Why is { } why)
        {
            return why;
        }

        var inputs = InputsOf(graph[node] as JsonObject)!;
        inputs["strength_model"] = strength;
        if (inputs.ContainsKey("strength_clip"))
        {
            inputs["strength_clip"] = strength;
        }

        return null;
    }

    /// <summary>A LoRA spliced out: what used its model output takes its model input, what used its CLIP output its CLIP input.</summary>
    public static string? RemoveLora(JsonObject graph, string node)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);
        var chain = Read(graph).Loras;
        if (chain.Why is { } why)
        {
            return why;
        }

        // A LoRA whose strength another node sets can still go (2026-11-26, the review's #20): only its strength is out of reach.
        if (chain.Loras.FirstOrDefault(l => l.Node == node && !l.SwitchedOff) is not { Why: null or ComfyText.EditStrengthNotLiteral } || graph[node] is not JsonObject self || InputsOf(self) is not { } own)
        {
            return ComfyText.EditNotInGraph;
        }

        var model = own["model"];
        var clip = own["clip"];
        var modelUsers = Consumers(graph, node, 0);
        var clipUsers = Consumers(graph, node, 1);
        if (model is not JsonArray || (clipUsers.Count > 0 && clip is not JsonArray))
        {
            return ComfyText.EditNotInGraph;
        }

        foreach (var (inputs, key) in modelUsers)
        {
            inputs[key] = model.DeepClone();
        }

        foreach (var (inputs, key) in clipUsers)
        {
            inputs[key] = clip!.DeepClone();
        }

        graph.Remove(node);
        return null;
    }

    /// <summary>
    /// A LoRA added after the last one (nearest the sampler): a <c>LoraLoader</c> when the CLIP comes from the same
    /// <c>CheckpointLoaderSimple</c>, else a <c>LoraLoaderModelOnly</c>. The new node's id, or null with why not.
    /// </summary>
    public static (string? Id, string? Why) AddLora(JsonObject graph, string name, double strength)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(name);
        var parts = Read(graph);
        if (parts.Loras.Why is { } why)
        {
            return (null, why);
        }

        var walk = WalkAll(graph, Nodes(graph));
        if (walk.Tail is not { } tail)
        {
            return (null, ComfyText.EditNotInGraph);
        }

        var modelUsers = Consumers(graph, tail.Id, tail.Slot);
        string? clipSource = walk.EndType == CheckpointLoader ? ClipSource(graph, parts) : null;
        var clipUsers = clipSource is null ? [] : Consumers(graph, clipSource, 1);
        string id = NewId(graph);
        var inputs = new JsonObject { ["lora_name"] = name, ["strength_model"] = strength };
        if (clipUsers.Count > 0)
        {
            inputs["strength_clip"] = strength;
            inputs["model"] = Link(tail.Id, tail.Slot);
            inputs["clip"] = Link(clipSource!, 1);
            graph[id] = Node(LoraLoader, inputs);
        }
        else
        {
            inputs["model"] = Link(tail.Id, tail.Slot);
            graph[id] = Node(LoraLoaderModelOnly, inputs);
        }

        foreach (var (users, key) in modelUsers)
        {
            users[key] = Link(id, 0);
        }

        foreach (var (users, key) in clipUsers)
        {
            users[key] = Link(id, 1);
        }

        return (id, null);
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    private static ComfyGraphPart Literal(JsonObject graph, (string Id, JsonObject Node) node, string input)
    {
        var value = InputsOf(node.Node)?[input];
        if (value is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            return new ComfyGraphPart(v.GetValue<string>(), null, node.Id, TypeOf(node.Node), input);
        }

        return value is JsonArray && Linked(graph, value) is { } source
            ? new ComfyGraphPart(null, ComfyText.EditSetBy(source.Id, TypeOf(source.Node) ?? "?"), node.Id, TypeOf(node.Node), input)
            : new ComfyGraphPart(null, ComfyText.EditNotInGraph, node.Id, TypeOf(node.Node), input);
    }

    /// <summary>The checkpoint or diffusion model: the loader the model's path ends at, else the one node that loads one.</summary>
    private static ComfyGraphPart ModelPart(JsonObject graph, List<(string Id, JsonObject Node)> nodes, Walk walk)
    {
        var loaders = nodes.Where(n => ModelKey(n.Node) is not null).ToList();
        (string Id, JsonObject Node)? end = walk.End is { } e && graph[e] is JsonObject n0 ? (e, n0) : loaders.Count == 1 ? loaders[0] : null;
        if (end is not { } loader)
        {
            return new ComfyGraphPart(null, loaders.Count > 1 ? ComfyText.EditSeveral(loaders.Count, "model loaders") : ComfyText.EditNotInGraph);
        }

        var part = Literal(graph, loader, ModelKey(loader.Node) ?? "ckpt_name");
        return TypeOf(loader.Node) is CheckpointLoader or "UNETLoader" || part.Why is not null
            ? part
            : part with { Why = ComfyText.EditOtherLoader(TypeOf(loader.Node) ?? "?") };
    }

    private static string? ModelKey(JsonObject node) =>
        InputsOf(node) is { } inputs ? inputs.ContainsKey("ckpt_name") ? "ckpt_name" : inputs.ContainsKey("unet_name") ? "unet_name" : null : null;

    private static List<(JsonObject Inputs, string Key)> VaeLinks(List<(string Id, JsonObject Node)> nodes)
    {
        var links = new List<(JsonObject, string)>();
        foreach (var (_, node) in nodes)
        {
            if (InputsOf(node) is { } inputs && inputs["vae"] is JsonArray)
            {
                links.Add((inputs, "vae"));
            }
        }

        return links;
    }

    private static (ComfyGraphPart Part, bool CheckpointVae) VaePart(JsonObject graph, List<(string Id, JsonObject Node)> nodes)
    {
        bool checkpoint = nodes.Any(n => TypeOf(n.Node) == CheckpointLoader);
        var sources = VaeLinks(nodes).Select(l => LinkOf(l.Inputs[l.Key])).Distinct().ToList();
        if (sources.Count == 0)
        {
            return (new ComfyGraphPart(null, ComfyText.EditNotInGraph), checkpoint);
        }

        if (sources.Count > 1 || sources[0] is not { } link || graph[link.Id] is not JsonObject source)
        {
            return (new ComfyGraphPart(null, ComfyText.EditSeveralVaes), checkpoint);
        }

        return TypeOf(source) switch
        {
            CheckpointLoader when link.Slot == 2 => (new ComfyGraphPart("", null, link.Id, VaeLoader), true),   // no input of its own to set (2026-11-26, #68)
            VaeLoader => (Literal(graph, (link.Id, source), "vae_name"), checkpoint),
            _ => (new ComfyGraphPart(null, ComfyText.EditSetBy(link.Id, TypeOf(source) ?? "?")), checkpoint),
        };
    }

    /// <summary>The CLIP skip: the one <c>CLIPSetLastLayer</c> on a checkpoint's CLIP ("2"), none (""), or why it can't change.</summary>
    private static ComfyGraphPart ClipSkipPart(JsonObject graph, List<(string Id, JsonObject Node)> nodes)
    {
        var skips = nodes.Where(n => TypeOf(n.Node) == ClipSetLastLayer).ToList();
        if (skips.Count > 1)
        {
            return new ComfyGraphPart(null, ComfyText.EditSeveral(skips.Count, "CLIP skips"));
        }

        if (skips.Count == 1)
        {
            var inputs = InputsOf(skips[0].Node);
            if (!FromCheckpointClip(graph, inputs?["clip"]))
            {
                return new ComfyGraphPart(null, ComfyText.EditNoCheckpointClip, skips[0].Id);
            }

            // Read as a number (2026-11-26, the review's #69): a whole -2.0 is layer -2, as ComfyUI reads it.
            return Number(inputs?["stop_at_clip_layer"]) is { } d && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue
                ? new ComfyGraphPart((int)d <= -2 ? (-(int)d).ToString(CultureInfo.InvariantCulture) : "", null, skips[0].Id)
                : new ComfyGraphPart(null, ComfyText.EditNotInGraph, skips[0].Id);
        }

        // None yet: one can go in where a checkpoint's CLIP reaches an encoder (directly or through LoraLoaders).
        bool used = nodes.Any(n => InputsOf(n.Node) is { } inputs && inputs.Any(p => p.Value is JsonArray && TypeOf(n.Node) != LoraLoader && FromCheckpointClip(graph, p.Value) && LinkOf(p.Value)?.Slot == 1));
        return used ? new ComfyGraphPart("", null) : new ComfyGraphPart(null, ComfyText.EditNoCheckpointClip);
    }

    /// <summary>Whether a CLIP link comes from a <c>CheckpointLoaderSimple</c>, directly or through <c>LoraLoader</c>s.</summary>
    private static bool FromCheckpointClip(JsonObject graph, JsonNode? link)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (Linked(graph, link) is { } source && seen.Add(source.Id))
        {
            switch (TypeOf(source.Node))
            {
                case CheckpointLoader:
                    return true;
                case LoraLoader:
                    link = InputsOf(source.Node)?["clip"];
                    continue;
                default:
                    return false;
            }
        }

        return false;
    }

    /// <summary>The node whose CLIP output (1) the encoders see on a checkpoint graph: the last <c>LoraLoader</c> on the model's path, else the checkpoint.</summary>
    private static string? ClipSource(JsonObject graph, ComfyGraphParts parts)
    {
        var last = parts.Loras.Loras.LastOrDefault(l => !l.SwitchedOff && l.ClassType == LoraLoader && graph[l.Node] is JsonObject n && FromCheckpointClip(graph, InputsOf(n)?["clip"]));
        if (last is not null)
        {
            return last.Node;
        }

        return Nodes(graph).FirstOrDefault(n => TypeOf(n.Node) == CheckpointLoader).Id;
    }

    private static ComfyGraphPart OutputPart(JsonObject graph, List<(string Id, JsonObject Node)> nodes)
    {
        var saves = nodes.Where(n => TypeOf(n.Node) == SaveImage).ToList();
        return saves.Count switch
        {
            1 => Literal(graph, saves[0], "filename_prefix"),
            0 => new ComfyGraphPart(null, ComfyText.EditNotInGraph),
            _ => new ComfyGraphPart(null, ComfyText.EditSeveral(saves.Count, "SaveImage nodes")),
        };
    }

    // ── The model's path ─────────────────────────────────────────────────────

    /// <summary>What the walks back from the samplers found: the LoRAs (nearest the loader first), the loader, the last link before the samplers, and why add and remove are read-only.</summary>
    private sealed record Walk(List<ComfyLora> Loras, string? End, string? EndType, int EndSlot, string? Why)
    {
        /// <summary>Where a new LoRA goes: after the last live LoRA (nearest the sampler), else after the loader.</summary>
        public (string Id, int Slot)? Tail =>
            Loras.LastOrDefault(l => !l.SwitchedOff) is { } last ? (last.Node, 0) : End is { } end ? (end, EndSlot) : null;
    }

    private static Walk WalkAll(JsonObject graph, List<(string Id, JsonObject Node)> nodes)
    {
        var starts = new List<JsonNode?>();
        foreach (var (_, node) in nodes)
        {
            var inputs = InputsOf(node);
            switch (TypeOf(node))
            {
                case "KSampler" or "KSamplerAdvanced" or "SamplerCustom":
                    starts.Add(inputs?["model"]);
                    break;
                case "SamplerCustomAdvanced":
                    starts.Add(Linked(graph, inputs?["guider"]) is { } guider ? InputsOf(guider.Node)?["model"] : null);
                    break;
            }
        }

        if (starts.Count == 0)
        {
            return new Walk([], null, null, 0, ComfyText.EditNoSampler);
        }

        Walk? first = null;
        var budget = new int[] { (4 * graph.Count) + 16 };   // shared by every walk, the switches' off branches too
        foreach (var start in starts)
        {
            var walk = WalkOne(graph, start, null, budget);
            if (first is null)
            {
                first = walk;
                continue;
            }

            bool same = walk.End == first.End && walk.Loras.Select(l => l.Node).SequenceEqual(first.Loras.Select(l => l.Node), StringComparer.Ordinal);
            if (!same)
            {
                // Ends that differ name no one loader (2026-11-26, the review's #37: a base and a refiner), so the Model row says the loaders' count.
                return first with { End = walk.End == first.End ? first.End : null, Why = first.Why ?? ComfyText.EditSamplersDiffer, Loras = first.Loras.Select(l => l with { Why = l.Why ?? ComfyText.EditSamplersDiffer }).ToList() };
            }

            first = first with { Why = first.Why ?? walk.Why };
        }

        return first!;
    }

    /// <summary>
    /// One sampler's model path back to its loader. A switch's off branch is walked with a copy of <paramref name="seenBefore"/> (2026-11-26, the
    /// review's #91: a shared set would call the two branches meeting at the loader a loop, and none let a self- or cross-linked switch recurse
    /// for ever); <paramref name="budget"/>, shared by the whole walk, ends a long chain of switches as a loop rather than doubling per switch.
    /// </summary>
    private static Walk WalkOne(JsonObject graph, JsonNode? link, HashSet<string>? seenBefore, int[] budget)
    {
        var loras = new List<ComfyLora>();
        var seen = seenBefore is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(seenBefore, StringComparer.Ordinal);
        string? why = null;
        bool switched = false;
        while (LinkOf(link) is { } at)
        {
            if (--budget[0] < 0 || !seen.Add(at.Id))
            {
                why ??= ComfyText.EditLoop;
                break;
            }

            if (graph[at.Id] is not JsonObject node)
            {
                why ??= ComfyText.EditNotInGraph;
                break;
            }

            var inputs = InputsOf(node);
            string type = TypeOf(node) ?? "?";
            if (ModelKey(node) is not null && inputs?["model"] is not JsonArray)
            {
                return new Walk(Ordered(loras), at.Id, type, at.Slot, why ?? (switched ? ComfyText.EditBehindSwitch : null));
            }

            if (type == Switch)
            {
                if (SwitchValue(graph, inputs) is not { } on)
                {
                    why ??= ComfyText.EditSwitchUnknown(at.Id);
                    break;
                }

                switched = true;
                // The branch left unused: its LoRAs shown, switched off.
                var offWalk = WalkOne(graph, inputs?[on ? "on_false" : "on_true"], seen, budget);
                foreach (var off in offWalk.Loras.Where(l => !l.SwitchedOff))
                {
                    loras.Add(off with { SwitchedOff = true, Why = ComfyText.EditSwitchedOff });
                }

                if (offWalk.Why == ComfyText.EditLoop)
                {
                    why ??= ComfyText.EditLoop;
                }

                link = inputs?[on ? "on_true" : "on_false"];
                continue;
            }

            if (inputs?["lora_name"] is { } name)
            {
                bool plain = type is LoraLoader or LoraLoaderModelOnly;
                string? file = name is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
                string? loraWhy = !plain ? ComfyText.EditOtherLoraNode(type)
                    : file is null ? Linked(graph, name) is { } by ? ComfyText.EditSetBy(by.Id, TypeOf(by.Node) ?? "?") : ComfyText.EditNotInGraph
                    : !IsNumber(inputs["strength_model"]) ? ComfyText.EditStrengthNotLiteral
                    : null;
                loras.Add(new ComfyLora(at.Id, type, file ?? "", Number(inputs["strength_model"]), Number(inputs["strength_clip"]), false, loraWhy));
                if (!plain || file is null)
                {
                    why ??= loraWhy;
                }

                if (inputs["model"] is JsonArray next)
                {
                    link = next;
                    continue;
                }

                why ??= ComfyText.EditPathEnds(at.Id, type);
                break;
            }

            if (inputs?["model"] is JsonArray through)
            {
                link = through;
                continue;
            }

            why ??= ComfyText.EditPathEnds(at.Id, type);
            break;
        }

        why ??= ComfyText.EditNotInGraph;
        return new Walk(Ordered(loras), null, null, 0, why);
    }

    /// <summary>The walk meets the LoRAs nearest the sampler first: listed nearest the loader first, the switched-off ones after.</summary>
    private static List<ComfyLora> Ordered(List<ComfyLora> found)
    {
        var live = found.Where(l => !l.SwitchedOff).Reverse().ToList();
        var nodes = live.Select(l => l.Node).ToHashSet(StringComparer.Ordinal);
        return [.. live, .. found.Where(l => l.SwitchedOff && !nodes.Contains(l.Node)).DistinctBy(l => l.Node)];
    }

    /// <summary>A switch's <c>switch</c>: a literal boolean, or a <c>PrimitiveBoolean</c>'s literal; null otherwise.</summary>
    private static bool? SwitchValue(JsonObject graph, JsonObject? inputs)
    {
        var value = inputs?["switch"];
        if (value is JsonArray && Linked(graph, value) is { } source && TypeOf(source.Node) == "PrimitiveBoolean")
        {
            value = InputsOf(source.Node)?["value"];
        }

        return value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private static List<(string Id, JsonObject Node)> Nodes(JsonObject graph) =>
        graph.Where(p => p.Value is JsonObject).Select(p => (p.Key, (JsonObject)p.Value!)).ToList();

    /// <summary>A node's inputs, or null; never made where missing (<c>ComfyImport.Inputs</c> adds one, so it isn't used here).</summary>
    private static JsonObject? InputsOf(JsonObject? node) => node?["inputs"] as JsonObject;

    private static string? TypeOf(JsonObject node) => ComfyImport.TypeOf(node);

    /// <summary>A link's node id and output, or null; a numeric id is read as its text.</summary>
    private static (string Id, int Slot)? LinkOf(JsonNode? link)
    {
        if (link is not JsonArray { Count: 2 } pair || pair[0] is not JsonValue idValue || pair[1] is not JsonValue slotValue || slotValue.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        string id = idValue.GetValueKind() == JsonValueKind.String ? idValue.GetValue<string>() : idValue.ToJsonString();
        return int.TryParse(slotValue.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot) ? (id, slot) : null;
    }

    private static (string Id, JsonObject Node)? Linked(JsonObject graph, JsonNode? link) =>
        LinkOf(link) is { } at && graph[at.Id] is JsonObject node ? (at.Id, node) : null;

    /// <summary>Every input that links to <paramref name="id"/>'s output <paramref name="slot"/>.</summary>
    private static List<(JsonObject Inputs, string Key)> Consumers(JsonObject graph, string id, int slot)
    {
        var found = new List<(JsonObject, string)>();
        foreach (var (_, node) in Nodes(graph))
        {
            if (InputsOf(node) is not { } inputs)
            {
                continue;
            }

            foreach (var (key, value) in inputs)
            {
                if (LinkOf(value) is { } at && at.Id == id && at.Slot == slot)
                {
                    found.Add((inputs, key));
                }
            }
        }

        return found;
    }

    private static void RemoveIfUnused(JsonObject graph, string? id, string type)
    {
        if (id is not null && graph[id] is JsonObject node && TypeOf(node) == type
            && !Nodes(graph).Any(n => InputsOf(n.Node) is { } inputs && inputs.Any(p => LinkOf(p.Value) is { } at && at.Id == id)))
        {
            graph.Remove(id);
        }
    }

    /// <summary>The next node id: the highest whole-number id plus one (ids like <c>57:30</c> are passed over).</summary>
    public static string NewId(JsonObject graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        long top = 0;
        foreach (var (key, _) in graph)
        {
            if (long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out long n) && n > top)
            {
                top = n;
            }
        }

        return (top + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsNumber(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number;

    private static double? Number(JsonNode? node) =>
        IsNumber(node) && double.TryParse(node!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

    private static JsonObject Node(string type, JsonObject inputs) => new() { ["class_type"] = type, ["inputs"] = inputs };

    // The params constructor, not a collection expression: that one binds JsonArray.Add<T>, which AOT flags (IL2026/IL3050).
    private static JsonArray Link(string node, int output) => new(JsonValue.Create(node), JsonValue.Create(output));
}
