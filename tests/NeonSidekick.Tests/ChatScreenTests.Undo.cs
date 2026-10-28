using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/undo</c> and <c>/rewind … files</c> on the screen (2026-10-28, the checkpoints plan): a turn's file changes journaled into a
/// <see cref="FakeCheckpointStore"/>, put back after a yes (a file the turn made to the <see cref="FakeRecycleBin"/>), a file changed since
/// asked about, the model told on the next message, and the purges.
/// </summary>
public partial class ChatScreenTests
{
    private string WorkFile(string name) => Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName, name);

    private void CheckpointsOn()
    {
        _settings.Update(d => d.TtsOutput = false);
        _geometry = new ScreenGeometry(() => null);
        _checkpoints = new FakeCheckpointStore();
        _recycleBin = new FakeRecycleBin();
    }

    private static Dictionary<string, object?> WriteArgs(string path, string content) => new() { ["path"] = path, ["content"] = content };

    private static string? UndoNoteIn(IReadOnlyList<ChatMessage> request) =>
        request.SelectMany(m => m.Contents).Where(ConversationHistory.IsUndoNote).OfType<TextContent>().Select(t => t.Text).SingleOrDefault();

    [Fact]
    public async Task Undo_PutsBackTheLastTurnsFiles_AfterAYes_AndTheNextMessageTellsTheModel()
    {
        CheckpointsOn();
        _chat.Enqueue(FakeChatClient.Call("c1", WriteFileTool.ToolName, WriteArgs("note.txt", "hello")))
            .EnqueueText("Written.")
            .EnqueueText("Noted.");
        StepsWhenIdle(
            Line("write a note"),
            Line("/undo"), Key(Keys.Char('y')), Key(Keys.Enter),
            Line("what now"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(UndoText.Question(1), output);
        Assert.False(File.Exists(WorkFile("note.txt")));
        Assert.Equal([WorkFile("note.txt")], _recycleBin!.Moved);
        Assert.Empty(_checkpoints!.Records);   // spent
        string note = UndoNoteIn(_chat.Requests[^1])!;
        Assert.StartsWith(UndoText.ModelNote(["write a note"], [], [])[..^1], note, StringComparison.Ordinal);
        Assert.Contains("note.txt", note, StringComparison.Ordinal);
        Assert.Null(UndoNoteIn(_chat.Requests[1]));
    }

    [Fact]
    public async Task Undo_ANo_KeepsTheFiles_AndTheCheckpoint()
    {
        CheckpointsOn();
        _chat.Enqueue(FakeChatClient.Call("c1", WriteFileTool.ToolName, WriteArgs("note.txt", "hello"))).EnqueueText("Written.");
        StepsWhenIdle(Line("write a note"), Line("/undo"), Key(Keys.Enter), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("  · " + ChatScreen.KeptNotice, output);
        Assert.Equal("hello", File.ReadAllText(WorkFile("note.txt")));
        Assert.Single(_checkpoints!.Records);
    }

    [Fact]
    public async Task Undo_WithNothingToUndo_SaysSo_NamingTheSettingWhileItIsOff()
    {
        CheckpointsOn();
        _chat.EnqueueText("Hi.");
        StepsWhenIdle(Line("hello"), Line("/undo"), Line("/undo 0"), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(UndoText.NothingToUndo, output);
        Assert.Contains(UndoText.UsageError, output);
        Assert.Empty(_checkpoints!.Begun);   // a turn that changed no file opens no store
    }

    [Fact]
    public async Task Undo_WithTheSettingOff_JournalsNothing()
    {
        CheckpointsOn();
        _settings.Update(d => d.FileCheckpoints = false);
        _chat.Enqueue(FakeChatClient.Call("c1", WriteFileTool.ToolName, WriteArgs("note.txt", "hello"))).EnqueueText("Written.");
        StepsWhenIdle(Line("write a note"), Line("/undo"), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(UndoText.NothingNotice(off: true), output);
        Assert.Empty(_checkpoints!.Begun);
        Assert.True(File.Exists(WorkFile("note.txt")));
    }

    [Fact]
    public async Task Undo_AFileChangedSince_IsAsked_SkipLeavesYours()
    {
        CheckpointsOn();
        _chat.Enqueue(FakeChatClient.Call("c1", WriteFileTool.ToolName, WriteArgs("note.txt", "hello"))).EnqueueText("Written.");
        StepsWhenIdle(
            Line("write a note"),
            input =>
            {
                File.WriteAllText(WorkFile("note.txt"), "mine");
                PushLine(input, "/undo");
            },
            Key(Keys.Char('y')), Key(Keys.Enter),
            Key(Keys.Char('s')), Key(Keys.Enter),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(UndoText.ConflictQuestion(1), output);
        Assert.Equal("mine", File.ReadAllText(WorkFile("note.txt")));
        Assert.Empty(_recycleBin!.Moved);
    }

    [Fact]
    public async Task Undo_AFileChangedSince_RestoreOver_BinsYoursFirst()
    {
        CheckpointsOn();
        _chat.Enqueue(FakeChatClient.Call("c1", WriteFileTool.ToolName, WriteArgs("note.txt", "hello"))).EnqueueText("Written.");
        StepsWhenIdle(
            Line("write a note"),
            input =>
            {
                File.WriteAllText(WorkFile("note.txt"), "mine");
                PushLine(input, "/undo");
            },
            Key(Keys.Char('y')), Key(Keys.Enter),
            Key(Keys.Char('r')), Key(Keys.Enter),
            Line("/exit"));

        await RunAsync();

        Assert.False(File.Exists(WorkFile("note.txt")));
        Assert.NotEmpty(_recycleBin!.Moved);
        Assert.Empty(_checkpoints!.Records);
    }

    [Fact]
    public async Task Undo_TwoInARow_MergeOneNote_AndClearDropsIt()
    {
        CheckpointsOn();
        _chat.Enqueue(FakeChatClient.Call("c1", WriteFileTool.ToolName, WriteArgs("a.txt", "a"))).EnqueueText("A.")
            .Enqueue(FakeChatClient.Call("c2", WriteFileTool.ToolName, WriteArgs("b.txt", "b"))).EnqueueText("B.")
            .EnqueueText("Merged.")
            .Enqueue(FakeChatClient.Call("c3", WriteFileTool.ToolName, WriteArgs("c.txt", "c"))).EnqueueText("C.")
            .EnqueueText("Fresh.");
        StepsWhenIdle(
            Line("make a"), Line("make b"),
            Line("/undo"), Key(Keys.Char('y')), Key(Keys.Enter),
            Line("/undo"), Key(Keys.Char('y')), Key(Keys.Enter),
            Line("both gone?"),
            Line("make c"),
            Line("/undo"), Key(Keys.Char('y')), Key(Keys.Enter),
            Line("/clear"),
            Line("fresh start"),
            Line("/exit"));

        await RunAsync();

        var merged = _chat.Requests.First(r => r.Any(m => m.Text.Contains("both gone?", StringComparison.Ordinal)));
        string note = UndoNoteIn(merged)!;
        Assert.Contains("\"make b\"", note, StringComparison.Ordinal);
        Assert.Contains("; then the file changes you made answering \"make a\"", note, StringComparison.Ordinal);
        Assert.Null(UndoNoteIn(_chat.Requests[^1]));   // /clear dropped c's note
        Assert.False(File.Exists(WorkFile("a.txt")) || File.Exists(WorkFile("b.txt")) || File.Exists(WorkFile("c.txt")));
    }

    [Fact]
    public async Task Rewind_ConversationAndFiles_PutsTheFilesBack_ConversationOnlyLeavesThem_AndDropsTheRefs()
    {
        CheckpointsOn();
        _chat.EnqueueText("One.")
            .Enqueue(FakeChatClient.Call("c1", WriteFileTool.ToolName, WriteArgs("a.txt", "a"))).EnqueueText("A.")
            .EnqueueText("Again.")
            .Enqueue(FakeChatClient.Call("c2", WriteFileTool.ToolName, WriteArgs("b.txt", "b"))).EnqueueText("B.")
            .EnqueueText("Again too.");
        StepsWhenIdle(
            Line("one"), Line("make a"),
            Line("/rewind"), Key(Keys.Enter), Key(Keys.Char('f')), Key(Keys.Enter),
            Key(Keys.Enter),   // the draft sent again
            Line("make b"),
            Line("/rewind"), Key(Keys.Enter), Key(Keys.Char('c')), Key(Keys.Enter),
            Key(Keys.Enter),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(RewindText.FilesRows[0], output);
        Assert.False(File.Exists(WorkFile("a.txt")));
        Assert.Equal("b", File.ReadAllText(WorkFile("b.txt")));   // conversation only
        Assert.Empty(_checkpoints!.Records);   // both turns' refs gone: one spent, one dropped with its turn
        Assert.Equal(2, _checkpoints.Removed.Count);
    }

    [Fact]
    public async Task UndoPurge_AfterAYes_PurgesThisFolder_ThenUndoSaysNoLongerKept()
    {
        CheckpointsOn();
        _chat.Enqueue(FakeChatClient.Call("c1", WriteFileTool.ToolName, WriteArgs("note.txt", "hello"))).EnqueueText("Written.");
        StepsWhenIdle(
            Line("write a note"),
            Line("/undo purge"), Key(Keys.Char('y')), Key(Keys.Enter),
            Line("/undo"),
            Line("/exit"));

        string output = await RunAsync();

        var roots = Assert.Single(_checkpoints!.Purges);
        Assert.NotNull(roots);
        Assert.Contains(Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName), roots!, WorkingDirectory.PathComparer);
        Assert.Contains(UndoText.NoLongerKept, output);
        Assert.True(File.Exists(WorkFile("note.txt")));
    }

    [Fact]
    public async Task UndoPurge_WithNothingKept_AsksNothing()
    {
        CheckpointsOn();
        StepsWhenIdle(Line("/undo purge all"), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(UndoText.NothingToPurge, output);
        Assert.Empty(_checkpoints!.Purges);
    }
}
