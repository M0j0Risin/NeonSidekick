using NeonSidekick.Prompts;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/prompts</c> headless (2026-10-13): no pane and no draft line, so the list and a prompt are printed; save, remove and
/// <c>list --clear</c> (at once) work, ahead of the server check, and none of it reaches the model.
/// </summary>
public partial class SidekickAppTests
{
    [Fact]
    public async Task Headless_Prompts_SavesListsPrintsAndRemoves_TheModelAskedNothing()
    {
        string output = await Headless("/prompts save Summarize this.\n/prompts save Review the diff\n/prompts list\n/prompts 2\n/prompts 9\n/prompts remove 1\n/prompts\n/prompts list --clear\n");

        var prompts = new SavedPrompts(_settings.ProfileDirectory);
        Assert.Contains(SavedPromptText.Caption(2) + Environment.NewLine, output);
        Assert.Contains("2. Review the diff" + Environment.NewLine, output);
        Assert.Contains("prompt 2: Review the diff" + Environment.NewLine, output);
        Assert.Contains("[error] There is no saved prompt 9", output);
        Assert.Contains(SavedPromptText.Caption(1) + Environment.NewLine, output);   // the bare word prints the list after the remove
        Assert.Contains(SavedPromptText.Cleared(1) + Environment.NewLine, output);   // at once, no question
        Assert.Empty(prompts.List());
        Assert.Empty(_chat.Requests);
    }
}
