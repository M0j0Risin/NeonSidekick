using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>Memory mode</c> (2026-10-04, the user's ask: the Memory switch became read-write / read-only / disabled): the parse, the
/// pinned words, the turn's tools and prompt per mode, and the summaries that say so.
/// </summary>
public class MemoryModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Names_Default_AndTheParse_ArePinned()
    {
        Assert.Equal(["read-write", "read-only", "disabled"], MemoryMode.Names);
        Assert.Equal("read-write", MemoryMode.Default);
        Assert.Equal("read-write", new AppSettingsData().MemoryMode);
        Assert.True(MemoryMode.TryParse(" Read-Only ", out var mode));
        Assert.Equal(MemoryAccess.ReadOnly, mode);
        Assert.True(MemoryMode.TryParse("disabled", out mode));
        Assert.Equal(MemoryAccess.Disabled, mode);
        Assert.False(MemoryMode.TryParse("on", out mode));   // /memory's alias, not a saved value
        Assert.Equal(MemoryAccess.ReadWrite, mode);
        Assert.False(MemoryMode.TryParse(null, out _));
        Assert.All(MemoryMode.Names, name => Assert.Equal(name, MemoryMode.Name(MemoryMode.TryParse(name, out var m) ? m : throw new InvalidOperationException(name))));
        Assert.Equal("the model reads and saves memories", MemoryMode.Describe("read-write"));
        Assert.Equal("the model reads memories, never saves one", MemoryMode.Describe("read-only"));
        Assert.Equal("no memory: nothing read, nothing saved", MemoryMode.Describe("disabled"));
        Assert.Equal("", MemoryMode.Describe("nope"));
    }

    [Fact]
    public void Resolve_EnabledAndSaves_PerMode_AnUnknownValueReadsAsTheDefault()
    {
        Assert.Equal(MemoryAccess.ReadOnly, MemoryMode.Resolve(new AppSettingsData { MemoryMode = "read-only" }));
        Assert.Equal(MemoryAccess.ReadWrite, MemoryMode.Resolve(new AppSettingsData { MemoryMode = "sometimes" }));
        Assert.True(MemoryMode.Enabled(new AppSettingsData()));
        Assert.True(MemoryMode.Saves(new AppSettingsData()));
        Assert.True(MemoryMode.Enabled(new AppSettingsData { MemoryMode = "read-only" }));
        Assert.False(MemoryMode.Saves(new AppSettingsData { MemoryMode = "read-only" }));
        Assert.False(MemoryMode.Enabled(new AppSettingsData { MemoryMode = "disabled" }));
        Assert.False(MemoryMode.Saves(new AppSettingsData { MemoryMode = "disabled" }));
    }

    [Fact]
    public void ReadOnlyDirective_IsPinned_AndNeverNamesTheSaveTool()
    {
        Assert.Equal(
            "You have a long-term memory that lasts across sessions. " +
            "What you remember arrives as the result of recall_memory at the start of the conversation; call it again when the list is no longer in view.",
            MemoryPrompt.ReadOnlyDirective);
        Assert.DoesNotContain(SaveMemoryTool.ToolName, MemoryPrompt.ReadOnlyDirective);
        Assert.Equal(MemoryPrompt.ReadOnlyDirective, MemoryPrompt.Section(["a"], tools: true, save: false));
        Assert.Equal(MemoryPrompt.Section(["a"], tools: false), MemoryPrompt.Section(["a"], tools: false, save: false));   // tool-free: no save sentence either way
    }

    /// <summary>The user's rule: read-only never offers save_memory, recall_memory stays and opens the conversation; read-write offers both.</summary>
    [Fact]
    public void PrepareTurn_ReadOnly_OffersRecallAlone_AndThePromptHasNoSaveSentence()
    {
        var assistant = new Assistant(new FakeChatClient(), new ConversationHistory(""), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
        var memory = new MemoryStore(_dir);
        string home = Path.Combine(_dir, "profile");

        ChatScreen.PrepareTurn(assistant, memory, ChatScreen.MemoryTools(memory), [], new PersonaFile(home), new OperataFile(home), new VocaliaFile(home), memoryEnabled: true, speechOutput: false, memorySave: false);

        var names = assistant.Tools.Select(t => t.Name).ToList();
        Assert.Contains(RecallMemoryTool.ToolName, names);
        Assert.DoesNotContain(SaveMemoryTool.ToolName, names);
        Assert.Contains(assistant.OpeningCalls, c => c.CallId == Assistant.OpeningMemoryCallId);
        Assert.Contains(MemoryPrompt.ReadOnlyDirective, assistant.History.SystemPrompt);
        Assert.DoesNotContain(MemoryPrompt.SaveSentences, assistant.History.SystemPrompt);

        ChatScreen.PrepareTurn(assistant, memory, ChatScreen.MemoryTools(memory), [], new PersonaFile(home), new OperataFile(home), new VocaliaFile(home), memoryEnabled: true, speechOutput: false);

        Assert.Contains(SaveMemoryTool.ToolName, assistant.Tools.Select(t => t.Name));
        Assert.Contains(MemoryPrompt.Directive, assistant.History.SystemPrompt);
    }

    [Fact]
    public void ToolGroups_ReadOnly_NotesSaveMemory_AndOffersRecall()
    {
        var memory = new MemoryStore(_dir);
        var groups = SystemPromptSummary.ToolGroups([], [], [], ChatScreen.MemoryTools(memory), memoryEnabled: true, memorySave: false);
        var group = groups.Single(g => g.Label == "Memory");

        Assert.True(group.Offered);
        Assert.False(group.Offers(SaveMemoryTool.ToolName));
        Assert.True(group.Offers(RecallMemoryTool.ToolName));
        Assert.Equal(SystemPromptSummary.NotOffered("memory mode is read-only"), group.ToolNotes[SaveMemoryTool.ToolName]);
        Assert.Equal("memory mode is disabled", SystemPromptSummary.MemoryOffSuffix);
        Assert.Equal(SettingsField.MemoryMode, group.Switch);
    }

    /// <summary>/sys' Prompt tab names the mode and its section is the turn's own.</summary>
    [Fact]
    public void PromptSections_ReadOnly_NameTheMode_AndAgreeWithTheTurnsPrompt()
    {
        var facts = new SystemPromptFacts(null, null, null, Memory: true, Memories: ["Their name is Chris."], TtsOutput: false, SpeechReady: false, MemorySave: false);
        var section = SystemPromptSummary.PromptSections(facts).Single(s => s.Label == "Memory");

        Assert.Equal("Memory — read-only, directive (the list rides the opening recall_memory call)", section.Heading);
        Assert.Equal(MemoryPrompt.ReadOnlyDirective, section.Body);
        Assert.Equal(Assistant.SystemPrompt(false, ["Their name is Chris."], skills: [], save: false), SystemPromptSummary.SystemPrompt(facts));
    }
}
