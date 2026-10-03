using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Help;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>neon_help</c> (2026-10-02): the catalog covers every setting and every command (so a new one without its help fails here),
/// the answers for each sort of query, the tool's schema and arguments, plan mode and the rule.
/// </summary>
public class NeonHelpTests
{
    private readonly NeonHelpTool _tool = new();

    private string Invoke(string? query = null, string? kind = null)
    {
        var args = new Dictionary<string, object?>();
        if (query is not null)
        {
            args[NeonHelpTool.QueryArgument] = query;
        }

        if (kind is not null)
        {
            args[NeonHelpTool.KindArgument] = kind;
        }

        return (string)_tool.InvokeAsync(new AIFunctionArguments(args)).AsTask().GetAwaiter().GetResult()!;
    }

    // ── The catalog ──────────────────────────────────────────────────────────

    [Fact]
    public void EverySetting_HasHelp_OnePlace_AndADefault()
    {
        foreach (var field in Enum.GetValues<SettingsField>())
        {
            Assert.False(string.IsNullOrWhiteSpace(HelpSettings.Describe(field)), $"{field} has no help: add it to HelpSettings.Describe");
            Assert.Single(HelpLocation.Rows, r => r.Field == field);
            Assert.NotNull(HelpLocation.Default(field));
        }

        Assert.Equal(Enum.GetValues<SettingsField>().Length, HelpLocation.Rows.Count);
    }

    [Fact]
    public void EveryCommand_HasItsForms_AndNoFormNamesACommandThatIsNotThere()
    {
        var commands = SlashCommands.HelpEntriesFor(log: true).Select(e => e.Command).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(commands, HelpCommands.Commands.Select(c => c.Command).ToArray());   // A to Z, one each
        foreach (var command in HelpCommands.Commands)
        {
            Assert.NotEmpty(command.Forms);
            foreach (var form in command.Forms)
            {
                Assert.StartsWith(command.Command, form.Syntax, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(form.Meaning), form.Syntax);
            }
        }
    }

    [Fact]
    public void Location_IsWorkedOutFromTheMenus()
    {
        Assert.Equal("/tools › Camera › Camera watch interval (s)", HelpLocation.Of(SettingsField.CameraWatchSeconds).Path);
        Assert.Equal("/settings › General › Profile", HelpLocation.Of(SettingsField.Profile).Path);
        Assert.Equal("/skills › Reflection", HelpLocation.Of(SettingsField.ReflectionAutoLearn).Pane + HelpText.PathSeparator + HelpLocation.Of(SettingsField.ReflectionAutoLearn).Tab);
        Assert.Equal("/mcp › Options › MCP servers", HelpLocation.Of(SettingsField.McpServers).Path);
        Assert.Equal("10", HelpLocation.Default(SettingsField.CameraWatchSeconds));   // the unit is the label's, (s)
        Assert.Equal(HelpText.WorkingDirectoryDefault, HelpLocation.Default(SettingsField.WorkingDirectory));
    }

    // ── The answers ──────────────────────────────────────────────────────────

    [Fact]
    public void NoQuery_IsTheOverview_EveryPaneWithItsTabs_AndEveryCommand()
    {
        string text = Invoke();

        Assert.StartsWith(HelpText.OverviewHeading, text, StringComparison.Ordinal);
        Assert.EndsWith(HelpText.OverviewTail, text, StringComparison.Ordinal);
        foreach (var pane in HelpLocation.Panes)
        {
            Assert.Contains("- " + pane.Command + ": " + pane.Summary + ". Tabs: " + string.Join(", ", pane.Tabs) + "\n", text);
        }

        Assert.Contains("\nSlash commands: " + string.Join(", ", SlashCommands.HelpEntriesFor(log: true).Select(e => e.Label)) + "\n", text);
        Assert.True(text.Length < 3000, text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));   // a first look a small model can afford
    }

    [Fact]
    public void CommandKind_NoQuery_ListsEveryCommandWithItsFirstSentence_Whole()
    {
        string text = Invoke(kind: "command");

        Assert.StartsWith(HelpText.CommandListHeading + "\n", text, StringComparison.Ordinal);
        foreach (var entry in SlashCommands.HelpEntriesFor(log: true))
        {
            Assert.Contains("\n- " + entry.Label + ": " + HelpText.FirstSentence(entry.Summary), text);
        }

        Assert.DoesNotContain(HelpText.CutNote, text);
    }

    [Theory]
    [InlineData("/camera")]
    [InlineData("camera")]
    [InlineData("/camera watch")]
    [InlineData("/CAMERA")]
    public void ACommand_ListsEveryForm(string query)
    {
        string text = Invoke(query);

        Assert.StartsWith("Command /camera:", text, StringComparison.Ordinal);
        foreach (var form in HelpCommands.Commands.Single(c => c.Command == "/camera").Forms)
        {
            Assert.Contains("\n- " + form.Syntax + ": " + form.Meaning, text);
        }
    }

    [Fact]
    public void AnAlias_IsItsCommand()
    {
        Assert.StartsWith("Command /settings:", Invoke("//"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Camera watch interval (s)")]
    [InlineData("camera watch interval")]
    public void ASettingByName_IsWhereItIs_ItsDefault_AndWhatItDoes(string query)
    {
        Assert.Equal(
            "Setting Camera watch interval (s)\n  Where: /tools › Camera › Camera watch interval (s) (open /tools, go to the Camera tab, Enter on the row edits it)\n  Default: 10\n  What it does: " + HelpSettings.Describe(SettingsField.CameraWatchSeconds),
            Invoke(query));
    }

    [Fact]
    public void PartOfASettingsName_ListsEachThatHoldsIt()
    {
        string text = Invoke("watch min gap", "setting");

        Assert.StartsWith("Setting Camera watch min gap (s)\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/tools camera")]
    [InlineData("tools camera")]
    [InlineData("camera tab")]
    public void ATab_ListsItsRows_WithDefaultsAndAFirstSentence(string query)
    {
        string text = Invoke(query);

        Assert.StartsWith("Tab /tools › Camera:\n", text, StringComparison.Ordinal);
        foreach (var row in HelpLocation.OnTab("/tools", ToolsText.CameraTabTitle))
        {
            Assert.Contains("\n- " + row.Label + " (default " + HelpLocation.Default(row.Field) + "): ", text);
        }
    }

    [Fact]
    public void EveryPaneAndTab_IsAnsweredWhole()
    {
        foreach (var pane in HelpLocation.Panes)
        {
            Assert.DoesNotContain(HelpText.CutNote, Invoke(pane.Command));
            foreach (string tab in pane.Tabs)
            {
                string text = Invoke(pane.Command + " " + tab);
                Assert.StartsWith("Tab " + pane.Command + HelpText.PathSeparator + tab + ":", text, StringComparison.Ordinal);
                Assert.DoesNotContain(HelpText.CutNote, text);
            }
        }
    }

    [Fact]
    public void ATabByItsStartOrItsLongName_IsFound()
    {
        Assert.StartsWith("Tab /tools › GitLib:", Invoke("/tools git"), StringComparison.Ordinal);
        Assert.StartsWith("Tab /tools › HA:", Invoke("/tools home assistant"), StringComparison.Ordinal);
    }

    [Fact]
    public void ATabWithoutSettings_SaysWhatItLists()
    {
        Assert.Equal("Tab /tools › Offered: " + HelpText.ListTab("/tools", ToolsText.OfferedTabTitle), Invoke("/tools offered"));
    }

    [Fact]
    public void APane_ListsEveryTab_WithItsRowsNames()
    {
        string text = Invoke("/skills");

        Assert.StartsWith("Pane /skills: " + HelpText.SkillsPaneSummary + ".\n", text, StringComparison.Ordinal);
        Assert.Contains("\n- Offered: " + HelpText.ListTab("/skills", "Offered"), text);
        Assert.Contains("\n- Options: " + string.Join(", ", HelpLocation.OnTab("/skills", "Options").Select(r => r.Label)), text);
    }

    [Fact]
    public void Keys_ListEveryKey_AndAKeyByItself()
    {
        string keys = Invoke("keyboard shortcuts");
        Assert.StartsWith(HelpText.KeysHeading + "\n- Enter: ", keys, StringComparison.Ordinal);
        Assert.EndsWith(HelpText.KeysTail, keys, StringComparison.Ordinal);
        Assert.Equal(keys, Invoke(kind: "keys"));
        Assert.Equal("Key Ctrl+H: open help (/help)", Invoke("ctrl+h"));
    }

    [Fact]
    public void PlainWords_ScoreTheSettingNamedLikeThemFirst()
    {
        string text = Invoke("how do I change the push to talk key");

        Assert.StartsWith(HelpText.MatchesHeading("how do I change the push to talk key") + "\n\nSetting STT push-to-talk key\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainWords_UnderACommandKind_FindOnlyCommands()
    {
        string text = Invoke("photo webcam", "command");

        Assert.StartsWith(HelpText.MatchesHeading("photo webcam") + "\n\nCommand /camera:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Setting ", text);
    }

    [Fact]
    public void NothingMatching_SaysSo()
    {
        Assert.Equal(HelpText.NothingFound("zzqx plorb"), Invoke("zzqx plorb"));
    }

    [Fact]
    public void ABadKind_IsAnErrorSentence()
    {
        Assert.Equal("Unknown kind 'colour'; use any, command, setting, pane or keys.", Invoke("x", "colour"));
    }

    [Fact]
    public void ALongAnswer_IsCut_WithTheNote()
    {
        string text = Invoke("/settings general");
        string capped = HelpText.Cap(text + "\n" + new string('x', HelpText.MaxChars));

        Assert.True(capped.Length <= HelpText.MaxChars, capped.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.EndsWith("\n" + HelpText.CutNote, capped, StringComparison.Ordinal);
        Assert.Same(text, HelpText.Cap(text));
    }

    [Fact]
    public void FirstSentence_StopsAtTheFirstFullStop_AndCapsALongOne()
    {
        Assert.Equal("One.", HelpText.FirstSentence("One. Two."));
        Assert.Equal("No stop", HelpText.FirstSentence("No stop"));
        Assert.Equal(200, HelpText.FirstSentence(new string('a', 300)).Length);
    }

    // ── The tool ─────────────────────────────────────────────────────────────

    [Fact]
    public void Schema_HasAnOptionalQueryAndKind()
    {
        Assert.Equal("neon_help", NeonHelpTool.ToolName);
        Assert.Equal(NeonHelpTool.ToolName, _tool.Name);
        Assert.Contains("NeonSidekick's own manual", _tool.Description);

        var schema = _tool.JsonSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var properties = schema.GetProperty("properties");
        Assert.Equal([NeonHelpTool.QueryArgument, NeonHelpTool.KindArgument], properties.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(HelpText.Kinds, properties.GetProperty("kind").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(schema.TryGetProperty("required", out _));
    }

    [Fact]
    public void PlanMode_KeepsIt()
    {
        Assert.True(PlanTools.Allowed(NeonHelpTool.ToolName));
    }

    [Fact]
    public void TheRule_RidesAfterTheFileRule_OnlyWhileOffered()
    {
        Assert.Equal(Assistant.OperatingRules + " " + Assistant.HelpRule, Assistant.DefaultRules(markdown: false, tools: true, help: true));
        Assert.Equal(Assistant.OperatingRules, Assistant.DefaultRules(markdown: false, tools: true));
        Assert.DoesNotContain(Assistant.HelpRule, Assistant.DefaultRules(markdown: false, tools: false, help: true));
        Assert.Contains(NeonHelpTool.ToolName, Assistant.HelpRule);
    }

    [Fact]
    public void TheGroup_StandsAfterTheTimers_WithoutASwitch_AndCountsASwitchedOffTool()
    {
        var help = ChatScreen.HelpTools();
        var groups = SystemPromptSummary.ToolGroups([], [], [], [], memoryEnabled: false, disabled: ToolsText.DisabledSet([NeonHelpTool.ToolName]), help: help);

        Assert.Equal(["Clock (0)", "Timers (0)", HelpText.GroupTitle + " (0 of 1)", "Files (0)"], groups.Take(4).Select(g => g.Name).ToArray());
        var group = groups[2];
        Assert.Null(group.Switch);
        Assert.False(group.Offers(NeonHelpTool.ToolName));
    }
}
