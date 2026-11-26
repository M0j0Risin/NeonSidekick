using System.Text;
using System.Text.Json.Nodes;
using NeonSidekick.Comfy;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The workflow manager's file and graph layer (2026-11-25): one sidecar key set in place (<see cref="ComfySidecarEdit"/>), a graph's parts
/// read and changed (<see cref="ComfyGraphEdit"/>), and an installed workflow saved, renamed, copied, moved and deleted (<see cref="ComfyWorkflowFile"/>).
/// </summary>
public sealed class ComfyEditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _profile;
    private readonly string _global;

    public ComfyEditTests()
    {
        _profile = Path.Combine(_dir, "profile", "comfy");
        _global = Path.Combine(_dir, "home", "comfy");
        Directory.CreateDirectory(_profile);
        Directory.CreateDirectory(_global);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private IReadOnlyList<string> Roots => [_profile, _global];

    private static JsonObject Graph(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static JsonObject Built(int clipSkip = 2, bool fromImage = false) =>
        Graph(ComfyGraphs.Build("portrait_txt2img", "model.safetensors", "euler", "normal", clipSkip, fromImage));

    // ── The sidecar ─────────────────────────────────────────────────────────

    [Fact]
    public void Set_KeepsCommentsUnknownKeysTipsAndCrlf_AndReplacesEveryCopyWithOne()
    {
        string text = "---\r\n# my note\r\nfamily: sdxl\r\nwidth: 800\r\nmystery: kept\r\nwidth: 900\r\nreinforce: false\r\n---\r\nTips stay.\r\n";
        string done = ComfySidecarEdit.SetNumber(text, "width", 1024)!;
        Assert.Equal("---\r\n# my note\r\nfamily: sdxl\r\nwidth: 1024\r\nmystery: kept\r\nreinforce: false\r\n---\r\nTips stay.\r\n", done);
        Assert.True(ComfyWorkflow.TryParse("w", "", """{"1":{"class_type":"X","inputs":{"t":"{{prompt}}"}}}""", done, out var workflow, out _));
        Assert.Equal(1024, workflow!.Width);
        Assert.False(workflow.Reinforce);
        Assert.Equal("Tips stay.", workflow.Tips);
    }

    [Fact]
    public void Set_OnTextWithNoHead_MakesOne_AndKeepsTheTextAsTips()
    {
        string done = ComfySidecarEdit.Set("Use short prompts.\n", "description", "Portraits")!;
        Assert.StartsWith("---\ndescription: Portraits\n---\n", done, StringComparison.Ordinal);
        Assert.True(ComfyWorkflow.TryParse("w", "", """{"1":{"class_type":"X","inputs":{"t":"{{prompt}}"}}}""", done, out var workflow, out _));
        Assert.Equal("Portraits", workflow!.Description);
        Assert.Equal("Use short prompts.", workflow.Tips);
        Assert.Equal("---\nfamily: pony\n---\n", ComfySidecarEdit.SetWord("", "family", "pony"));
    }

    [Fact]
    public void Set_AddsANewKeyBeforeTheClosingFence_OrLastInAnUnclosedHead()
    {
        Assert.Equal("---\nfamily: sdxl\nsteps: 20\n---\ntips\n", ComfySidecarEdit.SetNumber("---\nfamily: sdxl\n---\ntips\n", "steps", 20));
        Assert.Equal("---\nfamily: sdxl\nsteps: 20\n", ComfySidecarEdit.SetNumber("---\nfamily: sdxl\n", "steps", 20));
    }

    [Fact]
    public void Set_RemovesAKey_FoldsLineBreaks_AndRefusesAValueThatWouldNotReadBack()
    {
        Assert.Equal("---\nfamily: sdxl\n---\n", ComfySidecarEdit.SetNumber("---\nfamily: sdxl\nwidth: 1\nwidth: 2\n---\n", "width", null));
        Assert.Equal("---\nnegative: blurry, low quality\n---\n", ComfySidecarEdit.Set("---\n---\n", "negative", "blurry,\nlow quality"));
        Assert.Equal("---\nnegative: \"\"\n---\n", ComfySidecarEdit.Set("---\n---\n", "negative", ""));
        Assert.Null(ComfySidecarEdit.Set("---\n---\n", "negative", "bad\u0001char"));
        Assert.Equal("a: b # not a comment", ComfySidecarEdit.Get(ComfySidecarEdit.Set("", "description", "a: b # not a comment")!, "description"));
    }

    [Fact]
    public void Set_KeepsABom()
    {
        Assert.Equal("﻿---\nfamily: pony\n---\n", ComfySidecarEdit.SetWord("﻿---\nfamily: sdxl\n---\n", "family", "pony"));
    }

    // ── The graph ───────────────────────────────────────────────────────────

    [Fact]
    public void Read_ABuiltGraph_FindsEveryPart_AndChangesNothing()
    {
        var graph = Built();
        var before = graph.DeepClone();
        var parts = ComfyGraphEdit.Read(graph);
        Assert.Equal("euler", parts.Sampler.Value);
        Assert.Equal("normal", parts.Scheduler.Value);
        Assert.Equal("model.safetensors", parts.Model.Value);
        Assert.Equal("CheckpointLoaderSimple", parts.Model.ClassType);
        Assert.Equal("", parts.Vae.Value);
        Assert.True(parts.CheckpointVae);
        Assert.Equal("2", parts.ClipSkip.Value);
        Assert.Empty(parts.Loras.Loras);
        Assert.True(parts.Loras.Editable);
        Assert.Equal("neon/portrait_txt2img", parts.OutputName.Value);
        Assert.True(JsonNode.DeepEquals(before, graph));
    }

    [Fact]
    public void AddLora_OnACheckpoint_LandsBeforeTheClipSkip_AndRemovingInEitherOrderGivesTheGraphBack()
    {
        var original = Built();
        var graph = (JsonObject)original.DeepClone();
        var (first, why) = ComfyGraphEdit.AddLora(graph, "style.safetensors", 0.8);
        Assert.Null(why);
        Assert.Equal("LoraLoader", graph[first!]!["class_type"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(new JsonArray(first, 1), graph["10"]!["inputs"]!["clip"]));
        Assert.True(JsonNode.DeepEquals(new JsonArray(first, 0), graph["3"]!["inputs"]!["model"]));
        var (second, _) = ComfyGraphEdit.AddLora(graph, "detail.safetensors", 0.5);
        var loras = ComfyGraphEdit.Read(graph).Loras.Loras;
        Assert.Equal([first, second], loras.Select(l => l.Node));
        Assert.Equal(0.5, loras[1].StrengthModel);

        var twice = (JsonObject)graph.DeepClone();
        Assert.Null(ComfyGraphEdit.RemoveLora(graph, second!));
        Assert.Null(ComfyGraphEdit.RemoveLora(graph, first!));
        Assert.True(JsonNode.DeepEquals(original, graph));
        Assert.Null(ComfyGraphEdit.RemoveLora(twice, first!));
        Assert.Null(ComfyGraphEdit.RemoveLora(twice, second!));
        Assert.True(JsonNode.DeepEquals(original, twice));
    }

    [Fact]
    public void SetLoraStrength_ChangesModelAndClipInPlace()
    {
        var graph = Built();
        var (id, _) = ComfyGraphEdit.AddLora(graph, "style.safetensors", 1);
        Assert.Null(ComfyGraphEdit.SetLoraStrength(graph, id!, -0.5));
        Assert.Equal(-0.5, graph[id!]!["inputs"]!["strength_model"]!.GetValue<double>());
        Assert.Equal(-0.5, graph[id!]!["inputs"]!["strength_clip"]!.GetValue<double>());
    }

    /// <summary>A split-file graph (a diffusion model, its own CLIP and VAE loaders) with ids like ComfyUI's subgraph exports.</summary>
    private const string Split = """
        {
          "57:30": { "class_type": "UNETLoader", "inputs": { "unet_name": "diffusion.safetensors", "weight_dtype": "default" } },
          "57:31": { "class_type": "CLIPLoader", "inputs": { "clip_name": "encoder.safetensors", "type": "flux2" } },
          "57:32": { "class_type": "VAELoader", "inputs": { "vae_name": "vae.safetensors" } },
          "57:40": { "class_type": "ModelSamplingAuraFlow", "inputs": { "model": ["57:30", 0], "shift": 3 } },
          "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "{{prompt}}", "clip": ["57:31", 0] } },
          "7": { "class_type": "CLIPTextEncode", "inputs": { "text": "", "clip": ["57:31", 0] } },
          "3": { "class_type": "KSampler", "inputs": { "model": ["57:40", 0], "positive": ["6", 0], "negative": ["7", 0], "sampler_name": "euler", "scheduler": "simple" } },
          "8": { "class_type": "VAEDecode", "inputs": { "samples": ["3", 0], "vae": ["57:32", 0] } },
          "9": { "class_type": "SaveImage", "inputs": { "filename_prefix": "ComfyUI", "images": ["8", 0] } }
        }
        """;

    [Fact]
    public void ASplitGraph_TakesAModelOnlyLora_ThroughTheModelSampling_UnderAWholeNumberId()
    {
        var graph = Graph(Split);
        var parts = ComfyGraphEdit.Read(graph);
        Assert.Equal("diffusion.safetensors", parts.Model.Value);
        Assert.Equal("unet_name", parts.Model.Input);
        Assert.Equal("vae.safetensors", parts.Vae.Value);
        Assert.False(parts.CheckpointVae);
        Assert.False(parts.ClipSkip.Editable);
        var (id, why) = ComfyGraphEdit.AddLora(graph, "style.safetensors", 1);
        Assert.Null(why);
        Assert.Equal("10", id);
        Assert.Equal("LoraLoaderModelOnly", graph["10"]!["class_type"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(new JsonArray("57:30", 0), graph["10"]!["inputs"]!["model"]));
        Assert.True(JsonNode.DeepEquals(new JsonArray("10", 0), graph["57:40"]!["inputs"]!["model"]));
        Assert.Equal(ComfyText.EditNoCheckpointVae, ComfyGraphEdit.SetVae(graph, null));
    }

    [Fact]
    public void SetVae_AddsAVaeLoader_AndBackToTheCheckpointGivesTheGraphBack()
    {
        var original = Built();
        var graph = (JsonObject)original.DeepClone();
        Assert.Null(ComfyGraphEdit.SetVae(graph, "vae.safetensors"));
        Assert.Equal("vae.safetensors", ComfyGraphEdit.Read(graph).Vae.Value);
        Assert.Null(ComfyGraphEdit.SetVae(graph, null));
        Assert.True(JsonNode.DeepEquals(original, graph));
    }

    [Fact]
    public void SetClipSkip_SplicesTheNodeOut_AndAddsItBack()
    {
        var graph = Built(clipSkip: 2);
        Assert.Null(ComfyGraphEdit.SetClipSkip(graph, 0));
        Assert.True(JsonNode.DeepEquals(Built(clipSkip: 0), graph));
        Assert.Null(ComfyGraphEdit.SetClipSkip(graph, 2));
        Assert.Equal("2", ComfyGraphEdit.Read(graph).ClipSkip.Value);
        Assert.Null(ComfyGraphEdit.SetClipSkip(graph, 3));
        Assert.Equal(-3, graph.Single(p => p.Value!["class_type"]!.GetValue<string>() == "CLIPSetLastLayer").Value!["inputs"]!["stop_at_clip_layer"]!.GetValue<int>());
    }

    private static string Switched(bool on) => $$"""
        {
          "4": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "model.safetensors" } },
          "20": { "class_type": "LoraLoaderModelOnly", "inputs": { "lora_name": "fast.safetensors", "strength_model": 1, "model": ["4", 0] } },
          "21": { "class_type": "PrimitiveBoolean", "inputs": { "value": {{(on ? "true" : "false")}} } },
          "22": { "class_type": "ComfySwitchNode", "inputs": { "switch": ["21", 0], "on_true": ["20", 0], "on_false": ["4", 0] } },
          "3": { "class_type": "KSampler", "inputs": { "model": ["22", 0], "sampler_name": "euler", "scheduler": "simple" } }
        }
        """;

    [Fact]
    public void ASwitch_IsFollowedAlongItsChoice_AStrengthStillEdits_AndTheOtherBranchShowsSwitchedOff()
    {
        var on = Graph(Switched(true));
        var chain = ComfyGraphEdit.Read(on).Loras;
        Assert.Equal(ComfyText.EditBehindSwitch, chain.Why);
        Assert.True(Assert.Single(chain.Loras).Editable);
        Assert.Null(ComfyGraphEdit.SetLoraStrength(on, "20", 0.7));
        Assert.Equal(ComfyText.EditBehindSwitch, ComfyGraphEdit.RemoveLora(on, "20"));

        var off = ComfyGraphEdit.Read(Graph(Switched(false))).Loras;
        var lora = Assert.Single(off.Loras);
        Assert.True(lora.SwitchedOff);
        Assert.False(lora.Editable);
    }

    /// <summary>A switch linked to itself, or two linked to each other, read as a loop (2026-11-26, the review's #91): never a stack overflow.</summary>
    [Theory]
    [InlineData("""{"4":{"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"m"}},"21":{"class_type":"PrimitiveBoolean","inputs":{"value":true}},"22":{"class_type":"ComfySwitchNode","inputs":{"switch":["21",0],"on_true":["4",0],"on_false":["22",0]}},"3":{"class_type":"KSampler","inputs":{"model":["22",0]}}}""")]
    [InlineData("""{"4":{"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"m"}},"21":{"class_type":"PrimitiveBoolean","inputs":{"value":true}},"22":{"class_type":"ComfySwitchNode","inputs":{"switch":["21",0],"on_true":["4",0],"on_false":["23",0]}},"23":{"class_type":"ComfySwitchNode","inputs":{"switch":["21",0],"on_true":["4",0],"on_false":["22",0]}},"3":{"class_type":"KSampler","inputs":{"model":["22",0]}}}""")]
    public void ALinkedSwitch_IsALoop(string json) => Assert.Equal(ComfyText.EditLoop, ComfyGraphEdit.Read(Graph(json)).Loras.Why);

    /// <summary>A long chain of switches whose branches both go on ends as a loop at once, rather than doubling the walk per switch.</summary>
    [Fact]
    public void AChainOfSwitches_EndsAsALoop_Quickly()
    {
        var graph = new JsonObject
        {
            ["4"] = new JsonObject { ["class_type"] = "CheckpointLoaderSimple", ["inputs"] = new JsonObject { ["ckpt_name"] = "m" } },
            ["21"] = new JsonObject { ["class_type"] = "PrimitiveBoolean", ["inputs"] = new JsonObject { ["value"] = true } },
            ["3"] = new JsonObject { ["class_type"] = "KSampler", ["inputs"] = new JsonObject { ["model"] = new JsonArray("100", 0) } },
        };
        for (int i = 100; i < 160; i++)
        {
            string next = i == 159 ? "4" : (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            graph[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["class_type"] = "ComfySwitchNode",
                ["inputs"] = new JsonObject { ["switch"] = new JsonArray("21", 0), ["on_true"] = new JsonArray(next, 0), ["on_false"] = new JsonArray(next, 0) },
            };
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(ComfyText.EditLoop, ComfyGraphEdit.Read(graph).Loras.Why);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    /// <summary>A base and a refiner, each its own loader: the Model row names no one of them (2026-11-26, the review's #37).</summary>
    [Fact]
    public void TwoSamplersOnTwoLoaders_LeaveTheModelUnedited()
    {
        var graph = Graph("""
            {
              "4": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "base.safetensors" } },
              "5": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "refiner.safetensors" } },
              "3": { "class_type": "KSampler", "inputs": { "model": ["4", 0] } },
              "30": { "class_type": "KSampler", "inputs": { "model": ["5", 0] } }
            }
            """);
        Assert.Equal(ComfyText.EditSeveral(2, "model loaders"), ComfyGraphEdit.Read(graph).Model.Why);
    }

    [Fact]
    public void ABypassLoader_IsListedReadOnly_NeverHidden()
    {
        var graph = Graph("""
            {
              "4": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "model.safetensors" } },
              "20": { "class_type": "LoraLoaderBypass", "inputs": { "lora_name": "style.safetensors", "strength_model": 1, "model": ["4", 0], "clip": ["4", 1] } },
              "3": { "class_type": "KSampler", "inputs": { "model": ["20", 0] } }
            }
            """);
        var chain = ComfyGraphEdit.Read(graph).Loras;
        var lora = Assert.Single(chain.Loras);
        Assert.Equal("style.safetensors", lora.Name);
        Assert.Equal(ComfyText.EditOtherLoraNode("LoraLoaderBypass"), lora.Why);
        Assert.False(chain.Editable);
    }

    [Fact]
    public void TwoSamplers_OnOnePath_AreEditable_AndOnTwoPaths_AreNot()
    {
        var graph = Built();
        graph["30"] = new JsonObject { ["class_type"] = "KSampler", ["inputs"] = new JsonObject { ["model"] = new JsonArray("4", 0) } };
        var parts = ComfyGraphEdit.Read(graph);
        Assert.True(parts.Loras.Editable);
        Assert.Equal(ComfyText.EditSeveral(2, "samplers"), parts.Sampler.Why);
        ComfyGraphEdit.AddLora(graph, "style.safetensors", 1);
        Assert.True(ComfyGraphEdit.Read(graph).Loras.Editable);

        var differ = Built();
        differ["40"] = new JsonObject { ["class_type"] = "LoraLoaderModelOnly", ["inputs"] = new JsonObject { ["lora_name"] = "x.safetensors", ["strength_model"] = 1, ["model"] = new JsonArray("4", 0) } };
        differ["30"] = new JsonObject { ["class_type"] = "KSampler", ["inputs"] = new JsonObject { ["model"] = new JsonArray("40", 0) } };
        Assert.Equal(ComfyText.EditSamplersDiffer, ComfyGraphEdit.Read(differ).Loras.Why);
    }

    [Theory]
    [InlineData("""{"3":{"class_type":"KSampler","inputs":{"model":["20",0]}},"20":{"class_type":"LoraLoaderModelOnly","inputs":{"lora_name":"a","strength_model":1,"model":["20",0]}}}""")]
    [InlineData("""{"3":{"class_type":"KSampler","inputs":null},"4":{"class_type":"CheckpointLoaderSimple"}}""")]
    [InlineData("""{"3":{"class_type":"KSampler","inputs":{"model":[4,0],"sampler_name":["9",0]}},"4":{"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"m"}}}""")]
    [InlineData("""{"3":{"class_type":"KSampler","inputs":{"model":["99",0],"vae":[1]}}}""")]
    [InlineData("""{"3":{"class_type":"SamplerCustomAdvanced","inputs":{"guider":["5",0]}},"5":{"class_type":"CFGGuider","inputs":{"model":"x"}}}""")]
    public void OddShapes_NeverThrow_AndAreNeverChanged_ByARead(string json)
    {
        var graph = Graph(json);
        var before = graph.DeepClone();
        var parts = ComfyGraphEdit.Read(graph);
        Assert.NotNull(parts);
        Assert.True(JsonNode.DeepEquals(before, graph));
    }

    [Fact]
    public void ANumericLinkId_IsReadAsItsText()
    {
        var graph = Graph("""{"3":{"class_type":"KSampler","inputs":{"model":[4,0],"sampler_name":"euler"}},"4":{"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"m"}}}""");
        Assert.Equal("m", ComfyGraphEdit.Read(graph).Model.Value);
    }

    [Fact]
    public void Load_RefusesComments_AndRepeatedKeys()
    {
        Assert.Equal(ComfyText.EditNotPlainJson, ComfyGraphEdit.Load("{\"1\":{\"class_type\":\"X\",\"inputs\":{}}, // note\n}").Why);
        Assert.Equal(ComfyText.EditNotPlainJson, ComfyGraphEdit.Load("""{"1":{"class_type":"X","inputs":{}},"1":{"class_type":"Y","inputs":{}}}""").Why);
        Assert.NotNull(ComfyGraphEdit.Load("""{"1":{"class_type":"X","inputs":{}}}""").Graph);
    }

    [Fact]
    public void AGraphWithRepeatedKeys_IsSkippedByTheScanAndRefusedByImport_NeverThrown()
    {
        const string twice = """{"1":{"class_type":"KSampler","inputs":{"text":"{{prompt}}"}},"1":{"class_type":"KSampler","inputs":{}}}""";
        File.WriteAllText(Path.Combine(_profile, "twice.json"), twice);
        var (workflows, problems) = new ComfyWorkflowCatalog(() => Roots).Scan();
        Assert.Empty(workflows);
        Assert.Equal(ComfyWorkflow.NotJsonProblem, Assert.Single(problems).Problem);
        Assert.Equal(ComfyWorkflow.NotJsonProblem, ComfyImport.Placehold(twice).Problem);
    }

    // ── The files ───────────────────────────────────────────────────────────

    private void Install(string folder, string name, string? sidecar = "---\nfamily: sdxl\n---\n", string? prefix = null)
    {
        string json = ComfyGraphs.Build(name, "model.safetensors", "euler", "normal", 0, false);
        if (prefix is not null)
        {
            json = json.Replace("neon/" + name, prefix, StringComparison.Ordinal);
        }

        File.WriteAllText(Path.Combine(folder, name + ".json"), json);
        if (sidecar is not null)
        {
            File.WriteAllText(Path.Combine(folder, name + ".md"), sidecar);
        }
    }

    private static string Prefix(string jsonPath) => ComfyGraphEdit.Read(Graph(File.ReadAllText(jsonPath))).OutputName.Value!;

    [Fact]
    public void Rename_MovesBothFiles_AndTheOutputNameFollows()
    {
        Install(_profile, "portrait_txt2img");
        Assert.Null(ComfyWorkflowFile.Rename(_profile, Roots, "portrait_txt2img", "portrait_v2"));
        Assert.False(File.Exists(Path.Combine(_profile, "portrait_txt2img.json")));
        Assert.Equal("---\nfamily: sdxl\n---\n", File.ReadAllText(Path.Combine(_profile, "portrait_v2.md")));
        Assert.Equal("neon/portrait_v2", Prefix(Path.Combine(_profile, "portrait_v2.json")));
    }

    /// <summary>A rename writes only the output name's string, the rest of the file as it was (2026-11-26, the review's #80).</summary>
    [Fact]
    public void Rename_KeepsTheFilesOtherBytes()
    {
        Install(_profile, "portrait_txt2img");
        string path = Path.Combine(_profile, "portrait_txt2img.json");
        string odd = File.ReadAllText(path).Replace("\"euler\"", "\"euler\"   ", StringComparison.Ordinal);
        File.WriteAllText(path, odd);
        Assert.Null(ComfyWorkflowFile.Rename(_profile, Roots, "portrait_txt2img", "portrait_v2"));
        Assert.Equal(odd.Replace("\"neon/portrait_txt2img\"", "\"neon/portrait_v2\"", StringComparison.Ordinal), File.ReadAllText(Path.Combine(_profile, "portrait_v2.json")));
    }

    /// <summary>A prefix written with JSON escapes still follows, by the rewrite.</summary>
    [Fact]
    public void Rename_AnEscapedPrefix_StillFollows()
    {
        Install(_profile, "portrait_txt2img", prefix: "neon\\/portrait_txt2img");
        Assert.Null(ComfyWorkflowFile.Rename(_profile, Roots, "portrait_txt2img", "portrait_v2"));
        Assert.Equal("neon/portrait_v2", Prefix(Path.Combine(_profile, "portrait_v2.json")));
    }

    [Fact]
    public void Rename_LeavesAnotherWorkflowsOutputName()
    {
        Install(_profile, "portrait_txt2img", prefix: "other/thing");
        Assert.Null(ComfyWorkflowFile.Rename(_profile, Roots, "portrait_txt2img", "portrait_v2"));
        Assert.Equal("other/thing", Prefix(Path.Combine(_profile, "portrait_v2.json")));
    }

    [Fact]
    public void Rename_WritesAFamilyGuessedFromTheOldName_IntoANewMd()
    {
        Install(_profile, "pony-portrait", sidecar: null);
        Assert.Null(ComfyWorkflowFile.Rename(_profile, Roots, "pony-portrait", "portrait"));
        Assert.Equal("---\nfamily: pony\n---\n", File.ReadAllText(Path.Combine(_profile, "portrait.md")));
    }

    [Fact]
    public void Rename_RefusesATakenName_ALeftoverMd_AndADeviceName_ChangingNothing()
    {
        Install(_profile, "portrait_txt2img");
        Install(_global, "landscape");
        File.WriteAllText(Path.Combine(_profile, "stray.md"), "x");
        Assert.Equal(ComfyText.NameTaken("landscape", _global), ComfyWorkflowFile.Rename(_profile, Roots, "portrait_txt2img", "landscape"));
        Assert.Equal(ComfyText.LeftoverSidecar(Path.Combine(_profile, "stray.md")), ComfyWorkflowFile.Rename(_profile, Roots, "portrait_txt2img", "stray"));
        Assert.Equal(ComfyText.DeviceName("con"), ComfyWorkflowFile.Rename(_profile, Roots, "portrait_txt2img", "con"));
        Assert.True(File.Exists(Path.Combine(_profile, "portrait_txt2img.json")));
        Assert.True(File.Exists(Path.Combine(_profile, "portrait_txt2img.md")));
    }

    [Fact]
    public void Rename_OnlyChangingCase_IsFree()
    {
        Install(_profile, "Portrait", sidecar: "---\nfamily: sdxl\n---\n");
        Assert.Null(ComfyWorkflowFile.Rename(_profile, Roots, "Portrait", "portrait"));
        Assert.Contains("portrait.json", Directory.GetFiles(_profile).Select(Path.GetFileName));
        Assert.Equal("portrait", Assert.Single(new ComfyWorkflowCatalog(() => Roots).Scan().Workflows).Name);
    }

    [Fact]
    public void Rename_ThatFailsHalfway_PutsTheMdBack()
    {
        Install(_profile, "portrait_txt2img");
        Directory.CreateDirectory(Path.Combine(_profile, "portrait_v2.json", "in-the-way"));   // a folder where the .json would go
        Assert.NotNull(ComfyWorkflowFile.Rename(_profile, [_global], "portrait_txt2img", "portrait_v2"));
        Assert.True(File.Exists(Path.Combine(_profile, "portrait_txt2img.json")));
        Assert.True(File.Exists(Path.Combine(_profile, "portrait_txt2img.md")));
        Assert.False(File.Exists(Path.Combine(_profile, "portrait_v2.md")));
    }

    [Fact]
    public void Duplicate_LeavesTheOriginal_AndMove_MakesTheGlobalFolder()
    {
        Install(_profile, "portrait_txt2img");
        Assert.Null(ComfyWorkflowFile.Duplicate(_profile, Roots, "portrait_txt2img", "portrait_copy"));
        Assert.Equal("neon/portrait_txt2img", Prefix(Path.Combine(_profile, "portrait_txt2img.json")));
        Assert.Equal("neon/portrait_copy", Prefix(Path.Combine(_profile, "portrait_copy.json")));

        Directory.Delete(_global);
        Assert.Null(ComfyWorkflowFile.Move(_profile, _global, Roots, "portrait_copy"));
        Assert.True(File.Exists(Path.Combine(_global, "portrait_copy.json")));
        Assert.True(File.Exists(Path.Combine(_global, "portrait_copy.md")));
        Assert.False(File.Exists(Path.Combine(_profile, "portrait_copy.json")));
    }

    [Fact]
    public void Move_RefusesANameTakenThere()
    {
        Install(_profile, "portrait_txt2img");
        Install(_global, "portrait_txt2img");
        Assert.Equal(ComfyText.NameTaken("portrait_txt2img", _global), ComfyWorkflowFile.Move(_profile, _global, Roots, "portrait_txt2img"));
    }

    [Fact]
    public void Delete_SendsBothFilesToTheBin()
    {
        Install(_profile, "portrait_txt2img");
        var bin = new FakeRecycleBin();
        var (way, error) = ComfyWorkflowFile.Delete(_profile, "portrait_txt2img", bin);
        Assert.Null(error);
        Assert.Equal(DeleteWay.Recycled, way);
        Assert.Equal(2, bin.Moved.Count);
        Assert.Empty(Directory.GetFiles(_profile));
        Assert.Equal(ComfyText.WorkflowGone("portrait_txt2img"), ComfyWorkflowFile.Delete(_profile, "portrait_txt2img", bin).Error);
    }

    [Fact]
    public void Save_IsRefusedWhenAFileChangedOnDisk()
    {
        Install(_profile, "portrait_txt2img");
        string json = Path.Combine(_profile, "portrait_txt2img.json");
        string md = Path.Combine(_profile, "portrait_txt2img.md");
        var jsonFile = ComfyFileText.Read(json);
        var mdFile = ComfyFileText.Read(md);
        File.AppendAllText(md, "edited by hand\n");
        Assert.Equal(ComfyText.ChangedOnDisk(md), ComfyWorkflowFile.Save("portrait_txt2img", jsonFile, null, mdFile, "---\nfamily: pony\n---\n"));
        Assert.EndsWith("edited by hand\n", File.ReadAllText(md), StringComparison.Ordinal);
    }

    [Fact]
    public void Save_KeepsTheMdsBom_AndTheJsonsLineEndings()
    {
        Install(_profile, "portrait_txt2img");
        string json = Path.Combine(_profile, "portrait_txt2img.json");
        string md = Path.Combine(_profile, "portrait_txt2img.md");
        File.WriteAllText(json, File.ReadAllText(json).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));
        File.WriteAllBytes(md, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("---\nfamily: sdxl\n---\n")]);
        var jsonFile = ComfyFileText.Read(json);
        var mdFile = ComfyFileText.Read(md);
        var graph = ComfyGraphEdit.Load(jsonFile.Text).Graph!;
        ComfyGraphEdit.AddLora(graph, "style.safetensors", 1);
        Assert.Null(ComfyWorkflowFile.Save("portrait_txt2img", jsonFile, graph.ToJsonString(ComfyGraphs.Indented), mdFile, ComfySidecarEdit.SetNumber(mdFile.Text, "steps", 30)));
        Assert.Equal(Encoding.UTF8.GetPreamble(), File.ReadAllBytes(md)[..3]);
        string written = File.ReadAllText(json);
        Assert.Contains("\r\n", written, StringComparison.Ordinal);
        Assert.DoesNotMatch("(?<!\r)\n", written);
        Assert.Contains("style.safetensors", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_RefusesALeftoverMd_AndADeviceName()
    {
        File.WriteAllText(Path.Combine(_profile, "portrait.md"), "stray");
        Assert.Equal(ComfyText.LeftoverSidecar(Path.Combine(_profile, "portrait.md")), ComfyWorkflowFile.Add(_profile, Roots, "portrait", ComfyGraphs.Build("portrait", "m", "euler", "normal", 0, false), "---\n---\n"));
        Assert.Equal("stray", File.ReadAllText(Path.Combine(_profile, "portrait.md")));
        Assert.Equal(ComfyText.DeviceName("nul"), ComfyWorkflowFile.Add(_profile, Roots, "nul", "{}", "---\n---\n"));
    }

    [Fact]
    public void FreeName_CountsUp_AndSuggestDropsALeadingUnderscore()
    {
        Install(_profile, "portrait");
        Install(_global, "portrait-2");
        Assert.Equal("portrait-3", ComfyWorkflowFile.FreeName("portrait", Roots, _profile));
        // Kind both (2026-11-26, the review's #3): each suffixed name free, and the stem cut so the longest still fits 64.
        Install(_profile, "dream_img2img");
        Assert.Equal("dream-2", ComfyWorkflowFile.FreeName("dream", Roots, _profile, ["_txt2img", "_img2img"]));
        Assert.True(ComfyWorkflowFile.FreeName(new string('a', 70), Roots, _profile, ["_txt2img", "_img2img"]).Length <= 64 - "_txt2img".Length);
        Assert.Equal("hidden", ComfyWorkflowFile.Suggest("_hidden.safetensors"));
    }

    [Fact]
    public void EnsureSidecar_MakesTheMissingMdOnce()
    {
        Install(_profile, "portrait", sidecar: null);
        var (path, created, error) = ComfyWorkflowFile.EnsureSidecar(Path.Combine(_profile, "portrait.json"), Comfy.ComfyFamily.Pony);
        Assert.Null(error);
        Assert.True(created);
        Assert.Equal("---\nfamily: pony\n---\n", File.ReadAllText(path!));
        Assert.False(ComfyWorkflowFile.EnsureSidecar(Path.Combine(_profile, "portrait.json"), Comfy.ComfyFamily.Sdxl).Created);
    }

    [Fact]
    public void TheSidecar_WritesReinforceAndTips()
    {
        string text = ComfyWorkflowFile.Sidecar(new ComfySidecar("d", Comfy.ComfyFamily.Sdxl, null, null, null, null, null, "", Reinforce: false, Tips: "Keep it short."));
        Assert.EndsWith("reinforce: false\n---\nKeep it short.\n", text, StringComparison.Ordinal);
    }

    // ── SetBody: the tips under the head ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetBody_ReplacesTheTips_KeepsTheHeadAndItsLineEndings()
    {
        Assert.Equal("---\r\n# note\r\nfamily: sdxl\r\n---\r\nNew tips.\r\nTwo lines.\r\n", ComfySidecarEdit.SetBody("---\r\n# note\r\nfamily: sdxl\r\n---\r\nOld tips.\r\n", "New tips.\nTwo lines.\n"));
        Assert.Equal("---\nfamily: sdxl\n---\n", ComfySidecarEdit.SetBody("---\nfamily: sdxl\n---\nOld.\n", "  "));
        Assert.Equal("---\n---\nTips.\n", ComfySidecarEdit.SetBody("", "Tips."));
        Assert.Equal("---\nfamily: sdxl\n---\nTips.\n", ComfySidecarEdit.SetBody("---\nfamily: sdxl\n", "Tips."));
    }

    // ── ComfyCheck: /object_info answers and findings ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseNode_ReadsBothComboShapes_AndNeverThrows()
    {
        var info = ComfyCheck.ParseNode("""{"KSampler":{"input":{"required":{"sampler_name":[["euler","dpmpp_2m"]],"steps":["INT",{"default":20}]},"optional":{"scheduler":["COMBO",{"options":["normal","karras"]}]}}}}""", "KSampler")!;
        Assert.False(info.Missing);
        Assert.Equal(["euler", "dpmpp_2m"], info.Inputs["sampler_name"].Choices);
        Assert.Equal(["normal", "karras"], info.Inputs["scheduler"].Choices);
        Assert.Null(info.Inputs["steps"].Choices);

        Assert.True(ComfyCheck.ParseNode("{}", "Gone")!.Missing);
        Assert.Null(ComfyCheck.ParseNode("[1]", "X"));
        Assert.Null(ComfyCheck.ParseNode("not json", "X"));
        Assert.Null(ComfyCheck.ParseNode("""{"X":5}""", "X"));
        Assert.Empty(ComfyCheck.ParseNode("""{"X":{"input":7}}""", "X")!.Inputs);

        var upload = ComfyCheck.ParseNode("""{"LoadImage":{"input":{"required":{"image":[["a.png"],{"image_upload":true}]}}}}""", "LoadImage")!;
        Assert.True(upload.Inputs["image"].Skip);
        var dynamic = ComfyCheck.ParseNode("""{"X":{"input":{"required":{"mode":["COMFY_DYNAMICCOMBO_V3",{"options":[{"key":"a"}]}]}}}}""", "X")!;
        Assert.True(dynamic.Inputs["mode"].Skip);
        // Objects among strings (2026-11-26, the review's #8): every string kept, the check off.
        var mixed = ComfyCheck.ParseNode("""{"X":{"input":{"required":{"mode":[["a",{"key":"k"},"b"]]}}}}""", "X")!;
        Assert.Equal(["a", "b"], mixed.Inputs["mode"].Choices);
        Assert.True(mixed.Inputs["mode"].Skip);
        var empty = ComfyCheck.ParseNode("""{"LoraLoader":{"input":{"required":{"lora_name":[[]]}}}}""", "LoraLoader")!;
        Assert.Equal([], empty.Inputs["lora_name"].Choices);
        Assert.False(empty.Inputs["lora_name"].Skip);
    }

    [Fact]
    public void Findings_NameAMissingClass_AndAValueOffTheList_SkippingWhatProvesNothing()
    {
        var graph = (JsonObject)JsonNode.Parse("""
            {
              "1": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "gone.safetensors" } },
              "2": { "class_type": "KSampler", "inputs": { "sampler_name": "euler", "scheduler": "{{scheduler}}", "model": ["1", 0], "extra": "anything" } },
              "3": { "class_type": "SomeCustomNode", "inputs": {} },
              "4": { "class_type": "LoraLoader", "inputs": { "lora_name": "style.safetensors" } },
              "5": { "class_type": "LoadImage", "inputs": { "image": "elsewhere.png" } },
              "6": { "class_type": "Unread", "inputs": { "x": "y" } },
              "7": "odd",
              "8": { "class_type": 5 }
            }
            """)!;
        Assert.Equal(["CheckpointLoaderSimple", "KSampler", "SomeCustomNode", "LoraLoader", "LoadImage", "Unread"], ComfyCheck.Classes(graph));
        var info = new Dictionary<string, ComfyNodeInfo?>
        {
            ["CheckpointLoaderSimple"] = ComfyCheck.ParseNode("""{"CheckpointLoaderSimple":{"input":{"required":{"ckpt_name":[["model.safetensors"]]}}}}""", "CheckpointLoaderSimple"),
            ["KSampler"] = ComfyCheck.ParseNode("""{"KSampler":{"input":{"required":{"sampler_name":[["euler"]],"scheduler":[["normal"]]}}}}""", "KSampler"),
            ["SomeCustomNode"] = ComfyNodeInfo.Unknown,
            ["LoraLoader"] = ComfyCheck.ParseNode("""{"LoraLoader":{"input":{"required":{"lora_name":[[]]}}}}""", "LoraLoader"),
            ["LoadImage"] = ComfyCheck.ParseNode("""{"LoadImage":{"input":{"required":{"image":[["a.png"],{"image_upload":true}]}}}}""", "LoadImage"),
            ["Unread"] = null,
        };

        Assert.Equal(
        [
            ComfyText.CheckNotListed("1", "CheckpointLoaderSimple", "ckpt_name", "gone.safetensors"),
            ComfyText.CheckNoNode("3", "SomeCustomNode"),
            ComfyText.CheckNotListed("4", "LoraLoader", "lora_name", "style.safetensors"),
        ], ComfyCheck.Findings(graph, info));
    }
}
