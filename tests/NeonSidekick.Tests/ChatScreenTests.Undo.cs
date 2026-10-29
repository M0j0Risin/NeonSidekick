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
        request.Select(ConversationHistory.UndoNoteOf).SingleOrDefault(n => n is not null);

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

        Assert.Contains(UndoText.Question(["write a note"]), output);
        Assert.False(File.Exists(WorkFile("note.txt")));
        Assert.Equal([WorkFile("note.txt")], _recycleBin!.Moved);
        Assert.Empty(_checkpoints!.Records);   // spent
        string note = UndoNoteIn(_chat.Requests[^1])!;
        Assert.StartsWith(UndoText.ModelNote([UndoText.NoteClause(["write a note"], [], [], [], partly: false)])[..^1], note, StringComparison.Ordinal);
        // The note rides in the user's own text (2026-10-28, review P10-01: a part of its own made a two-part array some servers refuse).
        Assert.Equal(note + ConversationHistory.NoteBreak + "what now", _chat.Requests[^1].Last(ConversationHistory.IsTurnStart).Text);
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

    /// <summary>
    /// The question quotes the turn it undoes, never "the last turn" when that one changed nothing (2026-10-28, review P4-07), and one
    /// <c>/undo</c> looks its checkpoints up once (P1-12: <c>Find</c> ran for the plan and again for the stamps).
    /// </summary>
    [Fact]
    public async Task Undo_QuotesTheTurnThatWrote_PastAChat_AndFindsOnce()
    {
        CheckpointsOn();
        _chat.Enqueue(FakeChatClient.Call("c1", WriteFileTool.ToolName, WriteArgs("note.txt", "hello"))).EnqueueText("Written.")
            .EnqueueText("Just chat.");
        StepsWhenIdle(Line("write a note"), Line("how are you"), Line("/undo"), Key(Keys.Enter), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(UndoText.Question(["write a note"]), output);
        Assert.Equal(1, _checkpoints!.Finds);
        Assert.Single(_checkpoints.Records);   // a no keeps it
    }

    /// <summary>Newer turns whose checkpoints are gone are said, and <c>/undo</c> reaches the one still kept (2026-10-28, review P1-10).</summary>
    [Fact]
    public async Task Undo_PastNewerTurnsNoLongerKept_SaysSo_AndUndoesTheKeptOne()
    {
        CheckpointsOn();
        _chat.Enqueue(FakeChatClient.Call("c1", WriteFileTool.ToolName, WriteArgs("a.txt", "a"))).EnqueueText("A.")
            .Enqueue(FakeChatClient.Call("c2", WriteFileTool.ToolName, WriteArgs("b.txt", "b"))).EnqueueText("B.");
        StepsWhenIdle(
            Line("make a"), Line("make b"),
            input =>
            {
                _checkpoints!.Records.RemoveAt(_checkpoints.Records.Count - 1);   // b's checkpoint cleared
                PushLine(input, "/undo");
            },
            Key(Keys.Char('y')), Key(Keys.Enter),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(UndoText.NewerNotKept(1), output);
        Assert.Contains(UndoText.Question(["make a"]), output);
        Assert.False(File.Exists(WorkFile("a.txt")));
        Assert.True(File.Exists(WorkFile("b.txt")));
    }

    /// <summary>The conflict choice opens on "Skip them": Enter leaves your file as it is (2026-10-28, review P7-03).</summary>
    [Fact]
    public async Task Undo_AFileChangedSince_EnterSkips()
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
            Key(Keys.Enter),
            Line("/exit"));

        await RunAsync();

        Assert.Equal("mine", File.ReadAllText(WorkFile("note.txt")));
        Assert.Empty(_recycleBin!.Moved);
    }

    /// <summary>
    /// The rewind's files choice opens on Cancel: Enter rewinds nothing and keeps the files and their checkpoint (2026-10-28, review P7-01:
    /// it opened on "Conversation and files"): back to the turns' list.
    /// </summary>
    [Fact]
    public async Task Rewind_TheFilesChoice_Enter_Cancels()
    {
        CheckpointsOn();
        _chat.EnqueueText("One.")
            .Enqueue(FakeChatClient.Call("c1", WriteFileTool.ToolName, WriteArgs("a.txt", "a"))).EnqueueText("A.");
        StepsWhenIdle(Line("one"), Line("make a"), Line("/rewind"), Key(Keys.Enter), Key(Keys.Enter), Key(Keys.Escape), Line("/exit"));   // Cancel goes back to the list; Esc closes it

        string output = await RunAsync();

        Assert.Contains(RewindText.ConversationOnlyNote, output);
        Assert.Equal("a", File.ReadAllText(WorkFile("a.txt")));
        Assert.Single(_checkpoints!.Records);
        Assert.Empty(_checkpoints.Removed);
        Assert.Equal(3, _chat.Requests.Count);   // nothing sent again
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
