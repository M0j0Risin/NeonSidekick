using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A page's speaker as the speech path sees it (2026-11-13, the webui-voice plan): the voice cut into clips (0.4 s first, then 1 s, the rest
/// after 150 ms quiet), each announced to its page alone; what the page reports played is what the speaker no longer owes; a clear tells the
/// page to stop that turn; and a page that stops reporting (or fetching) gives up rather than holding the speaker forever, except while it
/// listens.
/// </summary>
public sealed class WebAudioPlaybackTests
{
    private const string Page = "page-0123456789abcdef";
    private const string Turn = "turn-0123456789abcdef";

    private readonly ManualTimeProvider _time = new();
    private readonly List<WebEvent> _events = [];

    private WebAudioPlayback Make()
    {
        var playback = new WebAudioPlayback(Page, Turn, _time, _events.Add);
        playback.Start();
        return playback;
    }

    private static byte[] Pcm(int bytes) => new byte[bytes];

    private List<WebVoiceClip> Clips() =>
        [.. _events.Where(e => e.Name == "voice-clip").Select(e => JsonSerializer.Deserialize(e.Json.Value, WebUiJsonContext.Default.WebVoiceClip)!)];

    [Fact]
    public void TheVoice_IsCutIntoAShortFirstClip_ThenSecondClips_AndTheRestAfterAQuietMoment()
    {
        using var playback = Make();

        playback.Write(Pcm(WebAudioPlayback.FirstClipBytes + WebAudioPlayback.ClipBytes + 1000), WebAudioPlayback.FirstClipBytes + WebAudioPlayback.ClipBytes + 1000);
        Assert.Equal([(0L, (long)WebAudioPlayback.FirstClipBytes), (1L, (long)WebAudioPlayback.ClipBytes)], Clips().Select(c => (c.N, c.Bytes)));

        _time.Advance(WebAudioPlayback.FlushIdle);

        var clips = Clips();
        Assert.Equal(3, clips.Count);
        Assert.Equal((2L, 1000L), (clips[2].N, clips[2].Bytes));
        Assert.All(clips, c => Assert.Equal(Turn, c.Turn));
        Assert.All(_events, e => Assert.Equal(Page, e.Page));   // to that page alone
    }

    [Fact]
    public void WhatThePageReportsPlayed_IsNoLongerOwed()
    {
        using var playback = Make();
        playback.Write(Pcm(WebAudioPlayback.FirstClipBytes), WebAudioPlayback.FirstClipBytes);
        Assert.NotNull(playback.Clip(0));
        Assert.Equal(WebAudioPlayback.FirstClipBytes, playback.BufferedBytes);

        Assert.Equal(WebPlayedResult.Taken, playback.Played(0, 4800));   // 0.2 s of 24 kHz samples
        Assert.Equal(WebAudioPlayback.FirstClipBytes - 9600, playback.BufferedBytes);
        Assert.Equal(WebPlayedResult.Taken, playback.Played(0, 2400));   // a late report moves nothing back
        Assert.Equal(WebAudioPlayback.FirstClipBytes - 9600, playback.BufferedBytes);

        Assert.Equal(WebPlayedResult.Taken, playback.Played(0, 1_000_000));   // held to the clip's length
        Assert.False(playback.Owed);
    }

    [Fact]
    public void AClipNeverMade_OrANegativeOffset_IsRefused()
    {
        using var playback = Make();
        playback.Write(Pcm(WebAudioPlayback.FirstClipBytes), WebAudioPlayback.FirstClipBytes);

        Assert.Equal(WebPlayedResult.Refused, playback.Played(1, 0));
        Assert.Equal(WebPlayedResult.Refused, playback.Played(0, -1));
        Assert.Null(playback.Clip(5));
    }

    [Fact]
    public void AClip_IsKeptUntilAReportPassesIt_SoAFailedFetchsRetryFindsItAgain()
    {
        using var playback = Make();
        playback.Write(Pcm(WebAudioPlayback.FirstClipBytes + WebAudioPlayback.ClipBytes), WebAudioPlayback.FirstClipBytes + WebAudioPlayback.ClipBytes);

        Assert.NotNull(playback.Clip(0));
        Assert.NotNull(playback.Clip(0));   // the GET that sent it never drops it
        playback.Played(1, 0);

        Assert.Null(playback.Clip(0));
        Assert.NotNull(playback.Clip(1));
    }

    [Fact]
    public void AClear_DropsEverything_TellsThePageToStopThatTurn_AndAnOldReportIsStale()
    {
        using var playback = Make();
        playback.Write(Pcm(WebAudioPlayback.FirstClipBytes), WebAudioPlayback.FirstClipBytes);

        playback.ClearBuffer();

        Assert.Equal(0, playback.BufferedBytes);
        var stop = Assert.Single(_events, e => e.Name == "voice-stop");
        Assert.Equal(Turn, JsonSerializer.Deserialize(stop.Json.Value, WebUiJsonContext.Default.WebVoiceStopped)!.Turn);
        Assert.Equal(WebPlayedResult.Stale, playback.Played(0, 100));

        // The next clip after a clear is a quick first one again.
        playback.Write(Pcm(WebAudioPlayback.FirstClipBytes), WebAudioPlayback.FirstClipBytes);
        Assert.Equal((1L, (long)WebAudioPlayback.FirstClipBytes), (Clips()[^1].N, Clips()[^1].Bytes));
    }

    [Fact]
    public void FiveSecondsOwed_WithNoReport_GivesUp_Once()
    {
        using var playback = Make();
        var gaveUp = new List<string>();
        playback.GaveUp = gaveUp.Add;
        playback.Write(Pcm(WebAudioPlayback.FirstClipBytes), WebAudioPlayback.FirstClipBytes);

        _time.Advance(WebAudioPlayback.SilentAfter - TimeSpan.FromMilliseconds(100));
        Assert.Empty(gaveUp);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        _time.Advance(WebAudioPlayback.SilentAfter);

        Assert.Single(gaveUp);
    }

    [Fact]
    public void AReport_PutsTheSilentRuleOff_AndAListenHoldsIt()
    {
        using var playback = Make();
        var gaveUp = new List<string>();
        playback.GaveUp = gaveUp.Add;
        playback.Write(Pcm(WebAudioPlayback.FirstClipBytes), WebAudioPlayback.FirstClipBytes);

        _time.Advance(TimeSpan.FromSeconds(4));
        playback.Played(0, 100);
        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.Empty(gaveUp);

        playback.Hold(true);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(gaveUp);

        playback.Hold(false);
        _time.Advance(WebAudioPlayback.SilentAfter + TimeSpan.FromMilliseconds(100));
        Assert.Single(gaveUp);
    }

    [Fact]
    public void MoreThanAMinuteNeverFetched_GivesUp()
    {
        using var playback = Make();
        var gaveUp = new List<string>();
        playback.GaveUp = gaveUp.Add;

        for (int i = 0; i < 62; i++)
        {
            playback.Write(Pcm(WebAudioPlayback.ClipBytes), WebAudioPlayback.ClipBytes);   // no time passes: the silent rule never fires
        }

        Assert.Single(gaveUp);
    }

    [Fact]
    public void ThePageGone_CountsEverythingPlayed_AndDropsLaterAudio()
    {
        using var playback = Make();
        playback.Write(Pcm(WebAudioPlayback.FirstClipBytes), WebAudioPlayback.FirstClipBytes);

        playback.Gone();
        playback.Write(Pcm(WebAudioPlayback.ClipBytes), WebAudioPlayback.ClipBytes);

        Assert.Equal(0, playback.BufferedBytes);
        Assert.Single(Clips());
        Assert.DoesNotContain(_events, e => e.Name == "voice-stop");
    }

    [Fact]
    public void Disposed_WithAudioOwed_TellsThePageToStop_AndOnlyOnce()
    {
        var playback = Make();
        playback.Write(Pcm(WebAudioPlayback.FirstClipBytes), WebAudioPlayback.FirstClipBytes);

        playback.Dispose();
        playback.Dispose();

        Assert.Single(_events, e => e.Name == "voice-stop");
    }

    [Fact]
    public void WritesBeforeTheStart_OrAfterTheStop_AreDropped()
    {
        using var playback = new WebAudioPlayback(Page, Turn, _time, _events.Add);
        playback.Write(Pcm(WebAudioPlayback.FirstClipBytes), WebAudioPlayback.FirstClipBytes);
        Assert.Empty(_events);

        playback.Start();
        playback.Stop();
        playback.Write(Pcm(WebAudioPlayback.FirstClipBytes), WebAudioPlayback.FirstClipBytes);
        Assert.Empty(_events);
    }

    [Fact]
    public void ThePublisherThrowing_NeverReachesTheSynthesizer()
    {
        using var playback = new WebAudioPlayback(Page, Turn, _time, _ => throw new InvalidOperationException("boom"));
        playback.Start();

        playback.Write(Pcm(WebAudioPlayback.FirstClipBytes), WebAudioPlayback.FirstClipBytes);

        Assert.Equal(WebAudioPlayback.FirstClipBytes, playback.BufferedBytes);
    }

    [Fact]
    public void TheSnapshot_NamesTheTurn_AndTheClipsMade()
    {
        using var playback = Make();
        playback.Write(Pcm(WebAudioPlayback.FirstClipBytes + WebAudioPlayback.ClipBytes), WebAudioPlayback.FirstClipBytes + WebAudioPlayback.ClipBytes);

        Assert.Equal(new WebSpeaking(Turn, 2), playback.Snapshot());
    }
}
