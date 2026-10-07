using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Camera;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>The camera's pure parts (2026-10-02): formats, pixels, the JPEG, the change test, the words, the modes.</summary>
public sealed class CameraPureTests
{
    private static CameraFormat F(int w, int h, double fps, string subtype) => new(new CameraSize(w, h), fps, subtype);

    [Fact]
    public void Pick_TakesTheExactSize_ThenTheSmallestLarger_ThenTheLargestSmaller()
    {
        var formats = new[] { F(640, 480, 30, "MJPG"), F(1280, 720, 30, "MJPG"), F(1920, 1080, 30, "MJPG"), F(3840, 2160, 30, "MJPG") };

        Assert.Equal(new CameraSize(1280, 720), CameraFormats.Pick(formats, new CameraSize(1280, 720))!.Size);
        Assert.Equal(new CameraSize(1920, 1080), CameraFormats.Pick(formats, new CameraSize(1600, 900))!.Size);
        Assert.Equal(new CameraSize(640, 480), CameraFormats.Pick([F(320, 240, 30, "YUY2"), F(640, 480, 30, "YUY2")], new CameraSize(1280, 720))!.Size);
        Assert.Null(CameraFormats.Pick([], new CameraSize(1280, 720)));
    }

    [Fact]
    public void Pick_AtOneSize_PrefersAtLeastFifteenFps_NearestThirty_ThenNv12OverYuy2OverMjpg()
    {
        var size = new CameraSize(1280, 720);

        Assert.Equal(30, CameraFormats.Pick([F(1280, 720, 5, "NV12"), F(1280, 720, 60, "MJPG"), F(1280, 720, 30, "MJPG")], size)!.Fps);
        Assert.Equal(60, CameraFormats.Pick([F(1280, 720, 5, "NV12"), F(1280, 720, 60, "MJPG")], size)!.Fps);
        Assert.Equal("NV12", CameraFormats.Pick([F(1280, 720, 30, "MJPG"), F(1280, 720, 30, "YUY2"), F(1280, 720, 30, "NV12")], size)!.Subtype);
        Assert.Equal("YUY2", CameraFormats.Pick([F(1280, 720, 30, "H264"), F(1280, 720, 30, "YUY2")], size)!.Subtype);
        Assert.Equal(3, CameraFormats.SubtypeRank("H264"));
    }

    [Fact]
    public void Attributes_PackAndUnpack_AndSubtypesReadAsTheirFourCc()
    {
        var size = new CameraSize(1920, 1080);

        Assert.Equal(size, CameraFormats.UnpackSize(CameraFormats.PackSize(size)));
        Assert.Equal(0x0000_0780_0000_0438UL, CameraFormats.PackSize(size));
        Assert.Equal(30, CameraFormats.UnpackRate((30UL << 32) | 1));
        Assert.Equal(29.97, CameraFormats.UnpackRate((30000UL << 32) | 1001), 2);
        Assert.Equal(0, CameraFormats.UnpackRate(30UL << 32));
        Assert.Equal("NV12", CameraFormats.SubtypeName(new Guid("3231564e-0000-0010-8000-00aa00389b71")));
        Assert.Equal("MJPG", CameraFormats.SubtypeName(new Guid("47504a4d-0000-0010-8000-00aa00389b71")));
        Assert.Equal("00000016-0000-0010-8000-00aa00389b71", CameraFormats.SubtypeName(new Guid("00000016-0000-0010-8000-00aa00389b71")));
        Assert.Equal("1280x720 MJPG 30 fps", CameraFormats.Describe(F(1280, 720, 30, "MJPG")));
    }

    [Theory]
    [InlineData("1280x720", 1280, 720)]
    [InlineData(" 640 X 480 ", 640, 480)]
    [InlineData("1920×1080", 1920, 1080)]
    public void TryParseSize_ReadsTheResolution(string text, int width, int height)
    {
        Assert.True(CameraFormats.TryParseSize(text, out var size));
        Assert.Equal(new CameraSize(width, height), size);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1280")]
    [InlineData("8x8")]
    [InlineData("axb")]
    [InlineData("1x2x3")]
    public void TryParseSize_RefusesTheRest(string? text) => Assert.False(CameraFormats.TryParseSize(text, out _));

    [Fact]
    public void CopyTopDown_ReadsATopDownBuffer_ABottomUpOne_AndAPaddedPitch()
    {
        // Two rows of two pixels: row 0 is 1s, row 1 is 2s.
        byte[] topDown = [.. Enumerable.Repeat((byte)1, 8), .. Enumerable.Repeat((byte)2, 8)];
        byte[] bottomUp = [.. Enumerable.Repeat((byte)2, 8), .. Enumerable.Repeat((byte)1, 8)];
        byte[] padded = [.. Enumerable.Repeat((byte)1, 8), 9, 9, 9, 9, .. Enumerable.Repeat((byte)2, 8), 9, 9, 9, 9];
        var into = new byte[16];

        CameraPixels.CopyTopDown(topDown, 0, 8, 2, 2, into);
        Assert.Equal(topDown, into);
        CameraPixels.CopyTopDown(bottomUp, 8, -8, 2, 2, into);
        Assert.Equal(topDown, into);
        CameraPixels.CopyTopDown(padded, 0, 12, 2, 2, into);
        Assert.Equal(topDown, into);
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraPixels.CopyTopDown(topDown, 0, 4, 2, 2, into));
        Assert.Throws<ArgumentException>(() => CameraPixels.CopyTopDown(topDown, 0, 8, 2, 2, new byte[8]));
    }

    [Fact]
    public void Fit_NeverEnlarges_AndKeepsTheAspect()
    {
        Assert.Equal((640, 480), CameraPixels.Fit(640, 480, 1024));
        Assert.Equal((1024, 576), CameraPixels.Fit(1920, 1080, 1024));
        Assert.Equal((540, 960), CameraPixels.Fit(1080, 1920, 960));
        Assert.Equal((2, 1), CameraPixels.Fit(4000, 1, 2));   // a side never below 1
    }

    [Fact]
    public void Scale_BoxAverages_Mirrors_AndHandsBackTheFrameUnchanged()
    {
        // 2×1: a black pixel then a white one.
        var frame = new CameraFrame(2, 1, [0, 0, 0, 0, 255, 255, 255, 0], 1, default);

        Assert.Same(frame.Bgrx, CameraPixels.Scale(frame, 2, 1));
        Assert.Equal([255, 255, 255, 255, 0, 0, 0, 255], CameraPixels.Scale(frame, 2, 1, mirror: true));
        Assert.Equal([127, 127, 127, 255], CameraPixels.Scale(frame, 1, 1));
        Assert.Throws<ArgumentException>(() => CameraPixels.ScaleInto(frame, 2, 1, false, new byte[4]));
    }

    [Fact]
    public void MeanLuma_ReadsTheBrightness()
    {
        var size = new CameraSize(64, 36);
        Assert.Equal(0, CameraPixels.MeanLuma(new CameraFrame(64, 36, FakeCameraSystem.Solid(size, 0, 0, 0), 1, default)));
        Assert.Equal(255, CameraPixels.MeanLuma(new CameraFrame(64, 36, FakeCameraSystem.Solid(size, 255, 255, 255), 1, default)), 0);
        Assert.Equal(0, CameraPixels.MeanLuma(new CameraFrame(64, 36, [], 1, default)));
        Assert.Equal(76, CameraPixels.Luma(0, 0, 255));
    }

    [WindowsFact]
    public void Encode_IsAJpeg_ScaledToFit_ThatTryLoadKeepsByteForByte_RedStayingRed()
    {
        var size = new CameraSize(320, 240);
        var frame = new CameraFrame(320, 240, FakeCameraSystem.Solid(size, 230, 20, 20), 1, default);

        byte[] jpeg = CameraJpeg.Encode(frame);
        byte[] small = CameraJpeg.Encode(frame, maxSide: 160);
        var attached = CameraJpeg.Attachment(frame, "shot.jpg", 160);

        Assert.Equal([0xFF, 0xD8, 0xFF], jpeg.Take(3));
        Assert.True(ImageFile.TryLoad(jpeg, "shot.jpg", out var image, out _));
        Assert.Same(jpeg, image!.Bytes);
        Assert.Equal((320, 240), (image.Width, image.Height));
        var decoded = ViewerImage.Decode(jpeg, "shot.jpg")!;
        Assert.True(decoded.Bgrx[2] > 200 && decoded.Bgrx[0] < 60);
        Assert.True(ImageFile.TryLoad(small, "small.jpg", out var shrunk, out _));
        Assert.Equal((160, 120), (shrunk!.Width, shrunk.Height));
        Assert.True(attached.Camera);
        Assert.Equal(ImageFile.Jpeg, attached.MediaType);
        Assert.Equal((160, 120), (attached.Width, attached.Height));
        Assert.Throws<ArgumentException>(() => CameraJpeg.Encode(new CameraFrame(4, 4, [], 1, default)));
    }

    [Fact]
    public void FrameDiff_SeesNoChangeInTheSamePicture_NorInABrighterOne_ButSeesAHand()
    {
        var size = new CameraSize(128, 72);
        var still = new CameraFrame(128, 72, FakeCameraSystem.Solid(size, 100, 100, 100), 1, default);
        var brighter = new CameraFrame(128, 72, FakeCameraSystem.Solid(size, 160, 160, 160), 2, default);
        var hand = FakeCameraSystem.Solid(size, 100, 100, 100);
        for (int y = 0; y < 36; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int at = ((y * 128) + x) * 4;
                hand[at] = hand[at + 1] = hand[at + 2] = 250;
            }
        }

        double none = FrameDiff.Changed(FrameDiff.Grid(still), FrameDiff.Grid(still));
        double exposure = FrameDiff.Changed(FrameDiff.Grid(still), FrameDiff.Grid(brighter));
        double moved = FrameDiff.Changed(FrameDiff.Grid(still), FrameDiff.Grid(new CameraFrame(128, 72, hand, 3, default)));

        Assert.Equal(0, none);
        Assert.Equal(0, exposure);
        Assert.InRange(moved, 0.2, 1);
        Assert.True(FrameDiff.Reaches(moved, 8));
        Assert.False(FrameDiff.Reaches(0.07, 8));
        Assert.Equal(1, FrameDiff.Changed([], []));
        Assert.Equal(1, FrameDiff.Changed([1], [1, 2]));
    }

    [Fact]
    public void FailureOf_MapsTheHresults_AndEachFailureHasItsSentence()
    {
        Assert.Equal(CameraFailure.Blocked, CameraText.FailureOf(unchecked((int)0x80070005)));
        Assert.Equal(CameraFailure.InUse, CameraText.FailureOf(unchecked((int)0xC00D3704)));
        Assert.Equal(CameraFailure.InUse, CameraText.FailureOf(unchecked((int)0xC00DABE1)));
        Assert.Equal(CameraFailure.Unplugged, CameraText.FailureOf(unchecked((int)0xC00DABE0)));
        Assert.Equal(CameraFailure.Failed, CameraText.FailureOf(unchecked((int)0x80004005)));
        Assert.Equal("0x80004005", CameraText.Hresult(unchecked((int)0x80004005)));
        Assert.Contains("Let desktop apps access your camera", CameraText.Failure(CameraFailure.Blocked, null));
        Assert.Contains("Another app", CameraText.Failure(CameraFailure.InUse, null));
        Assert.Equal("The camera failed. (0x80004005 from ReadSample)", CameraText.Failure(CameraFailure.Failed, "0x80004005 from ReadSample"));
        Assert.All(Enum.GetValues<CameraFailure>(), f => Assert.False(string.IsNullOrWhiteSpace(CameraText.Failure(f, null))));
        var error = new CameraException(CameraFailure.NoCamera, null);
        Assert.Equal(CameraFailure.NoCamera, error.Failure);
        Assert.Equal("No camera is connected.", error.Message);
        Assert.Equal(CameraFailure.Failed, new CameraException("x").Failure);
        Assert.Equal(CameraFailure.Failed, new CameraException("x", new InvalidOperationException()).Failure);
        Assert.Equal(CameraFailure.Failed, new CameraException().Failure);
    }

    [Fact]
    public void TheModes_ReadTheirWords_UnknownOnesTheDefault()
    {
        Assert.Equal(CameraShutter.Model, CameraShutterMode.Resolve(new AppSettingsData { CameraShutter = " MODEL " }));
        Assert.Equal(CameraShutter.User, CameraShutterMode.Resolve(new AppSettingsData { CameraShutter = "nobody" }));
        Assert.Equal(CameraPreview.Post, CameraPreviewMode.Resolve(new AppSettingsData { CameraPreview = "post" }));
        Assert.Equal(CameraPreview.Disabled, CameraPreviewMode.Resolve(new AppSettingsData { CameraPreview = "disabled" }));
        Assert.Equal(CameraPreview.Live, CameraPreviewMode.Resolve(new AppSettingsData { CameraPreview = "huh" }));
        Assert.Equal(CameraPreview.Live, CameraPreviewMode.Resolve(new AppSettingsData()));
        Assert.All(CameraShutterMode.Names, n => Assert.NotEmpty(CameraShutterMode.Describe(n)));
        Assert.All(CameraPreviewMode.Names, n => Assert.NotEmpty(CameraPreviewMode.Describe(n)));
        Assert.Empty(CameraShutterMode.Describe("x"));
        Assert.Empty(CameraPreviewMode.Describe("x"));
        Assert.Equal(new CameraOptions("Fake Cam", new CameraSize(640, 480)), CameraSettings.Options(new AppSettingsData { CameraDevice = "Fake Cam", CameraResolution = "640x480" }));
        Assert.Equal(new CameraSize(1280, 720), CameraSettings.Options(new AppSettingsData { CameraResolution = "huge" }).Target);
    }

    [Fact]
    public void TheDefaults_KeepTheModelAwayFromTheCamera_AndTheFacesOutOfTheSessions()
    {
        var data = new AppSettingsData();

        Assert.False(data.CameraTools);
        Assert.Equal("user", data.CameraShutter);
        Assert.Equal("live", data.CameraPreview);
        Assert.Equal("", data.CameraDevice);
        Assert.Equal("1280x720", data.CameraResolution);
        Assert.False(data.CameraKeepInSessions);
        Assert.Equal(10, data.CameraWatchSeconds);
        Assert.Equal(8, data.CameraWatchThreshold);
        Assert.False(data.CameraWatchUnprompted);
        Assert.Equal(120, data.CameraWatchMinGapSeconds);
        Assert.False(data.BotChatCamera);
    }

    [Fact]
    public void Command_Parses_ItsWords()
    {
        Assert.Equal(new CameraCommandLine(CameraVerb.Shutter, ""), CameraCommand.Parse("  "));
        Assert.Equal(new CameraCommandLine(CameraVerb.Snap, ""), CameraCommand.Parse("SNAP"));
        Assert.Equal(new CameraCommandLine(CameraVerb.Use, "Other Cam"), CameraCommand.Parse("use  Other Cam"));
        Assert.Equal(new CameraCommandLine(CameraVerb.Watch, "5"), CameraCommand.Parse("watch 5"));
        Assert.Equal(new CameraCommandLine(CameraVerb.Watch, "off"), CameraCommand.Parse("watch off"));
        Assert.Equal(CameraVerb.Unknown, CameraCommand.Parse("use").Verb);
        Assert.Equal(CameraText.Usage, CameraCommand.Parse("use").Error);
        Assert.Equal(CameraText.Usage, CameraCommand.Parse("list all").Error);
        Assert.Equal(CameraText.Unknown("zoom"), CameraCommand.Parse("zoom in").Error);
        Assert.Equal(CameraVerb.Live, CameraCommand.Parse("live").Verb);
        Assert.Equal(CameraVerb.Off, CameraCommand.Parse("off").Verb);
        Assert.Equal(CameraVerb.List, CameraCommand.Parse("list").Verb);
    }

    [Fact]
    public void Find_TakesANumber_AName_OrTheOnePrefix()
    {
        IReadOnlyList<CameraDevice> devices = [new("MX Brio", "a"), new("MX Keys Cam", "b"), new("Laptop Camera", "c")];

        Assert.Equal("MX Keys Cam", CameraCommand.Find(devices, "2")!.Name);
        Assert.Null(CameraCommand.Find(devices, "4"));
        Assert.Null(CameraCommand.Find(devices, "0"));
        Assert.Equal("MX Brio", CameraCommand.Find(devices, "\"mx brio\"")!.Name);
        Assert.Equal("Laptop Camera", CameraCommand.Find(devices, "lap")!.Name);
        Assert.Null(CameraCommand.Find(devices, "MX"));   // two begin with it
        Assert.Null(CameraCommand.Find(devices, ""));
    }

    [Fact]
    public void Complete_OffersTheWords_ThenOffAfterWatch()
    {
        Assert.Contains(CameraCommand.Complete(""), i => i.Text == "snap");
        Assert.Equal(["watch"], CameraCommand.Complete("wa").Select(i => i.Text));
        Assert.Equal(["watch off"], CameraCommand.Complete("watch o").Select(i => i.Text));
        Assert.Empty(CameraCommand.Complete("watch off now"));
        Assert.Empty(CameraCommand.Complete("use x"));
    }

    [Fact]
    public void Texts_ReadAsPinned()
    {
        var shot = new CameraShot(new ImageAttachment(@"C:\w\camera\x.jpg", [1], ImageFile.Jpeg, 1280, 720) { Camera = true }, "camera/x.jpg", @"C:\w\camera\x.jpg",
            new CameraFrame(1280, 720, [], 1, default), "MX Brio");

        Assert.Equal("photo taken with MX Brio (1280x720), saved as camera/x.jpg: the picture is in the next message", CameraText.Taken(shot));
        Assert.Equal("MX Brio: 1280x720 at 14:02:03, camera/x.jpg", CameraText.ShotStatus(shot, new DateTimeOffset(2026, 10, 2, 14, 2, 3, TimeSpan.Zero)));
        Assert.Equal("[camera photo not kept in the session: camera/x.jpg]", CameraText.NotKept("camera/x.jpg"));
        Assert.Equal("The camera \"Gone\" is not connected; using \"MX Brio\".", CameraText.FellBack("Gone", "MX Brio"));
        Assert.Equal("  2. MX Brio  ← chosen", CameraText.ListLine(2, new CameraDevice("MX Brio", "a"), true));
        Assert.Equal("📷 camera/x.jpg is on the input line.", CameraText.Attached("camera/x.jpg"));
        Assert.Equal("📷 camera/x.jpg", CameraText.Note("camera/x.jpg"));
        Assert.Equal("Error: boom", CameraText.ToolFailed("boom"));
        Assert.Equal("📷 MX Brio — live", CameraText.LiveTitle("MX Brio"));
        Assert.Equal("📷 Camera — the shot", CameraText.HeldTitle(null));
        Assert.Contains("every 5 s", CameraText.WatchOn(5, 8, false));
        Assert.EndsWith("the model may speak up. /camera watch off stops it.", CameraText.WatchOn(5, 8, true));
        Assert.Equal("(Attached: the user's camera at 14:02:03; the picture changed since the last one you saw.)", CameraText.WatchCaption(new DateTimeOffset(2026, 10, 2, 14, 2, 3, TimeSpan.Zero)));
        Assert.Contains("2–3600", CameraText.BadSeconds("x"));
        Assert.Equal("The camera was not held by /camera live or watch.", CameraText.Off(0));
        Assert.Equal("The photo could not be saved: Error: no", CameraText.NotSaved("Error: no"));
        Assert.Equal("MX Brio is on: frame the shot, then press Space.", CameraText.Waiting("MX Brio"));
        Assert.Equal("The camera is opening…", CameraText.Waiting(null));
        Assert.Equal("No camera 'x' is connected (/camera list).", CameraText.NoSuchCamera("x"));
        Assert.Equal("Camera: MX Brio.", CameraText.Using("MX Brio"));
        Assert.EndsWith("The bots go on without the camera.", CameraText.BotChatFailed("No camera is connected."));
    }

    [Fact]
    public void BotChat_PutsTheCameraLast_WithinTheCap()
    {
        var camera = new ImageAttachment("cam", [1], ImageFile.Jpeg, 1, 1) { Camera = true };
        IReadOnlyList<ImageAttachment> four = Enumerable.Range(1, 4).Select(i => new ImageAttachment("p" + i, [1], ImageFile.Png, 1, 1)).ToList();

        var withCamera = BotChat.WithCamera(four, camera);

        Assert.Equal(["p2", "p3", "p4", "cam"], withCamera.Select(i => i.Path));
        Assert.Equal(["cam"], BotChat.WithCamera([], camera).Select(i => i.Path));
        Assert.Equal("(Picture 3 of 3, attached last: a live photo of User, the human in this chat, from their webcam just now — not from any of the bots.)", BotChat.CameraCaption(3, 3));
        Assert.EndsWith(BotChat.CameraRule, BotChat.Rules("ada", ["neon"], "", camera: true));
        Assert.DoesNotContain(BotChat.CameraRule, BotChat.Rules("ada", ["neon"], ""));
        Assert.Contains("It shows User", BotChat.CameraRule);
        Assert.EndsWith(BotChat.CameraRule, BotChat.SystemPrompt(null, "ada", ["neon"], "", false, null, false, camera: true));
    }

    [Fact]
    public void BotChat_TurnPictures_LeaveTheCameraTheLastSlot_SoTheCaptionNamesWhatIsAttached()
    {
        var camera = new ImageAttachment("cam", [9], ImageFile.Jpeg, 1, 1) { Camera = true };
        IReadOnlyList<BotPicture> log = Enumerable.Range(1, 4).Select(i => new BotPicture(i, "ada", false, [new ImageAttachment("p" + i, [1], ImageFile.Png, 1, 1)])).ToList();

        var (images, captions) = BotChat.TurnPictures("neon", log, 0, vision: true, camera);
        var (alone, aloneCaptions) = BotChat.TurnPictures("neon", log, 0, vision: false, camera);
        var (noCamera, noCameraCaptions) = BotChat.TurnPictures("neon", log, 0, vision: true, null);
        var (nothing, nothingCaptions) = BotChat.TurnPictures("neon", null, 0, vision: true, null);

        Assert.Equal(["p2", "p3", "p4", "cam"], images.Select(i => i.Path));
        Assert.Contains(BotChat.PicturesCaption("neon", BotChat.PicturesFor("neon", log, 0, 3)), captions);
        Assert.Equal(3, captions.Split("ada's reply").Length - 1);   // three named, three attached before the camera's
        Assert.EndsWith(BotChat.CameraCaption(4, 4), captions);
        Assert.Equal(["cam"], alone.Select(i => i.Path));
        Assert.Equal("\n\n" + BotChat.CameraCaption(1, 1), aloneCaptions);
        Assert.Equal(["p1", "p2", "p3", "p4"], noCamera.Select(i => i.Path));
        Assert.DoesNotContain("Picture", noCameraCaptions);
        Assert.Empty(nothing);
        Assert.Equal("", nothingCaptions);
    }

    [Fact]
    public void TheStrip_LeadsWithTheCamera_WhileItIsOpen()
    {
        Assert.Equal("🧠", ChatScreen.CameraStrip(false, "🧠"));
        Assert.Equal("📷 🧠", ChatScreen.CameraStrip(true, "🧠"));
        Assert.Equal("📷", ChatScreen.CameraStrip(true, ""));
        Assert.Equal(2, NeonSidekick.UI.TextCells.Width(CameraText.Glyph));
    }

    [Fact]
    public void CameraOffered_NeedsTheSwitch_ALayer_ThePane_AndAModelThatSees()
    {
        var on = new AppSettingsData { CameraTools = true };

        Assert.True(ChatScreen.CameraOffered(on, available: true, pane: true, blind: false));
        Assert.False(ChatScreen.CameraOffered(new AppSettingsData(), true, true, false));
        Assert.False(ChatScreen.CameraOffered(on, false, true, false));
        Assert.False(ChatScreen.CameraOffered(on, true, false, false));
        Assert.False(ChatScreen.CameraOffered(on, true, true, true));
    }

    [Fact]
    public void LiveViewState_KeepsFullScreenAndClose_AndPacesTheFrames()
    {
        Assert.Equal(ViewerAction.Close, LiveViewState.Filter(ViewerAction.Close));
        Assert.Equal(ViewerAction.ToggleFullScreen, LiveViewState.Filter(ViewerAction.ToggleFullScreen));
        Assert.Equal(ViewerAction.LeaveFullScreen, LiveViewState.Filter(ViewerAction.LeaveFullScreen));
        Assert.Equal(ViewerAction.None, LiveViewState.Filter(ViewerAction.Delete));
        Assert.Equal(ViewerAction.None, LiveViewState.Filter(ViewerAction.Newer));
        Assert.True(LiveViewState.Due(TimeSpan.FromMilliseconds(66)));
        Assert.False(LiveViewState.Due(TimeSpan.FromMilliseconds(30)));
    }

    [Fact]
    public void ALiveView_WithoutAWindow_RecyclesBuffers_AndEndsOnce()
    {
        int ended = 0;
        var view = new LiveView("title", () => ended++);

        var buffer = view.Rent(16);
        view.Recycle(buffer);
        view.Recycle(new byte[8]);
        view.Recycle(new byte[16]);   // two at most
        view.Post(new ViewerBitmap(2, 2, buffer));
        view.Freeze(new ViewerBitmap(2, 2, buffer), "held");
        view.Resume("live");

        Assert.Same(buffer, view.Rent(16));
        Assert.Equal(32, view.Rent(32).Length);
        Assert.Equal("live", view.Title);
        Assert.True(view.Open);
        view.OnEnded();
        view.OnEnded();
        view.Dispose();
        Assert.Equal(1, ended);
        Assert.False(view.Open);
    }

    [Fact]
    public void ACameraPicture_IsMarked_AndAStoredSessionKeepsALineForIt_UnlessAskedToKeepIt()
    {
        var history = new ConversationHistory("system");
        history.AddUser("look", [new ImageAttachment(@"C:\w\camera\a.jpg", [1, 2, 3], ImageFile.Jpeg, 1, 1) { Camera = true }, new ImageAttachment("pasted.png", [4], ImageFile.Png, 1, 1)]);
        history.AddToolImages([new ImageAttachment(@"C:\w\camera\b.jpg", [5], ImageFile.Jpeg, 1, 1) { Camera = true }], CameraCaptureTool.ToolName);

        string dropped = SessionHistory.ToJson(history.Messages);
        string kept = SessionHistory.ToJson(history.Messages, keepCamera: true);

        Assert.Equal(@"C:\w\camera\a.jpg", ConversationHistory.CameraPath(history.Messages[^2].Contents[1]));
        Assert.Null(ConversationHistory.CameraPath(history.Messages[^2].Contents[2]));
        Assert.Contains(JsonEncodedText.Encode(CameraText.NotKept(@"C:\w\camera\a.jpg")).ToString(), dropped);
        Assert.Contains(JsonEncodedText.Encode(CameraText.NotKept(@"C:\w\camera\b.jpg")).ToString(), dropped);
        Assert.DoesNotContain(Convert.ToBase64String([1, 2, 3]), dropped);
        Assert.Contains(Convert.ToBase64String([4]), dropped);   // a pasted picture is kept as ever
        Assert.Contains(Convert.ToBase64String([1, 2, 3]), kept);
        var restored = SessionHistory.FromJson(dropped);
        Assert.Equal(2, restored.Count(m => m.Contents.OfType<TextContent>().Any(t => t.Text.StartsWith("[camera photo", StringComparison.Ordinal))));
    }
}

/// <summary>The shared camera stream over the fake (2026-10-02): leases, the linger, the warm-up, failures, the notice.</summary>
public sealed class CameraSessionTests : IDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeCameraSystem _system = new();
    private readonly AppSettingsData _settings = new();
    private readonly CameraSession _session;

    public CameraSessionTests()
    {
        _session = new CameraSession(_system, () => CameraSettings.Options(_settings), _time);
    }

    public void Dispose() => _session.Dispose();

    internal static async Task Until(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task TwoLeases_ShareOneOpen_AndTheLingerClosesIt_AfterTheLast()
    {
        var states = new List<CameraState>();
        _session.Changed += () => { lock (states) { states.Add(_session.State); } };

        var one = _session.Acquire("one");
        var two = _session.Acquire("two");
        var frame = await one.NextFrameAsync(settled: true, CancellationToken.None);
        _ = await two.NextFrameAsync(settled: false, CancellationToken.None);

        Assert.Equal(1, _system.Opens);
        Assert.Equal(_system.Devices[0], _system.Opened);
        Assert.Equal(new CameraSize(1280, 720), _system.Target);
        Assert.Equal("Fake Cam", _session.Device!.Name);
        Assert.Equal("NV12", _session.Format!.Subtype);
        Assert.Equal(new CameraSize(64, 48), _session.FrameSize);
        Assert.Equal(64 * 48 * 4, frame.Bgrx.Length);
        Assert.True(frame.Sequence >= 1);
        Assert.Equal(_time.GetUtcNow(), frame.At);
        Assert.Equal(["one", "two"], _session.Holders);
        Assert.True(_session.Live);
        one.Dispose();
        one.Dispose();
        _time.Advance(CameraSession.Linger);
        Assert.Equal(0, _system.Closes);   // a lease still holds it
        two.Dispose();
        _time.Advance(CameraSession.Linger - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, _system.Closes);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await Until(() => _system.Closes == 1 && _session.State == CameraState.Off);
        Assert.False(_session.Live);
        lock (states)
        {
            Assert.Contains(CameraState.On, states);
            Assert.Equal(CameraState.Off, states[^1]);
        }
    }

    [Fact]
    public async Task ALeaseWithinTheLinger_KeepsTheStream()
    {
        var first = _session.Acquire("one");
        _ = await first.NextFrameAsync(settled: false, CancellationToken.None);
        first.Dispose();
        _time.Advance(TimeSpan.FromSeconds(1));
        using var second = _session.Acquire("two");
        _time.Advance(CameraSession.Linger);
        _ = await second.NextFrameAsync(settled: false, CancellationToken.None);

        Assert.Equal(1, _system.Opens);
        Assert.Equal(0, _system.Closes);
    }

    [Fact]
    public async Task ASettledFrame_WaitsForTheWarmUpsFramesAndTime()
    {
        _system.Warmup = new CameraWarmup(3, TimeSpan.FromMilliseconds(800));
        using var lease = _session.Acquire("test");

        var early = await lease.NextFrameAsync(settled: false, CancellationToken.None);
        var settled = lease.NextFrameAsync(settled: true, CancellationToken.None);
        await Until(() => _system.Reads >= 5);

        Assert.False(settled.IsCompleted);
        Assert.Equal(CameraState.Warming, _session.State);
        _time.Advance(TimeSpan.FromMilliseconds(800));
        var frame = await settled;
        Assert.True(frame.Sequence >= 3);
        Assert.True(frame.Sequence > early.Sequence);
        await Until(() => _session.State == CameraState.On);
    }

    [Fact]
    public async Task AFailedOpen_FailsTheWait_AndTheNextWaitOpensAgain()
    {
        _system.OpenFailure = new CameraException(CameraFailure.InUse, null);
        using var lease = _session.Acquire("test");

        var error = await Assert.ThrowsAsync<CameraException>(() => lease.NextFrameAsync(settled: false, CancellationToken.None));
        Assert.Equal(CameraFailure.InUse, error.Failure);
        await Until(() => _session.State == CameraState.Faulted);
        Assert.Equal(error.Message, _session.Fault);
        _system.OpenFailure = null;
        _ = await lease.NextFrameAsync(settled: false, CancellationToken.None);
        Assert.Equal(1, _system.Opens);
        Assert.Null(_session.Fault);
    }

    [Fact]
    public async Task AReadFailure_FailsTheWaiters()
    {
        _system.ReadFailure = new CameraException(CameraFailure.Unplugged, "gone");
        _system.FailAfterFrames = 2;
        using var lease = _session.Acquire("test");

        var error = await Assert.ThrowsAsync<CameraException>(async () =>
        {
            while (true)
            {
                _ = await lease.NextFrameAsync(settled: false, CancellationToken.None);
            }
        });

        Assert.Equal(CameraFailure.Unplugged, error.Failure);
        await Until(() => _system.Closes == 1);
    }

    [Fact]
    public async Task NoCamera_IsTheWaitsFailure()
    {
        _system.Devices.Clear();
        using var lease = _session.Acquire("test");

        var error = await Assert.ThrowsAsync<CameraException>(() => lease.NextFrameAsync(settled: false, CancellationToken.None));

        Assert.Equal(CameraFailure.NoCamera, error.Failure);
    }

    [Fact]
    public async Task AnotherException_IsAFailure_ToBeToldAbout()
    {
        var session = new CameraSession(new ThrowingSystem(), () => CameraSettings.Options(_settings), _time);
        using var lease = session.Acquire("test");

        var error = await Assert.ThrowsAsync<CameraException>(() => lease.NextFrameAsync(settled: false, CancellationToken.None));

        Assert.Equal(CameraFailure.Failed, error.Failure);
        Assert.Contains("odd", error.Message);
        session.Dispose();
    }

    [Fact]
    public async Task NoFrame_InTime_IsNoFrames()
    {
        _system.Stepped = true;
        using var lease = _session.Acquire("test");

        var wait = lease.NextFrameAsync(settled: false, CancellationToken.None);
        await Until(() => _session.State == CameraState.Warming);
        _time.Advance(CameraSession.FrameTimeout);

        var error = await Assert.ThrowsAsync<CameraException>(() => wait);
        Assert.Equal(CameraFailure.NoFrames, error.Failure);
        _system.Deliver(5);
    }

    [Fact]
    public async Task TheSavedCamera_IsOpened_AndAMissingOneFallsBackWithANotice()
    {
        _settings.CameraDevice = "other cam";
        using (var lease = _session.Acquire("test"))
        {
            _ = await lease.NextFrameAsync(settled: false, CancellationToken.None);
            Assert.Equal("Other Cam", _system.Opened!.Name);
            Assert.Null(_session.TakeNotice());
        }

        Assert.Equal(_system.Devices[1], CameraSession.Choose(_system.Devices, "OTHER CAM", out bool fellBack));
        Assert.False(fellBack);
        Assert.Equal(_system.Devices[0], CameraSession.Choose(_system.Devices, "Gone", out fellBack));
        Assert.True(fellBack);
        Assert.Null(CameraSession.Choose([], "x", out _));

        _time.Advance(CameraSession.Linger);
        await Until(() => _session.State == CameraState.Off);
        _settings.CameraDevice = "Gone";
        using var again = _session.Acquire("test");
        _ = await again.NextFrameAsync(settled: false, CancellationToken.None);
        Assert.Equal(CameraText.FellBack("Gone", "Fake Cam"), _session.TakeNotice());
        Assert.Null(_session.TakeNotice());
    }

    [Fact]
    public async Task Revoke_TakesThePurposesLeases_AndTellsTheirOwners()
    {
        int revoked = 0;
        var live = _session.Acquire("live", () => revoked++);
        using var other = _session.Acquire("watch");
        _ = await other.NextFrameAsync(settled: false, CancellationToken.None);

        Assert.Equal(1, _session.Revoke("live"));
        Assert.Equal(0, _session.Revoke("live"));
        Assert.Equal(1, revoked);
        Assert.True(live.Released);
        Assert.Equal(["watch"], _session.Holders);
        Assert.Throws<ObjectDisposedException>(() => { _ = live.NextFrameAsync(false, CancellationToken.None); });
    }

    [Fact]
    public async Task AWatcher_GetsTheFrames_UntilDisposed()
    {
        int frames = 0;
        using var lease = _session.Acquire("live");
        var watch = _session.Watch(f => Interlocked.Increment(ref frames));
        await Until(() => Volatile.Read(ref frames) >= 3);
        watch.Dispose();
        watch.Dispose();
        int seen = Volatile.Read(ref frames);
        int reads = _system.Reads;
        await Until(() => _system.Reads > reads + 5);

        Assert.InRange(Volatile.Read(ref frames), seen, seen + 1);
    }

    [Fact]
    public async Task WithoutALayer_ALeaseIsUnsupported_AndAWaitNeedsALease()
    {
        var none = new CameraSession(null, () => CameraSettings.Options(_settings), _time);

        Assert.False(none.Available);
        Assert.Equal(CameraFailure.Unsupported, Assert.Throws<CameraException>(() => none.Acquire("x")).Failure);
        Assert.Equal(CameraFailure.Unsupported, Assert.Throws<CameraException>(() => none.List()).Failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.NextFrameAsync(false, CancellationToken.None));
        none.Dispose();
    }

    [Fact]
    public async Task Dispose_FailsAWaiter_AndRefusesMore()
    {
        _system.Stepped = true;
        var lease = _session.Acquire("test");
        var wait = lease.NextFrameAsync(settled: false, CancellationToken.None);
        await Until(() => _session.State == CameraState.Warming);

        _session.Dispose();

        await Assert.ThrowsAsync<CameraException>(() => wait);
        Assert.Throws<ObjectDisposedException>(() => _session.Acquire("again"));
        _system.Deliver(5);
    }

    private sealed class ThrowingSystem : ICameraSystem
    {
        public IReadOnlyList<CameraDevice> List() => [new("x", "x")];

        public ICameraStream Open(CameraDevice device, CameraSize target) => throw new InvalidOperationException("odd");
    }
}

/// <summary>Photos taken and saved, the model's tool, the list and watch mode over the fake (2026-10-02).</summary>
public sealed class CameraCaptureTests : IDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeCameraSystem _system = new();
    private readonly AppSettingsData _settings = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("neon-camera-").FullName;
    private readonly CameraSession _session;
    private readonly WorkingDirectory _files;

    public CameraCaptureTests()
    {
        _session = new CameraSession(_system, () => CameraSettings.Options(_settings), _time);
        _files = new WorkingDirectory(() => _dir, _time);
    }

    public void Dispose()
    {
        _session.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private CameraCapture Capture() => new(_session, () => _files, () => _settings.CameraOutputFolder, () => CameraSettings.Options(_settings), _time);

    [WindowsFact]
    public async Task ASnap_IsSavedUnderCamera_StampedLocally_AClashNumbered_AndADiscardDeletesIt()
    {
        var capture = Capture();
        using var lease = _session.Acquire("test");

        var first = await capture.SnapAsync(lease, CancellationToken.None);
        var second = await capture.SnapAsync(lease, CancellationToken.None);

        string stem = CameraCapture.Stem(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone));
        Assert.Equal(Path.Combine("camera_images", stem + ".jpg"), first.RelativePath);   // the sandbox's own spelling of a relative path
        Assert.Equal(Path.Combine("camera_images", stem + "-2.jpg"), second.RelativePath);
        Assert.Equal(Path.Combine(_dir, "camera_images", stem + ".jpg"), first.FullPath);
        Assert.Equal(first.FullPath, first.Image.Path);
        Assert.True(first.Image.Camera);
        Assert.Equal("Fake Cam", first.Device);
        Assert.Equal(first.Image.Bytes, await File.ReadAllBytesAsync(first.FullPath));
        Assert.Same(capture.Session, _session);
        capture.Discard(second);
        capture.Discard(null);
        Assert.False(File.Exists(second.FullPath));
        capture.Discard(second);   // gone already: only logged
        Assert.Equal("20260911-140530", CameraCapture.Stem(new DateTimeOffset(2026, 9, 11, 14, 5, 30, TimeSpan.Zero)));
    }

    [WindowsFact]
    public async Task ASnap_GoesToTheOutputFolderSetting_EvenComfysOrTheWorkingDirectoryItself()
    {
        var capture = Capture();
        using var lease = _session.Acquire("test");

        _settings.CameraOutputFolder = AppSettingsData.DefaultComfyOutputFolder;
        var shared = await capture.SnapAsync(lease, CancellationToken.None);
        _settings.CameraOutputFolder = " shots/today ";
        var nested = await capture.SnapAsync(lease, CancellationToken.None);
        _settings.CameraOutputFolder = "";
        var here = await capture.SnapAsync(lease, CancellationToken.None);

        Assert.Equal(Path.Combine(_dir, "comfy_images"), Path.GetDirectoryName(shared.FullPath));
        Assert.Equal(Path.Combine(_dir, "shots", "today"), Path.GetDirectoryName(nested.FullPath));
        Assert.Equal(_dir, Path.GetDirectoryName(here.FullPath));
        Assert.True(File.Exists(here.FullPath));
    }

    [Fact]
    public void TheOutputFolder_AndTheWatchFolderUnderIt_ReadTheSetting()
    {
        Assert.Equal("camera_images", new AppSettingsData().CameraOutputFolder);
        Assert.Equal("camera_images", CameraCapture.OutputFolder("camera_images"));
        Assert.Equal(".", CameraCapture.OutputFolder("  "));
        Assert.Equal(".", CameraCapture.OutputFolder(null));
        Assert.Equal("x.jpg", CameraCapture.Under(".", "x.jpg"));
        Assert.Equal("a/b/x.jpg", CameraCapture.Under("a/b/", "x.jpg"));
        Assert.Equal("camera_images/.watch", CameraWatch.FolderFor("camera_images"));
        Assert.Equal("comfy_images/.watch", CameraWatch.FolderFor("comfy_images"));
        Assert.Equal(".watch", CameraWatch.FolderFor(""));
    }

    [WindowsFact]
    public async Task ASnap_IsScaledToTheResolutionsLongerSide()
    {
        _system.Size = new CameraSize(1920, 1080);
        _settings.CameraResolution = "640x480";
        var capture = Capture();
        using var lease = _session.Acquire("test");

        var shot = await capture.SnapAsync(lease, CancellationToken.None);

        Assert.Equal((640, 360), (shot.Image.Width, shot.Image.Height));
    }

    [WindowsFact]
    public async Task ASaveTheSandboxRefuses_IsACameraFailure()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "camera_images"), "a file where the folder goes");
        var capture = Capture();
        using var lease = _session.Acquire("test");

        var error = await Assert.ThrowsAsync<CameraException>(() => capture.SnapAsync(lease, CancellationToken.None));

        Assert.StartsWith("The camera failed. (The photo could not be saved: Error:", error.Message);
    }

    [WindowsFact]
    public async Task TheTool_HasItsSchema_ReadsThePromptLeniently_AndAnswersEachOutcome()
    {
        var shot = await Capture().SnapAsync(_session.Acquire("test"), CancellationToken.None);
        string? asked = null;
        CameraAnswer next = new CameraAnswer.Shot(shot);
        var tool = new CameraCaptureTool((prompt, _) =>
        {
            asked = prompt;
            return Task.FromResult(next);
        });

        Assert.Equal("camera_capture", tool.Name);
        Assert.Contains("prompt", tool.JsonSchema.GetProperty("required")[0].GetString());
        Assert.Contains("do not ask again", tool.Description);
        var result = Assert.IsType<ToolImageResult>(await tool.InvokeAsync(new AIFunctionArguments { ["prompt"] = "Show me\nthe label." }));
        Assert.Equal("Show me the label.", asked);
        Assert.Equal(CameraText.Taken(shot), result.Text);
        Assert.Same(shot.Image, Assert.Single(result.Images));

        next = new CameraAnswer.Declined();
        Assert.Equal(CameraText.Declined, await tool.InvokeAsync(new AIFunctionArguments { ["reason"] = "why" }));
        Assert.Equal("why", asked);
        next = new CameraAnswer.Denied();
        Assert.Equal(CameraText.Denied, await tool.InvokeAsync(new AIFunctionArguments()));
        Assert.Equal(CameraText.DefaultPrompt, asked);
        next = new CameraAnswer.AlreadyDeclined();
        Assert.Equal(CameraText.AlreadyDeclined, await tool.InvokeAsync(new AIFunctionArguments()));
        next = new CameraAnswer.Failed("No camera is connected.");
        Assert.Equal("Error: No camera is connected.", await tool.InvokeAsync(new AIFunctionArguments()));
        next = new CameraAnswer.NoScreen();
        Assert.Equal(CameraText.NoScreen, await tool.InvokeAsync(new AIFunctionArguments()));
        Assert.Equal(CameraText.NoScreen, await new CameraCaptureTool(null).InvokeAsync(new AIFunctionArguments()));
        Assert.Contains("camera_capture", NeonSidekick.Plans.PlanTools.ReadOnly);
    }

    [Fact]
    public async Task TheList_NumbersTheCameras_MarksTheChosen_AndSaysWhatFailed()
    {
        var lines = await CameraCommand.ListAsync(_session, "", CancellationToken.None);
        Assert.Equal([CameraText.ListHeader, "  1. Fake Cam  ← chosen", "  2. Other Cam", CameraText.ListFirstNote], lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.False(l.Error));

        lines = await CameraCommand.ListAsync(_session, "Gone", CancellationToken.None);
        Assert.Equal(CameraText.FellBack("Gone", "Fake Cam"), lines[^1].Text);

        lines = await CameraCommand.ListAsync(_session, "Other Cam", CancellationToken.None);
        Assert.Equal("  2. Other Cam  ← chosen", lines[^1].Text);

        _system.ListFailure = new CameraException(CameraFailure.NoMediaFoundation, null);
        lines = await CameraCommand.ListAsync(_session, "", CancellationToken.None);
        Assert.True(Assert.Single(lines).Error);

        _system.ListFailure = null;
        _system.Devices.Clear();
        lines = await CameraCommand.ListAsync(_session, "", CancellationToken.None);
        Assert.Equal(CameraText.Failure(CameraFailure.NoCamera, null), Assert.Single(lines).Text);
    }

    [WindowsFact]
    public async Task Watch_KeepsTheFirstFrame_ThenOnlyAChange_AndStopsOnRevoke()
    {
        long frameNo = 0;
        bool hand = false;
        _system.Paint = (n, size) =>
        {
            Interlocked.Exchange(ref frameNo, n);
            var pixels = FakeCameraSystem.Solid(size, 90, 90, 90);
            if (Volatile.Read(ref hand))
            {
                Array.Fill(pixels, (byte)250, 0, pixels.Length / 2);
            }

            return pixels;
        };
        int changed = 0;
        using var watch = new CameraWatch(_session, _time, () => _settings.CameraWatchThreshold, () => Interlocked.Increment(ref changed));

        watch.Start(10);
        Assert.True(watch.Running);
        Assert.Equal(10, watch.Seconds);
        _time.Advance(TimeSpan.Zero);
        await CameraSessionTests.Until(() => watch.HasPending);
        Assert.Equal(1, changed);
        var first = watch.TakePending();
        Assert.True(first!.Value.Image.Camera);
        Assert.Equal(CameraWatch.PictureName(first.Value.At), first.Value.Image.Path);
        Assert.Equal("camera-watch-150210.jpg", CameraWatch.PictureName(new DateTimeOffset(2026, 10, 2, 15, 2, 10, TimeSpan.Zero)));
        Assert.True(ImageFile.IsImagePath(first.Value.Image.Path));
        Assert.Equal(-1, first.Value.Image.Path.IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.Null(watch.TakePending());

        await watch.SampleAsync();   // the same picture: nothing kept
        Assert.False(watch.HasPending);
        Volatile.Write(ref hand, true);
        await watch.SampleAsync();
        Assert.True(watch.HasPending);
        Assert.Equal(2, changed);

        Assert.Equal(1, _session.Revoke("watch"));
        Assert.False(watch.Running);
        Assert.False(watch.HasPending);
        Assert.False(watch.Stop());
        await watch.SampleAsync();   // stopped: nothing
        Assert.Equal(2, changed);
    }
}
