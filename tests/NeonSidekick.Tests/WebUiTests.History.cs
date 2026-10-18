using System.Net;
using NeonSidekick.App;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The sheet's hooks on the session (2026-10-16): a line the page sends is told once as it is accepted (queued, or bare <c>/prompts</c>), never
/// for a retry, a refusal, an empty line or a full queue; the memories and the history read through their hooks, a throwing read answering
/// empty; a change with no hook to take it is not found.
/// </summary>
public sealed partial class WebUiTests
{
    [Fact]
    public void ALineThePageSends_IsToldOnce_AsItIsAccepted()
    {
        var told = new List<string>();
        _session.Accepted = told.Add;
        _session.CommandSettings = () => PromptCommandsOn;

        _session.Send(new WebSendRequest { Id = "a", Text = "hello" }, IPAddress.Loopback);
        _session.Send(new WebSendRequest { Id = "a", Text = "hello" }, IPAddress.Loopback);   // the same id again: a retry
        _session.Send(new WebSendRequest { Id = "b", Text = "/prompts" }, IPAddress.Loopback);   // opens the sheet: accepted too
        _session.Send(new WebSendRequest { Id = "c", Text = "   " }, IPAddress.Loopback);
        _session.Send(new WebSendRequest { Id = "d", Text = "/settings" }, IPAddress.Loopback);   // refused
        _session.Send(new WebSendRequest { Id = "e", Text = new string('x', WebUiSession.MaxMessageBytes + 1) }, IPAddress.Loopback);

        Assert.Equal(["hello", "/prompts"], told);
    }

    [Fact]
    public void AFullQueue_TellsNothing()
    {
        var told = new List<string>();
        _session.Accepted = told.Add;
        for (int i = 0; i < WebUiSession.MaxWaiting; i++)
        {
            _session.Send(new WebSendRequest { Id = "m" + i, Text = "line " + i }, IPAddress.Loopback);
        }

        var full = _session.Send(new WebSendRequest { Id = "over", Text = "one too many" }, IPAddress.Loopback);

        Assert.Equal(WebOutcome.TooMany, full.Outcome);
        Assert.Equal(WebUiSession.MaxWaiting, told.Count);
        Assert.DoesNotContain("one too many", told);
    }

    [Fact]
    public void TheMemoriesAndTheHistory_ReadThroughTheirHooks_AThrowingReadAnswersEmpty()
    {
        Assert.Equal((0, false), (_session.Memories().Items.Count, _session.Memories().ModeOff));
        Assert.Empty(_session.History().Lines);

        _session.MemoriesNow = () => new WebMemories([new WebMemory("Likes tea.", true)], true);
        _session.HistoryNow = () => ["one", "two"];
        Assert.Equal([new WebMemory("Likes tea.", true)], _session.Memories().Items);
        Assert.True(_session.Memories().ModeOff);
        Assert.Equal(["one", "two"], _session.History().Lines);

        _session.MemoriesNow = () => throw new IOException("locked");
        Assert.Empty(_session.Memories().Items);
    }

    [Fact]
    public void TheSheetsChanges_ReachTheirHooks_AndWithNoneAreNotFound()
    {
        Assert.Equal(WebOutcome.NotFound, _session.SavePrompt(new WebTextBody { Text = "x" }, null).Outcome);
        Assert.Equal((WebOutcome.NotFound, WebUiText.PromptGone), (_session.DeletePrompt(new WebPromptDelete { Id = "p1" }, null).Outcome, _session.DeletePrompt(new WebPromptDelete { Id = "p1" }, null).Message));
        Assert.Equal(WebOutcome.NotFound, _session.SaveMemory(new WebTextBody { Text = "x" }, null).Outcome);
        Assert.Equal((WebOutcome.NotFound, WebUiText.MemoryGone), (_session.DeleteMemory(new WebTextBody { Text = "x" }, null).Outcome, _session.DeleteMemory(new WebTextBody { Text = "x" }, null).Message));
        Assert.Equal(WebOutcome.NotFound, _session.ClearHistory(null).Outcome);

        var calls = new List<string>();
        _session.PromptSave = (text, _) => { calls.Add("save " + text); return WebReply.Ok; };
        _session.PromptDelete = (id, _) => { calls.Add("delete " + id); return WebReply.Ok; };
        _session.MemorySave = (text, _) => { calls.Add("remember " + text); return WebReply.Ok; };
        _session.MemoryDelete = (text, _) => { calls.Add("forget " + text); return WebReply.Ok; };
        _session.HistoryClear = _ => { calls.Add("clear"); return WebReply.Ok; };

        _session.SavePrompt(new WebTextBody { Text = "Summarize." }, null);
        _session.SavePrompt(new WebTextBody(), null);   // no text: the hook answers it (an empty draft refused there)
        _session.DeletePrompt(new WebPromptDelete { Id = "p2" }, null);
        _session.DeletePrompt(new WebPromptDelete(), null);   // no id: nothing to delete
        _session.SaveMemory(new WebTextBody { Text = "Likes tea." }, null);
        _session.DeleteMemory(new WebTextBody { Text = "Likes tea." }, null);
        _session.DeleteMemory(new WebTextBody(), null);
        _session.ClearHistory(null);

        Assert.Equal(["save Summarize.", "save ", "delete p2", "remember Likes tea.", "forget Likes tea.", "clear"], calls);
    }

    [Fact]
    public void TheMemoryEditAndToggle_ReachTheirHooks_AndWithNoneOrNoMemoryAreNotFound()
    {
        // The 💾 tab's edit and off/on (2026-10-18): no hook or no memory named is NotFound as a delete is; an off/on saying neither is refused.
        Assert.Equal((WebOutcome.NotFound, WebUiText.MemoryGone), (_session.EditMemory(new WebMemoryEdit { Old = "x", Text = "y" }, null).Outcome, _session.EditMemory(new WebMemoryEdit { Old = "x", Text = "y" }, null).Message));
        Assert.Equal(WebOutcome.NotFound, _session.ToggleMemory(new WebMemoryToggle { Text = "x", Off = true }, null).Outcome);

        var calls = new List<string>();
        _session.MemoryEdit = (old, text, _) => { calls.Add("edit " + old + " > " + text); return WebReply.Ok; };
        _session.MemoryToggle = (text, off, _) => { calls.Add((off ? "off " : "on ") + text); return WebReply.Ok; };

        _session.EditMemory(new WebMemoryEdit { Old = "Likes tea.", Text = "Likes green tea." }, null);
        _session.EditMemory(new WebMemoryEdit { Old = "Likes tea." }, null);   // no text: the hook answers it (Empty refused there)
        Assert.Equal(WebOutcome.NotFound, _session.EditMemory(new WebMemoryEdit { Text = "y" }, null).Outcome);   // no memory named
        _session.ToggleMemory(new WebMemoryToggle { Text = "Likes tea.", Off = true }, null);
        _session.ToggleMemory(new WebMemoryToggle { Text = "Likes tea.", Off = false }, null);
        Assert.Equal((WebOutcome.Refused, WebUiText.MemoryToggleUnsaid), (_session.ToggleMemory(new WebMemoryToggle { Text = "Likes tea." }, null).Outcome, _session.ToggleMemory(new WebMemoryToggle { Text = "Likes tea." }, null).Message));
        Assert.Equal(WebOutcome.NotFound, _session.ToggleMemory(new WebMemoryToggle { Off = true }, null).Outcome);

        Assert.Equal(["edit Likes tea. > Likes green tea.", "edit Likes tea. > ", "off Likes tea.", "on Likes tea."], calls);
        Assert.All(
            new[] { WebUiText.Words.MemoryEdit, WebUiText.Words.MemoryEditSave, WebUiText.Words.MemoryEditCancel, WebUiText.Words.MemoryTurnOff, WebUiText.Words.MemoryTurnOn },
            word => Assert.False(string.IsNullOrEmpty(word)));
    }

    [Fact]
    public void TheSheetsWords_AreSentWithTheSnapshot()
    {
        Assert.Equal(WebUiText.PromptsTab, WebUiText.Words.PromptsTab);
        Assert.Equal(WebUiText.MemoryModeOff, WebUiText.Words.MemoryModeOff);
        Assert.Equal(WebUiText.ClearAllArmed, WebUiText.Words.ClearAllArmed);
        Assert.All(
            new[] { WebUiText.Words.MemoryTab, WebUiText.Words.HistoryTab, WebUiText.Words.MemoryNone, WebUiText.Words.HistoryNone, WebUiText.Words.SaveDraft, WebUiText.Words.DraftEmpty, WebUiText.Words.DeleteRow, WebUiText.Words.DeleteRowArmed, WebUiText.Words.ClearAll, WebUiText.Words.MemoryOff },
            word => Assert.False(string.IsNullOrEmpty(word)));
    }
}
