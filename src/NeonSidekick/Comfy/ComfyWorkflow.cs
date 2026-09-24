using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NeonSidekick.Comfy;

/// <summary>
/// What one generation fills a template with. Null numbers fall back to the sidecar's value, then the family's
/// (<see cref="ComfyFamilies.Defaults"/>); <see cref="Image"/> is the name ComfyUI gave the uploaded input picture.
/// </summary>
public sealed record ComfyValues(string Prompt, string Negative, long Seed, int? Width = null, int? Height = null, int? Steps = null, double? Cfg = null, double? Denoise = null, string? Image = null);

/// <summary>
/// One ComfyUI workflow the image tools can run (2026-09-24): a graph the user exported from ComfyUI with
/// <b>Save (API)</b> — the API format, a JSON object of numbered nodes each with a <c>class_type</c> and
/// <c>inputs</c> — dropped into a <c>comfy</c> folder (<see cref="ComfyWorkflowCatalog"/>), with placeholders where
/// the call's values go. A string value that is exactly <c>{{seed}}</c>, <c>{{width}}</c>, <c>{{height}}</c>,
/// <c>{{steps}}</c>, <c>{{cfg}}</c> or <c>{{denoise}}</c> becomes that number; <c>{{prompt}}</c>, <c>{{negative}}</c>
/// and <c>{{image}}</c> are replaced wherever they stand in a string (so <c>"{{prompt}}, masterpiece"</c> keeps its
/// tail). A workflow holding <c>{{image}}</c> takes an input picture (img2img, upscale, inpaint).
///
/// <para>A sidecar <c>&lt;name&gt;.md</c> beside it is optional: YAML-ish <c>key: value</c> lines between two
/// <c>---</c> fences — <c>description</c>, <c>family</c> (<see cref="ComfyFamilies.Names"/>), <c>width</c>,
/// <c>height</c>, <c>steps</c>, <c>cfg</c>, <c>denoise</c>, <c>negative</c> — and, under them, free prompt tips the
/// tool description carries. The graph is kept as text and parsed afresh for every fill, so no call sees another's
/// values. Pure apart from <see cref="TryLoad"/>'s two reads.</para>
/// </summary>
public sealed record ComfyWorkflow(
    string Name,
    string FilePath,
    string GraphJson,
    IReadOnlySet<string> Placeholders,
    string Description = "",
    ComfyFamily Family = ComfyFamily.Other,
    int? Width = null,
    int? Height = null,
    int? Steps = null,
    double? Cfg = null,
    double? Denoise = null,
    string? Negative = null,
    string Tips = "",
    bool Reinforce = true)
{
    public const string PromptKey = "prompt";
    public const string NegativeKey = "negative";
    public const string ImageKey = "image";
    public const string SeedKey = "seed";
    public const string WidthKey = "width";
    public const string HeightKey = "height";
    public const string StepsKey = "steps";
    public const string CfgKey = "cfg";
    public const string DenoiseKey = "denoise";

    /// <summary>The placeholders whose whole-string form becomes a number.</summary>
    public static readonly IReadOnlySet<string> NumericKeys = new HashSet<string>(StringComparer.Ordinal) { SeedKey, WidthKey, HeightKey, StepsKey, CfgKey, DenoiseKey };

    /// <summary>The strength an image workflow's <c>{{denoise}}</c> gets when neither call nor sidecar sets one: enough to restyle, not so much the picture is lost.</summary>
    public const double DefaultImageDenoise = 0.6;

    /// <summary>The problems a file can have, pinned: the catalog shows them beside the name.</summary>
    public const string NotJsonProblem = "is not JSON";
    public const string UiFormatProblem = "is a UI-format save (it has \"nodes\" and \"links\"); in ComfyUI use Workflow → Export (API), or enable dev mode and Save (API)";
    public const string NotApiFormatProblem = "is no API-format workflow: expected an object of nodes, each with a class_type";
    public const string NoPromptProblem = "has no {{prompt}} placeholder, so nothing the call says would reach the picture";

    /// <summary>Whether reinforcing tags may be appended to this workflow's negative: it has a <c>{{negative}}</c>, its family uses one, and its sidecar did not say <c>reinforce: false</c>.</summary>
    public bool TakesReinforcement => Reinforce && Placeholders.Contains(NegativeKey) && ComfyFamilies.UsesNegative(Family);

    /// <summary>Whether the workflow takes an input picture: it holds <c>{{image}}</c>.</summary>
    public bool TakesImage => Placeholders.Contains(ImageKey);

    /// <summary>The fallbacks in force: the sidecar's numbers over the family's.</summary>
    public ComfyDefaults Defaults
    {
        get
        {
            var family = ComfyFamilies.Defaults(Family);
            return new ComfyDefaults(Width ?? family.Width, Height ?? family.Height, Steps ?? family.Steps, Cfg ?? family.Cfg, Negative ?? family.Negative);
        }
    }

    /// <summary>
    /// Reads <paramref name="jsonPath"/> and its sidecar (same name, <c>.md</c>). True with the workflow; false with
    /// a problem sentence (<see cref="NotJsonProblem"/>, <see cref="UiFormatProblem"/>, …) or the IO failure's message.
    /// The name is the file's name without <c>.json</c>.
    /// </summary>
    public static bool TryLoad(string jsonPath, out ComfyWorkflow? workflow, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(jsonPath);
        workflow = null;
        string json;
        string? sidecar;
        try
        {
            json = File.ReadAllText(jsonPath);
            string md = Path.ChangeExtension(jsonPath, ".md");
            sidecar = File.Exists(md) ? File.ReadAllText(md) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return false;
        }

        return TryParse(Path.GetFileNameWithoutExtension(jsonPath), jsonPath, json, sidecar, out workflow, out problem);
    }

    /// <summary><see cref="TryLoad"/> over text already read. Pure.</summary>
    public static bool TryParse(string name, string filePath, string json, string? sidecar, out ComfyWorkflow? workflow, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(json);
        workflow = null;
        problem = CheckGraph(json);
        if (problem is not null)
        {
            return false;
        }

        var placeholders = FindPlaceholders(json);
        if (!placeholders.Contains(PromptKey))
        {
            problem = NoPromptProblem;
            return false;
        }

        var head = sidecar is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : ReadSidecar(sidecar);
        string body = sidecar is null ? "" : SidecarBody(sidecar);
        var family = head.TryGetValue("family", out string? word) && ComfyFamilies.TryParse(word, out var named) ? named : ComfyFamilies.Guess(name);
        workflow = new ComfyWorkflow(
            name,
            filePath,
            json,
            placeholders,
            head.TryGetValue("description", out string? description) ? description : "",
            family,
            Int(head, WidthKey),
            Int(head, HeightKey),
            Int(head, StepsKey),
            Number(head, CfgKey),
            Number(head, DenoiseKey),
            head.TryGetValue(NegativeKey, out string? negative) ? negative : null,
            body,
            // reinforce: false (later still on 2026-09-24): this workflow's negative is sent as written, nothing appended.
            !(head.TryGetValue("reinforce", out string? reinforce) && reinforce.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)));
        return true;
    }

    /// <summary>Null when <paramref name="json"/> is an API-format graph, else the problem.</summary>
    private static string? CheckGraph(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return NotJsonProblem;
        }

        if (root is not JsonObject nodes || nodes.Count == 0)
        {
            return NotApiFormatProblem;
        }

        if (nodes.ContainsKey("nodes") && nodes.ContainsKey("links"))
        {
            return UiFormatProblem;
        }

        foreach (var (_, node) in nodes)
        {
            if (node is not JsonObject body || body["class_type"] is not JsonValue type || type.GetValueKind() != JsonValueKind.String)
            {
                return NotApiFormatProblem;
            }
        }

        return null;
    }

    /// <summary>Every <c>{{name}}</c> in the text (the name lower-cased letters, digits and underscores). Pure.</summary>
    public static IReadOnlySet<string> FindPlaceholders(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new HashSet<string>(StringComparer.Ordinal);
        int at = 0;
        while ((at = text.IndexOf("{{", at, StringComparison.Ordinal)) >= 0)
        {
            int end = text.IndexOf("}}", at + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            string name = text[(at + 2)..end].Trim();
            if (name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                found.Add(name.ToLowerInvariant());
            }

            at = end + 2;
        }

        return found;
    }

    /// <summary>
    /// The graph with <paramref name="values"/> in its placeholders, ready for <c>POST /prompt</c>: a fresh parse, so
    /// the template is never touched. A numeric placeholder the values leave null takes <see cref="Defaults"/>
    /// (<c>{{denoise}}</c> the sidecar's, or <see cref="DefaultImageDenoise"/> for an image workflow, 1 otherwise); an
    /// unknown <c>{{name}}</c> is left as it is.
    /// </summary>
    public JsonObject Fill(ComfyValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var graph = (JsonObject)JsonNode.Parse(GraphJson, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })!;
        var defaults = Defaults;
        var numbers = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            [SeedKey] = JsonValue.Create(values.Seed),
            [WidthKey] = JsonValue.Create(values.Width ?? defaults.Width),
            [HeightKey] = JsonValue.Create(values.Height ?? defaults.Height),
            [StepsKey] = JsonValue.Create(values.Steps ?? defaults.Steps),
            [CfgKey] = JsonValue.Create(values.Cfg ?? defaults.Cfg),
            [DenoiseKey] = JsonValue.Create(values.Denoise ?? Denoise ?? (TakesImage ? DefaultImageDenoise : 1.0)),
        };
        var texts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PromptKey] = values.Prompt,
            [NegativeKey] = values.Negative,
            [ImageKey] = values.Image ?? "",
        };
        foreach (var (key, number) in numbers)
        {
            texts[key] = number.ToJsonString();
        }

        Walk(graph, numbers, texts);
        return graph;
    }

    private static void Walk(JsonNode node, Dictionary<string, JsonNode> numbers, Dictionary<string, string> texts)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(p => p.Key).ToList())
                {
                    if (obj[key] is { } child && Replace(child, numbers, texts) is { } replaced)
                    {
                        obj[key] = replaced;
                    }
                    else if (obj[key] is { } inner)
                    {
                        Walk(inner, numbers, texts);
                    }
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } child && Replace(child, numbers, texts) is { } replaced)
                    {
                        array[i] = replaced;
                    }
                    else if (array[i] is { } inner)
                    {
                        Walk(inner, numbers, texts);
                    }
                }

                break;
        }
    }

    /// <summary>The node that stands in for a string holding a placeholder, or null to leave it (and walk it, if it is a container).</summary>
    private static JsonNode? Replace(JsonNode node, Dictionary<string, JsonNode> numbers, Dictionary<string, string> texts)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }

        string text = value.GetValue<string>();
        if (!text.Contains("{{", StringComparison.Ordinal))
        {
            return null;
        }

        string whole = text.Trim();
        if (whole.StartsWith("{{", StringComparison.Ordinal) && whole.EndsWith("}}", StringComparison.Ordinal))
        {
            string key = whole[2..^2].Trim().ToLowerInvariant();
            if (numbers.TryGetValue(key, out var number))
            {
                return number.DeepClone();
            }
        }

        var sb = new StringBuilder(text.Length + 64);
        int at = 0;
        while (at < text.Length)
        {
            int open = text.IndexOf("{{", at, StringComparison.Ordinal);
            int close = open < 0 ? -1 : text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (open < 0 || close < 0)
            {
                sb.Append(text, at, text.Length - at);
                break;
            }

            sb.Append(text, at, open - at);
            string raw = text[(open + 2)..close].Trim();
            string key = raw.ToLowerInvariant();
            if (raw.StartsWith('!'))
            {
                // An escape (later still on 2026-09-24): {{!width}} is the literal {{width}} — a workflow's own text (Ideogram 4's StringReplace), never a placeholder.
                sb.Append("{{").Append(raw, 1, raw.Length - 1).Append("}}");
            }
            else
            {
                sb.Append(texts.TryGetValue(key, out string? fill) ? fill : text[open..(close + 2)]);
            }
            at = close + 2;
        }

        return JsonValue.Create(sb.ToString());
    }

    /// <summary>The sidecar's <c>key: value</c> lines between its fences, the keys case-insensitive; none without a leading fence.</summary>
    private static Dictionary<string, string> ReadSidecar(string text)
    {
        var head = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = Fold(text).Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd() != Skills.SkillFrontmatter.Fence)
        {
            return head;
        }

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimEnd() == Skills.SkillFrontmatter.Fence)
            {
                break;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (line.TrimStart().StartsWith('#') || colon <= 0)
            {
                continue;
            }

            head[line[..colon].Trim()] = Skills.SkillFrontmatter.Unquote(line[(colon + 1)..].Trim());
        }

        return head;
    }

    /// <summary>What follows the sidecar's closing fence, trimmed — the whole text when it has no fences (plain tips).</summary>
    private static string SidecarBody(string text)
    {
        string[] lines = Fold(text).Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd() != Skills.SkillFrontmatter.Fence)
        {
            return Fold(text).Trim();
        }

        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == Skills.SkillFrontmatter.Fence)
            {
                return string.Join('\n', lines.Skip(i + 1)).Trim();
            }
        }

        return "";
    }

    private static string Fold(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿');

    private static int? Int(Dictionary<string, string> head, string key) =>
        head.TryGetValue(key, out string? raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0 ? value : null;

    private static double? Number(Dictionary<string, string> head, string key) =>
        head.TryGetValue(key, out string? raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value >= 0 ? value : null;
}
