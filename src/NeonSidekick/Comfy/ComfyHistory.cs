using System.Text.Json;
using System.Text.Json.Nodes;

namespace NeonSidekick.Comfy;

/// <summary>
/// One run the server remembers, ready to import: its graph placeholdered (<see cref="Import"/>, or <see cref="Problem"/> when the import
/// refuses it), when it started (Unix milliseconds, null when the server kept no time), whether the app queued it (<see cref="Own"/>), its first
/// output file, and how many runs of the same workflow it stands for (<see cref="Repeats"/>, the newest kept).
/// </summary>
public sealed record ComfyRun(string Id, string GraphJson, long? StartedMs, bool Own, string? OutputFile, ComfyImportResult? Import, string? Problem, int Repeats)
{
    /// <summary>The checkpoint or diffusion model the run used, or empty.</summary>
    public string Model => Import?.Checkpoint ?? "";
}

/// <summary>
/// <c>GET /history</c> read for the import from a recent run (2026-11-25, the workflow manager; the shape checked in ComfyUI's source that day:
/// each entry's <c>prompt</c> is a tuple whose third item is the graph it ran and fourth its <c>extra_data</c>, with <c>client_id</c> and, in newer
/// servers, <c>create_time</c>; <c>status</c> holds <c>status_str</c> and the <c>execution_start</c> message with its timestamp). Newest first; a
/// failed run left out; the same workflow once, compared on the placeholdered graph without its <c>filename_prefix</c>. The app's own runs are
/// marked by the client id prefix <see cref="OwnClientPrefix"/>, older ones by a <c>neon/</c> output prefix. Never throws on an odd answer. Pure.
/// </summary>
public static class ComfyHistory
{
    /// <summary>The start of the client id the app queues its runs under.</summary>
    public const string OwnClientPrefix = "neonsidekick-";

    /// <summary>How many runs are asked for, and how many when that answer is too large.</summary>
    public const int MaxItems = 100;

    public const int FewerItems = 20;

    /// <summary>The largest <c>/history</c> answer read (runs queued from ComfyUI carry its whole UI graph).</summary>
    public const int MaxAnswerBytes = 32 * 1024 * 1024;

    /// <summary>The runs in <paramref name="json"/>, newest first, deduplicated; empty for an answer that isn't one.</summary>
    public static IReadOnlyList<ComfyRun> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(ComfyImport.NumbersAsNull(json), documentOptions: ComfyWorkflow.Tolerant) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return [];
        }

        if (root is null)
        {
            return [];
        }

        var runs = new List<(ComfyRun Run, string Key, long Number)>();
        foreach (var (id, value) in root)
        {
            if (value is not JsonObject entry || entry["prompt"] is not JsonArray tuple || tuple.Count < 3 || tuple[2] is not JsonObject graph
                || Text((entry["status"] as JsonObject)?["status_str"]) == "error")   // an entry of another shape is skipped, never a throw (2026-11-26, #17)
            {
                continue;
            }

            var extra = tuple.Count > 3 ? tuple[3] as JsonObject : null;
            long number = Number(tuple[0]) ?? 0;
            long? started = Number(extra?["create_time"]) ?? StartMessage(entry["status"]);
            string graphJson = graph.ToJsonString();
            bool own = Text(extra?["client_id"]).StartsWith(OwnClientPrefix, StringComparison.Ordinal) || OwnPrefix(graph);
            var (import, problem) = ComfyImport.Placehold(graphJson);
            string key = import is null ? graphJson : WithoutPrefix(import.GraphJson);
            runs.Add((new ComfyRun(id, graphJson, started, own, FirstOutput(entry["outputs"]), import, problem, 1), key, number));
        }

        var newest = runs.OrderByDescending(r => r.Run.StartedMs ?? long.MinValue).ThenByDescending(r => r.Number).ToList();
        var kept = new List<ComfyRun>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (run, key, _) in newest)
        {
            if (seen.TryGetValue(key, out int at))
            {
                kept[at] = kept[at] with { Repeats = kept[at].Repeats + 1, Own = kept[at].Own && run.Own };
                continue;
            }

            seen[key] = kept.Count;
            kept.Add(run);
        }

        return kept;
    }

    private static string Text(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";

    private static long? Number(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out double d) && double.IsFinite(d) ? (long)d : null;

    /// <summary>The <c>execution_start</c> message's timestamp (milliseconds), or null.</summary>
    private static long? StartMessage(JsonNode? status)
    {
        if ((status as JsonObject)?["messages"] is not JsonArray messages)
        {
            return null;
        }

        foreach (var message in messages)
        {
            if (message is JsonArray { Count: >= 2 } pair && Text(pair[0]) == "execution_start")
            {
                return Number((pair[1] as JsonObject)?["timestamp"]);
            }
        }

        return null;
    }

    /// <summary>Whether a node's literal <c>filename_prefix</c> starts with <c>neon/</c> (the app's own output folder, before the client id prefix).</summary>
    private static bool OwnPrefix(JsonObject graph) =>
        graph.Any(p => p.Value is JsonObject node && node["inputs"] is JsonObject inputs
            && Text(inputs["filename_prefix"]).StartsWith(ComfyGraphs.PrefixFolder, StringComparison.Ordinal));

    /// <summary>The placeholdered graph without its <c>filename_prefix</c> values, as the key two runs of one workflow share.</summary>
    private static string WithoutPrefix(string graphJson)
    {
        try
        {
            if (JsonNode.Parse(graphJson) is not JsonObject graph)
            {
                return graphJson;
            }

            foreach (var (_, node) in graph)
            {
                (node?["inputs"] as JsonObject)?.Remove("filename_prefix");
            }

            return graph.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return graphJson;
        }
    }

    /// <summary>The first output file a run made (its <c>images</c>, <c>gifs</c> or any list of files), or null.</summary>
    private static string? FirstOutput(JsonNode? outputs)
    {
        if (outputs is not JsonObject nodes)
        {
            return null;
        }

        foreach (var (_, node) in nodes)
        {
            if (node is not JsonObject lists)
            {
                continue;
            }

            foreach (var (_, list) in lists)
            {
                if (list is JsonArray files && files.Count > 0 && files[0] is JsonObject file && Text(file["filename"]) is { Length: > 0 } name)
                {
                    string folder = Text(file["subfolder"]);
                    return folder.Length > 0 ? folder + "/" + name : name;
                }
            }
        }

        return null;
    }
}
