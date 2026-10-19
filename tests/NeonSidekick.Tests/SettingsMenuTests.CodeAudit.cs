using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The code audit's six rows (2026-10-18): the Code audit section of Servers › Common after Plan mode, their values and editors.</summary>
public partial class SettingsMenuTests
{
    [Fact]
    public void TheCodeAuditRows_AreASectionAfterPlanMode_LabeledLLM()
    {
        var common = SettingsMenu.TabFields[(int)SettingsTab.Common].ToList();
        Assert.Equal(common.IndexOf(SettingsField.RalphLoopStopTone) + 1, common.IndexOf(SettingsField.CodeAuditPasses));   // the Ralph loop's seven rows between since later on 2026-10-18
        var section = SettingsMenu.TabSections[SettingsTab.Common].Single(s => s.Title == "Code audit");
        Assert.Equal(SettingsField.CodeAuditPasses, section.First);
        Assert.Equal(
            ["LLM code audit passes", "LLM code audit clean passes", "LLM code audit ask user", "LLM code audit focus", "LLM code audit web research", "LLM code audit pass tool calls"],
            new[] { SettingsField.CodeAuditPasses, SettingsField.CodeAuditCleanPasses, SettingsField.CodeAuditAskUser, SettingsField.CodeAuditFocus, SettingsField.CodeAuditWebResearch, SettingsField.CodeAuditPassToolCalls }.Select(SettingsMenu.FieldName));
        Assert.True(SettingsMenu.IsToggle(SettingsField.CodeAuditAskUser));
        Assert.True(SettingsMenu.IsToggle(SettingsField.CodeAuditWebResearch));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.CodeAuditPasses));
    }

    [Fact]
    public void TheCodeAuditRows_DefaultsAndValues()
    {
        var data = new AppSettingsData();
        Assert.Equal((5, 2, true, false, 40), (data.CodeAuditPasses, data.CodeAuditCleanPasses, data.CodeAuditAskUser, data.CodeAuditWebResearch, data.CodeAuditPassToolCalls));
        Assert.Equal(NeonSidekick.Audits.AuditFocus.Dimensions, data.CodeAuditFocus);
        Assert.Equal("5 passes", SettingsMenu.FieldValue(SettingsField.CodeAuditPasses, data, _settings.ProfileDirectory));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.CodeAuditAskUser, data, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.CodeAuditWebResearch, data, _settings.ProfileDirectory));
        Assert.Equal("40 tool calls", SettingsMenu.FieldValue(SettingsField.CodeAuditPassToolCalls, data, _settings.ProfileDirectory));
        Assert.Equal("no budget", SettingsMenu.ToolCallBudget(0));
        // The focus row's three forms, a null focus read as all.
        Assert.Equal("all", SettingsMenu.CodeAuditFocusValue(data.CodeAuditFocus));
        Assert.Equal("all", SettingsMenu.CodeAuditFocusValue(null));
        Assert.Equal("security, tests", SettingsMenu.CodeAuditFocusValue(["tests", "security"]));
        Assert.Equal("4 of 5", SettingsMenu.CodeAuditFocusValue(["security", "correctness", "performance", "tests"]));
        // The ranges are the plan gap loop's own.
        Assert.Equal((AppSettingsData.MinPlanGapPasses, AppSettingsData.MaxPlanGapPasses), (AppSettingsData.MinCodeAuditPasses, AppSettingsData.MaxCodeAuditPasses));
        Assert.Equal((AppSettingsData.MinPlanGapCleanPasses, AppSettingsData.MaxPlanGapCleanPasses), (AppSettingsData.MinCodeAuditCleanPasses, AppSettingsData.MaxCodeAuditCleanPasses));
    }

    [Fact]
    public void TheFocusChecklist_HasAllAndDefault_NoNone()
    {
        Assert.Equal([SettingsMenu.SelectAllButton, SettingsMenu.DefaultsButton], SettingsMenu.CodeAuditFocusButtons.Select(b => b.Title));
        Assert.DoesNotContain("none", SettingsMenu.CodeAuditFocusKeys);
    }

    [Fact]
    public async Task TheFocusChecklist_FlipsADimension()
    {
        Down((int)SettingsField.CodeAuditFocus);
        Push(Keys.Enter, Keys.Enter, Keys.Escape, Keys.Escape);   // open, flip security off, leave the checklist, leave

        await _menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["correctness", "performance", "maintainability", "tests"], _settings.Current.CodeAuditFocus);
    }

    [Fact]
    public async Task TheFocusChecklist_RefusesToUntickTheLast()
    {
        _settings.Update(d => d.CodeAuditFocus = ["security"]);
        Down((int)SettingsField.CodeAuditFocus);
        Push(Keys.Enter, Keys.Enter, Keys.Escape, Keys.Escape);

        await _menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["security"], _settings.Current.CodeAuditFocus);
        Assert.Contains(SettingsMenu.CodeAuditFocusLastError, Output);
    }

    [Theory]
    [InlineData(SettingsField.CodeAuditPasses, "51")]
    [InlineData(SettingsField.CodeAuditCleanPasses, "0")]
    [InlineData(SettingsField.CodeAuditPassToolCalls, "501")]
    public async Task TheTypedRows_RefuseOutOfRange(SettingsField field, string typed)
    {
        Down((int)field);
        Push(Keys.Enter);
        Backspace(5);
        Push([.. typed.Select(Keys.Char), Keys.Enter, Keys.Escape]);

        await _menu.ShowAsync(CancellationToken.None);

        string error = field switch
        {
            SettingsField.CodeAuditPasses => SettingsMenu.CodeAuditPassesRangeError,
            SettingsField.CodeAuditCleanPasses => SettingsMenu.CodeAuditCleanPassesRangeError,
            _ => SettingsMenu.CodeAuditPassToolCallsRangeError,
        };
        Assert.Contains(SettingsMenu.FieldName(field) + " " + error, Output);
    }
}
