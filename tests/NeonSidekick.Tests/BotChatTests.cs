using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

/// <summary><c>/botchat</c>'s pure half (2026-09-24): the cast and topic, the next speaker, each speaker's view and prompt.</summary>
public class BotChatTests
{
    private static readonly string[] Home = ["default", "ada", "max"];

    [Theory]
    [InlineData("", "default,ada,max", "")]                          // no names: everyone, the starter first
    [InlineData("ada", "default,ada", "")]
    [InlineData("ADA max", "default,ada,max", "")]                   // names ignore case and take the listed spelling
    [InlineData("ada the best pizza", "default,ada", "the best pizza")]
    [InlineData("pizza with ada", "default,ada,max", "pizza with ada")]   // a word that is no profile ends the names
    [InlineData("ada ada", "default,ada", "")]
    [InlineData("default ada", "default,ada", "")]
    [InlineData("ada -- max speed", "default,ada", "max speed")]     // after --, a profile's name is the topic's (later on 2026-09-24)
    [InlineData("-- max speed", "default,ada,max", "max speed")]     // -- with no names: everyone
    [InlineData("ada --", "default,ada", "")]                        // -- with nothing after it: no topic
    [InlineData("ada max--x", "default,ada", "max--x")]             // only a whole word is the separator
    public void ParseArgs_SplitsTheCastFromTheTopic(string args, string cast, string topic)
    {
        var (names, parsedTopic) = BotChat.ParseArgs(args, Home, "default");

        Assert.Equal(cast.Split(','), names);
        Assert.Equal(topic, parsedTopic);
    }

    [Theory]
    [InlineData("--resume", true, "")]
    [InlineData("  --RESUME   Talk about cats  ", true, "Talk about cats")]
    [InlineData("--resume\tcats", true, "cats")]
    [InlineData("ada --resume", false, "")]          // only as the first word
    [InlineData("--resumed", false, "")]             // a whole word
    [InlineData("-- resume", false, "")]             // the topic separator, then a topic
    [InlineData("", false, "")]
    public void ParseResume_IsTheFirstWord_TheRestTheUsersLine(string args, bool resume, string line)
    {
        Assert.Equal((resume, line), BotChat.ParseResume(args));
    }

    [Theory]
    [InlineData("--kill", true, "")]
    [InlineData("  --KILL  ", true, "")]
    [InlineData("--kill now", true, "now")]           // the usage error's
    [InlineData("ada --kill", false, "")]             // only as the first word
    [InlineData("--killer", false, "")]               // a whole word
    [InlineData("", false, "")]
    public void ParseKill_IsTheFirstWord(string args, bool kill, string after)
    {
        // Later on 2026-09-29 (the user's ask): /botchat --kill stops a multi-server botchat's extra embedded servers.
        Assert.Equal((kill, after), BotChat.ParseKill(args));
    }

    [Fact]
    public void TheEmbeddedBotsWording_IsPinned()
    {
        Assert.Equal("--kill", BotChat.KillSwitch);
        Assert.Equal("stop the extra embedded servers of multi-server botchats", BotChat.KillNote);
        Assert.Equal("Usage: /botchat --kill, alone: it stops the extra embedded servers a multi-server botchat left running.", BotChat.KillUsageError);
        Assert.Equal("(botchat: bob wanted Gemma 4 E4B QAT, but one embedded server runs Gemma 4 E2B, so bob uses it; Botchat multi-embedded multi-server gives it its own)",
            BotChat.SharedEmbeddedWarning("bob", "Gemma 4 E4B QAT", "Gemma 4 E2B"));
        Assert.Equal("(botchat: stopped 1 extra embedded server: Gemma 4 E2B)", BotChat.ExtrasStoppedNotice(["Gemma 4 E2B"]));
        Assert.Equal("(botchat: stopped 2 extra embedded servers: Gemma 4 E2B and Gemma 4 E4B QAT)", BotChat.ExtrasStoppedNotice(["Gemma 4 E2B", "Gemma 4 E4B QAT"]));
        Assert.Equal("(botchat: no extra embedded server is running)", BotChat.NoExtrasNotice);
        Assert.Equal("parent-server", BotChatMultiEmbedded.Default);
        Assert.Equal(["parent-server", "multi-server"], BotChatMultiEmbedded.Names);
        Assert.Equal(BotEmbeddedMode.MultiServer, BotChatMultiEmbedded.Resolve(new Settings.AppSettingsData { BotChatMultiEmbedded = " Multi-Server " }));
        Assert.Equal(BotEmbeddedMode.ParentServer, BotChatMultiEmbedded.Resolve(new Settings.AppSettingsData { BotChatMultiEmbedded = "both" }));   // warns, the default
        Assert.Equal("one embedded server: a bot naming another embedded model uses the one running, with a warning", BotChatMultiEmbedded.Describe("parent-server"));
        Assert.Equal("an extra llama-server for each other embedded model the bots name; the running one is kept (more VRAM)", BotChatMultiEmbedded.Describe("multi-server"));
        var data = new Settings.AppSettingsData();
        Assert.Equal(("parent-server", true), (data.BotChatMultiEmbedded, data.BotChatMultiEmbeddedKill));
        var copy = Settings.AppSettings.Copy(new Settings.AppSettingsData { BotChatMultiEmbedded = "multi-server", BotChatMultiEmbeddedKill = false });
        Assert.Equal(("multi-server", false), (copy.BotChatMultiEmbedded, copy.BotChatMultiEmbeddedKill));
    }

    [Fact]
    public void ParseArgs_NamingOnlyTheStarter_LeavesACastOfOne()
    {
        Assert.Equal(new[] { "ada" }, BotChat.ParseArgs("ada", Home, "ada").Names);
    }

    [Fact]
    public void NextSpeaker_NeverRepeats_AndReachesEveryoneElse()
    {
        var random = new Random(42);
        int last = 0;
        var seen = new HashSet<int>();
        for (int i = 0; i < 1000; i++)
        {
            int next = BotChat.NextSpeaker(4, last, random);
            Assert.NotEqual(last, next);
            Assert.InRange(next, 0, 3);
            seen.Add(next);
            last = next;
        }

        Assert.Equal(4, seen.Count);
    }

    private static readonly string[] Cast = ["default", "ada", "max"];

    [Theory]
    [InlineData("What do you think, Ada?", "1")]
    [InlineData("ada and MAX, over to you", "1,2")]
    [InlineData("Adam agrees", "")]
    [InlineData("ada_x and max-y", "")]
    [InlineData("@ada's point stands", "1")]
    [InlineData("I set it by default.", "0")]   // a profile named with a common word is named by chance (the known limit)
    [InlineData("no names here", "")]
    public void Mentioned_FindsWholeNames_IgnoringCase(string text, string expected)
    {
        var want = expected.Length == 0 ? [] : expected.Split(',').Select(int.Parse).ToArray();
        Assert.Equal(want, BotChat.Mentioned(text, Cast));
    }

    [Fact]
    public void NextSpeaker_WithMentions_PicksOnlyAMentionedBot()
    {
        var random = new Random(7);
        var seen = new HashSet<int>();
        for (int i = 0; i < 1000; i++)
        {
            int next = BotChat.NextSpeaker(4, 0, random, [2, 3]);
            Assert.Contains(next, new[] { 2, 3 });
            seen.Add(next);
        }

        Assert.Equal(new[] { 2, 3 }, seen.Order());
    }

    [Fact]
    public void NextSpeaker_AMentionOfTheLastSpeakerAlone_OrNone_FallsBackToRandom()
    {
        var random = new Random(3);
        var seen = new HashSet<int>();
        for (int i = 0; i < 300; i++)
        {
            seen.Add(BotChat.NextSpeaker(3, 0, random, [0]));
            seen.Add(BotChat.NextSpeaker(3, 0, random, []));
        }

        Assert.Equal(new[] { 1, 2 }, seen.Order());
    }

    [Fact]
    public void Addressed_TheUsersLinesFirst_ThenTheLastReply()
    {
        BotChatLine reply = new("default", "Max, you tell them.");
        Assert.Equal(new[] { 2 }, BotChat.Addressed([reply], Cast));
        Assert.Equal(new[] { 1 }, BotChat.Addressed([reply, new("User", "No, ada first.", IsUser: true)], Cast));
        Assert.Equal(new[] { 2 }, BotChat.Addressed([reply, new("User", "I agree.", IsUser: true)], Cast));   // the user named nobody: the reply's word stands
        Assert.Empty(BotChat.Addressed([new("default", "Nice weather.")], Cast));
        Assert.Empty(BotChat.Addressed([], Cast));
    }

    [Fact]
    public void NextSpeaker_WithTwo_Alternates()
    {
        var random = new Random(1);
        Assert.Equal(1, BotChat.NextSpeaker(2, 0, random));
        Assert.Equal(0, BotChat.NextSpeaker(2, 1, random));
    }

    [Fact]
    public void BuildView_OwnLinesAreTheAssistants_OthersSignedAndMerged()
    {
        BotChatLine[] lines =
        [
            new("default", "Hi."),
            new("ada", "Hello."),
            new("User", "Talk about cats.", IsUser: true),
        ];

        var (prior, text) = BotChat.BuildView("default", lines, ["ada"], "");

        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant }, prior.Select(m => m.Role));
        Assert.Equal(BotChat.OpeningText(["ada"], ""), prior[0].Text);
        Assert.Equal("Hi.", prior[1].Text);
        Assert.Equal("ada: Hello.\n\nUser: Talk about cats.", text);
    }

    [Fact]
    public void BuildView_FirstTurn_IsTheOpening_AndAnotherSpeakerSeesItAll()
    {
        var (prior, text) = BotChat.BuildView("default", [], ["ada"], "pizza");
        Assert.Empty(prior);
        Assert.Equal(BotChat.OpeningText(["ada"], "pizza"), text);

        var (adaPrior, adaText) = BotChat.BuildView("ada", [new("default", "Hi.")], ["default"], "pizza");
        Assert.Empty(adaPrior);
        Assert.Equal("default: Hi.", adaText);
    }

    [Fact]
    public void BuildView_KeepsTheLatestLinesOnly()
    {
        var lines = Enumerable.Range(1, 10).Select(i => new BotChatLine(i % 2 == 0 ? "ada" : "default", "line " + i)).ToList();

        var (prior, text) = BotChat.BuildView("default", lines, ["ada"], "", maxLines: 3);

        // Lines 8, 9, 10 kept: ada's 8, default's own 9, ada's 10 — opening inserted ahead of none (it starts on a user line).
        Assert.Equal(new[] { "ada: line 8", "line 9" }, prior.Select(m => m.Text));
        Assert.Equal("ada: line 10", text);
    }

    [Fact]
    public void StoredHistory_EndsOnEverythingSaid()
    {
        BotChatLine[] lines = [new("default", "Hi."), new("ada", "Hello.")];

        var stored = BotChat.StoredHistory("default", lines, ["default", "ada"], "");

        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User }, stored.Select(m => m.Role));
        Assert.Equal("ada: Hello.", stored[^1].Text);
    }

    [Fact]
    public void SystemPrompt_IsThePersona_ThenTheRules_WithNoToolRules()
    {
        string prompt = BotChat.SystemPrompt("You are Ada, a pirate.", "ada", ["default"], "ships", speechOutput: false, voiceDirective: null, markdown: false);

        Assert.StartsWith("You are Ada, a pirate.", prompt, StringComparison.Ordinal);
        Assert.EndsWith(BotChat.Rules("ada", ["default"], "ships"), prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(Assistant.DefaultPersona, prompt, StringComparison.Ordinal);
        Assert.StartsWith(Assistant.DefaultPersona, BotChat.SystemPrompt(null, "neon", ["ada"], "", false, null, false), StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPrompt_WithSkills_PutsTheLoadOnlyBlock_BetweenThePersonaAndTheRules()
    {
        var haiku = new Skills.Skill("haiku", "Writes haiku.", Skills.SkillScope.Global, @"D:\home\skills\haiku");
        string plain = BotChat.SystemPrompt("You are Ada.", "ada", ["default"], "ships", false, null, false);
        string prompt = BotChat.SystemPrompt("You are Ada.", "ada", ["default"], "ships", false, null, false, skills: [haiku]);

        Assert.Equal(plain, BotChat.SystemPrompt("You are Ada.", "ada", ["default"], "ships", false, null, false, skills: []));
        Assert.DoesNotContain(Skills.SkillsPrompt.LoadOnlyDirective, plain, StringComparison.Ordinal);
        Assert.EndsWith("\n\n" + Skills.SkillsPrompt.LoadOnlySection([haiku]) + "\n\n" + BotChat.Rules("ada", ["default"], "ships"), prompt, StringComparison.Ordinal);
        Assert.StartsWith("You are Ada.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(Llm.Tools.SkillEditorTool.ToolName, prompt, StringComparison.Ordinal);
    }

    private static IReadOnlyList<Files.ImageAttachment> Pics(int n) =>
        Enumerable.Range(0, n).Select(i => new Files.ImageAttachment("p" + i + ".png", [(byte)i], "image/png", 1, 1)).ToList();

    [Fact]
    public void PicturesFor_TheNewerOnes_LessTheSpeakersOwnDrawn_TheNewestFourPictures()
    {
        BotPicture[] log =
        [
            new(1, "ada", Drawn: false, Pics(1)),     // seen already
            new(2, "neon", Drawn: true, Pics(1)),     // neon drew it: neon saw it then
            new(3, "neon", Drawn: false, Pics(1)),    // the app's picture of neon's reply: neon never saw it
            new(4, "max", Drawn: true, Pics(2)),
            new(5, "ada", Drawn: false, Pics(2)),
        ];

        var shown = BotChat.PicturesFor("NEON", log, seen: 1);
        Assert.Equal([4, 5], shown.Select(p => p.Seq));   // four pictures: 3 no longer fits
        Assert.Equal([3, 4, 5], BotChat.PicturesFor("neon", log, seen: 1, max: 5).Select(p => p.Seq));
        var cut = BotChat.PicturesFor("neon", log, seen: 1, max: 3);
        Assert.Equal([4, 5], cut.Select(p => p.Seq));
        Assert.Equal([log[3].Images[1]], cut[0].Images);   // the entry over the cap keeps its newest
        Assert.Equal([2, 3, 4, 5], BotChat.PicturesFor("ada", log, seen: 1, max: 10).Select(p => p.Seq));
        Assert.Empty(BotChat.PicturesFor("ada", log, seen: 5));
    }

    [Fact]
    public void PicturesCaption_IsPinned()
    {
        BotPicture[] shown = [new(3, "neon", false, Pics(1)), new(4, "max", true, Pics(2)), new(5, "ada", false, Pics(1)), new(6, "neon", true, Pics(1))];

        Assert.Equal("(Attached, oldest first: the pictures shown in the chat since you last spoke — the picture of your reply, max's 2 pictures, the picture of ada's reply, neon's picture.)",
            BotChat.PicturesCaption("neon", shown));
    }

    /// <summary>Prompt text is pinned (the test pin policy): the words the models are told.</summary>
    [Fact]
    public void Rules_ArePinned()
    {
        Assert.Equal(
            "You are ada, in a group chat with default and max and the user, who may join in at any time. "
            + "The others' lines reach you as \"Name: text\". Speak only as yourself, in your own voice and personality; never write lines for anyone else and never start your reply with your name. "
            + "Keep each reply short: a few sentences. Keep the conversation going: react to what was just said, disagree when you see it differently, ask questions, and bring up something new when a thread runs dry. Never say goodbye or try to end the chat. "
            + "The topic: ships",
            BotChat.Rules("ada", ["default", "max"], "ships"));
        Assert.Equal("(The group chat with ada begins. Open it on any subject you like.)", BotChat.OpeningText(["ada"], ""));
    }

    [Theory]
    [InlineData("am_eric", BotGender.Male)]
    [InlineData("bm_george", BotGender.Male)]
    [InlineData(" AM_Adam ", BotGender.Male)]
    [InlineData("jm_kumo", BotGender.Male)]
    [InlineData("af_heart", BotGender.Female)]
    [InlineData("bf_emma", BotGender.Female)]
    [InlineData("jf_alpha", BotGender.Female)]
    [InlineData("", BotGender.Female)]          // unknown is female: the user's call (2026-09-25)
    [InlineData(null, BotGender.Female)]
    [InlineData("alloy", BotGender.Female)]
    [InlineData("mm", BotGender.Female)]
    [InlineData("_m_x", BotGender.Female)]
    public void GenderOf_ReadsTheKokoroPrefix_FemaleByDefault(string? voice, BotGender expected)
    {
        Assert.Equal(expected, BotChat.GenderOf(voice));
    }

    /// <summary>Prompt text is pinned: the pronouns sentence, and where it sits in the rules.</summary>
    [Fact]
    public void PronounsLine_IsPinned_AndFollowsTheFirstSentence()
    {
        string line = BotChat.PronounsLine([("ada", BotGender.Female), ("max", BotGender.Male)]);
        Assert.Equal("Use these pronouns for the others: ada is a woman (she/her); max is a man (he/him).", line);

        string rules = BotChat.Rules("neon", ["ada", "max"], "", line);
        Assert.StartsWith("You are neon, in a group chat with ada and max and the user, who may join in at any time. " + line + " The others' lines", rules, StringComparison.Ordinal);
        Assert.Equal(BotChat.Rules("neon", ["ada", "max"], ""), rules.Replace(line + " ", "", StringComparison.Ordinal));
    }

    [Fact]
    public void JoinNames_ReadsAsAList()
    {
        Assert.Equal("", BotChat.JoinNames([]));
        Assert.Equal("ada", BotChat.JoinNames(["ada"]));
        Assert.Equal("ada and max", BotChat.JoinNames(["ada", "max"]));
        Assert.Equal("ada, max and neon", BotChat.JoinNames(["ada", "max", "neon"]));
    }

    /// <summary>With neither tools nor memories (2026-10-04) the prompt is the tool-free one, byte for byte as before.</summary>
    [Fact]
    public void SystemPrompt_WithoutToolsOrMemories_IsAsBefore()
    {
        string expected = Assistant.SystemPrompt(false, null, "You are Ada.", tools: false, files: false, timers: false) + "\n\n" + BotChat.Rules("ada", ["max"], "pizza");

        Assert.Equal(expected, BotChat.SystemPrompt("You are Ada.", "ada", ["max"], "pizza", false, null, false));
    }

    /// <summary>
    /// With tools (2026-10-04) the default rules carry the offered tools' sentences, as the main chat's would; with memories the
    /// listed section follows them, then the skills, then the rules with the memory rule last.
    /// </summary>
    [Fact]
    public void SystemPrompt_WithToolsAndMemories_TheRulesTheListAndTheMemoryRule_InOrder()
    {
        var skill = new Skills.Skill("haiku", "Writes haiku.", Skills.SkillScope.Profile, "haiku");
        string prompt = BotChat.SystemPrompt("You are Ada.", "ada", ["max"], "", false, null, false, skills: [skill], tools: new TurnRules(Web: true, Timers: true), memories: ["The user likes pizza."]);

        string rules = Assistant.DefaultRules(false, tools: true, files: false, web: true);
        string memory = Memory.MemoryPrompt.ListedSection(["The user likes pizza."], save: true);
        Assert.StartsWith("You are Ada.\n\n" + rules + "\n\n" + memory + "\n\n" + Skills.SkillsPrompt.LoadOnlySection([skill]), prompt);
        Assert.EndsWith(" " + BotChat.MemoryRule, prompt);
        Assert.DoesNotContain(BotChat.MemoryRule, BotChat.SystemPrompt("You are Ada.", "ada", ["max"], "", false, null, false));
    }

    [Theory]
    [InlineData(null, false, false, false, 1)]
    [InlineData(null, true, false, false, 3)]
    [InlineData(null, false, true, false, 3)]
    [InlineData(null, true, true, false, 5)]
    [InlineData(null, false, false, true, 2)]
    [InlineData(10000, false, false, false, 10000)]
    [InlineData(2, true, true, true, 6)]   // never fewer than the botchat's own
    public void ToolIterations_TheBotchatsFew_OrTheMainChatsCap(int? mainCap, bool image, bool skill, bool memory, int expected)
    {
        Assert.Equal(expected, BotChat.ToolIterations(mainCap, image, skill, memory));
    }

    private sealed class NamedTool(string name) : AIFunction
    {
        public override string Name => name;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => new("ok");
    }

    [Fact]
    public void TurnTools_TheGeneralTools_Memory_ThePicture_ThenTheSkill()
    {
        var tools = BotChat.TurnTools([new NamedTool("get_current_time")], [new NamedTool("save_memory"), new NamedTool("recall_memory")], new NamedTool("generate_image"), new NamedTool("load_skill"));

        Assert.Equal(["get_current_time", "save_memory", "recall_memory", "generate_image", "load_skill"], tools.Select(t => t.Name));
        Assert.Empty(BotChat.TurnTools([], [], null, null));
        Assert.Equal(["generate_image", "load_skill", "recall_memory", "save_memory"], BotChat.CaughtWrittenCalls.Order(StringComparer.Ordinal));
    }
}
