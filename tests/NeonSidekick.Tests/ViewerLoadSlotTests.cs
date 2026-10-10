using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// The viewer's read slot (2026-10-09, the user's report: the viewer opened black on a double-clicked splash picture): the newest
/// version offered is kept, so a cancelled read still decoding never overwrites the picked picture.
/// </summary>
public sealed class ViewerLoadSlotTests
{
    private static readonly ViewerBitmap Newest = new(1, 1, [1, 2, 3, 0]);
    private static readonly ViewerBitmap Picked = new(1, 1, [4, 5, 6, 0]);

    [Fact]
    public void AnOlderRead_DoneAfterTheNewer_IsRefused_AndTheNewerIsTaken()
    {
        var slot = new ViewerLoadSlot();

        Assert.True(slot.Offer(2, "picked.png", Picked));
        Assert.False(slot.Offer(1, "newest.png", Newest));   // the folder's newest, cancelled, its decode done last

        Assert.Equal(("picked.png", (ViewerBitmap?)Picked), slot.Take(2));
    }

    [Fact]
    public void AnOlderRead_DoneFirst_IsReplacedByTheNewer()
    {
        var slot = new ViewerLoadSlot();

        Assert.True(slot.Offer(1, "newest.png", Newest));
        Assert.True(slot.Offer(2, "picked.png", Picked));

        Assert.Equal(("picked.png", (ViewerBitmap?)Picked), slot.Take(2));
    }

    [Fact]
    public void AnotherVersionsAnswer_IsDropped_AndTheSlotEmptied()
    {
        var slot = new ViewerLoadSlot();
        slot.Offer(1, "newest.png", Newest);

        Assert.Null(slot.Take(2));
        Assert.True(slot.Offer(2, "picked.png", Picked));   // the newer, still reading, lands in the empty slot
        Assert.Equal(("picked.png", (ViewerBitmap?)Picked), slot.Take(2));
        Assert.Null(slot.Take(2));
    }

    [Fact]
    public void AnUnreadableFile_StillReachesTheWindow()
    {
        var slot = new ViewerLoadSlot();

        Assert.True(slot.Offer(3, "broken.png", null));

        Assert.Equal(("broken.png", (ViewerBitmap?)null), slot.Take(3));
    }
}
