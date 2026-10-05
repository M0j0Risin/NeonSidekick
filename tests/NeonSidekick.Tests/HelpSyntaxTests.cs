using System.Text.RegularExpressions;
using NeonSidekick.App;
using NeonSidekick.Help;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The command forms' notation (2026-10-05, the user's ask: one way to write every command's forms, in their own column of
/// <c>/help</c>'s Commands tabs, in two tones): the runs a form is cut into, the rules every form in <see cref="HelpCommands"/>
/// keeps, and which forms <c>/help</c> shows.
/// </summary>
public class HelpSyntaxTests
{
    [Fact]
    public void Runs_CutAFormIntoWordsSlotsAndMarks_AndJoinBackToIt()
    {
        Assert.Equal(
        [
            ("/profile", HelpSyntaxPart.Word), (" ", HelpSyntaxPart.Space), ("rename", HelpSyntaxPart.Word), (" ", HelpSyntaxPart.Space),
            ("<name>", HelpSyntaxPart.Slot), (" ", HelpSyntaxPart.Space), ("<new-name>", HelpSyntaxPart.Slot),
        ], HelpSyntax.Runs("/profile rename <name> <new-name>"));
        Assert.Equal(
        [
            ("/copy", HelpSyntaxPart.Word), (" ", HelpSyntaxPart.Space), ("[", HelpSyntaxPart.Mark), ("<n>", HelpSyntaxPart.Slot), ("|", HelpSyntaxPart.Mark),
            ("all", HelpSyntaxPart.Word), ("]", HelpSyntaxPart.Mark), (" ", HelpSyntaxPart.Space), ("[", HelpSyntaxPart.Mark), ("--thinking", HelpSyntaxPart.Word),
            ("]", HelpSyntaxPart.Mark),
        ], HelpSyntax.Runs("/copy [<n>|all] [--thinking]"));
        Assert.Equal(
        [
            ("/botchat", HelpSyntaxPart.Word), (" ", HelpSyntaxPart.Space), ("[", HelpSyntaxPart.Mark), ("<profile>", HelpSyntaxPart.Slot), (" ", HelpSyntaxPart.Space),
            ("...", HelpSyntaxPart.Mark), ("]", HelpSyntaxPart.Mark),
        ], HelpSyntax.Runs("/botchat [<profile> ...]"));
        Assert.Equal([("/imagine", HelpSyntaxPart.Word), (" ", HelpSyntaxPart.Space), ("--size", HelpSyntaxPart.Word), (" ", HelpSyntaxPart.Space), ("<w>", HelpSyntaxPart.Slot), ("x", HelpSyntaxPart.Word), ("<h>", HelpSyntaxPart.Slot)],
            HelpSyntax.Runs("/imagine --size <w>x<h>"));

        foreach (var form in HelpCommands.Commands.SelectMany(c => c.Forms))
        {
            Assert.Equal(form.Syntax, string.Concat(HelpSyntax.Runs(form.Syntax).Select(r => r.Text)));
        }
    }

    [Theory]
    [InlineData("/timer [<duration> [<name>]]")]
    [InlineData("/ha tv on|off|mute|unmute|up|down")]
    [InlineData("/screen window:<id>|<title-words>")]
    [InlineData("/timer")]
    public void Problem_IsNull_ForAFormInTheNotation(string form) => Assert.Null(HelpSyntax.Problem(form, "/" + form[1..].Split(' ')[0]));

    [Theory]
    [InlineData("/copy [n | all]", "/copy", "blank beside a bar")]
    [InlineData("/copy [<n>", "/copy", "bracket open")]
    [InlineData("/copy <n>]", "/copy", "never opened")]
    [InlineData("/copy [<Name>]", "/copy", "<Name>")]
    [InlineData("/copy <n|name>", "/copy", "<n|name>")]
    [InlineData("/copy []", "/copy", "empty brackets")]
    [InlineData("/copy  all", "/copy", "doubled")]
    [InlineData("/copyall", "/copy", "does not start")]
    [InlineData("copy all", "/copy", "does not start")]
    public void Problem_NamesWhatIsWrong(string form, string command, string part) => Assert.Contains(part, HelpSyntax.Problem(form, command));

    /// <summary>Every form the catalogue holds keeps the notation: what <c>/help</c> shows and <c>neon_help</c> reads is written one way.</summary>
    [Fact]
    public void EveryFormInTheCatalogue_KeepsTheNotation()
    {
        foreach (var command in HelpCommands.Commands)
        {
            Assert.NotEmpty(command.Forms);
            foreach (var form in command.Forms)
            {
                Assert.True(HelpSyntax.Problem(form.Syntax, command.Command) is null, form.Syntax + ": " + HelpSyntax.Problem(form.Syntax, command.Command));
            }

            Assert.Equal(command.Forms.Count, command.Forms.Select(f => f.Syntax).Distinct().Count());
        }
    }

    [Fact]
    public void Forms_AreTheCataloguesSyntaxes_NoneForABareCommand()
    {
        Assert.Empty(HelpSyntax.Forms("/clear"));   // its one form is the bare word: the description says it all
        Assert.Empty(HelpSyntax.Forms("/nosuch"));
        Assert.Equal(["/timer [<duration> [<name>]]", "/timer stop <name>|all"], HelpSyntax.Forms("/timer"));
        Assert.Equal(["/settings", "/settings <words>", "/settings changed"], HelpSyntax.Forms("/settings"));   // a bare form among others stays
        Assert.Equal(["/remember <text>"], HelpSyntax.Forms("/remember"));
    }

    /// <summary>On the pane a form's words and its placeholders and brackets are drawn in two styles (the user's pick).</summary>
    [Fact]
    public void TheFormsCell_DrawsWordsAndSlotsInTwoStyles_OneFormALine()
    {
        Assert.NotEqual(Theme.HelpForm, Theme.HelpSlot);
        Assert.Equal(Theme.TrailerMark, Theme.HelpForm);
        Assert.Equal(Theme.DimText, Theme.HelpSlot);

        var console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Profile.Capabilities.ColorSystem = ColorSystem.TrueColor;
        console.Write(ChatScreen.FormsCell(["/profile rename <name> <new-name>", "/profile edit|reload"]));
        string output = console.Output;

        string Before(string text) => Regex.Match(output, @"(\e\[[0-9;]*m)" + Regex.Escape(text)).Groups[1].Value;
        Assert.NotEqual("", Before("/profile"));
        Assert.NotEqual(Before("/profile"), Before("<name>"));
        Assert.Equal(Before("/profile"), Before("rename"));
        Assert.Equal(Before("<name>"), Before("|"));

        var plain = new TestConsole();
        plain.Profile.Width = 120;
        plain.Write(ChatScreen.FormsCell(["/profile rename <name> <new-name>", "/profile edit|reload"]));
        Assert.Equal(["/profile rename <name> <new-name>", "/profile edit|reload"], plain.Output.TrimEnd('\n').Split('\n').Select(l => l.TrimEnd()));
    }
}
