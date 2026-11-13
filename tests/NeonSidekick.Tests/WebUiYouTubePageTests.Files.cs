using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A video file on the YouTube tab in Edge (2026-11-12, the video-files plan), offline: the clip plays in the frame's own <c>&lt;video&gt;</c>
/// (YouTube's player hidden, its missing script never a failure for a file), its reports carry the token, the PC's pause reaches it, a close
/// empties it; a file gone and one with sound alone say so on the page and at the PC; a reload offers Resume, whose tap plays the file again.
/// </summary>
public sealed partial class WebUiYouTubePageTests
{
    // A fixture clip copied under a fresh folder (the test deletes what it plays).
    private static string FixtureCopy(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "video", name), path);
        return path;
    }

    private async Task<HeadlessPage?> OwnerPageAsync()
    {
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return null;
        }

        await page.WaitAsync("document.getElementById('hint').textContent.length > 0", "the tab's words");
        Assert.True(SpinWait.SpinUntil(() => Player.Owner is not null, TimeSpan.FromSeconds(10)), "the page never became the player");
        return page;
    }

    [WindowsFact]
    public async Task AFile_PlaysInTheTabsOwnVideo_Offline_DrivenFromThePc_AndAClose_EmptiesIt()
    {
        if (await OwnerPageAsync() is not { } page)
        {
            return;
        }

        string clip = FixtureCopy("clip.webm");
        var request = VideoRequest.File(clip);
        Player.Play(request);

        Assert.True(SpinWait.SpinUntil(() => Player.Snapshot is { State: VideoState.Playing } s && s.FileToken == request.FileToken, TimeSpan.FromSeconds(20)),
            "the file never reported playing: " + Player.Snapshot);
        Assert.False(await HiddenAsync(page, "#file"));
        Assert.True(await HiddenAsync(page, "#player"));
        Assert.False(await HiddenAsync(page, "#file-head"));
        Assert.Equal("clip.webm", await page.TextAsync("document.getElementById('file-name').textContent"));
        Assert.Equal(WebUiText.VideoWords.Files!.Tag, await page.TextAsync("document.getElementById('file-tag').textContent"));
        Assert.Contains(request.FileToken!, await page.TextAsync("document.getElementById('file').src"), StringComparison.Ordinal);

        // YouTube's script never loads offline (1.5 s here): no failure for a file.
        await Task.Delay(2000);
        Assert.NotEqual(VideoState.Failed, Player.Snapshot?.State);
        Assert.NotEqual(WebUiText.VideoWords.Failed, await page.TextAsync("document.getElementById('hint').textContent"));

        Assert.True(Player.Send(new VideoCommand(VideoCommandKind.Pause)));
        Assert.True(SpinWait.SpinUntil(() => Player.Snapshot?.State == VideoState.Paused, TimeSpan.FromSeconds(10)), "the pause never reached the file");

        Assert.True(Player.Close());
        await page.WaitAsync("!document.getElementById('file').getAttribute('src') && document.getElementById('file').classList.contains('hidden')", "the file's video emptied");
        Assert.Null(Player.FileOf(request.FileToken!));
    }

    /// <summary>
    /// Offline, a YouTube load left waiting for the API, then a file: the file plays and nothing replaces it. Its keys act (K pauses), but not
    /// typed into the saved filter; ⛶ fills the window with the file's video; a seek past the end ends it; a second file over the first reports
    /// nothing under the new token but its own.
    /// </summary>
    [WindowsFact]
    public async Task AFile_OverAWaitingYouTubeLoad_ItsKeysFillAndEnd_AndASecondFileOverIt()
    {
        if (await OwnerPageAsync() is not { } page)
        {
            return;
        }

        Player.Play(new VideoRequest("aqz-KE-bpKQ"));   // offline: held in the core's pending, never loaded
        await Task.Delay(300);
        var request = VideoRequest.File(FixtureCopy("clip.webm"));
        Player.Play(request);
        Assert.True(SpinWait.SpinUntil(() => Player.Snapshot is { State: VideoState.Playing } s && s.FileToken == request.FileToken, TimeSpan.FromSeconds(20)),
            "the file never played: " + Player.Snapshot);
        await page.EvalAsync("window.onYouTubeIframeAPIReady && window.onYouTubeIframeAPIReady()");   // the API arriving late loads nothing
        Assert.True(await HiddenAsync(page, "#player"));

        // K pauses the file; typed into the saved filter, it doesn't.
        await page.EvalAsync("document.getElementById('file').play()");
        await page.EvalAsync("window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', bubbles: true }))");
        await page.WaitAsync("document.getElementById('file').paused", "K paused the file");
        await page.EvalAsync("document.getElementById('file').play()");
        await page.WaitAsync("!document.getElementById('file').paused", "the file playing again");
        await page.EvalAsync("(() => { const f = document.getElementById('saved-filter'); f.classList.remove('hidden'); f.focus(); f.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', bubbles: true })); })()");
        Assert.False((await page.EvalAsync("document.getElementById('file').paused")).GetBoolean());

        // ⛶ Fill window takes the file's video, offline too.
        await page.WaitAsync("!document.getElementById('fill').classList.contains('hidden')", "the fill button");
        await page.EvalAsync("document.getElementById('fill').click()");
        await page.WaitAsync("document.body.classList.contains('yt-windowed') && document.getElementById('file').getBoundingClientRect().height > 100", "the file filling the window");

        // A seek past the end ends it.
        Assert.True(Player.Send(new VideoCommand(VideoCommandKind.Seek, 600)));
        Assert.True(SpinWait.SpinUntil(() => Player.Snapshot?.State == VideoState.Ended, TimeSpan.FromSeconds(10)), "no end: " + Player.Snapshot);

        // A second file: only its own reports, never the first's pause or emptying under its token.
        var second = VideoRequest.File(FixtureCopy("clip.mp4"));
        var seen = new List<VideoSnapshot?>();
        Player.Changed += s => { lock (seen) { seen.Add(s); } };
        Player.Play(second);
        Assert.True(SpinWait.SpinUntil(() => Player.Snapshot is { State: VideoState.Playing } s && s.FileToken == second.FileToken, TimeSpan.FromSeconds(20)),
            "the second file never played: " + Player.Snapshot);
        lock (seen)
        {
            Assert.All(seen.Where(s => s is not null), s => Assert.Equal(second.FileToken, s!.FileToken));
            Assert.DoesNotContain(seen, s => s is { State: VideoState.Ended or VideoState.Paused });
        }
    }

    [WindowsFact]
    public async Task AFileGone_AndOneWithSoundAlone_SaySo_OnThePageAndAtThePc()
    {
        if (await OwnerPageAsync() is not { } page)
        {
            return;
        }

        string gone = FixtureCopy("clip.mp4");
        File.Delete(gone);
        var request = VideoRequest.File(gone);
        Player.Play(request);
        Assert.True(SpinWait.SpinUntil(() => Player.Snapshot?.Error == VideoFile.GoneCode, TimeSpan.FromSeconds(20)), "no gone error: " + Player.Snapshot);
        await page.WaitAsync("document.getElementById('message').textContent === " + JsonSerializer.Serialize(WebUiText.VideoWords.Files!.Gone), "the gone file's words");

        var sound = VideoRequest.File(FixtureCopy("sound.mp4"));
        Player.Play(sound);
        Assert.True(SpinWait.SpinUntil(() => Player.Snapshot is { Error: VideoFile.NoPictureCode } s && s.FileToken == sound.FileToken, TimeSpan.FromSeconds(20)),
            "no no-picture error: " + Player.Snapshot);
        await page.WaitAsync("document.getElementById('message').textContent === " + JsonSerializer.Serialize(WebUiText.VideoWords.Files!.NoPicture), "the sound-only file's words");
    }

    [WindowsFact]
    public async Task AReload_OffersTheFile_AndResumePlaysItAgain()
    {
        // YouTube's script given a minute (as AReload_KeepsThePagesId_AndShowsResume_NotPlayHere): a file never waits on it anyway.
        Player.Dispose();
        _backend.VideoPlayer = new WebVideoPlayer(apiWaitMs: 60_000);
        if (await OwnerPageAsync() is not { } page)
        {
            return;
        }

        var request = VideoRequest.File(FixtureCopy("clip.mp4"));
        Player.Play(request);
        Assert.True(SpinWait.SpinUntil(() => Player.Snapshot is { State: VideoState.Playing or VideoState.Paused or VideoState.Ended } s && s.FileToken == request.FileToken,
            TimeSpan.FromSeconds(20)), "the MP4 never played: " + Player.Snapshot);
        Player.Send(new VideoCommand(VideoCommandKind.Pause));
        Assert.True(SpinWait.SpinUntil(() => Player.Snapshot?.State == VideoState.Paused, TimeSpan.FromSeconds(10)));

        // The fixture is 3 s, which the saved videos' rule counts as seen to its end: the page's report of a longer file's place, as it would send.
        Player.Report(Player.Owner!, "{\"ev\":\"state\",\"file\":\"" + request.FileToken + "\",\"title\":\"clip.mp4\",\"state\":2,\"position\":200,\"duration\":600,\"volume\":100,\"muted\":false}", null);
        Assert.Equal(200, Player.Snapshot?.Position);

        await page.EvalAsync("source.close()");
        SpinWait.SpinUntil(() => Player.Streams == 0, TimeSpan.FromSeconds(10));
        await page.GoAsync(Root + "youtube");
        await page.WaitAsync("!document.getElementById('resume').classList.contains('hidden')", "the Resume button", 20);
        Assert.Equal("Resume \"clip.mp4\" at 3:17", await page.TextAsync("document.getElementById('resume').textContent"));
        Assert.NotNull(Player.FileOf(request.FileToken!));

        await page.EvalAsync("document.getElementById('resume').click()");
        // Resumed at 3:17 of a 3 s clip: held to its end, so it reports ended or paused there; reported under its token either way.
        Assert.True(SpinWait.SpinUntil(() => Player.Snapshot is { State: not VideoState.Opening } s && s.FileToken == request.FileToken, TimeSpan.FromSeconds(20)),
            "the resumed file never reported: " + Player.Snapshot);
        Assert.False(await HiddenAsync(page, "#file"));
    }
}
