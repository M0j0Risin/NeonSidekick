using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The user's case for the pane's held height (2026-10-14): <c>/tools › Core › Ask</c>, Enter on <c>Ask user</c> opened its two-row
/// on/off picker at the window's bottom, the title dropping from the top; now the picker stays on the menu's rows and ESC brings
/// the page back where it was.
/// </summary>
public partial class ToolsMenuTests
{
    [Fact]
    public async Task OnThePane_AskUsersPicker_OpensWhereTheMenuWas_AndEscKeepsIt()
    {
        var (menu, pane, input) = ClickablePaneMenu(cursorTop: 100);
        var heights = new List<int>();
        var steps = new Queue<ConsoleKeyInfo>([Keys.Right, Keys.Enter, Keys.Escape, Keys.Escape]);
        // At every read the script waits for, the pane as drawn, then the next key: Offered, Core › Ask, the picker, Core › Ask again.
        input.OnWait = () =>
        {
            heights.Add(pane.DrawnRows);
            if (steps.TryDequeue(out var key))
            {
                input.Push(key);
            }
        };

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(_settings.Current.AskUser);   // ESC on the picker: nothing changed
        Assert.Contains(CoreCrumb + " › Ask user", Output);
        Assert.Equal(4, heights.Count);
        // The picker (two rows) at the menu's own height: the upper rule, and the title under it, on the row the strip had.
        Assert.Equal(heights[1], heights[2]);
        Assert.Equal(heights[1], heights[3]);
        Assert.True(heights[2] > ScreenPane.PaneRows + 4);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }
}
