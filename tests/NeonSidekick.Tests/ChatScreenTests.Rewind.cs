using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/rewind</c> and the double ESC on the screen (2026-09-30).</summary>
public partial class ChatScreenTests
{
    private static List<string> UserLines(IReadOnlyList<ChatMessage> request) =>
        request.Where(ConversationHistory.IsTurnStart).Select(m => m.Text).ToList();

    /// <summary>
    /// Three turns; <c>/rewind</c>, Up to the second, Enter, yes. The history and the stored session go back to the first turn,
    /// the second's text is on the row (Enter alone sends it again), and the notice names the tool whose change stays.
    /// </summary>
    [Fact]
    public async Task Rewind_PickedTurn_LeavesWithWhatFollows_AndItsTextComesBackToTheRow()
    {
        _settings.Update(d => d.TtsOutput = false);
        _geometry = new ScreenGeometry(() => null);
        _chat.EnqueueText("One.")
            .Enqueue(FakeChatClient.Call("c1", "write_file", new Dictionary<string, object?> { ["path"] = "note.txt", ["content"] = "hello" }))
            .EnqueueText("Two.")
            .EnqueueText("Three.")
            .EnqueueText("Two again.");
        StepsWhenIdle(
            Line("one"), Line("two"), Line("three"),
            Line("/rewind"), Key(Keys.Up), Key(Keys.Enter), Key(Keys.Char('y')), Key(Keys.Enter),
            Key(Keys.Enter),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(Titled(RewindText.Title), output);
        Assert.Contains("· " + RewindText.RewoundNotice(2, "two"), output);
        Assert.Contains(RewindText.ChangesStayWarning(["write_file"]), output);
        Assert.Equal(5, _chat.Requests.Count);
        Assert.Equal(["one", "two"], UserLines(_chat.Requests[4]));   // the re-sent line follows the first turn alone
        Assert.DoesNotContain(_chat.Requests[4], m => m.Contents.OfType<FunctionCallContent>().Any(c => c.Name == "write_file"));
        using var store = OpenSessions();
        var record = store.Load(Assert.Single(store.List(0)).Id)!;
        Assert.Equal(["one", "two"], record.Turns.Select(t => t.UserText));
        Assert.Equal([1, 2], record.Turns.Select(t => t.Ordinal));
        Assert.Equal(2, SessionHistory.FromJson(record.HistoryJson).Count(ConversationHistory.IsTurnStart));
    }

    /// <summary>No at the yes/no goes back to the list; ESC there closes it; nothing changed.</summary>
    [Fact]
    public async Task Rewind_NoThenEsc_ChangesNothing()
    {
        _settings.Update(d => d.TtsOutput = false);
        _geometry = new ScreenGeometry(() => null);
        _chat.EnqueueText("One.").EnqueueText("Two.");
        StepsWhenIdle(Line("one"), Line("/rewind"), Key(Keys.Enter), Key(Keys.Enter), Key(Keys.Escape), Line("two"), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(Titled(RewindText.ConfirmPrompt(new RewindTurn(1, 0, "one", 0), new RewindCut(1, [], false, false, null, false))), output);
        Assert.DoesNotContain("rewound", output);
        Assert.Equal(["one", "two"], UserLines(_chat.Requests[1]));
    }

    /// <summary>Two ESCs on an empty line open the picker (the first shows the armed hint); a line sent between them disarms.</summary>
    [Fact]
    public async Task DoubleEscape_OnAnEmptyLine_OpensThePicker_ALineBetweenDisarms()
    {
        _settings.Update(d => d.TtsOutput = false);
        _geometry = new ScreenGeometry(() => null);
        _chat.EnqueueText("One.").EnqueueText("Two.");
        StepsWhenIdle(
            Line("one"),
            Key(Keys.Escape), Line("two"), Key(Keys.Escape),   // disarmed by the line: the second ESC only arms again
            Line("/help"), Key(Keys.Escape),                   // a pane between: disarmed again
            Key(Keys.Escape), Key(Keys.Escape),                // the pair: the picker
            Key(Keys.Escape),                                  // closed, nothing picked
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(RewindText.ArmedHint, output);
        Assert.Single(output.Split(Titled(RewindText.Title)).Skip(1));
        Assert.Equal(2, _chat.Requests.Count);
    }

    /// <summary>With no turn the double ESC never arms and <c>/rewind</c> says there is nothing; a count past the turns is the usage error.</summary>
    [Fact]
    public async Task Rewind_WithNothing_OrABadCount_SaysSo()
    {
        _settings.Update(d => d.TtsOutput = false);
        _geometry = new ScreenGeometry(() => null);
        _chat.EnqueueText("One.");
        StepsWhenIdle(Key(Keys.Escape), Key(Keys.Escape), Line("/rewind"), Line("one"), Line("/rewind 2"), Line("/rewind x"), Line("/exit"));

        string output = await RunAsync();

        Assert.DoesNotContain(RewindText.ArmedHint, output);
        Assert.Contains("· " + RewindText.NothingNotice, output);
        Assert.Equal(2, output.Split(RewindText.UsageError(1)).Length - 1);
        Assert.DoesNotContain(Titled(RewindText.Title), output);
    }

    /// <summary>
    /// A restored session's picture (no recall entry for it this run): the line is rebuilt with the picture put back as a
    /// token, so the re-sent message carries it again. <c>/rewind 1</c> puts the cursor on the last message.
    /// </summary>
    [Fact]
    public async Task Rewind_RestoredPicture_ComesBackAsAToken_AndIsSentAgain()
    {
        _settings.Update(d => d.TtsOutput = false);
        _geometry = new ScreenGeometry(() => null);
        byte[] png = ImageFileTests.BlackPng(4, 4);
        long id;
        using (var store = OpenSessions())
        {
            id = store.Begin("first", "llama")!.Value;
            var history = new ConversationHistory("");
            foreach (var (line, picture) in new[] { ("first", false), ("look at [Image #1]", true) })
            {
                history.AddUser(line, picture ? [new ImageAttachment("clipboard-1.png", png, ImageFile.Png, 4, 4)] : []);
                history.AddAssistant("ok");
                ConversationHistory.SetTurnOrdinal(history.LastTurnStart()!, store.AppendTurn(id, line, "ok", 0, [], [], 0, 1, 1, false)!.Value);
            }

            store.SaveHistory(id, SessionHistory.ToJson(history.Messages));
        }

        _chat.EnqueueText("Seen.");
        StepsWhenIdle(Line("/sessions " + id), Line("/rewind 1"), Key(Keys.Enter), Key(Keys.Char('y')), Key(Keys.Enter), Key(Keys.Enter), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("· " + RewindText.RewoundNotice(1, "look at [Image #1]"), output);
        var request = Assert.Single(_chat.Requests);
        var sent = request.Last(ConversationHistory.IsTurnStart);
        Assert.Equal("look at " + PasteBlocks.ImageLabel(1), sent.Text);
        Assert.Single(sent.Contents.OfType<DataContent>());
        Assert.Equal(["first", "look at [Image #1]"], UserLines(request));
    }
    /// <summary>Without the bottom pane there is no list: <c>/rewind 2</c> asks the yes/no for the second message from the end; No keeps.</summary>
    [Fact]
    public async Task Rewind_WithoutThePane_AsksForTheCountedMessage()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("One.").EnqueueText("Two.").EnqueueText("Again.");
        PushLine("one");
        PushLine("two");
        PushLine("/rewind");
        _console.Input.PushKey(Keys.Enter);   // No
        PushLine("/rewind 2");
        PickYes();
        PushLine("fresh");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SettingsMenu.PromptTitle(RewindText.ConfirmPrompt(new RewindTurn(2, 0, "two", 0), new RewindCut(1, [], false, false, null, false)), SettingsMenu.ConfirmKeys), output);
        Assert.Contains("· " + ChatScreen.KeptNotice, output);
        Assert.Contains("· " + RewindText.RewoundNotice(2, "one"), output);
        Assert.Equal(3, _chat.Requests.Count);
        Assert.Single(UserLines(_chat.Requests[2]));   // both turns went: the next message opens the conversation again
    }
}
