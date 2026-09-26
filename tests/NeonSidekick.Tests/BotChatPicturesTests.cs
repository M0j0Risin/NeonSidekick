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
    public void ImageWorkflows_AreTheTextToImageOnes_InOrder()
    {
        var offered = new[] { Workflow("edit", image: true), Workflow("upscale", prompt: false), Workflow("pony"), Workflow("flux", family: ComfyFamily.Flux) };

        Assert.Equal(["pony", "flux"], BotChat.ImageWorkflows(offered).Select(w => w.Name));
    }

    [Theory]
    [InlineData(null, "pony")]         // blank: the first text → image one
    [InlineData("", "pony")]
    [InlineData(" FLUX ", "flux")]     // named, ignoring case and spaces
    [InlineData("edit", "pony")]       // an image → image workflow is never the pick
    [InlineData("gone", "pony")]       // a name no longer offered: the first
    public void ImageWorkflow_IsTheNamedOne_OrTheFirst(string? setting, string expected)
    {
        var offered = new[] { Workflow("edit", image: true), Workflow("pony"), Workflow("flux", family: ComfyFamily.Flux) };

        Assert.Equal(expected, BotChat.ImageWorkflow(offered, setting)!.Name);
    }

    [Fact]
    public void ImageWorkflow_IsNull_WithNoTextToImageWorkflow()
    {
        Assert.Null(BotChat.ImageWorkflow([Workflow("edit", image: true)], null));
        Assert.Null(BotChat.ImageWorkflow([], "pony"));
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
    public void TheBotchatTab_IsLast_ItsFiveRowsDefaultingToSingle_Off_Automatic_TheFirstWorkflow_AndAsync()
    {
        var data = new AppSettingsData();

        Assert.Equal("Botchat", SettingsMenu.TabTitles[(int)SettingsTab.BotChat]);
        Assert.Equal((int)SettingsTab.BotChat, SettingsMenu.TabTitles.Count - 1);
        Assert.Equal([SettingsField.BotChatLlmMode, SettingsField.BotChatImages, SettingsField.BotChatImageMode, SettingsField.BotChatImageWorkflow, SettingsField.BotChatImageAsync], SettingsMenu.TabFields[(int)SettingsTab.BotChat]);
        Assert.Equal(["Botchat LLM mode", "Botchat images enabled", "Botchat image mode", "Botchat image workflow", "Botchat image async"], SettingsMenu.TabFields[(int)SettingsTab.BotChat].Select(SettingsMenu.FieldName));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatLlmMode));
        Assert.Equal("single", SettingsMenu.FieldValue(SettingsField.BotChatLlmMode, data, "."));
        Assert.Equal("multi", SettingsMenu.FieldValue(SettingsField.BotChatLlmMode, new AppSettingsData { BotChatLlmMode = "multi" }, "."));
        Assert.True(SettingsMenu.IsToggle(SettingsField.BotChatImages));
        Assert.True(SettingsMenu.IsToggle(SettingsField.BotChatImageAsync));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatImageMode));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatImageWorkflow));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.BotChatImages, data, "."));
        Assert.Equal("automatic", SettingsMenu.FieldValue(SettingsField.BotChatImageMode, data, "."));
        Assert.Equal(SettingsMenu.FirstBotChatWorkflowLabel, SettingsMenu.FieldValue(SettingsField.BotChatImageWorkflow, data, "."));
        Assert.Equal("flux", SettingsMenu.FieldValue(SettingsField.BotChatImageWorkflow, new AppSettingsData { BotChatImageWorkflow = "flux" }, "."));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.BotChatImageAsync, data, "."));
    }

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
