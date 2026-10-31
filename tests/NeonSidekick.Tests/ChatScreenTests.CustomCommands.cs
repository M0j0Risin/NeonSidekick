using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.CustomCommands;
using NeonSidekick.Help;
using NeonSidekick.Prompts;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// Custom slash commands on the screen (2026-10-30, plan i-want-to-talk-golden-sunbeam): a file in the profile's or the home's
/// <c>commands</c> folder typed as <c>/name</c> sends its body filled from the words after it, under the 🪄 notice; a built-in always
/// wins; under a reply it runs when the reply ends; <c>/help</c> has a Custom tab and a card; <c>/commands new</c> and <c>save</c> write files.
/// </summary>
public sealed partial class ChatScreenTests
{
    private string WriteCustomCommand(string name, string text, bool global = false)
    {
        string root = global ? _settings.GlobalCustomCommandsDirectory : _settings.ProfileCustomCommandsDirectory;
        string path = Path.Combine(root, name.Replace(':', Path.DirectorySeparatorChar) + ".md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private const string ReviewFile = "---\ndescription: review a file\nargument-hint: <file> [focus]\n---\nReview $0 with care, looking at $1.";

    private static CustomCommand ReviewCommand(string path) =>
        new("review", CustomCommandScope.Profile, path, "review a file", "<file> [focus]", [], null, "Review $0 with care, looking at $1.");

    [Fact]
    public async Task CustomCommand_Typed_SendsItsFilledPrompt_UnderTheNotice()
    {
        _settings.Update(d => d.TtsOutput = false);
        string path = WriteCustomCommand("review", ReviewFile);
        _chat.EnqueueText("Reviewed.");
        PushLine("/review src/x.cs bugs");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal("Review src/x.cs with care, looking at bugs.", UserText(Assert.Single(_chat.Requests)));
        Assert.Contains("› /review src/x.cs bugs", output);   // the typed line is the user row, never the prompt
        Assert.Contains("  · " + CustomCommandText.SentNotice(ReviewCommand(path)), output);
        Assert.DoesNotContain("› Review src/x.cs", output);
    }

    [Fact]
    public async Task CustomCommand_TitlesANewSession_WithTheTypedLine()
    {
        _settings.Update(d => d.TtsOutput = false);
        WriteCustomCommand("review", ReviewFile);
        _chat.EnqueueText("Reviewed.");
        PushLine("/review src/x.cs");
        PushLine("/exit");

        await RunAsync();

        using var store = new SessionStore(_settings.ProfileDirectory, _time);
        Assert.Equal("/review src/x.cs", Assert.Single(store.List(5)).Title);
    }

    [Fact]
    public async Task CustomCommand_ABuiltInWins_AndTheFileIsAnnouncedOnce()
    {
        _settings.Update(d => d.TtsOutput = false);
        WriteCustomCommand("usage", "Tell me about usage.");
        PushLine("/usage");
        PushLine("/usage");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);   // /usage ran both times
        Assert.Equal(1, Count(output, CustomCommandText.ShadowedByBuiltInNotice([new(new("usage", CustomCommandScope.Profile, Path.Combine(_settings.ProfileCustomCommandsDirectory, "usage.md"), null, null, [], null, "Tell me about usage."), "/usage", null)])));
    }

    [Fact]
    public async Task CustomCommand_NoModel_IsNoAssistantError_WithNoNotice()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmScanMode = "off"; d.LlmUrl = ""; });
        WriteCustomCommand("review", ReviewFile);
        PushLine("/review x");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Contains("  ✗ " + ChatScreen.NoAssistantError, output);
        Assert.DoesNotContain(CustomCommandText.Glyph + " /review from", output);
    }

    [Fact]
    public async Task CustomCommand_ABlankExpansion_IsItsError_AndNothingIsSent()
    {
        _settings.Update(d => d.TtsOutput = false);
        WriteCustomCommand("ask", "---\nargument-hint: <question>\n---\n$ARGUMENTS");
        PushLine("/ask");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Contains("  ✗ " + CustomCommandText.EmptyPromptError("/ask", "<question>"), output);
    }

    [Fact]
    public async Task CustomCommand_ABareWord_IsSentAsText()
    {
        _settings.Update(d => d.TtsOutput = false);
        WriteCustomCommand("review", ReviewFile);
        _chat.EnqueueText("Sure.");
        PushLine("review");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal("review", UserText(Assert.Single(_chat.Requests)));
    }

    [Fact]
    public async Task WithGeometry_CustomCommand_TypedWithExtraSlashes_IsOfferedFixed()
    {
        _settings.Update(d => d.TtsOutput = false);
        _geometry = new ScreenGeometry(() => null);
        WriteCustomCommand("review", ReviewFile);
        _chat.EnqueueText("Reviewed.");
        StepsWhenIdle(Line("//review a.cs"), Key(Keys.Enter), Key(Keys.Enter), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(Titled(ChatScreen.TypoTitle("/review a.cs")), output);
        Assert.StartsWith("Review a.cs with care", UserText(Assert.Single(_chat.Requests)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomCommand_UnderAReply_RunsWhenTheReplyEnds()
    {
        WriteCustomCommand("review", ReviewFile);
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                PushLine("/review a.cs");
            }
        });
        _chat.EnqueueText("Reviewed.");

        string output = await RunAsync();

        Assert.Contains("  · " + ChatScreen.MidTurnDeferredNotice("/review"), output);
        Assert.Contains(_chat.Requests, r => r.Last(m => m.Role == ChatRole.User).Text is { } text && text.StartsWith("Review a.cs with care", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CustomCommand_InALoop_SendsItsPromptEachPass()
    {
        _settings.Update(d => d.TtsOutput = false);
        WriteCustomCommand("review", ReviewFile);
        _chat.EnqueueText("One.").EnqueueText("Two.");
        PushLine("/loop 2 /review a.cs");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.All(_chat.Requests, r => Assert.Equal("Review a.cs with care, looking at $1.", r.Last(m => m.Role == ChatRole.User).Text));
        using var store = new SessionStore(_settings.ProfileDirectory, _time);
        Assert.Equal("/review a.cs", Assert.Single(store.List(5)).Title);   // 2026-10-31, review P1-3: the typed line, as the idle line titles
    }

    /// <summary>A message loop with no model says so once and sends nothing (2026-10-31, review P3-2: each pass said it, and an endless loop spun).</summary>
    [Fact]
    public async Task ALoop_WithNoModel_SaysSoOnce()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmScanMode = "off"; d.LlmUrl = ""; });
        WriteCustomCommand("review", ReviewFile);
        PushLine("/loop 3 /review x");
        PushLine("/loop 3 hello");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Equal(2, Count(output, "✗ " + ChatScreen.NoAssistantError));
        Assert.DoesNotContain(CustomCommandText.Glyph + " /review from", output);
    }

    /// <summary>
    /// <c>/rewind</c> lists a custom command's turn by the line typed for it, not the prompt it sent (2026-10-31, review P1-5); a later
    /// <c>/commands save</c> refuses it (the user's call).
    /// </summary>
    [Fact]
    public async Task CustomCommand_RewindShowsTheTypedLine_AndSaveRefusesIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        _geometry = new ScreenGeometry(() => null);
        WriteCustomCommand("review", ReviewFile);
        _chat.EnqueueText("Reviewed.");
        StepsWhenIdle(Line("/review a.cs"), Line("/commands save again"), Line("/rewind"), Key(Keys.Enter), Key(Keys.Enter), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(Titled(RewindText.ConfirmPrompt(new RewindTurn(1, 0, "/review a.cs", 0), new RewindCut(1, [], false, null, false))), output);
        Assert.Contains("✗ " + CustomCommandText.FromCustomCommandError("/review"), output);
        Assert.False(File.Exists(Path.Combine(_settings.ProfileCustomCommandsDirectory, "again.md")));
    }

    /// <summary>
    /// <c>/commands save</c> under a reply saves the message that reply answers, at once, never one queued since (2026-10-31, review P8-2).
    /// </summary>
    [Fact]
    public async Task CommandsSave_UnderAReply_SavesTheMessageItAnswers()
    {
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                PushLine("the next one");
                PushLine("/commands save hi");
            }
        });
        _chat.EnqueueText("Two.");

        string output = await RunAsync();

        string path = Path.Combine(_settings.ProfileCustomCommandsDirectory, "hi.md");
        Assert.True(File.Exists(path));
        Assert.Contains("hi", CustomCommandFile.Read(File.ReadAllText(path)).Body, StringComparison.Ordinal);
        Assert.DoesNotContain("the next one", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain(ChatScreen.MidTurnDeferredNotice("/commands"), output);
    }

    [Fact]
    public async Task CustomCommand_HelpWithItsWord_ShowsTheCard_WithNoModelCall()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Width = 400;
        string path = WriteCustomCommand("review", ReviewFile);
        PushLine("/help review");
        PushLine("/help /review");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Equal(2, Count(output, "/review <file> [focus] — your custom command, from the profile's commands."));
        Assert.Contains(path, output);
        Assert.DoesNotContain(HelpText.NotAskedNotice, output);
        Assert.Equal(2, Count(output, CustomCommandText.ManualWayOut("/review")));   // 2026-10-31, the user's call
    }

    /// <summary>A Skipped file named after /help has a short card with its reason, no model asked (2026-10-31, review P4-6).</summary>
    [Fact]
    public async Task CustomCommand_HelpWithASkippedFilesName_ShowsWhy()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Width = 400;
        WriteCustomCommand("broken", "---\ndescription: x\n---\n");
        PushLine("/help broken");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Contains(CustomCommandText.SkippedCardHead, output);
        Assert.Contains(CustomCommandText.EmptyBodyProblem, output);
    }

    [Fact]
    public async Task CustomCommand_TheHelpLines_ListItUnderCustom()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmScanMode = "off"; d.LlmUrl = ""; });
        _console.Profile.Width = 400;
        WriteCustomCommand("review", ReviewFile);
        PushLine("/help");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.All(CustomCommandText.HelpLines(new CustomCommandScan([ReviewCommand(Path.Combine(_settings.ProfileCustomCommandsDirectory, "review.md"))], [], [])),
            line => Assert.Contains(line.TrimEnd(), output));
    }

    [Fact]
    public async Task CustomCommand_CommandsNew_WritesTheTemplate_AndOpensIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/commands new triage");
        PushLine("/commands new triage");
        PushLine("/exit");

        string output = await RunAsync();

        string path = Path.Combine(_settings.ProfileCustomCommandsDirectory, "triage.md");
        Assert.Equal(CustomCommandText.Template("triage"), File.ReadAllText(path).ReplaceLineEndings("\n"));
        Assert.Equal([path], _openedFiles);
        Assert.Contains("  · " + CustomCommandText.CreatedNotice("triage", path), output);
        Assert.Contains("  ✗ " + CustomCommandText.ExistsError("triage", CustomCommandScope.Profile), output);   // the second never overwrites
    }

    [Fact]
    public async Task CustomCommand_CommandsSave_ThenTyped_SendsTheSameText()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("First.").EnqueueText("Again.");
        PushLine("Summarize the diff in three bullets");
        PushLine("/commands save sum");
        PushLine("/sum");
        PushLine("/exit");

        string output = await RunAsync();

        string path = Path.Combine(_settings.ProfileCustomCommandsDirectory, "sum.md");
        Assert.True(File.Exists(path));
        Assert.Contains(CustomCommandText.SavedNotice("sum", path, "Summarize the diff in three bullets"), output);
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal(UserText(_chat.Requests[0]), _chat.Requests[1].Last(m => m.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task CustomCommand_CommandsSave_WithNothingSent_IsItsError()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/commands save sum");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + CustomCommandText.NothingToSaveError, output);
        Assert.False(Directory.Exists(_settings.ProfileCustomCommandsDirectory) && Directory.EnumerateFiles(_settings.ProfileCustomCommandsDirectory).Any());
    }

    [Fact]
    public async Task CustomCommand_ASavedPromptOfItsWord_RunsIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        _geometry = new ScreenGeometry(() => null);
        WriteCustomCommand("review", ReviewFile);
        SavePrompts("/review a.cs");
        _chat.EnqueueText("Reviewed.");
        StepsWhenIdle(Line("/prompts"), Key(Keys.Enter), Line("/exit"));

        await RunAsync();

        Assert.StartsWith("Review a.cs with care", UserText(Assert.Single(_chat.Requests)), StringComparison.Ordinal);
    }

    /// <summary><c>/prompts</c>' <c>m</c>: a name typed, the scope picked, the prompt saved as a command that then runs.</summary>
    [Fact]
    public async Task CustomCommand_APromptsM_SavesTheSavedPromptAsACommand()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        _geometry = new ScreenGeometry(() => null);
        SavePrompts("Summarize the diff.");
        _chat.EnqueueText("Summed.");
        var steps = new List<Action<ScriptedInput>> { Line("/prompts"), Key(Keys.Char(SavedPromptText.AsCommandKey)) };
        steps.AddRange("sum".Select(c => Key(Keys.Char(c))));
        steps.AddRange([Key(Keys.Enter), Key(Keys.Enter), Key(Keys.Escape), Line("/sum"), Line("/exit")]);   // the name, profile, the pane closed
        StepsWhenIdle([.. steps]);

        string output = await RunAsync();

        string path = Path.Combine(_settings.ProfileCustomCommandsDirectory, "sum.md");
        Assert.Contains(SavedPromptText.AsCommandScopeTitle("sum"), output);
        Assert.True(File.Exists(path));
        Assert.Equal("Summarize the diff.", UserText(Assert.Single(_chat.Requests)));
        Assert.Equal(SavedPromptText.AsCommandButton, SavedPromptsMenu.Buttons[4].Title);   // after edit, before remove
    }

    [Fact]
    public async Task CustomCommand_ThePanesUse_PutsTheWordOnTheDraft()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        _geometry = new ScreenGeometry(() => null);
        WriteCustomCommand("review", ReviewFile);
        _chat.EnqueueText("Reviewed.");
        StepsWhenIdle(
            Line("/commands"),
            Key(Keys.Down),    // /review, under new command…
            Key(Keys.Enter),   // its page
            Key(Keys.Enter),   // use
            Key(Keys.Char('x')),
            Key(Keys.Enter),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(CustomCommandText.PageTitle("/review"), output);
        Assert.Contains("› /review x", output);
        Assert.StartsWith("Review x with care", UserText(Assert.Single(_chat.Requests)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomCommand_ThePane_ListsCommandsAndTheNewRow()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        _geometry = new ScreenGeometry(() => null);
        WriteCustomCommand("review", ReviewFile);
        WriteCustomCommand("triage", "Triage it.", global: true);
        StepsWhenIdle(Line("/commands"), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(CustomCommandText.NewRow, output);
        Assert.Contains("/review  <file> [focus]  review a file · profile", output);
        Assert.Contains("/triage  Triage it. · global", output);   // no description: the body's first line, Claude Code's way
        Assert.Empty(_chat.Requests);
    }
}
