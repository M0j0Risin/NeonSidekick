using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NeonSidekick.Comfy;

/// <summary>
/// The image tools' words (2026-09-24): the result sentences of <c>generate_image</c>, <c>set_splash_image</c> and
/// <c>/imagine</c>, the failures, the <c>/comfy</c> lines and the tool description's parts. Pure; pinned by tests.
/// </summary>
public static class ComfyText
{
    /// <summary>The glyph the image lines open with.</summary>
    public const string Glyph = "🎨 ";

    /// <summary>The hint row's lead while the picture strip stands with nothing highlighted and the draft empty (later still on 2026-09-24). Pinned.</summary>
    public const string StripHint = "← → pictures";

    /// <summary>The hint row's lead while picture <paramref name="index"/> (one-based, the newest 1) of <paramref name="count"/> is highlighted. Pinned.</summary>
    public static string StripSelectedHint(int index, int count) =>
        $"← → picture {index.ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)} · Enter opens";

    /// <summary>The sentence when <c>ComfyUI URL</c> is empty or no http(s) URL. Pinned.</summary>
    public const string NoServer = "Error: no ComfyUI server is set; set ComfyUI URL on the ComfyUI tab of /tools (or NEONSIDEKICK_COMFY_URL)";

    /// <summary>The sentence when no workflow is in either <c>comfy</c> folder. Pinned.</summary>
    public static string NoWorkflows(IReadOnlyList<string> roots) =>
        "Error: no ComfyUI workflow found; export one from ComfyUI with Save (API) into " + string.Join(" or ", roots) + " (see /comfy)";

    /// <summary>A new workflow's name already used in a <c>comfy</c> folder (the add-workflow wizard, later on 2026-09-24). Pinned.</summary>
    public static string NameTaken(string name, string folder) => $"a workflow named '{name}' is already in {folder}";

    /// <summary>Workflows are installed but <c>ComfyUI workflows offered</c> ticks none (later on 2026-09-24). Pinned.</summary>
    public const string NoneOffered = "Error: no ComfyUI workflow is offered; tick one in ComfyUI workflows offered on the ComfyUI tab of /tools";

    /// <summary>A workflow name that is not in the catalog. Pinned.</summary>
    public static string UnknownWorkflow(string name, IReadOnlyList<ComfyWorkflow> workflows) =>
        $"Error: no workflow named '{name}'; the workflows are: " + string.Join(", ", workflows.Select(w => w.Name));

    /// <summary>No workflow named and more than one fits. Pinned.</summary>
    public static string WhichWorkflow(IReadOnlyList<ComfyWorkflow> candidates) =>
        "Error: several workflows fit; name one of: " + string.Join(", ", candidates.Select(w => w.Name));

    /// <summary>An input picture for a workflow without <c>{{image}}</c>. Pinned.</summary>
    public static string TakesNoImage(string name) => $"Error: workflow '{name}' takes no input image (it has no {{{{image}}}} placeholder)";

    /// <summary>A workflow with <c>{{image}}</c> called without one. Pinned.</summary>
    public static string NeedsImage(string name) => $"Error: workflow '{name}' needs an input image: give \"image\" (a picture under the working directory, or a pasted one's [Image #N] label)";

    /// <summary>A multi-image workflow (later still on 2026-09-24, the face swap) given another number of pictures than it takes. Pinned.</summary>
    public static string WrongImageCount(ComfyWorkflow workflow, int given)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        int takes = workflow.ImageCount;
        string keys = string.Join(", ", ComfyWorkflow.ImageKeys.Take(takes));
        return $"Error: workflow '{workflow.Name}' takes {Pictures(takes)} ({keys}), not {given.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>A later input picture given without the one before it (<c>image2</c> with no <c>image</c>). Pinned.</summary>
    public static string ImageGap(string given, string missing) => $"Error: {given} needs {missing}: the input pictures fill image, image2, image3 in order";

    private static string Pictures(int count) => count == 1 ? "1 input image" : count.ToString(CultureInfo.InvariantCulture) + " input images";

    /// <summary>
    /// Whether <paramref name="image"/> names a pasted picture by the label the model read (later still on 2026-09-24):
    /// <c>[Image #1]</c>, or the same without the brackets or the <c>#</c>, any case, spaces around allowed — nothing
    /// else, so a file really named <c>Image 1.png</c> is still a path. Pure; pinned.
    /// </summary>
    public static bool TryPastedLabel(string image, out int number)
    {
        ArgumentNullException.ThrowIfNull(image);
        number = 0;
        var s = image.AsSpan().Trim();
        if (s.Length >= 2 && s[0] == '[' && s[^1] == ']')
        {
            s = s[1..^1].Trim();
        }

        if (!s.StartsWith("image", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        s = s[5..].TrimStart();
        if (s.Length > 0 && s[0] == '#')
        {
            s = s[1..].TrimStart();
        }

        return s.Length > 0 && char.IsAsciiDigit(s[0])
            && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
    }

    /// <summary>An <c>[Image #N]</c> input with no such paste this session (or none kept at full size). Pinned.</summary>
    public static string NoPastedPicture(int number) =>
        $"Error: there is no pasted picture {UI.PasteBlocks.ImageLabel(number)} in this session";

    /// <summary>The result's line naming where a pasted input was saved, so the model can give that path from now on. Pinned.</summary>
    public static string PastedInput(string saved, int number)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return $"input: {saved} (the pasted {UI.PasteBlocks.ImageLabel(number)}, saved at full size)";
    }

    /// <summary>A blank prompt. Pinned.</summary>
    public const string NoPrompt = "Error: give \"prompt\" (what to draw)";

    /// <summary>A number argument out of its range. Pinned.</summary>
    public static string OutOfRange(string argument, string raw, string range) => $"Error: {argument} must be {range}, not '{raw}'";

    /// <summary>The input picture could not be read from the sandbox. Pinned.</summary>
    public static string BadInput(string path, string why) => $"Error: cannot use '{path}' as the input image: {why}";

    /// <summary>A server that did not answer at all. Pinned.</summary>
    public static string Unreachable(string url, string detail) => $"Error: ComfyUI at {url} did not answer: {Diagnostics.LogText.Excerpt(detail)}";

    /// <summary>A call past its ceiling. Pinned.</summary>
    public static string NoAnswer(string budget) => $"Error: ComfyUI gave no answer within {budget}";

    /// <summary>A generation past <c>ComfyUI timeout (s)</c>: the job may still finish in ComfyUI. Pinned.</summary>
    public static string TimedOut(string budget, string promptId) =>
        $"Error: no picture within {budget}" + (promptId.Length > 0 ? $"; the job ({promptId}) may still finish in ComfyUI's output folder" : "");

    /// <summary>A job that ran and saved nothing. Pinned.</summary>
    public const string NoPictures = "Error: the workflow ran but saved no picture (does it end in a SaveImage or PreviewImage node?)";

    /// <summary>An answer that was not the JSON expected. Pinned.</summary>
    public static string BadAnswer(string endpoint) => $"Error: ComfyUI's answer on {endpoint} was not what was expected";

    /// <summary>A non-success status with its body's head. Pinned.</summary>
    public static string HttpError(int status, string endpoint, string body)
    {
        string snippet = Diagnostics.LogText.Excerpt(body ?? "");
        return $"Error: HTTP {status.ToString(CultureInfo.InvariantCulture)} on {endpoint}" + (snippet.Length > 0 ? ": " + snippet : "");
    }

    /// <summary>A job whose status came back <c>error</c>: the failing node's type and message. Pinned.</summary>
    public static string ExecutionError(string detail) =>
        "Error: ComfyUI failed running the workflow" + (detail.Length > 0 ? ": " + Diagnostics.LogText.Excerpt(detail) : "");

    /// <summary>
    /// ComfyUI's refusal of a queued graph (<c>POST /prompt</c> 400): its <c>error.message</c> and each node's errors —
    /// <c>node 4 (CheckpointLoaderSimple): Value not in list: ckpt_name: 'x.safetensors' not in [...]</c> — so the model
    /// (or the user) can see which input is wrong. Pure; pinned.
    /// </summary>
    public static string Refused(int status, string body)
    {
        var sb = new StringBuilder("Error: ComfyUI refused the workflow (HTTP " + status.ToString(CultureInfo.InvariantCulture) + ")");
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && Str(error, "message") is { Length: > 0 } message)
            {
                sb.Append(": ").Append(message.TrimEnd('.'));
            }

            if (root.TryGetProperty("node_errors", out var nodes) && nodes.ValueKind == JsonValueKind.Object)
            {
                foreach (var node in nodes.EnumerateObject())
                {
                    string type = Str(node.Value, "class_type");
                    if (!node.Value.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var e in errors.EnumerateArray())
                    {
                        string what = Str(e, "message");
                        string details = Str(e, "details");
                        sb.Append("; node ").Append(node.Name).Append(type.Length > 0 ? " (" + type + ")" : "").Append(": ").Append(what).Append(details.Length > 0 ? ": " + details : "");
                    }
                }
            }
        }
        catch (JsonException)
        {
            string snippet = Diagnostics.LogText.Excerpt(body ?? "");
            if (snippet.Length > 0)
            {
                sb.Append(": ").Append(snippet);
            }
        }

        return Diagnostics.LogText.Excerpt(sb.ToString(), 1200);

        static string Str(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    /// <summary><c>/system_stats</c> as one line: the versions and each device's free memory. Pure; pinned.</summary>
    public static string Stats(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var parts = new List<string>();
            if (root.TryGetProperty("system", out var system) && system.ValueKind == JsonValueKind.Object)
            {
                if (system.TryGetProperty("comfyui_version", out var v) && v.ValueKind == JsonValueKind.String)
                {
                    parts.Add("ComfyUI " + v.GetString());
                }

                if (system.TryGetProperty("python_version", out var p) && p.ValueKind == JsonValueKind.String)
                {
                    parts.Add("Python " + (p.GetString() ?? "").Split(' ')[0]);
                }
            }

            if (root.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
            {
                foreach (var device in devices.EnumerateArray())
                {
                    string name = device.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                    double total = device.TryGetProperty("vram_total", out var t) && t.TryGetDouble(out double tt) ? tt : 0;
                    double free = device.TryGetProperty("vram_free", out var f) && f.TryGetDouble(out double ff) ? ff : 0;
                    parts.Add(total > 0 ? $"{name} ({Gb(free)} of {Gb(total)} GB VRAM free)" : name);
                }
            }

            return parts.Count == 0 ? "ComfyUI answered" : string.Join(" · ", parts);
        }
        catch (JsonException)
        {
            return "ComfyUI answered";
        }

        static string Gb(double bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A generation's result, the first line the transcript's one tool line: <c>generated 2 pictures with
    /// pony-txt2img (seed 1234, 1024×1024): comfy_images\pony-txt2img-1234.png, comfy_images\pony-txt2img-1235.png — the
    /// pictures are in the next message</c>; <paramref name="attached"/> false (the <c>/imagine</c> note, or a picture
    /// the codecs refused) drops the tail. The prompt follows on its own line, so what was sent is on record, and after it
    /// (2026-09-25, the user's ask) the line of <paramref name="parameters"/> (<see cref="Parameters"/>). Pinned.
    /// </summary>
    public static string Generated(string workflow, long seed, int width, int height, IReadOnlyList<string> paths, string prompt, string negative, bool attached, string? input = null, ComfyParameters? parameters = null)
    {
        string count = paths.Count == 1 ? "1 picture" : paths.Count.ToString(CultureInfo.InvariantCulture) + " pictures";
        string head = $"generated {count} with {workflow} (seed {seed.ToString(CultureInfo.InvariantCulture)}, {width.ToString(CultureInfo.InvariantCulture)}×{height.ToString(CultureInfo.InvariantCulture)}): " + string.Join(", ", paths);
        if (attached)
        {
            head += paths.Count == 1 ? " — the picture is in the next message" : " — the pictures are in the next message";
        }

        // A pasted input's line (PastedInput, later still on 2026-09-24) sits between the head and the prompt.
        // A workflow with no {{prompt}} (a face swap, later still on 2026-09-24) has no prompt line.
        return head + (input is null ? "" : "\n" + input) + (prompt.Length > 0 ? "\nprompt: " + prompt : "") + (negative.Length > 0 ? "\nnegative: " + negative : "")
            + (parameters is null || Parameters(parameters) is not { Length: > 0 } line ? "" : "\n" + line);
    }

    /// <summary>
    /// The parameters' line of a generation's result (2026-09-25, the user's ask: the rest of the params beside the prompts):
    /// <c>params: 1024×1024, steps 20, cfg 7, denoise 1, seed 1234, sampler euler, scheduler normal</c> — each one the
    /// graph has, in that order; empty when it has none. Pinned.
    /// </summary>
    public static string Parameters(ComfyParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var parts = new List<string>();
        if (parameters.Width is { } width && parameters.Height is { } height)
        {
            parts.Add(width.ToString(CultureInfo.InvariantCulture) + "×" + height.ToString(CultureInfo.InvariantCulture));
        }

        if (parameters.Steps is { } steps) parts.Add("steps " + steps.ToString(CultureInfo.InvariantCulture));
        if (parameters.Cfg is { } cfg) parts.Add("cfg " + cfg.ToString("0.###", CultureInfo.InvariantCulture));
        if (parameters.Denoise is { } denoise) parts.Add("denoise " + denoise.ToString("0.###", CultureInfo.InvariantCulture));
        if (parameters.Seed is { } seed) parts.Add("seed " + seed.ToString(CultureInfo.InvariantCulture));
        if (parameters.Sampler is { } sampler) parts.Add("sampler " + sampler);
        if (parameters.Scheduler is { } scheduler) parts.Add("scheduler " + scheduler);
        return parts.Count == 0 ? "" : "params: " + string.Join(", ", parts);
    }

    /// <summary>
    /// A generation's result (<see cref="Generated"/>) split for the transcript (later still on 2026-09-24): the first line — the
    /// picture's line — and the rest, one entry per non-blank line (<c>prompt: …</c>, <c>negative: …</c>, <c>params: …</c>, a partial failure's
    /// error), shown in full under it with <c>ComfyUI show prompts</c> on. Pure.
    /// </summary>
    public static (string Head, IReadOnlyList<string> Details) SplitGenerated(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string[] lines = result.ReplaceLineEndings("\n").Split('\n');
        return (lines[0], lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).ToList());
    }

    /// <summary>The note the conversation gets after <c>/imagine</c> (2026-09-24), so the model knows the picture exists and what made it. Pinned.</summary>
    public static string ImagineNote(string result) => "(the user made a picture with /imagine; this note is from the app, not typed by the user: " + result.Replace("\n", "; ", StringComparison.Ordinal) + ")";

    /// <summary>The spinner's label while a generation runs (<c>⠹ 🎨 00:12</c>; 2026-09-25, the user's wording — it read <c>Generating with &lt;workflow&gt;…</c>, then <c>ComfyUI:</c>, until later that day: the palette, <see cref="Glyph"/>'s, U+1F3A8, two cells, no variation selector). Since 2026-09-26 the image-to-image one; <see cref="TextToImageLabel"/> is the other. Pinned.</summary>
    public const string GeneratingLabel = "🎨";

    /// <summary>
    /// The text-to-image generation's glyph and spinner label (2026-09-26, the user's ask: 🖼️ when it draws from text, 🎨 when it
    /// works from a picture): the framed picture, U+1F5BC with its U+FE0F — two cells, as <c>TextCells</c> counts the pair and
    /// the selector, like <c>🗑️</c>. The kind is the request's input pictures (<see cref="GlyphFor"/>). Pinned.
    /// </summary>
    public const string TextToImageGlyph = "🖼️ ";
    public const string TextToImageLabel = "🖼️";

    /// <summary>The result line's glyph for a generation given <paramref name="inputImages"/> pictures: 🎨 with any, 🖼️ with none. Pure.</summary>
    public static string GlyphFor(int inputImages) => inputImages > 0 ? Glyph : TextToImageGlyph;

    /// <summary>The spinner's label for a generation given <paramref name="inputImages"/> pictures: 🎨 with any, 🖼️ with none. Pure.</summary>
    public static string GeneratingLabelFor(int inputImages) => inputImages > 0 ? GeneratingLabel : TextToImageLabel;

    /// <summary>Whether <paramref name="label"/> is a generation's spinner label, either kind (the pane draws it after the tally). Pure.</summary>
    public static bool IsGeneratingLabel(string label) =>
        string.Equals(label, GeneratingLabel, StringComparison.Ordinal) || string.Equals(label, TextToImageLabel, StringComparison.Ordinal);

    /// <summary>A generation the user stopped (ESC). Pinned.</summary>
    public const string Cancelled = "(image generation cancelled)";

    /// <summary>The notice after the hint row's double-click on the 🖼️ / 🎨 (2026-09-28): <see cref="Cancelled"/> for one, the count for more. Pinned.</summary>
    public static string Drained(int count) =>
        count <= 1 ? Cancelled : $"({count.ToString(CultureInfo.InvariantCulture)} image generations cancelled)";

    /// <summary>
    /// <c>generate_image</c>'s answer when the user cancelled the pictures from the hint row (2026-09-28, the user's call: the
    /// pictures stop, the reply goes on), so the model neither retries nor says it failed. Pinned.
    /// </summary>
    public const string CancelledByUser = "Error: the user cancelled this image generation. Do not generate it again unless the user asks.";

    /// <summary>
    /// <c>/imagine</c>'s notice when its generation outlasts the half-second grace and goes behind the input line (2026-10-04, the
    /// user's report: the line was held until it ended), so the missing spinner reads as running, and how to stop it. Led by
    /// <c>✨ imagining</c> since later that day (the user's wording; it read <c>generating behind the input line</c>). Pinned.
    /// </summary>
    public const string ImagineInBackground = "(✨ imagining: the picture shows here when it is done; double-click the 🖼️ / 🎨 on the hint row to cancel)";

    /// <summary><c>/imagine</c> with both a negative after <c>--</c> and <c>--no-negative</c> (later on 2026-09-24). Pinned.</summary>
    public const string NegativeAndNoNegative = "give a negative after -- or --no-negative, not both";

    /// <summary><c>/imagine</c> with nothing after it. Pinned.</summary>
    public const string ImagineUsage = "Usage: /imagine [workflow] <prompt> [-- <negative> | --no-negative] [--seed N] [--size WxH] [--steps N] [--cfg X] [--denoise X] [--image <path>] [--image2 <path>] [--image3 <path>]";

    /// <summary>A flag <c>/imagine</c> could not read. Pinned.</summary>
    public static string BadFlag(string flag, string value) => $"{flag} takes {FlagShape(flag)}, not '{value}'";

    private static string FlagShape(string flag) => flag switch
    {
        "--size" => "WxH (1024x1024)",
        "--cfg" or "--denoise" => "a number",
        "--image" or "--image2" or "--image3" => "a path",
        _ => "a whole number",
    };

    /// <summary>The line after <c>set_splash_image</c> copied a picture. Pinned.</summary>
    public static string SplashSet(string relative, string target, bool onlyOne) =>
        $"copied {relative} to {target}; the splash shows it from the next /splash or start"
        + (onlyOne ? " — it is the only picture in the profile's splash folder, so it stands in for the bundled set until more are added" : "");

    /// <summary>A splash source that is no picture. Pinned.</summary>
    public static string SplashNotAnImage(string relative) => $"Error: '{relative}' is not a picture the splash can show (png, jpg, gif, webp or bmp)";

    /// <summary>A splash copy that failed on disk. Pinned.</summary>
    public static string SplashFailed(string target, string detail) => $"Error: could not copy into {target}: {Diagnostics.LogText.Excerpt(detail)}";

    /// <summary>The warning for a workflow file the catalog skips. Pinned.</summary>
    public static string SkippedWorkflow(string file, string problem) => $"Skipped the ComfyUI workflow {file}: it {problem}.";

    /// <summary>The warning for a <c>comfy</c> folder that could not be listed. Pinned.</summary>
    public static string UnreadableFolder(string folder, string detail) => $"Could not read the ComfyUI workflows folder {folder}: {detail}";

    /// <summary>One workflow as the tool description and <c>/comfy</c> list it: <c>pony-txt2img · pony · text → image · 1024×1024 — Anime portraits</c>. Pinned.</summary>
    public static string WorkflowLine(ComfyWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var d = workflow.Defaults;
        string input = InputShape(workflow, roles: true);
        return $"{workflow.Name} · {ComfyFamilies.Name(workflow.Family)} · {input} · {d.Width.ToString(CultureInfo.InvariantCulture)}×{d.Height.ToString(CultureInfo.InvariantCulture)}"
            + (workflow.Description.Length > 0 ? " — " + workflow.Description : "");
    }

    /// <summary>
    /// What a workflow takes in: <c>text → image</c>, <c>image → image</c>, or (later still on 2026-09-24, the face swap)
    /// <c>2 images → image</c> — with <paramref name="roles"/> each input picture's sidecar role after it, <c>(image: the
    /// picture whose face is replaced; image2: the face to put in)</c>, so the model knows which goes where — and
    /// <c>, no prompt</c> for one with no <c>{{prompt}}</c>. Pinned.
    /// </summary>
    public static string InputShape(ComfyWorkflow workflow, bool roles = false)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        int count = workflow.ImageCount;
        string shape = count == 0 ? "text → image" : count == 1 ? "image → image" : count.ToString(CultureInfo.InvariantCulture) + " images → image";
        if (roles && Enumerable.Range(0, count).Any(i => workflow.ImageRole(i).Length > 0))
        {
            shape += " (" + string.Join("; ", Enumerable.Range(0, count).Select(i => ComfyWorkflow.ImageKeys[i] + ": " + (workflow.ImageRole(i) is { Length: > 0 } role ? role : "?"))) + ")";
        }

        return workflow.TakesPrompt ? shape : shape + ", no prompt";
    }

    /// <summary>
    /// The <c>generate_image</c> description over the catalog as it stands: what the tool does, when to write a
    /// prompt and when to pass the user's through (<c>verbatim</c>, the user's ask: "we will be able to send standard
    /// prompts too, as in score_9, etc."), the workflows one line each with their tips, and the style guide of each
    /// family among them. Pinned.
    /// </summary>
    public static string Describe(IReadOnlyList<ComfyWorkflow> workflows, bool reinforce = false)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        var sb = new StringBuilder(
            "Generates pictures on the user's ComfyUI server from one of their workflows and saves them under the working directory; you see them in the next message. " +
            "When the user describes a picture in their own words, write the prompt yourself in the dialect of the workflow's family (below). " +
            "When the user gives a ready-made prompt — tags, score_9 / score_8_up, (tag:1.2) weights, or says to use their prompt as is — pass it unchanged in prompt with verbatim true, and their negative the same way; never rewrite, reorder or add to it. " +
            "For an edit, restyle or upscale of an existing picture, give its path — or, for a picture the user pasted, its label ([Image #1]), which sends it at full size — as image and pick a workflow that takes one. " +
            "A workflow taking several pictures (a face swap, a composite) gets them as image, image2, image3, each in the role its line names; one marked 'no prompt' needs no prompt. Seeds are random unless given; reuse the reported seed to vary a picture slightly.");
        bool anyReinforced = reinforce && workflows.Any(w => w.TakesReinforcement);
        if (anyReinforced)
        {
            sb.Append(' ').Append(ReinforceGuide);
        }

        sb.Append("\nWorkflows (name · family · input · size):");
        foreach (var workflow in workflows)
        {
            sb.Append("\n- ").Append(WorkflowLine(workflow));
            if (anyReinforced && workflow.TakesReinforcement)
            {
                sb.Append(" · negative reinforced");
            }
            if (workflow.Tips.Length > 0)
            {
                sb.Append("\n  tips:\n").Append(IndentTips(workflow.Tips));
            }
        }

        var families = workflows.Select(w => w.Family).Distinct().Order().ToList();
        if (families.Count > 0)
        {
            sb.Append("\nPrompt style by family:");
            foreach (var family in families)
            {
                sb.Append("\n- ").Append(ComfyFamilies.StyleGuide(family));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// A workflow's tips as <see cref="Describe"/> lists them (later still on 2026-09-24, the user's call): kept on their own
    /// lines, each indented four spaces under <c>tips:</c> so headings and one-rule-per-line tips stay readable and plainly
    /// belong to that workflow — they were flattened into one run-on line, as a skill's summary is. Trailing spaces go and
    /// a run of blank lines becomes one, left empty. Pinned.
    /// </summary>
    public static string IndentTips(string tips)
    {
        ArgumentNullException.ThrowIfNull(tips);
        var lines = new List<string>();
        foreach (string raw in tips.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length > 0)
            {
                lines.Add("    " + line);
            }
            else if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add("");
            }
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
    }

    /// <summary><c>/imagine</c>'s argument-list note for a workflow (later on 2026-09-24): <c>pony · text → image · 1024×1024</c>, then <c> · hidden from the model</c> when it is not offered. Pinned.</summary>
    public static string CompletionNote(ComfyWorkflow workflow, bool offered)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var d = workflow.Defaults;
        return ComfyFamilies.Name(workflow.Family) + " · " + InputShape(workflow) + " · "
            + d.Width.ToString(CultureInfo.InvariantCulture) + "×" + d.Height.ToString(CultureInfo.InvariantCulture) + (offered ? "" : " · hidden from the model");
    }

    /// <summary>The spinner's label while <c>/comfy</c> asks the server. Pinned.</summary>
    public const string CheckingServer = "Asking ComfyUI…";

    /// <summary>
    /// <c>/comfy</c>'s lines under the server's (2026-09-24): each workflow (<see cref="WorkflowLine"/>) with its placeholders,
    /// each file skipped with why, the folders a workflow goes in, and a word when the switch is off; since later that day
    /// each workflow the model is not offered (<paramref name="offered"/>, ComfyUI workflows offered) is marked hidden. Pinned.
    /// </summary>
    public static IReadOnlyList<string> StatusLines(IReadOnlyList<ComfyWorkflow> workflows, IReadOnlyList<ComfyWorkflowProblem> problems, IReadOnlyList<string> roots, bool enabled, IReadOnlyList<string>? offered = null)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(problems);
        ArgumentNullException.ThrowIfNull(roots);
        var lines = new List<string>();
        if (!enabled)
        {
            lines.Add(ToolsOffLine);
        }

        var shown = ComfyWorkflowCatalog.Offered(workflows, offered);
        string count = workflows.Count == 0 ? "no workflow yet" : workflows.Count == 1 ? "1 workflow" : workflows.Count.ToString(CultureInfo.InvariantCulture) + " workflows";
        lines.Add(workflows.Count == 0 ? count : count + ", " + shown.Count.ToString(CultureInfo.InvariantCulture) + " offered to the model:");
        foreach (var workflow in workflows)
        {
            lines.Add("  " + WorkflowLine(workflow) + " · {{" + string.Join("}} {{", workflow.Placeholders.Order(StringComparer.Ordinal)) + "}}" + (shown.Contains(workflow) ? "" : " · hidden"));
        }

        foreach (var problem in problems)
        {
            lines.Add("  skipped " + Path.GetFileName(problem.FilePath) + ": it " + problem.Problem);
        }

        lines.Add("workflows go in " + string.Join(" or ", roots) + " — export them from ComfyUI with Save (API) and put {{prompt}} (and {{negative}}, {{seed}}, {{width}}, {{height}}, {{steps}}, {{cfg}}, {{image}}, {{image2}}, {{image3}}, {{denoise}}) where the values go");
        return lines;
    }

    /// <summary>The line <c>/comfy</c> and <c>/comfy offered</c> open with while <c>ComfyUI tools</c> is off. Pinned.</summary>
    public const string ToolsOffLine = "ComfyUI tools is off on the ComfyUI tab of /tools: the model is offered no image tool (/imagine still works)";

    /// <summary><c>/comfy offered</c> with no workflow installed at all (2026-10-04). Pinned.</summary>
    public const string NoWorkflowInstalled = "no ComfyUI workflow is installed yet: /comfy says where they go";

    /// <summary><c>/comfy offered</c> with workflows installed and none ticked (2026-10-04). Pinned.</summary>
    public const string NoneOfferedLine = "no ComfyUI workflow is offered to the model: tick them in ComfyUI workflows offered on the ComfyUI tab of /tools";

    /// <summary>
    /// <c>/comfy offered</c>'s lines (2026-10-04, the user's ask: the offered workflows as a bulleted list, without the server's
    /// status and the rest of <c>/comfy</c>): <see cref="ToolsOffLine"/> first while the switch is off, then a count and one
    /// <c>  • </c> line per offered workflow (<see cref="WorkflowLine"/>), in the catalog's order; <see cref="NoWorkflowInstalled"/>
    /// or <see cref="NoneOfferedLine"/> in place of the list when it is empty. Pure; pinned.
    /// </summary>
    public static IReadOnlyList<string> OfferedLines(IReadOnlyList<ComfyWorkflow> workflows, IReadOnlyList<string>? offered, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        var lines = new List<string>();
        if (!enabled)
        {
            lines.Add(ToolsOffLine);
        }

        var shown = ComfyWorkflowCatalog.Offered(workflows, offered);
        if (workflows.Count == 0)
        {
            lines.Add(NoWorkflowInstalled);
            return lines;
        }

        if (shown.Count == 0)
        {
            lines.Add(NoneOfferedLine);
            return lines;
        }

        lines.Add(Glyph + (shown.Count == 1 ? "1 workflow" : shown.Count.ToString(CultureInfo.InvariantCulture) + " workflows") + " offered to the model:");
        lines.AddRange(shown.Select(workflow => "  • " + WorkflowLine(workflow)));
        return lines;
    }

    /// <summary>
    /// What <c>generate_image</c>'s description tells the model about <c>negative_extra</c> while <c>ComfyUI reinforce negatives</c>
    /// is on and a workflow takes it (later still on 2026-09-24, the user's ask). Pinned.
    /// </summary>
    public const string ReinforceGuide =
        "When you write the prompt yourself for a workflow marked 'negative reinforced', also give negative_extra: a handful of tags (at most about eight) " +
        "naming the opposite of what your prompt asks, where the model tends to drift — solo → multiple girls, 2girls; night → daylight, sunlight; short hair → long hair; " +
        "black hair → blonde hair, brown hair; a photo → illustration, anime, 3d render; an empty background → crowd, people in background. " +
        "Never repeat a tag of your prompt, and leave negative_extra out for the user's own verbatim prompts; it is appended to the workflow's negative, which stays.";

    /// <summary>The description while no workflow is installed (the tool is not offered then; this is what <c>/tools</c> shows). Pinned.</summary>
    public const string DescribeEmpty = "Generates pictures on the user's ComfyUI server from one of their workflows (none is installed yet: see /comfy).";
}
