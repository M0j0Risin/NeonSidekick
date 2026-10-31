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
        Assert.Equal("review", CustomCommandsCommand.HelpCard(scan, "review")!.Name);
        Assert.Equal("review", CustomCommandsCommand.HelpCard(scan, " /Review ")!.Name);
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
}
