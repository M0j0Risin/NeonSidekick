using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Comfy;
using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/botchat</c>'s pictures, the pure half (2026-09-25): the workflow the app's picture uses, the image-prompt request and
/// its cleaning, the bots' image rule, the mode words, and the Botchat tab of <c>/settings</c>.
/// </summary>
public class BotChatPicturesTests
{
    private static ComfyWorkflow Workflow(string name, bool image = false, bool prompt = true, ComfyFamily family = ComfyFamily.Pony, string tips = "")
    {
        var placeholders = new HashSet<string>(StringComparer.Ordinal) { ComfyWorkflow.SeedKey };
        if (prompt)
        {
            placeholders.Add(ComfyWorkflow.PromptKey);
        }

        if (image)
        {
            placeholders.Add(ComfyWorkflow.ImageKey);
        }

        return new ComfyWorkflow(name, name + ".json", "{}", placeholders, Family: family, Tips: tips);
    }

    [Fact]
    public void Txt2ImgWorkflows_AreTheTextToImageOnes_AndImg2ImgTheOnePictureOnes_InOrder()
    {
        var offered = new[] { Workflow("edit", image: true), Workflow("upscale", prompt: false, image: true), Workflow("pony"), Workflow("flux", family: ComfyFamily.Flux), Workflow("restyle", image: true) };

        Assert.Equal(["pony", "flux"], BotChat.Txt2ImgWorkflows(offered).Select(w => w.Name));
        Assert.Equal(["edit", "restyle"], BotChat.Img2ImgWorkflows(offered).Select(w => w.Name));   // the promptless upscale is neither
    }

    /// <summary>2026-09-27 (the user's call): blank is none, no longer the first; a name of the wrong kind or no longer offered is none too.</summary>
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "  ", null)]
    [InlineData(" FLUX ", " EDIT ", "flux")]   // named, ignoring case and spaces
    [InlineData("edit", "pony", null)]         // each only of its own kind
    [InlineData("gone", "gone", null)]
    public void TheTwoWorkflows_AreTheNamedOnes_OrNone(string? txt2img, string? img2img, string? expected)
    {
        var offered = new[] { Workflow("edit", image: true), Workflow("pony"), Workflow("flux", family: ComfyFamily.Flux) };

        Assert.Equal(expected, BotChat.Txt2ImgWorkflow(offered, txt2img)?.Name);
        Assert.Equal(expected is null ? null : "edit", BotChat.Img2ImgWorkflow(offered, img2img)?.Name);
    }

    [Fact]
    public void BotWorkflows_AreTheTwo_TheReworkOneOnlyWithAPictureToRework()
    {
        var edit = Workflow("edit", image: true);
        var pony = Workflow("pony");
        var offered = new[] { edit, Workflow("flux", family: ComfyFamily.Flux), pony };

        Assert.Equal(["edit", "pony"], BotChat.BotWorkflows(offered, pony, edit, reworkable: true).Select(w => w.Name));
        Assert.Equal(["pony"], BotChat.BotWorkflows(offered, pony, edit, reworkable: false).Select(w => w.Name));
        Assert.Equal(["edit"], BotChat.BotWorkflows(offered, null, edit, reworkable: true).Select(w => w.Name));
        Assert.Empty(BotChat.BotWorkflows(offered, null, null, reworkable: true));
    }

    private static BotPicture Logged(int seq, string owner, bool drawn, params string[] paths) =>
        new(seq, owner, drawn, paths.Select(p => new Files.ImageAttachment(p, [], "image/png", 4, 4)).ToList());

    [Fact]
    public void ReworkCandidates_AreTheLatest_OrTheLastEight_NumberedOldestFirst()
    {
        var log = new List<BotPicture> { Logged(1, "default", false, "a.png"), Logged(2, "ada", true, "b-1.png", "b-2.png") };
        Assert.Empty(BotChat.ReworkCandidates([], BotImg2ImgMode.Latest));
        Assert.Equal([new ReworkPicture(1, "ada", true, "b-2.png")], BotChat.ReworkCandidates(log, BotImg2ImgMode.Latest));
        Assert.Equal([new ReworkPicture(1, "default", false, "a.png"), new ReworkPicture(2, "ada", true, "b-1.png"), new ReworkPicture(3, "ada", true, "b-2.png")],
            BotChat.ReworkCandidates(log, BotImg2ImgMode.ChatHistory));
        var many = Enumerable.Range(1, 10).Select(i => Logged(i, "ada", false, i + ".png")).ToList();
        var kept = BotChat.ReworkCandidates(many, BotImg2ImgMode.ChatHistory);
        Assert.Equal(BotChat.MaxReworkPictures, kept.Count);
        Assert.Equal(("3.png", 1), (kept[0].Path, kept[0].Number));
        Assert.Equal(("10.png", 8), (kept[^1].Path, kept[^1].Number));
    }

    private static readonly ReworkPicture[] Two = [new(1, "default", false, "a.png"), new(2, "ada", true, "b.png")];

    [Theory]
    [InlineData("a red fox", true, "a red fox", null)]              // no rework line: fresh
    [InlineData("a red fox", false, "a red fox", "b.png")]          // no txt2img: the latest is reworked
    [InlineData("REWORK 1\na red fox", true, "a red fox", "a.png")]
    [InlineData("**Rework #1:** a red fox", true, "a red fox", "a.png")]
    [InlineData("REWORK\na red fox", true, "a red fox", "b.png")]    // no number: the latest
    [InlineData("REWORK 9\na red fox", true, "a red fox", "b.png")]  // a number naming none: the latest
    [InlineData("```\nREWORK 2\n\"a red fox\"\n```", true, "a red fox", "b.png")]
    [InlineData("REWORKED foxes", true, "REWORKED foxes", null)]    // a word, not the line
    public void ParseImagePrompt_ReadsTheReworkLine(string text, bool fresh, string prompt, string? path)
    {
        var (read, rework) = BotChat.ParseImagePrompt(text, Two, fresh);

        Assert.Equal(prompt, read);
        Assert.Equal(path, rework?.Path);
    }

    [Fact]
    public void PictureInstruction_WithNothingToRework_IsTheOldOne()
    {
        var pony = Workflow("pony");
        Assert.Equal(BotChat.ImagePromptInstruction(pony), BotChat.PictureInstruction(false, pony, Workflow("edit", image: true), []));
        Assert.Equal(BotChat.PromisedPictureInstruction(pony), BotChat.PictureInstruction(true, pony, null, Two));
    }

    [Fact]
    public void PictureInstruction_OffersTheRework_WithItsStyle_AndTheNumberedList()
    {
        var pony = Workflow("pony");
        var edit = Workflow("edit", image: true, family: ComfyFamily.Flux, tips: "Keep the faces.");

        string both = BotChat.PictureInstruction(false, pony, edit, Two);
        Assert.Contains(ComfyFamilies.StyleGuide(ComfyFamily.Pony), both);
        Assert.Contains("You may instead rework one of the chat's pictures", both);
        Assert.Contains("\n1. the picture of default's reply\n2. ada's picture\n", both);
        Assert.Contains("put REWORK n (n the picture's number) alone on the first line", both);
        Assert.Contains(ComfyFamilies.StyleGuide(ComfyFamily.Flux), both);
        Assert.Contains("Tips for the rework workflow: Keep the faces.", both);
        Assert.EndsWith("Answer with the prompt alone, or the REWORK line and then the prompt: no preamble, no explanation, no quotes.", both);

        string latest = BotChat.PictureInstruction(false, null, edit, [Two[1]]);
        Assert.Contains("write a single prompt that reworks the chat's latest picture (ada's picture) so that it illustrates the line", latest);
        Assert.Contains("Put REWORK alone on the first line", latest);
        Assert.DoesNotContain("1. ", latest);

        string promised = BotChat.PictureInstruction(true, pony, edit, [Two[0]]);
        Assert.Contains($"answer exactly {BotChat.NoPictureAnswer}", promised);
        Assert.EndsWith($"the REWORK line and then the prompt, or {BotChat.NoPictureAnswer}: no preamble, no explanation, no quotes.", promised);
    }

    [Fact]
    public void ReworkCaption_NamesThePictures_TheirPaths_AndTheCall()
    {
        Assert.Equal("(You may rework the chat's latest picture, ada's picture, at \"b.png\": call generate_image with workflow \"edit\", its path as image, and a prompt for the reworked picture.)",
            BotChat.ReworkCaption("edit", [Two[1]]));
        Assert.Equal("(You may rework one of the chat's pictures — call generate_image with workflow \"edit\", its path as image, and a prompt for the reworked picture. Oldest first: the picture of default's reply, \"a.png\"; ada's picture, \"b.png\".)",
            BotChat.ReworkCaption("edit", Two));
    }

    [Theory]
    [InlineData("latest", BotImg2ImgMode.Latest)]
    [InlineData(" Chat-History ", BotImg2ImgMode.ChatHistory)]
    [InlineData("all", BotImg2ImgMode.Latest)]   // a hand-edited word: the default
    public void Img2ImgMode_ResolvesTheSavedWord(string saved, BotImg2ImgMode mode)
    {
        Assert.Equal(mode, BotChatImg2ImgMode.Resolve(new AppSettingsData { BotChatImg2ImgMode = saved }));
        Assert.Equal("latest", new AppSettingsData().BotChatImg2ImgMode);
        Assert.All(BotChatImg2ImgMode.Names, name => Assert.NotEmpty(BotChatImg2ImgMode.Describe(name)));
    }

    [Fact]
    public void ImagePromptInstruction_CarriesTheFamilysStyle_AndTheTips()
    {
        string flux = BotChat.ImagePromptInstruction(Workflow("flux", family: ComfyFamily.Flux, tips: "Always golden hour."));

        Assert.Contains(ComfyFamilies.StyleGuide(ComfyFamily.Flux), flux);
        Assert.Contains("Tips for this workflow: Always golden hour.", flux);
        Assert.EndsWith("Answer with the prompt alone: no preamble, no explanation, no quotes.", flux);
        Assert.DoesNotContain("Tips for this workflow", BotChat.ImagePromptInstruction(Workflow("pony")));
    }

    [Fact]
    public void ImagePromptSkills_IsTheLoadOnlyCatalog_ThenTheDirective()
    {
        var skill = new Skills.Skill("pony-prompts", "Writes Pony prompts.", Skills.SkillScope.Profile, "pony-prompts");
        string text = BotChat.ImagePromptSkills([skill]);

        Assert.StartsWith(Skills.SkillsPrompt.LoadOnlySection([skill]), text);
        Assert.EndsWith("\n\n" + BotChat.ImagePromptSkillsDirective, text);
        Assert.Contains("the chat's topic or the line asks", BotChat.ImagePromptSkillsDirective);
        Assert.Contains("answer exactly as instructed above", BotChat.ImagePromptSkillsDirective);
    }

    private static Skills.Skill SkillNamed(string name) => new(name, "About " + name + ".", Skills.SkillScope.Profile, name);

    [Theory]
    [InlineData(null, "", "")]
    [InlineData(new[] { "haiku" }, "", "haiku")]
    [InlineData(new[] { "gone", "HAIKU" }, "", "haiku")]                                   // a name not installed is dropped
    [InlineData(null, "Use the Pony-Prompts skill for pictures", "pony-prompts")]          // named in the topic, any case
    [InlineData(null, "draw like a pony", "")]                                           // pony is not pony-prompts
    [InlineData(null, "use pony-prompts-v2", "")]                                         // nor is a longer name
    [InlineData(new[] { "pony-prompts" }, "pony-prompts and haiku please", "haiku, pony-prompts")]   // both, once each, catalog order
    public void PreloadedSkills_AreTheSettingsAndTheTopicsNames(string[]? setting, string topic, string expected)
    {
        var catalog = new[] { SkillNamed("haiku"), SkillNamed("pony-prompts") };

        Assert.Equal(expected, string.Join(", ", BotChat.PreloadedSkills(catalog, setting, topic).Select(s => s.Name)));
    }

    [Fact]
    public void PreloadedSkillsSection_AndNotice_ArePinned()
    {
        Assert.Equal("", BotChat.PreloadedSkillsSection([]));
        Assert.Equal(BotChat.PreloadedSkillsLead + "\n\nA\n\nB", BotChat.PreloadedSkillsSection(["A", "B"]));
        Assert.Equal("(botchat: skills loaded for the picture prompts and the bots: a, b)", BotChat.PreloadedNotice(["a", "b"], BotSkillMode.PromptWriterAndBots));
        Assert.Equal("(botchat: skills loaded for the picture prompts: a)", BotChat.PreloadedNotice(["a"], BotSkillMode.PromptWriterOnly));
        // The bots' prompt: the section after the load-only block, before the rules; none, as before.
        string prompt = BotChat.SystemPrompt(null, "ada", ["max"], "", false, null, false, preloaded: "PRELOADED");
        Assert.True(prompt.IndexOf("PRELOADED", StringComparison.Ordinal) < prompt.IndexOf("You are ada", StringComparison.Ordinal));
        Assert.Equal(BotChat.SystemPrompt(null, "ada", ["max"], "", false, null, false), BotChat.SystemPrompt(null, "ada", ["max"], "", false, null, false, preloaded: ""));
    }

    [Theory]
    [InlineData("prompt-writer-only", BotSkillMode.PromptWriterOnly)]
    [InlineData(" Prompt-Writer-And-Bots ", BotSkillMode.PromptWriterAndBots)]
    [InlineData("prompt-writer", BotSkillMode.PromptWriterAndBots)]   // a hand-edited word: the default
    public void SkillMode_ResolvesTheSavedWord(string saved, BotSkillMode mode)
    {
        Assert.Equal(mode, BotChatSkillMode.Resolve(new AppSettingsData { BotChatSkillMode = saved }));
        Assert.Equal("prompt-writer-and-bots", new AppSettingsData().BotChatSkillMode);
        Assert.All(BotChatSkillMode.Names, name => Assert.NotEmpty(BotChatSkillMode.Describe(name)));
    }

    [Fact]
    public void ImagePromptRequest_NamesTheSpeaker_AndTheTopicWhenThereIsOne()
    {
        Assert.Equal("ada's line (the chat's topic: pizza):\n\nPineapple belongs.", BotChat.ImagePromptRequest("ada", "Pineapple belongs.", "pizza"));
        Assert.Equal("ada's line:\n\nPineapple belongs.", BotChat.ImagePromptRequest("ada", "Pineapple belongs.", ""));
    }

    [Theory]
    [InlineData("  a red fox, snow  ", "a red fox, snow")]
    [InlineData("\"a red fox, snow\"", "a red fox, snow")]
    [InlineData("'a red fox'", "a red fox")]
    [InlineData("Prompt: a red fox", "a red fox")]
    [InlineData("```\na red fox, snow\n```", "a red fox, snow")]
    [InlineData("```text\n\"a red fox\"\n```", "a red fox")]
    [InlineData("```", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void CleanImagePrompt_TakesOffTheWrapping(string? text, string expected)
    {
        Assert.Equal(expected, BotChat.CleanImagePrompt(text));
    }

    [Fact]
    public void Rules_CloseWithTheImageRule_OnlyWhenTheBotsAreOfferedTheTool()
    {
        Assert.EndsWith(" " + BotChat.ImageRule, BotChat.Rules("ada", ["max"], "pizza", images: true));
        Assert.DoesNotContain(BotChat.ImageRule, BotChat.Rules("ada", ["max"], "pizza"));
        Assert.Contains("generate_image", BotChat.ImageRule);
        Assert.Contains(BotChat.ImageRule, BotChat.SystemPrompt(null, "ada", ["max"], "", false, null, false, images: true));
    }

    [Theory]
    [InlineData("automatic", BotImageMode.Automatic, true, false)]
    [InlineData("Autonomous", BotImageMode.Autonomous, false, true)]
    [InlineData("sometimes", BotImageMode.Automatic, true, false)]   // a hand-edited word: the default
    public void ImageMode_ResolvesTheSavedWord(string saved, BotImageMode mode, bool draws, bool offers)
    {
        var resolved = BotChatImageMode.Resolve(new AppSettingsData { BotChatImageMode = saved });

        Assert.Equal(mode, resolved);
        Assert.Equal(draws, BotChatImageMode.Draws(resolved));
        Assert.Equal(offers, BotChatImageMode.Offers(resolved));
        Assert.All(BotChatImageMode.Names, name => Assert.NotEmpty(BotChatImageMode.Describe(name)));
    }

    [Theory]
    [InlineData("single", BotLlmMode.Single)]
    [InlineData(" Multi ", BotLlmMode.Multi)]
    [InlineData("both", BotLlmMode.Single)]   // a hand-edited word: the default
    public void LlmMode_ResolvesTheSavedWord(string saved, BotLlmMode mode)
    {
        Assert.Equal(mode, BotChatLlmMode.Resolve(new AppSettingsData { BotChatLlmMode = saved }));
        Assert.Equal("single", new AppSettingsData().BotChatLlmMode);
        Assert.All(BotChatLlmMode.Names, name => Assert.NotEmpty(BotChatLlmMode.Describe(name)));
    }

    [Fact]
    public void TheBotchatTab_IsLast_ItsTwelveRowsDefaultingToSingle_Off_Automatic_NoWorkflows_Latest_Async_AFiveSecondPause_NoSkills_NonePreloaded_ToBoth_AndNoVision()
    {
        var data = new AppSettingsData();

        Assert.Equal("Botchat", SettingsMenu.TabTitles[(int)SettingsTab.BotChat]);
        Assert.Equal((int)SettingsTab.BotChat, SettingsMenu.TabTitles.Count - 1);   // last again since later on 2026-09-27 (the Claude tab moved to /tools)
        Assert.Equal([SettingsField.BotChatLlmMode, SettingsField.BotChatImages, SettingsField.BotChatImageMode, SettingsField.BotChatTxt2ImgWorkflow, SettingsField.BotChatImg2ImgWorkflow, SettingsField.BotChatImg2ImgMode, SettingsField.BotChatImageAsync, SettingsField.BotChatNonTtsDelaySeconds, SettingsField.BotChatSkills, SettingsField.BotChatPreloadedSkills, SettingsField.BotChatSkillMode, SettingsField.BotChatVision], SettingsMenu.TabFields[(int)SettingsTab.BotChat]);
        Assert.Equal(["Botchat LLM mode", "Botchat images enabled", "Botchat image mode", "Botchat txt2img workflow", "Botchat img2img workflow", "Botchat img2img mode", "Botchat image async", "Botchat non-TTS delay", "Botchat skills enabled", "Botchat preloaded skills", "Botchat skill mode", "Botchat vision enabled"], SettingsMenu.TabFields[(int)SettingsTab.BotChat].Select(SettingsMenu.FieldName));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatLlmMode));
        Assert.Equal("single", SettingsMenu.FieldValue(SettingsField.BotChatLlmMode, data, "."));
        Assert.Equal("multi", SettingsMenu.FieldValue(SettingsField.BotChatLlmMode, new AppSettingsData { BotChatLlmMode = "multi" }, "."));
        Assert.True(SettingsMenu.IsToggle(SettingsField.BotChatImages));
        Assert.True(SettingsMenu.IsToggle(SettingsField.BotChatImageAsync));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatImageMode));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatTxt2ImgWorkflow));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatImg2ImgWorkflow));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatImg2ImgMode));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.BotChatImages, data, "."));
        Assert.Equal("automatic", SettingsMenu.FieldValue(SettingsField.BotChatImageMode, data, "."));
        Assert.Equal(SettingsMenu.NoBotChatWorkflowLabel, SettingsMenu.FieldValue(SettingsField.BotChatTxt2ImgWorkflow, data, "."));
        Assert.Equal(SettingsMenu.NoBotChatWorkflowLabel, SettingsMenu.FieldValue(SettingsField.BotChatImg2ImgWorkflow, data, "."));
        Assert.Equal("flux", SettingsMenu.FieldValue(SettingsField.BotChatTxt2ImgWorkflow, new AppSettingsData { BotChatTxt2ImgWorkflow = "flux" }, "."));
        Assert.Equal("edit", SettingsMenu.FieldValue(SettingsField.BotChatImg2ImgWorkflow, new AppSettingsData { BotChatImg2ImgWorkflow = "edit" }, "."));
        Assert.Equal("latest", SettingsMenu.FieldValue(SettingsField.BotChatImg2ImgMode, data, "."));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.BotChatImageAsync, data, "."));
        // The pause with no voice (2026-09-26): typed seconds, 5 by default, 0 off.
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatNonTtsDelaySeconds));
        Assert.Equal("5 seconds", SettingsMenu.FieldValue(SettingsField.BotChatNonTtsDelaySeconds, data, "."));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.BotChatNonTtsDelaySeconds, new AppSettingsData { BotChatNonTtsDelaySeconds = 0 }, "."));
        Assert.Equal("1 second", SettingsMenu.SecondsLabel(1));
        Assert.Equal("5", SettingsMenu.EditableValue(SettingsField.BotChatNonTtsDelaySeconds, data));
        Assert.Equal("must be 0 (off) or 1 to 30 seconds", SettingsMenu.BotChatNonTtsDelayRangeError);
        // The skills (2026-09-27): a toggle, off by default.
        Assert.True(SettingsMenu.IsToggle(SettingsField.BotChatSkills));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.BotChatSkills, data, "."));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.BotChatSkills, new AppSettingsData { BotChatSkills = true }, "."));
        // Preloaded skills and where they go (2026-09-27): a checklist, none by default; a picker, both by default.
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatPreloadedSkills));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatSkillMode));
        Assert.Equal(SettingsMenu.NoBotChatWorkflowLabel, SettingsMenu.FieldValue(SettingsField.BotChatPreloadedSkills, data, "."));
        Assert.Equal("haiku, pony-prompts", SettingsMenu.FieldValue(SettingsField.BotChatPreloadedSkills, new AppSettingsData { BotChatPreloadedSkills = ["haiku", " pony-prompts ", ""] }, "."));
        Assert.Equal("prompt-writer-and-bots", SettingsMenu.FieldValue(SettingsField.BotChatSkillMode, data, "."));
        // Vision (2026-09-27): a toggle, off by default.
        Assert.True(SettingsMenu.IsToggle(SettingsField.BotChatVision));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.BotChatVision, data, "."));
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(0, null)]
    [InlineData(-3, null)]
    [InlineData(90, 30)]
    public void BotPause_IsTheSavedSeconds_ClampedToTheRange_NullAtZero(int saved, int? seconds) =>
        Assert.Equal(seconds is { } s ? TimeSpan.FromSeconds(s) : null, ChatScreen.BotPause(new AppSettingsData { BotChatNonTtsDelaySeconds = saved }));

    [Fact]
    public void ReplyText_JoinsAHeldTurnsTextDeltas_AndNothingElse()
    {
        TurnEvent[] events =
        [
            new TurnEvent.ToolCall("generate_image", "g1", "{}"),
            new TurnEvent.ToolResult("generate_image", "g1", "generated 1 picture"),
            new TurnEvent.TextDelta("  Here is "),
            new TurnEvent.TextDelta("my dog. "),
        ];

        Assert.Equal("Here is my dog.", BotChat.ReplyText(events));
        Assert.Equal("", BotChat.ReplyText([]));
    }

    [Theory]
    [InlineData("Here's a quick sketch of the harbour I made.", true)]
    [InlineData("I drew us a dragon!", true)]
    [InlineData("Let me paint you a PICTURE of it.", true)]
    [InlineData("Check out this pic.", true)]
    [InlineData("Imagine the illustration on the cover.", true)]
    [InlineData("Here's an image I generated for you.", true)]
    [InlineData("Took a selfie at the beach!", true)]
    [InlineData("I made some art for you.", true)]
    [InlineData("A little cartoon of us both.", true)]
    [InlineData("Read this article first.", false)]    // "art" is a whole word only
    [InlineData("Let's start again.", false)]
    [InlineData("I think pineapple belongs on pizza.", false)]
    [InlineData("The withdrawal was slow.", false)]      // a stem only at a word's start
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MentionsPicture_IsTheLooseSieve(string? reply, bool expected)
    {
        Assert.Equal(expected, BotChat.MentionsPicture(reply));
    }

    [Fact]
    public void PictureAttempt_TellsATriedCall_FromOneThatDrew()
    {
        var drew = new List<ChatMessage>
        {
            new(ChatRole.User, "hi"),
            new(ChatRole.Assistant, [new FunctionCallContent("g1", "generate_image")]),
            new(ChatRole.Tool, [new FunctionResultContent("g1", "generated 1 picture with pony")]),
        };
        var failed = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("g1", "generate_image")]),
            new(ChatRole.Tool, [new FunctionResultContent("g1", ComfyText.NoPrompt)]),
        };
        var other = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "get_time")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "noon")]),
            new(ChatRole.Assistant, "I drew a dog."),
        };

        Assert.Equal((true, true), BotChat.PictureAttempt(drew));
        Assert.Equal((true, false), BotChat.PictureAttempt(failed));
        Assert.Equal((false, false), BotChat.PictureAttempt(other));
        Assert.Equal((false, false), BotChat.PictureAttempt([]));
    }

    [Fact]
    public void PromisedPictureInstruction_CarriesTheStyle_TheTips_AndTheNoPictureAnswer()
    {
        string flux = BotChat.PromisedPictureInstruction(Workflow("flux", family: ComfyFamily.Flux, tips: "Always golden hour."));

        Assert.Contains(ComfyFamilies.StyleGuide(ComfyFamily.Flux), flux);
        Assert.Contains("Tips for this workflow: Always golden hour.", flux);
        Assert.Contains("answer exactly " + BotChat.NoPictureAnswer, flux);
        Assert.Contains("image, photo", flux);
        Assert.DoesNotContain("Tips for this workflow", BotChat.PromisedPictureInstruction(Workflow("pony")));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("NONE", true)]
    [InlineData("none.", true)]
    [InlineData("a dog surfing", false)]
    [InlineData("none of the dogs are wet", false)]
    public void IsNoPicture_IsEmptyOrNone(string prompt, bool expected)
    {
        Assert.Equal(expected, BotChat.IsNoPicture(prompt));
    }

    [Fact]
    public void ImageRule_TellsTheBotsToDrawWhatTheySayTheyDraw()
    {
        Assert.Contains("call generate_image in that same reply", BotChat.ImageRule);
        Assert.Contains("image, photo, drawing, painting, sketch, illustration", BotChat.ImageRule);
        Assert.Contains("never write the call out as text", BotChat.ImageRule);
    }
}
