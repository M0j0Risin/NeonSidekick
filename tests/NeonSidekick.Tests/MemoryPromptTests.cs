using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;

namespace NeonSidekick.Tests;

public class MemoryPromptTests
{
    [Fact]
    public void Directive_IsPinned_AndNamesBothTools()
    {
        Assert.Equal(
            "You have a long-term memory that lasts across sessions. " +
            "When the user tells you a lasting fact about themselves (their name, where they live, what they like, what they are working on) " +
            "or asks you to remember something, call save_memory with one short sentence in the third person, then continue your reply. " +
            "Do not save passing details, and do not save anything you already remember. " +
            "What you remember arrives as the result of recall_memory at the start of the conversation; call it again when the list is no longer in view.",
            MemoryPrompt.Directive);
        Assert.Contains(SaveMemoryTool.ToolName, MemoryPrompt.Directive);
        Assert.Contains(RecallMemoryTool.ToolName, MemoryPrompt.Directive);
        Assert.Equal("What you remember about the user, oldest first:", MemoryPrompt.Heading);
        Assert.Equal("Nothing remembered about the user yet.", MemoryPrompt.NothingRemembered);
    }

    [Fact]
    public void Section_WithTools_IsTheDirectiveAlone_WhateverIsStored()
    {
        // The list rides the opening recall_memory pair (2026-09-17), never the tooled prompt.
        Assert.Equal(MemoryPrompt.Directive, MemoryPrompt.Section(Array.Empty<string>()));
        Assert.Equal(MemoryPrompt.Directive, MemoryPrompt.Section(new[] { "Their name is Chris.", "They live in Leeds." }));
        Assert.Equal(MemoryPrompt.Section(["Their name is Chris."]), MemoryPrompt.Section(["Their name is Chris."], tools: true));
    }

    [Fact]
    public void Recalled_ListsEveryMemory_OldestFirst_OneBulletEach_OrSaysNothingYet()
    {
        Assert.Equal(MemoryPrompt.NothingRemembered, MemoryPrompt.Recalled([]));
        Assert.Equal(
            MemoryPrompt.Heading + "\n- Their name is Chris.\n- They live in Leeds.",
            MemoryPrompt.Recalled(new[] { "Their name is Chris.", "They live in Leeds." }));
    }

    [Fact]
    public void WithoutTheTool_TheDirectiveIsItsFirstSentence_AndTheListFollowsInThePrompt()
    {
        Assert.Equal("You have a long-term memory that lasts across sessions.", MemoryPrompt.DirectiveWithoutTool);
        Assert.StartsWith(MemoryPrompt.DirectiveWithoutTool + " ", MemoryPrompt.Directive, StringComparison.Ordinal);
        Assert.DoesNotContain(SaveMemoryTool.ToolName, MemoryPrompt.DirectiveWithoutTool);
        Assert.DoesNotContain(RecallMemoryTool.ToolName, MemoryPrompt.DirectiveWithoutTool);
        Assert.Equal(MemoryPrompt.DirectiveWithoutTool, MemoryPrompt.Section([], tools: false));
        Assert.Equal(
            MemoryPrompt.DirectiveWithoutTool + "\n\n" + MemoryPrompt.Heading + "\n- Their name is Chris.",
            MemoryPrompt.Section(["Their name is Chris."], tools: false));
    }

    [Fact]
    public void NullList_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MemoryPrompt.Section(null!));
        Assert.Throws<ArgumentNullException>(() => MemoryPrompt.Recalled(null!));
    }

    /// <summary>The split of 2026-10-04 keeps the directive byte for byte.</summary>
    [Fact]
    public void Directive_IsTheThreeParts()
    {
        Assert.Equal(MemoryPrompt.DirectiveWithoutTool + " " + MemoryPrompt.SaveSentences + " " + MemoryPrompt.RecallSentence, MemoryPrompt.Directive);
        Assert.StartsWith("When the user tells you a lasting fact", MemoryPrompt.SaveSentences);
        Assert.StartsWith("What you remember arrives as the result of recall_memory", MemoryPrompt.RecallSentence);
    }

    /// <summary>The bots' section (2026-10-04): the list always, the save sentences only with save_memory offered.</summary>
    [Fact]
    public void ListedSection_TheListWhateverTheTools_TheSaveSentencesWithSave()
    {
        Assert.Equal(MemoryPrompt.DirectiveWithoutTool + " " + MemoryPrompt.SaveSentences + "\n\n" + MemoryPrompt.Heading + "\n- a\n- b", MemoryPrompt.ListedSection(["a", "b"], save: true));
        Assert.Equal(MemoryPrompt.DirectiveWithoutTool + "\n\n" + MemoryPrompt.NothingRemembered, MemoryPrompt.ListedSection([], save: false));
    }
}
