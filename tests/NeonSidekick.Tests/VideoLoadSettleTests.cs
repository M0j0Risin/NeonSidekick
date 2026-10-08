using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="VideoLoadSettle"/> over the sequences measured in WebKit on 2026-10-07 (a second video loaded into the player): the
/// stopping report (paused, the new id, no duration) is dropped, everything after it passes, and an error after it is the answer.
/// </summary>
public sealed class VideoLoadSettleTests
{
    private const string Second = "eRsGyueVLvQ";
    private const string Missing = "aaaaaaaaaaa";

    private static string State(string id, int state, double position = 0, double duration = 0) =>
        $"{{\"ev\":\"state\",\"why\":\"change\",\"id\":\"{id}\",\"title\":null,\"state\":{state},\"position\":{position},\"duration\":{duration},\"volume\":100,\"muted\":false}}";

    private static List<VideoSnapshot> Run(VideoLoadSettle settle, VideoSnapshot start, params string[] messages)
    {
        var passed = new List<VideoSnapshot>();
        var current = start;
        foreach (string message in messages)
        {
            var (kind, next) = VideoMessages.Apply(current, message);
            if (settle.Accept(kind, next))
            {
                current = next;
                if (kind is VideoPageEvent.State or VideoPageEvent.Error)
                {
                    passed.Add(next);
                }
            }
        }

        return passed;
    }

    [Fact]
    public void ASecondVideo_TheStoppingReportIsDropped_ThePlayingOnePasses()
    {
        var settle = new VideoLoadSettle();
        settle.Loaded();

        var passed = Run(settle, VideoSnapshot.Opening(Second),
            State(Second, 2), State(Second, -1), State(Second, 3), State(Second, -1, 60, 888), State(Second, 3, 60, 888.061), State(Second, 1, 60.01, 888.061));

        Assert.DoesNotContain(passed, s => s.State == VideoState.Paused);
        Assert.Equal(VideoState.Playing, passed[^1].State);
        Assert.Equal(5, passed.Count);
    }

    [Fact]
    public void AMissingVideo_TheErrorIsTheFirstAnswerAPlayWouldTake()
    {
        var settle = new VideoLoadSettle();
        settle.Loaded();

        var passed = Run(settle, VideoSnapshot.Opening(Missing),
            State(Missing, 2), State(Missing, -1), State(Missing, 3), "{\"ev\":\"error\",\"code\":150,\"id\":\"aaaaaaaaaaa\"}");

        // youtube_play's test: the video's Playing, Paused, Cued or Ended, or its error.
        var answer = passed.First(s => s.State is VideoState.Playing or VideoState.Paused or VideoState.Cued or VideoState.Ended || s.Error is not null);
        Assert.Equal(150, answer.Error);
    }

    [Fact]
    public void AfterTheLoadSettles_ARealPause_Passes()
    {
        var settle = new VideoLoadSettle();
        settle.Loaded();
        var passed = Run(settle, VideoSnapshot.Opening(Second), State(Second, 1, 61, 888), State(Second, 2, 70, 888), State(Second, 2, 70, 0));

        Assert.Equal([VideoState.Playing, VideoState.Paused, VideoState.Paused], passed.Select(s => s.State));
    }

    [Fact]
    public void WithNoLoad_NothingIsDropped_AndOtherMessagesPass()
    {
        var settle = new VideoLoadSettle();

        Assert.True(settle.Accept(VideoPageEvent.State, VideoSnapshot.Opening(Second) with { State = VideoState.Paused }));
        settle.Loaded();
        Assert.True(settle.Accept(VideoPageEvent.Page, VideoSnapshot.Opening(Second)));
        Assert.True(settle.Accept(VideoPageEvent.Ready, VideoSnapshot.Opening(Second)));
        Assert.False(settle.Accept(VideoPageEvent.State, VideoSnapshot.Opening(Second) with { State = VideoState.Paused }));
        Assert.Throws<ArgumentNullException>(() => settle.Accept(VideoPageEvent.State, null!));
    }
}
