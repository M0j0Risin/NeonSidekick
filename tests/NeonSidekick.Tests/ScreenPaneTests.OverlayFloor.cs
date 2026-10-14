using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.Tests;

/// <summary>
/// The pane's held height (2026-10-14, the user's ask: <c>/tools › Core › Ask user</c>'s two-row picker dropped the title from the
/// window's top to its bottom): a titled overlay shorter than the tallest pane drawn since it opened gets blank rows under its content,
/// so the upper rule stays on its row; a close, a height change and a new Menus max height start afresh; a pane opened over another
/// hands it back as it was (<see cref="ScreenPane.RestoreOverlay"/>).
/// </summary>
public partial class ScreenPaneTests
{
    /// <summary><paramref name="count"/> rows, <c>line 0</c> on.</summary>
    private static IRenderable Lines(int count) => new Rows(Enumerable.Range(0, count).Select(i => new Markup("line " + i)));

    /// <summary>A pane in a window of twenty rows, the Status bar's text <c>idle</c>.</summary>
    private ScreenPane HeldPane()
    {
        _console.Profile.Height = 20;
        _cursorTop = 100;
        var pane = Pane();
        pane.Hint = () => "idle";
        pane.Show();
        return pane;
    }

    [Fact]
    public void AShorterPage_IsHeldAtTheTallerOnesHeight_TheBlankRowsUnderItsContent()
    {
        using var pane = HeldPane();
        pane.ShowOverlay(Lines(8), "keys", close: true);
        int tall = pane.DrawnRows;
        Assert.Equal(3 + 8, tall);

        pane.ShowOverlay(Lines(2), "keys", close: true);

        Assert.Equal(tall, pane.DrawnRows);
        Assert.Equal(8, pane.OverlayRows);
        Assert.EndsWith("line 1\n\n\n\n\n\n\n" + Rule(40) + "\nkeys", Output);

        // A click on a blank row is on the pane (a menu reads it as a miss), never off it.
        Assert.True(pane.TryHitOverlay(5, 100 + 5, out int row));
        Assert.Equal(5, row);
        Assert.False(pane.TryHitOutside(5, 100 + 5));
    }

    [Fact]
    public void ATallerPage_RaisesTheHold_AndAShorterOneAfterKeepsIt_UntilTheClose()
    {
        using var pane = HeldPane();
        pane.ShowOverlay(Lines(3), "keys", close: true);
        Assert.Equal(3 + 3, pane.DrawnRows);

        pane.ShowOverlay(Lines(9), "keys", close: true);
        Assert.Equal(3 + 9, pane.DrawnRows);

        pane.ShowOverlay(Lines(3), "keys", close: true);
        Assert.Equal(3 + 9, pane.DrawnRows);

        // The close ends the visit: the next pane opens at its own height.
        pane.CloseOverlay();
        Assert.Equal(ScreenPane.PaneRows, pane.DrawnRows);
        pane.ShowOverlay(Lines(2), "keys", close: true);
        Assert.Equal(3 + 2, pane.DrawnRows);
        Assert.Equal(2, pane.OverlayRows);
    }

    [Fact]
    public void TheCompletionList_IsNeverHeld()
    {
        using var pane = HeldPane();
        pane.ShowOverlay(Lines(6), "list", input: true);
        Assert.Equal(6, pane.OverlayRows);

        pane.ShowOverlay(Lines(2), "list", input: true);

        Assert.Equal(2, pane.OverlayRows);
        Assert.Equal(3 + 2 + 1, pane.DrawnRows);
    }

    [Fact]
    public void ATypedEdit_HasItsBlankRowsOverTheInput_ByTheLowerRule()
    {
        using var pane = HeldPane();
        pane.ShowOverlay(Lines(8), "keys", close: true);
        int tall = pane.DrawnRows;

        pane.ShowOverlay(Lines(2), "type", input: true, close: true);

        Assert.Equal(tall, pane.DrawnRows);
        Assert.Equal(8 - 1, pane.OverlayRows);   // the input's row is one of the held ones
        Assert.EndsWith("line 1\n\n\n\n\n\n› \n" + Rule(40) + "\ntype", Output);
    }

    [Fact]
    public void AWidthChange_KeepsTheHold_AHeightChangeStartsItAfresh()
    {
        using var pane = HeldPane();
        pane.ShowOverlay(Lines(8), "keys", close: true);
        pane.ShowOverlay(Lines(2), "keys", close: true);
        Assert.Equal(8, pane.OverlayRows);

        _console.Profile.Width = 50;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(8, pane.OverlayRows);

        _console.Profile.Height = 18;
        _time.Advance(ScreenPane.Tick);
        Assert.Equal(2, pane.OverlayRows);
        Assert.Equal(3 + 2, pane.DrawnRows);
    }

    [Fact]
    public void ANewMenusMaxHeight_StartsTheHoldAfresh()
    {
        using var pane = HeldPane();
        string word = MenuHeight.Default;
        pane.MenuHeight = () => word;
        pane.ShowOverlay(Lines(8), "keys", close: true);
        pane.ShowOverlay(Lines(2), "keys", close: true);
        Assert.Equal(8, pane.OverlayRows);

        word = "half";
        pane.ShowOverlay(Lines(2), "keys", close: true);

        Assert.Equal(2, pane.OverlayRows);
    }

    [Fact]
    public void ABarGoingOff_IsTakenUpByABlankRow_TheUpperRuleStaysOnItsRow()
    {
        using var pane = HeldPane();
        string mode = HintsBarMode.Always;
        pane.HintsBar = () => mode;
        pane.ShowOverlay(Lines(8), "keys", close: true);
        pane.ShowOverlay(Lines(2), "keys", close: true);
        int held = pane.DrawnRows;
        Assert.Equal(1, pane.HintsRows);
        Assert.Equal(8, pane.OverlayRows);

        mode = HintsBarMode.Off;
        pane.ShowOverlay(Lines(2), "keys", close: true);

        Assert.Equal(0, pane.HintsRows);
        Assert.Equal(held, pane.DrawnRows);
        Assert.Equal(9, pane.OverlayRows);
    }

    [Fact]
    public void Scrolled_AShorterPage_KeepsTheScrollsRowAndTheHeight()
    {
        using var pane = HeldPane();
        for (int i = 0; i < 40; i++)
        {
            pane.Write(new Markup("flow " + i + "\n"));
        }

        pane.ShowOverlay(Lines(8), "keys", close: true);
        pane.ScrollWheel(1);
        int held = pane.DrawnRows;
        Assert.Equal(3 + 8 + 1, held);   // the scroll's row over the rest

        pane.ShowOverlay(Lines(2), "keys", close: true);

        Assert.Equal(held, pane.DrawnRows);
        Assert.Equal(8, pane.OverlayRows);
    }

    [Fact]
    public void RestoreOverlay_HandsBackTheOpenerAtItsOwnHeight_TallerOrShorter()
    {
        using var pane = HeldPane();
        pane.ShowOverlay(Lines(3), "menu", close: true);
        var under = pane.CurrentOverlay;
        Assert.True(under.IsSome);

        // A taller pane over it grows the pane; closing it gives the height back (the user's pick).
        pane.ShowOverlay(Lines(10), "details", close: true);
        Assert.Equal(3 + 10, pane.DrawnRows);
        pane.RestoreOverlay(under);
        Assert.True(pane.OverlayOpen);
        Assert.Equal(3 + 3, pane.DrawnRows);
        Assert.EndsWith("line 2\n" + Rule(40) + "\nmenu", Output);

        // A shorter one is held at the opener's height and moves nothing either way.
        under = pane.CurrentOverlay;
        pane.ShowOverlay(Lines(1), "details", close: true);
        Assert.Equal(3 + 3, pane.DrawnRows);
        pane.RestoreOverlay(under);
        Assert.Equal(3 + 3, pane.DrawnRows);
    }

    [Fact]
    public void RestoreOverlay_KeepsTheOpenersHeldHeight_UnlessTheWindowsHeightChanged()
    {
        using var pane = HeldPane();
        pane.ShowOverlay(Lines(8), "menu", close: true);
        pane.ShowOverlay(Lines(3), "picker", close: true);   // held at the menu's height
        var under = pane.CurrentOverlay;
        pane.ShowOverlay(Lines(12), "details", close: true);

        pane.RestoreOverlay(under);
        Assert.Equal(3 + 8, pane.DrawnRows);

        under = pane.CurrentOverlay;
        pane.ShowOverlay(Lines(12), "details", close: true);
        _console.Profile.Height = 18;   // before the tick sees it
        pane.RestoreOverlay(under);
        Assert.Equal(3 + 3, pane.DrawnRows);
    }

    [Fact]
    public void RestoreOverlay_WithNoneUnder_Closes_AndADismissalClosesTheNestedPaneAlone()
    {
        using var pane = HeldPane();
        var nothing = pane.CurrentOverlay;
        Assert.False(nothing.IsSome);
        pane.ShowOverlay(Lines(2), "info", close: true);
        pane.RestoreOverlay(nothing);
        Assert.False(pane.OverlayOpen);
        Assert.EndsWith(Rule(40) + "\nidle", Output);

        // A double-click off the nested pane: it goes, the one under it stays (the approval's ESC would be Deny).
        pane.ShowOverlay(Lines(4), "approval", close: true);
        var under = pane.CurrentOverlay;
        pane.ShowOverlay(Lines(6), "details", close: true);
        pane.Dismiss();
        Assert.True(pane.Dismissed);
        pane.RestoreOverlay(under);
        Assert.False(pane.Dismissed);
        Assert.True(pane.OverlayOpen);
        Assert.EndsWith("line 3\n" + Rule(40) + "\napproval", Output);
    }
}
