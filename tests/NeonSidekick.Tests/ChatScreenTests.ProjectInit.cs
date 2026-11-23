using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.ProjectInit;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/project-init</c> on the screen (2026-11-23): one narrowed turn that calls <c>present_project_notes</c>, the Write pane inside the call,
/// the file on disk after Write, nothing after Esc, the no-draft notice, a new conversation after a chat, and the plan-mode refusal.
/// </summary>
public partial class ChatScreenTests
{
    private const string NotesDraft = "# Notes\n\nBuild with `dotnet build`.\n";

    private string NotesRoot => Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName);

    private string NotesPath => Path.Combine(NotesRoot, ProjectFile.PrimaryFileName);

    /// <summary>The working folder with a README, the pane on; <paramref name="answers"/> answer the panes the turn opens.</summary>
    private void ProjectInitFixture(ConsoleKeyInfo[][] answers, params Action<ScriptedInput>[] steps)
    {
        Directory.CreateDirectory(NotesRoot);
        File.WriteAllText(Path.Combine(NotesRoot, "README.md"), "# Demo\n\nRun `dotnet test` to test.\n");
        PlanGapsFixture(answers, steps);
    }

    private static ChatResponseUpdate NotesCall(string id, string content = NotesDraft) =>
        FakeChatClient.Call(id, PresentProjectNotesTool.ToolName, new Dictionary<string, object?> { [ProjectInitText.ContentArgument] = content });

    [Fact]
    public async Task ProjectInit_Write_PutsTheDraftOnDisk_InANarrowedTurn()
    {
        ProjectInitFixture([[Keys.Char('w'), Keys.Enter]], Line("/project-init build"), Line("/exit"));
        _chat.Enqueue(NotesCall("n1"));
        _chat.EnqueueText("Wrote NEON.md.");

        string output = await RunAsync();

        Assert.Equal(NotesDraft, File.ReadAllText(NotesPath).ReplaceLineEndings("\n"));
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal(ProjectInitText.Message("build"), LastUserText(_chat.Requests[0]));
        string system = _chat.Requests[0][0].Text;
        Assert.Contains("# Drafting this folder's project notes (/project-init)", system);
        Assert.Contains("## The survey", system);
        Assert.Contains("Run `dotnet test` to test.", system);   // the README rides the survey
        var tools = OfferedNames(_chat.Options[0]);
        Assert.Contains(PresentProjectNotesTool.ToolName, tools);
        Assert.Contains(ReadFileTool.ToolName, tools);
        Assert.DoesNotContain(WriteFileTool.ToolName, tools);
        Assert.DoesNotContain(RunCommandTool.ToolName, tools);
        Assert.DoesNotContain(PresentPlanTool.ToolName, tools);
        Assert.DoesNotContain("recall_memory", tools);
        Assert.Contains(ProjectInitText.NotesTitle, output);
        Assert.Contains(ProjectInitText.WroteNotice(ProjectFile.PrimaryFileName, NotesDraft.Length, null), output);
        Assert.Contains(ProjectInitText.WrittenResult(ProjectFile.PrimaryFileName), ToolResultText(_chat.Requests[1]));
    }

    [Fact]
    public async Task ProjectInit_EscOnThePane_WritesNothing()
    {
        ProjectInitFixture([[Keys.Escape]], Line("/project-init"), Line("/exit"));
        _chat.Enqueue(NotesCall("n1"));
        _chat.EnqueueText("Left as it was.");

        string output = await RunAsync();

        Assert.False(File.Exists(NotesPath));
        Assert.Contains(ProjectInitText.CanceledNotice(ProjectFile.PrimaryFileName), output);
        Assert.Contains(ProjectInitText.CanceledResult, ToolResultText(_chat.Requests[1]));
    }

    [Fact]
    public async Task ProjectInit_UndoTakesTheWriteBack()
    {
        CheckpointsOn();
        ProjectInitFixture([[Keys.Char('w'), Keys.Enter]], Line("/project-init"), Line("/undo"), Key(Keys.Char('y')), Key(Keys.Enter), Line("/exit"));
        _chat.Enqueue(NotesCall("n1"));
        _chat.EnqueueText("Wrote NEON.md.");

        await RunAsync();

        Assert.False(File.Exists(NotesPath));
        Assert.Equal([NotesPath], _recycleBin!.Moved);
    }

    /// <summary>A canceled draft changed nothing, so <c>/undo</c> has nothing to list (feature idea #64's <c>NothingRan</c>).</summary>
    [Fact]
    public async Task ProjectInit_ACanceledDraft_LeavesNothingToUndo()
    {
        CheckpointsOn();
        ProjectInitFixture([[Keys.Escape]], Line("/project-init"), Line("/undo"), Line("/exit"));
        _chat.Enqueue(NotesCall("n1"));
        _chat.EnqueueText("Left as it was.");

        string output = await RunAsync();

        Assert.Contains(UndoText.NothingToUndo, output);
        Assert.DoesNotContain("Not undone", output);
    }

    [Fact]
    public async Task ProjectInit_ATurnWithoutTheCall_SaysSo()
    {
        ProjectInitFixture([], Line("/project-init"), Line("/exit"));
        _chat.EnqueueText("Here are some notes, but I didn't call the tool.");

        string output = await RunAsync();

        Assert.False(File.Exists(NotesPath));
        Assert.Contains(ProjectInitText.NoDraftNotice, output);
    }

    [Fact]
    public async Task ProjectInit_AfterAChat_StartsANewConversation()
    {
        ProjectInitFixture([[Keys.Escape]], Line("remember the word kumquat"), Line("/project-init"), Line("/exit"));
        _chat.EnqueueText("Noted.");
        _chat.Enqueue(NotesCall("n1"));
        _chat.EnqueueText("Canceled.");

        string output = await RunAsync();

        Assert.Contains(ProjectInitText.NewConversationNotice, output);
        Assert.DoesNotContain(_chat.Requests[1], m => m.Text.Contains("kumquat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectInit_FirstThing_StartsNoNewConversation()
    {
        ProjectInitFixture([[Keys.Escape]], Line("/project-init"), Line("/exit"));
        _chat.Enqueue(NotesCall("n1"));
        _chat.EnqueueText("Canceled.");

        string output = await RunAsync();

        Assert.DoesNotContain(ProjectInitText.NewConversationNotice, output);
    }

    [Fact]
    public async Task ProjectInit_IsRefused_InPlanMode()
    {
        ProjectInitFixture([], Line("/plan Add a guide"), Line("/project-init"), Line("/exit"));
        _chat.EnqueueText("What should it cover?");

        string output = await RunAsync();

        Assert.Single(_chat.Requests);   // only /plan's turn
        Assert.Contains(ProjectInitText.PlanningError, output);
    }

    [Fact]
    public async Task ProjectInit_IsRefused_WithOfferToolsOff()
    {
        ProjectInitFixture([], Line("/project-init"), Line("/exit"));
        _settings.Update(d => d.LlmOfferTools = false);

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Contains(ProjectInitText.NeedsToolsError, output);
    }

    [Fact]
    public async Task ProjectInit_IsRefused_WithReadFileOff()
    {
        ProjectInitFixture([], Line("/project-init"), Line("/exit"));
        _settings.Update(d => d.ToolsDisabled = [ReadFileTool.ToolName]);

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Contains(ProjectInitText.NeedsReadFileError, output);
    }

    /// <summary>A Revise, then a turn with no new draft (2026-11-23, review #79/#97): the no-revised-draft notice, not silence.</summary>
    [Fact]
    public async Task ProjectInit_AReviseWithNoNewDraft_SaysSo()
    {
        ProjectInitFixture([[Keys.Char('r'), Keys.Enter, Keys.Enter]], Line("/project-init"), Line("/exit"));
        _chat.Enqueue(NotesCall("n1"));
        _chat.EnqueueText("I'll leave it there.");

        string output = await RunAsync();

        Assert.False(File.Exists(NotesPath));
        Assert.Contains(ProjectInitText.NoRevisedDraftNotice, output);
        Assert.DoesNotContain(ProjectInitText.NoDraftNotice, output);
    }

    /// <summary>A failed write (2026-11-23, review #91/#97) shows its own error once: no model-facing twin, no no-draft notice after it.</summary>
    [Fact]
    public async Task ProjectInit_AFailedWrite_ShowsItsErrorOnce()
    {
        ProjectInitFixture([[Keys.Char('w'), Keys.Enter]], Line("/project-init"), Line("/exit"));
        File.WriteAllText(NotesPath, "# Old\n");
        File.SetAttributes(NotesPath, FileAttributes.ReadOnly);
        try
        {
            _chat.Enqueue(NotesCall("n1"));
            _chat.EnqueueText("Could not write it.");

            string output = await RunAsync();

            Assert.Equal("# Old\n", File.ReadAllText(NotesPath));
            Assert.Contains("/project-init couldn't write the notes:", output);
            Assert.DoesNotContain("Tell the user in one line and stop.", output);
            Assert.DoesNotContain(ProjectInitText.NoRevisedDraftNotice, output);
            Assert.DoesNotContain(ProjectInitText.NoDraftNotice, output);
        }
        finally
        {
            File.SetAttributes(NotesPath, FileAttributes.Normal);
        }
    }

    /// <summary>Every tool result's text in a request, joined.</summary>
    private static string ToolResultText(IReadOnlyList<ChatMessage> request) =>
        string.Join("\n", request.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString() ?? ""));}
