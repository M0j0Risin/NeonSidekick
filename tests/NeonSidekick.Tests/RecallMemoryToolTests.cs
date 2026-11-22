using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;

namespace NeonSidekick.Tests;

public class RecallMemoryToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly MemoryStore _store;
    private readonly RecallMemoryTool _tool;

    public RecallMemoryToolTests()
    {
        _store = new MemoryStore(_dir);
        _tool = new RecallMemoryTool(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Schema_IsAnEmptyObject_AndTheDescriptionIsPinned()
    {
        Assert.Equal("recall_memory", RecallMemoryTool.ToolName);
        Assert.Equal(RecallMemoryTool.ToolName, _tool.Name);
        Assert.Equal(
            "Everything you remember, oldest first: the facts saved with save_memory in any session for everywhere, and those saved for this folder only " +
            "(another folder's are left out). " +
            "Its result opens every conversation; call it again after a save or when the list is no longer in view.",
            _tool.Description);

        var schema = _tool.JsonSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Empty(schema.GetProperty("properties").EnumerateObject());
        Assert.False(schema.TryGetProperty("required", out _));
    }

    [Fact]
    public void Describe_IsTheRecalledList_OrNothingYet()
    {
        Assert.Equal(MemoryPrompt.NothingRemembered, _tool.Describe());

        _store.Add("Their name is Chris.");
        _store.Add("They live in Leeds.");

        Assert.Equal(MemoryPrompt.Heading + "\n- Their name is Chris.\n- They live in Leeds.", _tool.Describe());
    }

    [Fact]
    public async Task Invoke_AnswersDescribe_AsTheStoreStandsNow()
    {
        Assert.Equal(MemoryPrompt.NothingRemembered, await _tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None));

        _store.Add("Their name is Chris.");

        Assert.Equal(MemoryPrompt.Heading + "\n- Their name is Chris.", await _tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None));
    }

    [Fact]
    public void Note_CountsTheBullets_Pinned()
    {
        Assert.Equal("nothing remembered yet", RecallMemoryTool.Note(MemoryPrompt.NothingRemembered));
        Assert.Equal("1 memory recalled", RecallMemoryTool.Note(MemoryPrompt.Recalled(["Their name is Chris."])));
        Assert.Equal("12 memories recalled", RecallMemoryTool.Note(MemoryPrompt.Recalled(Enumerable.Range(1, 12).Select(i => $"Fact {i}.").ToArray())));
        // A memory's own text never counts: only a line break followed by the bullet does.
        Assert.Equal("1 memory recalled", RecallMemoryTool.Note(MemoryPrompt.Recalled(["They write lists like - this - one."])));
        Assert.Throws<ArgumentNullException>(() => RecallMemoryTool.Note(null!));
    }
}
