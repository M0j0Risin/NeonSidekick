using System.Net;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Docker;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// The Docker log window (2026-10-23, the user's ask: <c>/docker logs</c> tails the log in a window of its own): the window's
/// choice (<see cref="DockerLogWindow.Decide"/>), and its feed over a <see cref="FakeDockerEngine"/>: the first follow after an
/// inspect, stderr a warning, a stream's end checked against the engine before any marker (the same start: on again quietly; a
/// restart; a new container under the name; not running; gone), the events waited on by exact name, the engine's inclusive
/// <c>since</c> never repeating a line, a refusal ending it, a failure said once and tried again, the tight-loop guard, and the
/// loop's end with the window or the session. <c>ProcessWindowTests</c>' shape.
/// </summary>
public sealed class DockerLogWindowTests : IDisposable
{
    private const string Name = "mysql_dev";
    private const string Id = "2dc6d221ea8a008bfa4558be3d820e5e015273f0c3158005caefeaaca7f11177";
    private const string NewId = "8676a171815c4e2f9e0c1d2b3a4f5e6d7c8b9a0f1e2d3c4b5a6f7e8d9c0b1a2f";

    private readonly FakeDockerEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private DockerLogFeed? _feed;

    public void Dispose()
    {
        _feed?.Dispose();
        _engine.Dispose();
    }

    private DockerLogFeed Feed(int tail = 200)
    {
        _feed = new DockerLogFeed(new DockerLogSource(_engine.Session, Id, Name, tail), _time);
        return _feed;
    }

    private static byte[] Frame(byte stream, string text) => SmokeChecks.DockerFrame(stream, text);

    private static List<LogLine> Lines(DockerLogFeed feed)
    {
        var lines = new List<LogLine>();
        feed.CopySince(0, lines);
        return lines;
    }

    private static List<string> Texts(DockerLogFeed feed) => Lines(feed).Select(l => l.Text).ToList();

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    /// <summary>The clock moved a second at a time until <paramref name="condition"/> holds: a retry's wait waited out.</summary>
    private async Task AdvanceUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for " + what);
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }
    }

    // ── the window's choice ──────────────────────────────────────────────────

    [Fact]
    public void Decide_OpensRaisesOrSwaps()
    {
        var source = new DockerLogSource(_engine.Session, Id, Name, 200);
        Assert.Equal(DockerLogWindowMove.Open, DockerLogWindow.Decide(open: false, null, 0, refused: false, source));
        Assert.Equal(DockerLogWindowMove.Raise, DockerLogWindow.Decide(open: true, Name, 200, refused: false, source));
        Assert.Equal(DockerLogWindowMove.Swap, DockerLogWindow.Decide(open: true, "other", 200, refused: false, source));
        Assert.Equal(DockerLogWindowMove.Swap, DockerLogWindow.Decide(open: true, Name, 50, refused: false, source));
        Assert.Equal(DockerLogWindowMove.Swap, DockerLogWindow.Decide(open: true, Name, 200, refused: true, source));
        Assert.Equal(DockerLogWindowMove.Swap, DockerLogWindow.Decide(open: true, "MYSQL_DEV", 200, refused: false, source));   // names are ordinal
    }

    [Fact]
    public void TheEventsFilter_NamesTheContainer_EncodedAsJson()
    {
        Assert.Equal("""{"type":["container"],"container":["mysql_dev"],"event":["start","destroy"]}""", DockerLogFeed.EventsFilter(Name));
        Assert.Contains("a\\u0022b", DockerLogFeed.EventsFilter("a\"b"));
    }

    // ── the feed ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheFirstFollow_ComesAfterAnInspect_WithTheTail_AndStderrIsAWarning()
    {
        _engine.Set(Name, Id);
        var feed = Feed(tail: 30);

        var follow = await _engine.NextFollowAsync();
        Assert.Contains("/containers/" + Id + "/logs?follow=1&stdout=1&stderr=1&tail=30&timestamps=1", follow.PathAndQuery);
        follow.Body.Write([.. Frame(1, "2026-10-23T10:00:01.5Z ready\n"), .. Frame(2, "2026-10-23T10:00:02Z \u001b[33mlow disk\u001b[0m\n")]);
        await Until(() => Lines(feed).Count == 2, "two lines");

        var lines = Lines(feed);
        Assert.Equal(("ready", DiagnosticLevel.Info), (lines[0].Text, lines[0].Level));
        Assert.Equal(("low disk", DiagnosticLevel.Warning), (lines[1].Text, lines[1].Level));
        Assert.Equal(DockerLogState.Following, feed.State);
        Assert.Contains("Docker · mysql_dev — following", feed.Title(following: true));
        Assert.EndsWith(ProcessWindowText.PausedTail, feed.Title(following: false));
    }

    [Fact]
    public async Task AnEnd_WithTheSameStart_FollowsAgainQuietly_WithSince_AndNoLineTwice()
    {
        _engine.Set(Name, Id);
        var feed = Feed();
        var first = await _engine.NextFollowAsync();
        first.Body.Write([.. Frame(1, "2026-10-23T10:00:01.5Z one\n"), .. Frame(1, "2026-10-23T10:00:01.5Z one again\n")]);
        await Until(() => Lines(feed).Count == 2, "the first lines");

        first.Body.End();
        await _engine.NextEventsAsync();   // opened before the inspect
        var second = await _engine.NextFollowAsync();
        Assert.Contains("tail=all&since=1792749601.500000000&timestamps=1", second.PathAndQuery);
        // The engine's since is inclusive: both lines stamped at the boundary come again, then a new one.
        second.Body.Write([.. Frame(1, "2026-10-23T10:00:01.5Z one\n"), .. Frame(1, "2026-10-23T10:00:01.5Z one again\n"), .. Frame(1, "2026-10-23T10:00:03Z two\n")]);
        await Until(() => Texts(feed).Contains("two"), "the new line");

        Assert.Equal(["one", "one again", "two"], Texts(feed));   // no marker: the same start, so no restart
    }

    [Fact]
    public async Task AnEnd_WithANewerStart_IsARestart()
    {
        _engine.Set(Name, Id);
        var feed = Feed();
        var first = await _engine.NextFollowAsync();
        first.Body.Write(Frame(1, "2026-10-23T10:00:01Z before\n"));
        await Until(() => Lines(feed).Count == 1, "the first line");

        _engine.Set(Name, Id, startedAt: "2026-10-23T10:05:00.000000001Z");
        first.Body.End();
        var second = await _engine.NextFollowAsync();
        second.Body.Write(Frame(1, "2026-10-23T10:05:01Z after\n"));
        await Until(() => Texts(feed).Contains("after"), "the line after");

        Assert.Equal(["before", DockerLogWindowText.RestartedMarker("14:05:30"), "after"], Texts(feed));
        Assert.Contains("since=", second.PathAndQuery);
    }

    [Fact]
    public async Task AStoppedContainer_ShowsItsTail_ThenWaits_AndAStartEventFollowsOn()
    {
        _engine.Set(Name, Id, status: "exited");
        var feed = Feed();
        var first = await _engine.NextFollowAsync();
        first.Body.Write(Frame(1, "2026-10-23T09:00:00Z last words\n"));
        first.Body.End();
        var events = await _engine.NextEventsAsync();
        await Until(() => feed.State == DockerLogState.Waiting, "the wait");

        Assert.Equal(["last words", DockerLogWindowText.NotRunningMarker("14:05:30")], Texts(feed));
        Assert.Contains("waiting for it to start", feed.Title(following: true));
        Assert.Contains("filters=", events.PathAndQuery);

        // The engine's filter is a prefix match: a start of mysql_dev2 comes through and is not ours.
        events.Body.Write(FakeDockerEngine.Event("start", NewId, Name + "2"));
        await Task.Delay(200);
        Assert.False(_engine.FollowWaiting);

        _engine.Set(Name, Id);
        events.Body.Write(FakeDockerEngine.Event("start", Id, Name));
        var second = await _engine.NextFollowAsync();
        Assert.Contains("/containers/" + Id + "/logs", second.PathAndQuery);
        Assert.Contains("since=", second.PathAndQuery);
        await Until(() => feed.State == DockerLogState.Following, "following again");
    }

    [Fact]
    public async Task TheEnginesEventsEnding_WhileItWaits_OpensThemAgain_WithNoSecondMarker()
    {
        _engine.Set(Name, Id, status: "exited");
        var feed = Feed();
        (await _engine.NextFollowAsync()).Body.End();
        var events = await _engine.NextEventsAsync();
        await Until(() => feed.State == DockerLogState.Waiting, "the wait");

        events.Body.End();   // the engine closed its events (a restart of its own)
        await AdvanceUntil(EventsReopened, "the events again");
        await Task.Delay(200);   // the inspect after them, which must add nothing

        Assert.Equal([DockerLogWindowText.NotRunningMarker("14:05:30")], Texts(feed));
        Assert.False(_engine.FollowWaiting);   // no follow of a container still stopped
        Assert.Equal(DockerLogState.Waiting, feed.State);
    }

    private Task<FakeDockerEngine.Opened>? _reopened;

    private bool EventsReopened()
    {
        _reopened ??= _engine.NextEventsAsync();
        return _reopened.IsCompletedSuccessfully;
    }

    [Fact]
    public async Task AGoneContainer_Waits_AndANewOneUnderTheName_IsFollowedWithTheTail()
    {
        _engine.Set(Name, Id);
        var feed = Feed(tail: 40);
        var first = await _engine.NextFollowAsync();
        first.Body.Write(Frame(1, "2026-10-23T10:00:01Z old\n"));
        await Until(() => Lines(feed).Count == 1, "the first line");

        _engine.Remove(Name);
        first.Body.End();
        var events = await _engine.NextEventsAsync();
        await Until(() => feed.State == DockerLogState.Gone, "gone");
        Assert.Contains(DockerLogWindowText.GoneMarker("14:05:30", Name), Texts(feed));
        Assert.Contains("gone; waiting for a new one", feed.Title(following: true));

        _engine.Set(Name, NewId);
        events.Body.Write(FakeDockerEngine.Event("start", NewId, Name));
        var second = await _engine.NextFollowAsync();

        Assert.Contains("/containers/" + NewId + "/logs?follow=1&stdout=1&stderr=1&tail=40&timestamps=1", second.PathAndQuery);
        Assert.DoesNotContain("since=", second.PathAndQuery);
        Assert.Contains(DockerLogWindowText.NewContainerMarker("14:05:30", Name), Texts(feed));
    }

    [Fact]
    public async Task AnEnd_WhereTheInspectFindsANewId_FollowsItWithTheTail()
    {
        _engine.Set(Name, Id);
        var feed = Feed(tail: 25);
        var first = await _engine.NextFollowAsync();
        _engine.Set(Name, NewId);   // compose's down and up, quicker than the inspect
        first.Body.End();
        var second = await _engine.NextFollowAsync();

        Assert.Contains("/containers/" + NewId + "/logs?follow=1&stdout=1&stderr=1&tail=25&timestamps=1", second.PathAndQuery);
        Assert.Contains(DockerLogWindowText.NewContainerMarker("14:05:30", Name), Texts(feed));
    }

    [Fact]
    public async Task ARefusal_IsAWarningLine_AndTheEnd()
    {
        _engine.Set(Name, Id);
        _engine.LogsStatus = HttpStatusCode.NotImplemented;
        var feed = Feed();

        await feed.Loop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(feed.Refused);
        var warning = Assert.Single(Lines(feed));
        Assert.Equal(DiagnosticLevel.Warning, warning.Level);
        Assert.Contains("logging driver", warning.Text);
        Assert.Contains("can't be read", feed.Title(following: true));
    }

    [Fact]
    public async Task AnEngineDown_IsSaidOnce_ThenTriedAgain_UntilItAnswers()
    {
        _engine.Set(Name, Id);
        _engine.Down = true;
        var feed = Feed();
        await Until(() => feed.State == DockerLogState.WaitingForDocker, "waiting for Docker");
        int asked = _engine.Requests;
        await AdvanceUntil(() => _engine.Requests > asked + 1, "two more tries");
        Assert.Single(Lines(feed));   // said once a spell
        Assert.Contains("waiting for Docker", feed.Title(following: true));

        _engine.Down = false;
        _time.Advance(DockerLogFeed.RetryDelay);
        await AdvanceUntil(() => _engine.FollowWaiting, "the follow");
        await Until(() => feed.State == DockerLogState.Following, "following");
    }

    [Fact]
    public async Task TwoQuickEnds_WhileItRuns_WaitBeforeTheThirdFollow()
    {
        _engine.Set(Name, Id);
        Feed();
        (await _engine.NextFollowAsync()).Body.End();
        (await _engine.NextFollowAsync()).Body.End();
        await Task.Delay(300);
        Assert.False(_engine.FollowWaiting);   // the guard's wait

        await AdvanceUntil(() => _engine.FollowWaiting, "the third follow");
    }

    [Fact]
    public async Task ADisposedSession_EndsTheLoop_WithNoLine_AndNoNewClient()
    {
        _engine.Set(Name, Id);
        _engine.Down = true;
        var feed = Feed();
        await Until(() => feed.State == DockerLogState.WaitingForDocker, "waiting for Docker");
        _engine.Session.Dispose();
        int asked = _engine.Requests;

        await AdvanceUntil(() => feed.Loop.IsCompleted, "the loop's end");

        Assert.Equal(asked, _engine.Requests);
        Assert.Single(Lines(feed));
        Assert.False(_engine.Session.TryClient(out _));
    }

    [Fact]
    public async Task Dispose_StopsTheLoop_AndLetsTheFollowGo()
    {
        _engine.Set(Name, Id);
        var feed = Feed();
        var follow = await _engine.NextFollowAsync();

        feed.Dispose();
        await feed.Loop.WaitAsync(TimeSpan.FromSeconds(10));

        await Until(() => follow.Body.Disposed, "the follow let go");
    }
}
