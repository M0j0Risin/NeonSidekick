using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// What the Web UI's strip reads of the PC's (later on 2026-10-15): <see cref="PictureStrip.Changed"/> after every change the drawing shows, raised
/// with the lock let go; <see cref="PictureStrip.Snapshot"/> and <see cref="PictureStrip.Holds"/>; and <see cref="PictureStrip.Open"/> letting the
/// highlight go, so a pick from the page while the strip was closed never shows on its reopening.
/// </summary>
public partial class PictureStripTests
{
    [Fact]
    public void Changed_FiresOnAnAddAHighlightAStepAndAClear_WithTheLockLetGo()
    {
        var strip = new PictureStrip();
        int changes = 0;
        bool lockFree = true;
        strip.Changed += () =>
        {
            changes++;
            // Another thread reads the strip at once: it would wait on the lock if the event came under it.
            var reader = new Thread(() => _ = strip.Snapshot());
            reader.Start();
            lockFree &= reader.Join(TimeSpan.FromSeconds(10));
        };

        Put(strip, Tile(4, 4), 1);
        Put(strip, Tile(4, 4), 2);
        Assert.Equal(2, changes);
        Assert.True(strip.Highlight(id => id == 1));
        Assert.Equal(3, changes);
        Assert.False(strip.Highlight(id => id == 1));   // already highlighted: no change, no event
        Assert.Equal(3, changes);
        Assert.True(strip.Step(-1));
        Assert.Equal(4, changes);
        strip.Clear();
        Assert.Equal(5, changes);
        strip.Clear();   // empty already
        Assert.Equal(5, changes);
        Assert.True(lockFree);
    }

    [Fact]
    public void Snapshot_IsTheIdsNewestFirst_AndTheHighlight_AndHoldsAnswersById()
    {
        var strip = new PictureStrip();
        Put(strip, Tile(4, 4), 5);
        Put(strip, Tile(4, 4), 9);
        Put(strip, Tile(4, 4), 7);

        Assert.Equal([9, 7, 5], strip.Snapshot().Ids);
        Assert.Null(strip.Snapshot().Selected);
        strip.Highlight(id => id == 7);
        Assert.Equal(7, strip.Snapshot().Selected);
        Assert.True(strip.Holds(5));
        Assert.False(strip.Holds(6));
    }

    [Fact]
    public void Remove_DropsEveryIdTaken_KeepsASurvivingHighlight_AndLetsGoOfAGoneOne()
    {
        var strip = new PictureStrip();
        Put(strip, Tile(4, 4), 5);
        Put(strip, Tile(4, 4), 9);
        Put(strip, Tile(4, 4), 7);
        strip.Highlight(id => id == 5);
        int changes = 0;
        strip.Changed += () => changes++;

        Assert.True(strip.Remove(id => id is 9 or 7));   // one file, registered twice (later on 2026-10-15, the page's delete)
        Assert.Equal([5], strip.Snapshot().Ids);
        Assert.Equal(5, strip.Snapshot().Selected);
        Assert.Equal(1, changes);
        Assert.False(strip.Remove(id => id == 42));
        Assert.Equal(1, changes);
        Assert.True(strip.Remove(id => id == 5));
        Assert.Equal((0, (int?)null), (strip.Snapshot().Ids.Count, strip.Snapshot().Selected));
    }

    [Fact]
    public void Open_AfterAPickWhileClosed_ShowsNothingHighlighted()
    {
        var strip = new PictureStrip();
        Put(strip, Tile(4, 4), 1);
        Put(strip, Tile(4, 4), 2);
        strip.Close();
        int changes = 0;
        strip.Changed += () => changes++;

        Assert.True(strip.Highlight(id => id == 1));   // the page's pick, the PC's strip closed
        strip.Open();

        Assert.False(strip.Closed);
        Assert.Equal(-1, strip.Selected);
        Assert.Equal(2, changes);
    }
}
