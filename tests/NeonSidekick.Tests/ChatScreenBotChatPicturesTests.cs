using System.Net;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Comfy;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/botchat</c>'s pictures on the screen (2026-09-25): off, the chat is talk alone; <c>automatic</c>, the model writes an
/// image prompt from each reply and the app draws it — under the reply, or with <c>Botchat image async</c> on while the next
/// bot answers; <c>autonomous</c>, the bots are offered <c>generate_image</c> alone; ESC under a picture skips it alone.
/// </summary>
public partial class ChatScreenTests
{
    private const string DogReply = "I once saw a dog surf.";

    /// <summary>The two-bot fixture on a stub ComfyUI with one text → image workflow (pony), pictures on in <paramref name="mode"/>.</summary>
    private StubHttpMessageHandler BotPicturesFixture(string mode = "automatic", bool async = false, int width = 4, int height = 4)
    {
        BotChatFixture();
        var stub = ComfyServer(width, height);
        // The workflow named (2026-09-27): blank is none since, no longer the first.
        _settings.Update(d => { d.BotChatImages = true; d.BotChatImageMode = mode; d.BotChatImageAsync = async; d.BotChatTxt2ImgWorkflow = "pony"; });
        return stub;
    }

    private static IList<AITool> ToolsOf(ChatOptions? options) => options?.Tools ?? [];

    private string ComfyImages => Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName, "comfy_images");

    /// <summary>
    /// The app's picture is drawn at <c>Image thumbnail size</c> (later on 2026-09-25, the user's report: it filled the window
    /// whatever the setting said): a wide picture fills the size's columns, one half-block row at 4 px tall; <c>fullsize</c>
    /// is the window's 238 of 240, less the strip's two-cell margin since 2026-10-03.
    /// </summary>
    [Theory]
    [InlineData("tiny", 32)]
    [InlineData("medium", 64)]
    [InlineData("fullsize", 236)]
    public async Task BotChat_TheAppsPicture_IsDrawnAtTheThumbnailSize(string size, int columns)
    {
        BotPicturesFixture(width: 512, height: 4);
        _settings.Update(d => d.ImageThumbnailSize = size);
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(new string('▀', columns), output);
        Assert.DoesNotContain(new string('▀', columns + 1), output);
    }

    /// <summary>
    /// Show image thumbnails off (2026-09-28, the user's report: botchat drew its pictures whatever the toggle said): the
    /// transcript gets the picture's result line alone; the picture is still made and gathered in the strip.
    /// </summary>
    [Fact]
    public async Task BotChat_ThumbnailsOff_TheAppsPictureIsNotDrawn()
    {
        var stub = BotPicturesFixture(width: 512, height: 4);
        _settings.Update(d => { d.ImageThumbnailSize = "tiny"; d.ShowImageThumbnails = false; });
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(stub.Requests, r => r.Uri.AbsolutePath == "/view");
        Assert.Contains(ComfyText.TextToImageGlyph, output);
        Assert.DoesNotContain(new string('▀', 32), output);
    }

    private static int ImagesIn(ChatMessage message) => message.Contents.OfType<DataContent>().Count();

    /// <summary>
    /// Botchat vision enabled (2026-09-27, the user's ask): the next bot's turn message carries the app's picture of the reply
    /// before it, captioned; the first bot's next turn carries its own reply's picture and ada's, and no request's earlier
    /// messages carry any.
    /// </summary>
    [Fact]
    public async Task BotChat_Vision_TheNextBotSeesThePictureOfTheReplyBefore_AndEachBotWhatItMissed()
    {
        BotPicturesFixture();
        _settings.Update(d => d.BotChatVision = true);
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("a dog surfing a wave");
        _chat.EnqueueText("Ada answers.");
        _chat.EnqueueText("a cat on a boat");
        _chat.EnqueueText("Neon ", "again");
        EscDuringRequest(5);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(5, _chat.Requests.Count);
        Assert.Equal(0, ImagesIn(_chat.Requests[0][^1]));
        var ada = _chat.Requests[2];
        Assert.Contains(AdaMarker, SystemText(ada));
        Assert.Equal(1, ImagesIn(ada[^1]));
        Assert.EndsWith("the picture of default's reply.)", ada[^1].Text, StringComparison.Ordinal);
        var neon = _chat.Requests[4];
        Assert.Equal(2, ImagesIn(neon[^1]));
        Assert.EndsWith("the picture of your reply, the picture of ada's reply.)", neon[^1].Text, StringComparison.Ordinal);
        Assert.All(neon.Take(neon.Count - 1), m => Assert.Equal(0, ImagesIn(m)));
        // The image-prompt requests see no picture.
        Assert.Equal(0, ImagesIn(_chat.Requests[3][^1]));
    }

    [Fact]
    public async Task BotChat_VisionOff_TheDefault_NoRequestCarriesAPicture()
    {
        BotPicturesFixture();
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);
        Assert.All(_chat.Requests.SelectMany(r => r), m => Assert.Equal(0, ImagesIn(m)));
        Assert.DoesNotContain("(Attached, oldest first", _chat.Requests[2][^1].Text, StringComparison.Ordinal);
    }

    /// <summary>Autonomous, vision on (2026-09-27): a bot's own generate_image picture reaches the other bot next turn, not itself again.</summary>
    [Fact]
    public async Task BotChat_Vision_Autonomous_ABotsOwnPicture_ReachesTheOther_NotItself()
    {
        BotPicturesFixture(mode: "autonomous");
        _settings.Update(d => d.BotChatVision = true);
        _chat.Enqueue(FakeChatClient.Call("g1", GenerateImageTool.ToolName, new Dictionary<string, object?> { ["prompt"] = "a dog surfing", ["seed"] = 7, ["verbatim"] = true }));
        _chat.EnqueueText("Here is my dog.");
        _chat.EnqueueText("Nice dog.");
        _chat.EnqueueText("Neon ", "again");
        EscDuringRequest(4);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(4, _chat.Requests.Count);
        Assert.Equal(1, ImagesIn(_chat.Requests[2][^1]));
        Assert.EndsWith("default's picture.)", _chat.Requests[2][^1].Text, StringComparison.Ordinal);
        Assert.Equal(0, ImagesIn(_chat.Requests[3][^1]));
    }

    [Fact]
    public async Task BotChat_PicturesOff_OffersNoTool_AndAsksForNoPicture()
    {
        BotChatFixture();
        var stub = ComfyServer();
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(2);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.All(_chat.Options, options => Assert.Empty(ToolsOf(options)));
        Assert.DoesNotContain(BotChat.ImageRule, SystemText(_chat.Requests[0]));
        Assert.DoesNotContain(stub.Requests, r => r.Uri.AbsolutePath == "/prompt");
    }

    [Fact]
    public async Task BotChat_Automatic_Sync_WritesAPromptFromTheHeldReply_AndDrawsThePictureAboveIt()
    {
        var stub = BotPicturesFixture();
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("```\n\"a dog surfing a wave, sunny\"\n```");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        // The second request is the image prompt's: the family's style and the reply, no tools; the bots get none either.
        Assert.Equal(3, _chat.Requests.Count);
        var pony = BotChat.Txt2ImgWorkflow([.. new ComfyWorkflowCatalog(() => [_settings.ProfileComfyDirectory]).Workflows], "pony")!;
        Assert.Equal(BotChat.ImagePromptInstruction(pony), SystemText(_chat.Requests[1]));
        Assert.Equal(BotChat.ImagePromptRequest("default", DogReply, ""), _chat.Requests[1][^1].Text);
        Assert.All(_chat.Options, options => Assert.Empty(ToolsOf(options)));
        Assert.Contains(AdaMarker, SystemText(_chat.Requests[2]));

        // The cleaned prompt reaches ComfyUI; the reply was held (later on 2026-09-25): the name, then the picture, then the words.
        Assert.Contains("\"text\":\"a dog surfing a wave, sunny", stub.Requests.Single(r => r.Uri.AbsolutePath == "/prompt").Body!);
        int name = output.IndexOf(TranscriptRenderer.SpeakerGlyph + "default\n", StringComparison.Ordinal);
        int picture = output.IndexOf(ComfyText.TextToImageGlyph + "generated 1 picture with pony", StringComparison.Ordinal);
        int words = output.IndexOf("● " + DogReply, StringComparison.Ordinal);
        Assert.True(name >= 0 && picture > name && words > picture, $"name {name}, picture {picture}, words {words}");
        Assert.Contains(BotChat.ThinkingSpinner("default"), output);
        Assert.DoesNotContain(BotChat.PictureNotice("default"), output);
        // ESC over ada's unseen reply stops the chat.
        Assert.Contains("  · " + ChatScreen.CancelledNotice + "\n", output);
        Assert.Contains(BotChat.StoppedNotice(1), output);
    }

    [Fact]
    public async Task BotChat_Automatic_Async_DrawsThePictureLater_UnderWhoseItIs()
    {
        var stub = BotPicturesFixture(async: true);
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        _chat.EnqueueText("   ");                     // ada's image prompt: nothing, so no picture
        _chat.EnqueueText("Neon ", "again");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (_chat.Requests.Count == 3 && i == 0)
            {
                // Ada answers while the picture renders; the reply waits until it is saved, so it is drawn after it.
                for (int tries = 0; tries < 500 && !(Directory.Exists(ComfyImages) && Directory.EnumerateFiles(ComfyImages).Any()); tries++)
                {
                    await Task.Delay(10, CancellationToken.None);
                }

                await Task.Delay(100, CancellationToken.None);
            }

            if (_chat.Requests.Count == 5 && i == 1)
            {
                // Twice: the first cuts neon short, the second ends the chat at ada's turn (the ESC ladder, 2026-09-25).
                _console.Input.PushKey(Keys.Escape);
                _console.Input.PushKey(Keys.Escape);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(5, _chat.Requests.Count);
        Assert.Single(stub.Requests, r => r.Uri.AbsolutePath == "/prompt");
        int label = output.IndexOf(BotChat.PictureNotice("default"), StringComparison.Ordinal);
        Assert.True(label >= 0);
        Assert.True(output.IndexOf(ComfyText.TextToImageGlyph + "generated 1 picture with pony", label, StringComparison.Ordinal) > label);
        Assert.Contains(BotChat.NoPromptNotice, output);
    }

    /// <summary>
    /// Botchat image async (2026-09-27, the user's ask: nothing said a generation ran while the next bot answered): the strip
    /// shows the picture's glyph from its send until it is made, and not after the chat stops.
    /// </summary>
    [Fact]
    public async Task BotChat_Async_ThePictureRendering_IsOnTheStrip_UntilItIsMade()
    {
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        BotPicturesFixture(async: true);
        // The picture's download held until ada's reply has been seen with the glyph on the strip.
        var view = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stub = new StubHttpMessageHandler()
            .Map("http://comfy.lan:8188/prompt", HttpStatusCode.OK, "{\"prompt_id\":\"p-1\"}")
            .Map("http://comfy.lan:8188/history/", HttpStatusCode.OK, "{\"p-1\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"x.png\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}")
            .Map("http://comfy.lan:8188/view", async (_, ct) =>
            {
                await view.Task.WaitAsync(ct);
                return StubHttpMessageHandler.Bytes(HttpStatusCode.OK, SmokeChecks.SolidBmp(4, 4), "image/png");
            })
            .Map("http://comfy.lan:8188/system_stats", HttpStatusCode.OK, "{\"system\":{\"comfyui_version\":\"0.3.40\"},\"devices\":[]}");
        _comfyClient = url => new NeonSidekick.Comfy.ComfyClient(url, new HttpClient(stub), TimeSpan.FromMilliseconds(1));
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        string rule = new(ScreenPane.RuleGlyph, 240);
        string? rendering = null;
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (_chat.Requests.Count == 3 && i == 0)
            {
                // Ada answers while the picture renders: the tick's redraw carries the strip.
                for (int tries = 0; tries < 500 && !stub.Requests.Any(r => r.Uri.AbsolutePath == "/view"); tries++)
                {
                    await Task.Delay(10, CancellationToken.None);
                }

                _time.Advance(ScreenPane.Tick);
                rendering = Output[(Output.LastIndexOf(rule + "\n", StringComparison.Ordinal) + rule.Length + 1)..];
                view.SetResult();
                _console.Input.PushKey(Keys.Escape);
                _console.Input.PushKey(Keys.Escape);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.NotNull(rendering);
        Assert.Contains(ComfyText.TextToImageLabel, rendering);
        string lastIdle = output[(output.LastIndexOf(rule + "\n", StringComparison.Ordinal) + rule.Length + 1)..];
        Assert.DoesNotContain(ComfyText.TextToImageLabel, lastIdle);
    }

    /// <summary>
    /// A double-click on the strip's 🖼️ while a picture renders (2026-09-28, the user's ask): the picture is cancelled — its
    /// prompt deleted from ComfyUI's queue — the chat goes on, and the notice says so; nothing opens /settings.
    /// </summary>
    [Fact]
    public async Task BotChat_Async_ADoubleClickOnTheRenderingGlyph_CancelsThePicture_AndTheChatGoesOn()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the hint row at 102
        BotPicturesFixture(async: true);
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stub = HeldComfyServer(polled);
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        bool drained = false;
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (_chat.Requests.Count == 3 && i == 0)
            {
                // Ada answers while the picture renders: the strip's glyph double-clicked on the busy row.
                await polled.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                _time.Advance(ScreenPane.Tick);
                Scripted().PushClick(0, 102);         // 🖼️ at 0–1, the strip's only glyph
                Scripted().PushClick(1, 102);
                for (int tries = 0; tries < 500 && !drained; tries++)
                {
                    await Task.Delay(10, CancellationToken.None);
                    drained = stub.Requests.ToArray().Any(r => r.Uri.AbsolutePath == "/queue");
                }

                Scripted().Push(Keys.Escape, Keys.Escape);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        PushLine(Scripted(), "/botchat");
        PushLine(Scripted(), "/exit");

        string output = await RunAsync();

        Assert.True(drained, "requests " + _chat.Requests.Count + "\n" + output);
        Assert.Contains(ComfyText.Drained(1), output);
        Assert.DoesNotContain(SettingsMenu.Title + "   General", output);   // the glyph's, never the row's /settings
    }

    [Fact]
    public async Task BotChat_Autonomous_OffersTheBotsGenerateImageAlone_AndTheAppDrawsNothing()
    {
        var stub = BotPicturesFixture(mode: "autonomous");
        _chat.Enqueue(FakeChatClient.Call("g1", GenerateImageTool.ToolName, new Dictionary<string, object?> { ["prompt"] = "a dog surfing", ["seed"] = 7, ["verbatim"] = true }));
        _chat.EnqueueText("Here is my dog.");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        // Neon's turn is two round trips (the call, then its words); no image-prompt request follows, ada speaks next.
        Assert.Equal(3, _chat.Requests.Count);
        Assert.Equal([GenerateImageTool.ToolName], ToolsOf(_chat.Options[0]).Select(t => t.Name));
        Assert.Equal([GenerateImageTool.ToolName], ToolsOf(_chat.Options[2]).Select(t => t.Name));
        Assert.Contains(BotChat.ImageRule, SystemText(_chat.Requests[0]));
        Assert.Contains(AdaMarker, SystemText(_chat.Requests[2]));
        Assert.Single(stub.Requests, r => r.Uri.AbsolutePath == "/prompt");
        Assert.Contains("generated 1 picture with pony (seed 7", output);
    }

    [Fact]
    public async Task BotChat_EscUnderThePicture_SkipsItAlone_AndTheChatGoesOn()
    {
        var stub = BotPicturesFixture();
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("a dog ", "surfing");
        _chat.EnqueueText("Ada ", "answers.");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (_chat.Requests.Count is 2 or 3 && i == 1)
            {
                _console.Input.PushKey(Keys.Escape);
                if (_chat.Requests.Count == 3)
                {
                    // Ada's reply: cut short, then the chat's end (the ESC ladder, 2026-09-25).
                    _console.Input.PushKey(Keys.Escape);
                }

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        // The first ESC cancels the image prompt alone: the held reply still shows, and ada speaks next; the second ends the chat.
        Assert.Equal(3, _chat.Requests.Count);
        Assert.Contains(AdaMarker, SystemText(_chat.Requests[2]));
        int skipped = output.IndexOf(ComfyText.Cancelled, StringComparison.Ordinal);
        Assert.True(skipped >= 0 && output.IndexOf("● " + DogReply, StringComparison.Ordinal) > skipped);
        Assert.DoesNotContain(stub.Requests, r => r.Uri.AbsolutePath == "/prompt");
    }

    [Fact]
    public async Task BotChat_Sync_EscOverTheUnseenReply_CutsTheBotShort_TwiceStopsTheChat_WithNoPicture()
    {
        var stub = BotPicturesFixture();
        _chat.EnqueueText("I once saw ", "a dog surf.");
        EscDuringRequest(1);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        // The first ESC cuts neon short over its unseen reply, the second ends the chat as ada's turn opens (2026-09-25).
        Assert.Single(_chat.Requests);
        Assert.Contains("  · " + BotChat.CutShortNotice("default") + "\n", output);
        Assert.Contains("  · " + ChatScreen.CancelledNotice + "\n", output);
        Assert.Contains(BotChat.StoppedNotice(0), output);
        Assert.DoesNotContain("● I once saw", output);
        Assert.DoesNotContain(stub.Requests, r => r.Uri.AbsolutePath == "/prompt");
    }

    /// <summary>An image → image workflow beside the fixture's pony (2026-09-27): a prompt and one picture in.</summary>
    private void Img2ImgWorkflow(StubHttpMessageHandler stub, string name = "hatter")
    {
        File.WriteAllText(Path.Combine(_settings.ProfileComfyDirectory, name + ".json"),
            "{\"3\":{\"class_type\":\"KSampler\",\"inputs\":{\"seed\":\"{{seed}}\"}},\"6\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"{{prompt}}\"}},\"10\":{\"class_type\":\"LoadImage\",\"inputs\":{\"image\":\"{{image}}\"}}}");
        stub.Map("http://comfy.lan:8188/upload/image", HttpStatusCode.OK, "{\"name\":\"uploaded-input.png\",\"subfolder\":\"\",\"type\":\"input\"}");
    }

    /// <summary>
    /// Botchat img2img workflow (2026-09-27, the user's ask): the first picture is fresh, its prompt writer offered no rework;
    /// the next reply's writer is offered the latest picture and answers REWORK, so that picture is uploaded and reworked.
    /// </summary>
    [Fact]
    public async Task BotChat_Automatic_TheNextPicture_MayReworkTheLatest()
    {
        var stub = BotPicturesFixture();
        Img2ImgWorkflow(stub);
        _settings.Update(d => d.BotChatImg2ImgWorkflow = "hatter");
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        _chat.EnqueueText("REWORK\nthe same dog, now in a top hat");
        _chat.EnqueueText("Neon ", "again");
        EscDuringRequest(5);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(5, _chat.Requests.Count);
        Assert.Equal(BotChat.ImagePromptInstruction(PonyWorkflow), SystemText(_chat.Requests[1]));   // nothing to rework yet
        Assert.Contains("You may instead rework the chat's latest picture (the picture of default's reply)", SystemText(_chat.Requests[3]));
        var prompts = stub.Requests.Where(r => r.Uri.AbsolutePath == "/prompt").ToList();
        Assert.Equal(2, prompts.Count);
        Assert.Contains("\"text\":\"a dog surfing a wave", prompts[0].Body!);
        Assert.Contains("\"text\":\"the same dog, now in a top hat", prompts[1].Body!);
        Assert.Contains("uploaded-input.png", prompts[1].Body!);   // the first picture went up as the input
        Assert.Single(stub.Requests, r => r.Uri.AbsolutePath == "/upload/image");
    }

    /// <summary>Botchat txt2img workflow blank (2026-09-27: none, no longer the first): no picture, and the notice once.</summary>
    [Fact]
    public async Task BotChat_NoTxt2ImgWorkflow_DrawsNothing_AndSaysSoOnce()
    {
        var stub = BotPicturesFixture();
        _settings.Update(d => d.BotChatTxt2ImgWorkflow = null);
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);   // no image-prompt request at all
        Assert.DoesNotContain(stub.Requests, r => r.Uri.AbsolutePath == "/prompt");
        Assert.Equal(1, CountOf(output, BotChat.NoWorkflowNotice));
    }

    /// <summary>
    /// Autonomous with both workflows (2026-09-27, the user's ask: the bots limited as automatic is): generate_image lists the
    /// txt2img workflow alone until there is a picture, then the img2img one too, and the next bot's turn names the picture's path.
    /// </summary>
    [Fact]
    public async Task BotChat_Autonomous_TheBotsSeeTheTwoWorkflowsAlone_AndThePictureToRework()
    {
        var stub = BotPicturesFixture(mode: "autonomous");
        Img2ImgWorkflow(stub);
        File.WriteAllText(Path.Combine(_settings.ProfileComfyDirectory, "other-t2i.json"), File.ReadAllText(Path.Combine(_settings.ProfileComfyDirectory, "pony.json")));
        _settings.Update(d => d.BotChatImg2ImgWorkflow = "hatter");
        _chat.Enqueue(FakeChatClient.Call("g1", GenerateImageTool.ToolName, new Dictionary<string, object?> { ["prompt"] = "a dog surfing", ["seed"] = 7, ["verbatim"] = true }));
        _chat.EnqueueText("Here is my dog.");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);
        string first = Assert.Single(ToolsOf(_chat.Options[0])).Description!;
        Assert.Contains("pony", first);
        Assert.DoesNotContain("hatter", first);     // nothing to rework yet
        Assert.DoesNotContain("other-t2i", first);   // never a workflow the settings do not name
        string next = Assert.Single(ToolsOf(_chat.Options[2])).Description!;
        Assert.Contains("hatter", next);
        Assert.DoesNotContain("other-t2i", next);
        Assert.EndsWith(BotChat.ReworkCaption("hatter", [new ReworkPicture(1, "default", true, @"comfy_images\pony-7.png")]), _chat.Requests[2][^1].Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Botchat skills enabled in automatic (2026-09-27, the user's report: the first picture was prompted before any skill could
    /// be loaded): the image-prompt writer sees the catalog and load_skill, loads the one the topic asks for, and its prompt follows.
    /// </summary>
    [Fact]
    public async Task BotChat_Automatic_ThePromptWriter_LoadsTheSkillFirst()
    {
        var stub = BotPicturesFixture();
        _settings.Update(d => d.BotChatSkills = true);
        PutSkill(ProfileSkills, "pony-prompts", "Writes Pony Diffusion prompts.", "# Pony prompts\n\nAlways start with score_9.");
        _chat.EnqueueText(DogReply);
        _chat.Enqueue(FakeChatClient.Call("s1", LoadSkillTool.ToolName, new Dictionary<string, object?> { ["name"] = "pony-prompts" }));
        _chat.EnqueueText("score_9, a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(4);
        PushLine("/botchat use the pony prompts skill for pictures");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(4, _chat.Requests.Count);
        Assert.Equal([LoadSkillTool.ToolName], ToolsOf(_chat.Options[1]).Cast<AIFunction>().Select(t => t.Name));
        string system = SystemText(_chat.Requests[1]);
        Assert.StartsWith(BotChat.ImagePromptInstruction(PonyWorkflow), system);
        Assert.Contains("<name>pony-prompts</name>", system);
        Assert.EndsWith(BotChat.ImagePromptSkillsDirective, system);
        Assert.Contains("use the pony prompts skill for pictures", _chat.Requests[1][^1].Text);   // the topic
        Assert.Contains(_chat.Requests[2].SelectMany(m => m.Contents).OfType<FunctionResultContent>(), r => (r.Result?.ToString() ?? "").Contains("Always start with score_9.", StringComparison.Ordinal));
        Assert.Contains("\"text\":\"score_9, a dog surfing a wave", stub.Requests.Single(r => r.Uri.AbsolutePath == "/prompt").Body!);
        Assert.Contains("pony-prompts", output);   // the skill line, as a bot's own load shows
    }

    /// <summary>
    /// The writer's round trips are capped (2026-09-27): three load_skill calls, then a request with no tool, whose text is the
    /// prompt. Each for another file (2026-09-30, code review): a call repeating one already answered ends the rounds sooner.
    /// </summary>
    [Fact]
    public async Task BotChat_Automatic_ThePromptWriter_IsAskedForTheAnswer_AfterThreeLoads()
    {
        var stub = BotPicturesFixture();
        _settings.Update(d => d.BotChatSkills = true);
        PutSkill(ProfileSkills, "pony-prompts", "Writes Pony Diffusion prompts.");
        _chat.EnqueueText(DogReply);
        for (int i = 0; i < 3; i++)
        {
            _chat.Enqueue(FakeChatClient.Call("s" + i, LoadSkillTool.ToolName, new Dictionary<string, object?> { ["name"] = "pony-prompts", ["file"] = "part" + i + ".md" }));
        }

        _chat.EnqueueText("a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(6);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(6, _chat.Requests.Count);
        Assert.All([1, 2, 3], i => Assert.Single(ToolsOf(_chat.Options[i])));
        Assert.Empty(ToolsOf(_chat.Options[4]));
        Assert.Contains("\"text\":\"a dog surfing a wave", stub.Requests.Single(r => r.Uri.AbsolutePath == "/prompt").Body!);
    }

    /// <summary>Skills off (the default): the image-prompt request is as before — no tool, no catalog.</summary>
    [Fact]
    public async Task BotChat_Automatic_SkillsOff_ThePromptWriterGetsNoTool()
    {
        BotPicturesFixture();
        PutSkill(ProfileSkills, "pony-prompts", "Writes Pony Diffusion prompts.");
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.Empty(ToolsOf(_chat.Options[1]));
        Assert.Equal(BotChat.ImagePromptInstruction(PonyWorkflow), SystemText(_chat.Requests[1]));
    }

    /// <summary>
    /// The botchat workflows are any installed ones (later on 2026-09-27, the user's call): with ComfyUI workflows offered ticking
    /// none of them, automatic still draws with pony and reworks with the img2img one.
    /// </summary>
    [Fact]
    public async Task BotChat_Automatic_UsesItsWorkflows_EvenWhenNotOffered()
    {
        var stub = BotPicturesFixture();
        Img2ImgWorkflow(stub);
        _settings.Update(d => { d.BotChatImg2ImgWorkflow = "hatter"; d.ComfyWorkflowsOffered = ["something-else"]; });
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        _chat.EnqueueText("REWORK\nthe same dog, now in a top hat");
        _chat.EnqueueText("Neon ", "again");
        EscDuringRequest(5);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        var prompts = stub.Requests.Where(r => r.Uri.AbsolutePath == "/prompt").ToList();
        Assert.Equal(2, prompts.Count);
        Assert.Contains("\"text\":\"a dog surfing a wave", prompts[0].Body!);
        Assert.Contains("uploaded-input.png", prompts[1].Body!);
        Assert.DoesNotContain(BotChat.NoWorkflowNotice, output);
    }

    /// <summary>Autonomous: the bots' tool lists the two workflows though neither is offered (later on 2026-09-27).</summary>
    [Fact]
    public async Task BotChat_Autonomous_TheBotsToolListsItsWorkflows_EvenWhenNotOffered()
    {
        var stub = BotPicturesFixture(mode: "autonomous");
        Img2ImgWorkflow(stub);
        _settings.Update(d => { d.BotChatImg2ImgWorkflow = "hatter"; d.ComfyWorkflowsOffered = ["something-else"]; });
        _chat.Enqueue(FakeChatClient.Call("g1", GenerateImageTool.ToolName, new Dictionary<string, object?> { ["prompt"] = "a dog surfing", ["seed"] = 7, ["verbatim"] = true }));
        _chat.EnqueueText("Here is my dog.");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.Contains("\n- pony ", Assert.Single(ToolsOf(_chat.Options[0])).Description!);
        Assert.Single(stub.Requests, r => r.Uri.AbsolutePath == "/prompt");   // the bot's own call ran
        Assert.Contains("\n- hatter ", Assert.Single(ToolsOf(_chat.Options[2])).Description!);
    }

    /// <summary>
    /// Botchat limited skills (2026-10-04, the user's ask): with Botchat skills enabled off, the ticked skills alone are listed and
    /// loadable — load_skill's enum names them alone — for the bots and the picture prompt writer alike; nothing is pasted in.
    /// </summary>
    [Fact]
    public async Task BotChat_LimitedSkills_TheTickedSkillsAlone_AreOfferedToTheBotsAndTheWriter()
    {
        BotPicturesFixture();
        _settings.Update(d => d.BotChatLimitedSkills = ["pony-prompts", "gone"]);
        PutSkill(ProfileSkills, "pony-prompts", "Writes Pony Diffusion prompts.", "# Pony prompts\n\nAlways start with score_9.");
        PutSkill(ProfileSkills, "haiku");
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("score_9, a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.All([0, 1, 2], i =>
        {
            string system = SystemText(_chat.Requests[i]);   // neon, the prompt writer, ada
            Assert.Contains("<name>pony-prompts</name>", system);
            Assert.DoesNotContain("<name>haiku</name>", system);
            Assert.DoesNotContain("Always start with score_9.", system);
            var tool = Assert.Single(ToolsOf(_chat.Options[i]).Cast<AIFunction>());
            Assert.Equal(LoadSkillTool.ToolName, tool.Name);
            string names = tool.JsonSchema.GetProperty("properties").GetProperty("name").GetProperty("enum").GetRawText();
            Assert.Contains("\"pony-prompts\"", names);
            Assert.DoesNotContain("\"haiku\"", names);
        });
    }

    /// <summary>
    /// None ticked and Botchat skills enabled off (the defaults): no skill tool or list anywhere, and a topic naming a skill pastes
    /// nothing (until 2026-10-04 it was preloaded).
    /// </summary>
    [Fact]
    public async Task BotChat_NoLimitedSkills_NoSkillTool_AndATopicNamingOne_PastesNothing()
    {
        BotPicturesFixture();
        PutSkill(ProfileSkills, "pony-prompts", "Writes Pony Diffusion prompts.", "# Pony prompts\n\nAlways start with score_9.");
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("score_9, a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat use pony-prompts for the pictures");
        PushLine("/exit");

        await RunAsync();

        Assert.All([0, 1, 2], i =>
        {
            Assert.Empty(ToolsOf(_chat.Options[i]));
            Assert.DoesNotContain(Skills.SkillsPrompt.CatalogOpen, SystemText(_chat.Requests[i]));
            Assert.DoesNotContain("Always start with score_9.", SystemText(_chat.Requests[i]));
        });
    }

    /// <summary>Botchat skills enabled on: every skill, the limited list not read.</summary>
    [Fact]
    public async Task BotChat_SkillsEnabled_TheLimitedListIsNotRead()
    {
        BotPicturesFixture();
        _settings.Update(d => { d.BotChatSkills = true; d.BotChatLimitedSkills = ["pony-prompts"]; });
        PutSkill(ProfileSkills, "pony-prompts", "Writes Pony Diffusion prompts.");
        PutSkill(ProfileSkills, "haiku");
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("score_9, a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.All([0, 1], i =>
        {
            Assert.Contains("<name>pony-prompts</name>", SystemText(_chat.Requests[i]));
            Assert.Contains("<name>haiku</name>", SystemText(_chat.Requests[i]));
        });
    }

    /// <summary>
    /// The writer's last request, which goes without tools (2026-09-30): <c>&lt;tool_call&gt;</c> markup written there is taken
    /// out — not the ComfyUI prompt, and not run — and the words after it are the prompt. A round that only repeats a call already
    /// answered (2026-09-30, code review: four requests, the skill sent each time) goes straight to that last request.
    /// </summary>
    [Fact]
    public async Task BotChat_SkillsEnabled_ARepeatedLoad_EndsTheToolRounds_AndMarkupInTheLastIsTakenOut()
    {
        var stub = BotPicturesFixture();
        _settings.Update(d => d.BotChatSkills = true);
        PutSkill(ProfileSkills, "pony-prompts", "Writes Pony Diffusion prompts.", "# Pony prompts\n\nAlways start with score_9.");
        const string Markup = "<tool_call>\n<function=load_skill>\n<parameter=name>\npony-prompts\n</parameter>\n</function>\n</tool_call>";
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText(Markup);
        _chat.EnqueueText(Markup);
        _chat.EnqueueText(Markup + "\nscore_9, a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(5);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(5, _chat.Requests.Count);
        Assert.Equal([LoadSkillTool.ToolName], ToolsOf(_chat.Options[2]).Select(t => t.Name));
        Assert.Empty(ToolsOf(_chat.Options[3]));
        Assert.Single(_chat.Requests[3].SelectMany(m => m.Contents).OfType<FunctionResultContent>());   // the repeat was not run
        Assert.Contains("\"text\":\"score_9, a dog surfing a wave\"", stub.Requests.Single(r => r.Uri.AbsolutePath == "/prompt").Body!);
    }

    /// <summary>
    /// Words beside a written load (2026-09-30, code review): never the prompt — they are as often a lead-in to the call as a
    /// prompt — so the writer is asked again, the skill's content in front of it. The transcript's line is the short one.
    /// </summary>
    [Fact]
    public async Task BotChat_SkillsEnabled_WordsBesideAWrittenLoad_AreNotThePrompt()
    {
        var stub = BotPicturesFixture();
        _settings.Update(d => d.BotChatSkills = true);
        PutSkill(ProfileSkills, "pony-prompts", "Writes Pony Diffusion prompts.", "# Pony prompts\n\nAlways start with score_9.");
        PutSkill(ProfileSkills, "haiku");
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("I'll load the pony-prompts skill first.\n<tool_call>\n<function=load_skill>\n<parameter=name>\npony-prompts\n</parameter>\n</function>\n</tool_call>");
        _chat.EnqueueText("score_9, a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(4);
        PushLine("/botchat use pony-prompts for the pictures");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(4, _chat.Requests.Count);
        string body = stub.Requests.Single(r => r.Uri.AbsolutePath == "/prompt").Body!;
        Assert.Contains("\"text\":\"score_9, a dog surfing a wave\"", body);
        Assert.DoesNotContain("I'll load", body);
        Assert.Contains("loaded skill 'pony-prompts'", output);
    }

    /// <summary>
    /// A load_skill line written by a writer offered no tool (2026-09-30, code review: Botchat skills enabled off): taken out of the
    /// prompt, the line form too — caught only with load_skill's parameter names.
    /// </summary>
    [Fact]
    public async Task BotChat_NoSkillTool_AWrittenLoadSkillLine_IsTakenOutOfThePrompt()
    {
        var stub = BotPicturesFixture();
        PutSkill(ProfileSkills, "pony-prompts", "Writes Pony Diffusion prompts.", "# Pony prompts\n\nAlways start with score_9.");
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("load_skill name: pony-prompts file: references/tags.md\nscore_9, a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat use pony-prompts for the pictures");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Empty(ToolsOf(_chat.Options[1]));
        Assert.Contains("\"text\":\"score_9, a dog surfing a wave\"", stub.Requests.Single(r => r.Uri.AbsolutePath == "/prompt").Body!);
    }

    /// <summary>
    /// The writer's load_skill written as markup in a round that offers the tool (2026-09-30, code review: a server that did not
    /// convert it): the call runs as a real one and the writer is asked again, where before the empty rest was the prompt.
    /// </summary>
    [Fact]
    public async Task BotChat_Automatic_TheWritersCallWrittenAsText_Runs_AndThePromptFollows()
    {
        var stub = BotPicturesFixture();
        _settings.Update(d => d.BotChatSkills = true);
        PutSkill(ProfileSkills, "pony-prompts", "Writes Pony Diffusion prompts.", "# Pony prompts\n\nAlways start with score_9.");
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("<tool_call>\n<function=load_skill>\n<parameter=name>\npony-prompts\n</parameter>\n</function>\n</tool_call>");
        _chat.EnqueueText("score_9, a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(4);
        PushLine("/botchat use the pony prompts skill for pictures");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(4, _chat.Requests.Count);
        var second = _chat.Requests[2];
        Assert.Contains(second.SelectMany(m => m.Contents).OfType<FunctionCallContent>(), c => c.Name == LoadSkillTool.ToolName);
        Assert.Contains(second.SelectMany(m => m.Contents).OfType<FunctionResultContent>(), r => (r.Result?.ToString() ?? "").Contains("Always start with score_9.", StringComparison.Ordinal));
        Assert.DoesNotContain(second.Where(m => m.Role == ChatRole.Assistant), m => m.Text.Contains("<tool_call>", StringComparison.Ordinal));
        Assert.Contains("\"text\":\"score_9, a dog surfing a wave", stub.Requests.Single(r => r.Uri.AbsolutePath == "/prompt").Body!);
        Assert.DoesNotContain(BotChat.NoPromptNotice, output);
    }

    /// <summary>
    /// A lead-in before a load_skill line in the writer's only round (2026-09-30, code review: with no tool to run it, the lead-in
    /// was the ComfyUI prompt): no words after the call, so no prompt and no picture.
    /// </summary>
    [Fact]
    public async Task BotChat_NoSkillTool_ALeadInBeforeAWrittenCall_IsNotThePrompt()
    {
        var stub = BotPicturesFixture();
        PutSkill(ProfileSkills, "pony-prompts", "Writes Pony Diffusion prompts.", "# Pony prompts\n\nAlways start with score_9.");
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("Let me check the tag list first.\nload_skill name: pony-prompts file: references/tags.md");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat use pony-prompts for the pictures");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(BotChat.NoPromptNotice, output);
        Assert.DoesNotContain(stub.Requests, r => r.Uri.AbsolutePath == "/prompt");
    }

    /// <summary>
    /// A written load_skill whose arguments do not parse (2026-09-30, code review: nothing ran, and the lead-in beside it was the
    /// prompt): a call answered the parse error, and the writer asked again.
    /// </summary>
    [Fact]
    public async Task BotChat_SkillsEnabled_AWrittenCallThatDoesNotParse_IsAnsweredTheError_AndTheWriterAskedAgain()
    {
        var stub = BotPicturesFixture();
        _settings.Update(d => d.BotChatSkills = true);
        PutSkill(ProfileSkills, "pony-prompts", "Writes Pony Diffusion prompts.", "# Pony prompts\n\nAlways start with score_9.");
        PutSkill(ProfileSkills, "haiku");
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText("I'll load the pony-prompts skill first.\nload_skill(\"pony-prompts\")");
        _chat.EnqueueText("score_9, a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(4);
        PushLine("/botchat use pony-prompts for the pictures");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(4, _chat.Requests.Count);
        var result = Assert.Single(_chat.Requests[2].SelectMany(m => m.Contents).OfType<FunctionResultContent>());
        Assert.Contains("could not be parsed", result.Result?.ToString(), StringComparison.Ordinal);
        string body = stub.Requests.Single(r => r.Uri.AbsolutePath == "/prompt").Body!;
        Assert.Contains("\"text\":\"score_9, a dog surfing a wave\"", body);
        Assert.DoesNotContain("I'll load", body);
    }

    /// <summary>
    /// A generate_image call the writer writes (2026-09-30, code review: a skill that says to call it; the call's text, or nothing,
    /// was the ComfyUI prompt): its prompt is the prompt — in the only round, and in a round that offers load_skill, at once.
    /// </summary>
    [Theory]
    [InlineData(false, "Here is the updated prompt:\ngenerate_image(prompt=\"score_9, a dog surfing a wave\")")]
    [InlineData(true, "Here is the updated prompt:\n<tool_call>\n<function=generate_image>\n<parameter=prompt>\nscore_9, a dog surfing a wave\n</parameter>\n</function>\n</tool_call>")]
    public async Task BotChat_TheWritersGenerateImageCall_ItsPromptIsThePrompt(bool skills, string answer)
    {
        var stub = BotPicturesFixture();
        _settings.Update(d => d.BotChatSkills = skills);
        PutSkill(ProfileSkills, "pony-prompts", "Writes Pony Diffusion prompts.", "# Pony prompts\n\nAlways start with score_9.");
        PutSkill(ProfileSkills, "haiku");
        _chat.EnqueueText(DogReply);
        _chat.EnqueueText(answer);
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat use pony-prompts for the pictures");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Equal(skills ? new[] { LoadSkillTool.ToolName } : [], ToolsOf(_chat.Options[1]).Select(t => t.Name));
        string body = stub.Requests.Single(r => r.Uri.AbsolutePath == "/prompt").Body!;
        Assert.Contains("\"text\":\"score_9, a dog surfing a wave\"", body);
        Assert.DoesNotContain("updated prompt", body);
    }

    /// <summary>
    /// A bot offered no load_skill writes one (2026-09-30, code review: the line was shown, spoken and stored): taken out of its
    /// line, never run.
    /// </summary>
    [Fact]
    public async Task BotChat_ABotOfferedNoLoadSkill_ItsWrittenLoadSkill_IsTakenOutOfItsLine()
    {
        BotPicturesFixture();
        PutSkill(ProfileSkills, "pony-prompts", "Writes Pony Diffusion prompts.", "# Pony prompts\n\nRead references/tags.md with load_skill.");
        _chat.EnqueueText("load_skill name: pony-prompts file: references/tags.md\n", DogReply);
        _chat.EnqueueText("score_9, a dog surfing a wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat use pony-prompts for the pictures");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Empty(ToolsOf(_chat.Options[0]));
        Assert.Contains(DogReply, output);
        Assert.DoesNotContain("load_skill name:", output);
        Assert.DoesNotContain("load_skill name:", string.Concat(_chat.Requests[1].Select(m => m.Text)));   // nor in what the writer reads
    }

    private const string SketchReply = "Here's a sketch I drew of a dog surfing.";

    private ComfyWorkflow PonyWorkflow => BotChat.Txt2ImgWorkflow([.. new ComfyWorkflowCatalog(() => [_settings.ProfileComfyDirectory]).Workflows], "pony")!;

    [Fact]
    public async Task BotChat_Autonomous_APictureTheBotOnlyTalkedAbout_IsDrawnUnderTheReply()
    {
        var stub = BotPicturesFixture(mode: "autonomous");
        _chat.EnqueueText(SketchReply);
        _chat.EnqueueText("a dog surfing a big wave");     // the promised picture's prompt
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        // Neon's words, then the promised-picture request (no tools), then ada; one picture, under the words.
        Assert.Equal(3, _chat.Requests.Count);
        Assert.Equal(BotChat.PromisedPictureInstruction(PonyWorkflow), SystemText(_chat.Requests[1]));
        Assert.Equal(BotChat.ImagePromptRequest("default", SketchReply, ""), _chat.Requests[1][^1].Text);
        Assert.Empty(ToolsOf(_chat.Options[1]));
        Assert.Contains(AdaMarker, SystemText(_chat.Requests[2]));
        Assert.Contains("\"text\":\"a dog surfing a big wave", stub.Requests.Single(r => r.Uri.AbsolutePath == "/prompt").Body!);
        int words = output.IndexOf(SketchReply, StringComparison.Ordinal);
        int picture = output.IndexOf(ComfyText.TextToImageGlyph + "generated 1 picture with pony", StringComparison.Ordinal);
        Assert.True(words >= 0 && picture > words, $"words {words}, picture {picture}");
        Assert.DoesNotContain(BotChat.PictureNotice("default"), output);
    }

    [Fact]
    public async Task BotChat_Autonomous_WhenTheModelFindsNoPicturePromised_NothingIsDrawn_Quietly()
    {
        var stub = BotPicturesFixture(mode: "autonomous");
        _chat.EnqueueText("What a picture you paint with words.");
        _chat.EnqueueText("NONE");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Contains(AdaMarker, SystemText(_chat.Requests[2]));
        Assert.DoesNotContain(stub.Requests, r => r.Uri.AbsolutePath == "/prompt");
        Assert.DoesNotContain(BotChat.NoPromptNotice, output);
    }

    [Fact]
    public async Task BotChat_Autonomous_ABotThatDrewItsPicture_IsNotDrawnForAgain()
    {
        var stub = BotPicturesFixture(mode: "autonomous");
        _chat.Enqueue(FakeChatClient.Call("g1", GenerateImageTool.ToolName, new Dictionary<string, object?> { ["prompt"] = "a dog surfing", ["seed"] = 7, ["verbatim"] = true }));
        _chat.EnqueueText("Here is the picture I drew.");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        // The call and the words, then ada at once: no promised-picture request.
        Assert.Equal(3, _chat.Requests.Count);
        Assert.Contains(AdaMarker, SystemText(_chat.Requests[2]));
        Assert.Single(stub.Requests, r => r.Uri.AbsolutePath == "/prompt");
    }

    [Fact]
    public async Task BotChat_Autonomous_ACallWithNoPrompt_DrewNothing_SoThePromisedPictureIsMade()
    {
        var stub = BotPicturesFixture(mode: "autonomous");
        _chat.Enqueue(FakeChatClient.Call("g1", GenerateImageTool.ToolName, new Dictionary<string, object?>()));
        _chat.EnqueueText("Oops, there it is anyway.");      // no picture word: the failed call is intent enough
        _chat.EnqueueText("a dog surfing a big wave");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(4);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        // The failed call and the words, the promised-picture request, then ada.
        Assert.Equal(4, _chat.Requests.Count);
        Assert.Equal(BotChat.PromisedPictureInstruction(PonyWorkflow), SystemText(_chat.Requests[2]));
        Assert.Contains(AdaMarker, SystemText(_chat.Requests[3]));
        Assert.Contains("\"text\":\"a dog surfing a big wave", stub.Requests.Single(r => r.Uri.AbsolutePath == "/prompt").Body!);
    }

    [Fact]
    public async Task BotChat_Autonomous_ACallWrittenAsText_Runs_AndIsNeverShown()
    {
        var stub = BotPicturesFixture(mode: "autonomous");
        _chat.EnqueueText("Here you go! generate_image(prompt=\"a dog ", "surfing\", seed=7, verbatim=true)");
        _chat.EnqueueText("Hope you like it.");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        // The written call ran as a real one: its picture, the bot's words after it, then ada — no promised-picture request.
        Assert.Equal(3, _chat.Requests.Count);
        Assert.Contains(AdaMarker, SystemText(_chat.Requests[2]));
        Assert.Contains("\"text\":\"a dog surfing", stub.Requests.Single(r => r.Uri.AbsolutePath == "/prompt").Body!);
        Assert.Contains("generated 1 picture with pony (seed 7", output);
        Assert.Contains("Here you go!", output);
        Assert.DoesNotContain("prompt=\"a dog", output);
    }

    [Fact]
    public async Task BotChat_Autonomous_Async_APromisedPictureIsDrawnLater_UnderWhoseItIs()
    {
        var stub = BotPicturesFixture(mode: "autonomous", async: true);
        _chat.EnqueueText(SketchReply);
        _chat.EnqueueText("a dog surfing a big wave");
        _chat.EnqueueText("Ada ", "answers.");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (_chat.Requests.Count == 3 && i == 0)
            {
                // Ada answers while the picture renders; wait until it is saved so it is drawn when the chat ends.
                for (int tries = 0; tries < 500 && !(Directory.Exists(ComfyImages) && Directory.EnumerateFiles(ComfyImages).Any()); tries++)
                {
                    await Task.Delay(10, CancellationToken.None);
                }

                await Task.Delay(100, CancellationToken.None);
            }

            if (_chat.Requests.Count == 3 && i == 1)
            {
                // Twice: ada cut short, then the chat's end (the ESC ladder, 2026-09-25).
                _console.Input.PushKey(Keys.Escape);
                _console.Input.PushKey(Keys.Escape);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Single(stub.Requests, r => r.Uri.AbsolutePath == "/prompt");
        int label = output.IndexOf(BotChat.PictureNotice("default"), StringComparison.Ordinal);
        Assert.True(label >= 0);
        Assert.True(output.IndexOf(ComfyText.TextToImageGlyph + "generated 1 picture with pony", label, StringComparison.Ordinal) > label);
    }
}
