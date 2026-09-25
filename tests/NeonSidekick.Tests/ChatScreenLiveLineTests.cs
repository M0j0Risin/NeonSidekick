using NeonSidekick.App;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The input row under a reply (2026-09-25, the user's ask): the idle line's editor, live — the arrows, Home/End, the
/// history, the lists and the selection work while the model answers, Enter queues the edited line, and the draft
/// outlives the reply with its cursor; ESC cancels the reply and keeps the draft, the next ESC at the idle line clears it.
/// And the <c>/botchat</c> ESC ladder on the screen: the bot replying cut short, then the chat.
/// </summary>
public partial class ChatScreenTests
{
    private void TypeUnderTheReply(params ConsoleKeyInfo[] keys) => Scripted().Push(keys);

    private static ConsoleKeyInfo[] Chars(string text) => text.Select(Keys.Char).ToArray();

    [Fact]
    public async Task LiveLine_TheArrowsEditTheRow_UnderTheReply_AndEnterQueuesTheEditedLine()
    {
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                TypeUnderTheReply([.. Chars("helo"), Keys.Left, Keys.Char('l'), Keys.Home, Keys.Char('>'), Keys.End, Keys.Char('?'), Keys.Enter]);
            }
        });
        _chat.EnqueueText("Second.");

        string output = await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal(">hello?", _chat.Requests[1][^1].Text);
        Assert.DoesNotContain(ChatScreen.CancelledNotice, output);
    }

    [Fact]
    public async Task LiveLine_UpRecallsTheHistory_UnderTheReply()
    {
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                TypeUnderTheReply(Keys.Up, Keys.Enter);
            }
        });
        _chat.EnqueueText("Again.");

        await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal("hi", _chat.Requests[1][^1].Text);
    }

    [Fact]
    public async Task LiveLine_TheDraftOutlivesTheReply_WithItsCursor()
    {
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                TypeUnderTheReply([.. Chars("abc"), Keys.Left]);
            }
        });
        StepsWhenIdle(Line("hi"), input => input.Push(Keys.Char('X'), Keys.Enter), Line("/exit"));
        _chat.EnqueueText("Second.");

        await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal("abXc", _chat.Requests[1][^1].Text);
    }

    [Fact]
    public async Task LiveLine_Esc_WithADraft_CancelsTheReply_AndTheDraftStays()
    {
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                TypeUnderTheReply([.. Chars("keep me"), Keys.Escape]);
            }
        });
        StepsWhenIdle(Line("hi"), Key(Keys.Enter), Line("/exit"));
        _chat.EnqueueText("Second.");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.CancelledNotice, output);
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal("keep me", _chat.Requests[1][^1].Text);
    }

    [Fact]
    public async Task LiveLine_EscOverTheCommandList_ClosesIt_AndTheReplyRunsOn()
    {
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                TypeUnderTheReply([.. Chars("/he"), Keys.Escape, Keys.Ctrl(ConsoleKey.A), Keys.Backspace]);
            }
        });

        string output = await RunAsync();

        Assert.Single(_chat.Requests);
        Assert.Contains("three.", output);
        Assert.Contains(MentionCompleter.Hint, output);
        Assert.DoesNotContain(ChatScreen.CancelledNotice, output);
    }

    [Fact]
    public async Task LiveLine_ACommandThatCancels_WaitsForTheIdleLine_AndRunsThere()
    {
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                TypeUnderTheReply([.. Chars("/new"), Keys.Enter]);
            }
        });

        string output = await RunAsync();

        Assert.Single(_chat.Requests);
        Assert.Contains(ChatScreen.CancelledNotice, output);
        Assert.Contains(ChatScreen.NewConversationNotice, output);
    }

    /// <summary>Three bots' turns with two chunks each, ESC once in the first two and twice in the third.</summary>
    [Fact]
    public async Task BotChat_Esc_CutsTheBotReplying_ItsWordsStay_TheChatGoesOn_TwiceEndsIt()
    {
        BotChatFixture();
        _chat.EnqueueText("Neon ", "one");
        _chat.EnqueueText("Ada ", "two");
        _chat.EnqueueText("Neon ", "three");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 1)
            {
                // The first two presses each cut one bot (ada's after its first word: the arm from neon's is spent); the third
                // cuts neon again, and the fourth, read as ada's turn opens, ends the chat before its request.
                _console.Input.PushKey(Keys.Escape);
                if (_chat.Requests.Count == 3)
                {
                    _console.Input.PushKey(Keys.Escape);
                }

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        PushLine("/botchat pizza");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Equal("default: Neon", _chat.Requests[1][^1].Text);   // the cut reply's words stay in the chat
        Assert.Equal("ada: Ada", _chat.Requests[2][^1].Text);
        Assert.Contains("  · " + BotChat.CutShortNotice("default") + "\n", output);
        Assert.Contains("  · " + BotChat.CutShortNotice("ada") + "\n", output);
        Assert.Contains("  · " + BotChat.StoppedNotice(3) + "\n", output);
    }

    [Fact]
    public async Task BotChat_ACommandThatCancels_EndsTheChat_AndRunsAtTheIdleLine()
    {
        BotChatFixture();
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _chat.EnqueueText("Hello ", "from Neon.");
        _chat.EnqueueText("Ada ", "answers.");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (_chat.Requests.Count == 2 && i == 1)
            {
                PushLine("/exit");
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        PushLine("/botchat pizza");

        string output = await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.DoesNotContain(BotChat.CutShortNotice("ada"), output);
        Assert.Contains(BotChat.StoppedNotice(2), output);
        Assert.Contains(InputLine.PromptGlyph + "/exit", output);
    }
}
