using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;
using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The <c>/botchat</c> bots' tools and memory (2026-10-04, the user's asks): <c>Botchat tools enabled</c> and
/// <c>Botchat limited tools</c>, <c>Botchat memory enabled</c> and <c>Botchat memory mode</c>.
/// </summary>
public partial class ChatScreenTests
{
    private static string[] ToolNames(ChatOptions? options) => (options?.Tools ?? []).Cast<AIFunction>().Select(t => t.Name).ToArray();

    /// <summary>Tools on: the bot is offered exactly the main chat's turn's tools, less memory and skills, with the rules they call for.</summary>
    [Fact]
    public async Task BotChat_ToolsEnabled_TheBotGetsTheMainChatsTools_LessMemoryAndSkills()
    {
        BotChatFixture();
        _settings.Update(d => d.BotChatTools = true);
        _chat.EnqueueText("Main reply.");
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("hello");
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        string[] main = ToolNames(_chat.Options[0]);
        string[] bot = ToolNames(_chat.Options[1]);
        Assert.Contains(GetCurrentTimeTool.ToolName, bot);
        Assert.Contains(SaveMemoryTool.ToolName, main);
        Assert.Equal(main.Where(n => n is not (SaveMemoryTool.ToolName or RecallMemoryTool.ToolName or LoadSkillTool.ToolName or SkillEditorTool.ToolName)), bot);
        Assert.Contains(Assistant.ToolRules, SystemText(_chat.Requests[1]));
        Assert.Contains("in a group chat with ada and the user", SystemText(_chat.Requests[1]));
    }

    /// <summary>
    /// Limited tools: the named tools alone, each only while the main chat offers it (web_search with Web tools off is not), a name
    /// that is no tool skipped; the rules follow what is offered.
    /// </summary>
    [Fact]
    public async Task BotChat_LimitedTools_TheNamedOnesTheMainChatOffers_Alone()
    {
        BotChatFixture();
        _settings.Update(d => { d.WebTools = false; d.BotChatLimitedTools = [GetCurrentTimeTool.ToolName, "web_search", "nope"]; });
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(2);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal([GetCurrentTimeTool.ToolName], ToolNames(_chat.Options[0]));
        string system = SystemText(_chat.Requests[0]);
        Assert.Contains(Assistant.ToolRulesWithoutTimers, system);
        Assert.DoesNotContain(Assistant.WebRule, system);
    }

    /// <summary>
    /// The main chat's ComfyUI tools are never the bots' (later on 2026-10-04, the user's report: its generate_image sat beside the
    /// bot's own and the bots drew with the profile's workflows): ticked in the limited tools, or with the tools on, they are not
    /// offered; the Botchat ComfyUI rows alone give a bot generate_image.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BotChat_TheMainChatsComfyTools_AreNeverTheBots(bool toolsEnabled)
    {
        BotChatFixture();
        ComfyServer();   // pony offered to the main chat
        _settings.Update(d => { d.BotChatTools = toolsEnabled; d.BotChatLimitedTools = [GetCurrentTimeTool.ToolName, GenerateImageTool.ToolName, SetSplashImageTool.ToolName]; });
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(2);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        string[] bot = ToolNames(_chat.Options[0]);
        Assert.Contains(GetCurrentTimeTool.ToolName, bot);
        Assert.DoesNotContain(GenerateImageTool.ToolName, bot);
        Assert.DoesNotContain(SetSplashImageTool.ToolName, bot);
    }

    /// <summary>With the main chat's tools a bot's turn has the main chat's round trips, not the botchat's few.</summary>
    [Fact]
    public async Task BotChat_ToolsEnabled_ABotMayTakeMoreThanThreeRoundTrips()
    {
        BotChatFixture();
        _settings.Update(d => d.BotChatTools = true);
        for (int i = 0; i < 4; i++)
        {
            _chat.Enqueue(FakeChatClient.Call("t" + i, GetCurrentTimeTool.ToolName, new Dictionary<string, object?>()));
        }

        _chat.EnqueueText("It is late.");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(6);
        PushLine("/botchat");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(6, _chat.Requests.Count);
        Assert.Contains("It is late.", output);
        Assert.All([0, 1, 2, 3, 4], i => Assert.Contains(GetCurrentTimeTool.ToolName, ToolNames(_chat.Options[i])));
    }

    /// <summary>
    /// Memory on, shared-parent (the defaults), and the parent's Memory off (Botchat memory enabled alone decides): every bot's
    /// prompt lists the starting profile's memories with the save sentences and the memory rule, it is offered save_memory and
    /// recall_memory, and a bot's save lands in the starting profile's memory.json and in the next bot's prompt.
    /// </summary>
    [Fact]
    public async Task BotChat_Memory_SharedParent_EveryBotSeesAndWritesTheParentsMemories()
    {
        BotChatFixture();
        _settings.Update(d => { d.BotChatMemory = true; d.Memory = false; });
        new MemoryStore(_settings.ProfileDirectory).Add("The user likes pizza.");
        _chat.Enqueue(FakeChatClient.Call("m1", SaveMemoryTool.ToolName, new Dictionary<string, object?> { ["text"] = "The user plays chess." }));
        _chat.EnqueueText("Noted.");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(3);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal([SaveMemoryTool.ToolName, RecallMemoryTool.ToolName], ToolNames(_chat.Options[0]));
        string neon = SystemText(_chat.Requests[0]);
        Assert.Contains("- The user likes pizza.", neon);
        Assert.Contains(MemoryPrompt.SaveSentences, neon);
        Assert.Contains(BotChat.MemoryRule, neon);
        Assert.DoesNotContain(MemoryPrompt.RecallSentence, neon);
        Assert.Contains("- The user plays chess.", SystemText(_chat.Requests[2]));   // ada's
        Assert.Contains("The user plays chess.", File.ReadAllText(Path.Combine(_settings.ProfileDirectory, MemoryStore.FileName)));
    }

    /// <summary>Independent: a bot's save goes to its own profile's memory.json, and the other bots never see it.</summary>
    [Fact]
    public async Task BotChat_Memory_Independent_EachBotItsOwnProfilesMemory()
    {
        BotChatFixture();
        _settings.Update(d => { d.BotChatMemory = true; d.BotChatMemoryMode = "independent"; });
        _chat.EnqueueText("Hello from Neon.");
        _chat.Enqueue(FakeChatClient.Call("m1", SaveMemoryTool.ToolName, new Dictionary<string, object?> { ["text"] = "The user plays chess." }));
        _chat.EnqueueText("Saved.");
        _chat.EnqueueText("Neon ", "again.");
        EscDuringRequest(4);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.Contains("The user plays chess.", File.ReadAllText(Path.Combine(ProfileDir("ada"), MemoryStore.FileName)));
        string parent = Path.Combine(_settings.ProfileDirectory, MemoryStore.FileName);
        Assert.False(File.Exists(parent) && File.ReadAllText(parent).Contains("The user plays chess.", StringComparison.Ordinal));
        Assert.DoesNotContain("The user plays chess.", SystemText(_chat.Requests[3]));   // neon
    }

    /// <summary>Botchat memory enabled off with the parent's Memory on: no bot remembers — no tools, no memory section.</summary>
    [Fact]
    public async Task BotChat_MemoryOff_NoBotRemembers_WhateverTheParentsMemory()
    {
        BotChatFixture();   // Botchat memory enabled off; Memory on
        new MemoryStore(_settings.ProfileDirectory).Add("The user likes pizza.");
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(2);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.True(_settings.Current.Memory);
        Assert.Empty(ToolNames(_chat.Options[0]));
        Assert.DoesNotContain(MemoryPrompt.DirectiveWithoutTool, SystemText(_chat.Requests[0]));
        Assert.DoesNotContain("pizza", SystemText(_chat.Requests[0]));
    }

    /// <summary>save_memory switched off on /tools: recall alone, and the section says nothing of saving.</summary>
    [Fact]
    public async Task BotChat_Memory_SaveSwitchedOffOnTools_RecallAlone_NoSaveSentences()
    {
        BotChatFixture();
        _settings.Update(d => { d.BotChatMemory = true; d.ToolsDisabled = [SaveMemoryTool.ToolName]; });
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("Ada ", "answers.");
        EscDuringRequest(2);
        PushLine("/botchat");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal([RecallMemoryTool.ToolName], ToolNames(_chat.Options[0]));
        string system = SystemText(_chat.Requests[0]);
        Assert.Contains(MemoryPrompt.ListedSection([], save: false), system);
        Assert.DoesNotContain(MemoryPrompt.SaveSentences, system);
    }
}
