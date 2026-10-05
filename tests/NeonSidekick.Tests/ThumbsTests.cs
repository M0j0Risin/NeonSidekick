using NeonSidekick.App;
using NeonSidekick.Images;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// The thumbnail browser's decisions (<see cref="ThumbsState"/>, <see cref="ThumbCache"/>), the picture menu's
/// (<see cref="ContextMenuState"/>, <see cref="PictureMenu.Build"/>) and their words, 2026-10-04; the windows themselves are the
/// smoke's and the user's.
/// </summary>
public sealed class ThumbsTests
{
    private static readonly DateTime T0 = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    // A 1000 × 800 client at 96 DPI with 20-pixel captions: content width 988 (the 12-pixel bar kept), gap 12.
    private static ThumbsState Grid(int count, int width = 1000, int height = 800, uint dpi = 96)
    {
        var state = new ThumbsState();
        state.Reset(@"D:\pics", Enumerable.Range(0, count).Select(i => new ThumbEntry($@"D:\pics\{i:D3}.png", T0.AddMinutes(i))));
        state.Relayout(width, height, dpi, 20);
        return state;
    }

    [Fact]
    public void Reset_SortsOldestFirst_TheNameBreakingATie_NothingSelected()
    {
        var state = new ThumbsState();
        state.Reset(@"D:\pics", [new(@"D:\pics\c.png", T0.AddMinutes(2)), new(@"D:\pics\b.png", T0), new(@"D:\pics\a.png", T0), new(@"D:\pics\A.PNG", T0)]);

        Assert.Equal([@"D:\pics\a.png", @"D:\pics\b.png", @"D:\pics\c.png"], state.Entries.Select(e => e.Path));
        Assert.Null(state.Selected);
        Assert.Null(state.SelectedPath);
        Assert.Equal(0, state.ScrollTop);
    }

    [Fact]
    public void FitTile_TheLargestThatFitsEveryPicture_ElseTheSmallest()
    {
        Assert.Equal(256, ThumbsState.FitTile(988, 800, 0, 96, 20));     // none: the largest
        Assert.Equal(256, ThumbsState.FitTile(988, 800, 6, 96, 20));     // 3 columns × 2 rows of 256 + 20 + 12 fit 800
        int tile = ThumbsState.FitTile(988, 800, 40, 96, 20);
        Assert.InRange(tile, 96, 255);
        int columns = (988 - 12) / (tile + 12), rows = (40 + columns - 1) / columns;
        Assert.True(12 + rows * (tile + 20 + 12) <= 800);
        Assert.Equal(96, ThumbsState.FitTile(988, 800, 5000, 96, 20));   // too many: the smallest, and the grid scrolls
        Assert.Equal(144, ThumbsState.FitTile(988, 800, 5000, 144, 30)); // scaled by the DPI
        Assert.Equal(384, ThumbsState.FitTile(4000, 4000, 1, 144, 30));
    }

    [Fact]
    public void Add_GoesOnTheEnd_AndNoTileMoves_TheSizeKept()
    {
        var state = Grid(6);
        int tile = state.Tile;
        var before = Enumerable.Range(0, 6).Select(state.TileRect).ToList();

        Assert.Equal(ThumbChange.Added, state.Add(@"D:\pics\new.png", T0.AddDays(1)));
        Assert.Equal(ThumbChange.Added, state.Add(@"D:\pics\older-but-late.png", T0.AddDays(-1)));   // an arrival goes last, whatever its date

        Assert.Equal(tile, state.Tile);
        Assert.Equal(before, Enumerable.Range(0, 6).Select(state.TileRect));
        Assert.Equal(@"D:\pics\older-but-late.png", state.Entries[^1].Path);
        Assert.Equal(ThumbChange.Changed, state.Add(@"D:\pics\002.png", T0.AddDays(2)));   // written again: read in place
        Assert.Equal(@"D:\pics\002.png", state.Entries[2].Path);
    }

    [Fact]
    public void Add_KeepsAViewAtTheBottom_OnlyWhenTheGridAlreadyScrolled()
    {
        var fitted = Grid(6);
        Assert.Equal(0, fitted.MaxScroll);
        for (int i = 0; i < 20; i++)
        {
            fitted.Add($@"D:\pics\late{i}.png", T0.AddDays(1));
        }

        Assert.True(fitted.MaxScroll > 0);
        Assert.Equal(0, fitted.ScrollTop);   // a fitted grid is never scrolled by an arrival

        var tall = Grid(400);
        tall.ScrollTo(tall.MaxScroll);
        tall.Add(@"D:\pics\next.png", T0.AddDays(1));
        tall.Add(@"D:\pics\next2.png", T0.AddDays(1));
        tall.Add(@"D:\pics\next3.png", T0.AddDays(1));
        tall.Add(@"D:\pics\next4.png", T0.AddDays(1));
        tall.Add(@"D:\pics\next5.png", T0.AddDays(1));
        Assert.Equal(tall.MaxScroll, tall.ScrollTop);   // following the newest

        var middle = Grid(400);
        middle.ScrollTo(100);
        middle.Add(@"D:\pics\next.png", T0.AddDays(1));
        Assert.Equal(100, middle.ScrollTop);
    }

    [Fact]
    public void Rename_KeepsThePlace_OntoAListedName_IsAWriteAgain()
    {
        var state = Grid(3);

        Assert.Equal(ThumbChange.Renamed, state.Rename(@"D:\pics\001.png", @"D:\pics\renamed.png", T0));
        Assert.Equal(@"D:\pics\renamed.png", state.Entries[1].Path);
        Assert.Equal(ThumbChange.Changed, state.Rename(@"D:\pics\renamed.png", @"D:\pics\002.png", T0));
        Assert.Equal([@"D:\pics\000.png", @"D:\pics\002.png"], state.Entries.Select(e => e.Path));
        Assert.Equal(ThumbChange.Added, state.Rename(@"D:\pics\x.png.tmp", @"D:\pics\x.png", T0));   // an old name not listed: an arrival
        Assert.Equal(@"D:\pics\x.png", state.Entries[^1].Path);
    }

    [Fact]
    public void Remove_ClosesUp_TheSelectionOnTheSamePictureOrTheNext()
    {
        var state = Grid(5);
        state.SelectIndex(3);

        Assert.True(state.Remove(@"D:\pics\001.png"));
        Assert.Equal(@"D:\pics\003.png", state.SelectedPath);   // the same picture, one place on
        Assert.True(state.Remove(@"D:\pics\003.png"));
        Assert.Equal(@"D:\pics\004.png", state.SelectedPath);   // the next one in its place
        Assert.True(state.Remove(@"D:\pics\004.png"));
        Assert.Equal(@"D:\pics\002.png", state.SelectedPath);   // the last one gone: the one before
        Assert.False(state.Remove(@"D:\pics\nope.png"));
        state.Remove(@"D:\pics\000.png");
        state.Remove(@"D:\pics\002.png");
        Assert.Null(state.Selected);
    }

    [Fact]
    public void Select_ScrollsIntoView_AndAddsAPathNotListedYet()
    {
        var state = Grid(400);
        Assert.True(state.Select(@"D:\pics\399.png", T0));
        Assert.Equal(state.MaxScroll, state.ScrollTop);
        Assert.False(state.Select(@"D:\pics\399.png", T0));   // already selected
        Assert.True(state.Select(@"D:\pics\000.png", T0));
        Assert.Equal(0, state.ScrollTop);
        Assert.True(state.Select(@"D:\pics\fresh.png", T0));   // written a moment ago, its event not read yet
        Assert.Equal(@"D:\pics\fresh.png", state.Entries[^1].Path);
        Assert.Equal(400, state.Selected);
    }

    [Fact]
    public void HitTest_FindsTheTileAndItsCaption_NeverTheGaps_ScrollCounted()
    {
        var state = Grid(10);
        var (x, y, side) = state.TileRect(0);

        Assert.Equal(0, state.HitTest(x, y));
        Assert.Equal(0, state.HitTest(x + side - 1, y + side + 19));   // the caption's last row
        Assert.Null(state.HitTest(x + side + 1, y));                   // the gap to the right
        Assert.Null(state.HitTest(x, y + side + 21));                  // the gap below the caption
        Assert.Null(state.HitTest(0, 0));                              // the margin
        Assert.Null(state.HitTest(state.ContentWidth + 1, y));         // the scroll bar
        var (x1, y1, _) = state.TileRect(state.Columns);
        Assert.Equal(state.Columns, state.HitTest(x1 + 1, y1 + 1));

        var tall = Grid(400);
        tall.ScrollTo(tall.PitchY * 2);
        var (x2, y2, _) = tall.TileRect(2 * tall.Columns);
        Assert.Equal(tall.GapPixels, y2);
        Assert.Equal(2 * tall.Columns, tall.HitTest(x2, y2));
    }

    [Fact]
    public void Keys_MoveTheSelection_ByTilesRowsAndPages()
    {
        var state = Grid(400);
        int columns = state.Columns, page = state.PageRows * columns;

        Assert.True(state.Apply(ThumbsAction.Right));   // nothing selected: the first in view
        Assert.Equal(0, state.Selected);
        Assert.False(state.Apply(ThumbsAction.Left));
        Assert.True(state.Apply(ThumbsAction.Down));
        Assert.Equal(columns, state.Selected);
        Assert.True(state.Apply(ThumbsAction.Up));
        Assert.Equal(0, state.Selected);
        Assert.True(state.Apply(ThumbsAction.PageDown));
        Assert.Equal(page, state.Selected);
        Assert.True(state.Apply(ThumbsAction.Last));
        Assert.Equal(399, state.Selected);
        Assert.Equal(state.MaxScroll, state.ScrollTop);
        Assert.True(state.Apply(ThumbsAction.First));
        Assert.Equal(0, state.ScrollTop);

        // ↓ from a row whose tile below is past the end lands on the last tile.
        var ragged = Grid(5);
        Assert.Equal(3, ragged.Columns);
        ragged.SelectIndex(2);
        Assert.True(ragged.Apply(ThumbsAction.Down));
        Assert.Equal(4, ragged.Selected);
        Assert.False(ragged.Apply(ThumbsAction.Down));
    }

    [Fact]
    public void Zoom_ChangesTheSize_KeptOnResize_UntilRefit()
    {
        var state = Grid(6);
        Assert.Equal(256, state.Tile);
        Assert.True(state.Zoom(-1));
        Assert.Equal(205, state.Tile);   // 256 / 1.25
        Assert.True(state.Zoomed);
        state.Relayout(1200, 900, 96, 20);
        Assert.Equal(205, state.Tile);
        Assert.True(state.Zoom(20));
        Assert.Equal(ThumbsState.ZoomMax, state.Tile);
        Assert.False(state.Zoom(1));     // already the largest
        Assert.True(state.Zoom(-40));
        Assert.Equal(ThumbsState.ZoomMin, state.Tile);
        state.Refit();
        Assert.False(state.Zoomed);
        Assert.Equal(256, state.Tile);
        state.Zoom(1);
        state.Relayout(1200, 900, 192, 40);   // a zoom carried to a new DPI
        Assert.Equal(640, state.Tile);
    }

    [Fact]
    public void Zoom_KeepsTheSelectedTileWhereItWasOnTheScreen()
    {
        var state = Grid(400);
        state.Select(@"D:\pics\200.png", T0);
        int before = state.TileRect(200).Y;

        state.Zoom(2);

        Assert.InRange(state.TileRect(200).Y, before - 1, before + 1);
    }

    [Fact]
    public void Visible_IsTheTilesInView_AndTheRowsAhead()
    {
        var state = Grid(400);
        var (first, last) = state.Visible();
        Assert.Equal(0, first);
        Assert.Equal(state.Columns * ((800 / state.PitchY) + 1) - 1, last);
        var (_, aheadLast) = state.Visible(2);
        Assert.Equal(last + 2 * state.Columns, aheadLast);
        Assert.Equal((0, -1), new ThumbsState().Visible());
    }

    [Fact]
    public void WheelPixels_ARowANotch_TouchpadTurnsAddedUp()
    {
        int remainder = 0;
        Assert.Equal(268, ThumbsState.WheelPixels(ref remainder, -120, 268));   // toward the user: down
        Assert.Equal(-268, ThumbsState.WheelPixels(ref remainder, 120, 268));
        int total = 0;
        for (int i = 0; i < 120; i++)
        {
            total += ThumbsState.WheelPixels(ref remainder, 1, 268);   // a touchpad's notch in 120 small turns
        }

        Assert.Equal(-268, total);
    }

    [Theory]
    [InlineData(0x25, false, false, false, ThumbsAction.Left)]
    [InlineData(0x27, false, false, false, ThumbsAction.Right)]
    [InlineData(0x26, false, false, false, ThumbsAction.Up)]
    [InlineData(0x28, false, false, false, ThumbsAction.Down)]
    [InlineData(0x21, false, false, false, ThumbsAction.PageUp)]
    [InlineData(0x22, false, false, false, ThumbsAction.PageDown)]
    [InlineData(0x24, false, false, false, ThumbsAction.First)]
    [InlineData(0x23, false, false, false, ThumbsAction.Last)]
    [InlineData(0x0D, false, false, false, ThumbsAction.Open)]
    [InlineData(0x74, false, false, false, ThumbsAction.Refresh)]
    [InlineData(0x6B, false, false, false, ThumbsAction.ZoomIn)]
    [InlineData(0xBB, false, true, false, ThumbsAction.ZoomIn)]
    [InlineData(0x6D, false, false, false, ThumbsAction.ZoomOut)]
    [InlineData(0xBD, false, false, false, ThumbsAction.ZoomOut)]
    [InlineData(0x5D, false, false, false, ThumbsAction.Menu)]
    [InlineData(0x79, false, true, false, ThumbsAction.Menu)]
    [InlineData(0x79, false, false, false, ThumbsAction.None)]
    [InlineData(0x7A, false, false, false, ThumbsAction.ToggleFullScreen)]
    [InlineData(0x1B, false, false, false, ThumbsAction.Close)]
    [InlineData(0x1B, false, false, true, ThumbsAction.LeaveFullScreen)]
    [InlineData(0x2E, false, false, false, ThumbsAction.Delete)]   // 2026-10-05, the viewer's double-Del
    [InlineData(0x2E, true, false, false, ThumbsAction.None)]
    [InlineData(0x25, true, false, false, ThumbsAction.None)]   // a Ctrl chord is the terminal's
    [InlineData(0x41, false, false, false, ThumbsAction.None)]
    public void ActionFor_MapsTheKeys(int key, bool control, bool shift, bool fullScreen, ThumbsAction expected) =>
        Assert.Equal(expected, ThumbsState.ActionFor(key, control, shift, fullScreen));

    [Fact]
    public void PressDelete_ArmsTheSelected_ThenDeletesIt_OnASecondWithinTheWindow()
    {
        var state = Grid(3);
        Assert.Null(state.PressDelete(1_000));   // nothing selected: nothing armed
        Assert.False(state.DeleteArmed);

        state.SelectIndex(1);
        Assert.Null(state.PressDelete(1_000));
        Assert.True(state.DeleteArmed);
        Assert.Equal("001.png — 2/3 · " + ViewerText.DeleteArmedHint, state.Title());

        Assert.Equal(@"D:\pics\001.png", state.PressDelete(1_000 + ViewerState.DeleteArmMilliseconds));
        Assert.False(state.DeleteArmed);
        Assert.Equal("001.png — 2/3 · NeonSidekick thumbnails", state.Title());
    }

    [Fact]
    public void PressDelete_TooLate_ArmsAgain_AndTheSelectionMoving_OrDisarm_OrReset_DropsIt()
    {
        var state = Grid(3);
        state.SelectIndex(0);
        state.PressDelete(0);
        Assert.Null(state.PressDelete(ViewerState.DeleteArmMilliseconds + 1));   // late: armed afresh from now
        Assert.True(state.DeleteArmed);

        state.SelectIndex(2);
        Assert.False(state.DeleteArmed);
        Assert.Null(state.PressDelete(ViewerState.DeleteArmMilliseconds + 2));   // the new picture is only armed, not deleted
        Assert.True(state.DeleteArmed);

        Assert.True(state.Disarm());
        Assert.False(state.Disarm());
        Assert.False(state.DeleteArmed);

        state.PressDelete(10_000);
        state.Reset(@"D:\pics", state.Entries.ToList());
        state.SelectIndex(2);
        Assert.False(state.DeleteArmed);
    }

    [Fact]
    public void PressDelete_TheArm_NeverComesBack_WhenTheSelectionReturns()
    {
        // The viewer pages away and back (followThumbs: no key, no click here): one Del must only arm again (2026-10-05, the code review).
        var state = Grid(3);
        state.SelectIndex(1);
        state.PressDelete(0);
        state.SelectIndex(2);
        state.SelectIndex(1);
        Assert.False(state.DeleteArmed);
        Assert.Null(state.PressDelete(100));
        Assert.True(state.DeleteArmed);

        // Another picture leaving, the selection on the same one: the arm stands. The armed one leaving drops it.
        Assert.True(state.Remove(@"D:\pics\002.png"));
        Assert.True(state.DeleteArmed);
        Assert.True(state.Remove(@"D:\pics\001.png"));
        Assert.False(state.DeleteArmed);
    }

    [Fact]
    public void Bucket_TheSmallestThatCovers()
    {
        Assert.Equal(128, ThumbsState.Bucket(96));
        Assert.Equal(256, ThumbsState.Bucket(256));
        Assert.Equal(384, ThumbsState.Bucket(257));
        Assert.Equal(768, ThumbsState.Bucket(5000));
    }

    [Fact]
    public void Cache_KeepsTheBudget_NeverWhatIsInView_AndKnowsWhatNeedsNoRead()
    {
        var cache = new ThumbCache(3 * 4 * 100);   // three 10 × 10 tiles
        ViewerBitmap Tile(int side = 10) => new(side, side, new byte[side * side * 4]);
        cache.Put("a", 128, Tile());
        cache.Put("b", 128, Tile());
        cache.Put("c", 128, Tile());
        cache.Get("a");                        // a used again: b is now the oldest
        cache.Put("d", 128, Tile());
        cache.Trim(new HashSet<string> { "d" });

        Assert.Equal(3, cache.Count);
        Assert.Null(cache.Get("b").Bitmap);
        Assert.NotNull(cache.Get("a").Bitmap);
        cache.Put("e", 128, Tile());
        cache.Trim(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a", "c", "d", "e" });   // all in view: nothing goes
        Assert.Equal(4, cache.Count);

        var reads = new ThumbCache(ThumbCache.DefaultBudget);
        reads.Put("big", 256, Tile(256));
        reads.Put("small", 256, Tile(40));
        reads.Put("bad", 128, null);
        Assert.True(reads.Satisfies("big", 192));
        Assert.False(reads.Satisfies("big", 384));
        Assert.True(reads.Satisfies("small", 768));   // read whole: no bigger read adds anything
        Assert.True(reads.Satisfies("bad", 512));     // unreadable stays unreadable
        Assert.Equal((null, true), reads.Get("bad"));
        Assert.False(reads.Satisfies("none", 128));
        Assert.True(reads.Rename("big", "BIG2"));
        Assert.True(reads.Satisfies("big2", 256));
        Assert.True(reads.Remove("big2"));
        reads.Clear();
        Assert.Equal(0, reads.Bytes);
    }

    // ── The picture menu ────────────────────────────────────────────────────

    private static readonly IReadOnlyList<ContextMenuItem> Rows =
    [
        new("Open", 1),
        ContextMenuItem.Separator,
        new("Rotate", Children: [new("Right", 10), new("Off", 11, Enabled: false), new("Left", 12)]),
        new("Colour", Children: [new("Grey", 20)]),
        new("Delete", 3),
        new("note", Enabled: false),
    ];

    [Fact]
    public void Menu_Keys_SkipSeparatorsAndDisabledRows_Wrapping_InAndOutOfASubmenu()
    {
        var state = new ContextMenuState(Rows, keyboard: true);
        Assert.Equal(0, state.Hot);

        Assert.Equal(MenuOutcome.Changed, state.Key(ContextMenuState.VkDown));
        Assert.Equal(2, state.Hot);   // the separator skipped
        Assert.Equal(MenuOutcome.Changed, state.Key(ContextMenuState.VkRight));
        Assert.True(state.InSub);
        Assert.Equal(2, state.Open);
        Assert.Equal(0, state.SubHot);
        state.Key(ContextMenuState.VkDown);
        Assert.Equal(2, state.SubHot);   // the disabled row skipped
        state.Key(ContextMenuState.VkDown);
        Assert.Equal(0, state.SubHot);   // wrapped
        Assert.Equal(MenuOutcome.Changed, state.Key(ContextMenuState.VkLeft));
        Assert.False(state.InSub);
        Assert.Null(state.Open);
        state.Key(ContextMenuState.VkUp);
        state.Key(ContextMenuState.VkUp);
        Assert.Equal(4, state.Hot);      // up from Rotate past Open wraps to Delete; the note is never landed on
        Assert.Equal(MenuOutcome.Chosen, state.Key(ContextMenuState.VkReturn));
        Assert.Equal(3, state.Chosen);
        Assert.Equal(MenuOutcome.None, state.Key(0x41));
    }

    [Fact]
    public void Menu_Esc_LeavesTheSubmenu_ThenCloses_EnterOpensASubmenu()
    {
        var state = new ContextMenuState(Rows, keyboard: true);
        state.Key(ContextMenuState.VkDown);
        Assert.Equal(MenuOutcome.Changed, state.Key(ContextMenuState.VkReturn));
        Assert.True(state.InSub);
        Assert.Equal(MenuOutcome.Changed, state.Key(ContextMenuState.VkEscape));
        Assert.False(state.InSub);
        Assert.Equal(MenuOutcome.Close, state.Key(ContextMenuState.VkEscape));
        Assert.Equal(-1, new ContextMenuState(Rows).Hot);   // a right-click opens with nothing lit
    }

    [Fact]
    public void Menu_Mouse_HoverOpensASubmenu_AClickChooses()
    {
        var state = new ContextMenuState(Rows);
        Assert.Equal(MenuOutcome.Changed, state.HoverRoot(3));
        Assert.Equal(3, state.Open);
        Assert.Equal(MenuOutcome.None, state.HoverRoot(1));   // the separator
        Assert.Equal(MenuOutcome.Changed, state.HoverSub(0));
        Assert.True(state.InSub);
        Assert.Equal(MenuOutcome.Chosen, state.ClickSub(0));
        Assert.Equal(20, state.Chosen);
        Assert.Equal(MenuOutcome.Changed, state.HoverRoot(0));
        Assert.Null(state.Open);
        Assert.Equal(MenuOutcome.Changed, state.ClickRoot(2));   // a submenu's row opens it
        Assert.Equal(MenuOutcome.None, state.ClickSub(1));       // disabled
        Assert.Equal(MenuOutcome.None, state.ClickRoot(5));
        Assert.Equal(MenuOutcome.Chosen, state.ClickRoot(0));
        Assert.Equal(1, state.Chosen);
    }

    [Fact]
    public void Menu_Layout_RowsSeparatorsAndPlacement()
    {
        Assert.Equal(4, ContextMenuState.ItemTop(Rows, 0, 26, 9, 4));
        Assert.Equal(4 + 26 + 9, ContextMenuState.ItemTop(Rows, 2, 26, 9, 4));
        Assert.Equal(4 + 5 * 26 + 9 + 4, ContextMenuState.Height(Rows, 26, 9, 4));
        Assert.Equal(0, ContextMenuState.ItemAt(Rows, 5, 26, 9, 4));
        Assert.Equal(-1, ContextMenuState.ItemAt(Rows, 32, 26, 9, 4));   // the separator
        Assert.Equal(2, ContextMenuState.ItemAt(Rows, 40, 26, 9, 4));
        Assert.Equal(-1, ContextMenuState.ItemAt(Rows, 4 + 4 * 26 + 9 + 1, 26, 9, 4));   // the disabled note
        Assert.Equal(-1, ContextMenuState.ItemAt(Rows, 1, 26, 9, 4));    // the padding

        Assert.Equal((100, 100), ContextMenuState.Place(100, 100, 200, 300, 0, 0, 1920, 1080));
        Assert.Equal((1600, 700), ContextMenuState.Place(1800, 1000, 200, 300, 0, 0, 1920, 1080));   // flipped left and up
        Assert.Equal((0, 0), ContextMenuState.Place(-50, -50, 200, 300, 0, 0, 1920, 1080));
        Assert.Equal((300, 120), ContextMenuState.PlaceSub(100, 300, 120, 200, 100, 0, 0, 1920, 1080));
        Assert.Equal((1520, 120), ContextMenuState.PlaceSub(1720, 1900, 120, 200, 100, 0, 0, 1920, 1080));   // no room on the right
        Assert.Equal((300, 980), ContextMenuState.PlaceSub(100, 300, 1050, 200, 100, 0, 0, 1920, 1080));
    }

    [Fact]
    public void PictureMenu_Build_TheRows_TheOwnFormatGreyed_TheModeLast()
    {
        var rows = PictureMenu.Build(thumbs: true, @"D:\pics\cat.png", "beside-original");

        Assert.Equal(
            [PictureMenuText.OpenInViewer, "", PictureMenuText.Rotate, PictureMenuText.Colour, PictureMenuText.Resize, PictureMenuText.Convert, PictureMenuText.Shrink,
             PictureMenuText.StripMetadata, "", PictureMenuText.CopyPath, PictureMenuText.ShowInExplorer, PictureMenuText.Attach, PictureMenuText.Print, "", PictureMenuText.Delete, "",
             "Edits: beside-original"],
            rows.Select(r => r.Label));
        Assert.False(rows[^1].Selectable);
        Assert.Equal((int)PictureCommand.Delete, rows[14].Command);
        Assert.Equal((int)PictureCommand.StripMetadata, rows[7].Command);
        Assert.True(rows[7].Enabled);   // a PNG: the strip reads it
        Assert.False(PictureMenu.Build(thumbs: true, @"D:\pics\cat.bmp", "beside-original")[7].Enabled);
        var convert = rows[5].Children!;
        Assert.Equal(["PNG", "JPEG", "GIF", "BMP"], convert.Select(r => r.Label));
        Assert.False(convert[0].Enabled);   // already a PNG
        Assert.True(convert[1].Enabled);
        Assert.Equal((int)PictureCommand.ToGif, convert[2].Command);
        Assert.Equal(["Fit in 3840 px", "Fit in 1920 px", "Fit in 1280 px", "Fit in 1024 px", "Fit in 512 px"], rows[4].Children!.Skip(3).Select(r => r.Label));
        Assert.Equal(["Under 2 MB", "Under 1 MB", "Under 500 KB", "Under 200 KB"], rows[6].Children!.Select(r => r.Label));
        Assert.Equal(PictureMenuText.Rotate, PictureMenu.Build(thumbs: false, @"D:\pics\cat.jpg", "overwrite-original")[0].Label);   // no Open in the viewer itself
        Assert.All(rows.Where(r => r.Selectable && !r.HasChildren).Concat(rows.Where(r => r.HasChildren).SelectMany(r => r.Children!).Where(r => r.Selectable)),
            r => Assert.NotEqual(0, r.Command));
    }

    [Fact]
    public void Styles_FollowTheTheme_OrTheBlackLook()
    {
        var palette = NeonSidekick.UI.Theme.Current;
        Assert.Equal(MenuStyle.Black, MenuStyle.For(palette, themed: false));
        Assert.Equal(ViewerStyle.ColorRef(palette.Primary), MenuStyle.For(palette).HotBack);
        Assert.Equal(ViewerStyle.ColorRef(palette.PanelBg), MenuStyle.For(palette).Background);
        Assert.Equal(ThumbsStyle.Black, ThumbsStyle.For(palette, themed: false));
        Assert.Equal(ViewerStyle.ColorRef(palette.Bg), ThumbsStyle.For(palette).Background);
        Assert.Equal(ViewerStyle.ColorRef(palette.Primary), ThumbsStyle.For(palette).Selected);
    }

    [Fact]
    public void Wording_IsPinned()
    {
        Assert.Equal(@"NeonSidekick thumbnails — D:\p", ThumbsText.Title(@"D:\p", 0, null, 0));
        Assert.Equal(@"D:\p — 12 pictures · NeonSidekick thumbnails", ThumbsText.Title(@"D:\p", 12, null, 0));
        Assert.Equal(@"D:\p — 1 picture · NeonSidekick thumbnails", ThumbsText.Title(@"D:\p", 1, null, 0));
        Assert.Equal("0001.png — 3/12 · NeonSidekick thumbnails", ThumbsText.Title(@"D:\p", 12, "0001.png", 3));
        Assert.Equal("0001.png — 3/12 · Del again to delete", ThumbsText.Title(@"D:\p", 12, "0001.png", 3, deleteArmed: true));
        Assert.Equal(@"D:\p — 12 pictures · NeonSidekick thumbnails", ThumbsText.Title(@"D:\p", 12, null, 0, deleteArmed: true));
        Assert.Equal("(\U0001F5BC\uFE0F thumbnails of D:\\p)", ThumbsText.Opened(@"D:\p"));
        Assert.Equal("(🖼️ thumbnails closed)", ThumbsText.Closed);
        Assert.Equal("thumbs", ThumbsText.ThumbsWord);
        Assert.Contains("Ctrl+wheel", ThumbsText.Keys);
        Assert.Contains("TAB terminal", ThumbsText.Keys);
        Assert.Contains("Del twice deletes", ThumbsText.Keys);
        Assert.Equal(@"No pictures in D:\p yet", ThumbsText.Empty(@"D:\p"));
        Assert.Equal("Under 500 KB", PictureMenuText.Under(500));
        Assert.Equal("Under 1 MB", PictureMenuText.Under(1024));
        Assert.Equal("(\U0001F5BC\uFE0F deleted cat.png)", PictureMenuText.Deleted("cat.png"));
        var state = Grid(2);
        state.SelectIndex(1);
        Assert.Equal("001.png — 2/2 · NeonSidekick thumbnails", state.Title());
    }

    [Fact]
    public void ProbeThumbsWindow_MakesAHiddenWindow_AndDrawsATile()
    {
        var check = SmokeChecks.ProbeThumbsWindow();

        Assert.True(check.Passed, check.Detail);
        Assert.Equal("viewer:thumbs", check.Name);
    }

    [Fact]
    public void ProbePictureMenu_MakesAHiddenPopup_AndParsesTheExe()
    {
        var check = SmokeChecks.ProbePictureMenu();

        Assert.True(check.Passed, check.Detail);
        Assert.Equal("viewer:menu", check.Name);
    }

    [Fact]
    public void DecodeThumbnail_BringsTheLongestSideDown_NeverUp()
    {
        var big = ViewerImage.DecodeThumbnail(SmokeChecks.SolidBmp(400, 200), "big.bmp", 128);
        Assert.NotNull(big);
        Assert.Equal((128, 64), (big.Width, big.Height));
        var small = ViewerImage.DecodeThumbnail(SmokeChecks.SolidBmp(40, 20), "small.bmp", 128);
        Assert.Equal((40, 20), (small!.Width, small.Height));
        Assert.Null(ViewerImage.DecodeThumbnail([1, 2, 3], "bad.png", 128));
    }

    // ── The viewer's additions (2026-10-04) ────────────────────────────────

    [Fact]
    public void Viewer_WheelSteps_ANotchAPicture_TouchpadTurnsAddedUp()
    {
        int remainder = 0;
        Assert.Equal(1, ViewerState.WheelSteps(ref remainder, 120));
        Assert.Equal(-2, ViewerState.WheelSteps(ref remainder, -240));
        Assert.Equal(0, ViewerState.WheelSteps(ref remainder, 60));
        Assert.Equal(1, ViewerState.WheelSteps(ref remainder, 60));
        Assert.Equal(0, remainder);
    }

    [Fact]
    public void Viewer_Touched_IsTheShownPicture_AndNothingMoves()
    {
        var state = new ViewerState();
        state.Reset(@"D:\pics", [new(@"D:\pics\a.png", T0), new(@"D:\pics\b.png", T0.AddMinutes(1)), new(@"D:\pics\c.png", T0.AddMinutes(2))]);
        state.Select(@"D:\pics\b.png", T0);

        Assert.True(state.Touched(@"D:\pics\B.png"));
        Assert.False(state.Touched(@"D:\pics\c.png"));
        Assert.True(state.Contains(@"D:\pics\a.png"));
        Assert.False(state.Contains(@"D:\pics\z.png"));
        Assert.Equal([@"D:\pics\a.png", @"D:\pics\b.png", @"D:\pics\c.png"], state.Pictures.Select(p => p.Path));
    }

    [Theory]
    [InlineData(0x79, false, ViewerAction.ToggleShuffle)]
    [InlineData(0x79, true, ViewerAction.Menu)]
    [InlineData(0x5D, false, ViewerAction.Menu)]
    public void Viewer_ActionFor_TheMenuKeys(int key, bool shift, ViewerAction expected) =>
        Assert.Equal(expected, ViewerState.ActionFor(key, fullScreen: false, slideShow: false, shift));
}
