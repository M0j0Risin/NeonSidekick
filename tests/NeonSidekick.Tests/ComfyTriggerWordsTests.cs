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

/// <summary>
/// A LoRA's trigger words (2026-11-26, the user's ask): kept per LoRA in the sidecar (<c>lora.&lt;name&gt;</c>), the ones in play put in front
/// of every prompt, <c>/imagine</c> and verbatim ones too, never twice. Made-up names only.
/// </summary>
public sealed class ComfyTriggerWordsTests : IDisposable
{
    private const string Server = "http://comfy.lan:8188";

    /// <summary>A checkpoint, a LoRA on the model and the CLIP, a model-only LoRA after it, the prompts and a sampler.</summary>
    private const string Graph = """
        {
          "4": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "base.safetensors" } },
          "10": { "class_type": "LoraLoader", "inputs": { "lora_name": "style.safetensors", "strength_model": 0.8, "strength_clip": 0.8, "model": ["4", 0], "clip": ["4", 1] } },
          "11": { "class_type": "LoraLoaderModelOnly", "inputs": { "lora_name": "detail.safetensors", "strength_model": 1, "model": ["10", 0] } },
          "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "{{prompt}}", "clip": ["10", 1] } },
          "7": { "class_type": "CLIPTextEncode", "inputs": { "text": "{{negative}}", "clip": ["10", 1] } },
          "5": { "class_type": "EmptyLatentImage", "inputs": { "width": "{{width}}", "height": "{{height}}", "batch_size": 1 } },
          "3": { "class_type": "KSampler", "inputs": { "seed": "{{seed}}", "steps": 20, "cfg": 7, "model": ["11", 0], "positive": ["6", 0], "negative": ["7", 0], "latent_image": ["5", 0] } },
          "9": { "class_type": "SaveImage", "inputs": { "filename_prefix": "neon", "images": ["8", 0] } }
        }
        """;

    private const string Sidecar = "---\nfamily: sdxl\nlora.style.safetensors: xyz_style, glowing eyes\nlora.detail.safetensors: fine detail\n---\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _comfy;
    private readonly AppSettingsData _settings = new() { ComfyUrl = Server, ComfyTools = true };
    private readonly StubHttpMessageHandler _stub = new();
    private readonly ComfyStudio _studio;

    public ComfyTriggerWordsTests()
    {
        _comfy = Path.Combine(_dir, "profile", "comfy");
        string root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_comfy);
        Directory.CreateDirectory(root);
        var files = new WorkingDirectory(() => root, new ManualTimeProvider());
        _studio = new ComfyStudio(new ComfyWorkflowCatalog(() => [_comfy]), files, () => _settings,
            url => new ComfyClient(url, new HttpClient(_stub), TimeSpan.FromMilliseconds(1)), new Random(7));
    }

    public void Dispose()
    {
        _studio.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ComfyWorkflow Parse(string json, string? sidecar = Sidecar, string name = "style-txt2img")
    {
        Assert.True(ComfyWorkflow.TryParse(name, name + ".json", json, sidecar, out var workflow, out string? problem), problem);
        return workflow!;
    }

    private void Install(string json, string sidecar = Sidecar, string name = "style-txt2img")
    {
        _settings.ComfyWorkflowsOffered = [.. _settings.ComfyWorkflowsOffered ?? [], name];
        File.WriteAllText(Path.Combine(_comfy, name + ".json"), json);
        File.WriteAllText(Path.Combine(_comfy, name + ".md"), sidecar);
    }

    private void ServeOnePicture()
    {
        int reads = 0;
        _stub.Map(Server + "/prompt", HttpStatusCode.OK, "{\"prompt_id\":\"p-1\",\"number\":1,\"node_errors\":{}}");
        _stub.Map(Server + "/history/", (_, _) => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK,
            ++reads < 2 ? "{}" : "{\"p-1\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"neon_00001_.png\",\"subfolder\":\"\",\"type\":\"output\"}]}},\"status\":{\"status_str\":\"success\",\"completed\":true}}}")));
        _stub.Map(Server + "/view", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, SmokeChecks.SolidBmp(8, 8), "image/png")));
    }

    private string SentPrompt()
    {
        var body = _stub.Requests.Single(r => r.Uri.AbsolutePath == "/prompt").Body!;
        return JsonDocument.Parse(body).RootElement.GetProperty("prompt").GetProperty("6").GetProperty("inputs").GetProperty("text").GetString()!;
    }

    private static AIFunctionArguments Args(params (string Name, object? Value)[] values) => new(values.ToDictionary(v => v.Name, v => v.Value));

    // ── Which words are in play ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheWords_AreTheLorasInPlay_InTheGraphsOrder_EachPhraseOnce_AnyCaseOfTheKey()
    {
        string md = "---\nLORA.Style.SAFETENSORS: xyz_style, glowing eyes\nlora.detail.safetensors: Glowing Eyes, , fine detail,\nlora.gone.safetensors: never\nlora.empty.safetensors:\n---\n";

        Assert.Equal(["xyz_style", "glowing eyes", "fine detail"], Parse(Graph, md).TriggerWords);
        Assert.Null(Parse(Graph, "---\nfamily: sdxl\n---\n").TriggerWords);
        Assert.Null(Parse(Graph, null).TriggerWords);
    }

    [Fact]
    public void AStrength_OfZero_TakesALorasWordsOut_ButOnlyBothOfALoraLoaders()
    {
        // A CLIP-only LoRA still takes its words: they act through the text encoder.
        string clipOnly = Graph.Replace("\"strength_model\": 0.8, \"strength_clip\": 0.8", "\"strength_model\": 0, \"strength_clip\": 0.8", StringComparison.Ordinal);
        Assert.Equal(["xyz_style", "glowing eyes", "fine detail"], Parse(clipOnly).TriggerWords);

        string bothOff = Graph.Replace("\"strength_model\": 0.8, \"strength_clip\": 0.8", "\"strength_model\": 0, \"strength_clip\": 0", StringComparison.Ordinal);
        Assert.Equal(["fine detail"], Parse(bothOff).TriggerWords);

        // A model-only LoRA has no CLIP: its model strength alone decides.
        string modelOnlyOff = Graph.Replace("\"strength_model\": 1,", "\"strength_model\": 0,", StringComparison.Ordinal);
        Assert.Equal(["xyz_style", "glowing eyes"], Parse(modelOnlyOff).TriggerWords);

        // A strength another node sets counts as on.
        string linked = Graph.Replace("\"strength_model\": 1,", "\"strength_model\": [\"20\", 0],", StringComparison.Ordinal)
            .Replace("\"9\":", "\"20\": { \"class_type\": \"PrimitiveFloat\", \"inputs\": { \"value\": 0 } },\n  \"9\":", StringComparison.Ordinal);
        Assert.Equal(["xyz_style", "glowing eyes", "fine detail"], Parse(linked).TriggerWords);
    }

    [Fact]
    public void LoraInPlay_FollowsTheClass()
    {
        Assert.False(ComfyWorkflow.LoraInPlay(new ComfyLora("1", "LoraLoader", "a", 0, 0, false, null)));
        Assert.True(ComfyWorkflow.LoraInPlay(new ComfyLora("1", "LoraLoader", "a", 0, null, false, null)));
        Assert.False(ComfyWorkflow.LoraInPlay(new ComfyLora("1", "LoraLoaderModelOnly", "a", 0, null, false, null)));
        Assert.True(ComfyWorkflow.LoraInPlay(new ComfyLora("1", "LoraLoaderModelOnly", "a", null, null, false, null)));
        Assert.True(ComfyWorkflow.LoraInPlay(new ComfyLora("1", "LoraLoaderBypass", "a", null, null, false, "custom")));   // a custom loader: missing strengths count as on
        Assert.False(ComfyWorkflow.LoraInPlay(new ComfyLora("1", "LoraLoader", "a", 1, 1, true, null)));   // switched off
        Assert.False(ComfyWorkflow.LoraInPlay(new ComfyLora("1", "LoraLoader", "", 1, 1, false, "set by node 9")));   // no literal name
    }

    [Fact]
    public void AWordTheTemplateAlreadyHas_IsLeftOut_OnlyWhenEveryPromptStringHasIt()
    {
        string own = Graph.Replace("\"text\": \"{{prompt}}\"", "\"text\": \"{{prompt}}, XYZ_STYLE\"", StringComparison.Ordinal);
        Assert.Equal(["glowing eyes", "fine detail"], Parse(own).TriggerWords);

        // Two encoders each get the prompt; the word in one of them still goes in, for the other.
        string two = own.Replace("\"9\":", "\"12\": { \"class_type\": \"CLIPTextEncode\", \"inputs\": { \"text\": \"{{prompt}}\", \"clip\": [\"10\", 1] } },\n  \"9\":", StringComparison.Ordinal);
        Assert.Equal(["xyz_style", "glowing eyes", "fine detail"], Parse(two).TriggerWords);
    }

    [Fact]
    public void AWorkflowWithNoPrompt_GetsNoWords()
    {
        string swap = Graph.Replace("\"text\": \"{{prompt}}\"", "\"text\": \"a face\"", StringComparison.Ordinal)
            .Replace("\"9\":", "\"13\": { \"class_type\": \"LoadImage\", \"inputs\": { \"image\": \"{{image}}\" } },\n  \"9\":", StringComparison.Ordinal);
        var workflow = Parse(swap);

        Assert.False(workflow.TakesPrompt);
        Assert.Null(workflow.TriggerWords);
        Assert.Equal("", ComfyText.TriggerWordsPart(workflow, "trigger words"));
    }

    [Fact]
    public void ABaseAndARefiner_GiveTheFirstChainsWords()
    {
        const string refined = """
            {
              "4": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "base.safetensors" } },
              "10": { "class_type": "LoraLoader", "inputs": { "lora_name": "style.safetensors", "strength_model": 1, "strength_clip": 1, "model": ["4", 0], "clip": ["4", 1] } },
              "14": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "refiner.safetensors" } },
              "15": { "class_type": "LoraLoader", "inputs": { "lora_name": "refine.safetensors", "strength_model": 1, "strength_clip": 1, "model": ["14", 0], "clip": ["14", 1] } },
              "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "{{prompt}}", "clip": ["10", 1] } },
              "3": { "class_type": "KSamplerAdvanced", "inputs": { "noise_seed": "{{seed}}", "model": ["10", 0], "positive": ["6", 0] } },
              "16": { "class_type": "KSamplerAdvanced", "inputs": { "noise_seed": "{{seed}}", "model": ["15", 0], "positive": ["6", 0] } }
            }
            """;

        var workflow = Parse(refined, "---\nlora.style.safetensors: xyz_style\nlora.refine.safetensors: sharp\n---\n");

        Assert.Equal(["xyz_style"], workflow.TriggerWords);
    }

    [Fact]
    public void LoraWordsKey_RefusesANameNoKeyCanHold()
    {
        Assert.Equal("lora.sub\\style.safetensors", ComfyWorkflow.LoraWordsKey("sub\\style.safetensors"));
        Assert.Equal("lora.my style.safetensors", ComfyWorkflow.LoraWordsKey("my style.safetensors"));
        Assert.Null(ComfyWorkflow.LoraWordsKey("a:b.safetensors"));
        Assert.Null(ComfyWorkflow.LoraWordsKey("a\nb.safetensors"));
        Assert.Null(ComfyWorkflow.LoraWordsKey(" style.safetensors"));
        Assert.Null(ComfyWorkflow.LoraWordsKey(""));
    }

    // ── Joining them to a prompt ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HasPhrase_MatchesWholePhrases_InAnyCase()
    {
        Assert.True(ComfyWorkflow.HasPhrase("a (XYZ_Style:1.2) cat", "xyz_style"));
        Assert.False(ComfyWorkflow.HasPhrase("a smart cat", "art"));
        Assert.False(ComfyWorkflow.HasPhrase("xyz_style2, a cat", "xyz_style"));
        Assert.True(ComfyWorkflow.HasPhrase("glowing eyes", "Glowing Eyes"));
        Assert.True(ComfyWorkflow.HasPhrase("x (y) z", "(y)"));
        Assert.False(ComfyWorkflow.HasPhrase("anything", ""));
    }

    [Fact]
    public void JoinPositive_PutsTheWordsInFront_NeverTwice()
    {
        Assert.Equal("xyz_style, glowing eyes, a cat", ComfyStudio.JoinPositive(["xyz_style", "glowing eyes"], "a cat"));
        Assert.Equal("glowing eyes, (XYZ_STYLE:1.1), a cat", ComfyStudio.JoinPositive(["xyz_style", "glowing eyes"], "(XYZ_STYLE:1.1), a cat"));
        Assert.Equal("art, a smart cat", ComfyStudio.JoinPositive(["art"], "a smart cat"));
        Assert.Equal("xyz_style, a cat,", ComfyStudio.JoinPositive(["xyz_style"], " , a cat,"));
        Assert.Equal("xyz_style", ComfyStudio.JoinPositive(["xyz_style", "XYZ_style"], ""));
    }

    [Fact]
    public void JoinPositive_WithNothingToAdd_ReturnsThePromptExactly()
    {
        const string typed = "  score_9, (neon:1.2),  ";
        Assert.Same(typed, ComfyStudio.JoinPositive(null, typed));
        Assert.Same(typed, ComfyStudio.JoinPositive([], typed));
        Assert.Same(typed, ComfyStudio.JoinPositive(["Score_9"], typed));
    }

    // ── Every run ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AVerbatimRun_SendsAndReports_TheWordsInFront_AndATypedOneOnce()
    {
        Install(Graph);
        ServeOnePicture();

        var result = await new GenerateImageTool(_studio).InvokeAsync(Args(("prompt", "Glowing eyes, a cat"), ("verbatim", true), ("seed", 7)));

        Assert.IsType<ToolImageResult>(result);
        Assert.Equal("xyz_style, fine detail, Glowing eyes, a cat", SentPrompt());
        Assert.Contains("prompt: xyz_style, fine detail, Glowing eyes, a cat", ((ToolImageResult)result).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlankPrompt_IsStillRefused_WhenTheWorkflowHasWords()
    {
        Install(Graph);
        ServeOnePicture();

        var generation = await _studio.GenerateAsync(new ComfyRequest("  ", Verbatim: true), CancellationToken.None);

        Assert.Empty(generation.Images);
        Assert.DoesNotContain(_stub.Requests, r => r.Uri.AbsolutePath == "/prompt");
    }

    // ── What the model and the user read ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Describe_MarksTheWords_AndAddsTheGuide_OnlyWithSome()
    {
        var with = Parse(Graph);
        var without = Parse(Graph, "---\nfamily: sdxl\n---\nTips here.\n", "plain");

        string text = ComfyText.Describe([with, without]);
        Assert.Contains(ComfyText.TriggerWordsGuide, text, StringComparison.Ordinal);
        Assert.Contains(" · trigger words added: xyz_style, glowing eyes, fine detail", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ComfyText.TriggerWordsGuide, ComfyText.Describe([without]), StringComparison.Ordinal);
        Assert.DoesNotContain("trigger words", ComfyText.Describe([without]), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatus_ShowsTheWords_TheOfferedList_DoesNot()
    {
        Install(Graph);
        var workflows = _studio.Catalog.Workflows;

        Assert.Contains(ComfyText.StatusLines(workflows, [], [], true, ["style-txt2img"]), l => l.Contains(" · trigger words: xyz_style, glowing eyes, fine detail", StringComparison.Ordinal) && !l.Contains("hidden", StringComparison.Ordinal));
        Assert.DoesNotContain(ComfyText.OfferedLines(workflows, ["style-txt2img"], true), l => l.Contains("trigger words", StringComparison.Ordinal));
        // The catalog hands back the same instance, so Offered's Contains still finds a workflow with words.
        Assert.Single(ComfyWorkflowCatalog.Offered(workflows, ["style-txt2img"]));
    }

    // ── The sidecar a new workflow gets ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSidecar_WritesTheWords_AfterTheRoles_BeforeReinforce_SkippingWhatNoKeyCanHold()
    {
        string text = ComfyWorkflowFile.Sidecar(new ComfySidecar("d", ComfyFamily.Sdxl, null, null, null, null, null, "", ["the picture"], Reinforce: false,
            LoraWords: [("style.safetensors", "xyz_style, #1 look"), ("a:b.safetensors", "never"), ("a\nb.safetensors", "never"), ("empty.safetensors", "  ")]));

        Assert.Contains("image: the picture\nlora.style.safetensors: \"xyz_style, #1 look\"\nreinforce: false\n---\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("never", text, StringComparison.Ordinal);
        Assert.DoesNotContain("empty", text, StringComparison.Ordinal);
        // Read back as written: the # inside the words survives.
        Assert.Equal(["xyz_style", "#1 look"], Parse(Graph, text).TriggerWords);
    }

    // ── A LoRA switched off (2026-11-27, the user's ask) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOffLora_IsSplicedOutOfTheTemplate_ItsConsumersRewired_AndGivesNoWords()
    {
        var workflow = Parse(Graph, "---\nlora-off.STYLE.safetensors: Yes\n" + Sidecar["---\n".Length..]);

        var graph = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(workflow.GraphJson)!;
        Assert.Null(graph["10"]);
        Assert.Equal("4", graph["11"]!["inputs"]!["model"]![0]!.GetValue<string>());   // the next LoRA takes the checkpoint's model
        Assert.Equal("4", graph["6"]!["inputs"]!["clip"]![0]!.GetValue<string>());     // the encoders the checkpoint's CLIP
        Assert.Equal(["fine detail"], workflow.TriggerWords);
        Assert.Equal(["style.safetensors"], workflow.LorasOff);
        Assert.Equal(" · 1 LoRA off", ComfyText.LorasOffPart(workflow));
    }

    [Theory]
    [InlineData("lora-off.style.safetensors: no\n")]
    [InlineData("lora-off.gone.safetensors: yes\n")]
    [InlineData("")]
    public void AnOffKey_ThatIsNotYes_OrNamesNoLoader_KeepsEveryLora(string line)
    {
        var workflow = Parse(Graph, "---\n" + line + "family: sdxl\n---\n");
        Assert.NotNull(System.Text.Json.Nodes.JsonNode.Parse(workflow.GraphJson)!["10"]);
        Assert.Null(workflow.LorasOff);
        Assert.Equal("", ComfyText.LorasOffPart(workflow));
    }

    [Fact]
    public void AnOffLora_TheGraphCantLeaveOut_FailsTheLoad_WithWhy()
    {
        string custom = Graph.Replace("\"LoraLoaderModelOnly\"", "\"LoraLoaderBypass\"", StringComparison.Ordinal);
        Assert.False(ComfyWorkflow.TryParse("w", "w.json", custom, "---\nlora-off.detail.safetensors: yes\n---\n", out _, out string? problem));
        Assert.Equal(ComfyText.LoraOffProblem("detail.safetensors", ComfyText.EditOtherLoader("LoraLoaderBypass")), problem);
    }

    [Fact]
    public void TheSidecar_WritesAnOffLine_AfterTheWords_AndNoneForANameNoKeyCanHold()
    {
        string md = ComfyWorkflowFile.Sidecar(new ComfySidecar("", ComfyFamily.Sdxl, null, null, null, null, null, "", LoraWords: [("style.safetensors", "xyz")], LoraOff: ["style.safetensors", "bad:name"]));
        Assert.Contains("lora.style.safetensors: xyz\nlora-off.style.safetensors: " + ComfyWorkflow.LoraOffValue + "\n", md, StringComparison.Ordinal);
        Assert.DoesNotContain("bad:name", md, StringComparison.Ordinal);
        Assert.Null(ComfyWorkflow.LoraOffKey("bad:name"));
    }
}
