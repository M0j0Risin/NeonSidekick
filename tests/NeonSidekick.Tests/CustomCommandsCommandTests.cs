using NeonSidekick.App;
using NeonSidekick.CustomCommands;
using NeonSidekick.Files;
using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

/// <summary><c>/commands</c>' line, the prompt a custom command sends, the <c>/help</c> card's word, and the text <c>/commands save</c> takes.</summary>
public class CustomCommandsCommandTests
{
    private static CustomCommand Command(string name, string body, params string[] arguments) =>
        new(name, CustomCommandScope.Profile, "C:/x/" + name + ".md", "does " + name, "[focus]", arguments, null, body);

    [Fact]
    public void Parse_TheThreeForms()
    {
        Assert.Equal(CustomCommandsVerb.Pane, CustomCommandsCommand.Parse("").Verb);
        Assert.Equal(new CustomCommandsLine(CustomCommandsVerb.New, "triage", CustomCommandScope.Profile), CustomCommandsCommand.Parse("new triage"));
        Assert.Equal(new CustomCommandsLine(CustomCommandsVerb.New, "triage", CustomCommandScope.Global), CustomCommandsCommand.Parse("NEW triage --global"));
        Assert.Equal(new CustomCommandsLine(CustomCommandsVerb.Save, "frontend:again", CustomCommandScope.Global), CustomCommandsCommand.Parse("save --global /frontend:again"));
        Assert.Equal(new CustomCommandsLine(CustomCommandsVerb.Save, "global", CustomCommandScope.Profile), CustomCommandsCommand.Parse("save global"));   // a command may be called global
    }

    [Fact]
    public void Parse_ABadName_SuggestsOne_AndAnUnknownVerbIsTheUsage()
    {
        var bad = CustomCommandsCommand.Parse("new My_Review");
        Assert.Equal(CustomCommandsVerb.Unknown, bad.Verb);
        Assert.Equal(CustomCommandText.BadNameError("My_Review", "my_review"), bad.Error);
        Assert.Equal(CustomCommandText.BadNameError("Re.view", "re-view", "save"), CustomCommandsCommand.Parse("save Re.view").Error);
        Assert.Equal(CustomCommandText.Usage, CustomCommandsCommand.Parse("list").Error);
        Assert.Equal(CustomCommandText.Usage, CustomCommandsCommand.Parse("new").Error);
        Assert.Equal(CustomCommandText.Usage, CustomCommandsCommand.Parse("new a b").Error);
        Assert.True(CustomCommandsCommand.IsPane(" "));
        Assert.False(CustomCommandsCommand.IsPane("new x"));
    }

    [Fact]
    public void Complete_OffersTheVerbs_ThenTheSwitch()
    {
        Assert.Equal(["new", "save"], CustomCommandsCommand.Complete("").Select(i => i.Text));
        Assert.Equal(["save"], CustomCommandsCommand.Complete("sa").Select(i => i.Text));
        Assert.Equal(["new triage --global"], CustomCommandsCommand.Complete("new triage -").Select(i => i.Text));
        Assert.Empty(CustomCommandsCommand.Complete("new triage --global "));
    }

    [Fact]
    public void Prompt_IsTheExpansion_ADroppedPictureLabelAddedBack()
    {
        Assert.Equal("Review src/x.cs for bugs", CustomCommandsCommand.Prompt(Command("review", "Review $0 for $1"), "src/x.cs bugs").Prompt);
        Assert.Equal("Look at it\n\nARGUMENTS: [Image #1]", CustomCommandsCommand.Prompt(Command("look", "Look at it"), "[Image #1]").Prompt);   // kept by the ARGUMENTS line
        Assert.Equal("Describe x\n\n[Image #1]", CustomCommandsCommand.Prompt(Command("describe", "Describe $0"), "x [Image #1]").Prompt);
        Assert.Equal("Describe [Image #2] now", CustomCommandsCommand.Prompt(Command("describe", "Describe $ARGUMENTS now"), "[Image #2]").Prompt);
    }

    [Fact]
    public void Prompt_ABlankExpansion_IsItsError()
    {
        var (prompt, error) = CustomCommandsCommand.Prompt(Command("blank", "$ARGUMENTS"), "");
        Assert.Null(prompt);
        Assert.Equal(CustomCommandText.EmptyPromptError("/blank", "[focus]"), error);
    }

    [Fact]
    public void HelpCard_IsOneWordThatNoBuiltInHas()
    {
        var scan = new CustomCommandScan([Command("review", "x")], [], []);
        Assert.Equal("review", CustomCommandsCommand.HelpCard(scan, "review")!.Command!.Name);
        Assert.Equal("review", CustomCommandsCommand.HelpCard(scan, " /Review ")!.Command!.Name);
        Assert.Null(CustomCommandsCommand.HelpCard(scan, "review code"));   // several words: the manual's lookup
        Assert.Null(CustomCommandsCommand.HelpCard(new CustomCommandScan([Command("clear", "x")], [], []), "clear"));   // a built-in's word
        Assert.Null(CustomCommandsCommand.HelpCard(scan, "triage"));
    }

    [Fact]
    public void LastSentMessage_IsTheLastTurnsWords()
    {
        var history = new ConversationHistory("system");
        Assert.Equal(CustomCommandText.NothingToSaveError, CustomCommandsCommand.LastSentMessage(history.Messages).Error);

        history.AddOpener("[opening]");
        Assert.Equal(CustomCommandText.NothingToSaveError, CustomCommandsCommand.LastSentMessage(history.Messages).Error);   // the opener is no turn

        history.AddUser("first message");
        history.AddAssistant("ok");
        history.AddUser(Help.HelpText.AskPrompt("voices"));
        history.AddAssistant("answer");
        var (text, pictures, error) = CustomCommandsCommand.LastSentMessage(history.Messages);
        Assert.Null(error);
        Assert.False(pictures);
        Assert.Equal(Help.HelpText.AskPrompt("voices"), text);   // a /help <words> turn: its prompt, as sent
    }

    [Fact]
    public void LastSentMessage_ACompactSummaryIsNoTurn()
    {
        var history = new ConversationHistory("system");
        history.AddUser("before");
        history.AddAssistant("ok");
        history.AddUser(ConversationCompactor.SummaryPreamble + "what happened");
        Assert.Equal("before", CustomCommandsCommand.LastSentMessage(history.Messages).Text);
    }

    [Fact]
    public void LastSentMessage_TakesThePictureLabelsOff()
    {
        var history = new ConversationHistory("system");
        var picture = new ImageAttachment("C:/p/a.png", [1, 2, 3], "image/png", 1, 1);
        history.AddUser("What is [Image #1, saved as C:\\p\\pasted-1.png] showing?", [picture]);
        var (text, pictures, _) = CustomCommandsCommand.LastSentMessage(history.Messages);
        Assert.Equal("What is showing?", text);
        Assert.True(pictures);
    }

    [Fact]
    public void LastSentMessage_RefusesAClaudeOrCmdTurn()
    {
        var history = new ConversationHistory("system");
        history.AddUser(Claude.ClaudeText.HistoryUser("fix it"));
        Assert.Equal(CustomCommandText.NotAMessageError("/claude"), CustomCommandsCommand.LastSentMessage(history.Messages).Error);

        history.AddAssistant("done");
        history.AddUser(Shell.ShellText.CmdHistoryUser("powershell", "git status"));
        Assert.Equal(CustomCommandText.NotAMessageError("/cmd"), CustomCommandsCommand.LastSentMessage(history.Messages).Error);
    }

    [Fact]
    public void AttachmentLabels_AreFoundAndTakenOff()
    {
        Assert.Equal(["[Image #1]", "[PDF #2 report [v2].pdf]"], UI.PasteBlocks.AttachmentLabels("a [Image #1] b [PDF #2 report [v2].pdf] c"));
        Assert.Equal("a b\nc", UI.PasteBlocks.WithoutAttachmentLabels("a [Image #1] b [Image #2]\nc", out bool removed));
        Assert.True(removed);
        Assert.Equal("plain", UI.PasteBlocks.WithoutAttachmentLabels("plain", out removed));
        Assert.False(removed);
    }

    /// <summary>The last message a custom command or the app wrote is no message of the user's to save (2026-10-31, review P1-5/P8-1, the user's call).</summary>
    [Fact]
    public void LastSentMessage_RefusesACustomCommandsOrTheAppsMessage()
    {
        var history = new ConversationHistory("system");
        history.AddUser("Review src carefully.", [], null, TurnMark.TypedAs("/review src", "Review src carefully."));
        Assert.Equal(CustomCommandText.FromCustomCommandError("/review"), CustomCommandsCommand.LastSentMessage(history.Messages).Error);

        history.AddAssistant("done");
        history.AddUser(Help.HelpText.AskPrompt("voices"), [], null, TurnMark.App);
        Assert.Equal(CustomCommandText.AppWrittenError, CustomCommandsCommand.LastSentMessage(history.Messages).Error);

        history.AddAssistant("answer");
        history.AddUser("my own words", [], null, TurnMark.TypedAs("my own words", "my own words"));
        Assert.Equal("my own words", CustomCommandsCommand.LastSentMessage(history.Messages).Text);
    }

    /// <summary>A file gone between the parse and the run is said so, never an unknown command (2026-10-31, review P1-11).</summary>
    [Fact]
    public void Resolve_AGoneFile_IsNotFound_AndAFoundOne_ItsPrompt()
    {
        string root = Path.Combine(Path.GetTempPath(), "neon-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "review.md"), "Review $ARGUMENTS.");
            var catalog = new CustomCommandCatalog(() => new CustomCommandRoots(root, Path.Combine(root, "none")));

            var run = CustomCommandsCommand.Resolve(catalog, "/review src");
            Assert.Equal("Review src.", run.Prompt);
            Assert.Null(run.Error);

            Assert.Equal(CustomCommandText.NotFound("/triage"), CustomCommandsCommand.Resolve(catalog, "/triage x").Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A Skipped file has a card of its own by its name; a profile command hiding a global one says so (2026-10-31, review P4-6, P2-3).</summary>
    [Fact]
    public void HelpCard_ASkippedFile_AndAHiddenGlobal()
    {
        var review = Command("review", "x");
        var hidden = new CustomCommand("review", CustomCommandScope.Global, "C:/h/review.md", null, null, [], null, "y");
        var problem = new CustomCommandProblem("C:/x/broken.md", CustomCommandScope.Profile, CustomCommandText.EmptyBodyProblem, "broken");
        var scan = new CustomCommandScan([review], [new(hidden, null, review)], [problem]);

        Assert.Same(hidden, CustomCommandsCommand.HelpCard(scan, "review")!.Hides!.Command);
        var skipped = CustomCommandsCommand.HelpCard(scan, "broken")!;
        Assert.Null(skipped.Command);
        Assert.Same(problem, skipped.Problem);
    }

    /// <summary>
    /// The card (2026-10-31, review P3-6, P4-7, P2-3): Markdown escapes the file's own words and fences the path, plain text has neither; both
    /// carry the hides note and the way to the manual (the user's call).
    /// </summary>
    [Fact]
    public void Card_MarkdownEscapes_PlainDoesNot_BothEndWithTheWayOut()
    {
        var command = new CustomCommand("init", CustomCommandScope.Profile, "C:/x/a`b/init.md", "makes __init__ files", null, [], null, "x");
        var hidden = new CustomCommand("init", CustomCommandScope.Global, "C:/h/init.md", null, null, [], null, "y");
        var card = new CustomHelpCard(command, null, new CustomCommandShadow(hidden, null, command));

        string markdown = CustomCommandText.Card(card, markdown: true);
        string plain = CustomCommandText.Card(card, markdown: false);

        Assert.Contains("\\_\\_init\\_\\_", markdown);
        Assert.Contains("``C:/x/a`b/init.md``", markdown);
        Assert.Contains("makes __init__ files", plain);
        Assert.DoesNotContain("\\", plain);
        Assert.Contains("C:/h/init.md", plain);
        Assert.EndsWith(CustomCommandText.ManualWayOut("/init"), plain);
        Assert.Contains("/help about init", markdown);

        var skipped = CustomCommandText.Card(new CustomHelpCard(null, new CustomCommandProblem("C:/x/broken.md", CustomCommandScope.Profile, "the prompt is empty", "broken")), markdown: false);
        Assert.Contains("the prompt is empty", skipped);
        Assert.Contains("C:/x/broken.md", skipped);
        Assert.EndsWith(CustomCommandText.ManualWayOut("/broken"), skipped);
    }

    [Fact]
    public void CodeSpan_IsFencedLongerThanAnyRunInIt()
    {
        Assert.Equal("`a`", CustomCommandText.CodeSpan("a"));
        Assert.Equal("```a``b```", CustomCommandText.CodeSpan("a``b"));
        Assert.Equal("`` `a ``", CustomCommandText.CodeSpan("`a"));
    }

    /// <summary>A saved global command the profile's hides gets no "type it to send it again" (2026-10-31, review P8-3).</summary>
    [Fact]
    public void SavedText_AHiddenGlobal_LeavesOutTheSendAgain()
    {
        string root = Path.Combine(Path.GetTempPath(), "neon-saved-" + Guid.NewGuid().ToString("N"));
        var roots = new CustomCommandRoots(Path.Combine(root, "p"), Path.Combine(root, "g"));
        Directory.CreateDirectory(roots.Profile);
        Directory.CreateDirectory(roots.Global);
        try
        {
            File.WriteAllText(Path.Combine(roots.Profile, "sum.md"), "Profile's.");
            var result = CustomCommandEditor.Save(roots, CustomCommandScope.Global, "sum", "Sum it up.");
            string text = CustomCommandsCommand.SavedText(roots, result, "Sum it up.", picturesDropped: false);

            Assert.DoesNotContain("to send it again", text);
            Assert.Contains(CustomCommandText.ShadowedHereNote("sum"), text);

            var mine = CustomCommandEditor.Save(roots, CustomCommandScope.Profile, "tidy", "Tidy up.");
            Assert.Contains("to send it again", CustomCommandsCommand.SavedText(roots, mine, "Tidy up.", picturesDropped: true));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The lists' rows (2026-10-31, review P6-7, P4-8, P2-1): a Skipped file by its path under its folder, every field cleaned, and the plain
    /// lines, the help lines and the tab built from them.
    /// </summary>
    [Fact]
    public void Rows_NameASkippedFileUnderItsFolder_AndAreCleaned()
    {
        var roots = new CustomCommandRoots(Path.Combine("p", "commands"), Path.Combine("h", "commands"));
        var command = new CustomCommand("review", CustomCommandScope.Profile, Path.Combine(roots.Profile, "review.md"), "red \u001b[31m text", "[file\u0007]", [], null, "x");
        var problem = new CustomCommandProblem(Path.Combine(roots.Global, "tools", "bad name.md"), CustomCommandScope.Global, "not a command name");
        var scan = new CustomCommandScan([command], [], [problem]) { Roots = roots, Note = CustomCommandText.SameFolderNote };

        var rows = CustomCommandText.Rows(scan);

        Assert.Equal(Path.Combine("tools", "bad name.md"), rows.Single(r => r.Kind == CustomCommandRowKind.Problem).Word);
        Assert.All(rows, r => Assert.DoesNotContain(r.Detail + r.Hint, c => char.IsControl(c)));
        var lines = CustomCommandText.Lines(scan);
        Assert.Contains(lines, l => l.Contains(Path.Combine("tools", "bad name.md"), StringComparison.Ordinal));
        Assert.Contains(CustomCommandText.NotePrefix + CustomCommandText.SameFolderNote, lines);
        Assert.All(lines.Concat(CustomCommandText.HelpLines(scan)), l => Assert.DoesNotContain(l, c => char.IsControl(c)));
    }
}
