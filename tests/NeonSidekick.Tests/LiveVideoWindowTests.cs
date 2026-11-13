using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// Whether the real video window may play a file (2026-11-12, the video-files plan, gated as the Browser window's test is):
/// <c>NEONSIDEKICK_TEST_VIDEO_WINDOW</c> is <c>1</c>, on Windows with a WebView2 Runtime (a Mac's window plays no files). Skipped without, so
/// <c>build.ps1</c> and CI never open a window. The window shows for a few seconds, with the clip's sound.
/// </summary>
public sealed class VideoWindowFactAttribute : FactAttribute
{
    public const string Variable = "NEONSIDEKICK_TEST_VIDEO_WINDOW";

    public VideoWindowFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable)?.Trim() != "1")
        {
            Skip = $"{Variable} is not set (1 opens the video window for a few seconds).";
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            Skip = "The video window plays files on Windows only.";
            return;
        }

        try
        {
            if (WebViewNative.RuntimeVersion(out _) is null)
            {
                Skip = "No WebView2 Runtime is installed.";
            }
        }
        catch (DllNotFoundException)
        {
            Skip = "The WebView2 loader is not beside the tests.";
        }
    }
}

/// <summary>
/// The real video window on a fixture clip (2026-11-12): the file page plays it from its <c>file:</c> address under the play's token, a pause
/// and a seek reach it, and the close ends it. Its own home under the temp folder, so the user's window profile is never touched.
/// </summary>
public sealed class LiveVideoWindowTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [VideoWindowFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void AFile_PlaysInTheWindow_ItsPauseAndSeekReachIt_AndItCloses()
    {
        string home = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "video-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        string clip = Path.Combine(home, "clip.webm");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "video", "clip.webm"), clip);
        string? before = VideoWindow.Home;
        VideoWindow.Home = home;
        var player = VideoWindow.Player;
        try
        {
            Assert.True(player.PlaysFiles);
            var request = VideoRequest.File(clip);
            player.Play(request);

            Assert.True(SpinWait.SpinUntil(() => player.Snapshot is { State: VideoState.Playing or VideoState.Ended } s && s.FileToken == request.FileToken, TimeSpan.FromSeconds(30)),
                "the clip never played: " + player.Snapshot);
            output.WriteLine("playing: " + player.Snapshot);
            Assert.Equal("clip.webm", player.Snapshot!.FileName);
            Assert.True(player.Snapshot.Duration is > 2.5 and < 3.5, "its length: " + player.Snapshot.Duration);

            Assert.True(player.Send(new VideoCommand(VideoCommandKind.Pause)));
            Assert.True(SpinWait.SpinUntil(() => player.Snapshot?.State is VideoState.Paused or VideoState.Ended, TimeSpan.FromSeconds(10)), "no pause: " + player.Snapshot);
            Assert.True(player.Send(new VideoCommand(VideoCommandKind.Seek, 1)));
            Assert.True(SpinWait.SpinUntil(() => player.Snapshot?.Position is > 0.9 and < 1.5, TimeSpan.FromSeconds(10)), "no seek: " + player.Snapshot);
            output.WriteLine("sought: " + player.Snapshot);

            // To the YouTube page (cued, so no sound; offline its script fails, which is an answer too) and back to the file page.
            player.Play(new VideoRequest("aqz-KE-bpKQ", autoplay: false));
            Assert.True(SpinWait.SpinUntil(() => player.Snapshot is { IsFile: false, VideoId: "aqz-KE-bpKQ" } s && (s.State != VideoState.Opening || s.Failure is not null),
                TimeSpan.FromSeconds(30)), "the YouTube page never answered: " + player.Snapshot);
            output.WriteLine("youtube: " + player.Snapshot);
            var again = VideoRequest.File(clip);
            player.Play(again);
            Assert.True(SpinWait.SpinUntil(() => player.Snapshot is { State: VideoState.Playing or VideoState.Ended } s && s.FileToken == again.FileToken, TimeSpan.FromSeconds(30)),
                "the file never played again: " + player.Snapshot);
        }
        finally
        {
            player.Close();
            SpinWait.SpinUntil(() => player.Snapshot is null, TimeSpan.FromSeconds(15));
            VideoWindow.Home = before;
            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch (IOException)
            {
                // WebView2's processes still hold the profile a moment: the temp folder's own cleanup takes it
            }
        }
    }
}
