using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
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

    [WindowsFact]
    public void Reset_SortsOldestFirst_AndIsLive_OnTheNewest()
    {
        var state = ThreePictures();

        Assert.Equal([@"D:\pics\a.png", @"D:\pics\b.png", @"D:\pics\c.png"], state.Pictures.Select(p => p.Path));
        Assert.True(state.Live);
        Assert.Equal(2, state.Index);
        Assert.Equal(@"D:\pics\c.png", state.Current);
        Assert.Equal("c.png — 1/3 (live) · NeonSidekick pictures", state.Title());   // the newest counts 1, as the strip does (2026-10-03)
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
        Assert.False(state.Browse(ViewerAction.Older));
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

    [WindowsFact]
    public void Add_WhileHeld_OnlyCountsIt()
    {
        var state = ThreePictures();
        Assert.True(state.Browse(ViewerAction.Older));

        Assert.False(state.Add(@"D:\pics\d.png", T0.AddMinutes(4)));
        Assert.Equal(@"D:\pics\b.png", state.Current);
        Assert.False(state.Live);
        Assert.Equal("b.png — 3/4 (paused) · NeonSidekick pictures", state.Title());   // d, c, b: the third from the newest
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
        state.Browse(ViewerAction.Oldest);

        Assert.True(state.Add(@"D:\pics\a.png", T0.AddMinutes(9)));
        Assert.Equal(@"D:\pics\b.png", state.Current);
    }

    /// <summary>2026-09-28, code review: holding the second newest, the newest written again pulled the view to it.</summary>
    [Fact]
    public void Add_OfTheNewestAgain_WhileHeld_KeepsTheHeldPicture()
    {
        var state = ThreePictures();
        Assert.True(state.Browse(ViewerAction.Older));

        Assert.False(state.Add(@"D:\pics\c.png", T0.AddMinutes(9)));
        Assert.Equal(@"D:\pics\b.png", state.Current);
        Assert.False(state.Live);
    }

    [Fact]
    public void Add_OfTheHeldSecondNewest_ShowsTheOneAfter_StillHeld()
    {
        var state = ThreePictures();
        Assert.True(state.Browse(ViewerAction.Older));

        Assert.True(state.Add(@"D:\pics\b.png", T0.AddMinutes(9)));
        Assert.Equal(@"D:\pics\c.png", state.Current);
        Assert.False(state.Live);
    }

    [Fact]
    public void Remove_KeepsTheHeldPicture_WhenAnOlderOneGoes()
    {
        var state = ThreePictures();
        state.Browse(ViewerAction.Older);   // b

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
        state.Browse(ViewerAction.Older);   // b

        Assert.True(state.Remove(@"D:\pics\b.png"));
        Assert.Equal(@"D:\pics\c.png", state.Current);
        Assert.True(state.Live);

        Assert.True(state.Remove(@"D:\pics\c.png"));
        Assert.True(state.Remove(@"D:\pics\a.png"));
        Assert.Null(state.Current);
    }

    /// <summary>Newest at the left since 2026-10-03 (the user's ask, the strip's way): → older, ← newer, End the oldest, Home the newest and live.</summary>
    [WindowsFact]
    public void Browse_WalksAndClamps_AndTheNewestFollowsAgain()
    {
        var state = ThreePictures();

        Assert.False(state.Browse(ViewerState.ActionFor(ViewerState.VkLeft, false)));   // ← : already the newest, live
        Assert.True(state.Browse(ViewerState.ActionFor(ViewerState.VkRight, false)));   // → : older
        Assert.Equal(@"D:\pics\b.png", state.Current);
        Assert.False(state.Live);
        Assert.Equal("b.png — 2/3 (paused) · NeonSidekick pictures", state.Title());
        Assert.True(state.Browse(ViewerState.ActionFor(ViewerState.VkEnd, false)));   // End: the oldest
        Assert.Equal(@"D:\pics\a.png", state.Current);
        Assert.Equal("a.png — 3/3 (paused) · NeonSidekick pictures", state.Title());
        Assert.False(state.Browse(ViewerAction.Older));   // clamped at the oldest
        Assert.True(state.Browse(ViewerAction.Newer));
        Assert.True(state.Browse(ViewerAction.Newer));
        Assert.True(state.Live);
        Assert.True(state.Browse(ViewerAction.Oldest));
        Assert.True(state.Browse(ViewerState.ActionFor(ViewerState.VkHome, false)));   // Home: the newest, live again
        Assert.Equal(@"D:\pics\c.png", state.Current);
        Assert.True(state.Live);
        Assert.False(state.Browse(ViewerAction.ToggleFullScreen));
    }

    /// <summary>
    /// The arrows' ends (2026-10-03, the user's ask: "&lt; hidden when we're on the latest image ... &gt; hidden when we're at
    /// the first"): each shown exactly where its key would move — no &lt; on the newest, no &gt; on the oldest, neither with one
    /// picture or none.
    /// </summary>
    [Fact]
    public void TheArrows_ShowWhereTheirKeysWouldMove()
    {
        var state = ThreePictures();
        Assert.False(state.CanNewer);   // the newest, live
        Assert.True(state.CanOlder);
        state.Browse(ViewerAction.Older);
        Assert.True(state.CanNewer);
        Assert.True(state.CanOlder);
        state.Browse(ViewerAction.Oldest);
        Assert.True(state.CanNewer);
        Assert.False(state.CanOlder);   // the oldest

        var one = new ViewerState();
        one.Reset(@"D:\pics", [new(@"D:\pics\a.png", T0)]);
        Assert.False(one.CanNewer || one.CanOlder);
        var none = new ViewerState();
        none.Reset(@"D:\pics", []);
        Assert.False(none.CanNewer || none.CanOlder);

        // Each moves exactly when its key does.
        var walk = ThreePictures();
        foreach (var action in new[] { ViewerAction.Newer, ViewerAction.Older, ViewerAction.Older, ViewerAction.Older, ViewerAction.Newer, ViewerAction.Newer, ViewerAction.Newer })
        {
            bool could = action == ViewerAction.Newer ? walk.CanNewer : walk.CanOlder;
            Assert.Equal(could, walk.Browse(action));
        }
    }

    /// <summary>
    /// The arrows' squares (2026-10-03): 44 px at 96 DPI, 12 in from each side, centred top to bottom, scaled with the DPI;
    /// none in a window too small for them; a point names a shown arrow's action and nothing else.
    /// </summary>
    [Fact]
    public void TheArrows_Layout_AndWhatAPointIsOn()
    {
        var squares = ViewerNav.Layout(800, 600, 96);
        Assert.Equal(new ViewerNav.Square(12, 278, 44), squares!.Value.Newer);
        Assert.Equal(new ViewerNav.Square(744, 278, 44), squares.Value.Older);
        Assert.Equal(new ViewerNav.Square(18, 267, 66), ViewerNav.Layout(800, 600, 144)!.Value.Newer);   // 150 %
        Assert.Null(ViewerNav.Layout(150, 600, 96));   // narrower than both and a button between
        Assert.Null(ViewerNav.Layout(800, 60, 96));
        Assert.NotNull(ViewerNav.Layout(2 * (12 + 44) + 44, 44 + 24, 96));   // just fits

        Assert.Equal(ViewerAction.Newer, ViewerNav.At(12, 278, squares, canNewer: true, canOlder: true));
        Assert.Equal(ViewerAction.Newer, ViewerNav.At(55, 321, squares, true, true));
        Assert.Equal(ViewerAction.None, ViewerNav.At(56, 300, squares, true, true));   // just past it
        Assert.Equal(ViewerAction.Older, ViewerNav.At(760, 300, squares, true, true));
        Assert.Equal(ViewerAction.None, ViewerNav.At(400, 300, squares, true, true));   // the picture
        Assert.Equal(ViewerAction.None, ViewerNav.At(20, 300, squares, canNewer: false, canOlder: true));   // hidden on the newest
        Assert.Equal(ViewerAction.None, ViewerNav.At(760, 300, squares, canNewer: true, canOlder: false));  // hidden on the oldest
        Assert.Equal(ViewerAction.None, ViewerNav.At(20, 300, null, true, true));
    }

    /// <summary>The arrows' fade and opacity (2026-10-03): in or out a step a tick, clamped; brighter under the mouse; nothing hidden.</summary>
    [Fact]
    public void TheArrows_FadeAndOpacity()
    {
        Assert.Equal(ViewerNav.FadeStep, ViewerNav.Fade(0, shown: true));
        Assert.Equal(255, ViewerNav.Fade(250, shown: true));
        Assert.Equal(0, ViewerNav.Fade(10, shown: false));
        int level = 0, ticks = 0;
        while (level < 255)
        {
            level = ViewerNav.Fade(level, true);
            ticks++;
        }

        Assert.InRange(ticks * (int)ViewerNav.FadeMilliseconds, 60, 160);   // about a tenth of a second
        Assert.Equal(ViewerNav.RestAlpha, ViewerNav.Alpha(255, hot: false));
        Assert.Equal(ViewerNav.HotAlpha, ViewerNav.Alpha(255, hot: true));
        Assert.True(ViewerNav.HotAlpha > ViewerNav.RestAlpha);
        Assert.Equal(0, ViewerNav.Alpha(0, hot: true));
        Assert.InRange(ViewerNav.Alpha(128, hot: false), 74, 76);
    }

    /// <summary>
    /// An arrow's pixels (2026-10-03): premultiplied BGRA, a disc of the fill with its corners clear, the chevron's ink on the
    /// side it points to and the fill on the other, the two arrows mirror images.
    /// </summary>
    [Fact]
    public void TheArrows_Pixels_AreADiscWithAChevron()
    {
        const int Side = 44;
        const uint Fill = 0x00102030;   // COLORREF: red 0x30, green 0x20, blue 0x10
        const uint Ink = 0x00FFFFFF;
        uint[] left = ViewerNav.Pixels(Side, pointsLeft: true, Fill, Ink);
        uint[] right = ViewerNav.Pixels(Side, pointsLeft: false, Fill, Ink);
        Assert.Equal(Side * Side, left.Length);
        uint Pixel(uint[] p, int x, int y) => p[y * Side + x];

        Assert.Equal(0u, Pixel(left, 0, 0));   // a corner: outside the disc
        Assert.Equal(0xFF302010u, Pixel(left, Side / 2, 4));   // inside, off the chevron: the fill, opaque (BGRA: blue low)
        int tip = Side / 2 - (int)(Side * 0.11);   // the left arrow's tip, on the middle row
        Assert.Equal(0xFFFFFFFFu, Pixel(left, tip, Side / 2));
        Assert.Equal(0xFF302010u, Pixel(left, Side - 8, Side / 2));   // the open side
        for (int y = 0; y < Side; y++)
        {
            for (int x = 0; x < Side; x++)
            {
                Assert.Equal(Pixel(left, x, y), Pixel(right, Side - 1 - x, y));
                uint p = Pixel(left, x, y);
                uint a = p >> 24;
                Assert.True((p & 0xFF) <= a && ((p >> 8) & 0xFF) <= a && ((p >> 16) & 0xFF) <= a);   // premultiplied
            }
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ViewerNav.Pixels(0, true, Fill, Ink));
    }

    /// <summary>A double-clicked picture (later on 2026-09-27): an older one held, the newest live, and browsing goes on from there.</summary>
    [Fact]
    public void Select_HoldsAnOlderPicture_GoesLiveOnTheNewest_AndBrowsingGoesOnFromIt()
    {
        var state = ThreePictures();

        Assert.True(state.Select(@"D:\PICS\A.PNG", T0));   // the case ignored
        Assert.Equal(0, state.Index);
        Assert.False(state.Live);
        Assert.False(state.Select(@"D:\pics\a.png", T0));   // already shown
        Assert.True(state.Browse(ViewerAction.Newer));
        Assert.Equal(@"D:\pics\b.png", state.Current);
        Assert.True(state.Select(@"D:\pics\c.png", T0));
        Assert.True(state.Live);
        Assert.Equal(2, state.Index);
    }

    /// <summary>A picture not listed yet (written a moment ago, its watcher event still queued) is added as the newest and shown, live.</summary>
    [Fact]
    public void Select_OfAPictureNotListedYet_AddsIt_AsTheNewest()
    {
        var state = ThreePictures();
        state.Browse(ViewerAction.Oldest);

        Assert.True(state.Select(@"D:\pics\d.png", T0.AddMinutes(4)));

        Assert.Equal(4, state.Count);
        Assert.Equal(@"D:\pics\d.png", state.Current);
        Assert.True(state.Live);
    }

    /// <summary>Where a double-clicked picture opens (later on 2026-09-27): empty is the built-in viewer where there is one, <c>system</c> (any case) the registered app, else the command.</summary>
    [Theory]
    [InlineData("", true, "Viewer")]
    [InlineData("   ", true, "Viewer")]
    [InlineData("", false, "System")]
    [InlineData("system", true, "System")]
    [InlineData(" SYSTEM ", false, "System")]
    [InlineData("mspaint", true, "Command")]
    [InlineData("mspaint", false, "Command")]
    public void PictureOpenerFor_Decides(string setting, bool viewerAvailable, string expected) =>   // a name: ChatScreen is internal
        Assert.Equal(expected, ChatScreen.PictureOpenerFor(setting, viewerAvailable).ToString());

    [Theory]
    [InlineData(ViewerState.VkLeft, false, ViewerAction.Newer)]     // newest at the left since 2026-10-03
    [InlineData(ViewerState.VkRight, false, ViewerAction.Older)]
    [InlineData(ViewerState.VkHome, false, ViewerAction.Newest)]
    [InlineData(ViewerState.VkEnd, false, ViewerAction.Oldest)]
    [InlineData(0x09, false, ViewerAction.None)]   // TAB: the terminal's (TerminalHandoff), not the viewer's
    [InlineData(ViewerState.VkF11, false, ViewerAction.ToggleFullScreen)]
    [InlineData(ViewerState.VkEscape, false, ViewerAction.Close)]
    [InlineData(ViewerState.VkEscape, true, ViewerAction.LeaveFullScreen)]
    [InlineData(ViewerState.VkDelete, false, ViewerAction.Delete)]
    [InlineData(ViewerState.VkDelete, true, ViewerAction.Delete)]
    [InlineData(ViewerState.VkF9, false, ViewerAction.ToggleSlideShow)]
    [InlineData(ViewerState.VkF10, false, ViewerAction.ToggleShuffle)]
    [InlineData(0x7B, false, ViewerAction.None)]   // F12: nothing since later on 2026-09-27 (random moved to F10)
    [InlineData(ViewerState.VkUp, false, ViewerAction.LongerSlides)]
    [InlineData(ViewerState.VkDown, false, ViewerAction.ShorterSlides)]
    [InlineData(0x41, false, ViewerAction.None)]   // A: nothing
    public void ActionFor_MapsTheKeys(int key, bool fullScreen, ViewerAction expected) =>
        Assert.Equal(expected, ViewerState.ActionFor(key, fullScreen));

    /// <summary>Esc during the slide show stops it first (later on 2026-09-27); the window and full screen stay.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActionFor_EscDuringTheSlideShow_StopsIt(bool fullScreen)
    {
        Assert.Equal(ViewerAction.StopSlideShow, ViewerState.ActionFor(ViewerState.VkEscape, fullScreen, slideShow: true));
        Assert.Equal(ViewerAction.ToggleSlideShow, ViewerState.ActionFor(ViewerState.VkF9, fullScreen, slideShow: true));
    }

    [WindowsFact]
    public void Slides_F10Toggles_UpDownStepAndClamp_OnlyWhileRunning()
    {
        var state = ThreePictures();

        Assert.False(state.Slides(ViewerAction.LongerSlides));   // the show off: nothing
        Assert.Equal(ViewerState.DefaultSlideSeconds, state.SlideSeconds);
        Assert.False(state.Slides(ViewerAction.StopSlideShow));

        Assert.True(state.Slides(ViewerAction.ToggleSlideShow));
        Assert.True(state.SlideShow);
        Assert.Equal("c.png — 1/3 (live) · ▶ 5 s", state.Title());

        Assert.True(state.Slides(ViewerAction.LongerSlides));
        Assert.Equal(6, state.SlideSeconds);
        for (int i = 0; i < 10; i++)
        {
            state.Slides(ViewerAction.ShorterSlides);
        }

        Assert.Equal(ViewerState.MinSlideSeconds, state.SlideSeconds);
        Assert.False(state.Slides(ViewerAction.ShorterSlides));
        for (int i = 0; i < 100; i++)
        {
            state.Slides(ViewerAction.LongerSlides);
        }

        Assert.Equal(ViewerState.MaxSlideSeconds, state.SlideSeconds);

        Assert.True(state.Slides(ViewerAction.ToggleShuffle));
        Assert.Equal("c.png — 1/3 (live) · ▶ 60 s · random", state.Title());
        Assert.True(state.Slides(ViewerAction.StopSlideShow));
        Assert.False(state.SlideShow);
        Assert.True(state.Shuffle);   // remembered
        Assert.Equal("c.png — 1/3 (live) · NeonSidekick pictures", state.Title());
    }

    [WindowsFact]
    public void Slides_TheDelHint_WinsOverTheSlideShowTail()
    {
        var state = ThreePictures();
        state.Slides(ViewerAction.ToggleSlideShow);
        state.PressDelete(1_000);

        Assert.Equal("c.png — 1/3 (live) · Del again to delete", state.Title());
    }

    /// <summary>The show steps to the right since 2026-10-03 (the user's call), older each slide, the oldest wrapping to the newest, live.</summary>
    [Fact]
    public void NextSlide_InOrder_StepsOlder_AndWrapsFromTheOldestToTheNewest()
    {
        var state = ThreePictures();
        state.Slides(ViewerAction.ToggleSlideShow);

        Assert.True(state.NextSlide(new Random(1)));
        Assert.Equal(@"D:\pics\b.png", state.Current);
        Assert.False(state.Live);
        Assert.True(state.NextSlide(new Random(1)));
        Assert.Equal(@"D:\pics\a.png", state.Current);
        Assert.False(state.Add(@"D:\pics\d.png", T0.AddMinutes(4)));   // held on the oldest: only counted
        Assert.True(state.NextSlide(new Random(1)));
        Assert.Equal(@"D:\pics\d.png", state.Current);   // the wrap lands on the newest, the one that arrived meanwhile
        Assert.True(state.Live);
        Assert.True(state.NextSlide(new Random(1)));
        Assert.Equal(@"D:\pics\c.png", state.Current);
    }

    [Fact]
    public void NextSlide_Shuffled_ShowsEveryPictureOnceARound_NeverTheSameTwice()
    {
        var state = new ViewerState();
        state.Reset(@"D:\p", Enumerable.Range(0, 6).Select(i => new ViewerEntry($@"D:\p\{i}.png", T0.AddMinutes(i))));
        state.Slides(ViewerAction.ToggleShuffle);
        state.Slides(ViewerAction.ToggleSlideShow);
        var random = new Random(42);

        var round = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            string before = state.Current!;
            Assert.True(state.NextSlide(random));
            Assert.NotEqual(before, state.Current);
            round.Add(state.Current!);
        }

        // The first round is everything but the picture the show started on, each once.
        Assert.Equal(5, round.Distinct().Count());
        Assert.DoesNotContain(@"D:\p\5.png", round);

        for (int i = 0; i < 20; i++)
        {
            string before = state.Current!;
            Assert.True(state.NextSlide(random));
            Assert.NotEqual(before, state.Current);
        }
    }

    [Fact]
    public void NextSlide_Shuffled_SkipsAPictureThatLeft()
    {
        var state = ThreePictures();
        state.Slides(ViewerAction.ToggleShuffle);
        state.Slides(ViewerAction.ToggleSlideShow);
        state.NextSlide(new Random(7));   // the bag now holds the one not shown
        string left = state.Pictures.Select(p => p.Path).Single(p => p != state.Current && p != @"D:\pics\c.png");
        state.Remove(left);

        Assert.True(state.NextSlide(new Random(7)));
        Assert.NotEqual(left, state.Current);
        Assert.Contains(state.Current, state.Pictures.Select(p => p.Path));
    }

    [Fact]
    public void NextSlide_WithOnePicture_IsNothing_AndResetStopsTheShow()
    {
        var state = new ViewerState();
        state.Reset(@"D:\p", [new(@"D:\p\a.png", T0)]);
        state.Slides(ViewerAction.ToggleSlideShow);

        Assert.False(state.NextSlide(new Random(1)));
        state.Reset(@"D:\q", []);
        Assert.False(state.SlideShow);
    }

    /// <summary>The viewer's colours from the theme (later on 2026-09-27) as COLORREFs, red in the low byte.</summary>
    [Fact]
    public void ViewerStyle_Synthwave_IsTheThemesColours()
    {
        var style = ViewerStyle.For(ShippedThemes.Synthwave);

        Assert.Equal(0x0016040Bu, style.Caption);
        Assert.Equal(0x00FFE6EFu, style.CaptionText);
        Assert.Equal(0x00972EFFu, style.Border);
        Assert.Equal(0x0016040Bu, style.Background);
        Assert.Equal(0x00B88B9Au, style.Text);
        Assert.Equal(0x00030201u, ViewerStyle.ColorRef(new Spectre.Console.Color(1, 2, 3)));
    }

    /// <summary>Themed external windows off (Themed image viewer, later on 2026-09-27): black whatever the theme; the default stays themed.</summary>
    [Fact]
    public void ViewerStyle_Unthemed_IsBlack()
    {
        var black = ViewerStyle.For(ShippedThemes.Synthwave, themed: false);

        Assert.Equal(ViewerStyle.Black, black);
        Assert.Equal((0u, 0x00FFFFFFu, 0u, 0u), (black.Caption, black.CaptionText, black.Border, black.Background));
        Assert.NotEqual(ViewerStyle.Black, ViewerStyle.For(ShippedThemes.Synthwave));
    }

    [Fact]
    public void ViewerStyle_EveryTheme_HasReadableCaptionText()
    {
        foreach (var palette in ShippedThemes.All)
        {
            var style = ViewerStyle.For(palette);
            Assert.NotEqual(style.Caption, style.CaptionText);
        }
    }

    /// <summary>Del twice (later on 2026-09-27, the user's call): the first arms with the title's hint, the second in time gives the path to delete.</summary>
    [Fact]
    public void IsAutoRepeat_ReadsBit30_SoAHeldDelIsOnePress()
    {
        Assert.False(ViewerState.IsAutoRepeat(0x00530001));                         // a fresh Del: repeat count 1, scan code
        Assert.True(ViewerState.IsAutoRepeat(0x40530001));                          // held: the key was down already
        Assert.True(ViewerState.IsAutoRepeat(unchecked((long)0xFFFFFFFFC0530001)));  // as a sign-extended IntPtr carries it
    }

    [WindowsFact]
    public void PressDelete_FirstArms_TheSecondInTimeGivesThePath()
    {
        var state = ThreePictures();

        Assert.Null(state.PressDelete(1_000));
        Assert.True(state.DeleteArmed);
        Assert.Equal("c.png — 1/3 (live) · Del again to delete", state.Title());

        Assert.Equal(@"D:\pics\c.png", state.PressDelete(1_000 + ViewerState.DeleteArmMilliseconds));
        Assert.False(state.DeleteArmed);
        Assert.Equal("c.png — 1/3 (live) · NeonSidekick pictures", state.Title());
    }

    [Fact]
    public void PressDelete_TooLate_ArmsAgain()
    {
        var state = ThreePictures();

        Assert.Null(state.PressDelete(1_000));
        Assert.Null(state.PressDelete(1_001 + ViewerState.DeleteArmMilliseconds));
        Assert.True(state.DeleteArmed);
        Assert.Equal(@"D:\pics\c.png", state.PressDelete(1_002 + ViewerState.DeleteArmMilliseconds));
    }

    /// <summary>Moving off the armed picture — browsing, or a new one arriving while live — drops the arming; the next Del arms, never deletes.</summary>
    [Fact]
    public void PressDelete_AfterTheShownPictureChanged_ArmsTheNewOne()
    {
        var state = ThreePictures();

        Assert.Null(state.PressDelete(1_000));
        state.Browse(ViewerAction.Older);
        Assert.False(state.DeleteArmed);
        Assert.Null(state.PressDelete(1_100));
        Assert.True(state.DeleteArmed);

        state.Browse(ViewerAction.Newest);
        Assert.Null(state.PressDelete(1_200));
        state.Add(@"D:\pics\d.png", T0.AddMinutes(4));   // live: d is shown now
        Assert.False(state.DeleteArmed);
        Assert.Null(state.PressDelete(1_300));
        Assert.Equal(@"D:\pics\d.png", state.PressDelete(1_400));
    }

    [Fact]
    public void Disarm_DropsTheArming_AndSaysWhetherThereWasOne()
    {
        var state = ThreePictures();

        Assert.False(state.Disarm());
        state.PressDelete(1_000);
        Assert.True(state.Disarm());
        Assert.False(state.DeleteArmed);
        Assert.Null(state.PressDelete(1_100));   // arms again, not deletes
    }

    [Fact]
    public void PressDelete_WithNoPicture_IsNothing()
    {
        var state = new ViewerState();
        state.Reset(@"D:\empty", []);

        Assert.Null(state.PressDelete(1_000));
        Assert.Null(state.PressDelete(1_100));
        Assert.False(state.DeleteArmed);
    }

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
        Assert.Equal("\U0001F39E️", ViewerText.StripButton);   // 🎞️ with its emoji selector, no word (2026-09-28)
        Assert.Equal(2, NeonSidekick.UI.TextCells.Width(ViewerText.StripButton));
        Assert.Equal("view", ViewerText.ViewWord);
        Assert.Equal("system", ViewerText.SystemViewerWord);
        Assert.Equal(@"Waiting for pictures in D:\p", ViewerText.Waiting(@"D:\p"));
        Assert.Equal("x.png could not be read as a picture", ViewerText.Unreadable("x.png"));
        Assert.Equal(@"Could not open the picture viewer on D:\p: denied", ViewerText.Failed(@"D:\p", "denied"));
        Assert.Equal("Could not delete x.png: in use", ViewerText.DeleteFailed("x.png", "in use"));
        Assert.Equal("(\U0001F5BC\uFE0F picture viewer on D:\\p)", ViewerText.Opened(@"D:\p"));   // the selector: two cells, one space
        Assert.Contains("F9 slide show", ViewerText.Keys);
        Assert.Contains("F10 random", ViewerText.Keys);
        Assert.Contains("← or wheel up newer · → or wheel down older · Home newest · End oldest · right-click picture menu", ViewerText.Keys);   // 2026-10-03; the wheel and the menu 2026-10-04
        Assert.Contains("TAB terminal", ViewerText.Keys);
        Assert.Equal("▶ 5 s", ViewerText.SlideShowTail(5, false));
        Assert.Equal("▶ 5 s · random", ViewerText.SlideShowTail(5, true));
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

    /// <summary>The log window's Win32 layer (2026-10-02): its own class and procedure, and Consolas drawn into a memory DC.</summary>
    [Fact]
    public void ProbeLogWindow_MakesAHiddenWindow_AndDrawsText()
    {
        var check = SmokeChecks.ProbeLogWindow();

        Assert.True(check.Passed, check.Detail);
        Assert.Equal("viewer:log-window", check.Name);
    }

    /// <summary>The drag out's shell data object (2026-09-28): made for a real file on an STA thread, it offers CF_HDROP.</summary>
    [Fact]
    public void ProbeDrag_TheShellsDataObject_OffersTheFile()
    {
        var check = SmokeChecks.ProbeViewerDrag();

        Assert.True(check.Passed, check.Detail);
        Assert.Equal("viewer:drag", check.Name);
    }

    /// <summary>The drag out starts past the system's drag rectangle centred on the press (2026-09-28), either way on either axis.</summary>
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(2, 2, false)]
    [InlineData(-2, -2, false)]
    [InlineData(3, 0, true)]
    [InlineData(-3, 0, true)]
    [InlineData(0, 3, true)]
    [InlineData(0, -3, true)]
    public void PastDragThreshold_IsTheDragRectangle(int dx, int dy, bool past)
    {
        Assert.Equal(past, ViewerState.PastDragThreshold(dx, dy, 4, 4));
        Assert.Equal(past, ViewerState.PastDragThreshold(dx, dy, 5, 5));   // an odd side rounds down, as DragDetect's
    }
}
