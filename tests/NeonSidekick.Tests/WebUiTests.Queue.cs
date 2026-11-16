using System.Net;
using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The 🗂️ sheet's Queue tab at the session (2026-11-16, plan lucid-roaming-lantern #42): each line keyed, the keyed list with the screen's
/// pended lines after the session's own, a removal by key from either of the session's lists (held or not) but never a chord's, the
/// version every change moves, the words, and the screen's hooks (NotFound without them).
/// </summary>
public sealed partial class WebUiTests
{
    [Fact]
    public void EachLine_GetsAKey_AndTheWaitingLines_ListTheScreensAfterTheSessions()
    {
        _session.HeldForPc = () => [new WebQueueLine(99, "/clear", false)];
        SendLine("one");
        Assert.Equal(WebOutcome.Ok, _session.QueueChord(App.BotChat.ResumeLine, null, null, null).Outcome);

        Assert.Equal([new WebQueueLine(1, "one", false), new WebQueueLine(2, App.BotChat.ResumeLine, true), new WebQueueLine(99, "/clear", false)], _session.WaitingLines);
        Assert.Equal(["one", App.BotChat.ResumeLine, "/clear"], _session.Waiting);
        Assert.Equal(2, _session.WaitingCount);   // the session's own, never the screen's
    }

    [Fact]
    public void ARemovalByKey_TakesTheLineOut_HeldOrNot_ButNeverAChordsLine()
    {
        _session.CancelHoldNow = () => true;
        SendLine("held");
        _session.HoldForCancel();
        SendLine("after");
        Assert.Equal(WebOutcome.Ok, _session.QueueChord(App.BotChat.ResumeLine, null, null, null).Outcome);
        long before = _session.Version;

        Assert.True(_session.TryRemoveLine(1, out var held));
        Assert.Equal("held", held.Text);
        Assert.Equal(0, _session.CancelHeldCount);
        Assert.True(_session.TryRemoveLine(2, out var after));
        Assert.Equal("after", after.Text);
        Assert.False(_session.TryRemoveLine(3, out _));   // the chord's
        Assert.False(_session.TryRemoveLine(1, out _));   // gone already
        Assert.False(_session.TryRemoveLine(0, out _));
        Assert.True(_session.Version > before);
        Assert.Equal([App.BotChat.ResumeLine], _session.Waiting);
    }

    [Fact]
    public void TheVersion_MovesWithEveryChange_AndNotWithAReadOrAFailedTake()
    {
        long v0 = _session.Version;
        Assert.False(_session.TryTakeLine(out _));
        _ = _session.WaitingLines;
        Assert.Equal(v0, _session.Version);

        SendLine("one");
        long v1 = _session.Version;
        Assert.True(v1 > v0);
        Assert.Equal("one", Take());
        long v2 = _session.Version;
        Assert.True(v2 > v1);
        SendLine("two");
        long v3 = _session.Version;
        Assert.Equal(1, _session.DropLines());
        Assert.True(_session.Version > v3);
        Assert.True(v3 > v2);
    }

    [Fact]
    public void ATakenBackLine_GoesToThePagesDraft_AsAWithdrawnOne()
    {
        using var subscription = _session.Events.Subscribe();
        _session.TakenBack("back to you");

        var events = Drain(subscription);
        Assert.Equal(["withdrawn:back to you"], events.Where(e => e.Name != "state").Select(e => e.Name + ":" + JsonSerializer.Deserialize(e.Json.Value, WebUiJsonContext.Default.WebTextBack)!.Text));
    }

    [Fact]
    public void TheQueueTabsActions_ReachTheirHooks_AndAreNotFoundWithoutThem()
    {
        Assert.Null(_session.Queue());
        Assert.Equal(WebOutcome.NotFound, _session.RemoveQueued(new WebQueueRemoveRequest(Key: 1), null).Outcome);
        Assert.Equal(WebOutcome.NotFound, _session.SendQueued(null).Outcome);
        Assert.Equal(WebOutcome.NotFound, _session.SetQueueMode(Queueing.Manual, null).Outcome);
        Assert.Equal(WebOutcome.NotFound, _session.SetQueueCancelMode(QueueCancel.Hold, null).Outcome);

        List<string> calls = [];
        var view = new WebQueueView([], [], "automatic", "empty", false, false, QueueMode.Names, QueueCancelMode.Names);
        _session.QueueNow = () => view;
        _session.QueueRemove = (request, _) => { calls.Add("remove " + request.Key); return WebReply.Ok; };
        _session.QueueSend = _ => { calls.Add("send"); return WebReply.Ok; };
        _session.QueueModeSet = (mode, _) => { calls.Add("mode " + mode); return WebReply.Ok; };
        _session.QueueCancelSet = (mode, _) => { calls.Add("cancel " + mode); return WebReply.Ok; };

        Assert.Same(view, _session.Queue());
        _session.RemoveQueued(new WebQueueRemoveRequest(Key: 4), IPAddress.Loopback);
        _session.SendQueued(null);
        _session.SetQueueMode(Queueing.Manual, null);
        _session.SetQueueCancelMode(QueueCancel.Hold, null);
        Assert.Equal(["remove 4", "send", "mode Manual", "cancel Hold"], calls);
    }

    [Theory]
    [InlineData(1L, null, null, true)]
    [InlineData(null, 0, "a", true)]
    [InlineData(null, null, null, false)]
    [InlineData(1L, 0, "a", false)]
    [InlineData(null, 0, null, false)]
    [InlineData(null, null, "a", false)]
    [InlineData(1L, 0, null, false)]
    public void ARemovesBody_NamesAPageLineOrAPcLine_NeverBothOrNeither(long? key, int? index, string? label, bool wellFormed) =>
        Assert.Equal(wellFormed, WebQueueRemoveRequest.WellFormed(new WebQueueRemoveRequest(index, label, key)));

    [Fact]
    public void TheQueueTabsWords_AreSentWithTheSnapshot()
    {
        Assert.Equal(WebUiText.ManageButton, WebUiText.Words.ManageButton);
        Assert.Equal(WebUiText.QueueTab, WebUiText.Words.QueueTab);
        Assert.All(
            new[]
            {
                WebUiText.Words.CloseManage, WebUiText.Words.SessionsTab, WebUiText.Words.FilesTab, WebUiText.Words.QueueNone, WebUiText.Words.QueueAtPc,
                WebUiText.Words.QueueFromPage, WebUiText.Words.QueueSendNow, WebUiText.Words.QueueModeLabel, WebUiText.Words.QueueCancelLabel,
                WebUiText.Words.RemoveRow, WebUiText.Words.RemoveRowArmed, WebUiText.Words.QueueChordLine,
            },
            word => Assert.False(string.IsNullOrEmpty(word)));
    }
}
