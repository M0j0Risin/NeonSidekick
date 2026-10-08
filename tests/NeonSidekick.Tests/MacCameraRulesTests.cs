using NeonSidekick.Camera;

namespace NeonSidekick.Tests;

/// <summary>
/// The Mac camera layer's decisions (2026-10-07, Stage 2: the camera on macOS), pure, so they run on every OS: the numbers are the
/// ones AVFoundation gave on the user's Mac (the Logitech StreamCam, the MacBook Pro Camera, an iPhone as Continuity Camera).
/// </summary>
public sealed class MacCameraRulesTests
{
    [Fact]
    public void FourCc_ReadsTheSubtypesCharacters_AndHexForTheUnprintable()
    {
        Assert.Equal("420v", MacCameraRules.FourCc(0x34323076));
        Assert.Equal("yuvs", MacCameraRules.FourCc(0x79757673));
        Assert.Equal("BGRA", MacCameraRules.FourCc(0x42475241));
        Assert.Equal("0x00000020", MacCameraRules.FourCc(0x20));
    }

    [Theory]
    [InlineData(30.00003000003, 30.00003000003, 30.00003000003)]   // the StreamCam: a range of one point
    [InlineData(15, 30, 30)]                                     // the built-in camera
    [InlineData(1, 60, 30)]                                      // the iPhone
    [InlineData(5, 5, 5)]                                        // the StreamCam's 2304x1296
    [InlineData(60.00024, 60.00024, 60.00024)]
    public void FpsIn_IsThirtyWhereTheRangeHoldsIt_ElseTheNearerEnd(double min, double max, double expected) =>
        Assert.Equal(expected, MacCameraRules.FpsIn(min, max), 6);

    [Theory]
    [InlineData(30.00003000003, 30.00003000003, FrameDuration.RangeShortest)]   // 1/30 here threw (the spike's crash)
    [InlineData(15, 30, FrameDuration.RangeShortest)]
    [InlineData(1, 60, FrameDuration.Thirtieth)]
    [InlineData(5, 5, FrameDuration.RangeShortest)]
    [InlineData(31, 60, FrameDuration.RangeLongest)]
    public void DurationFor_PassesTheRangesOwnEnds_AndAThirtiethOnlyInside(double min, double max, FrameDuration expected) =>
        Assert.Equal(expected, MacCameraRules.DurationFor(min, max));

    [Fact]
    public void WarmupFor_ApplesCamerasCountThirtyFrames_AUsbOneTheWebcams()
    {
        Assert.Equal(CameraWarmup.Apple, MacCameraRules.WarmupFor(MacCameraRules.BuiltInType, continuity: false));
        Assert.Equal(CameraWarmup.Apple, MacCameraRules.WarmupFor(MacCameraRules.ExternalType, continuity: true));   // an iPhone
        Assert.Equal(30, CameraWarmup.Apple.Frames);
        Assert.Equal(CameraWarmup.Webcam, MacCameraRules.WarmupFor(MacCameraRules.ExternalType, continuity: false));
        Assert.Equal(CameraWarmup.Webcam, MacCameraRules.WarmupFor(null, continuity: false));
        Assert.Equal([MacCameraRules.BuiltInType, MacCameraRules.ExternalType], MacCameraRules.DeviceTypes);
    }

    [Theory]
    [InlineData(0, CameraAccess.NotDetermined)]
    [InlineData(1, CameraAccess.Restricted)]
    [InlineData(2, CameraAccess.Denied)]
    [InlineData(3, CameraAccess.Authorized)]
    [InlineData(-1, CameraAccess.Unknown)]
    public void Access_ReadsTheAuthorizationStatus(long status, CameraAccess expected) => Assert.Equal(expected, MacCameraRules.Access(status));

    [Theory]
    [InlineData(-11814, CameraFailure.Unplugged)]
    [InlineData(-11815, CameraFailure.InUse)]
    [InlineData(-11817, CameraFailure.InUse)]
    [InlineData(-11852, CameraFailure.Blocked)]
    [InlineData(-11800, CameraFailure.Failed)]
    public void FailureOf_MapsAVFoundationsCodes(long code, CameraFailure expected) => Assert.Equal(expected, MacCameraRules.FailureOf(code));

    [Fact]
    public void Order_KeepsAVFoundationsOrder_WithTheSuspendedLast()
    {
        var streamCam = new CameraDevice("Logitech StreamCam", "0x200000046d0893");
        var builtIn = new CameraDevice("MacBook Pro Camera", "6C707041-05AC-0010-0001-000000000001");
        var iPhone = new CameraDevice("Christopher’s iPhone Camera", "048CDEB2-BFC0-4AA9-885F-099700000001");

        Assert.Equal([streamCam, iPhone, builtIn], MacCameraRules.Order([(builtIn, true), (streamCam, false), (iPhone, false)]));
        Assert.Equal([builtIn, streamCam, iPhone], MacCameraRules.Order([(builtIn, false), (streamCam, false), (iPhone, false)]));
        Assert.Empty(MacCameraRules.Order([]));
    }

    [Fact]
    public void TheMacsSentences_NameTheTerminal_AndTheSettingsPage()
    {
        Assert.Equal(
            "Camera access is off for iTerm2, so the camera cannot be used. Turn iTerm2 on in System Settings › Privacy & Security › Camera, then try again.",
            CameraText.MacBlocked("iTerm2"));
        Assert.Equal("macOS is asking whether Terminal may use the camera: answer its question, then try again.", CameraText.MacAsking("Terminal"));
        Assert.Equal(
            "MacBook Pro Camera is off while the MacBook's lid is closed: open the lid, or pick another camera (/camera use).",
            CameraText.Failure(CameraFailure.Suspended, "MacBook Pro Camera", mac: true));
        Assert.Contains("restricted on this Mac", CameraText.Failure(CameraFailure.Restricted, null, mac: true));
        Assert.Contains("macOS 14 or later", CameraText.Failure(CameraFailure.Unsupported, null, mac: true));
        Assert.Contains("Photo Booth", CameraText.Failure(CameraFailure.InUse, null, mac: true));
        Assert.Equal(CameraText.Failure(CameraFailure.NoCamera, null, mac: false), CameraText.Failure(CameraFailure.NoCamera, null, mac: true));
        Assert.DoesNotContain("Windows", CameraText.Failure(CameraFailure.Blocked, null, mac: true));
    }

    [MacFact]
    public void TheSmokesProbe_BindsAVFoundation_AndListsWithoutOpening()
    {
        // No LED: the probe lists and reads the permission, never opens a camera or asks (GitHub's runner lists none and passes).
        var check = App.SmokeChecks.ProbeCameraAvf();

        Assert.True(check.Passed, check.Detail);
        Assert.Equal("camera:avf", check.Name);
        Assert.Contains("32BGRA buffer", check.Detail);
    }
}
