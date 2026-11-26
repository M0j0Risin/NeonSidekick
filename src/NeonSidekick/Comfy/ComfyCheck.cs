using System.Text.Json;
using System.Text.Json.Nodes;

namespace NeonSidekick.Comfy;

/// <summary>
/// One input as <c>/object_info</c> describes it: <see cref="Choices"/> when it is a combo whose options are all plain text (an empty list is a
/// list: nothing installed), else null; <see cref="Skip"/> when the list isn't the whole story (an upload input, a remote list, options that
/// aren't all text), so a value off it proves nothing.
/// </summary>
public sealed record ComfyInputSpec(IReadOnlyList<string>? Choices, bool Skip);

/// <summary>What <c>/object_info/&lt;class&gt;</c> answered: <see cref="Missing"/> when the server has no such node (ComfyUI answers <c>{}</c>), else its inputs by name.</summary>
public sealed record ComfyNodeInfo(bool Missing, IReadOnlyDictionary<string, ComfyInputSpec> Inputs)
{
    public static readonly ComfyNodeInfo Unknown = new(true, new Dictionary<string, ComfyInputSpec>());
}

/// <summary>
/// The server check (2026-11-25, the workflow manager): a workflow's graph held against the server's <c>/object_info</c> for each node class
/// it uses — a class the server lacks, a literal combo value (a checkpoint, a LoRA, a VAE, a sampler …) not on its list. Placeholders, links,
/// array values, upload inputs, remote lists and lists the server doesn't give whole are skipped; an input the server doesn't name is no
/// finding (a custom node may take more). Nothing here ever throws on an odd answer or graph. Pure.
/// </summary>
public static class ComfyCheck
{
    /// <summary>The largest <c>/object_info/&lt;class&gt;</c> answer read.</summary>
    public const int MaxAnswerBytes = 4 * 1024 * 1024;

    /// <summary>How many classes are asked about at once.</summary>
    public const int Parallel = 4;

    /// <summary>The node classes <paramref name="graph"/> uses, each once, in order.</summary>
    public static IReadOnlyList<string> Classes(JsonObject graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var seen = new List<string>();
        foreach (var (_, node) in graph)
        {
            if (node is JsonObject o && o["class_type"] is JsonValue v && v.TryGetValue(out string? type) && type.Length > 0 && !seen.Contains(type, StringComparer.Ordinal))
            {
                seen.Add(type);
            }
        }

        return seen;
    }

    /// <summary>An <c>/object_info/&lt;class&gt;</c> answer read, or null when it isn't one (not JSON, not an object, the class not an object).</summary>
    public static ComfyNodeInfo? ParseNode(string json, string nodeClass)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(nodeClass);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty(nodeClass, out var node))
            {
                return ComfyNodeInfo.Unknown;
            }

            if (node.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var inputs = new Dictionary<string, ComfyInputSpec>(StringComparer.Ordinal);
            if (node.TryGetProperty("input", out var groups) && groups.ValueKind == JsonValueKind.Object)
            {
                foreach (string group in (string[])["required", "optional"])
                {
                    if (groups.TryGetProperty(group, out var set) && set.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var input in set.EnumerateObject())
                        {
                            inputs.TryAdd(input.Name, Spec(input.Value));
                        }
                    }
                }
            }

            return new ComfyNodeInfo(false, inputs);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One input's spec: <c>[["a","b"], {…}]</c> or <c>["COMBO", {"options": ["a","b"]}]</c> a combo; anything else none.</summary>
    private static ComfyInputSpec Spec(JsonElement spec)
    {
        if (spec.ValueKind != JsonValueKind.Array || spec.GetArrayLength() == 0)
        {
            return new ComfyInputSpec(null, false);
        }

        var first = spec[0];
        var options = spec.GetArrayLength() > 1 && spec[1].ValueKind == JsonValueKind.Object ? spec[1] : default;
        bool skip = options.ValueKind == JsonValueKind.Object
            && ((options.TryGetProperty("image_upload", out var upload) && upload.ValueKind == JsonValueKind.True)
                || options.TryGetProperty("remote", out _)
                || (options.TryGetProperty("multi_select", out var multi) && multi.ValueKind is JsonValueKind.True or JsonValueKind.Object));
        var list = first.ValueKind == JsonValueKind.Array ? first
            : options.ValueKind == JsonValueKind.Object && options.TryGetProperty("options", out var listed) && listed.ValueKind == JsonValueKind.Array ? listed
            : default;
        if (list.ValueKind != JsonValueKind.Array)
        {
            return new ComfyInputSpec(null, false);
        }

        var choices = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return new ComfyInputSpec(choices, true);   // options that are objects (a dynamic combo): no plain list to hold a value to
            }

            choices.Add(item.GetString()!);
        }

        return new ComfyInputSpec(choices, skip);
    }

    /// <summary>
    /// What <paramref name="graph"/> asks of the server that <paramref name="info"/> (by class; null for a class whose answer couldn't be read)
    /// says it lacks, one sentence each, by node id order.
    /// </summary>
    public static IReadOnlyList<string> Findings(JsonObject graph, IReadOnlyDictionary<string, ComfyNodeInfo?> info)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(info);
        var found = new List<string>();
        foreach (var (id, node) in graph)
        {
            if (node is not JsonObject o || o["class_type"] is not JsonValue v || !v.TryGetValue(out string? type) || info.GetValueOrDefault(type) is not { } known)
            {
                continue;
            }

            if (known.Missing)
            {
                found.Add(ComfyText.CheckNoNode(id, type));
                continue;
            }

            if (o["inputs"] is not JsonObject inputs)
            {
                continue;
            }

            foreach (var (input, value) in inputs)
            {
                if (value is not JsonValue literal || !literal.TryGetValue(out string? text) || text.Contains("{{", StringComparison.Ordinal)
                    || known.Inputs.GetValueOrDefault(input) is not { Choices: { } choices, Skip: false })
                {
                    continue;
                }

                if (!choices.Contains(text, StringComparer.Ordinal))
                {
                    found.Add(ComfyText.CheckNotListed(id, type, input, text));
                }
            }
        }

        return found;
    }
}
