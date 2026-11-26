using NeonSidekick.App;
using NeonSidekick.Comfy;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The ComfyUI workflows list (2026-11-25, the workflow manager): Space offers, a/n all or none, Del deletes, Enter opens the actions; the
/// shortened wizard (Source, Kind, Checkpoint, Name, then the summary), Edit, Test and the test connection row.
/// </summary>
public partial class ToolsMenuTests
{
    private const int ComfyListRow = 3;   // the ComfyUI tab: tools, URL, test connection, then the list

    /// <summary>An installed workflow made as the wizard makes one: a built graph and a sidecar.</summary>
    private static void ComfyBuilt(string folder, string name, string sidecar = "---\nfamily: sdxl\n---\n", Action<System.Text.Json.Nodes.JsonObject>? edit = null)
    {
        Directory.CreateDirectory(folder);
        var graph = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(ComfyGraphs.Build(name, "model-a.safetensors", "euler", "normal", 0, false))!;
        edit?.Invoke(graph);
        File.WriteAllText(Path.Combine(folder, name + ".json"), graph.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, name + ".md"), sidecar);
    }

    /// <summary>A ComfyUI stand-in with made-up models: two checkpoints, a LoRA, a VAE, the sampler lists, and a job that makes one real picture.</summary>
    private (ComfyClient Client, StubHttpMessageHandler Stub) ComfyPictureStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? prompt = null)
    {
        var stub = new StubHttpMessageHandler()
            .Map(ComfyServer + "/object_info/CheckpointLoaderSimple", System.Net.HttpStatusCode.OK, "{\"CheckpointLoaderSimple\":{\"input\":{\"required\":{\"ckpt_name\":[[\"model-a.safetensors\",\"pony-model.safetensors\"]]}}}}")
            .Map(ComfyServer + "/object_info/LoraLoader", System.Net.HttpStatusCode.OK, "{\"LoraLoader\":{\"input\":{\"required\":{\"lora_name\":[[\"style.safetensors\"]]}}}}")
            .Map(ComfyServer + "/object_info/VAELoader", System.Net.HttpStatusCode.OK, "{\"VAELoader\":{\"input\":{\"required\":{\"vae_name\":[[\"vae.safetensors\"]]}}}}")
            .Map(ComfyServer + "/object_info/KSampler", System.Net.HttpStatusCode.OK, "{\"KSampler\":{\"input\":{\"required\":{\"sampler_name\":[[\"euler\",\"euler_ancestral\"]],\"scheduler\":[[\"normal\",\"karras\"]]}}}}")
            .Map(ComfyServer + "/object_info/", System.Net.HttpStatusCode.OK, "{}")
            .Map(ComfyServer + "/system_stats", System.Net.HttpStatusCode.OK, "{\"system\":{\"comfyui_version\":\"0.3.1\"}}")
            .Map(ComfyServer + "/history/", System.Net.HttpStatusCode.OK, "{\"t-1\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"neon_00001_.png\",\"subfolder\":\"neon\",\"type\":\"output\"}]}}}}")
            .Map(ComfyServer + "/view", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(System.Net.HttpStatusCode.OK, FakeHeadlessBrowser.TinyPng, "image/png")));
        stub = prompt is null ? stub.Map(ComfyServer + "/prompt", System.Net.HttpStatusCode.OK, "{\"prompt_id\":\"t-1\"}") : stub.Map(ComfyServer + "/prompt", prompt);
        return (new ComfyClient(new Uri(ComfyServer), new HttpClient(stub), TimeSpan.FromMilliseconds(1)), stub);
    }

    private void OpenComfyList() => OpenImagesRow(ComfyListRow);

    private static void Back(List<ConsoleKeyInfo> keys, int count) => keys.AddRange(Enumerable.Repeat(Keys.Escape, count));

    [Fact]
    public async Task OnThePane_TheComfyList_SelectAll_SavesTodaysList_SelectNone_AnEmptyOne()
    {
        ComfyWorkflowFile(_settings.ProfileComfyDirectory, "portrait");
        ComfyWorkflowFile(_settings.GlobalComfyDirectory, "landscape");
        var (menu, _, _) = PaneMenu();
        OpenComfyList();
        Push(Keys.Char('a'));
        Push(Keys.Escape, Keys.Escape);   // the list, then the tab (the group menu stays out on the second run's count)

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(2, _settings.Current.ComfyWorkflowsOffered!.Count);

        (menu, _, _) = PaneMenu();
        OpenComfyList();
        Push(Keys.Char('n'));
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([], _settings.Current.ComfyWorkflowsOffered);
    }

    /// <summary>Space ticks the workflow under the cursor (the list opens on the first one), Enter opens its actions, and the row's value counts it.</summary>
    [Fact]
    public async Task OnThePane_TheComfyList_SpaceOffers_EnterOpensTheActions_AndTheValueCounts()
    {
        ComfyWorkflowFile(_settings.ProfileComfyDirectory, "portrait");
        ComfyWorkflowFile(_settings.GlobalComfyDirectory, "landscape");
        var (menu, _, _) = PaneMenu();
        OpenComfyList();
        Push(Keys.Char(' '));                     // landscape on (the list is by name)
        Push(Keys.Down, Keys.Enter);              // portrait's actions
        Push(Keys.Escape, Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["landscape"], _settings.Current.ComfyWorkflowsOffered);
        Assert.Matches(@"ComfyUI workflows +none of 2 offered", Output);
        Assert.Matches(@"ComfyUI workflows +1 of 2 offered", Output);
        Assert.Contains(SettingsMenu.ComfyNewWorkflowRow, Output);
        Assert.Contains("[x] landscape", Output);
        Assert.Contains(SettingsMenu.ComfyDuplicateAction, Output);
        Assert.Contains(SettingsMenu.ComfyMoveToGlobalAction, Output);   // portrait is the profile's
        Assert.Contains(SettingsMenu.ComfyManagerKeys, Output);
    }

    /// <summary>A file the catalog skipped is listed under Skipped with its reason, and Space on it does nothing.</summary>
    [Fact]
    public async Task OnThePane_TheComfyList_ListsASkippedFile_WithItsReason()
    {
        Directory.CreateDirectory(_settings.ProfileComfyDirectory);
        File.WriteAllText(Path.Combine(_settings.ProfileComfyDirectory, "broken.json"), "{ not json");
        var (menu, _, _) = PaneMenu();
        OpenComfyList();
        Push(Keys.Char(' '));
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.ComfySkippedHeading, Output);
        Assert.Contains("broken.json", Output);
        Assert.Null(_settings.Current.ComfyWorkflowsOffered);
    }

    /// <summary>Rename moves both files, follows the output name, and keeps the loaded profile's lists in step where the name stood.</summary>
    [Fact]
    public async Task OnThePane_TheComfyList_Rename_FollowsTheFilesAndTheLists()
    {
        ComfyBuilt(_settings.ProfileComfyDirectory, "portrait");
        _settings.Update(d => { d.ComfyWorkflowsOffered = ["portrait"]; d.BotChatLimitedComfyWorkflows = ["portrait"]; });
        var (menu, _, _) = PaneMenu();
        OpenComfyList();
        Push(Keys.Enter);                                              // portrait's actions
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // Rename
        Push([.. Enumerable.Repeat(Keys.Backspace, "portrait".Length)]);
        Type("portrait-v2");
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_settings.ProfileComfyDirectory, "portrait-v2.json")));
        Assert.True(File.Exists(Path.Combine(_settings.ProfileComfyDirectory, "portrait-v2.md")));
        Assert.False(File.Exists(Path.Combine(_settings.ProfileComfyDirectory, "portrait.json")));
        Assert.Contains("\"neon/portrait-v2\"", File.ReadAllText(Path.Combine(_settings.ProfileComfyDirectory, "portrait-v2.json")));
        Assert.Equal(["portrait-v2"], _settings.Current.ComfyWorkflowsOffered);
        Assert.Equal(["portrait-v2"], _settings.Current.BotChatLimitedComfyWorkflows);
        Assert.Contains(SettingsMenu.ComfyRenamedNotice("portrait", "portrait-v2", global: false), Output);
    }

    /// <summary>Duplicate offers a free name and leaves the copy unticked; Move takes a workflow to the global folder.</summary>
    [Fact]
    public async Task OnThePane_TheComfyList_Duplicate_ThenMove()
    {
        ComfyBuilt(_settings.ProfileComfyDirectory, "portrait");
        _settings.Update(d => d.ComfyWorkflowsOffered = ["portrait"]);
        var (menu, _, _) = PaneMenu();
        OpenComfyList();
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // Duplicate
        Push(Keys.Enter);                                                 // portrait-2, suggested
        Push(Keys.Enter);                                                 // the list follows the copy: its actions
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // Move to global
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_settings.ProfileComfyDirectory, "portrait.json")));
        Assert.True(File.Exists(Path.Combine(_settings.GlobalComfyDirectory, "portrait-2.json")));
        Assert.True(File.Exists(Path.Combine(_settings.GlobalComfyDirectory, "portrait-2.md")));
        Assert.False(File.Exists(Path.Combine(_settings.ProfileComfyDirectory, "portrait-2.json")));
        Assert.Equal(["portrait"], _settings.Current.ComfyWorkflowsOffered);
        Assert.Contains(SettingsMenu.ComfyDuplicatedNotice("portrait", "portrait-2"), Output);
        Assert.Contains(SettingsMenu.ComfyMovedNotice("portrait-2", global: true), Output);
    }

    /// <summary>Del asks (No first), then the files go and the lists drop the name; a profile workflow that hid a global one keeps the tick.</summary>
    [Fact]
    public async Task OnThePane_TheComfyList_Delete_AsksFirst_AndKeepsTheTickOfAnUncoveredGlobal()
    {
        ComfyBuilt(_settings.ProfileComfyDirectory, "landscape");
        ComfyBuilt(_settings.ProfileComfyDirectory, "portrait");
        ComfyBuilt(_settings.GlobalComfyDirectory, "portrait");
        _settings.Update(d => { d.ComfyWorkflowsOffered = ["landscape", "portrait"]; d.BotChatLimitedComfyWorkflows = ["landscape"]; });
        var (menu, _, _) = PaneMenu();
        OpenComfyList();
        Push(Keys.Delete, Keys.Enter);                       // landscape: No, kept
        Push(Keys.Delete, Keys.Char('y'), Keys.Enter);       // landscape: gone
        Push(Keys.Delete, Keys.Char('y'), Keys.Enter);       // portrait (the profile's): gone, the global one shows
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(ChatScreen.KeptNotice, Output);
        Assert.False(File.Exists(Path.Combine(_settings.ProfileComfyDirectory, "landscape.json")));
        Assert.False(File.Exists(Path.Combine(_settings.ProfileComfyDirectory, "landscape.md")));
        Assert.False(File.Exists(Path.Combine(_settings.ProfileComfyDirectory, "portrait.json")));
        Assert.True(File.Exists(Path.Combine(_settings.GlobalComfyDirectory, "portrait.json")));
        Assert.Equal(["portrait"], _settings.Current.ComfyWorkflowsOffered);
        Assert.Null(_settings.Current.BotChatLimitedComfyWorkflows);
        Assert.Contains(SettingsMenu.ComfyDeleteUncoveredNotice("portrait"), Output);
    }

    /// <summary>
    /// The build path, shortened: Source, Kind, Checkpoint, Name, then the summary with the family guessed from the checkpoint and its
    /// defaults; a small test, then both files saved and offered.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheComfyWizard_BuildsFromTheServer_TestsIt_AndSavesIt()
    {
        var (client, stub) = ComfyPictureStub();
        var (menu, _, settings) = PaneMenu(comfy: () => client);
        settings.ComfyTestFolder = Path.Combine(_dir, "comfy-test");
        string? shown = null;
        settings.ShowPicture = path => { shown = path; return "in the picture viewer"; };
        OpenComfyList();
        Push(Keys.Enter);                     // + New workflow
        Push(Keys.Enter);                     // build
        Push(Keys.Enter);                     // text → image
        Push(Keys.Down, Keys.Enter);          // pony-model: family pony
        Push([.. Enumerable.Repeat(Keys.Backspace, 20)]);
        Type("portrait");
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test (under the two save rows)
        Push(Keys.Enter, Keys.Escape);            // Run, then back to the summary
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string json = Path.Combine(_settings.ProfileComfyDirectory, "portrait.json");
        Assert.True(ComfyWorkflow.TryLoad(json, out var workflow, out string? problem), problem);
        Assert.Equal(ComfyFamily.Pony, workflow!.Family);
        Assert.Equal(ComfyFamilies.PonyNegative, workflow.Defaults.Negative);
        string text = File.ReadAllText(json);
        Assert.Contains("\"CLIPSetLastLayer\"", text);
        Assert.Contains("\"euler_ancestral\"", text);
        Assert.Contains("\"pony-model.safetensors\"", text);
        string queued = stub.Requests.Single(r => r.Uri.AbsolutePath == "/prompt").Body!;
        Assert.Contains("\"width\":512", queued);   // the test is small
        Assert.Contains("\"steps\":8", queued);
        Assert.NotNull(shown);
        Assert.StartsWith(settings.ComfyTestFolder, shown);
        Assert.EndsWith(".png", shown);
        Assert.True(File.Exists(shown));
        Assert.Contains("Tested 'portrait' in ", Output);
        Assert.Contains(SettingsMenu.ComfyWorkflowAdded("portrait", _settings.ProfileComfyDirectory), Output);
        Assert.Equal(["portrait"], _settings.Current.ComfyWorkflowsOffered);
        Assert.Contains(SettingsMenu.ComfySectionHeadings[0], Output);
    }

    /// <summary>Kind both saves a text and an image workflow, both offered.</summary>
    [Fact]
    public async Task OnThePane_TheComfyWizard_Both_SavesTwo_AndOffersBoth()
    {
        var (client, _) = ComfyPictureStub();
        var (menu, _, _) = PaneMenu(comfy: () => client);
        OpenComfyList();
        Push(Keys.Enter, Keys.Enter);                  // + New workflow, build
        Push(Keys.Down, Keys.Down, Keys.Enter);        // both
        Push(Keys.Enter);                              // model-a
        Push([.. Enumerable.Repeat(Keys.Backspace, 20)]);
        Type("portrait");
        Push(Keys.Enter);                              // Save, and offer it
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(ComfyWorkflow.TryLoad(Path.Combine(_settings.ProfileComfyDirectory, "portrait" + SettingsMenu.ComfyTextSuffix + ".json"), out var text, out _));
        Assert.True(ComfyWorkflow.TryLoad(Path.Combine(_settings.ProfileComfyDirectory, "portrait" + SettingsMenu.ComfyImageSuffix + ".json"), out var image, out _));
        Assert.False(text!.TakesImage);
        Assert.True(image!.TakesImage);
        Assert.Equal(["portrait" + SettingsMenu.ComfyTextSuffix, "portrait" + SettingsMenu.ComfyImageSuffix], _settings.Current.ComfyWorkflowsOffered);
    }

    /// <summary>The summary's LoRAs page adds one from the server's list, with a strength typed; the saved graph carries it.</summary>
    [Fact]
    public async Task OnThePane_TheComfyWizard_AddsALora()
    {
        var (client, _) = ComfyPictureStub();
        var (menu, _, _) = PaneMenu(comfy: () => client);
        OpenComfyList();
        Push(Keys.Enter, Keys.Enter, Keys.Enter, Keys.Enter);   // + New workflow, build, text, model-a
        Push([.. Enumerable.Repeat(Keys.Backspace, 20)]);
        Type("portrait");
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // LoRAs (after the four actions: model, VAE, LoRAs)
        Push(Keys.Enter, Keys.Enter);                            // + Add LoRA, style
        Push(Keys.Enter);                                        // its strength
        Push(Keys.Backspace);
        Type("0.7");
        Push(Keys.Escape);                                       // back to the summary
        Push(Keys.Up, Keys.Up, Keys.Up, Keys.Up, Keys.Up, Keys.Up, Keys.Enter);   // Save, and offer it
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string text = File.ReadAllText(Path.Combine(_settings.ProfileComfyDirectory, "portrait.json"));
        var parts = ComfyGraphEdit.Read(ComfyGraphEdit.Load(text).Graph!);
        var lora = Assert.Single(parts.Loras.Loras);
        Assert.Equal("style.safetensors", lora.Name);
        Assert.Equal(0.7, lora.StrengthModel);
    }

    /// <summary>The import path: an exported graph placeholdered, its own values the defaults; global first when only the global folder holds workflows.</summary>
    [Fact]
    public async Task OnThePane_TheComfyWizard_ImportsAnExport_AndOffersIt()
    {
        ComfyWorkflowFile(_settings.GlobalComfyDirectory, "other");
        _settings.Update(d => d.ComfyWorkflowsOffered = ["other"]);
        string export = Path.Combine(_dir, "My Export.json");
        File.WriteAllText(export, ComfyTests.Export);
        var (menu, _, _) = PaneMenu();
        OpenComfyList();
        Push(Keys.Up, Keys.Enter);            // + New workflow
        Push(Keys.Down, Keys.Down, Keys.Enter);   // a file
        Type(export);
        Push([.. Enumerable.Repeat(Keys.Backspace, 64)]);
        Type("my-export");
        Push(Keys.Enter);                     // Save, and offer it
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string json = Path.Combine(_settings.GlobalComfyDirectory, "my-export.json");
        Assert.True(ComfyWorkflow.TryLoad(json, out var workflow, out string? problem), problem);
        Assert.Equal(new ComfyDefaults(832, 1216, 20, 6.5, "score_4, blurry"), workflow!.Defaults);
        Assert.Contains(SettingsMenu.ComfyFieldLabels[(int)SettingsMenu.ComfyField.Found], Output);
        Assert.Equal(["other", "my-export"], _settings.Current.ComfyWorkflowsOffered);
        Assert.Contains(SettingsMenu.ComfyWizardSaveHiddenRow[..40], Output);
    }

    /// <summary>Esc off the first page writes nothing; a name already in a comfy folder is refused and asked again.</summary>
    [Fact]
    public async Task OnThePane_TheComfyWizard_EscWritesNothing_AndATakenNameIsRefused()
    {
        ComfyWorkflowFile(_settings.GlobalComfyDirectory, "portrait");
        var (client, _) = ComfyPictureStub();
        var (menu, _, _) = PaneMenu(comfy: () => client);
        OpenComfyList();
        Push(Keys.Up, Keys.Enter);            // + New workflow
        Push(Keys.Escape);                    // off the first page: cancelled
        Push(Keys.Enter);                     // the wizard again
        Push(Keys.Enter, Keys.Enter, Keys.Enter);   // build, text → image, model-a
        Push([.. Enumerable.Repeat(Keys.Backspace, 20)]);
        Type("portrait");                     // refused, asked again
        Push(Keys.Escape, Keys.Escape, Keys.Escape, Keys.Escape);   // back out page by page
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.ComfyWizardCancelledNotice, Output);
        Assert.Contains(ComfyText.NameTaken("portrait", _settings.GlobalComfyDirectory), Output);
        Assert.Equal(["portrait.json"], Directory.GetFiles(_settings.GlobalComfyDirectory).Select(Path.GetFileName));
        Assert.False(Directory.Exists(_settings.ProfileComfyDirectory) && Directory.GetFiles(_settings.ProfileComfyDirectory).Length > 0);
    }

    /// <summary>Edit opens an installed workflow's summary: a number set writes only its key; the graph, untouched, is not rewritten.</summary>
    [Fact]
    public async Task OnThePane_TheComfyEdit_WritesOnlyWhatChanged()
    {
        ComfyBuilt(_settings.ProfileComfyDirectory, "portrait", "---\n# my note\nfamily: sdxl\nmystery: kept\n---\nTips stay.\n");
        string json = Path.Combine(_settings.ProfileComfyDirectory, "portrait.json");
        byte[] before = File.ReadAllBytes(json);
        var (client, _) = ComfyPictureStub();
        var (menu, _, _) = PaneMenu(comfy: () => client);
        OpenComfyList();
        Push(Keys.Enter, Keys.Enter);                        // portrait's actions, Edit
        Push([.. Enumerable.Repeat(Keys.Down, 11), Keys.Enter]);   // Steps (the three actions, six model rows, width, height)
        Type("30");
        Push([.. Enumerable.Repeat(Keys.Up, 11), Keys.Enter]);     // Save
        Push(Keys.Escape, Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("---\n# my note\nfamily: sdxl\nmystery: kept\nsteps: 30\n---\nTips stay.\n", File.ReadAllText(Path.Combine(_settings.ProfileComfyDirectory, "portrait.md")));
        Assert.Equal(before, File.ReadAllBytes(json));
        Assert.Contains(SettingsMenu.ComfyEditSavedNotice, Output);
        Assert.Matches("CLIP skip +" + SettingsMenu.ComfyWizardClipSkipRows[0], Output);   // none in the graph reads none, never blank
    }

    /// <summary>Edit's LoRAs page changes an installed LoRA's strength in place, both sides of it.</summary>
    [Fact]
    public async Task OnThePane_TheComfyEdit_ChangesALorasStrength()
    {
        ComfyBuilt(_settings.ProfileComfyDirectory, "portrait", edit: graph => ComfyGraphEdit.AddLora(graph, "style.safetensors", 1));
        var (client, _) = ComfyPictureStub();
        var (menu, _, _) = PaneMenu(comfy: () => client);
        OpenComfyList();
        Push(Keys.Enter, Keys.Enter);                                // actions, Edit
        Push([.. Enumerable.Repeat(Keys.Down, 5), Keys.Enter]);     // LoRAs
        Push(Keys.Down, Keys.Enter);                                 // the LoRA's strength
        Push(Keys.Backspace);
        Type("0.5");
        Push(Keys.Escape);
        Push([.. Enumerable.Repeat(Keys.Up, 5), Keys.Enter]);       // Save
        Push(Keys.Escape, Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var lora = Assert.Single(ComfyGraphEdit.Read(ComfyGraphEdit.Load(File.ReadAllText(Path.Combine(_settings.ProfileComfyDirectory, "portrait.json"))).Graph!).Loras.Loras);
        Assert.Equal(0.5, lora.StrengthModel);
        Assert.Equal(0.5, lora.StrengthClip);
    }

    /// <summary>Esc on the busy page cancels a test that hasn't answered; the run is told to stop.</summary>
    [Fact]
    public async Task OnThePane_TheComfyTest_EscCancelsARun()
    {
        ComfyBuilt(_settings.ProfileComfyDirectory, "portrait");
        var (client, _) = ComfyPictureStub(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });
        var (menu, _, settings) = PaneMenu(comfy: () => client);
        settings.ComfyBusyGrace = TimeSpan.Zero;
        OpenComfyList();
        Push(Keys.Enter, Keys.Down, Keys.Enter);   // actions, Test
        Push(Keys.Enter);                          // Run
        Push(Keys.Escape);                         // the busy page: cancel
        Push(Keys.Escape, Keys.Escape, Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.ComfyTestCancelledNotice, Output);
        Assert.Contains(SettingsMenu.ComfyBusyKeys, Output);
    }

    /// <summary>The check marks a workflow whose checkpoint the server doesn't list, and the band names it.</summary>
    [Fact]
    public async Task OnThePane_TheComfyList_Check_MarksAMissingModel()
    {
        ComfyBuilt(_settings.ProfileComfyDirectory, "portrait", edit: graph => graph["4"]!["inputs"]!["ckpt_name"] = "gone.safetensors");
        var (client, _) = ComfyPictureStub();
        var (menu, _, _) = PaneMenu(comfy: () => client);
        OpenComfyList();
        Push(Keys.Char('c'));
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.ComfyCheckFoundNotice(1), Output);
        Assert.Contains("[ ] ! portrait", Output);
        Assert.Contains("'gone.safetensors' is not on the server's list", Output);
    }

    /// <summary>The test connection row asks the server for its version; with no URL it says so.</summary>
    [Fact]
    public async Task OnThePane_TheComfyTestConnection_AsksTheServer()
    {
        var (client, stub) = ComfyPictureStub();
        var (menu, _, _) = PaneMenu(comfy: () => client);
        OpenImagesRow(2);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(stub.Requests, r => r.Uri.AbsolutePath == "/system_stats");
    }

    [Fact]
    public async Task OnThePane_TheComfyTestConnection_WithNoServer_SaysSo()
    {
        var (menu, _, _) = PaneMenu(comfy: () => null);
        OpenImagesRow(2);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("no ComfyUI server is set", Output);
    }

    /// <summary>The run source: the server's recent runs, the app's own behind a row; the picked one placeholdered, its model the suggested name.</summary>
    [Fact]
    public async Task OnThePane_TheComfyWizard_ImportsARecentRun()
    {
        string own = ComfyImportSourcesTests.Graph.Replace("\"filename_prefix\":\"ComfyUI\"", "\"filename_prefix\":\"neon/x\"", StringComparison.Ordinal).Replace("\"euler\"", "\"dpmpp_2m\"", StringComparison.Ordinal);
        var history = new System.Text.Json.Nodes.JsonObject
        {
            ["a"] = ComfyImportSourcesTests.Entry(1, ComfyImportSourcesTests.Graph, createTime: 1_000, output: "ComfyUI_00001_.png"),
            ["b"] = ComfyImportSourcesTests.Entry(2, own, createTime: 2_000, client: ComfyHistory.OwnClientPrefix + "x"),
        };
        var (client, stub) = ComfyPictureStub();
        stub.Map(ComfyServer + "/history?", System.Net.HttpStatusCode.OK, history.ToJsonString());
        var (menu, _, _) = PaneMenu(comfy: () => client);
        OpenComfyList();
        Push(Keys.Enter);                     // + New workflow
        Push(Keys.Down, Keys.Enter);          // a recent run
        Push(Keys.Enter);                     // the user's run (the app's own hidden)
        Push([.. Enumerable.Repeat(Keys.Backspace, 20)]);
        Type("lighthouse");
        Push(Keys.Enter);                     // Save, and offer it
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(stub.Requests, r => r.Uri.PathAndQuery == "/history?max_items=" + ComfyHistory.MaxItems);
        Assert.True(ComfyWorkflow.TryLoad(Path.Combine(_settings.ProfileComfyDirectory, "lighthouse.json"), out var workflow, out string? problem), problem);
        Assert.Equal(new ComfyDefaults(832, 1216, 24, 6.5, "blurry"), workflow!.Defaults);
        Assert.DoesNotContain("lighthouse at dusk", File.ReadAllText(workflow.FilePath));   // the run's own prompt is not kept
        Assert.Contains(SettingsMenu.ComfyOwnRunsRow(1, false), Output);
        Assert.Contains("ComfyUI_00001_.png", Output);                                      // the band
        Assert.Equal(["lighthouse"], _settings.Current.ComfyWorkflowsOffered);
    }

    /// <summary>The file source takes a PNG ComfyUI saved, by its bytes; one with only the UI graph says so and asks again.</summary>
    [Fact]
    public async Task OnThePane_TheComfyWizard_ImportsAPicture()
    {
        string ui = Path.Combine(_dir, "ui-only.png");
        File.WriteAllBytes(ui, ComfyImportSourcesTests.Png(ComfyImportSourcesTests.Chunk("tEXt", ComfyImportSourcesTests.Text("workflow", "{\"nodes\":[]}"))));
        string picture = Path.Combine(_dir, "ComfyUI_00012_.png");
        File.WriteAllBytes(picture, ComfyImportSourcesTests.Png(ComfyImportSourcesTests.Chunk("tEXt", ComfyImportSourcesTests.Text("prompt", ComfyImportSourcesTests.Graph))));
        var (menu, _, _) = PaneMenu();
        OpenComfyList();
        Push(Keys.Enter);                             // + New workflow
        Push(Keys.Down, Keys.Down, Keys.Enter);       // a file
        Type(ui);                                     // refused, asked again
        Push([.. Enumerable.Repeat(Keys.Backspace, ui.Length)]);
        Type(picture);
        Push(Keys.Enter);                             // the suggested name: the model's
        Push(Keys.Enter);                             // Save, and offer it
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(ComfyText.EmbeddedUiOnly[..30], Output);
        Assert.True(ComfyWorkflow.TryLoad(Path.Combine(_settings.ProfileComfyDirectory, "model.json"), out var workflow, out string? problem), problem);
        Assert.Contains("{{prompt}}", File.ReadAllText(workflow!.FilePath));
    }
}
