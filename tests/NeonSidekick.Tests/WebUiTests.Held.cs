using System.Net;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The page's lines under a hold after a cancel (2026-10-17, the user's question: under <i>Queue cancel mode</i> <c>hold</c> the page's next
/// message waited with nothing saying why): the lines waiting when the hold came are set apart, a line sent since goes first while it holds,
/// the held ones go once it lifts; a held line alone never wakes the idle line; the counts, the cap and a drop see both; Resume reaches its hook.
/// </summary>
public sealed partial class WebUiTests
{
    private void SendLine(string text) => Assert.Equal(WebOutcome.Ok, _session.Send(new WebSendRequest { Text = text }, null).Outcome);

    private string? Take() => _session.TryTakeLine(out var line) ? line.Text : null;

    [Fact]
    public void UnderAHold_TheLinesThatWaitedAreHeld_ANewOneGoesFirst_AndTheHeldFollowOnceItLifts()
    {
        bool holding = false;
        _session.CancelHoldNow = () => holding;
        SendLine("one");
        SendLine("two");

        holding = true;
        _session.HoldForCancel();
        Assert.Equal(2, _session.CancelHeldCount);
        Assert.Equal(2, _session.WaitingCount);
        Assert.Equal(["one", "two"], _session.Waiting);
        Assert.False(_session.LinesReady);   // held lines alone never wake the idle line
        Assert.Null(Take());

        SendLine("three");   // the page's next message
        Assert.True(_session.LinesReady);
        Assert.Equal(["one", "two", "three"], _session.Waiting);
        Assert.Equal("three", Take());
        Assert.Null(Take());

        holding = false;   // a reply ended on its own, or Resume
        Assert.True(_session.LinesReady);
        Assert.Equal("one", Take());
        Assert.Equal("two", Take());
        Assert.Null(Take());
        Assert.Equal(0, _session.WaitingCount);
    }

    [Fact]
    public void TheCapAndADrop_CountTheHeldLinesToo()
    {
        _session.CancelHoldNow = () => true;
        for (int i = 0; i < WebUiSession.MaxWaiting; i++)
        {
            SendLine("line " + i);
        }

        _session.HoldForCancel();
        Assert.Equal(WebOutcome.TooMany, _session.Send(new WebSendRequest { Text = "one more" }, null).Outcome);
        Assert.Equal(WebUiSession.MaxWaiting, _session.DropLines());
        Assert.Equal((0, 0), (_session.WaitingCount, _session.CancelHeldCount));
    }

    [Fact]
    public void Resume_ReachesItsHook_AndWithNoneIsNotFound()
    {
        Assert.Equal(WebOutcome.NotFound, _session.ResumeQueue(null).Outcome);
        IPAddress? seen = null;
        _session.QueueResume = remote => { seen = remote; return WebReply.Ok; };
        Assert.Equal(WebOutcome.Ok, _session.ResumeQueue(IPAddress.Loopback).Outcome);
        Assert.Equal(IPAddress.Loopback, seen);
        Assert.Equal(WebUiText.Resume, WebUiText.Words.Resume);
    }
}
