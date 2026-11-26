using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NeonSidekick.Comfy;

/// <summary>
/// The image tools' words (2026-09-24): the result sentences of <c>generate_image</c> (<c>set_splash_image</c>'s moved to
/// <see cref="Images.ImageText"/> on 2026-10-12, with the tool) and
/// <c>/imagine</c>, the failures, the <c>/comfy</c> lines and the tool description's parts. Pure; pinned by tests.
/// </summary>
public static class ComfyText
{
    // ── A path typed in the workflow pane (2026-11-26, the review's #40/#70/#99): the import's file and the test's pictures ──

    /// <summary>A typed path that isn't fully qualified: it would be read against the app's own folder, not the working one.</summary>
    public const string PathNotFull = "is not a full path (start it with a drive or a folder at the root)";

    /// <summary>A path on a computer the app hasn't been told of (the UNC rules).</summary>
    public const string PathUnknownComputer = "is on a computer the app doesn't know";

    public const string PathNotAFile = "is not a file";

    public const string PathNotAPicture = "is not a png, jpg, gif, webp or bmp file";

    /// <summary>A file over the caller's cap: 64 MB for an import, 20 MB for a test picture.</summary>
    public static string PathTooLarge(long cap) => "is over " + Files.FileText.Size(cap);

    /// <summary>Opening a workflow's file with no editor set or found.</summary>
    public const string NoEditor = "no editor to open it in";

    // ── What the wizard still lacks before a save or a test ──
    public const string SetFileFirst = "Set File first.";
    public const string SetCheckpointFirst = "Set Checkpoint first.";
    public const string SetNameFirst = "Set Name first.";

    /// <summary>A workflow delete that couldn't start (the Recycle bin's probe failed), as ThemeText.DeleteFailed says a theme's. Pinned.</summary>
    public static string DeleteFailed(string name, string why) => $"Could not delete workflow '{name}': {why}";

    /// <summary>The glyph the image lines open with.</summary>
    public const string Glyph = "🎨 ";

    /// <summary>The hint row's lead while the picture strip stands with nothing highlighted and the draft empty (later still on 2026-09-24). Pinned.</summary>
    public const string StripHint = "← → pictures";

    /// <summary>The hint row's lead while picture <paramref name="index"/> (one-based, the newest 1) of <paramref name="count"/> is highlighted. Pinned.</summary>
    public static string StripSelectedHint(int index, int count) =>
        $"← → picture {index.ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)} · Enter opens";

    /// <summary>The sentence when <c>ComfyUI URL</c> is empty or no http(s) URL. Pinned.</summary>
    public const string NoServer = "Error: no ComfyUI server is set; set ComfyUI URL on /tools › Apps › ComfyUI (or NEONSIDEKICK_COMFY_URL)";

    /// <summary>The sentence when no workflow is in either <c>comfy</c> folder. Pinned.</summary>
    public static string NoWorkflows(IReadOnlyList<string> roots) =>
        "Error: no ComfyUI workflow found; add one under ComfyUI workflows on /tools › Apps › ComfyUI, or export one from ComfyUI with Export (API) into " + string.Join(" or ", roots);

    /// <summary>A new workflow's name already used in a <c>comfy</c> folder (the add-workflow wizard, later on 2026-09-24). Pinned.</summary>
    public static string NameTaken(string name, string folder) => $"a workflow named '{name}' is already in {folder}";

    /// <summary>Workflows are installed but <c>ComfyUI workflows</c> (<c>… offered</c> until 2026-11-25) ticks none (later on 2026-09-24). Pinned.</summary>
    public const string NoneOffered = "Error: no ComfyUI workflow is offered; tick one in ComfyUI workflows on /tools › Apps › ComfyUI";

    /// <summary>A workflow name that is not in the catalog. Pinned.</summary>
    public static string UnknownWorkflow(string name, IReadOnlyList<ComfyWorkflow> workflows) =>
        $"Error: no workflow named '{name}'; the workflows are: " + string.Join(", ", workflows.Select(w => w.Name));

    /// <summary>No workflow named and more than one fits. Pinned.</summary>
    public static string WhichWorkflow(IReadOnlyList<ComfyWorkflow> candidates) =>
        "Error: several workflows fit; name one of: " + string.Join(", ", candidates.Select(w => w.Name));

    /// <summary>An input picture for a workflow without <c>{{image}}</c>. Pinned.</summary>
    public static string TakesNoImage(string name) => $"Error: workflow '{name}' takes no input image (it has no {{{{image}}}} placeholder)";

    /// <summary>A workflow with <c>{{image}}</c> called without one. Pinned.</summary>
    public static string NeedsImage(string name) => $"Error: workflow '{name}' needs an input image: give \"image\" (a picture under the working directory, a saved picture's full path, or a pasted one's [Image #N] label)";

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
    /// <c>[Image #1]</c>, or the same without the brackets or the <c>#</c>, any case, spaces around allowed; since 2026-11-23 the whole sent
    /// label too, the number followed by <c>, from …</c> or <c>, saved as …</c> (a model copies it whole) — nothing else, so a file really
    /// named <c>Image 1.png</c> is still a path. Pure; pinned.
    /// </summary>
    public static bool TryPastedLabel(string image, out int number) => TryPastedLabel(image, out number, out _);

    /// <summary>
    /// <see cref="TryPastedLabel(string, out int)"/>, and the path a whole sent label names (2026-11-24, the last-7 review's #2): the text
    /// after <c>, from </c> or <c>, saved as </c>, a <c>]</c> inside it kept (only the outer brackets are the label's); null for a bare label.
    /// </summary>
    public static bool TryPastedLabel(string image, out int number, out string? path)
    {
        ArgumentNullException.ThrowIfNull(image);
        number = 0;
        path = null;
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

        int digits = 0;
        while (digits < s.Length && char.IsAsciiDigit(s[digits]))
        {
            digits++;
        }

        var rest = s[digits..];
        if (digits == 0 || (rest.Length > 0 && !rest.StartsWith(FromSeparator, StringComparison.OrdinalIgnoreCase) && !rest.StartsWith(SavedSeparator, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!int.TryParse(s[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out number) || number <= 0)
        {
            return false;
        }

        if (rest.Length > 0)
        {
            var named = rest[(rest.StartsWith(FromSeparator, StringComparison.OrdinalIgnoreCase) ? FromSeparator.Length : SavedSeparator.Length)..].Trim();
            path = named.Length > 0 ? named.ToString() : null;
        }

        return true;
    }

    private const string FromSeparator = ", from ";
    private const string SavedSeparator = ", saved as ";

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
    /// pony-txt2img (seed 1234, 1024×1024): comfy\pony-txt2img-1234.png, comfy\pony-txt2img-1235.png — the
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
    public static string ImagineNote(string result) => ImagineNotePrefix + result.Replace("\n", "; ", StringComparison.Ordinal) + ")";

    /// <summary>How an <see cref="ImagineNote"/> opens: a restored message led by one carries /imagine's pictures after the user's own (2026-10-10, <c>App.RestoredPictures</c>). Pinned.</summary>
    public const string ImagineNotePrefix = "(the user made a picture with /imagine; this note is from the app, not typed by the user: ";

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
    public const string Cancelled = "(image generation canceled)";

    /// <summary>The notice after the hint row's double-click on the 🖼️ / 🎨 (2026-09-28): <see cref="Cancelled"/> for one, the count for more. Pinned.</summary>
    public static string Drained(int count) =>
        count <= 1 ? Cancelled : $"({count.ToString(CultureInfo.InvariantCulture)} image generations canceled)";

    /// <summary>
    /// <c>generate_image</c>'s answer when the user cancelled the pictures from the hint row (2026-09-28, the user's call: the
    /// pictures stop, the reply goes on), so the model neither retries nor says it failed. Pinned.
    /// </summary>
    public const string CancelledByUser = "Error: the user canceled this image generation. Do not generate it again unless the user asks.";

    /// <summary>
    /// <c>/imagine</c>'s notice when its generation outlasts the half-second grace and goes behind the input line (2026-10-04, the
    /// user's report: the line was held until it ended), so the missing spinner reads as running, and how to stop it. Led by
    /// <c>✨ imagining</c> since later that day (the user's wording; it read <c>generating behind the input line</c>). Pinned.
    /// </summary>
    public const string ImagineInBackground = "(✨ imagining: the picture shows here when it is done; double-click the 🖼️ / 🎨 on the lower rule to cancel)";

    /// <summary><c>/imagine</c> with both a negative after <c>--</c> and <c>--no-negative</c> (later on 2026-09-24). Pinned.</summary>
    public const string NegativeAndNoNegative = "give a negative after -- or --no-negative, not both";

    /// <summary><c>/imagine</c> with nothing after it. Pinned.</summary>
    public const string ImagineUsage = "Usage: /imagine [workflow] <prompt> [-- <negative> | --no-negative] [--seed N] [--size WxH] [--steps N] [--cfg X] [--denoise X] [--image <path>] [--image2 <path>] [--image3 <path>]";

    /// <summary>A flag <c>/imagine</c> could not read. Pinned.</summary>
    public static string BadFlag(string flag, string value) => $"{flag} takes {FlagShape(flag)}, not '{value}'";

    /// <summary>What a flag's value is: <c>/imagine</c>'s error, and its note on the argument list (2026-10-27). Pinned.</summary>
    internal static string FlagShape(string flag) => flag switch
    {
        "--size" => "WxH (1024x1024)",
        "--cfg" or "--denoise" => "a number",
        "--image" or "--image2" or "--image3" => "a path",
        ComfyImagine.NoNegativeWord => "send no negative prompt at all",
        ComfyImagine.NegativeWord => "the negative prompt follows",
        _ => "a whole number",
    };

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
            "Generates pictures on the user's ComfyUI server from one of their workflows and saves them in the user's images folder (the result gives each full path); you see them in the next message. " +
            "When the user describes a picture in their own words, write the prompt yourself in the dialect of the workflow's family (below). " +
            "When the user gives a ready-made prompt — tags, score_9 / score_8_up, (tag:1.2) weights, or says to use their prompt as is — pass it unchanged in prompt with verbatim true, and their negative the same way; never rewrite, reorder or add to it. " +
            "For an edit, restyle or upscale of an existing picture, give its path — or, for a picture the user pasted, its label ([Image #1]), which sends it at full size — as image and pick a workflow that takes one. " +
            "A workflow taking several pictures (a face swap, a composite) gets them as image, image2, image3, each in the role its line names; one marked 'no prompt' needs no prompt. Seeds are random unless given; reuse the reported seed to vary a picture slightly.");
        bool anyReinforced = reinforce && workflows.Any(w => w.TakesReinforcement);
        if (anyReinforced)
        {
            sb.Append(' ').Append(ReinforceGuide);
        }

        // The LoRAs' trigger words (2026-11-26): said only when some workflow has them, as the reinforce guide is.
        if (workflows.Any(w => w.TakesPrompt && w.TriggerWords is { Count: > 0 }))
        {
            sb.Append(' ').Append(TriggerWordsGuide);
        }

        sb.Append("\nWorkflows (name · family · input · size):");
        foreach (var workflow in workflows)
        {
            sb.Append("\n- ").Append(WorkflowLine(workflow));
            if (anyReinforced && workflow.TakesReinforcement)
            {
                sb.Append(" · negative reinforced");
            }
            sb.Append(TriggerWordsPart(workflow, "trigger words added"));
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
            lines.Add("  " + WorkflowLine(workflow) + " · {{" + string.Join("}} {{", workflow.Placeholders.Order(StringComparer.Ordinal)) + "}}" + TriggerWordsPart(workflow, "trigger words") + (shown.Contains(workflow) ? "" : " · hidden"));
        }

        foreach (var problem in problems)
        {
            lines.Add("  skipped " + Path.GetFileName(problem.FilePath) + ": it " + problem.Problem);
        }

        lines.Add("workflows go in " + string.Join(" or ", roots) + " — add one under ComfyUI workflows on /tools › Apps › ComfyUI, or export one from ComfyUI with Export (API) and put {{prompt}} (and {{negative}}, {{seed}}, {{width}}, {{height}}, {{steps}}, {{cfg}}, {{image}}, {{image2}}, {{image3}}, {{denoise}}) where the values go");
        return lines;
    }

    /// <summary>The line <c>/comfy</c> and <c>/comfy offered</c> open with while <c>ComfyUI tools</c> is off. Pinned.</summary>
    public const string ToolsOffLine = "ComfyUI tools is off on /tools › Apps › ComfyUI: the model is offered no image tool (/imagine still works)";

    /// <summary><c>/comfy offered</c> with no workflow installed at all (2026-10-04). Pinned.</summary>
    public const string NoWorkflowInstalled = "no ComfyUI workflow is installed yet: add one under ComfyUI workflows on /tools › Apps › ComfyUI";

    /// <summary><c>/comfy offered</c> with workflows installed and none ticked (2026-10-04). Pinned.</summary>
    public const string NoneOfferedLine = "no ComfyUI workflow is offered to the model: tick them in ComfyUI workflows on /tools › Apps › ComfyUI";

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

    /// <summary>
    /// What <c>generate_image</c>'s description tells the model while some workflow has LoRA trigger words (2026-11-26, the user's ask: the app
    /// adds them to every prompt, verbatim ones too). Pinned.
    /// </summary>
    public const string TriggerWordsGuide =
        "A workflow marked 'trigger words added' gets those words put in front of every prompt by the app, the user's verbatim prompts too; leave them out of the prompt you write.";

    /// <summary>
    /// A workflow's trigger words after its line (2026-11-26): <c> · &lt;label&gt;: a, b</c>, or "" for none or a workflow without a prompt.
    /// One helper for the model's line (<c>trigger words added</c>) and <c>/comfy</c>'s (<c>trigger words</c>). Pure.
    /// </summary>
    public static string TriggerWordsPart(ComfyWorkflow workflow, string label)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return workflow.TakesPrompt && workflow.TriggerWords is { Count: > 0 } words ? " · " + label + ": " + string.Join(", ", words) : "";
    }

    /// <summary>A LoRA row whose name no sidecar key can hold (<see cref="ComfyWorkflow.LoraWordsKey"/>, 2026-11-26). Pinned.</summary>
    public const string TriggerWordsBadName = "A LoRA name with a colon, a line break or spaces at its ends can't hold trigger words.";

    /// <summary>The description while no workflow is installed (the tool is not offered then; this is what <c>/tools</c> shows). Pinned.</summary>
    public const string DescribeEmpty = "Generates pictures on the user's ComfyUI server from one of their workflows (none is installed yet: see ComfyUI workflows on /tools).";

    // ── The workflow manager's Edit (2026-11-25): why a part of a graph can't be changed here, shown dim in its row ──

    public const string EditNotPlainJson = "the file has comments, trailing commas or a repeated key: edit it in Open the JSON";
    public const string EditNotApiGraph = "the file holds no API-format graph";
    public const string EditNotInGraph = "not in this workflow";
    public const string EditCustomSampler = "set by a custom sampler";
    public const string EditNoCheckpointVae = "the workflow loads no checkpoint, so it needs a VAE file";
    public const string EditNoCheckpointClip = "only on a checkpoint's own CLIP";
    public const string EditSeveralVaes = "the VAE comes from several nodes";
    public const string EditNoSampler = "no sampler";
    public const string EditSamplersDiffer = "the samplers load different models or LoRAs";
    public const string EditLoop = "the model's links run in a loop";
    public const string EditBehindSwitch = "behind a switch: only a strength changes here";
    public const string EditSwitchedOff = "switched off";
    public const string EditStrengthNotLiteral = "its strength is set by another node";

    /// <summary>More than one node could hold the part ("3 samplers").</summary>
    public static string EditSeveral(int count, string what) => count.ToString(CultureInfo.InvariantCulture) + " " + what;

    /// <summary>The part is a link from another node.</summary>
    public static string EditSetBy(string id, string type) => $"set by node {id} {type}";

    /// <summary>The model comes from a loader other than a checkpoint or a diffusion model's.</summary>
    public static string EditOtherLoader(string type) => $"loaded by {type}";

    /// <summary>A switch on the model's path whose choice isn't a plain true or false.</summary>
    public static string EditSwitchUnknown(string id) => $"the switch at node {id} is set by another node";

    /// <summary>A node with a LoRA that isn't one of ComfyUI's two plain loaders.</summary>
    public static string EditOtherLoraNode(string type) => $"loaded by {type}";

    /// <summary>The model's path runs through a node that is no loader, LoRA or model patch.</summary>
    public static string EditPathEnds(string id, string type) => $"the model passes through node {id} {type}";

    // ── The workflow files (2026-11-25, the workflow manager) ──

    /// <summary>A name Windows keeps for a device (<c>con</c>, <c>nul</c> …): no file can carry it.</summary>
    public static string DeviceName(string name) => $"'{name}' is a name Windows keeps for a device";

    /// <summary>A <c>.md</c> left behind under the new name, which the new workflow would take over.</summary>
    public static string LeftoverSidecar(string path) => $"{path} is already there without its workflow: delete or rename it first";

    /// <summary>A workflow file changed on disk since it was read for the edit.</summary>
    public static string ChangedOnDisk(string path) => $"{path} changed on disk since it was read: nothing was saved, so open the workflow again";

    /// <summary>A save that would leave no workflow the catalog can load.</summary>
    public static string SaveRefused(string problem) => $"the result {problem}, so nothing was saved";

    /// <summary>A file whose encoding can't hold the new text.</summary>
    public static string CannotEncode(string path) => $"{path}'s encoding can't hold every character of the new text, so nothing was saved";

    /// <summary>A workflow gone meanwhile (another instance, a hand edit).</summary>
    public static string WorkflowGone(string name) => $"workflow '{name}' is no longer there: nothing was done";

    /// <summary>The <c>.json</c> deleted but its <c>.md</c> refused.</summary>
    public static string SidecarStays(string path, string why) => $"{path} stays: {why}";

    public static string LogAdded(string name, string folder) => $"Added workflow {name} to {folder}.";

    public static string LogSaved(string name, string folder) => $"Saved workflow {name} in {folder}.";

    public static string LogRenamed(string from, string to) => $"Renamed workflow {from} to {to}.";

    public static string LogDuplicated(string from, string to) => $"Copied workflow {from} as {to}.";

    public static string LogMoved(string name, string to) => $"Moved workflow {name} to {to}.";

    public static string LogDeleted(string name, string folder) => $"Deleted workflow {name} from {folder}.";

    // ── The test and the server check (2026-11-25, the workflow manager) ──

    /// <summary>A check finding: the server has no node of the class.</summary>
    public static string CheckNoNode(string id, string classType) => $"node {id}: the server has no {classType} node";

    /// <summary>A check finding: a literal value not on the server's list (a custom node may take more, so not "missing").</summary>
    public static string CheckNotListed(string id, string classType, string input, string value) => $"node {id} {classType}.{input}: '{value}' is not on the server's list";

    /// <summary>A picture with ComfyUI's UI graph alone (2026-11-25, the import from a picture): the end of a sentence about the file.</summary>
    public const string EmbeddedUiOnly = "holds only ComfyUI's UI-format workflow, not the graph it ran; in ComfyUI use Workflow → Export (API)";

    /// <summary>A picture with no ComfyUI graph in it: the end of a sentence about the file.</summary>
    public const string EmbeddedNone = "holds no ComfyUI workflow (the server may run with its metadata off)";

    /// <summary>A file that is neither JSON nor a PNG or WebP: the end of a sentence about the file.</summary>
    public const string NotWorkflowOrPicture = "is not a ComfyUI export or a PNG or WebP picture";

    /// <summary><c>/history</c> still over its cap when asked for fewer runs.</summary>
    public const string HistoryTooLarge = "Error: the server's run history is too large to read";

    /// <summary>The import's run list with nothing in it.</summary>
    public const string NoRuns = "ComfyUI remembers no run since it started.";

    public static string LogImported(string from, string outcome) => $"Imported a workflow from {from}: {outcome}.";

    public static string LogTested(string name, string outcome) => $"Tested workflow {name}: {outcome}.";

    public static string LogChecked(int workflows, int findings) => $"Checked {workflows} workflow(s) against the server: {findings} finding(s).";

    public static string LogConnection(string outcome) => $"Connection test: {outcome}.";
}
