using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;

namespace NeonSidekick.Tests;

public class SaveMemoryToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly MemoryStore _store;
    private readonly SaveMemoryTool _tool;

    public SaveMemoryToolTests()
    {
        _store = new MemoryStore(_dir);
        _tool = new SaveMemoryTool(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static AIFunctionArguments Args(object? text) =>
        new(new Dictionary<string, object?> { ["text"] = text });

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Schema_IsAnObjectWithOneRequiredStringProperty_MatchingTheKeyRead()
    {
        Assert.Equal("save_memory", SaveMemoryTool.ToolName);
        Assert.Equal(SaveMemoryTool.ToolName, _tool.Name);
        Assert.Contains("long-term memory", _tool.Description);

        var schema = _tool.JsonSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var properties = schema.GetProperty("properties");
        Assert.Single(properties.EnumerateObject());
        Assert.Equal("string", properties.GetProperty("text").GetProperty("type").GetString());
        Assert.Contains("third person", properties.GetProperty("text").GetProperty("description").GetString());
        Assert.Equal(new[] { "text" }, schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());

        // The key the schema declares is the key the invocation reads.
        Assert.Equal("x", SaveMemoryTool.ReadText(Args("x")));
    }

    [Fact]
    public async Task Invoke_WithAJsonString_SavesAndAnswersRemembered()
    {
        object? answer = await _tool.InvokeAsync(Args(Json("\"Their name is Chris.\"")), CancellationToken.None);

        Assert.Equal("remembered: Their name is Chris.", answer);
        Assert.Equal(new[] { "Their name is Chris." }, _store.Snapshot());
        Assert.True(File.Exists(_store.FilePath));
    }

    [Fact]
    public async Task Invoke_WithAPlainString_SavesToo()
    {
        object? answer = await _tool.InvokeAsync(Args("They live in Leeds."), CancellationToken.None);

        Assert.Equal("remembered: They live in Leeds.", answer);
        Assert.Equal(new[] { "They live in Leeds." }, _store.Snapshot());
    }

    [Fact]
    public async Task Invoke_Twice_AnswersAlreadyRemembered()
    {
        await _tool.InvokeAsync(Args("Their name is Chris."), CancellationToken.None);

        object? answer = await _tool.InvokeAsync(Args("their name is chris."), CancellationToken.None);

        Assert.Equal("already remembered: Their name is Chris.", answer);
        Assert.Equal(1, _store.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Invoke_WithoutText_AnswersEmpty_AndSavesNothing(string? text)
    {
        object? answer = await _tool.InvokeAsync(text is null ? new AIFunctionArguments() : Args(text), CancellationToken.None);

        Assert.Equal("nothing to remember: the text was empty", answer);
        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public async Task Invoke_WithAJsonNull_AnswersEmpty()
    {
        object? answer = await _tool.InvokeAsync(Args(Json("null")), CancellationToken.None);

        Assert.Equal("nothing to remember: the text was empty", answer);
    }

    [Fact]
    public async Task Invoke_WithANonStringJsonValue_SavesItsRawText()
    {
        object? answer = await _tool.InvokeAsync(Args(Json("42")), CancellationToken.None);

        Assert.Equal("remembered: 42", answer);
    }

    [Fact]
    public void Describe_IsPinned()
    {
        Assert.Equal("remembered: x", SaveMemoryTool.Describe(new(MemoryAddOutcome.Added, "x")));
        Assert.Equal("already remembered: x", SaveMemoryTool.Describe(new(MemoryAddOutcome.Duplicate, "x")));
        Assert.Equal("memory is full (200 entries apply in a folder); the user can clear it with /memory forget", SaveMemoryTool.Describe(new(MemoryAddOutcome.Full, "x")));
        Assert.Equal("memory is full (the memory file holds 1,000 entries); the user can clear it with /memory forget", SaveMemoryTool.Describe(new(MemoryAddOutcome.Full, "x", FileFull: true)));
        Assert.Equal("nothing to remember: the text was empty", SaveMemoryTool.Describe(new(MemoryAddOutcome.Empty, "")));
        Assert.Equal("could not save the memory (the file could not be written); tell the user", SaveMemoryTool.Describe(new(MemoryAddOutcome.Failed, "x")));
    }
}
