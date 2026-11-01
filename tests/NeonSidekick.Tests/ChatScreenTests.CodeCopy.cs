using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// Copy a code block (2026-11-01, plan memoized-dancing-horizon item #3): a click on a block's ⧉ copies its source with the clipboard's
/// line endings and flashes the hint row, the draft untouched, at the idle line and under a reply alike; <c>/copy code</c> numbers the
/// blocks as the screen drew them, a reply cut by a tool line read in its pieces.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The first cell of the transcript whose click copies a code block (the region's rows over the input row at 100), with what it copies, or null.</summary>
    private (int X, int Y, string Text)? FindCopyGlyph()
    {
        var pane = _running!.PaneForTests;
        for (int y = 99; y >= 50; y--)
        {
            for (int x = 0; x < _console.Profile.Width; x++)
            {
                if (pane.TryHitCodeCopy(x, y, out string text))
                {
                    return (x, y, text);
                }
            }
        }

        return null;
    }

    [Fact]
    public async Task AClickOnACodeBlocksGlyph_AtTheIdleLine_CopiesIt_AndFlashes_TheDraftKept()
    {
        _settings.Update(d => d.TtsOutput = false);
        PaneOf40Rows();
        _chat.EnqueueText("Here:\n\n```py\nx = 1\ny = 2\n```\n\nDone.");
        _chat.EnqueueText("ok");
        (int X, int Y, string Text)? at = null;
        StepsWhenIdle(
            Line("some code"),
            input =>
            {
                at = FindCopyGlyph();
                input.Push(Keys.Char('a'), Keys.Char('b'));
                input.PushClick(at!.Value.X, at.Value.Y);
            },
            input => input.Push(Keys.Enter),   // the draft is still there: sent
            Line("/exit"));

        string output = await RunAsync();

        Assert.NotNull(at);
        Assert.Equal("x = 1\ny = 2", at!.Value.Text);
        Assert.Equal(["x = 1\r\ny = 2"], _copied);
        Assert.Contains(InputLine.CopiedFlash("x = 1\r\ny = 2".Length), output);
        Assert.Equal("ab", _chat.Requests[1].Last(m => m.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task AClickOnACodeBlocksGlyph_UnderAReply_CopiesIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        PaneOf40Rows();
        _chat.EnqueueText("```\nfirst\n```");
        _chat.EnqueueText("Second ", "answer.");
        bool clicked = false;
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 1 && _chat.Requests.Count == 2 && FindCopyGlyph() is { } at)
            {
                _scripted!.PushClick(at.X, at.Y);
                clicked = true;
                for (int tries = 0; tries < 500 && _scripted.IsAvailable; tries++)
                {
                    await Task.Delay(10, CancellationToken.None);
                }
            }
        };
        StepsWhenIdle(Line("one"), Line("two"), Line("/exit"));

        await RunAsync();

        Assert.True(clicked);
        Assert.Equal(["first"], _copied);
    }

    [Fact]
    public async Task CopyCode_OnAReplyCutByAToolLine_ReadsEachPieceAsTheScreenDrewIt()
    {
        // The model opened a fence, called a tool, then wrote a block of its own: the screen drew two blocks (the first ended with its
        // piece), so /copy code numbers two, where the joined text would read one block swallowing the second's fence.
        _settings.Update(d => { d.TtsOutput = false; d.CopyUserPrompt = false; });
        _chat.Enqueue(new ChatResponseUpdate(ChatRole.Assistant, "Start:\n\n```\na"), FakeChatClient.Call("k1", GetCurrentTimeTool.ToolName));
        _chat.EnqueueText("```\nb\n```");
        PushLine("go");
        PushLine("/copy code 1");
        PushLine("/copy code");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["a", "b"], _copied);
        Assert.Contains("  · " + ChatScreen.CopiedCodeNotice(1, 2), output);
        Assert.Contains("  · " + ChatScreen.CopiedCodeNotice(2, 2), output);
    }
}
