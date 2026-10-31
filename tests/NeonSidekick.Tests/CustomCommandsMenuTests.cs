using NeonSidekick.App;
using NeonSidekick.CustomCommands;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The <c>/commands</c> pane's rows, footer band and move targets (2026-10-30, the custom commands plan). Pure parts.</summary>
public class CustomCommandsMenuTests
{
    private static readonly CustomCommandRoots Roots = new(Path.Combine("p", "commands"), Path.Combine("h", "commands"));

    private static CustomCommand Command(string name, CustomCommandScope scope, string body = "Body.", string? hint = null, string? warning = null) =>
        new(name, scope, Path.Combine(scope == CustomCommandScope.Profile ? Roots.Profile : Roots.Global, name + ".md"), "does " + name, hint, [], warning, body);

    [Fact]
    public void MoveTargets_TheOtherScopeFirst_ThenTheOtherProfiles()
    {
        Assert.Equal([CustomCommandText.ToGlobal, "to profile work", "to profile home"],
            CustomCommandsMenu.MoveTargets(CustomCommandScope.Profile, sameFolder: false, otherScope: true, ["work", "home"]).Select(t => t.Label));
        Assert.Equal([CustomCommandText.ToThisProfile], CustomCommandsMenu.MoveTargets(CustomCommandScope.Global, false, true, []).Select(t => t.Label));
        Assert.Equal(["to profile work"], CustomCommandsMenu.MoveTargets(CustomCommandScope.Profile, sameFolder: true, otherScope: true, ["work"]).Select(t => t.Label));
        Assert.Equal(["to profile work"], CustomCommandsMenu.MoveTargets(CustomCommandScope.Global, false, otherScope: false, ["work"]).Select(t => t.Label));   // a hidden global row
    }

    [Fact]
    public void Rows_NewFirst_ThenCommands_ThenShadowedAndSkipped()
    {
        var review = Command("review", CustomCommandScope.Profile, hint: "[file]");
        var hidden = Command("review", CustomCommandScope.Global);
        var clear = Command("clear", CustomCommandScope.Global);
        var scan = new CustomCommandScan([review], [new(hidden, null, review), new(clear, "/clear", null)], [new(Path.Combine(Roots.Profile, "bad name.md"), CustomCommandScope.Profile, "not a command name")]);

        var rows = CustomCommandsMenu.Rows(scan, Roots, "", null);

        Assert.Equal(CommandsRowKind.New, rows[0].Kind);
        Assert.Equal(CommandsRowKind.Command, rows[1].Kind);
        Assert.Contains("[[file]]", rows[1].Markup);   // a hint is escaped: [issue] is Spectre markup otherwise
        Assert.Equal(2, rows.Count(r => r.Kind == CommandsRowKind.Shadow));
        Assert.Single(rows, r => r.Kind == CommandsRowKind.Problem);
        Assert.Equal(2, rows.Count(r => r.Heading));
        Assert.Equal(["review", "clear"], rows.Where(r => r.Kind == CommandsRowKind.Shadow).Select(r => r.Shadow!.Command.Name));
    }

    [Fact]
    public void Rows_AFilter_KeepsNewFirst_AndShowsTheNoMatchRowUnderIt()
    {
        var scan = new CustomCommandScan([Command("review", CustomCommandScope.Profile), Command("triage", CustomCommandScope.Global)], [], []);

        var kept = CustomCommandsMenu.Rows(scan, Roots, "tri", null);
        Assert.Equal([CommandsRowKind.New, CommandsRowKind.Command], kept.Select(r => r.Kind));
        Assert.Equal("triage", kept[1].Command!.Name);

        var none = CustomCommandsMenu.Rows(scan, Roots, "zzz", null);
        Assert.Equal(CommandsRowKind.New, none[0].Kind);
        Assert.Equal(MenuFilter.NoMatchRow("zzz"), none[1].Markup);

        var page = CustomCommandsMenu.Page(scan, Roots, "tri", 0);
        Assert.Equal(MenuFilter.CaptionOrNull("tri", 1, 2), page.Tabs![0].SubTabs![0].Caption);   // new command… is never counted
    }

    [Fact]
    public void Rows_AScopedSubTab_ShowsItsScopeAlone()
    {
        var scan = new CustomCommandScan([Command("review", CustomCommandScope.Profile), Command("triage", CustomCommandScope.Global)], [], []);
        Assert.Equal(["triage"], CustomCommandsMenu.Rows(scan, Roots, "", CustomCommandScope.Global).Where(r => r.Command is not null).Select(r => r.Command!.Name));
        Assert.Equal(CustomCommandScope.Global, CustomCommandsMenu.ScopeOf(2));
        Assert.Null(CustomCommandsMenu.ScopeOf(0));
    }

    [Fact]
    public void Footer_IsPlainText_ControlsCleaned()
    {
        var command = Command("review", CustomCommandScope.Profile, body: "Look at [x]\n\tindented\n\u001b[31mred", warning: "still the template: edit it");
        var footer = CustomCommandsMenu.Footer(new CommandsRow("", CommandsRowKind.Command, Command: command))!;

        Assert.Equal(command.FilePath + "\nLook at [x]\n    indented\n·[31mred", footer.Text);
        Assert.Equal("Note: still the template: edit it", footer.Last);
        Assert.True(footer.KeepLines);
        Assert.Null(CustomCommandsMenu.Footer(new CommandsRow("", CommandsRowKind.New)));
    }

    [Fact]
    public void AnEmptyList_SaysHowToMakeOne()
    {
        var rows = CustomCommandsMenu.Rows(CustomCommandScan.Empty, Roots, "", null);
        Assert.Equal(2, rows.Count);
        Assert.Equal(Theme.DimMarkup(CustomCommandText.EmptyRow), rows[1].Markup);
    }

    [Fact]
    public void TheHint_FitsItsRow()
    {
        Assert.True(TextCells.Width(MenuFilter.Hint(CustomCommandsMenu.Keys, "x")) <= 119);
        Assert.True(TextCells.Width(CustomCommandsMenu.Keys) <= 119);
    }
}
