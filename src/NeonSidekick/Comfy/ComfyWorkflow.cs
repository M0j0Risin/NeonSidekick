using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NeonSidekick.Comfy;

/// <summary>
/// What one generation fills a template with. Null numbers fall back to the sidecar's value, then the family's
/// (<see cref="ComfyFamilies.Defaults"/>); <see cref="Images"/> are the names ComfyUI gave the uploaded input pictures,
/// in slot order — <c>{{image}}</c>, <c>{{image2}}</c>, <c>{{image3}}</c>.
/// </summary>
public sealed record ComfyValues(string Prompt, string Negative, long Seed, int? Width = null, int? Height = null, int? Steps = null, double? Cfg = null, double? Denoise = null, IReadOnlyList<string>? Images = null);

/// <summary>
/// What a filled graph sends ComfyUI beside the prompts (2026-09-25, the user's ask: the rest of the params under
/// <c>ComfyUI show prompts</c>), read by <see cref="ComfyWorkflow.ReadParameters"/>; null where the graph has none.
/// </summary>
public sealed record ComfyParameters(int? Width = null, int? Height = null, int? Steps = null, double? Cfg = null, double? Denoise = null, long? Seed = null, string? Sampler = null, string? Scheduler = null);

/// <summary>
/// One ComfyUI workflow the image tools can run (2026-09-24): a graph the user exported from ComfyUI with
/// <b>Save (API)</b> — the API format, a JSON object of numbered nodes each with a <c>class_type</c> and
/// <c>inputs</c> — dropped into a <c>comfy</c> folder (<see cref="ComfyWorkflowCatalog"/>), with placeholders where
/// the call's values go. A string value that is exactly <c>{{seed}}</c>, <c>{{width}}</c>, <c>{{height}}</c>,
/// <c>{{steps}}</c>, <c>{{cfg}}</c> or <c>{{denoise}}</c> becomes that number; <c>{{prompt}}</c>, <c>{{negative}}</c>
/// and <c>{{image}}</c> are replaced wherever they stand in a string (so <c>"{{prompt}}, masterpiece"</c> keeps its
/// tail). A workflow holding <c>{{image}}</c> takes an input picture (img2img, upscale, inpaint).
///
/// <para>Later still on 2026-09-24 (the user's ask: "replace the face in the first image with the face from the second
/// image"): up to <see cref="MaxInputImages"/> input pictures, <c>{{image}}</c>, <c>{{image2}}</c>, <c>{{image3}}</c> —
/// a ReActor face swap's target and source, Qwen-Image-Edit's pictures to compose. The slots run without a gap, and
/// the sidecar's <c>image</c> / <c>image2</c> / <c>image3</c> lines say what each is for (<see cref="ImageRoles"/>), so
/// the model knows which picture goes where. A workflow taking a picture needs no <c>{{prompt}}</c> then: a face swap
/// or a plain upscale has nothing to say.</para>
///
/// <para>A sidecar <c>&lt;name&gt;.md</c> beside it is optional: YAML-ish <c>key: value</c> lines between two
/// <c>---</c> fences — <c>description</c>, <c>family</c> (<see cref="ComfyFamilies.Names"/>), <c>width</c>,
/// <c>height</c>, <c>steps</c>, <c>cfg</c>, <c>denoise</c>, <c>negative</c>, <c>image</c> / <c>image2</c> / <c>image3</c> (each input picture's role) — and, under them, free prompt tips the
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
    bool Reinforce = true,
    IReadOnlyList<string>? ImageRoles = null,
    IReadOnlyList<string>? TriggerWords = null,
    IReadOnlyList<string>? LorasOff = null)
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

    /// <summary>
    /// The sidecar keys holding a LoRA's trigger words (2026-11-26, the user's ask): <c>lora.&lt;lora_name&gt;: words, more words</c>, one per
    /// LoRA, the name as the graph's <c>lora_name</c> spells it (case aside). <see cref="TriggerWords"/> is what of them is in play.
    /// </summary>
    public const string LoraWordsPrefix = "lora.";

    /// <summary>
    /// The sidecar keys switching a LoRA off (2026-11-27, the user's ask): <c>lora-off.&lt;lora_name&gt;: yes</c>. Every loader of that name is
    /// left out of every run (<see cref="LorasOff"/>), its trigger words with it, while the file keeps it, its strength and its words. A
    /// prefix of its own, so <see cref="LoraWordsPrefix"/>'s scan never reads it; any value but <see cref="LoraOffValue"/> reads as on.
    /// </summary>
    public const string LoraOffPrefix = "lora-off.";

    /// <summary>The value an off key holds. Pinned.</summary>
    public const string LoraOffValue = "yes";

    /// <summary>The sidecar key for <paramref name="lora"/>'s off mark, or null for a name no key can hold (<see cref="LoraWordsKey"/>'s rule). Pure.</summary>
    public static string? LoraOffKey(string lora) => LoraWordsKey(lora) is null ? null : LoraOffPrefix + lora;

    /// <summary>Whether a sidecar value switches a LoRA off: <see cref="LoraOffValue"/>, case and spaces aside. Pure.</summary>
    public static bool IsOffValue(string? value) => string.Equals(value?.Trim(), LoraOffValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splices every loader named in <paramref name="names"/> out of <paramref name="graph"/> (2026-11-27, a LoRA switched off), in place:
    /// what used its outputs takes its inputs (<see cref="ComfyGraphEdit.RemoveLora"/>). The names left out go into <paramref name="left"/>,
    /// each once. A loader on a switch's unused branch is skipped (it never runs). Null when done; else the first name that can't be left
    /// out and why: its own reason when it has one (other than a strength another node sets, which a splice doesn't need), else the splice's.
    /// </summary>
    public static (string Name, string Why)? LeaveOut(JsonObject graph, IReadOnlySet<string> names, List<string> left)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(left);
        // Read once: a splice re-reads the chain itself, and the other loaders' node ids stay as they were.
        foreach (var lora in ComfyGraphEdit.Read(graph).Loras.Loras.ToList())
        {
            if (lora.SwitchedOff || lora.Name.Length == 0 || !names.Contains(lora.Name))
            {
                continue;
            }

            string? why = lora.Why is { } own && own != ComfyText.EditStrengthNotLiteral ? own : ComfyGraphEdit.RemoveLora(graph, lora.Node);
            if (why is not null)
            {
                return (lora.Name, why);
            }

            if (!left.Contains(lora.Name, StringComparer.OrdinalIgnoreCase))
            {
                left.Add(lora.Name);
            }
        }

        return null;
    }

    /// <summary>The most input pictures a workflow takes (Qwen-Image-Edit-2509's three).</summary>
    public const int MaxInputImages = 3;

    /// <summary>The input pictures' placeholders in slot order: <c>image</c>, <c>image2</c>, <c>image3</c>.</summary>
    public static readonly IReadOnlyList<string> ImageKeys = [ImageKey, ImageKey + "2", ImageKey + "3"];

    /// <summary>The placeholders whose whole-string form becomes a number.</summary>
    public static readonly IReadOnlySet<string> NumericKeys = new HashSet<string>(StringComparer.Ordinal) { SeedKey, WidthKey, HeightKey, StepsKey, CfgKey, DenoiseKey };

    /// <summary>The strength an image workflow's <c>{{denoise}}</c> gets when neither call nor sidecar sets one: enough to restyle, not so much the picture is lost.</summary>
    public const double DefaultImageDenoise = 0.6;

    /// <summary>The problems a file can have, pinned: the catalog shows them beside the name.</summary>
    public const string NotJsonProblem = "is not JSON";
    public const string UiFormatProblem = "is a UI-format save (it has \"nodes\" and \"links\"); in ComfyUI use Workflow → Export (API), or enable dev mode and Save (API)";
    public const string NotApiFormatProblem = "is not an API-format workflow: expected an object of nodes, each with a class_type";
    public const string NoPromptProblem = "has no {{prompt}} placeholder, so nothing the call says would reach the picture";
    public const string GapInImagesProblem = "has a gap in its image placeholders: {{image2}} needs {{image}}, and {{image3}} needs {{image2}}";

    /// <summary>Whether reinforcing tags may be appended to this workflow's negative: it has a <c>{{negative}}</c>, its family uses one, and its sidecar did not say <c>reinforce: false</c>.</summary>
    public bool TakesReinforcement => Reinforce && Placeholders.Contains(NegativeKey) && ComfyFamilies.UsesNegative(Family);

    /// <summary>Whether the workflow takes an input picture: it holds <c>{{image}}</c>.</summary>
    public bool TakesImage => ImageCount > 0;

    /// <summary>How many input pictures the workflow takes: its <see cref="ImageKeys"/> slots, counted from the first until one is missing.</summary>
    public int ImageCount => CountImages(Placeholders);

    /// <summary>Whether the workflow has a <c>{{prompt}}</c> — a face swap or an upscale may not.</summary>
    public bool TakesPrompt => Placeholders.Contains(PromptKey);

    /// <summary>
    /// The sidecar key for <paramref name="lora"/>'s trigger words (<see cref="LoraWordsPrefix"/> + the name), or null for a name no key can
    /// hold (2026-11-26): empty, holding a <c>:</c> (the reader splits at the first), a control character (a line break would split the head)
    /// or spaces at either end (the reader trims them). Pure.
    /// </summary>
    public static string? LoraWordsKey(string lora)
    {
        ArgumentNullException.ThrowIfNull(lora);
        return lora.Length == 0 || lora.Contains(':', StringComparison.Ordinal) || lora.Any(char.IsControl) || lora.Trim() != lora ? null : LoraWordsPrefix + lora;
    }

    /// <summary>A sidecar value's phrases: split at commas, trimmed, the empty ones dropped (2026-11-26). Pure.</summary>
    public static IReadOnlyList<string> SplitWords(string? words) =>
        words is null ? [] : [.. words.Split(',').Select(w => w.Trim()).Where(w => w.Length > 0)];

    /// <summary>
    /// Whether <paramref name="text"/> holds <paramref name="phrase"/> as a whole phrase, ignoring case (2026-11-26, the trigger words): no
    /// letter, digit or <c>_</c> touches it on a side where the phrase itself starts or ends with one, so <c>(xyz_style:1.2)</c> holds
    /// <c>xyz_style</c> while <c>smart</c> doesn't hold <c>art</c> and <c>xyz_style2</c> doesn't hold <c>xyz_style</c>. Pure.
    /// </summary>
    public static bool HasPhrase(string text, string phrase)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(phrase);
        if (phrase.Length == 0)
        {
            return false;
        }

        static bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';
        bool front = Word(phrase[0]);
        bool back = Word(phrase[^1]);
        int at = 0;
        while ((at = text.IndexOf(phrase, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = at + phrase.Length;
            if ((!front || at == 0 || !Word(text[at - 1])) && (!back || end == text.Length || !Word(text[end])))
            {
                return true;
            }

            at++;
        }

        return false;
    }

    /// <summary>
    /// Whether a LoRA's trigger words apply (2026-11-26): on the branch that runs, a literal name, and some strength on. A linked strength
    /// (null) counts as on. A <c>LoraLoader</c> (and any other class with a <c>lora_name</c>) is off only with both strengths 0, since the
    /// words act through the text encoder; a <c>LoraLoaderModelOnly</c> has no CLIP input, so its model strength alone decides. Pure.
    /// </summary>
    public static bool LoraInPlay(ComfyLora lora)
    {
        ArgumentNullException.ThrowIfNull(lora);
        if (lora.SwitchedOff || lora.Name.Length == 0)
        {
            return false;
        }

        bool modelOff = lora.StrengthModel == 0;
        return lora.ClassType == ComfyGraphEdit.LoraLoaderModelOnly ? !modelOff : !(modelOff && lora.StrengthClip == 0);
    }

    /// <summary>The role the sidecar gave input picture <paramref name="slot"/> (0-based), or "".</summary>
    public string ImageRole(int slot) => ImageRoles is { } roles && slot >= 0 && slot < roles.Count ? roles[slot] : "";

    /// <summary>The <see cref="ImageKeys"/> slots <paramref name="placeholders"/> fills, from the first until one is missing. Pure.</summary>
    public static int CountImages(IReadOnlySet<string> placeholders)
    {
        ArgumentNullException.ThrowIfNull(placeholders);
        int count = 0;
        while (count < ImageKeys.Count && placeholders.Contains(ImageKeys[count]))
        {
            count++;
        }

        return count;
    }

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
        int images = CountImages(placeholders);
        if (ImageKeys.Skip(images).Any(placeholders.Contains))
        {
            problem = GapInImagesProblem;
            return false;
        }

        // A picture in is enough to work on (later still on 2026-09-24): a face swap or an upscale takes no prompt.
        if (!placeholders.Contains(PromptKey) && images == 0)
        {
            problem = NoPromptProblem;
            return false;
        }

        var head = sidecar is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : ReadSidecar(sidecar);
        string body = sidecar is null ? "" : SidecarBody(sidecar);
        // The LoRAs switched off (2026-11-27): spliced out of the template every run fills, so none of them runs and none gives words.
        var offNames = head.Where(p => p.Key.StartsWith(LoraOffPrefix, StringComparison.OrdinalIgnoreCase) && IsOffValue(p.Value))
            .Select(p => p.Key[LoraOffPrefix.Length..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string>? lorasOff = null;
        if (offNames.Count > 0 && JsonNode.Parse(json, documentOptions: Tolerant) is JsonObject offGraph)
        {
            var left = new List<string>();
            if (LeaveOut(offGraph, offNames, left) is { } refused)
            {
                problem = ComfyText.LoraOffProblem(refused.Name, refused.Why);
                return false;
            }

            if (left.Count > 0)
            {
                json = offGraph.ToJsonString();
                placeholders = FindPlaceholders(json);
                lorasOff = left;
            }
        }

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
            !(head.TryGetValue("reinforce", out string? reinforce) && reinforce.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)),
            ImageKeys.Take(images).Select(key => head.TryGetValue(key, out string? role) ? role.Trim() : "").ToList(),
            placeholders.Contains(PromptKey) ? FindTriggerWords(json, head) : null,
            lorasOff);
        return true;
    }

    /// <summary>
    /// The trigger words a run puts in front of the prompt (2026-11-26, the user's ask): for each LoRA in play (<see cref="LoraInPlay"/>), in
    /// the graph's order and each name once, its <see cref="LoraWordsPrefix"/> key's phrases. A phrase an earlier LoRA gave goes once, and
    /// one every string holding <c>{{prompt}}</c> already has (a template's own <c>"{{prompt}}, xyz_style"</c>) is left out: every
    /// <c>{{prompt}}</c> gets the same text, so a word some string lacks still goes in. Null for none, or for a graph the walk can't read.
    /// </summary>
    private static IReadOnlyList<string>? FindTriggerWords(string json, Dictionary<string, string> head)
    {
        if (!head.Keys.Any(k => k.StartsWith(LoraWordsPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        JsonObject? graph;
        try
        {
            graph = JsonNode.Parse(json, documentOptions: Tolerant) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }

        if (graph is null)
        {
            return null;
        }

        var prompts = new List<string>();
        PromptStrings(graph, prompts);
        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lora in ComfyGraphEdit.Read(graph).Loras.Loras)
        {
            if (!LoraInPlay(lora) || !seen.Add(lora.Name) || LoraWordsKey(lora.Name) is not { } key || !head.TryGetValue(key, out string? value))
            {
                continue;
            }

            foreach (string phrase in SplitWords(value))
            {
                if (!words.Any(w => HasPhrase(w, phrase)) && !(prompts.Count > 0 && prompts.All(p => HasPhrase(p, phrase))))
                {
                    words.Add(phrase);
                }
            }
        }

        return words.Count == 0 ? null : words;
    }

    /// <summary>Every string in <paramref name="node"/> holding a <c>{{prompt}}</c> placeholder, added to <paramref name="into"/>.</summary>
    private static void PromptStrings(JsonNode? node, List<string> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    PromptStrings(child, into);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    PromptStrings(child, into);
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String && FindPlaceholders(value.GetValue<string>()).Contains(PromptKey):
                // The placeholders blanked (2026-11-27, the last-3 review's #20): a trigger word "prompt" isn't already in "{{prompt}}".
                into.Add(WithoutPlaceholders(value.GetValue<string>()));
                break;
        }
    }

    /// <summary>
    /// How every graph is parsed: comments and trailing commas allowed, a repeated key refused (2026-11-25): <see cref="JsonNode"/> would
    /// throw an <see cref="ArgumentException"/> on one only when the object is first read, past every <see cref="JsonException"/> catch.
    /// </summary>
    internal static readonly JsonDocumentOptions Tolerant = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, AllowDuplicateProperties = false };

    /// <summary>Null when <paramref name="json"/> is an API-format graph, else the problem.</summary>
    private static string? CheckGraph(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: Tolerant);
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
        foreach (var (_, _, name) in PlaceholderSpans(text))
        {
            found.Add(name);
        }

        return found;
    }

    /// <summary>
    /// The text with each <c>{{name}}</c> a space (2026-11-27, the last-3 review's #20), for the trigger words' already-there check; a
    /// <c>{{ odd text }}</c> that is no placeholder stays, searchable as text. Pure.
    /// </summary>
    internal static string WithoutPlaceholders(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        int from = 0;
        foreach (var (start, end, _) in PlaceholderSpans(text))
        {
            sb.Append(text, from, start - from).Append(' ');
            from = end;
        }

        return sb.Append(text, from, text.Length - from).ToString();
    }

    /// <summary>
    /// Each placeholder's span (its start, the index past its <c>}}</c>) and lower-cased name: one scanner for <see cref="FindPlaceholders"/>
    /// and <see cref="WithoutPlaceholders"/> (2026-11-27).
    /// </summary>
    private static IEnumerable<(int Start, int End, string Name)> PlaceholderSpans(string text)
    {
        int at = 0;
        while ((at = text.IndexOf("{{", at, StringComparison.Ordinal)) >= 0)
        {
            int end = text.IndexOf("}}", at + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                yield break;
            }

            string name = text[(at + 2)..end].Trim();
            if (name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                yield return (at, end + 2, name.ToLowerInvariant());
            }

            at = end + 2;
        }
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
        var graph = (JsonObject)JsonNode.Parse(GraphJson, documentOptions: Tolerant)!;
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
        };
        for (int i = 0; i < ImageKeys.Count; i++)
        {
            texts[ImageKeys[i]] = values.Images is { } names && i < names.Count ? names[i] : "";
        }
        foreach (var (key, number) in numbers)
        {
            texts[key] = number.ToJsonString();
        }

        Walk(graph, numbers, texts);
        return graph;
    }

    /// <summary>
    /// The parameters a filled graph (<see cref="Fill"/>) actually carries (2026-09-25, the user's ask): for each, the first
    /// node input of that name holding a value rather than a link — <c>width</c>, <c>height</c>, <c>steps</c>, <c>cfg</c>,
    /// <c>denoise</c>, <c>seed</c> or <c>noise_seed</c>, <c>sampler_name</c>, <c>scheduler</c> — so a template that hardcodes
    /// its steps or its sampler is reported as it runs, whatever the placeholders say. <c>KSampler</c>, <c>KSamplerAdvanced</c>
    /// and <c>KSamplerSelect</c> + <c>BasicScheduler</c> are all covered by the names alone. Pure.
    /// </summary>
    public static ComfyParameters ReadParameters(JsonObject graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var found = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var (_, node) in graph)
        {
            if (node is not JsonObject { } obj || obj["inputs"] is not JsonObject inputs)
            {
                continue;
            }

            foreach (var (name, input) in inputs)
            {
                // A link is an array ([node, slot]): what it carries is another node's, read there.
                if (input is JsonValue value && !found.ContainsKey(name))
                {
                    found[name] = value;
                }
            }
        }

        return new ComfyParameters(
            (int?)Number(WidthKey), (int?)Number(HeightKey), (int?)Number(StepsKey), Number(CfgKey), Number(DenoiseKey),
            Whole(SeedKey) ?? Whole("noise_seed"), Text("sampler_name"), Text("scheduler"));

        // A seed reaches 2^64 in ComfyUI: read whole, never through a double.
        long? Whole(string name) =>
            found.TryGetValue(name, out var value) && value.GetValueKind() == JsonValueKind.Number
            && long.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) ? number : null;

        double? Number(string name) =>
            found.TryGetValue(name, out var value) && value.GetValueKind() == JsonValueKind.Number
            && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : null;

        string? Text(string name) =>
            found.TryGetValue(name, out var value) && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { Length: > 0 } text ? text : null;
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
