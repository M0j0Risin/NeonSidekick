using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Comfy;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The ComfyUI image tools (2026-09-24): the workflow templates, the catalog, the client over a stub server, the engine, the two tools and <c>/imagine</c>'s parse.</summary>
public sealed class ComfyTests : IDisposable
{
    private const string Server = "http://comfy.lan:8188";

    /// <summary>A small API-format text-to-image graph: a checkpoint, the two prompts, a latent, a sampler and a save.</summary>
    private const string Txt2Img = """
        {
          "3": { "class_type": "KSampler", "inputs": { "seed": "{{seed}}", "steps": "{{steps}}", "cfg": "{{cfg}}", "denoise": 1, "model": ["4", 0], "positive": ["6", 0], "negative": ["7", 0], "latent_image": ["5", 0] } },
          "4": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "ponyDiffusionV6XL.safetensors" } },
          "5": { "class_type": "EmptyLatentImage", "inputs": { "width": "{{width}}", "height": "{{height}}", "batch_size": 1 } },
          "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "{{prompt}}", "clip": ["4", 1] } },
          "7": { "class_type": "CLIPTextEncode", "inputs": { "text": "{{negative}}", "clip": ["4", 1] } },
          "9": { "class_type": "SaveImage", "inputs": { "filename_prefix": "neon-{{seed}}", "images": ["8", 0] } }
        }
        """;

    /// <summary>An image-to-image graph: LoadImage takes {{image}}, the sampler {{denoise}}.</summary>
    private const string Img2Img = """
        {
          "3": { "class_type": "KSampler", "inputs": { "seed": "{{seed}}", "denoise": "{{denoise}}" } },
          "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "{{prompt}}" } },
          "10": { "class_type": "LoadImage", "inputs": { "image": "{{image}}" } }
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _profileComfy;
    private readonly string _globalComfy;
    private readonly string _root;
    private readonly WorkingDirectory _files;
    private readonly AppSettingsData _settings = new() { ComfyUrl = Server, ComfyTools = true };   // the switch off by default since 2026-09-29
    private readonly StubHttpMessageHandler _stub = new();
    private readonly ComfyStudio _studio;

    public ComfyTests()
    {
        _profileComfy = Path.Combine(_dir, "profile", "comfy");
        _globalComfy = Path.Combine(_dir, "home", "comfy");
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_profileComfy);
        Directory.CreateDirectory(_globalComfy);
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, new ManualTimeProvider());
        var catalog = new ComfyWorkflowCatalog(() => [_profileComfy, _globalComfy]);
        _studio = new ComfyStudio(catalog, _files, () => _settings, url => new ComfyClient(url, new HttpClient(_stub), TimeSpan.FromMilliseconds(1)), new Random(7));
    }

    public void Dispose()
    {
        _studio.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Installs a workflow and ticks it in <c>ComfyUI workflows offered</c>, as the wizard's offer row would (2026-10-01: nothing is
    /// offered until ticked); a test that narrows sets the list itself after installing.
    /// </summary>
    private void Workflow(string name, string json, string? sidecar = null, bool global = false)
    {
        string folder = global ? _globalComfy : _profileComfy;
        _settings.ComfyWorkflowsOffered = [.. _settings.ComfyWorkflowsOffered ?? [], name];
        File.WriteAllText(Path.Combine(folder, name + ".json"), json);
        if (sidecar is not null)
        {
            File.WriteAllText(Path.Combine(folder, name + ".md"), sidecar);
        }
    }

    private static ComfyWorkflow Parse(string name, string json, string? sidecar = null)
    {
        Assert.True(ComfyWorkflow.TryParse(name, name + ".json", json, sidecar, out var workflow, out string? problem), problem);
        return workflow!;
    }

    private static byte[] Picture() => SmokeChecks.SolidBmp(8, 8);

    /// <summary>The stub as a server that queues, finishes on the second history read and serves one picture.</summary>
    private void ServeOnePicture(string promptId = "p-1")
    {
        int reads = 0;
        _stub.Map(Server + "/prompt", HttpStatusCode.OK, "{\"prompt_id\":\"" + promptId + "\",\"number\":1,\"node_errors\":{}}");
        _stub.Map(Server + "/history/", (_, _) => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK,
            ++reads < 2 ? "{}" : "{\"" + promptId + "\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"neon_00001_.png\",\"subfolder\":\"\",\"type\":\"output\"}]}},\"status\":{\"status_str\":\"success\",\"completed\":true}}}")));
        _stub.Map(Server + "/view", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, Picture(), "image/png")));
        _stub.Map(Server + "/upload/image", HttpStatusCode.OK, "{\"name\":\"input.bmp\",\"subfolder\":\"\",\"type\":\"input\"}");
    }

    private JsonElement QueuedGraph(int index = 0)
    {
        var body = _stub.Requests.Where(r => r.Uri.AbsolutePath == "/prompt").ElementAt(index).Body!;
        return JsonDocument.Parse(body).RootElement.GetProperty("prompt").Clone();
    }

    private static AIFunctionArguments Args(params (string Name, object? Value)[] values) => new(values.ToDictionary(v => v.Name, v => v.Value));

    // ── the glyphs (2026-09-26) ─────────────────────────────────────────────

    [Fact]
    public void TheGlyph_IsTheFramedPictureFromText_AndThePaletteFromAPicture()
    {
        Assert.Equal("🖼️ ", ComfyText.GlyphFor(0));
        Assert.Equal("🎨 ", ComfyText.GlyphFor(1));
        Assert.Equal("🎨 ", ComfyText.GlyphFor(2));
        Assert.Equal("🖼️", ComfyText.GeneratingLabelFor(0));
        Assert.Equal("🎨", ComfyText.GeneratingLabelFor(1));
        Assert.True(ComfyText.IsGeneratingLabel("🖼️"));
        Assert.True(ComfyText.IsGeneratingLabel("🎨"));
        Assert.False(ComfyText.IsGeneratingLabel("thinking"));
        // Two cells each, so the busy row's arithmetic is the same whichever is drawn.
        Assert.Equal(2, NeonSidekick.UI.TextCells.Width(ComfyText.TextToImageLabel));
        Assert.Equal(2, NeonSidekick.UI.TextCells.Width(ComfyText.GeneratingLabel));
    }

    // ── the template ────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_ReadsThePlaceholders_AndTheSidecar_AndGuessesTheFamilyFromTheName()
    {
        var workflow = Parse("pony-txt2img", Txt2Img);
        Assert.Equal(ComfyFamily.Pony, workflow.Family);
        Assert.False(workflow.TakesImage);
        Assert.Equal(["cfg", "height", "negative", "prompt", "seed", "steps", "width"], workflow.Placeholders.Order(StringComparer.Ordinal));
        Assert.Equal(ComfyFamilies.PonyNegative, workflow.Defaults.Negative);

        var described = Parse("portraits", Txt2Img, "---\ndescription: Anime portraits\nfamily: flux\nwidth: 832\nheight: 1216\nsteps: 12\nnegative: \"blurry\"\n---\nKeep it short.\n");
        Assert.Equal(ComfyFamily.Flux, described.Family);
        Assert.Equal("Anime portraits", described.Description);
        Assert.Equal(new ComfyDefaults(832, 1216, 12, 1.0, "blurry"), described.Defaults);
        Assert.Equal("Keep it short.", described.Tips);
        Assert.True(Parse("edit", Img2Img).TakesImage);
    }

    [Fact]
    public void TryParse_RefusesAUiSave_AGraphWithoutClassTypes_NonJson_AndNoPrompt()
    {
        Assert.False(ComfyWorkflow.TryParse("x", "x.json", "{\"nodes\":[],\"links\":[]}", null, out _, out string? ui));
        Assert.Equal(ComfyWorkflow.UiFormatProblem, ui);
        Assert.False(ComfyWorkflow.TryParse("x", "x.json", "{\"1\":{\"inputs\":{}}}", null, out _, out string? api));
        Assert.Equal(ComfyWorkflow.NotApiFormatProblem, api);
        Assert.False(ComfyWorkflow.TryParse("x", "x.json", "not json", null, out _, out string? json));
        Assert.Equal(ComfyWorkflow.NotJsonProblem, json);
        Assert.False(ComfyWorkflow.TryParse("x", "x.json", "{\"1\":{\"class_type\":\"SaveImage\",\"inputs\":{}}}", null, out _, out string? prompt));
        Assert.Equal(ComfyWorkflow.NoPromptProblem, prompt);
    }

    [Fact]
    public void Fill_PutsNumbersAsNumbers_TextInPlace_AndTheDefaultsWhereTheCallIsSilent()
    {
        var graph = Parse("pony", Txt2Img).Fill(new ComfyValues("score_9, (1girl:1.2), neon", "score_4", 42, Width: 768));
        var sampler = graph["3"]!["inputs"]!;
        Assert.Equal(42L, sampler["seed"]!.GetValue<long>());
        Assert.Equal(25, sampler["steps"]!.GetValue<int>());           // pony's default
        Assert.Equal(7.0, sampler["cfg"]!.GetValue<double>());
        Assert.Equal(768, graph["5"]!["inputs"]!["width"]!.GetValue<int>());
        Assert.Equal(1024, graph["5"]!["inputs"]!["height"]!.GetValue<int>());
        Assert.Equal("score_9, (1girl:1.2), neon", graph["6"]!["inputs"]!["text"]!.GetValue<string>());
        Assert.Equal("score_4", graph["7"]!["inputs"]!["text"]!.GetValue<string>());
        Assert.Equal("neon-42", graph["9"]!["inputs"]!["filename_prefix"]!.GetValue<string>());   // a numeric placeholder inside a string is its text
        Assert.Equal(ComfyWorkflow.DefaultImageDenoise, Parse("edit", Img2Img).Fill(new ComfyValues("x", "", 1, Images: ["a.png"]))["3"]!["inputs"]!["denoise"]!.GetValue<double>());
    }

    [Fact]
    public void Families_ParseTheirWords_GuessFromNames_AndHaveAGuideEach()
    {
        Assert.True(ComfyFamilies.TryParse("SD1.5", out var sd15));
        Assert.Equal(ComfyFamily.Sd15, sd15);
        Assert.True(ComfyFamilies.TryParse("pdxl", out var pony));
        Assert.Equal(ComfyFamily.Pony, pony);
        Assert.False(ComfyFamilies.TryParse("dalle", out _));
        Assert.Equal(ComfyFamily.Juggernaut, ComfyFamilies.Guess("juggernaut-xl"));   // its own family since later on 2026-09-24
        Assert.Equal(ComfyFamily.Sdxl, ComfyFamilies.Guess("famegridSDXL_photoRealV15"));
        Assert.Equal(ComfyFamily.Other, ComfyFamilies.Guess("upscale"));
        Assert.Contains("score_9, score_8_up, score_7_up", ComfyFamilies.StyleGuide(ComfyFamily.Pony));
        Assert.Contains("natural-language sentences", ComfyFamilies.StyleGuide(ComfyFamily.Flux));
    }

    // ── the catalog ─────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_ProfileShadowsGlobal_SkipsABadFile_AndSeesAnEdit()
    {
        Workflow("pony", Txt2Img, "---\ndescription: mine\n---\n");
        Workflow("pony", Txt2Img, "---\ndescription: global\n---\n", global: true);
        Workflow("edit", Img2Img, global: true);
        Workflow("broken", "{\"nodes\":[],\"links\":[]}");
        var catalog = new ComfyWorkflowCatalog(() => [_profileComfy, _globalComfy, Path.Combine(_dir, "missing")]);

        var (workflows, problems) = catalog.Scan();

        Assert.Equal(["edit", "pony"], workflows.Select(w => w.Name));
        Assert.Equal("mine", workflows.Single(w => w.Name == "pony").Description);
        Assert.Equal(ComfyWorkflow.UiFormatProblem, Assert.Single(problems).Problem);

        File.Delete(Path.Combine(_profileComfy, "pony.json"));
        Assert.Equal("global", catalog.Workflows.Single(w => w.Name == "pony").Description);
    }

    // ── the client ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Client_QueuesPollsAndFetches_ThePicture()
    {
        ServeOnePicture();
        using var client = new ComfyClient(new Uri(Server + "/"), new HttpClient(_stub), TimeSpan.FromMilliseconds(1));

        var run = await client.RunAsync(Parse("pony", Txt2Img).Fill(new ComfyValues("a cat", "", 5)), TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(run.Ok, run.Error);
        Assert.Equal("p-1", run.PromptId);
        Assert.Equal("neon_00001_.png", Assert.Single(run.Images).FileName);
        Assert.Equal(Picture(), run.Images[0].Bytes);
        Assert.Contains(_stub.Requests, r => r.Uri.PathAndQuery == "/view?filename=neon_00001_.png&subfolder=&type=output");
        Assert.Equal(2, _stub.Requests.Count(r => r.Uri.AbsolutePath == "/history/p-1"));
    }

    [Fact]
    public async Task Client_SaysWhichNodeComfyRefused_AndAnExecutionError_AndAClosedPort()
    {
        _stub.Map(Server + "/prompt", HttpStatusCode.BadRequest,
            "{\"error\":{\"type\":\"prompt_outputs_failed_validation\",\"message\":\"Prompt outputs failed validation\"},\"node_errors\":{\"4\":{\"errors\":[{\"message\":\"Value not in list\",\"details\":\"ckpt_name: 'x.safetensors' not in []\"}],\"class_type\":\"CheckpointLoaderSimple\"}}}");
        using var client = new ComfyClient(new Uri(Server), new HttpClient(_stub), TimeSpan.FromMilliseconds(1));
        var graph = Parse("pony", Txt2Img).Fill(new ComfyValues("a", "", 1));

        var refused = await client.RunAsync(graph, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("Error: ComfyUI refused the workflow (HTTP 400): Prompt outputs failed validation; node 4 (CheckpointLoaderSimple): Value not in list: ckpt_name: 'x.safetensors' not in []", refused.Error);

        var failing = new StubHttpMessageHandler()
            .Map(Server + "/prompt", HttpStatusCode.OK, "{\"prompt_id\":\"p-2\"}")
            .Map(Server + "/history/", HttpStatusCode.OK, "{\"p-2\":{\"outputs\":{},\"status\":{\"status_str\":\"error\",\"completed\":false,\"messages\":[[\"execution_error\",{\"node_type\":\"KSampler\",\"exception_message\":\"CUDA out of memory\"}]]}}}");
        using var failingClient = new ComfyClient(new Uri(Server), new HttpClient(failing), TimeSpan.FromMilliseconds(1));
        Assert.Equal("Error: ComfyUI failed running the workflow: KSampler: CUDA out of memory", (await failingClient.RunAsync(graph, TimeSpan.FromSeconds(5), CancellationToken.None)).Error);

        using var closed = new ComfyClient(new Uri(Server), new HttpClient(new StubHttpMessageHandler()), TimeSpan.FromMilliseconds(1));
        Assert.StartsWith("Error: ComfyUI at http://comfy.lan:8188 did not answer:", (await closed.RunAsync(graph, TimeSpan.FromSeconds(5), CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Client_TimesOut_WithTheJobsId()
    {
        _stub.Map(Server + "/prompt", HttpStatusCode.OK, "{\"prompt_id\":\"p-3\"}");
        _stub.Map(Server + "/history/", HttpStatusCode.OK, "{}");
        using var client = new ComfyClient(new Uri(Server), new HttpClient(_stub), TimeSpan.FromMilliseconds(5));

        var run = await client.RunAsync(Parse("pony", Txt2Img).Fill(new ComfyValues("a", "", 1)), TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.StartsWith("Error: no picture within", run.Error);
        Assert.Contains("(p-3)", run.Error);
    }

    [Fact]
    public async Task Client_Status_IsOneLine()
    {
        _stub.Map(Server + "/system_stats", HttpStatusCode.OK,
            "{\"system\":{\"comfyui_version\":\"0.3.40\",\"python_version\":\"3.12.7 (main)\"},\"devices\":[{\"name\":\"cuda:0 NVIDIA GeForce RTX 4090\",\"vram_total\":25769803776,\"vram_free\":19541180416}]}");
        using var client = new ComfyClient(new Uri(Server), new HttpClient(_stub));

        var (ok, text) = await client.StatusAsync(CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("ComfyUI 0.3.40 · Python 3.12.7 · cuda:0 NVIDIA GeForce RTX 4090 (18.2 of 24.0 GB VRAM free)", text);
    }

    // ── the engine and the tool ─────────────────────────────────────────────

    [Fact]
    public async Task GenerateImage_WritesThePony_Prompt_SavesThePicture_AndHandsItToTheModel()
    {
        Workflow("pony-txt2img", Txt2Img);
        ServeOnePicture();
        var tool = new GenerateImageTool(_studio);

        var result = Assert.IsType<ToolImageResult>(await tool.InvokeAsync(Args(("prompt", "score_9, score_8_up, 1girl"), ("seed", 1234))));

        Assert.StartsWith("generated 1 picture with pony-txt2img (seed 1234, 1024×1024): comfy_images\\pony-txt2img-1234.png — the picture is in the next message", result.Text);
        Assert.Contains("\nnegative: " + ComfyFamilies.PonyNegative, result.Text);   // not verbatim: the family's negative
        // The params as the graph carried them (2026-09-25): the family's size, steps and cfg, the graph's own denoise; no sampler input, no sampler.
        var pony = ComfyFamilies.Defaults(ComfyFamily.Pony);
        Assert.EndsWith("\n" + ComfyText.Parameters(new ComfyParameters(pony.Width, pony.Height, pony.Steps, pony.Cfg, 1, 1234)), result.Text);
        Assert.DoesNotContain("sampler", result.Text);
        Assert.Equal("comfy_images\\pony-txt2img-1234.png", Assert.Single(result.Images).Path);
        Assert.True(File.Exists(Path.Combine(_root, "comfy_images", "pony-txt2img-1234.png")));
        // To the model as a JPEG (2026-10-03, a render rides every request after); the file saved is the server's own bytes.
        Assert.Equal(ImageFile.Jpeg, result.Images[0].MediaType);
        Assert.Equal(Picture(), File.ReadAllBytes(Path.Combine(_root, "comfy_images", "pony-txt2img-1234.png")));
        Assert.Equal("score_9, score_8_up, 1girl", QueuedGraph().GetProperty("6").GetProperty("inputs").GetProperty("text").GetString());
        Assert.Contains("pony (Pony Diffusion XL)", tool.Description);
        Assert.Contains("pony-txt2img · pony · text → image · 1024×1024", tool.Description);
    }

    /// <summary>
    /// The stub as a server whose job runs until <paramref name="release"/> says done: the history is empty until then, and
    /// <paramref name="polled"/> is set on the first read. <c>/queue</c> and <c>/interrupt</c> answer, recorded.
    /// </summary>
    private void ServeAHeldPicture(Func<bool> release, TaskCompletionSource polled)
    {
        _stub.Map(Server + "/prompt", HttpStatusCode.OK, "{\"prompt_id\":\"p-1\",\"number\":1,\"node_errors\":{}}");
        _stub.Map(Server + "/history/", (_, _) =>
        {
            polled.TrySetResult();
            return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK,
                release() ? "{\"p-1\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"neon_00001_.png\",\"subfolder\":\"\",\"type\":\"output\"}]}},\"status\":{\"status_str\":\"success\",\"completed\":true}}}" : "{}"));
        });
        _stub.Map(Server + "/view", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, Picture(), "image/png")));
        _stub.Map(Server + "/queue", HttpStatusCode.OK, "{}");
        _stub.Map(Server + "/interrupt", HttpStatusCode.OK, "{}");
    }

    /// <summary>
    /// The hint row's double-click (2026-09-28, <see cref="ComfyStudio.Drain"/>): the running generation stops — its prompt
    /// taken out of ComfyUI's queue and interrupted by id — the tool tells the model the user cancelled, and a generation
    /// started after the drain runs.
    /// </summary>
    [Fact]
    public async Task Drain_CancelsTheRunningGeneration_DeletesAndInterruptsItsPrompt_AndTheNextOneRuns()
    {
        Workflow("pony-txt2img", Txt2Img);
        bool done = false;
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServeAHeldPicture(() => Volatile.Read(ref done), polled);
        Assert.Equal(0, _studio.Drain());   // nothing running: nothing to say

        var running = new GenerateImageTool(_studio).InvokeAsync(Args(("prompt", "a cat")));
        await polled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(_studio.Busy);

        Assert.Equal(1, _studio.Drain());

        Assert.Equal(ComfyText.CancelledByUser, await running.AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(_studio.Busy);
        Assert.Equal("{\"delete\":[\"p-1\"]}", Assert.Single(_stub.Requests, r => r.Uri.AbsolutePath == "/queue").Body);
        Assert.Equal("{\"prompt_id\":\"p-1\"}", Assert.Single(_stub.Requests, r => r.Uri.AbsolutePath == "/interrupt").Body);

        Volatile.Write(ref done, true);
        var next = await _studio.GenerateAsync(new ComfyRequest("a dog"), CancellationToken.None);
        Assert.True(next.Ok, next.Text);
    }

    /// <summary>The caller's own cancel (Esc, the turn's end) is still rethrown, the drain's token untouched.</summary>
    [Fact]
    public async Task Drain_LeavesTheCallersOwnCancel_Rethrown()
    {
        Workflow("pony-txt2img", Txt2Img);
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServeAHeldPicture(() => false, polled);
        using var cts = new CancellationTokenSource();

        var running = new GenerateImageTool(_studio).InvokeAsync(Args(("prompt", "a cat")), cts.Token).AsTask();
        await polled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.False(_studio.Busy);
        Assert.Equal("(image generation cancelled)", ComfyText.Drained(1));
        Assert.Equal("(3 image generations cancelled)", ComfyText.Drained(3));
    }

    [Fact]
    public async Task GenerateImage_Verbatim_SendsTheUsersTextAsTyped_WithNoDefaultNegative_AndANewNameWhenTaken()
    {
        Workflow("pony-txt2img", Txt2Img);
        ServeOnePicture();
        Directory.CreateDirectory(Path.Combine(_root, "comfy_images"));
        File.WriteAllBytes(Path.Combine(_root, "comfy_images", "pony-txt2img-7.png"), [1]);
        const string typed = "score_9, score_8_up, score_7_up, source_anime, rating_safe, (neon city:1.3), 1girl";

        var result = Assert.IsType<ToolImageResult>(await new GenerateImageTool(_studio).InvokeAsync(Args(("prompt", typed), ("verbatim", true), ("seed", 7))));

        var graph = QueuedGraph();
        Assert.Equal(typed, graph.GetProperty("6").GetProperty("inputs").GetProperty("text").GetString());
        Assert.Equal("", graph.GetProperty("7").GetProperty("inputs").GetProperty("text").GetString());
        Assert.Equal("comfy_images\\pony-txt2img-7-2.png", Assert.Single(result.Images).Path);
    }

    [Fact]
    public async Task GenerateImage_Img2Img_UploadsTheOriginal_AndPicksTheImageWorkflow()
    {
        Workflow("pony-txt2img", Txt2Img);
        Workflow("restyle", Img2Img);
        ServeOnePicture();
        File.WriteAllBytes(Path.Combine(_root, "photo.bmp"), Picture());

        var result = await new GenerateImageTool(_studio).InvokeAsync(Args(("prompt", "watercolor"), ("image", "photo.bmp"), ("denoise", 0.4)));

        Assert.IsType<ToolImageResult>(result);
        Assert.Contains(_stub.Requests, r => r.Uri.AbsolutePath == "/upload/image");
        var graph = QueuedGraph();
        Assert.Equal("input.bmp", graph.GetProperty("10").GetProperty("inputs").GetProperty("image").GetString());
        Assert.Equal(0.4, graph.GetProperty("3").GetProperty("inputs").GetProperty("denoise").GetDouble());
    }

    /// <summary>A narrowed tool (2026-09-27, <c>/botchat</c>'s bots): only its workflows are described, and another is unknown, never sent.</summary>
    [Fact]
    public async Task GenerateImage_Narrowed_DescribesItsWorkflowsAlone_AndRefusesAnother()
    {
        Workflow("pony-txt2img", Txt2Img);
        Workflow("restyle", Img2Img);
        ServeOnePicture();
        var tool = new GenerateImageTool(_studio, workflows => workflows.Where(w => w.Name == "pony-txt2img").ToList());

        Assert.Contains("\n- pony-txt2img ", tool.Description);
        Assert.DoesNotContain("\n- restyle ", tool.Description);
        var result = await tool.InvokeAsync(Args(("prompt", "watercolor"), ("workflow", "restyle")));

        Assert.StartsWith("Error", Assert.IsType<string>(result));
        Assert.DoesNotContain(_stub.Requests, r => r.Uri.AbsolutePath == "/prompt");
    }

    /// <summary>A narrowed tool chooses from every installed workflow (later on 2026-09-27): one not offered is described and run.</summary>
    [Fact]
    public async Task GenerateImage_Narrowed_UsesAnInstalledWorkflow_ThatIsNotOffered()
    {
        Workflow("pony-txt2img", Txt2Img);
        Workflow("restyle", Img2Img);
        ServeOnePicture();
        _settings.ComfyWorkflowsOffered = ["restyle"];
        var tool = new GenerateImageTool(_studio, workflows => workflows.Where(w => w.Name == "pony-txt2img").ToList());

        Assert.Contains("\n- pony-txt2img ", tool.Description);
        Assert.DoesNotContain("\n- pony-txt2img ", new GenerateImageTool(_studio).Description);   // the main chat's still follows the offered list
        var result = await tool.InvokeAsync(Args(("prompt", "a red fox")));

        Assert.IsType<ToolImageResult>(result);
        Assert.Single(_stub.Requests, r => r.Uri.AbsolutePath == "/prompt");
    }

    /// <summary>A ReActor face swap as exported (later still on 2026-09-24): two pictures in, no sampler, no prompt.</summary>
    private const string FaceSwapExport = """
        {
          "14": { "class_type": "LoadImage", "inputs": { "image": "face.png" } },
          "2": { "class_type": "LoadImage", "inputs": { "image": "target.png" } },
          "5": { "class_type": "ReActorFaceSwap", "inputs": { "enabled": true, "input_image": ["2", 0], "source_image": ["14", 0], "swap_model": "inswapper_128.onnx", "face_restore_model": "codeformer-v0.1.0.pth" } },
          "9": { "class_type": "SaveImage", "inputs": { "filename_prefix": "swap", "images": ["5", 0] } }
        }
        """;

    /// <summary>The same with its placeholders, as a template.</summary>
    private const string FaceSwap = """
        {
          "2": { "class_type": "LoadImage", "inputs": { "image": "{{image}}" } },
          "5": { "class_type": "ReActorFaceSwap", "inputs": { "input_image": ["2", 0], "source_image": ["14", 0] } },
          "14": { "class_type": "LoadImage", "inputs": { "image": "{{image2}}" } }
        }
        """;

    private const string FaceSwapSidecar = "---\ndescription: Face swap\nimage: the picture whose face is replaced\nimage2: the face to put in\n---\n";

    [Fact]
    public void TryParse_CountsTheImageSlots_ReadsTheirRoles_AndTakesAPromptlessImageWorkflow()
    {
        var swap = Parse("faceswap", FaceSwap, FaceSwapSidecar);
        Assert.Equal(2, swap.ImageCount);
        Assert.True(swap.TakesImage);
        Assert.False(swap.TakesPrompt);
        Assert.Equal(["the picture whose face is replaced", "the face to put in"], swap.ImageRoles);
        Assert.Equal("2 images → image (image: the picture whose face is replaced; image2: the face to put in), no prompt", ComfyText.InputShape(swap, roles: true));
        Assert.Equal("2 images → image, no prompt", ComfyText.InputShape(swap));
        Assert.Equal("faceswap · other · 2 images → image (image: the picture whose face is replaced; image2: the face to put in), no prompt · 1024×1024 — Face swap", ComfyText.WorkflowLine(swap));
        Assert.Equal("2 images → image (image: ?; image2: the face), no prompt", ComfyText.InputShape(Parse("x", FaceSwap, "---\nimage2: the face\n---\n"), roles: true));
        Assert.Equal("image → image", ComfyText.InputShape(Parse("edit", Img2Img)));
        Assert.Equal("text → image", ComfyText.InputShape(Parse("pony", Txt2Img)));

        Assert.False(ComfyWorkflow.TryParse("gap", "gap.json", FaceSwap.Replace("{{image}}", "a.png", StringComparison.Ordinal), null, out _, out string? gap));
        Assert.Equal(ComfyWorkflow.GapInImagesProblem, gap);
    }

    [Fact]
    public void Fill_PutsEachUploadInItsSlot()
    {
        var graph = Parse("faceswap", FaceSwap).Fill(new ComfyValues("", "", 1, Images: ["target.png", "face.png"]));
        Assert.Equal("target.png", graph["2"]!["inputs"]!["image"]!.GetValue<string>());
        Assert.Equal("face.png", graph["14"]!["inputs"]!["image"]!.GetValue<string>());
        Assert.Equal("", Parse("faceswap", FaceSwap).Fill(new ComfyValues("", "", 1, Images: ["only.png"]))["14"]!["inputs"]!["image"]!.GetValue<string>());
    }

    [Fact]
    public async Task GenerateImage_AFaceSwap_UploadsBothInOrder_NeedsNoPrompt_AndPicksByCount()
    {
        Workflow("pony-txt2img", Txt2Img);
        Workflow("restyle", Img2Img);
        Workflow("faceswap", FaceSwap, FaceSwapSidecar);
        int uploads = 0;
        _stub.Map(Server + "/upload/image", (_, _) => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK,
            "{\"name\":\"" + (++uploads == 1 ? "up-target.bmp" : "up-face.bmp") + "\",\"subfolder\":\"\",\"type\":\"input\"}")));
        ServeOnePicture();   // after: the first route that matches answers
        File.WriteAllBytes(Path.Combine(_root, "target.bmp"), Picture());
        File.WriteAllBytes(Path.Combine(_root, "face.bmp"), Picture());
        var tool = new GenerateImageTool(_studio);

        var result = Assert.IsType<ToolImageResult>(await tool.InvokeAsync(Args(("image", "target.bmp"), ("image2", "face.bmp"))));

        Assert.Equal(2, uploads);
        var graph = QueuedGraph();
        Assert.Equal("up-target.bmp", graph.GetProperty("2").GetProperty("inputs").GetProperty("image").GetString());
        Assert.Equal("up-face.bmp", graph.GetProperty("14").GetProperty("inputs").GetProperty("image").GetString());
        Assert.DoesNotContain("\nprompt:", result.Text, StringComparison.Ordinal);   // nothing was prompted
        Assert.StartsWith("generated 1 picture with faceswap", result.Text, StringComparison.Ordinal);

        Assert.Equal("Error: workflow 'faceswap' takes 2 input images (image, image2), not 1", (string?)await tool.InvokeAsync(Args(("workflow", "faceswap"), ("image", "target.bmp"))));
        Assert.Equal("Error: workflow 'faceswap' takes 2 input images (image, image2), not 3", (string?)await tool.InvokeAsync(Args(("workflow", "faceswap"), ("image", "a"), ("image2", "b"), ("image3", "c"))));
        Assert.Equal("Error: several workflows fit; name one of: faceswap, pony-txt2img, restyle", (string?)await tool.InvokeAsync(Args(("image", "a"), ("image2", "b"), ("image3", "c"))));
        Assert.Equal(ComfyText.NeedsImage("restyle"), (string?)await tool.InvokeAsync(Args(("prompt", "x"), ("workflow", "restyle"))));
        Assert.Equal(ComfyText.NoPrompt, (string?)await tool.InvokeAsync(Args(("workflow", "restyle"), ("image", "target.bmp"))));
        Assert.Equal("Error: image2 needs image: the input pictures fill image, image2, image3 in order", (string?)await tool.InvokeAsync(Args(("image2", "face.bmp"))));
        Assert.Equal("Error: image3 needs image2: the input pictures fill image, image2, image3 in order", (string?)await tool.InvokeAsync(Args(("image", "a"), ("image3", "c"))));
    }

    [Fact]
    public void Imagine_ReadsImage2_AndRunsAPromptlessWorkflowOnItsPictures()
    {
        var workflows = new[] { Parse("faceswap", FaceSwap), Parse("restyle", Img2Img) };

        var (request, error) = ComfyImagine.Parse("faceswap --image target.png --image2 \"my face.png\"", workflows);

        Assert.Null(error);
        Assert.Equal("faceswap", request!.Workflow);
        Assert.Equal("", request.Prompt);
        Assert.Equal(["target.png", "my face.png"], request.Images);
        Assert.Equal(ComfyText.ImagineUsage, ComfyImagine.Parse("restyle --image a.png", workflows).Error);
        Assert.Equal("Error: --image2 needs --image: the input pictures fill image, image2, image3 in order", ComfyImagine.Parse("faceswap --image2 a.png", workflows).Error);
        Assert.Equal("--image3 takes a path, not ''", ComfyImagine.Parse("faceswap --image a --image3", workflows).Error);
    }

    [Fact]
    public void Import_NumbersTheLoadImages_AndTakesASamplerlessImageGraph()
    {
        var (result, problem) = ComfyImport.Placehold(FaceSwapExport);

        Assert.Null(problem);
        Assert.Equal(2, result!.ImageCount);
        Assert.Equal(["{{image}} → node 2 LoadImage.image", "{{image2}} → node 14 LoadImage.image"], result.Found);   // node-id order
        Assert.Equal((null, null, null, null, null, "", ""), (result.Width, result.Height, result.Steps, result.Cfg, result.Denoise, result.Negative, result.Checkpoint));
        var workflow = Parse("faceswap", result.GraphJson);
        Assert.Equal(["image", "image2"], workflow.Placeholders.Order(StringComparer.Ordinal));
        Assert.Contains("\"swap_model\": \"inswapper_128.onnx\"", result.GraphJson);   // the rest as exported

        string four = "{" + string.Join(",", Enumerable.Range(1, 4).Select(i => $"\"{i}\":{{\"class_type\":\"LoadImage\",\"inputs\":{{\"image\":\"p{i}.png\"}}}}")) + "}";
        var (many, _) = ComfyImport.Placehold(four);
        Assert.Equal(3, many!.ImageCount);
        Assert.Equal(ComfyImport.ImageLeftAsExported("4"), many.Found[^1]);
        Assert.Equal("LoadImage node 4 left as exported: a workflow takes at most 3 input pictures", ComfyImport.ImageLeftAsExported("4"));
    }

    [Fact]
    public void WorkflowFile_WritesTheImageRoles_AndTheyReadBack()
    {
        string sidecar = ComfyWorkflowFile.Sidecar(new ComfySidecar("Face swap", ComfyFamily.Other, null, null, null, null, null, "", ["the target", "", "x"]));
        Assert.Contains("\nimage: the target\n", sidecar, StringComparison.Ordinal);
        Assert.DoesNotContain("image2:", sidecar, StringComparison.Ordinal);
        Assert.Equal(["the target", ""], Parse("faceswap", FaceSwap, sidecar).ImageRoles);
    }

    [Fact]
    public async Task GenerateImage_APastedPicturesLabel_SavesItsOriginalOnce_UploadsIt_AndSaysWhere()
    {
        // A paste as the input (later still on 2026-09-24, the user's ask): [Image #1] is the paste at full size, written
        // under the output folder the first time it is used and uploaded from there; a second use is the same file.
        Workflow("restyle", Img2Img);
        ServeOnePicture();
        byte[] original = SmokeChecks.SolidBmp(3000, 10);   // wider than the 2048 the model was shown
        int asked = 0;
        var time = new ManualTimeProvider();   // 2026-09-11 14:05:30 local
        using var studio = PastingStudio(n => { asked++; return n == 1 ? new PastedPicture(original, null) : null; }, time);
        var tool = new GenerateImageTool(studio);

        var first = Assert.IsType<ToolImageResult>(await tool.InvokeAsync(Args(("prompt", "watercolor"), ("image", "[Image #1]"))));
        time.Advance(TimeSpan.FromMinutes(5));
        var second = Assert.IsType<ToolImageResult>(await tool.InvokeAsync(Args(("prompt", "ink"), ("image", "image 1"))));

        string saved = Path.Combine("comfy_images", ".pasted", "pasted-20260911-140530.bmp");   // inputs apart, stamped with the first use
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_root, saved)));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "comfy_images", ".pasted")));   // the second use reused the file
        Assert.Equal(2, asked);
        var uploads = _stub.Requests.Where(r => r.Uri.AbsolutePath == "/upload/image").ToList();
        Assert.Equal(2, uploads.Count);
        Assert.All(uploads, u => Assert.Contains("pasted-20260911-140530.bmp", u.Body, StringComparison.Ordinal));
        Assert.All(uploads, u => Assert.True(u.Body!.Length > original.Length / 2, "the full-size bytes went up"));
        Assert.Contains("\n" + ComfyText.PastedInput(saved, 1) + "\n", first.Text, StringComparison.Ordinal);
        Assert.Contains(ComfyText.PastedInput(saved, 1), second.Text, StringComparison.Ordinal);
        Assert.Equal("Error: there is no pasted picture [Image #2] in this session", (string?)await tool.InvokeAsync(Args(("prompt", "x"), ("image", "[Image #2]"))));
    }

    private ComfyStudio PastingStudio(Func<int, PastedPicture?> pasted, TimeProvider? time = null) =>
        new(new ComfyWorkflowCatalog(() => [_profileComfy, _globalComfy]), _files, () => _settings,
            url => new ComfyClient(url, new HttpClient(_stub), TimeSpan.FromMilliseconds(1)), new Random(7), pasted, time ?? new ManualTimeProvider());

    [Fact]
    public async Task GenerateImage_APastedPicture_WithNoOutputFolder_GoesInPasted_UnderTheWorkingDirectory_AndADroppedFileKeepsItsName()
    {
        Workflow("restyle", Img2Img);
        ServeOnePicture();
        _settings.ComfyOutputFolder = "";
        byte[] bytes = SmokeChecks.SolidBmp(8, 8);
        using var studio = PastingStudio(n => n switch
        {
            1 => new PastedPicture(bytes, null),
            2 => new PastedPicture(bytes, "photo.BMP"),
            _ => null,
        });
        var tool = new GenerateImageTool(studio);

        Assert.IsType<ToolImageResult>(await tool.InvokeAsync(Args(("prompt", "a"), ("image", "[Image #1]"))));
        Assert.IsType<ToolImageResult>(await tool.InvokeAsync(Args(("prompt", "b"), ("image", "[Image #2]"))));

        Assert.True(File.Exists(Path.Combine(_root, ".pasted", "pasted-20260911-140530.bmp")));
        Assert.True(File.Exists(Path.Combine(_root, ".pasted", "photo.bmp")));   // its own name, the extension lower-cased
    }

    [Fact]
    public void PastedStem_AndFolder_ArePinned()
    {
        Assert.Equal(".pasted", ComfyStudio.PastedFolderName);
        Assert.Equal("pasted-20260924-153012", ComfyStudio.PastedStem(new DateTimeOffset(2026, 9, 24, 15, 30, 12, TimeSpan.FromHours(-7))));
    }

    [Fact]
    public async Task GenerateImage_APastedLabel_WithNoPasteStore_IsTheNoPasteError()
    {
        Workflow("restyle", Img2Img);
        Assert.Equal(ComfyText.NoPastedPicture(1), (string?)await new GenerateImageTool(_studio).InvokeAsync(Args(("prompt", "x"), ("image", "[Image #1]"))));
    }

    [Theory]
    [InlineData("[Image #1]", 1)]
    [InlineData("Image #12", 12)]
    [InlineData(" image 3 ", 3)]
    [InlineData("[IMAGE#4]", 4)]
    [InlineData("[ Image # 5 ]", 5)]
    [InlineData("image1", 1)]
    [InlineData("Image 1.png", 0)]
    [InlineData("images/1.png", 0)]
    [InlineData("[Image #0]", 0)]
    [InlineData("[Image #]", 0)]
    [InlineData("photo.png", 0)]
    [InlineData("[Pasted text #1 +3 lines]", 0)]
    public void TryPastedLabel_IsTheLabelAlone(string image, int expected)
    {
        Assert.Equal(expected > 0, ComfyText.TryPastedLabel(image, out int number));
        Assert.Equal(expected, expected > 0 ? number : 0);
    }

    [Fact]
    public void PastedInputWording_IsPinned()
    {
        Assert.Equal("input: comfy_images\\.pasted\\pasted-1.png (the pasted [Image #1], saved at full size)", ComfyText.PastedInput("comfy_images\\.pasted\\pasted-1.png", 1));
        Assert.Equal("Error: there is no pasted picture [Image #3] in this session", ComfyText.NoPastedPicture(3));
        string input = ComfyText.PastedInput("in.png", 1);
        Assert.Equal("generated 1 picture with a (seed 1, 8×8): x.png\n" + input + "\nprompt: p", ComfyText.Generated("a", 1, 8, 8, ["x.png"], "p", "", attached: false, input: input));
        Assert.Equal([input, "prompt: p"], ComfyText.SplitGenerated(ComfyText.Generated("a", 1, 8, 8, ["x.png"], "p", "", attached: false, input: input)).Details);
    }

    [Fact]
    public async Task GenerateImage_Refusals_AreSentences()
    {
        var tool = new GenerateImageTool(_studio);
        Assert.StartsWith("Error: no ComfyUI workflow found", (string?)await tool.InvokeAsync(Args(("prompt", "x"))));
        Workflow("a", Txt2Img);
        Workflow("b", Txt2Img);
        Assert.Equal("Error: several workflows fit; name one of: a, b", (string?)await tool.InvokeAsync(Args(("prompt", "x"))));
        Assert.Equal("Error: no workflow named 'c'; the workflows are: a, b", (string?)await tool.InvokeAsync(Args(("prompt", "x"), ("workflow", "c"))));
        Assert.Equal("Error: workflow 'a' takes no input image (it has no {{image}} placeholder)", (string?)await tool.InvokeAsync(Args(("prompt", "x"), ("workflow", "a"), ("image", "p.png"))));
        Assert.Equal("Error: width must be 64 to 4096, not '9000'", (string?)await tool.InvokeAsync(Args(("prompt", "x"), ("width", 9000))));
        Assert.Equal("Error: 'lots' is not a number for 'cfg'", (string?)await tool.InvokeAsync(Args(("prompt", "x"), ("cfg", "lots"))));
        Assert.Equal(ComfyText.NoPrompt, (string?)await tool.InvokeAsync(Args(("prompt", " "), ("workflow", "a"))));
        _settings.ComfyUrl = "";
        Assert.Equal(ComfyText.NoServer, (string?)await tool.InvokeAsync(Args(("prompt", "x"))));
    }

    [Fact]
    public void ComfyOffered_NeedsTheSwitch_AServer_AndAWorkflow()
    {
        Assert.False(ChatScreen.ComfyOffered(_settings, _studio));
        Workflow("a", Txt2Img);
        Assert.True(ChatScreen.ComfyOffered(_settings, _studio));
        _settings.ComfyWorkflowsOffered = null;
        Assert.False(ChatScreen.ComfyOffered(_settings, _studio));   // installed but not ticked: null offers none (2026-10-01)
        _settings.ComfyWorkflowsOffered = ["a"];
        Assert.False(ChatScreen.ComfyOffered(new AppSettingsData { ComfyUrl = "ftp://x" }, _studio));
        Assert.False(ChatScreen.ComfyOffered(new AppSettingsData { ComfyUrl = Server, ComfyTools = false }, _studio));
    }

    // ── the splash tool ─────────────────────────────────────────────────────

    [Fact]
    public void SetSplashImage_CopiesIntoTheSplashFolder_WarnsWhenItIsTheOnlyOne_AndNeverOverwrites()
    {
        string splash = Path.Combine(_dir, "profile", "splash");
        File.WriteAllBytes(Path.Combine(_root, "art.bmp"), Picture());
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "hi");
        var tool = new SetSplashImageTool(_files, () => splash);

        Assert.Equal(ComfyText.SplashSet("art.bmp", Path.Combine(splash, "art.bmp"), onlyOne: true), tool.Set("art.bmp", null));
        Assert.Equal(ComfyText.SplashSet("art.bmp", Path.Combine(splash, "art-2.bmp"), onlyOne: false), tool.Set("art.bmp", null));
        Assert.Equal(ComfyText.SplashSet("art.bmp", Path.Combine(splash, "neon.bmp"), onlyOne: false), tool.Set("art.bmp", "neon"));
        Assert.Equal(ComfyText.SplashNotAnImage("notes.txt"), tool.Set("notes.txt", null));
    }

    // ── /imagine ────────────────────────────────────────────────────────────

    [Fact]
    public void Imagine_KeepsThePromptAsTyped_SplitsTheNegative_AndReadsTheFlags()
    {
        var workflows = new[] { Parse("pony-txt2img", Txt2Img), Parse("restyle", Img2Img) };

        var (request, error) = ComfyImagine.Parse("pony-txt2img score_9, score_8_up, (1girl:1.2), neon -- score_4, blurry --seed 99 --size 832x1216 --image \"my pics/a.png\"", workflows);

        Assert.Null(error);
        Assert.Equal("pony-txt2img", request!.Workflow);
        Assert.Equal("score_9, score_8_up, (1girl:1.2), neon", request.Prompt);
        Assert.Equal("score_4, blurry", request.Negative);
        Assert.True(request.Verbatim);
        Assert.Equal(99, request.Seed);
        Assert.Equal((832, 1216), (request.Width!.Value, request.Height!.Value));
        Assert.Equal(["my pics/a.png"], request.Images);

        Assert.Equal("a cat", ComfyImagine.Parse("a cat", workflows).Request!.Prompt);
        Assert.Null(ComfyImagine.Parse("a cat", workflows).Request!.Workflow);
        Assert.Equal(ComfyText.ImagineUsage, ComfyImagine.Parse("  ", workflows).Error);
        Assert.Equal(ComfyText.ImagineUsage, ComfyImagine.Parse("pony-txt2img", workflows).Error);
        Assert.Equal("--size takes WxH (1024x1024), not 'big'", ComfyImagine.Parse("a cat --size big", workflows).Error);
        Assert.Equal("Error: count must be 1 to 5, not '9'", ComfyImagine.Parse("a cat --count 9", workflows).Error);
    }

    [Fact]
    public void Describe_TeachesVerbatim_AndListsTheFamiliesPresentOnly()
    {
        string text = ComfyText.Describe([Parse("flux-dev", Txt2Img, "---\ndescription: Photoreal\n---\nUse long captions.")]);
        Assert.Contains("verbatim true", text);
        Assert.Contains("- flux-dev · flux · text → image · 1024×1024 — Photoreal\n  tips:\n    Use long captions.\nPrompt style by family:", text);
        Assert.Contains(ComfyFamilies.StyleGuide(ComfyFamily.Flux), text);
        Assert.DoesNotContain("Pony Diffusion XL", text);
    }

    /// <summary>ComfyUI show prompts (later still on 2026-09-24): the picture's line, then what was sent, one entry a line.</summary>
    [Fact]
    public void SplitGenerated_HeadThenThePromptLines()
    {
        string result = ComfyText.Generated("pony", 5, 1024, 1024, [@"comfy_images\pony-5.png"], "a cat", "blurry", attached: true);
        var (head, details) = ComfyText.SplitGenerated(result);
        Assert.Equal(@"generated 1 picture with pony (seed 5, 1024×1024): comfy_images\pony-5.png — the picture is in the next message", head);
        Assert.Equal(["prompt: a cat", "negative: blurry"], details);

        Assert.Equal(["prompt: a cat"], ComfyText.SplitGenerated(ComfyText.Generated("flux", 1, 1024, 1024, ["a.png"], "a cat", "", attached: false)).Details);
        // The params line (2026-09-25) last, after the negative.
        Assert.Equal(["prompt: a cat", "negative: blurry", "params: 832×1216, steps 30, cfg 5.5, denoise 0.6, seed 5, sampler euler, scheduler karras"],
            ComfyText.SplitGenerated(ComfyText.Generated("pony", 5, 832, 1216, ["a.png"], "a cat", "blurry", attached: false, parameters: new ComfyParameters(832, 1216, 30, 5.5, 0.6, 5, "euler", "karras"))).Details);
        Assert.Equal(["prompt: a cat"], ComfyText.SplitGenerated(ComfyText.Generated("flux", 1, 1024, 1024, ["a.png"], "a cat", "", attached: false, parameters: new ComfyParameters())).Details);
        Assert.Equal(["prompt: x", "Error: ComfyUI stopped"], ComfyText.SplitGenerated("generated …\r\nprompt: x\n\nError: ComfyUI stopped").Details);
        Assert.Empty(ComfyText.SplitGenerated("Error: no server").Details);
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ComfyShowPrompts, new AppSettingsData(), ""));   // on by default (later still on 2026-09-24)
        Assert.True(SettingsMenu.IsToggle(SettingsField.ComfyShowPrompts));
        Assert.Equal("ComfyUI show prompts", SettingsMenu.FieldName(SettingsField.ComfyShowPrompts));
    }

    /// <summary>The picture strip's toggle (later still on 2026-09-24): on by default, on the ComfyUI tab under show prompts, and its hint words.</summary>
    [Fact]
    public void PictureStrip_Setting_IsAToggle_OnByDefault_UnderShowPrompts()
    {
        Assert.True(new AppSettingsData().ComfyPictureStrip);
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ComfyPictureStrip, new AppSettingsData(), ""));
        Assert.True(SettingsMenu.IsToggle(SettingsField.ComfyPictureStrip));
        Assert.Equal("ComfyUI picture strip", SettingsMenu.FieldName(SettingsField.ComfyPictureStrip));
        var tab = SettingsMenu.ToolsTabFields.Single(fields => fields.Contains(SettingsField.ComfyTools)).ToList();
        Assert.Equal(tab.IndexOf(SettingsField.ComfyShowPrompts) + 1, tab.IndexOf(SettingsField.ComfyPictureStrip));
        Assert.Equal("← → picture 2/7 · Enter opens", ComfyText.StripSelectedHint(2, 7));
    }

    /// <summary>Later still on 2026-09-24, the user's call: tips keep their lines, indented under the workflow, blank runs squeezed to one.</summary>
    [Fact]
    public void Describe_KeepsTheTipsLines_IndentedUnderTheWorkflow()
    {
        string md = "---\nfamily: pony\n---\n### A:  \r\nFormat: tags only.\n  Output: one line.\n\n\n\n### B:\nBe realistic.   \n\n";
        string text = ComfyText.Describe([Parse("pony-txt2img", Txt2Img, md)]);

        Assert.Contains("  tips:\n    ### A:\n    Format: tags only.\n      Output: one line.\n\n    ### B:\n    Be realistic.\nPrompt style by family:", text);
        Assert.Equal("", ComfyText.IndentTips("\n \n"));
    }

    // ── offered, the wizard's pieces, --no-negative (later on 2026-09-24) ─

    /// <summary>A graph as ComfyUI exports it (API format): a LoRA between the checkpoint and the prompts, a ControlNet apply on the positive, KSamplerAdvanced.</summary>
    internal const string Export = """
        {
          "4": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "ponyDiffusionV6XL_v6StartWithThisOne.safetensors" } },
          "10": { "class_type": "LoraLoader", "inputs": { "lora_name": "neon.safetensors", "strength_model": 0.8, "strength_clip": 0.8, "model": ["4", 0], "clip": ["4", 1] } },
          "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "score_9, a cat", "clip": ["10", 1] } },
          "7": { "class_type": "CLIPTextEncode", "inputs": { "text": "score_4, blurry", "clip": ["10", 1] } },
          "12": { "class_type": "ControlNetApply", "inputs": { "strength": 1, "conditioning": ["6", 0], "control_net": ["13", 0], "image": ["14", 0] } },
          "5": { "class_type": "EmptyLatentImage", "inputs": { "width": 832, "height": 1216, "batch_size": 1 } },
          "3": { "class_type": "KSamplerAdvanced", "inputs": { "noise_seed": 5, "steps": 20, "cfg": 6.5, "sampler_name": "euler", "scheduler": "normal", "model": ["10", 0], "positive": ["12", 0], "negative": ["7", 0], "latent_image": ["5", 0] } },
          "9": { "class_type": "SaveImage", "inputs": { "filename_prefix": "x", "images": ["8", 0] } }
        }
        """;

    [Fact]
    public async Task Offered_TheModelSeesTheTickedOnly_ImagineMayNameAny()
    {
        Workflow("pony-txt2img", Txt2Img);
        Workflow("juggernaut-xl", Txt2Img);
        _settings.ComfyWorkflowsOffered = ["PONY-txt2img"];
        ServeOnePicture();
        var tool = new GenerateImageTool(_studio);

        Assert.DoesNotContain("juggernaut-xl", tool.Description);
        var bare = Assert.IsType<ToolImageResult>(await tool.InvokeAsync(Args(("prompt", "x"), ("seed", 3))));
        Assert.StartsWith("generated 1 picture with pony-txt2img", bare.Text);   // the one offered, though two fit
        Assert.Equal("Error: no workflow named 'juggernaut-xl'; the workflows are: pony-txt2img", (string?)await tool.InvokeAsync(Args(("prompt", "x"), ("workflow", "juggernaut-xl"))));
        var named = await _studio.GenerateAsync(new ComfyRequest("x", "juggernaut-xl", Seed: 4, AnyWorkflow: true), CancellationToken.None);
        Assert.StartsWith("generated 1 picture with juggernaut-xl", named.Text);   // /imagine may name a hidden one
        var unnamed = await _studio.GenerateAsync(new ComfyRequest("x", Seed: 6, AnyWorkflow: true), CancellationToken.None);
        Assert.StartsWith("generated 1 picture with pony-txt2img", unnamed.Text);   // and without a name the offered one is the choice
        Assert.Empty(ComfyWorkflowCatalog.Offered(_studio.Catalog.Workflows, null));   // null offers none (2026-10-01; it was every one)

        _settings.ComfyWorkflowsOffered = [];
        Assert.Equal(ComfyText.NoneOffered, (string?)await tool.InvokeAsync(Args(("prompt", "x"))));
        Assert.False(ChatScreen.ComfyOffered(_settings, _studio));
        Assert.Contains("  pony-txt2img · pony · text → image · 1024×1024 · {{cfg}} {{height}} {{negative}} {{prompt}} {{seed}} {{steps}} {{width}} · hidden", ComfyText.StatusLines(_studio.Catalog.Workflows, [], [], true, []));
        Assert.Equal("pony · text → image · 1024×1024 · hidden from the model", ComfyText.CompletionNote(_studio.Catalog.Workflows[1], offered: false));
    }

    /// <summary>/comfy offered (2026-10-04, the user's ask): the offered workflows one bullet each; the switch's line first while it is off; a line of its own for none installed or none ticked.</summary>
    [Fact]
    public void OfferedLines_BulletEachOfferedWorkflow_InTheCatalogsOrder()
    {
        Assert.Equal([ComfyText.NoWorkflowInstalled], ComfyText.OfferedLines([], ["pony-txt2img"], enabled: true));
        Workflow("pony-txt2img", Txt2Img);
        Workflow("juggernaut-xl", Txt2Img);
        Workflow("anything", Txt2Img);
        var workflows = _studio.Catalog.Workflows;

        Assert.Equal([ComfyText.NoneOfferedLine], ComfyText.OfferedLines(workflows, null, enabled: true));
        Assert.Equal([ComfyText.NoneOfferedLine], ComfyText.OfferedLines(workflows, ["gone"], enabled: true));
        var lines = ComfyText.OfferedLines(workflows, ["PONY-txt2img", "anything"], enabled: true);
        Assert.Equal(
            [
                ComfyText.Glyph + "2 workflows offered to the model:",
                "  • " + ComfyText.WorkflowLine(workflows.Single(w => w.Name == "anything")),
                "  • " + ComfyText.WorkflowLine(workflows.Single(w => w.Name == "pony-txt2img")),
            ],
            lines);
        Assert.Equal("  • pony-txt2img · pony · text → image · 1024×1024", lines[2]);
        Assert.Equal([ComfyText.ToolsOffLine, ComfyText.Glyph + "1 workflow offered to the model:", "  • " + ComfyText.WorkflowLine(workflows.Single(w => w.Name == "juggernaut-xl"))],
            ComfyText.OfferedLines(workflows, ["juggernaut-xl"], enabled: false));
    }

    [Fact]
    public void TheCaretMention_ListsEachOfferedWorkflow_WithWhatItIs()
    {
        Workflow("pony-txt2img", Txt2Img);
        Workflow("juggernaut-xl", Txt2Img);
        _settings.ComfyWorkflowsOffered = ["pony-txt2img"];

        var items = ChatScreen.ComfyChoices(_studio.OfferedWorkflows());

        Assert.Equal(["pony-txt2img"], items.Select(i => i.Text));   // the hidden one is no mention: the model could not run it
        Assert.Equal(["pony · text → image · 1024×1024"], items.Select(i => i.Note));
        Assert.Empty(ChatScreen.ComfyChoices([]));
        Assert.True(new AppSettingsData().ComfyCaretMention);
    }

    [Theory]
    [InlineData(false, 0, new[] { "cfg", "height", "negative", "prompt", "seed", "steps", "width" })]
    [InlineData(true, 2, new[] { "cfg", "denoise", "image", "negative", "prompt", "seed", "steps" })]
    public void Graphs_Build_IsAWorkflowWithItsPlaceholders(bool fromImage, int clipSkip, string[] placeholders)
    {
        string json = ComfyGraphs.Build("my-flow", "ponyV6.safetensors", "euler_ancestral", "normal", clipSkip, fromImage);

        var workflow = Parse("my-flow", json);
        Assert.Equal(fromImage, workflow.TakesImage);
        Assert.Equal(placeholders, workflow.Placeholders.Order(StringComparer.Ordinal));
        Assert.Equal(clipSkip >= 2, json.Contains("\"CLIPSetLastLayer\"", StringComparison.Ordinal));
        Assert.Contains("\"neon/my-flow\"", json);
        Assert.Contains("\"ponyV6.safetensors\"", json);
        Assert.Equal("dpmpp_2m_sde", ComfyGraphs.DefaultSampler(ComfyFamily.Sdxl));
        Assert.Equal("karras", ComfyGraphs.DefaultScheduler(ComfyFamily.Sdxl));
        Assert.Equal(2, ComfyGraphs.DefaultClipSkip(ComfyFamily.Pony));
    }

    /// <summary>The params line (2026-09-25, the user's ask) reads what the filled graph carries: placeholders filled, values hardcoded, links skipped.</summary>
    [Fact]
    public void ReadParameters_ReadsTheFilledGraph()
    {
        var built = Parse("my-flow", ComfyGraphs.Build("my-flow", "ponyV6.safetensors", "euler_ancestral", "normal", 2, fromImage: false));
        var filled = ComfyWorkflow.ReadParameters(built.Fill(new ComfyValues("a", "b", 42, 832, 1216, 30, 5.5)));
        Assert.Equal((832, 1216, 30, 5.5, 42L, "euler_ancestral", "normal"), (filled.Width!.Value, filled.Height!.Value, filled.Steps!.Value, filled.Cfg!.Value, filled.Seed!.Value, filled.Sampler, filled.Scheduler));

        // An export's hardcoded values, noise_seed for the seed; the model, the prompts and the latent are links, never read.
        var exported = ComfyWorkflow.ReadParameters((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(Export)!);
        Assert.Equal((20, 6.5, 5L, "euler", "normal"), (exported.Steps!.Value, exported.Cfg!.Value, exported.Seed!.Value, exported.Sampler, exported.Scheduler));

        Assert.Equal(new ComfyParameters(), ComfyWorkflow.ReadParameters(new System.Text.Json.Nodes.JsonObject()));
        Assert.Equal("", ComfyText.Parameters(new ComfyParameters()));
        Assert.Equal("params: 1024×1024, steps 8, cfg 1, seed 18446744073709551", ComfyText.Parameters(new ComfyParameters(1024, 1024, 8, 1.0, Seed: 18446744073709551)));
    }

    [Fact]
    public void Import_FollowsTheConditioningBack_ToThePrompt_AndKeepsTheValuesAsDefaults()
    {
        var (result, problem) = ComfyImport.Placehold(Export);

        Assert.Null(problem);
        var workflow = Parse("imported", result!.GraphJson);
        Assert.Equal(["cfg", "height", "negative", "prompt", "seed", "steps", "width"], workflow.Placeholders.Order(StringComparer.Ordinal));
        Assert.Equal("score_4, blurry", result.Negative);
        Assert.Equal((832, 1216, 20, 6.5), (result.Width!.Value, result.Height!.Value, result.Steps!.Value, result.Cfg!.Value));
        Assert.Null(result.Denoise);
        Assert.False(result.TakesImage);
        Assert.Equal("ponyDiffusionV6XL_v6StartWithThisOne.safetensors", result.Checkpoint);
        Assert.Contains("{{prompt}} → node 6 CLIPTextEncode.text", result.Found);
        Assert.Contains("{{seed}} → node 3 KSamplerAdvanced.noise_seed", result.Found);
        Assert.Contains("\"lora_name\": \"neon.safetensors\"", result.GraphJson);   // the rest as exported

        Assert.Equal(ComfyImport.NoSamplerProblem, ComfyImport.Placehold("{\"1\":{\"class_type\":\"SaveImage\",\"inputs\":{}}}").Problem);
        Assert.Equal(ComfyImport.NoPromptNodeProblem, ComfyImport.Placehold("{\"3\":{\"class_type\":\"KSampler\",\"inputs\":{\"positive\":[\"9\",0]}}}").Problem);
        Assert.Equal(ComfyWorkflow.UiFormatProblem, ComfyImport.Placehold("{\"nodes\":[],\"links\":[]}").Problem);
    }

    [Fact]
    public void WorkflowFile_WritesBothFiles_RefusesATakenOrBadName_AndTheSidecarReadsBack()
    {
        string sidecar = ComfyWorkflowFile.Sidecar(new ComfySidecar("Anime: portraits", ComfyFamily.Pony, 832, 1216, 25, 7, null, ""));
        Assert.Null(ComfyWorkflowFile.Add(_profileComfy, [_profileComfy, _globalComfy], "pony-new", Txt2Img, sidecar));

        var workflow = Assert.Single(_studio.Catalog.Workflows);
        Assert.Equal(("pony-new", "Anime: portraits", ComfyFamily.Pony), (workflow.Name, workflow.Description, workflow.Family));
        Assert.Equal(new ComfyDefaults(832, 1216, 25, 7, ""), workflow.Defaults);   // an empty negative is none, not the family's
        Workflow("taken", Txt2Img, global: true);
        Assert.Equal(ComfyText.NameTaken("taken", _globalComfy), ComfyWorkflowFile.Add(_profileComfy, [_profileComfy, _globalComfy], "taken", Txt2Img, sidecar));
        Assert.Equal("the name must be " + ComfyWorkflowFile.NameRule, ComfyWorkflowFile.Add(_profileComfy, [], "Bad Name", Txt2Img, sidecar));
        Assert.Equal("ponydiffusionv6xl_v6startwiththisone", ComfyWorkflowFile.Suggest("ponyDiffusionV6XL_v6StartWithThisOne.safetensors"));
        Assert.Equal("my-export", ComfyWorkflowFile.Suggest(@"C:\Downloads\My Export!.json"));
    }

    [Fact]
    public async Task Choices_ReadBothComboShapes_AndSayWhyNot()
    {
        _stub.Map(Server + "/object_info/CheckpointLoaderSimple", HttpStatusCode.OK, "{\"CheckpointLoaderSimple\":{\"input\":{\"required\":{\"ckpt_name\":[[\"a.safetensors\",\"b.safetensors\"]]}}}}");
        _stub.Map(Server + "/object_info/KSampler", HttpStatusCode.OK, "{\"KSampler\":{\"input\":{\"required\":{\"sampler_name\":[\"COMBO\",{\"options\":[\"euler\",\"dpmpp_2m\"]}]}}}}");
        using var client = new ComfyClient(new Uri(Server), new HttpClient(_stub));

        Assert.Equal(["a.safetensors", "b.safetensors"], (await client.ChoicesAsync("CheckpointLoaderSimple", "ckpt_name", CancellationToken.None)).Choices);
        Assert.Equal(["euler", "dpmpp_2m"], (await client.ChoicesAsync("KSampler", "sampler_name", CancellationToken.None)).Choices);
        Assert.Equal(ComfyText.BadAnswer("/object_info/KSampler"), (await client.ChoicesAsync("KSampler", "scheduler", CancellationToken.None)).Error);
        using var closed = new ComfyClient(new Uri(Server), new HttpClient(new StubHttpMessageHandler()));
        Assert.StartsWith("Error: ComfyUI at", (await closed.ChoicesAsync("KSampler", "scheduler", CancellationToken.None)).Error);
    }

    [Fact]
    public void Imagine_NoNegative_SendsNone_AndCannotGoWithANegative()
    {
        var workflows = new[] { Parse("pony-txt2img", Txt2Img) };

        var (request, error) = ComfyImagine.Parse("pony-txt2img score_9, a cat --no-negative --seed 3", workflows);

        Assert.Null(error);
        Assert.Equal("", request!.Negative);   // none at all, not the workflow's
        Assert.Equal("score_9, a cat", request.Prompt);
        Assert.True(request.AnyWorkflow);
        Assert.Null(ComfyImagine.Parse("a cat", workflows).Request!.Negative);   // no switch: the workflow's own
        Assert.Equal(ComfyText.NegativeAndNoNegative, ComfyImagine.Parse("a cat -- blurry --no-negative", workflows).Error);
    }

    [Fact]
    public async Task Generate_WithAnEmptyNegative_SendsNone()
    {
        Workflow("pony-txt2img", Txt2Img);
        ServeOnePicture();

        await _studio.GenerateAsync(new ComfyRequest("a cat", Negative: "", Verbatim: true, Seed: 1), CancellationToken.None);

        Assert.Equal("", QueuedGraph().GetProperty("7").GetProperty("inputs").GetProperty("text").GetString());
    }

    /// <summary>The Illustrious family (later on 2026-09-24, the user's ask): its words, the name guess ahead of Pony and XL, its defaults, sampler and CLIP skip, and its prompt guide.</summary>
    [Fact]
    public void Illustrious_IsAFamilyOfItsOwn()
    {
        Assert.True(ComfyFamilies.TryParse("Illustrious", out var named));
        Assert.Equal(ComfyFamily.Illustrious, named);
        Assert.True(ComfyFamilies.TryParse("NoobAI", out var noob));
        Assert.Equal(ComfyFamily.Illustrious, noob);
        Assert.Equal("illustrious", ComfyFamilies.Name(ComfyFamily.Illustrious));
        Assert.Equal(ComfyFamily.Illustrious, ComfyFamilies.Guess("realPhotoMix_illustrious.safetensors"));
        Assert.Equal(ComfyFamily.Illustrious, ComfyFamilies.Guess("ponyIllustriousMix"));   // the merge is prompted the Illustrious way
        Assert.Equal(ComfyFamily.Illustrious, ComfyFamilies.Guess("noobaiXLVpred"));
        Assert.Equal(ComfyFamily.Juggernaut, ComfyFamilies.Guess("juggernautXL_ragnarok"));
        Assert.Equal(ComfyFamily.Pony, ComfyFamilies.Guess("ponyDiffusionV6XL"));
        Assert.Equal(new ComfyDefaults(1024, 1024, 28, 5.5, ComfyFamilies.IllustriousNegative), ComfyFamilies.Defaults(ComfyFamily.Illustrious));
        Assert.Equal("euler_ancestral", ComfyGraphs.DefaultSampler(ComfyFamily.Illustrious));
        Assert.Equal("normal", ComfyGraphs.DefaultScheduler(ComfyFamily.Illustrious));
        Assert.Equal(2, ComfyGraphs.DefaultClipSkip(ComfyFamily.Illustrious));
        Assert.Equal(ComfyFamilies.Names.Count, SettingsMenu.ComfyWizardFamilyRows.Count);   // a row per family, in the enum's order
        Assert.StartsWith("illustrious", SettingsMenu.ComfyWizardFamilyRows[(int)ComfyFamily.Illustrious]);

        var workflow = Parse("real-illustrious", Txt2Img, "---\nfamily: illustrious\n---\n");
        string text = ComfyText.Describe([workflow]);
        Assert.Contains("- real-illustrious · illustrious · text → image · 1024×1024", text);
        Assert.Contains("masterpiece, best quality, amazing quality, very aesthetic, absurdres", text);
        Assert.DoesNotContain("Pony Diffusion XL", text);   // only the families present get a guide
    }

    /// <summary>The Juggernaut family (later on 2026-09-24, the user's ask): its words, the name guess ahead of XL, its defaults, sampler and scheduler, and its photographic prompt guide.</summary>
    [Fact]
    public void Juggernaut_IsAFamilyOfItsOwn()
    {
        Assert.True(ComfyFamilies.TryParse("Juggernaut", out var named));
        Assert.Equal(ComfyFamily.Juggernaut, named);
        Assert.Equal("juggernaut", ComfyFamilies.Name(ComfyFamily.Juggernaut));
        Assert.Equal(ComfyFamily.Juggernaut, ComfyFamilies.Guess("juggernautXL_ragnarok.safetensors"));
        Assert.Equal(ComfyFamily.Sdxl, ComfyFamilies.Guess("famegridSDXL_photoRealV15"));
        Assert.Equal(ComfyFamily.Pony, ComfyFamilies.Guess("ponyDiffusionV6XL"));
        Assert.Equal(new ComfyDefaults(1024, 1024, 30, 4.5, ComfyFamilies.JuggernautNegative), ComfyFamilies.Defaults(ComfyFamily.Juggernaut));
        Assert.Equal(("dpmpp_2m_sde", "karras", 0), (ComfyGraphs.DefaultSampler(ComfyFamily.Juggernaut), ComfyGraphs.DefaultScheduler(ComfyFamily.Juggernaut), ComfyGraphs.DefaultClipSkip(ComfyFamily.Juggernaut)));
        Assert.Equal(ComfyFamilies.Names.Count, SettingsMenu.ComfyWizardFamilyRows.Count);
        Assert.StartsWith("juggernaut", SettingsMenu.ComfyWizardFamilyRows[(int)ComfyFamily.Juggernaut]);

        string text = ComfyText.Describe([Parse("juggernaut-xl", Txt2Img)]);
        Assert.Contains("- juggernaut-xl · juggernaut · text → image · 1024×1024", text);
        Assert.Contains("describe it like a photograph", text);
        Assert.DoesNotContain("Pony Diffusion XL", text);
        Assert.DoesNotContain("Danbooru tags —", text);
    }

    /// <summary>ComfyUI max pictures per call (later on 2026-09-24, the user's ask; a fixed 4 before): the tool, its schema and /imagine all read it; a hand-edited value clamps.</summary>
    [Fact]
    public async Task MaxPictures_IsASetting_TheToolTheSchemaAndImagineReadIt()
    {
        Workflow("pony-txt2img", Txt2Img);
        ServeOnePicture();
        var tool = new GenerateImageTool(_studio);
        var workflows = _studio.Catalog.Workflows;

        Assert.Equal(5, tool.MaxCount);   // the default (4 until later on 2026-09-24)
        Assert.Equal("Error: count must be 1 to 5, not '6'", (string?)await tool.InvokeAsync(Args(("prompt", "x"), ("count", 6))));
        Assert.Contains("How many pictures, 1 to 5,", tool.JsonSchema.GetRawText());
        Assert.Equal("Error: count must be 1 to 5, not '9'", ComfyImagine.Parse("a cat --count 9", workflows).Error);

        _settings.ComfyMaxPicturesPerCall = 6;
        Assert.Contains("How many pictures, 1 to 6,", tool.JsonSchema.GetRawText());   // rebuilt for the new cap
        var six = Assert.IsType<ToolImageResult>(await tool.InvokeAsync(Args(("prompt", "x"), ("count", 6), ("seed", 10))));
        Assert.Equal(6, six.Images.Count);
        Assert.Equal(6, _stub.Requests.Count(r => r.Uri.AbsolutePath == "/prompt"));   // six jobs, one after another
        Assert.Equal("Error: count must be 1 to 6, not '7'", (string?)await tool.InvokeAsync(Args(("prompt", "x"), ("count", 7))));
        Assert.Equal(9, ComfyImagine.Parse("a cat --count 9", workflows, maxCount: 9).Request!.Count);

        Assert.Equal(16, ComfyStudio.MaxCountOf(new AppSettingsData { ComfyMaxPicturesPerCall = 500 }));   // clamped
        Assert.Equal(1, ComfyStudio.MaxCountOf(new AppSettingsData { ComfyMaxPicturesPerCall = 0 }));
        Assert.Equal("1 picture", SettingsMenu.ComfyPictures(1));
        Assert.Equal("must be 1 to 16 pictures", SettingsMenu.ComfyMaxPicturesRangeError);
    }

    // ── The six newer families and their graphs (later still on 2026-09-24), built from the templates ComfyUI ships ─

    [Theory]
    [InlineData("flux2", "flux2_dev_fp8mixed.safetensors", ComfyFamily.Flux2)]
    [InlineData("flux.2", "flux-2-dev.safetensors", ComfyFamily.Flux2)]
    [InlineData("klein", "flux-2-klein-4b.safetensors", ComfyFamily.Flux2Klein)]
    [InlineData("flux-2-klein", "flux-2-klein-base-9b.safetensors", ComfyFamily.Flux2Klein)]
    [InlineData("krea2", "krea2_turbo_fp8_scaled.safetensors", ComfyFamily.Krea2)]
    [InlineData("z-image", "z_image_turbo_bf16.safetensors", ComfyFamily.ZImage)]
    [InlineData("qwen", "qwen_image_2512_fp8_e4m3fn.safetensors", ComfyFamily.QwenImage)]
    [InlineData("sd3.5", "sd3.5_large_fp8_scaled.safetensors", ComfyFamily.Sd35)]
    public void NewerFamilies_ParseTheirWords_AndGuessFromTheirModelFiles(string word, string file, ComfyFamily family)
    {
        Assert.True(ComfyFamilies.TryParse(word, out var parsed));
        Assert.Equal(family, parsed);
        Assert.Equal(family, ComfyFamilies.Guess(file));
        Assert.True(ComfyFamilies.TryParse(ComfyFamilies.Name(family), out var byName));
        Assert.Equal(family, byName);
        Assert.StartsWith(ComfyFamilies.Name(family) + " (", ComfyFamilies.StyleGuide(family));
    }

    [Fact]
    public void NewerFamilies_KeepTheOlderGuesses_AndHaveTheTemplatesDefaults()
    {
        Assert.Equal(ComfyFamily.Flux, ComfyFamilies.Guess("flux1_krea_dev"));   // Flux 1 Krea is no Krea 2
        Assert.Equal(ComfyFamily.Flux, ComfyFamilies.Guess("flux1-dev-fp8"));
        Assert.Equal(ComfyFamily.Sd15, ComfyFamilies.Guess("v1-5-pruned-emaonly-fp16"));
        Assert.Equal(ComfyFamily.Sdxl, ComfyFamilies.Guess("famegridSDXL_photoRealV15"));
        Assert.Equal(new ComfyDefaults(1024, 1024, 20, 4.0, ""), ComfyFamilies.Defaults(ComfyFamily.Flux2));
        Assert.Equal(new ComfyDefaults(1024, 1024, 4, 1.0, ""), ComfyFamilies.Defaults(ComfyFamily.Flux2Klein));
        Assert.Equal(new ComfyDefaults(1024, 1024, 8, 1.0, ""), ComfyFamilies.Defaults(ComfyFamily.Krea2));
        Assert.Equal(new ComfyDefaults(1024, 1024, 8, 1.0, ""), ComfyFamilies.Defaults(ComfyFamily.ZImage));
        Assert.Equal(new ComfyDefaults(1328, 1328, 50, 4.0, ComfyFamilies.QwenImageNegative), ComfyFamilies.Defaults(ComfyFamily.QwenImage));
        Assert.Equal(new ComfyDefaults(1024, 1024, 20, 4.0, ComfyFamilies.GenericNegative), ComfyFamilies.Defaults(ComfyFamily.Sd35));
        Assert.Equal(("res_multistep", "simple"), (ComfyGraphs.DefaultSampler(ComfyFamily.ZImage), ComfyGraphs.DefaultScheduler(ComfyFamily.ZImage)));
        Assert.Equal("sgm_uniform", ComfyGraphs.DefaultScheduler(ComfyFamily.Sd35));
        Assert.True(ComfyGraphs.UsuallySplit(ComfyFamily.Flux2Klein));
        Assert.False(ComfyGraphs.UsuallySplit(ComfyFamily.Sd35));
        Assert.Equal(ComfyFamilies.Names.Count, SettingsMenu.ComfyWizardFamilyRows.Count);
        for (int i = 0; i < ComfyFamilies.Names.Count; i++)
        {
            Assert.StartsWith(ComfyFamilies.Names[i] + " ", SettingsMenu.ComfyWizardFamilyRows[i]);   // the rows in the enum's order
        }
    }

    /// <summary>FLUX.2 dev as its template builds it: SamplerCustomAdvanced over RandomNoise, BasicGuider ← FluxGuidance, Flux2Scheduler, EmptyFlux2LatentImage.</summary>
    internal const string Flux2DevExport = """
        {
          "1": { "class_type": "UNETLoader", "inputs": { "unet_name": "flux2_dev_fp8mixed.safetensors", "weight_dtype": "default" } },
          "2": { "class_type": "CLIPLoader", "inputs": { "clip_name": "mistral_3_small_flux2_bf16.safetensors", "type": "flux2", "device": "default" } },
          "3": { "class_type": "VAELoader", "inputs": { "vae_name": "flux2-vae.safetensors" } },
          "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "a cat on a windowsill", "clip": ["2", 0] } },
          "26": { "class_type": "FluxGuidance", "inputs": { "guidance": 4, "conditioning": ["6", 0] } },
          "22": { "class_type": "BasicGuider", "inputs": { "model": ["1", 0], "conditioning": ["26", 0] } },
          "25": { "class_type": "RandomNoise", "inputs": { "noise_seed": 123 } },
          "16": { "class_type": "KSamplerSelect", "inputs": { "sampler_name": "euler" } },
          "48": { "class_type": "Flux2Scheduler", "inputs": { "steps": 20, "width": 1024, "height": 1024 } },
          "47": { "class_type": "EmptyFlux2LatentImage", "inputs": { "width": 1024, "height": 1024, "batch_size": 1 } },
          "13": { "class_type": "SamplerCustomAdvanced", "inputs": { "noise": ["25", 0], "guider": ["22", 0], "sampler": ["16", 0], "sigmas": ["48", 0], "latent_image": ["47", 0] } },
          "8": { "class_type": "VAEDecode", "inputs": { "samples": ["13", 0], "vae": ["3", 0] } },
          "9": { "class_type": "SaveImage", "inputs": { "filename_prefix": "Flux2_dev", "images": ["8", 0] } }
        }
        """;

    [Fact]
    public void Import_Flux2Dev_ReadsTheCustomSampler_TheGuidanceIsTheCfg()
    {
        var (result, problem) = ComfyImport.Placehold(Flux2DevExport);

        Assert.Null(problem);
        var workflow = Parse("flux2-dev", result!.GraphJson);
        Assert.Equal(["cfg", "height", "prompt", "seed", "steps", "width"], workflow.Placeholders.Order(StringComparer.Ordinal));
        Assert.Equal((20, 4.0, 1024, 1024), (result.Steps!.Value, result.Cfg!.Value, result.Width!.Value, result.Height!.Value));
        Assert.Contains("{{seed}} → node 25 RandomNoise.noise_seed", result.Found);
        Assert.Contains("{{steps}} → node 48 Flux2Scheduler.steps", result.Found);
        Assert.Contains("{{cfg}} → node 26 FluxGuidance.guidance", result.Found);
        Assert.Contains("{{width}} → node 48 Flux2Scheduler.width", result.Found);   // the scheduler's size follows the latent's
        Assert.Equal(ComfyFamily.Flux2, ComfyFamilies.Guess(result.Checkpoint));
        var filled = workflow.Fill(new ComfyValues("a dog", "", 7, 832, 1216, 30, 3.5));
        Assert.Equal(3.5, filled["26"]!["inputs"]!["guidance"]!.GetValue<double>());
        Assert.Equal(1216, filled["48"]!["inputs"]!["height"]!.GetValue<int>());
    }

    [Fact]
    public void Import_Flux2Klein_PutsThePlaceholdersOnThePrimitives_AndSkipsTheZeroedNegative()
    {
        const string klein = """
            {
              "1": { "class_type": "UNETLoader", "inputs": { "unet_name": "flux-2-klein-4b.safetensors", "weight_dtype": "default" } },
              "2": { "class_type": "CLIPLoader", "inputs": { "clip_name": "qwen_3_4b.safetensors", "type": "flux2", "device": "default" } },
              "3": { "class_type": "VAELoader", "inputs": { "vae_name": "flux2-vae.safetensors" } },
              "30": { "class_type": "PrimitiveStringMultiline", "inputs": { "value": "A hedgehog wearing a tiny party hat" } },
              "31": { "class_type": "PrimitiveInt", "inputs": { "value": 1024 } },
              "32": { "class_type": "PrimitiveInt", "inputs": { "value": 768 } },
              "6": { "class_type": "CLIPTextEncode", "inputs": { "text": ["30", 0], "clip": ["2", 0] } },
              "7": { "class_type": "ConditioningZeroOut", "inputs": { "conditioning": ["6", 0] } },
              "22": { "class_type": "CFGGuider", "inputs": { "cfg": 1, "model": ["1", 0], "positive": ["6", 0], "negative": ["7", 0] } },
              "25": { "class_type": "RandomNoise", "inputs": { "noise_seed": 5 } },
              "16": { "class_type": "KSamplerSelect", "inputs": { "sampler_name": "euler" } },
              "48": { "class_type": "Flux2Scheduler", "inputs": { "steps": 4, "width": ["31", 0], "height": ["32", 0] } },
              "47": { "class_type": "EmptyFlux2LatentImage", "inputs": { "width": ["31", 0], "height": ["32", 0], "batch_size": 1 } },
              "13": { "class_type": "SamplerCustomAdvanced", "inputs": { "noise": ["25", 0], "guider": ["22", 0], "sampler": ["16", 0], "sigmas": ["48", 0], "latent_image": ["47", 0] } },
              "8": { "class_type": "VAEDecode", "inputs": { "samples": ["13", 0], "vae": ["3", 0] } },
              "9": { "class_type": "SaveImage", "inputs": { "filename_prefix": "Flux2-Klein", "images": ["8", 0] } }
            }
            """;

        var (result, problem) = ComfyImport.Placehold(klein);

        Assert.Null(problem);
        var workflow = Parse("klein", result!.GraphJson);
        Assert.Equal(["cfg", "height", "prompt", "seed", "steps", "width"], workflow.Placeholders.Order(StringComparer.Ordinal));   // no negative: it is the prompt zeroed out
        Assert.Equal((4, 1.0, 1024, 768), (result.Steps!.Value, result.Cfg!.Value, result.Width!.Value, result.Height!.Value));
        Assert.Contains("{{prompt}} → node 30 PrimitiveStringMultiline.value", result.Found);
        Assert.Contains("{{width}} → node 31 PrimitiveInt.value", result.Found);
        Assert.Contains("{{cfg}} → node 22 CFGGuider.cfg", result.Found);
        Assert.DoesNotContain(result.Found, f => f.Contains("left as exported", StringComparison.Ordinal));   // one primitive, two inputs: placeholdered once
        Assert.Equal(ComfyFamily.Flux2Klein, ComfyFamilies.Guess(result.Checkpoint));
        var filled = workflow.Fill(new ComfyValues("a fox", "", 9, 640, 480));
        Assert.Equal("a fox", filled["30"]!["inputs"]!["value"]!.GetValue<string>());
        Assert.Equal(640, filled["31"]!["inputs"]!["value"]!.GetValue<int>());
    }

    [Fact]
    public void Import_QwenAndKrea_LeaveWhatOtherNodesSet_AsExported_AndSaySo()
    {
        const string qwen = """
            {
              "37": { "class_type": "UNETLoader", "inputs": { "unet_name": "qwen_image_2512_fp8_e4m3fn.safetensors", "weight_dtype": "default" } },
              "38": { "class_type": "CLIPLoader", "inputs": { "clip_name": "qwen_2.5_vl_7b_fp8_scaled.safetensors", "type": "qwen_image", "device": "default" } },
              "66": { "class_type": "ModelSamplingAuraFlow", "inputs": { "shift": 3.1, "model": ["37", 0] } },
              "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "Urban alleyway at dusk", "clip": ["38", 0] } },
              "7": { "class_type": "CLIPTextEncode", "inputs": { "text": "low resolution, low quality", "clip": ["38", 0] } },
              "58": { "class_type": "EmptySD3LatentImage", "inputs": { "width": 1328, "height": 1328, "batch_size": 1 } },
              "40": { "class_type": "ComfySwitchNode", "inputs": { "switch": false, "on_false": 50, "on_true": 4 } },
              "41": { "class_type": "ComfySwitchNode", "inputs": { "switch": false, "on_false": 4, "on_true": 1 } },
              "3": { "class_type": "KSampler", "inputs": { "seed": 1, "steps": ["40", 0], "cfg": ["41", 0], "sampler_name": "euler", "scheduler": "simple", "denoise": 1, "model": ["66", 0], "positive": ["6", 0], "negative": ["7", 0], "latent_image": ["58", 0] } },
              "9": { "class_type": "SaveImage", "inputs": { "filename_prefix": "Qwen-Image", "images": ["3", 0] } }
            }
            """;
        var (q, qProblem) = ComfyImport.Placehold(qwen);
        Assert.Null(qProblem);
        Assert.Equal(["height", "negative", "prompt", "seed", "width"], Parse("qwen", q!.GraphJson).Placeholders.Order(StringComparer.Ordinal));
        Assert.Contains("{{steps}}: set by node 40 ComfySwitchNode, left as exported", q.Found);
        Assert.Contains("{{cfg}}: set by node 41 ComfySwitchNode, left as exported", q.Found);
        Assert.Equal("low resolution, low quality", q.Negative);
        Assert.Equal((null, null), (q.Steps, q.Cfg));
        Assert.Equal(ComfyFamily.QwenImage, ComfyFamilies.Guess(q.Checkpoint));

        const string krea = """
            {
              "1": { "class_type": "UNETLoader", "inputs": { "unet_name": "krea2_turbo_fp8_scaled.safetensors", "weight_dtype": "default" } },
              "2": { "class_type": "CLIPLoader", "inputs": { "clip_name": "qwen3vl_4b_fp8_scaled.safetensors", "type": "krea2", "device": "default" } },
              "50": { "class_type": "StringConcatenate", "inputs": { "string_a": "a martini glass", "string_b": ", sketch style", "delimiter": "" } },
              "60": { "class_type": "ResolutionSelector", "inputs": { "aspect_ratio": "1:1 (Square)", "megapixels": 1, "multiple": 8 } },
              "6": { "class_type": "CLIPTextEncode", "inputs": { "text": ["50", 0], "clip": ["2", 0] } },
              "7": { "class_type": "ConditioningZeroOut", "inputs": { "conditioning": ["6", 0] } },
              "5": { "class_type": "EmptyLatentImage", "inputs": { "width": ["60", 0], "height": ["60", 1], "batch_size": 1 } },
              "3": { "class_type": "KSampler", "inputs": { "seed": 1, "steps": 8, "cfg": 1, "sampler_name": "euler", "scheduler": "simple", "denoise": 1, "model": ["1", 0], "positive": ["6", 0], "negative": ["7", 0], "latent_image": ["5", 0] } },
              "9": { "class_type": "SaveImage", "inputs": { "filename_prefix": "Krea2", "images": ["3", 0] } }
            }
            """;
        var (k, kProblem) = ComfyImport.Placehold(krea);
        Assert.Null(kProblem);
        Assert.Equal(["cfg", "prompt", "seed", "steps"], Parse("krea", k!.GraphJson).Placeholders.Order(StringComparer.Ordinal));
        Assert.Contains("{{prompt}} → node 6 CLIPTextEncode.text (replacing the link from node 50 StringConcatenate)", k.Found);
        Assert.Contains("{{width}}: set by node 60 ResolutionSelector, left as exported", k.Found);
        Assert.Equal(ComfyFamily.Krea2, ComfyFamilies.Guess(k.Checkpoint));
    }

    // ── Ernie, Boogu, LongCat, HiDream, Ideogram 4; a workflow's own {{…}} texts (later still on 2026-09-24) ─

    [Theory]
    [InlineData("ernie-image", "ernie-image-turbo.safetensors", ComfyFamily.Ernie, 8, 1.0)]
    [InlineData("boogu_image", "boogu_image_turbo_fp8_scaled.safetensors", ComfyFamily.Boogu, 4, 1.0)]
    [InlineData("longcat-image", "longcat_image_bf16.safetensors", ComfyFamily.LongCat, 20, 4.0)]
    [InlineData("hidream-i1", "hidream_i1_full_fp8.safetensors", ComfyFamily.HiDream, 50, 5.0)]
    [InlineData("ideogram v4", "ideogram4_fp8_scaled.safetensors", ComfyFamily.Ideogram4, 20, 7.0)]
    public void FiveMoreFamilies_ParseGuessAndHaveTheTemplatesDefaults(string word, string file, ComfyFamily family, int steps, double cfg)
    {
        Assert.True(ComfyFamilies.TryParse(word, out var parsed));
        Assert.Equal(family, parsed);
        Assert.Equal(family, ComfyFamilies.Guess(file));
        var d = ComfyFamilies.Defaults(family);
        Assert.Equal((1024, 1024, steps, cfg), (d.Width, d.Height, d.Steps, d.Cfg));
        Assert.StartsWith(ComfyFamilies.Name(family) + " (", ComfyFamilies.StyleGuide(family));
        Assert.True(ComfyGraphs.UsuallySplit(family));
        Assert.StartsWith(ComfyFamilies.Name(family) + " ", SettingsMenu.ComfyWizardFamilyRows[(int)family]);
    }

    [Fact]
    public void FiveMoreFamilies_TheirNegatives_AndPickerDefaults()
    {
        Assert.Equal(ComfyFamilies.HiDreamNegative, ComfyFamilies.Defaults(ComfyFamily.HiDream).Negative);
        Assert.Equal(ComfyFamilies.LongCatNegative, ComfyFamilies.Defaults(ComfyFamily.LongCat).Negative);
        Assert.DoesNotContain("text", ComfyFamilies.LongCatNegative.Split(", "));   // LongCat renders text: the template's "text" is not a family default
        Assert.Equal("", ComfyFamilies.Defaults(ComfyFamily.Ideogram4).Negative);
        Assert.Equal(("lcm", "sgm_uniform"), (ComfyGraphs.DefaultSampler(ComfyFamily.Boogu), ComfyGraphs.DefaultScheduler(ComfyFamily.Boogu)));
        Assert.Equal(("uni_pc", "simple"), (ComfyGraphs.DefaultSampler(ComfyFamily.HiDream), ComfyGraphs.DefaultScheduler(ComfyFamily.HiDream)));
        Assert.Contains("\"compositional_deconstruction\"", ComfyFamilies.StyleGuide(ComfyFamily.Ideogram4));
        Assert.Equal(ComfyFamilies.Names.Count, SettingsMenu.ComfyWizardFamilyRows.Count);
    }

    /// <summary>
    /// Ideogram 4 as its template builds it: SamplerCustomAdvanced over a DualModelGuider and Ideogram4Scheduler, its steps from a
    /// preset through ComfyNumberConvert, and StringReplace nodes that search for a literal {{width}} and {{original_prompt}} — the
    /// workflow's own text, kept as it was through import and Fill.
    /// </summary>
    [Fact]
    public void Import_Ideogram4_ReadsTheDualGuider_AndKeepsItsOwnBraces()
    {
        const string ideogram = """
            {
              "1": { "class_type": "UNETLoader", "inputs": { "unet_name": "ideogram4_fp8_scaled.safetensors", "weight_dtype": "default" } },
              "2": { "class_type": "UNETLoader", "inputs": { "unet_name": "ideogram4_unconditional_fp8_scaled.safetensors", "weight_dtype": "default" } },
              "3": { "class_type": "CLIPLoader", "inputs": { "clip_name": "qwen3vl_8b_fp8_scaled.safetensors", "type": "ideogram4", "device": "default" } },
              "20": { "class_type": "StringReplace", "inputs": { "string": "Write a prompt for a {{width}} x {{height}} image of {{original_prompt}}", "find": "{{width}}", "replace": "1024" } },
              "21": { "class_type": "JsonExtractString", "inputs": { "json_string": "{}", "key": "num_steps" } },
              "22": { "class_type": "ComfyNumberConvert", "inputs": { "value": ["21", 0] } },
              "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "{ \"high_level_description\": \"a poster\" }", "clip": ["3", 0] } },
              "7": { "class_type": "ConditioningZeroOut", "inputs": { "conditioning": ["6", 0] } },
              "30": { "class_type": "DualModelGuider", "inputs": { "cfg": 7, "model": ["1", 0], "model_negative": ["2", 0], "positive": ["6", 0], "negative": ["7", 0] } },
              "31": { "class_type": "RandomNoise", "inputs": { "noise_seed": 8 } },
              "32": { "class_type": "KSamplerSelect", "inputs": { "sampler_name": "euler" } },
              "33": { "class_type": "Ideogram4Scheduler", "inputs": { "steps": ["22", 0], "width": 1024, "height": 1536, "mu": 0.0, "std": 1.75 } },
              "34": { "class_type": "EmptyFlux2LatentImage", "inputs": { "width": 1024, "height": 1536, "batch_size": 1 } },
              "35": { "class_type": "SamplerCustomAdvanced", "inputs": { "noise": ["31", 0], "guider": ["30", 0], "sampler": ["32", 0], "sigmas": ["33", 0], "latent_image": ["34", 0] } },
              "9": { "class_type": "SaveImage", "inputs": { "filename_prefix": "Ideogram4", "images": ["35", 0] } }
            }
            """;

        var (result, problem) = ComfyImport.Placehold(ideogram);

        Assert.Null(problem);
        var workflow = Parse("ideogram", result!.GraphJson);
        Assert.Equal(["cfg", "height", "prompt", "seed", "width"], workflow.Placeholders.Order(StringComparer.Ordinal));   // the StringReplace's braces are not ours
        Assert.Contains("{{cfg}} → node 30 DualModelGuider.cfg", result.Found);
        Assert.Contains("{{steps}}: set by node 22 ComfyNumberConvert, left as exported", result.Found);
        Assert.Contains("{{height}} → node 33 Ideogram4Scheduler.height", result.Found);
        Assert.Contains(ComfyImport.KeptOwnTexts(4), result.Found);
        Assert.Equal(ComfyFamily.Ideogram4, ComfyFamilies.Guess(result.Checkpoint));

        var filled = workflow.Fill(new ComfyValues("{ \"high_level_description\": \"a cat\" }", "", 3, 832, 1216, Cfg: 6));
        Assert.Equal("Write a prompt for a {{width}} x {{height}} image of {{original_prompt}}", filled["20"]!["inputs"]!["string"]!.GetValue<string>());
        Assert.Equal("{{width}}", filled["20"]!["inputs"]!["find"]!.GetValue<string>());
        Assert.Equal(832, filled["33"]!["inputs"]!["width"]!.GetValue<int>());
        Assert.Equal(6.0, filled["30"]!["inputs"]!["cfg"]!.GetValue<double>());
    }

    [Fact]
    public void Import_HiDream_QuadrupleClip_KSampler_WithItsNegative()
    {
        const string hidream = """
            {
              "54": { "class_type": "QuadrupleCLIPLoader", "inputs": { "clip_name1": "clip_l_hidream.safetensors", "clip_name2": "clip_g_hidream.safetensors", "clip_name3": "t5xxl_fp8_e4m3fn_scaled.safetensors", "clip_name4": "llama_3.1_8b_instruct_fp8_scaled.safetensors" } },
              "69": { "class_type": "UNETLoader", "inputs": { "unet_name": "hidream_i1_dev_fp8.safetensors", "weight_dtype": "default" } },
              "70": { "class_type": "ModelSamplingSD3", "inputs": { "shift": 6, "model": ["69", 0] } },
              "16": { "class_type": "CLIPTextEncode", "inputs": { "text": "an albino woman in baroque oil", "clip": ["54", 0] } },
              "40": { "class_type": "CLIPTextEncode", "inputs": { "text": "bad ugly jpeg artifacts", "clip": ["54", 0] } },
              "53": { "class_type": "EmptySD3LatentImage", "inputs": { "width": 1024, "height": 1024, "batch_size": 1 } },
              "3": { "class_type": "KSampler", "inputs": { "seed": 1, "steps": 28, "cfg": 1, "sampler_name": "lcm", "scheduler": "normal", "denoise": 1, "model": ["70", 0], "positive": ["16", 0], "negative": ["40", 0], "latent_image": ["53", 0] } },
              "9": { "class_type": "SaveImage", "inputs": { "filename_prefix": "HiDream", "images": ["3", 0] } }
            }
            """;

        var (result, problem) = ComfyImport.Placehold(hidream);

        Assert.Null(problem);
        Assert.Equal(["cfg", "height", "negative", "prompt", "seed", "steps", "width"], Parse("hidream", result!.GraphJson).Placeholders.Order(StringComparer.Ordinal));
        Assert.Equal((28, 1.0, "bad ugly jpeg artifacts"), (result.Steps!.Value, result.Cfg!.Value, result.Negative));   // Dev's numbers, from the graph
        Assert.Equal(ComfyFamily.HiDream, ComfyFamilies.Guess(result.Checkpoint));
        Assert.DoesNotContain(result.Found, f => f.StartsWith("kept ", StringComparison.Ordinal));   // nothing to escape
    }

    [Fact]
    public void Fill_WritesAnEscape_BackAsTheLiteral()
    {
        var workflow = Parse("escape", """{ "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "{{prompt}} at {{!width}}px, {{!Mixed_Case}}" } }, "7": { "class_type": "StringReplace", "inputs": { "find": "{{!width}}" } } }""");

        Assert.Equal(["prompt"], workflow.Placeholders);   // an escape is no placeholder
        var filled = workflow.Fill(new ComfyValues("a cat", "", 1, Width: 640));
        Assert.Equal("a cat at {{width}}px, {{Mixed_Case}}", filled["6"]!["inputs"]!["text"]!.GetValue<string>());
        Assert.Equal("{{width}}", filled["7"]!["inputs"]!["find"]!.GetValue<string>());
    }

    /// <summary>The Pony guide (later still on 2026-09-24, the user's ask): no rating_ tag unless asked, Danbooru tags weighted where it helps, a phrase only where no tag is specific enough.</summary>
    [Fact]
    public void PonyGuide_LeavesRatingsToTheUser_AndPrefersWeightedDanbooruTags()
    {
        string guide = ComfyFamilies.StyleGuide(ComfyFamily.Pony);

        Assert.StartsWith("pony (Pony Diffusion XL): start with \"score_9, score_8_up, score_7_up\", then one source tag", guide);
        Assert.Contains("no rating_ tag unless the user asks for one", guide);
        Assert.DoesNotContain("rating_safe", guide);
        Assert.Contains("well-known Danbooru tags", guide);
        Assert.Contains("(tag:1.2) to (tag:1.4)", guide);
        Assert.Contains("(tag:0.8)", guide);
        Assert.Contains("only where no Danbooru tag is specific enough", guide);
        Assert.Contains(guide, ComfyText.Describe([Parse("pony-txt2img", Txt2Img)]));   // what generate_image's description carries
    }

    /// <summary>
    /// Negative reinforcement (later still on 2026-09-24, the user's ask): the model's negative_extra is appended to the workflow's
    /// negative — not for a verbatim prompt, a negative set for the call, the setting off, reinforce: false, or a family that runs without one.
    /// </summary>
    [Fact]
    public async Task NegativeExtra_IsAppendedToTheWorkflowsNegative_OnlyWhereItHelps()
    {
        Workflow("pony-txt2img", Txt2Img, "---\nfamily: pony\nnegative: score_4, blurry,\n---\n");
        ServeOnePicture();
        var tool = new GenerateImageTool(_studio);
        string SentNegative(int index) => QueuedGraph(index).GetProperty("7").GetProperty("inputs").GetProperty("text").GetString()!;

        await tool.InvokeAsync(Args(("prompt", "score_9, 1girl, solo, night"), ("negative_extra", "multiple girls, daylight"), ("seed", 1)));
        Assert.Equal("score_4, blurry, multiple girls, daylight", SentNegative(0));   // the workflow's own first, one comma between

        await tool.InvokeAsync(Args(("prompt", "score_9, 1girl"), ("negative_extra", "daylight"), ("verbatim", true), ("seed", 2)));
        Assert.Equal("score_4, blurry,", SentNegative(1));   // the user's own prompt: nothing added
        await tool.InvokeAsync(Args(("prompt", "x"), ("negative", "ugly"), ("negative_extra", "daylight"), ("seed", 3)));
        Assert.Equal("ugly", SentNegative(2));   // a negative set for the call is the whole negative
        _settings.ComfyReinforceNegatives = false;
        await tool.InvokeAsync(Args(("prompt", "x"), ("negative_extra", "daylight"), ("seed", 4)));
        Assert.Equal("score_4, blurry,", SentNegative(3));
        Assert.DoesNotContain(ComfyText.ReinforceGuide, tool.Description);

        _settings.ComfyReinforceNegatives = true;
        Assert.Contains(ComfyText.ReinforceGuide, tool.Description);
        Assert.Contains("pony-txt2img · pony · text → image · 1024×1024 · negative reinforced", tool.Description);
        Assert.False(Parse("opt-out", Txt2Img, "---\nfamily: pony\nreinforce: false\n---\n").TakesReinforcement);
        Assert.False(Parse("flux-dev", Txt2Img).TakesReinforcement);   // Flux runs without a negative
        Assert.False(ComfyFamilies.UsesNegative(ComfyFamily.Flux2Klein));
        Assert.True(ComfyFamilies.UsesNegative(ComfyFamily.Juggernaut));
        Assert.False(Parse("no-slot", Img2Img, "---\nfamily: pony\n---\n").TakesReinforcement);   // no {{negative}} to put it in
        Assert.Equal("a, b", ComfyStudio.JoinNegative(" a , ", ", b,"));
        Assert.Equal("b", ComfyStudio.JoinNegative("", "b"));
        Assert.DoesNotContain(ComfyText.ReinforceGuide, ComfyText.Describe([Parse("flux-dev", Txt2Img)], reinforce: true));   // no workflow takes it: not said
    }
}
