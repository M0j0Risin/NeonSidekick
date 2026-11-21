using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The 📣 sheet's Queue tab (the 🗂️ sheet's until 2026-11-21) on the screen (2026-11-16, plan lucid-roaming-lantern #42): the view lists the PC's queue and the page's lines;
/// a ✕ takes a PC line out (a stale label refused) with the PC's notice, or a page line back to the page's draft; ➤ Send now sends the front
/// PC line from the idle line (refused when the queue sends itself); a mode set from the page is saved with the settings row's notice; the
/// page's own lines still go under manual; the queue tag moves with each change.
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task TheQueueTab_ListsThePcsLines_AndRemovesOne_ButNotAStaleLabel()
    {
        ManualQueueFixture();
        WebQueueView? before = null;
        WebReply stale = default, removed = default;
        string? tag = null;
        StepsWhenIdle(
            Line("first"),
            Line("second"),
            _ =>
            {
                before = Web.Queue();
                tag = Web.Snapshot().State!.QueueTag;
                stale = Web.RemoveQueued(new WebQueueRemoveRequest(0, "second"), null);
                removed = Web.RemoveQueued(new WebQueueRemoveRequest(0, "first"), null);
            },
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal([new WebQueuePcLine(0, "first", 0), new WebQueuePcLine(1, "second", 0)], before!.Pc);
        Assert.Equal(("manual", true), (before.Mode, before.SendOffered));
        Assert.Equal((WebOutcome.Conflict, WebUiText.LineGone), (stale.Outcome, stale.Message));
        Assert.Equal(WebOutcome.Ok, removed.Outcome);
        Assert.Equal(1, _running!.QueuedCount);
        Assert.NotEqual(tag, Web.Snapshot().State!.QueueTag);
        Assert.Contains(QueueMenu.RemovedNotice("first"), output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task TheQueueTabsSendNow_SendsTheFrontPcLine_FromTheIdleLine()
    {
        ManualQueueFixture();
        _chat.EnqueueText("First.");
        WebReply sent = default;
        StepsWhenIdle(
            Line("first"),
            Line("second"),
            _ => sent = Web.SendQueued(null),
            Line("/exit"));

        await RunAsync();

        Assert.Equal((WebOutcome.Ok, null), (sent.Outcome, sent.Message));
        Assert.Single(_chat.Requests);
        Assert.Equal("first", UserText(_chat.Requests[0]));
        Assert.Equal(1, _running!.QueuedCount);   // second waits for the next send
    }

    [Fact]
    public async Task TheQueueTabsSendNow_IsRefused_WhenNothingWaits_OrTheQueueSendsItself()
    {
        _settings.Update(d => d.TtsOutput = false);
        WebReply empty = default;
        WebQueueView? view = null;
        StepsWhenIdle(
            i =>
            {
                empty = Web.SendQueued(null);
                view = Web.Queue();
                PushLine(i, "/exit");   // a refusal wakes nothing
            });

        await RunAsync();

        Assert.Equal((WebOutcome.Conflict, QueueMenu.EmptyNotice), (empty.Outcome, empty.Message));
        Assert.False(view!.SendOffered);
        Assert.Equal(QueueMode.Names, view.Modes);
        Assert.Equal(QueueCancelMode.Names, view.CancelModes);
    }

    [Fact]
    public async Task AModeSetFromTheQueueTab_IsSaved_WithTheSettingsRowsNotice()
    {
        _settings.Update(d => d.TtsOutput = false);
        WebReply mode = default, cancel = default, same = default;
        StepsWhenIdle(
            _ =>
            {
                mode = Web.SetQueueMode(Queueing.Manual, null);
                cancel = Web.SetQueueCancelMode(QueueCancel.Drain, null);
                same = Web.SetQueueCancelMode(QueueCancel.Drain, null);
            },
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal((WebOutcome.Ok, WebOutcome.Ok, WebOutcome.Ok), (mode.Outcome, cancel.Outcome, same.Outcome));
        Assert.Equal(("manual", "drain"), (_settings.Current.QueueMode, _settings.Current.QueueCancelMode));
        Assert.Contains(SettingsMenu.SavedNotice(SettingsField.QueueMode, new AppSettingsData { QueueMode = "manual" }, _settings.ProfileDirectory), output);
        Assert.Contains(SettingsMenu.SavedNotice(SettingsField.QueueCancelMode, new AppSettingsData { QueueCancelMode = "drain" }, _settings.ProfileDirectory), output);
    }

    [Fact]
    public async Task APageLine_UnderManual_StillGoesFromTheIdleLine_AndATakenBackOne_GoesToTheDraft()
    {
        ManualQueueFixture();
        _chat.EnqueueText("Phone.");
        WebEventHub.Subscription? page = null;
        WebReply back = default;
        IReadOnlyList<WebQueueLine>? waiting = null;
        _chat.BeforeUpdate = (i, ct) =>
        {
            if (i == 0 && _chat.Requests.Count == 1)
            {
                // Under the reply: one more page line waits, and the page takes it back.
                Assert.Equal(WebOutcome.Ok, Web.Send(new WebSendRequest { Text = "never mind" }, null).Outcome);
                waiting = Web.Queue()!.Page;
                back = Web.RemoveQueued(new WebQueueRemoveRequest(Key: waiting.Single().Key), null);
            }

            return Task.CompletedTask;
        };
        StepsWhenIdle(
            _ =>
            {
                page = Web.Events.Subscribe();
                Assert.Equal(WebOutcome.Ok, Web.Send(new WebSendRequest { Text = "from the phone" }, null).Outcome);
            },
            Line("/exit"));

        await RunAsync();
        using var subscription = page!;

        Assert.Single(_chat.Requests);
        Assert.Equal("from the phone", UserText(_chat.Requests[0]));
        Assert.Equal("never mind", Assert.Single(waiting!).Text);
        Assert.Equal(WebOutcome.Ok, back.Outcome);
        Assert.Contains(("withdrawn", "never mind"), TextBacks(subscription));
        Assert.Empty(Web.Queue()!.Page);
    }

    [Fact]
    public async Task APcLineSentNow_WaitingAtTheScreen_IsListedAtThePc_WithNoRemove()
    {
        // 2026-11-16, the review's #114: a PC line sent now under a reply waits in the screen's queue; the tab lists it first, with no ✕.
        _settings.Update(d => d.TtsOutput = false);
        WebQueueView? view = null;
        SubmittedLine? taken = null;
        StepsWhenIdle(
            i =>
            {
                var screen = _running!;
                screen.PendForTests(new SubmittedLine("later", "later", [], "later"));
                view = Web.Queue();
                taken = screen.TakePendingLineForTests();   // so the idle line sends nothing
                PushLine(i, "/exit");
            });

        await RunAsync();

        Assert.Equal([new WebQueuePcLine(-1, "later", 0, Removable: false)], view!.Pc);
        Assert.Empty(view.Page);
        Assert.Equal("later", taken?.Draft);
    }

    /// <summary>
    /// A page line pended at the screen (as a command under a botchat is): listed with its key; the first claim owns it, so a removal that wins
    /// hands it back and the idle line's take skips it, and a take that wins leaves the removal a Conflict.
    /// </summary>
    [Fact]
    public async Task APageLinePendedAtTheScreen_IsListed_AndTheFirstClaimOwnsIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        WebEventHub.Subscription? page = null;
        IReadOnlyList<WebQueueLine>? listed = null;
        WebReply back = default, late = default;
        SubmittedLine? skipped = null, taken = null;
        StepsWhenIdle(
            i =>
            {
                var screen = _running!;
                page = Web.Events.Subscribe();
                screen.PendForTests(new SubmittedLine("/clear", "/clear", [], "/clear") { FromWeb = true, WebKey = 41 });
                listed = Web.Queue()!.Page;
                back = Web.RemoveQueued(new WebQueueRemoveRequest(Key: 41), null);   // the removal wins
                skipped = screen.TakePendingLineForTests();

                screen.PendForTests(new SubmittedLine("/new", "/new", [], "/new") { FromWeb = true, WebKey = 42 });
                taken = screen.TakePendingLineForTests();   // the take wins
                late = Web.RemoveQueued(new WebQueueRemoveRequest(Key: 42), null);
                PushLine(i, "/exit");
            });

        await RunAsync();

        Assert.Equal([new WebQueueLine(41, "/clear", false)], listed);
        Assert.Equal(WebOutcome.Ok, back.Outcome);
        Assert.Null(skipped);
        Assert.Equal("/new", taken?.Draft);
        Assert.Equal((WebOutcome.Conflict, WebUiText.LineGone), (late.Outcome, late.Message));
        using var subscription = page!;
        Assert.Equal([("withdrawn", "/clear")], TextBacks(subscription));
    }
}
