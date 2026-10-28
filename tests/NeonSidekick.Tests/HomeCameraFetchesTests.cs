using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="HomeCameraFetches"/> on its own (2026-10-27, the review): once disposed it answers cancelled, a failed still or one past its
/// reuse is fetched again while a fresh one is shared, and a still is saved once, under its own lock.
/// </summary>
public sealed class HomeCameraFetchesTests
{
    private static Task<HaPictureReply> Picture() => Task.FromResult(HaPictureReply.Success(StripFixtures.Jpeg(), "image/jpeg"));

    [Fact]
    public async Task Disposed_AGetIsCancelled_TheTokenToo_AndAForgetIsQuiet()
    {
        int asked = 0;
        var fetches = new HomeCameraFetches((_, _) => { Interlocked.Increment(ref asked); return Picture(); }, new ManualTimeProvider());
        fetches.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetches.Get("camera.porch"));
        Assert.True(fetches.Token.IsCancellationRequested);
        fetches.Forget();
        fetches.Dispose();
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task AFailedStill_OrOnePastItsReuse_IsFetchedAgain_AndAFreshOneIsShared()
    {
        var time = new ManualTimeProvider();
        int asked = 0;
        using var fetches = new HomeCameraFetches((_, _) => Interlocked.Increment(ref asked) == 1
            ? Task.FromResult(HaPictureReply.Failed(HaText.CameraNoPicture, 500))
            : Picture(), time);

        Assert.Null((await fetches.Get("camera.porch")).Image);
        var fresh = await fetches.Get("camera.porch");
        Assert.NotNull(fresh.Image);
        Assert.Same(fresh, await fetches.Get("camera.porch"));
        time.Advance(HomeCameraFetches.Reuse);
        Assert.NotSame(fresh, await fetches.Get("camera.porch"));
        Assert.Equal(3, asked);
    }

    [Fact]
    public void SaveOnce_SavesOnce_UnderTheStillsOwnLock_AndAgainWhenTheFileIsGone()
    {
        var still = new HomeCameraStill(null, null, DateTimeOffset.UnixEpoch);
        int saves = 0;
        string? Save()
        {
            Assert.True(System.Threading.Monitor.IsEntered(still.SaveGate));
            saves++;
            return "a.jpg";
        }

        Assert.Equal("a.jpg", HomeCameraFetches.SaveOnce(still, Save));
        Assert.Equal("a.jpg", HomeCameraFetches.SaveOnce(still, Save, _ => true));
        Assert.Equal(1, saves);
        Assert.Equal("a.jpg", HomeCameraFetches.SaveOnce(still, Save, _ => false));
        Assert.Equal(2, saves);
    }
}
