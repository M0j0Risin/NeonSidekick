using Microsoft.Extensions.AI;
using NeonSidekick.CustomCommands;

namespace NeonSidekick.Tests;

/// <summary>
/// Custom commands headless (2026-10-30, plan i-want-to-talk-golden-sunbeam): <c>/name</c> sends its filled prompt under a <c>[notice]</c>,
/// never parsed as a command again; <c>/commands</c> prints its lines, <c>new</c> and <c>save</c> print the path (no editor here);
/// <c>/help name</c> prints the card with no model asked.
/// </summary>
public partial class SidekickAppTests
{
    private string WriteHeadlessCommand(string name, string text)
    {
        Directory.CreateDirectory(_settings.ProfileCustomCommandsDirectory);
        string path = Path.Combine(_settings.ProfileCustomCommandsDirectory, name + ".md");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task Headless_CustomCommand_SendsItsPrompt_UnderTheNotice()
    {
        WriteHeadlessCommand("review", "---\ndescription: review a file\n---\nReview $0 for bugs.");
        ServerOn1234("llama");
        _chat.EnqueueText("Reviewed.");

        string output = await Headless("/review a.cs\n");

        Assert.Equal("Review a.cs for bugs.", Assert.Single(_chat.Requests).Last(m => m.Role == ChatRole.User).Text);
        Assert.Contains("[notice] (" + CustomCommandText.Glyph + " /review from the profile's commands)", output);
        Assert.Contains("Reviewed.", output);
        using var store = new NeonSidekick.Sessions.SessionStore(_settings.ProfileDirectory);
        Assert.Equal("/review a.cs", Assert.Single(store.List(0)).Title);   // titled by the typed line, never the prompt
    }

    [Fact]
    public async Task Headless_CustomCommand_ABodyStartingWithACommand_ReachesTheModelAsText()
    {
        WriteHeadlessCommand("planit", "/plan $ARGUMENTS");
        ServerOn1234("llama");
        _chat.EnqueueText("Planned.");

        await Headless("/planit the release\n");

        Assert.Equal("/plan the release", Assert.Single(_chat.Requests).Last(m => m.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task Headless_CommandsNewAndSave_PrintThePath_TheModelAskedForTheMessageAlone()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Done.");

        string output = await Headless("/commands new triage\nSummarize this.\n/commands save sum\n/commands save sum\n/commands\n");

        string triage = Path.Combine(_settings.ProfileCustomCommandsDirectory, "triage.md");
        string sum = Path.Combine(_settings.ProfileCustomCommandsDirectory, "sum.md");
        Assert.Contains("[notice] " + CustomCommandText.CreatedNotice("triage", triage), output);
        Assert.Contains("[notice] " + CustomCommandText.SavedNotice("sum", sum, "Summarize this."), output);
        Assert.Contains("[error] " + CustomCommandText.ExistsError("sum", CustomCommandScope.Profile), output);
        Assert.Contains("Summarize this.", File.ReadAllText(sum));
        Assert.Contains("[notice]   /sum  Summarize this. · profile", output);   // the list's line: no description, the body's first line
        Assert.Single(_chat.Requests);
    }

    [Fact]
    public async Task Headless_HelpWithACommandsWord_PrintsTheCard_NoModelAsked()
    {
        string path = WriteHeadlessCommand("review", "---\ndescription: review a file\n---\nReview it.");

        string output = await Headless("/help review\n");

        Assert.Empty(_chat.Requests);
        Assert.Contains("Neon: /review — your custom command, from the profile's commands.", output);
        Assert.Contains(path, output);
        Assert.Contains(CustomCommandText.ManualWayOut("/review"), output);   // 2026-10-31, the user's call
    }

    /// <summary>
    /// Headless's card is plain text (2026-10-31, review P3-6): a description's underscores print as written; a Skipped file's name prints its
    /// reason (review P4-6).
    /// </summary>
    [Fact]
    public async Task Headless_TheCardIsPlain_AndASkippedFileHasOne()
    {
        WriteHeadlessCommand("init", "---\ndescription: makes __init__ files\n---\nMake them.");
        WriteHeadlessCommand("broken", "---\ndescription: x\n---\n");

        string output = await Headless("/help init\n/help broken\n");

        Assert.Empty(_chat.Requests);
        Assert.Contains("makes __init__ files", output);
        Assert.DoesNotContain("\\_", output);
        Assert.Contains(CustomCommandText.SkippedCardHead, output);
        Assert.Contains(CustomCommandText.EmptyBodyProblem, output);
    }

    /// <summary>
    /// A saved command runs at the next line (2026-10-31, review P3-8: the catalog's reuse window read it as unknown), and saving a custom
    /// command's own message is refused (review P1-5, the user's call).
    /// </summary>
    [Fact]
    public async Task Headless_ASavedCommand_RunsAtOnce_AndItsOwnMessageIsNotSavedAgain()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Done.").EnqueueText("Again.");

        string output = await Headless("Summarize this.\n/commands save sum\n/sum\n/commands save again\n");

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal("Summarize this.", _chat.Requests[1].Last(m => m.Role == ChatRole.User).Text);
        Assert.Contains("[error] " + CustomCommandText.FromCustomCommandError("/sum"), output);
        Assert.False(File.Exists(Path.Combine(_settings.ProfileCustomCommandsDirectory, "again.md")));
    }
}
