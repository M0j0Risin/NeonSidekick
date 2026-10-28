using System.Net;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Images;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A camera's still on the Web UI's Home tab, on the screen (2026-10-27, the ring-integration plan's step 5): a camera's sheet says so (an
/// unavailable one's doesn't); two pages asking at once share one request to Home Assistant; the still is stripped, never saved, never shown at
/// the PC, and each one served is logged; out of reach, not a camera or unavailable, nothing is asked; a profile switch under the wait is the sheet gone.
/// </summary>
public partial class ChatScreenTests
{
    private static List<string> CaptureLog(out Action stop)
    {
        var lines = new List<string>();
        Action<DiagnosticEvent> capture = e => { lock (lines) { lines.Add(e.Message); } };
        DiagnosticLog.Emitted += capture;
        stop = () => DiagnosticLog.Emitted -= capture;
        return lines;
    }

    [Fact]
    public async Task HomeTab_ACamerasStill_TwoAsksShareOneRequest_StrippedNeverSaved_AndEachOneServedIsLogged()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stub = CameraServer(async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return StubHttpMessageHandler.Bytes(HttpStatusCode.OK, ImageFixtures.ExifJpeg(), "image/jpeg");
        });
        WebHomeSheet? porch = null;
        WebHomeSheet? garage = null;
        WebCameraAnswer? first = null;
        WebCameraAnswer? second = null;
        var logs = CaptureLog(out var stop);
        try
        {
            var input = WhenIdle(
                i =>
                {
                    HomeSide.Read();
                    TickUntil(() => HomeSide.Sheet("entity:camera.porch") is { Camera: true });
                    porch = HomeSide.Sheet("entity:camera.porch");
                    garage = HomeSide.Sheet("entity:camera.garage");
                    var a = HomeSide.CameraAsync("entity:camera.porch", IPAddress.Loopback, CancellationToken.None);
                    var b = HomeSide.CameraAsync("entity:camera.porch", IPAddress.Loopback, CancellationToken.None);
                    TickUntil(() => Stills(stub) == 1);
                    release.SetResult();
                    first = a.GetAwaiter().GetResult();
                    second = b.GetAwaiter().GetResult();
                    PushLine(i, "/exit");
                });

            string output = await RunAsync(input);

            Assert.DoesNotContain("Porch Camera: a still", output);   // nothing at the PC
        }
        finally
        {
            stop();
        }

        Assert.True(porch!.Camera);
        Assert.False(garage!.Camera);   // unavailable: no still to ask for
        Assert.Equal(1, Stills(stub));
        var picture = Assert.IsType<WebCameraPicture>(first!.Picture);
        Assert.Equal("image/jpeg", picture.MediaType);
        Assert.Equal(MetadataKinds.None, MetadataStripper.Survey(picture.Bytes)!.Found);
        Assert.Equal(picture.Bytes, second!.Picture!.Bytes);
        Assert.False(Directory.Exists(HomeCameraFolder));
        lock (logs)
        {
            Assert.Equal(2, logs.Count(l => l == WebUiText.HomeCameraLogLine("Porch Camera", IPAddress.Loopback)));
        }
    }

    [Fact]
    public async Task HomeTab_ACamerasStill_OutOfReach_NotACamera_OrUnavailable_AsksNothing_AndLogsNothing()
    {
        var stub = CameraServer();
        var answers = new List<WebCameraAnswer>();
        var logs = CaptureLog(out var stop);
        try
        {
            var input = WhenIdle(
                i =>
                {
                    HomeSide.Read();
                    TickUntil(() => HomeSide.Sheet("entity:camera.porch") is { Camera: true });   // the feed's states in
                    foreach (string reference in new[] { "entity:camera.back", "entity:light.desk", "area:den", "entity:camera.garage" })
                    {
                        answers.Add(HomeSide.CameraAsync(reference, null, CancellationToken.None).GetAwaiter().GetResult());
                    }

                    PushLine(i, "/exit");
                });

            await RunAsync(input);
        }
        finally
        {
            stop();
        }

        Assert.All(answers, a => Assert.Null(a.Picture));
        Assert.All(answers[..3], a => Assert.Equal((WebOutcome.NotFound, WebUiText.HomeChanged), (a.Refusal!.Value.Outcome, a.Refusal.Value.Message)));
        Assert.Equal(WebOutcome.Refused, answers[3].Refusal!.Value.Outcome);
        Assert.Equal("Garage Camera is unavailable in Home Assistant, so it has no picture", answers[3].Refusal!.Value.Message);
        Assert.Equal(0, Stills(stub));
        lock (logs)
        {
            Assert.DoesNotContain(logs, l => l.StartsWith("Web UI showed a still", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task HomeTab_ACamerasFailure_IsHomeAssistantsReason_AndAProfileSwitchUnderTheWait_IsTheSheetGone()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hold = false;
        var stub = CameraServer(async (_, ct) =>
        {
            if (Volatile.Read(ref hold))
            {
                await release.Task.WaitAsync(ct);
            }

            return StubHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "500: Internal Server Error", "text/plain");
        });
        WorkProfile();
        WebCameraAnswer? failed = null;
        Task<WebCameraAnswer>? held = null;
        var input = WhenIdle(
            i =>
            {
                HomeSide.Read();
                TickUntil(() => HomeSide.Sheet("entity:camera.porch") is { Camera: true });
                failed = HomeSide.CameraAsync("entity:camera.porch", null, CancellationToken.None).GetAwaiter().GetResult();
                Volatile.Write(ref hold, true);
                held = HomeSide.CameraAsync("entity:camera.porch", null, CancellationToken.None);
                TickUntil(() => Stills(stub) == 2);
                PushLine(i, "/profile work");
            },
            i =>
            {
                TickUntil(() => held!.IsCompleted);
                release.TrySetResult();
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal((WebOutcome.Refused, HomeBarText.Failed("Porch Camera", HaText.CameraNoPicture)), (failed!.Refusal!.Value.Outcome, failed.Refusal.Value.Message));
        Assert.True(held!.IsCompletedSuccessfully);
        var late = await held;
        Assert.Equal((WebOutcome.NotFound, WebUiText.HomeChanged), (late.Refusal!.Value.Outcome, late.Refusal.Value.Message));
    }

    [Fact]
    public async Task HomeTab_ACamerasUnexpectedFailure_IsAFixedSentence_ItsOwnWordsOnlyInThePCsLog()
    {
        CameraServer((_, _) => throw new InvalidOperationException(@"C:\Users\someone\secret.txt"));
        WebCameraAnswer? answer = null;
        var logs = CaptureLog(out var stop);
        try
        {
            var input = WhenIdle(
                i =>
                {
                    HomeSide.Read();
                    TickUntil(() => HomeSide.Sheet("entity:camera.porch") is { Camera: true });
                    answer = HomeSide.CameraAsync("entity:camera.porch", null, CancellationToken.None).GetAwaiter().GetResult();
                    PushLine(i, "/exit");
                });

            await RunAsync(input);
        }
        finally
        {
            stop();
        }

        Assert.Equal((WebOutcome.Refused, WebUiText.HomeCameraFailed("Porch Camera")), (answer!.Refusal!.Value.Outcome, answer.Refusal.Value.Message));
        lock (logs)
        {
            Assert.Contains(logs, l => l.Contains("secret.txt", StringComparison.Ordinal));   // 2026-10-27, the review
        }
    }
}
