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
        _settings.Update(d => { d.BotChatImages = true; d.BotChatImageMode = mode; d.BotChatImageAsync = async; });
        return stub;
    }

    private static IList<AITool> ToolsOf(ChatOptions? options) => options?.Tools ?? [];

    private string ComfyImages => Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName, "comfy_images");

    /// <summary>
    /// The app's picture is drawn at <c>Image thumbnail size</c> (later on 2026-09-25, the user's report: it filled the window
    /// whatever the setting said): a wide picture fills the size's columns, one half-block row at 4 px tall; <c>fullsize</c>
    /// is the window's 238 of 240.
    /// </summary>
    [Theory]
    [InlineData("tiny", 32)]
    [InlineData("medium", 64)]
    [InlineData("fullsize", 238)]
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
        var pony = BotChat.ImageWorkflow([.. new ComfyWorkflowCatalog(() => [_settings.ProfileComfyDirectory]).Workflows], null)!;
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

    private const string SketchReply = "Here's a sketch I drew of a dog surfing.";

    private ComfyWorkflow PonyWorkflow => BotChat.ImageWorkflow([.. new ComfyWorkflowCatalog(() => [_settings.ProfileComfyDirectory]).Workflows], null)!;

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
