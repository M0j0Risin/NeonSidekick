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
}
