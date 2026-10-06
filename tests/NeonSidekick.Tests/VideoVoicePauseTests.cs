using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>YouTube while speaking</c> (2026-10-05, the YouTube plan's Phase 6): a playing video paused (or turned down) while the app
/// speaks or listens, set back after — only what this did, and never over another hand.
/// </summary>
public class VideoVoicePauseTests
{
    private const string Bunny = "aqz-KE-bpKQ";
    private readonly FakeVideoPlayer _player = new();
    private string _mode = YouTubeVoiceMode.Pause;

    private VideoVoicePause Pause() => new(_player, () => _mode);

    private void Playing(int volume = 100) => _player.Report(new VideoSnapshot(VideoState.Playing, Bunny, Volume: volume));

    [Fact]
    public void AHold_PausesAPlayingVideo_AndTheLastReleasePlaysItOn()
    {
        var pause = Pause();
        Playing();

        var speaking = pause.Hold();
        var listening = pause.Hold();   // overlapping: the second does nothing more
        Assert.Equal([VideoCommand.Pause], _player.Commands);

        speaking.Dispose();
        Assert.Equal([VideoCommand.Pause], _player.Commands);
        listening.Dispose();
        listening.Dispose();            // a second dispose is nothing
        Assert.Equal([VideoCommand.Pause, VideoCommand.Play], _player.Commands);
        Assert.Equal(VideoState.Playing, _player.Snapshot!.State);
    }

    [Fact]
    public void AVideoNotPlaying_OrNoneAtAll_IsLeftAlone()
    {
        var pause = Pause();
        pause.Hold().Dispose();                                                    // no window
        _player.Report(new VideoSnapshot(VideoState.Paused, Bunny));
        pause.Hold().Dispose();                                                    // the user's own pause

        Assert.Empty(_player.Commands);
        Assert.Equal(VideoState.Paused, _player.Snapshot!.State);
    }

    /// <summary>Resumed by someone else while held: not paused again, nor played twice.</summary>
    [Fact]
    public void AnotherHandWhileHeld_IsLeftAsItIs()
    {
        var pause = Pause();
        Playing();
        var hold = pause.Hold();
        _player.Report(_player.Snapshot! with { State = VideoState.Playing });   // the user pressed play over the reply

        hold.Dispose();

        Assert.Equal([VideoCommand.Pause], _player.Commands);
    }

    /// <summary>The page's first report after the pause still says playing (the harness): that is not another hand.</summary>
    [Fact]
    public void TheStaleReportAfterThePause_IsNotAnotherHand()
    {
        _player.Behave = false;
        var pause = Pause();
        Playing();
        var hold = pause.Hold();
        Playing();                                                                 // stale
        _player.Report(_player.Snapshot! with { State = VideoState.Paused });      // the pause shows

        hold.Dispose();

        Assert.Equal([VideoCommand.Pause, VideoCommand.Play], _player.Commands);
    }

    [Fact]
    public void Duck_TurnsItDown_AndBackToItsVolume()
    {
        _mode = YouTubeVoiceMode.Duck;
        var pause = Pause();
        Playing(volume: 60);

        var hold = pause.Hold();
        Assert.Equal(VideoVoicePause.DuckVolume, _player.Snapshot!.Volume);
        hold.Dispose();

        Assert.Equal([VideoCommand.Volume(VideoVoicePause.DuckVolume), VideoCommand.Volume(60)], _player.Commands);
    }

    [Fact]
    public void Duck_UnderTheDuckVolume_OrNone_DoesNothing()
    {
        _mode = YouTubeVoiceMode.Duck;
        var pause = Pause();
        Playing(volume: 10);
        pause.Hold().Dispose();
        _mode = YouTubeVoiceMode.None;
        Playing(volume: 80);
        pause.Hold().Dispose();

        Assert.Empty(_player.Commands);
    }

    [Fact]
    public async Task HoldUntil_ReleasesWhenTheAudioEnds()
    {
        var pause = Pause();
        Playing();
        var audio = new TaskCompletionSource();

        pause.HoldUntil(audio.Task);
        Assert.Equal(VideoState.Paused, _player.Snapshot!.State);
        audio.SetResult();
        await audio.Task;

        Assert.Equal(VideoState.Playing, _player.Snapshot!.State);
    }

    [Fact]
    public void TheMode_ReadsTheSavedWord_TheDefaultForAnythingElse()
    {
        Assert.Equal("pause", new AppSettingsData().YouTubeVoice);
        Assert.Equal("duck", YouTubeVoiceMode.Resolve(" DUCK "));
        Assert.Equal("pause", YouTubeVoiceMode.Resolve("loud"));
        Assert.Equal(["pause", "duck", "none"], YouTubeVoiceMode.Names);
        Assert.Contains("15%", YouTubeVoiceMode.Describe("duck"));
        Assert.Equal("pause ", SettingsMenu.YouTubeVoiceLabel("pause")[..6]);
        Assert.Equal("YouTube while speaking", SettingsMenu.FieldName(SettingsField.YouTubeVoice));
    }
}
