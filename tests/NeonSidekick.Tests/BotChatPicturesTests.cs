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

    /// <summary>2026-10-04 (the user's ask): Botchat ComfyUI enabled's offered workflows, else the limited ones among every installed one.</summary>
    [Fact]
    public void ComfyWorkflows_AreTheOfferedOnes_WhenEnabled_ElseTheLimitedOnes_InCatalogOrder()
    {
        var installed = new[] { Workflow("edit", image: true), Workflow("pony"), Workflow("flux", family: ComfyFamily.Flux) };

        var on = new AppSettingsData { BotChatComfy = true, ComfyWorkflowsOffered = ["flux"], BotChatLimitedComfyWorkflows = ["pony"] };
        Assert.Equal(["flux"], BotChat.ComfyWorkflows(installed, on).Select(w => w.Name));
        // Off: the limited list, any installed workflow offered or not, ignoring case and spaces; a name not installed is skipped.
        var off = new AppSettingsData { ComfyWorkflowsOffered = ["flux"], BotChatLimitedComfyWorkflows = [" FLUX ", "gone", "edit"] };
        Assert.Equal(["edit", "flux"], BotChat.ComfyWorkflows(installed, off).Select(w => w.Name));
        Assert.Empty(BotChat.ComfyWorkflows(installed, new AppSettingsData { ComfyWorkflowsOffered = ["flux"] }));   // the default: none
        Assert.Empty(BotChat.ComfyWorkflows(installed, new AppSettingsData { BotChatComfy = true }));   // on, with nothing offered
    }

    [Fact]
    public void ComfyChosen_IsTheSwitch_OrANameInTheLimitedList()
    {
        Assert.False(BotChat.ComfyChosen(new AppSettingsData()));
        Assert.False(BotChat.ComfyChosen(new AppSettingsData { BotChatLimitedComfyWorkflows = ["", "  "] }));
        Assert.True(BotChat.ComfyChosen(new AppSettingsData { BotChatComfy = true }));
        Assert.True(BotChat.ComfyChosen(new AppSettingsData { BotChatLimitedComfyWorkflows = ["pony"] }));
    }

    [Fact]
    public void BotWorkflows_AreTheFreshOnes_TheReworkOnesOnlyWithAPictureToRework()
    {
        var edit = Workflow("edit", image: true);
        var pony = Workflow("pony");
        var flux = Workflow("flux", family: ComfyFamily.Flux);
        var offered = new[] { edit, flux, pony };

        Assert.Equal(["edit", "pony"], BotChat.BotWorkflows(offered, [pony], [edit], reworkable: true).Select(w => w.Name));
        Assert.Equal(["pony"], BotChat.BotWorkflows(offered, [pony], [edit], reworkable: false).Select(w => w.Name));
        Assert.Equal(["edit"], BotChat.BotWorkflows(offered, [], [edit], reworkable: true).Select(w => w.Name));
        Assert.Equal(["flux", "pony"], BotChat.BotWorkflows(offered, [pony, flux], [], reworkable: false).Select(w => w.Name));   // in the catalog's order
        Assert.Empty(BotChat.BotWorkflows(offered, [], [], reworkable: true));
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
    [InlineData("Rework of an old castle at dusk", true, "Rework of an old castle at dusk", null)]  // a fresh prompt's first word
    [InlineData("rework: a red fox", true, "a red fox", "b.png")]   // any case with a mark after it
    public void ParseImagePrompt_ReadsTheReworkLine(string text, bool fresh, string prompt, string? path)
    {
        var (read, rework, workflow) = BotChat.ParseImagePrompt(text, Two, fresh ? [Workflow("pony")] : [], [Workflow("edit", image: true)]);

        Assert.Equal(prompt, read);
        Assert.Equal(path, rework?.Path);
        Assert.Equal(rework is null ? "pony" : "edit", workflow?.Name);   // one of each kind: that one, no line needed
    }

    private static readonly ComfyWorkflow[] TwoFresh = [Workflow("pony"), Workflow("flux-dev", family: ComfyFamily.Flux)];

    private static readonly ComfyWorkflow[] TwoReworks = [Workflow("edit", image: true), Workflow("restyle", image: true)];

    /// <summary>2026-10-04 (the user's pick): with several of a kind, the writer names its workflow on a line of its own.</summary>
    [Theory]
    [InlineData("WORKFLOW flux-dev\na red fox", "a red fox", null, "flux-dev")]
    [InlineData("**Workflow:** `FLUX-DEV`\na red fox", "a red fox", null, "flux-dev")]   // any case with a mark; marks and quotes around the name
    [InlineData("WORKFLOW: the flux-dev one\na red fox", "a red fox", null, "flux-dev")]   // the name the line holds
    [InlineData("a red fox", "a red fox", null, null)]                                       // none named: the caller takes the first
    [InlineData("WORKFLOW gone\na red fox", "a red fox", null, null)]
    [InlineData("Workflow of a busy kitchen", "Workflow of a busy kitchen", null, null)]      // a fresh prompt's first word
    [InlineData("Workflow-themed infographic of a kitchen", "Workflow-themed infographic of a kitchen", null, null)]   // the fourth review: a hyphenated word
    [InlineData("workflow - flux-dev\na red fox", "a red fox", null, "flux-dev")]           // a spaced dash is still the mark
    [InlineData("REWORK 1\nWORKFLOW restyle\na red fox", "a red fox", "a.png", "restyle")]
    [InlineData("WORKFLOW restyle\nREWORK 1\na red fox", "a red fox", "a.png", "restyle")]  // either order
    [InlineData("REWORK 2\nWORKFLOW pony\na red fox", "a red fox", "b.png", null)]          // a name of the other kind is none of the rework's
    // The name and the prompt on one line (code review, 2026-10-04: the prompt was lost).
    [InlineData("WORKFLOW: flux-dev — a cat on a moonlit roof", "a cat on a moonlit roof", null, "flux-dev")]
    [InlineData("**Workflow:** `flux-dev`: a red fox.", "a red fox.", null, "flux-dev")]
    [InlineData("REWORK 1\nWORKFLOW restyle, a red fox", "a red fox", "a.png", "restyle")]
    [InlineData("WORKFLOW: gone — a red fox", "a red fox", null, null)]                       // an unknown name before a separator: none
    [InlineData("WORKFLOW: I'd pick: flux-dev\na red fox", "a red fox", null, "flux-dev")]     // the name only the whole line holds
    [InlineData("WORKFLOW: flux-dev (the photographic one)\na red fox", "a red fox", null, "flux-dev")]   // a note when the prompt is below
    [InlineData("WORKFLOW: pony.", "", null, "pony")]
    // Joiners read as one another (the second 2026-10-04 review: "flux dev" took a shorter name and "dev" for its prompt).
    [InlineData("WORKFLOW: flux dev", "", null, "flux-dev")]
    [InlineData("WORKFLOW: flux_dev\na red fox", "a red fox", null, "flux-dev")]
    [InlineData("WORKFLOW: fluxdev a red fox", "a red fox", null, "flux-dev")]
    public void ParseImagePrompt_ReadsTheWorkflowLine_WhenAKindHasSeveral(string text, string prompt, string? path, string? workflow)
    {
        var (read, rework, named) = BotChat.ParseImagePrompt(text, Two, TwoFresh, TwoReworks);

        Assert.Equal(prompt, read);
        Assert.Equal(path, rework?.Path);
        Assert.Equal(workflow, named?.Name);
    }

    [Theory]
    [InlineData("WORKFLOW: sdxl-lightning\na red fox", null)]   // code review, 2026-10-04: took "sd", inside a longer name, quietly
    [InlineData("WORKFLOW: the sd one\na red fox", "sd")]
    [InlineData("WORKFLOW: sd_turbo\na red fox", null)]
    public void ParseImagePrompt_ANameInsideALongerWord_IsNoneOfTheSet(string text, string? workflow)
    {
        var (read, _, named) = BotChat.ParseImagePrompt(text, [], [Workflow("sd"), Workflow("flux-dev", family: ComfyFamily.Flux)], []);

        Assert.Equal("a red fox", read);
        Assert.Equal(workflow, named?.Name);
    }

    [Theory]
    [InlineData("WORKFLOW: flux dev", "flux-dev", "")]               // the second 2026-10-04 review: was flux, drawing "dev"
    [InlineData("WORKFLOW: flux dev.", "flux-dev", "")]              // the third: the full stop was the prompt
    [InlineData("WORKFLOW: fluxdev!", "flux-dev", "")]
    [InlineData("WORKFLOW: flux-dev (the photographic one)", "flux-dev", "")]   // the fourth: the note was drawn
    [InlineData("WORKFLOW: flux-dev [photo].", "flux-dev", "")]
    [InlineData("WORKFLOW: flux a red fox", "flux", "a red fox")]    // the shorter name still leads a prompt on its line
    [InlineData("WORKFLOW: flux-dev a red fox", "flux-dev", "a red fox")]
    public void ParseImagePrompt_TheLongerNameWins_WhateverItsJoiner(string text, string workflow, string prompt)
    {
        var (read, _, named) = BotChat.ParseImagePrompt(text, [], [Workflow("flux", family: ComfyFamily.Flux), Workflow("flux-dev", family: ComfyFamily.Flux)], []);

        Assert.Equal(workflow, named?.Name);
        Assert.Equal(prompt, read);
    }

    [Fact]
    public void ParseImagePrompt_WithOneWorkflowOfEachKind_LeavesAWorkflowLineInThePrompt()
    {
        var (read, rework, named) = BotChat.ParseImagePrompt("WORKFLOW: a red fox", [], [Workflow("pony")], []);

        Assert.Equal("WORKFLOW: a red fox", read);
        Assert.Null(rework);
        Assert.Equal("pony", named?.Name);
    }

    [Fact]
    public void PictureInstruction_WithNothingToRework_IsTheOldOne()
    {
        var pony = Workflow("pony");
        Assert.Equal(BotChat.ImagePromptInstruction(pony), BotChat.PictureInstruction(false, [pony], [Workflow("edit", image: true)], []));
        Assert.Equal(BotChat.PromisedPictureInstruction(pony), BotChat.PictureInstruction(true, [pony], [], Two));
    }

    [Fact]
    public void PictureInstruction_OffersTheRework_WithItsStyle_AndTheNumberedList()
    {
        var pony = Workflow("pony");
        var edit = Workflow("edit", image: true, family: ComfyFamily.Flux, tips: "Keep the faces.");

        string both = BotChat.PictureInstruction(false, [pony], [edit], Two);
        Assert.Contains(ComfyFamilies.StyleGuide(ComfyFamily.Pony), both);
        Assert.Contains("You may instead rework one of the chat's pictures", both);
        Assert.Contains("\n1. the picture of default's reply\n2. ada's picture\n", both);
        Assert.Contains("put REWORK n (n the picture's number) alone on the first line", both);
        Assert.Contains(ComfyFamilies.StyleGuide(ComfyFamily.Flux), both);
        Assert.Contains("Tips for the rework workflow: Keep the faces.", both);
        Assert.EndsWith("Answer with the prompt alone, or the REWORK line and then the prompt: no preamble, no explanation, no quotes.", both);

        string latest = BotChat.PictureInstruction(false, [], [edit], [Two[1]]);
        Assert.Contains("write a single prompt that reworks the chat's latest picture (ada's picture) so that it illustrates the line", latest);
        Assert.Contains("Put REWORK alone on the first line", latest);
        Assert.DoesNotContain("1. ", latest);

        string promised = BotChat.PictureInstruction(true, [pony], [edit], [Two[0]]);
        Assert.Contains($"answer exactly {BotChat.NoPictureAnswer}", promised);
        Assert.EndsWith($"the REWORK line and then the prompt, or {BotChat.NoPictureAnswer}: no preamble, no explanation, no quotes.", promised);
    }

    /// <summary>2026-10-04 (the user's pick): several of a kind are listed by name, each in its style, and the writer names its pick.</summary>
    [Fact]
    public void PictureInstruction_WithSeveralOfAKind_ListsThemByName_AndAsksForTheWorkflowLine()
    {
        var pony = Workflow("pony", tips: "Score tags first.");
        var flux = Workflow("flux-dev", family: ComfyFamily.Flux);

        string fresh = BotChat.PictureInstruction(false, [pony, flux], [], []);
        Assert.Contains("no screens of text. Choose the workflow that suits the picture best and write the prompt in its style. The workflows:", fresh);
        Assert.Contains("\n\npony: " + ComfyFamilies.StyleGuide(ComfyFamily.Pony) + "\nTips for pony: Score tags first.", fresh);
        Assert.Contains("\n\nflux-dev: " + ComfyFamilies.StyleGuide(ComfyFamily.Flux), fresh);
        Assert.Contains("\n\nPut WORKFLOW name (the workflow's name as listed) alone on the first line of your answer and the prompt after it.", fresh);
        Assert.EndsWith("\n\nAnswer with the WORKFLOW line and then the prompt: no preamble, no explanation, no quotes.", fresh);
        Assert.DoesNotContain(BotChat.ReworkAnswer, fresh);

        string promised = BotChat.PictureInstruction(true, [pony, flux], [], []);
        Assert.Contains($"answer exactly {BotChat.NoPictureAnswer}", promised);
        Assert.EndsWith($"the WORKFLOW line and then the prompt, or {BotChat.NoPictureAnswer}: no preamble, no explanation, no quotes.", promised);

        var restyle = Workflow("restyle", image: true, family: ComfyFamily.Flux);
        string reworks = BotChat.PictureInstruction(false, [pony], [Workflow("edit", image: true), restyle], Two);
        Assert.Contains("Write it in this style: " + ComfyFamilies.StyleGuide(ComfyFamily.Pony), reworks);   // one fresh workflow: as before
        Assert.Contains("put REWORK n (n the picture's number) alone on the first line of your answer, WORKFLOW name (the workflow's name as listed) alone on the second and the prompt after them. ", reworks);
        Assert.Contains("The rework workflows:\n\nedit: " + ComfyFamilies.StyleGuide(ComfyFamily.Pony), reworks);
        Assert.Contains("\n\nrestyle: " + ComfyFamilies.StyleGuide(ComfyFamily.Flux), reworks);
        Assert.EndsWith("Answer with the prompt alone, or the REWORK line, the WORKFLOW line and then the prompt: no preamble, no explanation, no quotes.", reworks);
    }

    [Fact]
    public void ReworkCaption_NamesThePictures_TheirPaths_AndTheCall()
    {
        Assert.Equal("(You may rework the chat's latest picture, ada's picture, at \"b.png\": call generate_image with workflow \"edit\", its path as image, and a prompt for the reworked picture.)",
            BotChat.ReworkCaption(["edit"], [Two[1]]));
        Assert.Equal("(You may rework one of the chat's pictures — call generate_image with workflow \"edit\", its path as image, and a prompt for the reworked picture. Oldest first: the picture of default's reply, \"a.png\"; ada's picture, \"b.png\".)",
            BotChat.ReworkCaption(["edit"], Two));
        // Several image → image workflows (2026-10-04): any of them.
        Assert.Equal("(You may rework the chat's latest picture, ada's picture, at \"b.png\": call generate_image with workflow \"edit\" or \"restyle\", its path as image, and a prompt for the reworked picture.)",
            BotChat.ReworkCaption(["edit", "restyle"], [Two[1]]));
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
        Assert.DoesNotContain("already loaded", BotChat.ImagePromptSkillsDirective);   // went with the preloaded skills, 2026-10-04
    }

    [Theory]
    [InlineData("shared-parent", BotMemoryMode.SharedParent)]
    [InlineData(" Independent ", BotMemoryMode.Independent)]
    [InlineData("shared", BotMemoryMode.SharedParent)]   // a hand-edited word: the default
    public void MemoryMode_ResolvesTheSavedWord(string saved, BotMemoryMode mode)
    {
        Assert.Equal(mode, BotChatMemoryMode.Resolve(new AppSettingsData { BotChatMemoryMode = saved }));
        Assert.Equal("shared-parent", new AppSettingsData().BotChatMemoryMode);
        Assert.True(new AppSettingsData().BotChatMemory);   // on by default (2026-10-04, the user's call)
        Assert.All(BotChatMemoryMode.Names, name => Assert.NotEmpty(BotChatMemoryMode.Describe(name)));
    }

    /// <summary>An unknown value warns once while it stays (the second 2026-10-04 review: every bot reply wrote it again).</summary>
    [Fact]
    public void MemoryMode_AnUnknownValue_WarnsOncePerValue()
    {
        var warnings = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Level >= NeonSidekick.Diagnostics.DiagnosticLevel.Warning && e.Message.Contains("BotChatMemoryMode", StringComparison.Ordinal)) warnings.Add(e.Message); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            string first = "typo-" + Guid.NewGuid().ToString("N");
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(BotMemoryMode.SharedParent, BotChatMemoryMode.Resolve(new AppSettingsData { BotChatMemoryMode = first }));
            }

            Assert.Single(warnings);
            BotChatMemoryMode.Resolve(new AppSettingsData { BotChatMemoryMode = first + "-2" });
            Assert.Equal(2, warnings.Count);
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }
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
    public void TheBotchatTab_IsLast_ItsRowsDefaultingToSingle_ParentServer_KillOn_NoComfy_NoWorkflows_Automatic_Latest_Async_AFiveSecondPause_NoTools_NoSkills_MemoryShared_AndNoVision()
    {
        var data = new AppSettingsData();

        Assert.Equal("Botchat", SettingsMenu.TabTitles[(int)SettingsTab.BotChat]);
        Assert.Equal((int)SettingsTab.BotChat, SettingsMenu.TabTitles.Count - 1);   // last again since later on 2026-09-27 (the Claude tab moved to /tools)
        Assert.Equal([SettingsField.BotChatLlmMode, SettingsField.BotChatMultiEmbedded, SettingsField.BotChatMultiEmbeddedKill, SettingsField.BotChatComfy, SettingsField.BotChatLimitedComfyWorkflows, SettingsField.BotChatImageMode, SettingsField.BotChatImg2ImgMode, SettingsField.BotChatImageAsync, SettingsField.BotChatNonTtsDelaySeconds, SettingsField.BotChatTools, SettingsField.BotChatLimitedTools, SettingsField.BotChatSkills, SettingsField.BotChatLimitedSkills, SettingsField.BotChatMemory, SettingsField.BotChatMemoryMode, SettingsField.BotChatVision, SettingsField.BotChatCamera], SettingsMenu.TabFields[(int)SettingsTab.BotChat]);
        Assert.Equal(["Botchat LLM mode", "Botchat multi-embedded", "Botchat multi-embedded kill", "Botchat ComfyUI enabled", "Botchat ComfyUI limited workflows", "Botchat image mode", "Botchat img2img mode", "Botchat image async", "Botchat non-TTS delay", "Botchat tools enabled", "Botchat limited tools", "Botchat skills enabled", "Botchat limited skills", "Botchat memory enabled", "Botchat memory mode", "Botchat vision enabled", "Botchat camera"], SettingsMenu.TabFields[(int)SettingsTab.BotChat].Select(SettingsMenu.FieldName));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatLlmMode));
        // Botchat multi-embedded and its kill switch under the mode (later on 2026-09-29, the user's asks).
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatMultiEmbedded));
        Assert.True(SettingsMenu.IsToggle(SettingsField.BotChatMultiEmbeddedKill));
        Assert.Equal("parent-server", SettingsMenu.FieldValue(SettingsField.BotChatMultiEmbedded, data, "."));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.BotChatMultiEmbeddedKill, data, "."));
        Assert.Equal("parent-server " + NeonSidekick.UI.Theme.DimMarkup(BotChatMultiEmbedded.Describe("parent-server")), SettingsMenu.BotChatMultiEmbeddedLabel("parent-server"));
        Assert.Equal("the extra servers stop when the botchat ends", SettingsMenu.ToggleDescribe(SettingsField.BotChatMultiEmbeddedKill, true));
        Assert.Equal("extra servers stay up for later botchats until /botchat --kill or exit", SettingsMenu.ToggleDescribe(SettingsField.BotChatMultiEmbeddedKill, false));
        Assert.Equal("single", SettingsMenu.FieldValue(SettingsField.BotChatLlmMode, data, "."));
        Assert.Equal("multi", SettingsMenu.FieldValue(SettingsField.BotChatLlmMode, new AppSettingsData { BotChatLlmMode = "multi" }, "."));
        // ComfyUI (later on 2026-10-04, in place of the images switch and the two pickers): a toggle, off, and its checklist, none.
        Assert.True(SettingsMenu.IsToggle(SettingsField.BotChatComfy));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatLimitedComfyWorkflows));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.BotChatComfy, data, "."));
        Assert.Equal(SettingsMenu.NoLimitedNamesLabel, SettingsMenu.FieldValue(SettingsField.BotChatLimitedComfyWorkflows, data, "."));
        Assert.Equal("flux, edit", SettingsMenu.FieldValue(SettingsField.BotChatLimitedComfyWorkflows, new AppSettingsData { BotChatLimitedComfyWorkflows = ["flux", " edit ", ""] }, "."));
        Assert.Equal("the bots get this profile's offered ComfyUI workflows", SettingsMenu.ToggleDescribe(SettingsField.BotChatComfy, true));
        Assert.Equal("the bots get the Botchat ComfyUI limited workflows alone", SettingsMenu.ToggleDescribe(SettingsField.BotChatComfy, false));
        Assert.True(SettingsMenu.IsToggle(SettingsField.BotChatImageAsync));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatImageMode));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatImg2ImgMode));
        Assert.Equal("automatic", SettingsMenu.FieldValue(SettingsField.BotChatImageMode, data, "."));
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
        // Limited skills (2026-10-04; the preloaded skills' checklist of 2026-09-27): a checklist, none by default.
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatLimitedSkills));
        Assert.Equal(SettingsMenu.NoLimitedNamesLabel, SettingsMenu.FieldValue(SettingsField.BotChatLimitedSkills, data, "."));
        Assert.Equal("haiku, pony-prompts", SettingsMenu.FieldValue(SettingsField.BotChatLimitedSkills, new AppSettingsData { BotChatLimitedSkills = ["haiku", " pony-prompts ", ""] }, "."));
        // Tools (2026-10-04): a toggle, off by default, and its checklist, none by default.
        Assert.True(SettingsMenu.IsToggle(SettingsField.BotChatTools));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.BotChatTools, data, "."));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatLimitedTools));
        Assert.Equal(SettingsMenu.NoLimitedNamesLabel, SettingsMenu.FieldValue(SettingsField.BotChatLimitedTools, data, "."));
        Assert.Equal("web_search, read_file", SettingsMenu.FieldValue(SettingsField.BotChatLimitedTools, new AppSettingsData { BotChatLimitedTools = ["web_search", "read_file"] }, "."));
        Assert.Equal("the bots get every tool this chat would offer", SettingsMenu.ToggleDescribe(SettingsField.BotChatTools, true));
        // Memory (2026-10-04): a toggle, on by default, and its mode, shared-parent by default.
        Assert.True(SettingsMenu.IsToggle(SettingsField.BotChatMemory));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.BotChatMemory, data, "."));
        Assert.False(SettingsMenu.IsToggle(SettingsField.BotChatMemoryMode));
        Assert.Equal("shared-parent", SettingsMenu.FieldValue(SettingsField.BotChatMemoryMode, data, "."));
        Assert.Equal("independent    " + NeonSidekick.UI.Theme.DimMarkup(BotChatMemoryMode.Describe("independent")), SettingsMenu.BotChatMemoryModeLabel("independent"));
        Assert.Equal("the bots remember nothing", SettingsMenu.ToggleDescribe(SettingsField.BotChatMemory, false));
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
