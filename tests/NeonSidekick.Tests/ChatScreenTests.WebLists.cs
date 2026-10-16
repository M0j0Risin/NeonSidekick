using System.Net;
using NeonSidekick.App;
using NeonSidekick.Memory;
using NeonSidekick.Prompts;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The page's sheet on the screen (2026-10-16, the user's ask): the draft saved as a prompt and a prompt deleted by its id; memories saved
/// (refused while Memory mode is off) and deleted by their text; the message history shared with the PC — a page's line in it as the page
/// sends it, the PC's set-aside drafts and paste tokens out of the page's copy — cleared from the 🕘 tab, and <c>/msgclear</c> from the page.
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task ThePagesPrompts_AreSavedFromTheDraft_AndDeletedByTheirId()
    {
        _settings.Update(d => d.TtsOutput = false);
        var replies = new List<WebReply>();
        WebPrompts? sheet = null;
        var input = WebSteps(
            i =>
            {
                replies.Add(Web.SavePrompt(new WebTextBody { Text = "Summarize this." }, IPAddress.Loopback));
                replies.Add(Web.SavePrompt(new WebTextBody { Text = "Summarize this." }, IPAddress.Loopback));   // already saved
                replies.Add(Web.SavePrompt(new WebTextBody { Text = "   " }, IPAddress.Loopback));
                replies.Add(Web.SavePrompt(new WebTextBody { Text = "Review the diff" }, IPAddress.Loopback));
                sheet = Web.Prompts();
                replies.Add(Web.DeletePrompt(new WebPromptDelete { Id = sheet.Items[0].Id }, IPAddress.Loopback));
                replies.Add(Web.DeletePrompt(new WebPromptDelete { Id = sheet.Items[0].Id }, IPAddress.Loopback));   // gone
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal(
            [WebOutcome.Ok, WebOutcome.Ok, WebOutcome.Refused, WebOutcome.Ok, WebOutcome.Ok, WebOutcome.NotFound],
            replies.Select(r => r.Outcome));
        var saved = new SavedPrompts(_settings.ProfileDirectory, _time).List();
        Assert.Equal(["Review the diff"], saved.Select(p => p.Text));
        Assert.Equal(SavedPromptText.Removed(new SavedPrompt(sheet!.Items[0].Id, sheet.Items[0].Text, default)), replies[4].Message);
        Assert.Equal(WebUiText.PromptGone, replies[5].Message);
    }

    [Fact]
    public async Task ThePagesMemories_AreSavedAsRememberSavesThem_AndDeletedInAnyMode()
    {
        _settings.Update(d => { d.TtsOutput = false; d.MemoryMode = "read-write"; });
        var replies = new List<WebReply>();
        WebMemories? on = null;
        WebMemories? off = null;
        var input = WebSteps(
            i =>
            {
                replies.Add(Web.SaveMemory(new WebTextBody { Text = "Likes tea." }, IPAddress.Loopback));
                replies.Add(Web.SaveMemory(new WebTextBody { Text = "likes tea." }, IPAddress.Loopback));   // the same, any case
                replies.Add(Web.SaveMemory(new WebTextBody { Text = "  " }, IPAddress.Loopback));
                replies.Add(Web.SaveMemory(new WebTextBody { Text = "Uses Obsidian." }, IPAddress.Loopback));
                on = Web.Memories();
                _settings.Update(d => d.MemoryMode = "off");
                replies.Add(Web.SaveMemory(new WebTextBody { Text = "Refused while off." }, IPAddress.Loopback));
                off = Web.Memories();
                replies.Add(Web.DeleteMemory(new WebTextBody { Text = "Likes tea." }, IPAddress.Loopback));   // deletes work in any mode
                replies.Add(Web.DeleteMemory(new WebTextBody { Text = "Likes tea." }, IPAddress.Loopback));
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal(
            [(WebOutcome.Ok, ChatScreen.RememberedNotice("Likes tea.")), (WebOutcome.Ok, ChatScreen.AlreadyRememberedNotice("Likes tea.")),
             (WebOutcome.Refused, WebUiText.DraftEmpty), (WebOutcome.Ok, ChatScreen.RememberedNotice("Uses Obsidian.")),
             (WebOutcome.Refused, ChatScreen.MemoryOffNotice), (WebOutcome.Ok, MemoryMenu.RemovedNotice("Likes tea.")),
             (WebOutcome.NotFound, WebUiText.MemoryGone)],
            replies.Select(r => (r.Outcome, r.Message)));
        Assert.Equal([new WebMemory("Likes tea.", false), new WebMemory("Uses Obsidian.", false)], on!.Items);
        Assert.False(on.ModeOff);
        Assert.True(off!.ModeOff);
        Assert.Equal(["Uses Obsidian."], new MemoryStore(_settings.ProfileDirectory).Snapshot());
    }

    [Fact]
    public async Task ATurnedOffMemory_IsListedSo_AndSavingItAgainTurnsItOn()
    {
        _settings.Update(d => { d.TtsOutput = false; d.MemoryMode = "read-write"; });
        var store = new MemoryStore(_settings.ProfileDirectory);
        store.Add("Likes tea.");
        store.SetDisabled("Likes tea.", true);
        WebMemories? before = null;
        WebReply again = default;
        var input = WebSteps(
            i =>
            {
                before = Web.Memories();
                again = Web.SaveMemory(new WebTextBody { Text = "Likes tea." }, IPAddress.Loopback);
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal([new WebMemory("Likes tea.", true)], before!.Items);
        Assert.Equal((WebOutcome.Ok, ChatScreen.RememberEnabledNotice("Likes tea.")), (again.Outcome, again.Message));
        Assert.Equal(["Likes tea."], new MemoryStore(_settings.ProfileDirectory).Snapshot());
    }

    [Fact]
    public async Task APagesLine_JoinsTheHistoryAndTheStore_AsThePageSendsIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("Hello.");
        IReadOnlyList<string>? seen = null;
        var input = WebSteps(
            _ => SendFromPage("from the phone"),
            i =>
            {
                seen = Web.History().Lines;
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal(["from the phone"], seen);
        Assert.Equal(["from the phone", "/exit"], StoredHistory(_settings.ProfileDirectory));
    }

    [Fact]
    public async Task APagesLine_WithKeepMessageHistoryOff_JoinsTheLineAlone()
    {
        _settings.Update(d => { d.TtsOutput = false; d.KeepMessageHistory = false; });
        _chat.EnqueueText("Hello.");
        var input = WebSteps(_ => SendFromPage("from the phone"), i => PushLine(i, "/exit"));

        await RunAsync(input);

        Assert.Equal(["from the phone", "/exit"], _running!.InputHistoryForTests);
        Assert.Empty(StoredHistory(_settings.ProfileDirectory));
    }

    /// <summary>The page's copy leaves out what never leaves the PC: a draft ESC set aside (a password in the wrong box) and a paste's token.</summary>
    [Fact]
    public async Task ThePagesHistory_LeavesOutThePcsSetAsideDrafts_AndPasteTokens()
    {
        _settings.Update(d => d.TtsOutput = false);
        IReadOnlyList<string>? seen = null;
        var input = WebSteps(
            i => i.Push([.. "sk-secret".Select(Keys.Char), Keys.Escape]),
            i => PushLine(i, "/help"),
            i =>
            {
                Assert.True(PasteBlocks.IsToken('\uE000'));
                _running!.RememberForTests("a line with \uE000");
                seen = Web.History().Lines;
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal(["/help"], seen);
        Assert.Contains("sk-secret", _running!.InputHistoryForTests);   // ↑ at the PC still finds it
    }

    [Fact]
    public async Task ClearAll_FromThePage_WipesTheStoreAndTheLine_AndSaysSoAtThePc()
    {
        _settings.Update(d => d.TtsOutput = false);
        SeedHistory(_settings.ProfileDirectory, "old", "older");
        WebReply cleared = default;
        WebReply nothing = default;
        var input = WebSteps(
            i => PushLine(i, "/help"),
            _ =>
            {
                cleared = Web.ClearHistory(IPAddress.Loopback);
                nothing = Web.ClearHistory(IPAddress.Loopback);
            },
            i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Equal((WebOutcome.Ok, ChatScreen.MsgClearedNotice(3)), (cleared.Outcome, cleared.Message));   // old, older, /help
        Assert.Equal((WebOutcome.Ok, ChatScreen.MsgClearNothingNotice), (nothing.Outcome, nothing.Message));
        Assert.Contains(ChatScreen.MsgClearedNotice(3), output);   // the PC's own line, queued for its task
        Assert.Equal(["/exit"], StoredHistory(_settings.ProfileDirectory));
        Assert.Equal(["/exit"], _running!.InputHistoryForTests);
    }

    [Theory]
    [InlineData("yes", 0)]
    [InlineData("no", 2)]
    public async Task MsgClear_FromThePage_AsksOnThePage_AndItsYesClears(string answer, int left)
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        SeedHistory(_settings.ProfileDirectory, "one");
        int count = -1;
        var input = WebSteps(
            answer,
            _ => SendFromPage("/msgclear"),
            i =>
            {
                count = _running!.InputHistoryForTests.Count;
                PushLine(i, "/exit");
            });

        string output = await RunAsync(input);

        Assert.Equal(left, count);   // yes: gone; no: "one" and the page's "/msgclear"
        Assert.Contains(answer == "yes" ? ChatScreen.MsgClearedNotice(2) : ChatScreen.KeptNotice, output);
        Assert.Empty(Web.Asks.OpenAsks());
    }

    [Fact]
    public async Task MsgClear_FromThePage_IsRefused_WithWebUiCommandsOff()
    {
        _settings.Update(d => d.TtsOutput = false);
        WebReply reply = default;
        var input = WebSteps(
            i =>
            {
                reply = Web.Send(new WebSendRequest { Text = "/msgclear" }, IPAddress.Loopback);
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal((WebOutcome.Refused, WebUiText.CommandsOff), (reply.Outcome, reply.Message));
    }

    [Fact]
    public void RememberAnswer_SaysWhatRememberSays_ForEveryOutcome()
    {
        Assert.Equal((ChatScreen.RememberedNotice("a"), false), ChatScreen.RememberAnswer(new MemoryAddResult(MemoryAddOutcome.Added, "a")));
        Assert.Equal((ChatScreen.RememberEnabledNotice("a"), false), ChatScreen.RememberAnswer(new MemoryAddResult(MemoryAddOutcome.Enabled, "a")));
        Assert.Equal((ChatScreen.AlreadyRememberedNotice("a"), false), ChatScreen.RememberAnswer(new MemoryAddResult(MemoryAddOutcome.Duplicate, "a")));
        Assert.Equal((ChatScreen.MemoryFullError, true), ChatScreen.RememberAnswer(new MemoryAddResult(MemoryAddOutcome.Full, "a")));
        Assert.Equal((ChatScreen.RememberUsageError, true), ChatScreen.RememberAnswer(new MemoryAddResult(MemoryAddOutcome.Empty, "")));
        Assert.Equal((ChatScreen.MemoryFailedError, true), ChatScreen.RememberAnswer(new MemoryAddResult(MemoryAddOutcome.Failed, "a")));
    }
}
