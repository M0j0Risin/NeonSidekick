using NeonSidekick.App;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>The picture viewer's decisions (<see cref="ViewerState"/>) and decode (<see cref="ViewerImage"/>), 2026-09-27; the window itself is the smoke's and the user's.</summary>
public sealed class ViewerTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "neonsidekick-viewer-" + Guid.NewGuid().ToString("N"));

    public ViewerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ViewerState ThreePictures()
    {
        var state = new ViewerState();
        state.Reset(@"D:\pics", [new(@"D:\pics\c.png", T0.AddMinutes(3)), new(@"D:\pics\a.png", T0.AddMinutes(1)), new(@"D:\pics\b.png", T0.AddMinutes(2))]);
        return state;
    }

    [Fact]
    public void Reset_SortsOldestFirst_AndIsLive_OnTheNewest()
    {
        var state = ThreePictures();

        Assert.Equal([@"D:\pics\a.png", @"D:\pics\b.png", @"D:\pics\c.png"], state.Pictures.Select(p => p.Path));
        Assert.True(state.Live);
        Assert.Equal(2, state.Index);
        Assert.Equal(@"D:\pics\c.png", state.Current);
        Assert.Equal("c.png — 3/3 (live) · NeonSidekick pictures", state.Title());
    }

    [Fact]
    public void Reset_BreaksATie_ByName_AndDropsADuplicate()
    {
        var state = new ViewerState();
        state.Reset(@"D:\p", [new(@"D:\p\b.png", T0), new(@"D:\p\a.png", T0), new(@"D:\P\A.PNG", T0)]);

        Assert.Equal([@"D:\p\a.png", @"D:\p\b.png"], state.Pictures.Select(p => p.Path));
    }

    [Fact]
    public void AnEmptyFolder_HasNoPicture_AndTheTitleNamesTheFolder()
    {
        var state = new ViewerState();
        state.Reset(@"D:\empty", []);

        Assert.Null(state.Index);
        Assert.Null(state.Current);
        Assert.False(state.Browse(ViewerAction.Previous));
        Assert.Equal(@"NeonSidekick pictures — D:\empty", state.Title());
    }

    [Fact]
    public void Add_WhileLive_ShowsTheNewPicture()
    {
        var state = ThreePictures();

        Assert.True(state.Add(@"D:\pics\d.png", T0.AddMinutes(4)));
        Assert.Equal(@"D:\pics\d.png", state.Current);
        Assert.True(state.Live);
    }

    [Fact]
    public void Add_WhileHeld_OnlyCountsIt()
    {
        var state = ThreePictures();
        Assert.True(state.Browse(ViewerAction.Previous));

        Assert.False(state.Add(@"D:\pics\d.png", T0.AddMinutes(4)));
        Assert.Equal(@"D:\pics\b.png", state.Current);
        Assert.False(state.Live);
        Assert.Equal("b.png — 2/4 (paused) · NeonSidekick pictures", state.Title());
    }

    [Fact]
    public void Add_OfAPathAlreadyThere_MovesItToTheNewest()
    {
        var state = ThreePictures();

        Assert.True(state.Add(@"D:\pics\A.png", T0.AddMinutes(9)));
        Assert.Equal([@"D:\pics\b.png", @"D:\pics\c.png", @"D:\pics\A.png"], state.Pictures.Select(p => p.Path));
        Assert.Equal(@"D:\pics\A.png", state.Current);
    }

    [Fact]
    public void Add_OfTheHeldPicture_MovesItAway_AndTheShownOneChanges()
    {
        var state = ThreePictures();
        state.Browse(ViewerAction.First);

        Assert.True(state.Add(@"D:\pics\a.png", T0.AddMinutes(9)));
        Assert.Equal(@"D:\pics\b.png", state.Current);
    }

    [Fact]
    public void Remove_KeepsTheHeldPicture_WhenAnOlderOneGoes()
    {
        var state = ThreePictures();
        state.Browse(ViewerAction.Previous);   // b

        Assert.False(state.Remove(@"D:\pics\a.png"));
        Assert.Equal(@"D:\pics\b.png", state.Current);
        Assert.False(state.Live);
        Assert.False(state.Remove(@"D:\pics\nope.png"));
    }

    [Fact]
    public void Remove_OfTheNewest_WhileLive_ShowsTheOneBefore()
    {
        var state = ThreePictures();

        Assert.True(state.Remove(@"D:\pics\c.png"));
        Assert.Equal(@"D:\pics\b.png", state.Current);
        Assert.True(state.Live);
    }

    [Fact]
    public void Remove_OfTheHeldPicture_ShowsTheNext_AndLiveWhenThatIsTheNewest()
    {
        var state = ThreePictures();
        state.Browse(ViewerAction.Previous);   // b

        Assert.True(state.Remove(@"D:\pics\b.png"));
        Assert.Equal(@"D:\pics\c.png", state.Current);
        Assert.True(state.Live);

        Assert.True(state.Remove(@"D:\pics\c.png"));
        Assert.True(state.Remove(@"D:\pics\a.png"));
        Assert.Null(state.Current);
    }

    [Fact]
    public void Browse_WalksAndClamps_AndTheNewestFollowsAgain()
    {
        var state = ThreePictures();

        Assert.False(state.Browse(ViewerAction.Next));   // already the newest, live
        Assert.True(state.Browse(ViewerAction.Previous));
        Assert.Equal(1, state.Index);
        Assert.False(state.Live);
        Assert.True(state.Browse(ViewerAction.First));
        Assert.Equal(0, state.Index);
        Assert.False(state.Browse(ViewerAction.Previous));   // clamped at the oldest
        Assert.True(state.Browse(ViewerAction.Next));
        Assert.True(state.Browse(ViewerAction.Next));
        Assert.True(state.Live);
        Assert.True(state.Browse(ViewerAction.First));
        Assert.True(state.Browse(ViewerAction.Last));
        Assert.True(state.Live);
        Assert.False(state.Browse(ViewerAction.ToggleFullScreen));
    }

    [Theory]
    [InlineData(ViewerState.VkLeft, false, ViewerAction.Previous)]
    [InlineData(ViewerState.VkRight, false, ViewerAction.Next)]
    [InlineData(ViewerState.VkHome, false, ViewerAction.First)]
    [InlineData(ViewerState.VkEnd, false, ViewerAction.Last)]
    [InlineData(ViewerState.VkF11, false, ViewerAction.ToggleFullScreen)]
    [InlineData(ViewerState.VkEscape, false, ViewerAction.Close)]
    [InlineData(ViewerState.VkEscape, true, ViewerAction.LeaveFullScreen)]
    [InlineData(0x2E, false, ViewerAction.None)]   // Del: deliberately nothing
    public void ActionFor_MapsTheKeys(int key, bool fullScreen, ViewerAction expected) =>
        Assert.Equal(expected, ViewerState.ActionFor(key, fullScreen));

    [Theory]
    [InlineData(100, 50, 400, 400, 0, 100, 400, 200)]    // wide: fills the width, centred down
    [InlineData(50, 100, 400, 400, 100, 0, 200, 400)]    // tall: fills the height, centred across
    [InlineData(1000, 1000, 300, 200, 50, 0, 200, 200)]  // downscaled
    [InlineData(10, 10, 0, 200, 0, 0, 0, 0)]             // a minimised window
    public void Fit_ScalesWhole_AndCentres(int w, int h, int cw, int ch, int x, int y, int fw, int fh) =>
        Assert.Equal((x, y, fw, fh), ViewerState.Fit(w, h, cw, ch));

    [Fact]
    public void Wording_IsPinned()
    {
        Assert.Equal("🖼  viewer", ViewerText.StripButton);
        Assert.Equal("view", ViewerText.ViewWord);
        Assert.Equal(@"Waiting for pictures in D:\p", ViewerText.Waiting(@"D:\p"));
        Assert.Equal("x.png could not be read as a picture", ViewerText.Unreadable("x.png"));
        Assert.Equal(@"Could not open the picture viewer on D:\p: denied", ViewerText.Failed(@"D:\p", "denied"));
    }

    [Fact]
    public void Decode_ABmp_IsBgrx_TopRowFirst()
    {
        var bitmap = ViewerImage.Decode(SmokeChecks.SolidBmp(8, 2), "x.bmp");

        Assert.NotNull(bitmap);
        Assert.Equal((8, 2), (bitmap.Width, bitmap.Height));
        Assert.Equal(8 * 2 * 4, bitmap.Bgrx.Length);
        Assert.Equal([0xC8, 0x40, 0xFF], bitmap.Bgrx[..3]);
    }

    [Fact]
    public void Decode_OverTheMaxSide_IsDownscaled()
    {
        var bitmap = ViewerImage.Decode(SmokeChecks.SolidBmp(ViewerImage.MaxSide * 2, 4), "wide.bmp");

        Assert.NotNull(bitmap);
        Assert.Equal(ViewerImage.MaxSide, bitmap.Width);
        Assert.Equal(2, bitmap.Height);
    }

    [Fact]
    public void Decode_NotAPicture_IsNull() => Assert.Null(ViewerImage.Decode("not a picture"u8.ToArray(), "x.png"));

    [Fact]
    public void ToBgrx_SpreadsGrey_KeepsBgr_AndBlendsAlphaOverBlack()
    {
        Assert.Equal([9, 9, 9, 0], ViewerImage.ToBgrx([9], 1));
        Assert.Equal([1, 2, 3, 0], ViewerImage.ToBgrx([1, 2, 3], 3));
        Assert.Equal([100, 50, 0, 0, 0, 0, 0, 0], ViewerImage.ToBgrx([200, 100, 0, 128, 255, 255, 255, 0], 4));
    }

    [Fact]
    public async Task LoadAsync_ReadsAPicture()
    {
        string path = Path.Combine(_dir, "a.bmp");
        File.WriteAllBytes(path, SmokeChecks.SolidBmp(4, 4));

        var bitmap = await ViewerImage.LoadAsync(path, CancellationToken.None, (_, _) => Task.CompletedTask);

        Assert.Equal((4, 4), (bitmap!.Width, bitmap.Height));
    }

    [Fact]
    public async Task LoadAsync_OfAFileStillBeingWritten_TriesAgain_ThenGivesUp()
    {
        string path = Path.Combine(_dir, "half.png");
        File.WriteAllBytes(path, "\x89PNG half"u8.ToArray());
        int waits = 0;

        var bitmap = await ViewerImage.LoadAsync(path, CancellationToken.None, (_, _) => { waits++; return Task.CompletedTask; });

        Assert.Null(bitmap);
        Assert.Equal(ViewerImage.Attempts - 1, waits);
    }

    [Fact]
    public async Task LoadAsync_OfAFileThatFinishesBetweenTries_ShowsIt()
    {
        string path = Path.Combine(_dir, "late.bmp");
        File.WriteAllBytes(path, [1, 2, 3]);

        var bitmap = await ViewerImage.LoadAsync(path, CancellationToken.None, (_, _) =>
        {
            File.WriteAllBytes(path, SmokeChecks.SolidBmp(2, 2));
            return Task.CompletedTask;
        });

        Assert.NotNull(bitmap);
    }

    [Fact]
    public async Task LoadAsync_OfAMissingFile_OrCancelled_IsNull()
    {
        Assert.Null(await ViewerImage.LoadAsync(Path.Combine(_dir, "gone.png"), CancellationToken.None));
        string path = Path.Combine(_dir, "b.bmp");
        File.WriteAllBytes(path, SmokeChecks.SolidBmp(2, 2));
        Assert.Null(await ViewerImage.LoadAsync(path, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Probe_MakesAHiddenWindow_ThatAnswers()
    {
        var check = SmokeChecks.ProbeViewerWindow();

        Assert.True(check.Passed, check.Detail);
        Assert.Equal("viewer:window", check.Name);
    }
}
