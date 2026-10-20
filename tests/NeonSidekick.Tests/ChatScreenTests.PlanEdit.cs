using NeonSidekick.App;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/plan template</c> and <c>/plan edit [name]</c> (2026-10-20, the user's ask): the example plan written (and written over) with its
/// notice; a plan file handed to the file opener with plan mode untouched; bare <c>/plan edit</c>'s picker, which lists the template and has no
/// buttons; a one-word name that matches nothing is the error and that picker; several words stay a requirement.
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task PlanTemplate_WritesTheExample_OverAnOldCopy_AndSaysTheWayOn()
    {
        PickerFixture(Line("/plan template"), Line("/exit"));
        File.WriteAllText(Path.Combine(PlansFolder, "plan-template.md"), "an old copy");

        string output = await RunAsync();

        Assert.Equal(PlanFiles.TemplateText(), File.ReadAllText(Path.Combine(PlansFolder, "plan-template.md")));
        Assert.Contains(PlanText.TemplateWrittenNotice(PlanFiles.TemplatePath), output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task PlanEdit_AName_HandsTheFileToTheOpener_PlanModeUntouched()
    {
        PickerFixture(Line("/plan edit zz-other"), Line("/exit"));

        string output = await RunAsync();

        Assert.Equal([Path.Combine(PlansFolder, "zz-other.md")], _openedFiles);
        Assert.Contains(ChatScreen.OpenOpenedNotice(Path.Combine(".neon", "plans", "zz-other.md")), output);   // /open's notice, the sandbox's spelling
        Assert.Empty(_chat.Requests);
        Assert.DoesNotContain(PlanText.Placeholder, output);   // not planning
    }

    [Fact]
    public async Task PlanEdit_TheTemplate_IsANameItTakes()
    {
        PickerFixture(Line("/plan template"), Line("/plan edit plan-template"), Line("/exit"));

        await RunAsync();

        Assert.Equal([Path.Combine(PlansFolder, "plan-template.md")], _openedFiles);
    }

    [Fact]
    public async Task PlanEdit_Bare_IsThePicker_WithTheTemplate_NoButtons_EnterEdits()
    {
        PickerFixture(Line("/plan template"), Line("/plan edit"), Key(Keys.Char(PlanText.OpenKey)), Key(Keys.Enter), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(PlanText.PickerKeys(PlanPickMode.Edit, ralphButton: true), output);
        Assert.Contains("plan-template — ", output);
        Assert.DoesNotContain(PlanText.OpenButton, output);
        Assert.Single(_openedFiles);   // o is a filter letter here, not the open button; Enter edits
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task PlanEdit_AWordNamingNoPlan_IsTheError_AndThePicker_SeveralWordsARequirement()
    {
        PickerFixture(Line("/plan edit nope"), Key(Keys.Escape), Line("/plan edit the contributing guide"), Line("/exit"));
        _chat.EnqueueText("Here is a plan.");

        string output = await RunAsync();

        Assert.Contains(PlanText.NoSuchPlanError("nope", PlanText.EditWord), output);
        Assert.Contains(PlanText.PickerKeys(PlanPickMode.Edit, ralphButton: true), output);
        Assert.Empty(_openedFiles);
        Assert.Contains("edit the contributing guide", LastUserText(Assert.Single(_chat.Requests)));   // plan mode on for that requirement
    }
}
