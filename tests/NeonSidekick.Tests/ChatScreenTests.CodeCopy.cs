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
        Assert.Contains("  · " + CopyText.CopiedCodeNotice(1, 2), output);
        Assert.Contains("  · " + CopyText.CopiedCodeNotice(2, 2), output);
    }

    /// <summary>The line ending a copy gets on this machine (2026-11-02, review P3-1).</summary>
    private static string ClipboardLines(string text) => text.ReplaceLineEndings(OperatingSystem.IsWindows() ? "\r\n" : "\n");

    /// <summary>Every ⧉ on the screen, top to bottom, with what its click copies (one a row).</summary>
    private List<(int X, int Y, string Text)> FindCopyGlyphs()
    {
        var pane = _running!.PaneForTests;
        var found = new List<(int X, int Y, string Text)>();
        for (int y = 50; y <= 99; y++)
        {
            for (int x = 0; x < _console.Profile.Width; x++)
            {
                if (pane.TryHitCodeCopy(x, y, out string text))
                {
                    found.Add((x, y, text));
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// <c>/copy code n</c> is the n-th ⧉'s click for every n (2026-11-02, the copy-code findings P2-7, P3-2, P12-4's numbering): an empty block
    /// (no ⧉, no number), a block in the piece after a tool line, and a label too wide for its ⧉ (no number either), folded or not.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    public async Task CopyCode_N_IsTheNthGlyphsClick_ForEveryN(int keep)
    {
        _settings.Update(d => { d.TtsOutput = false; d.CopyUserPrompt = false; d.CodeCollapseCount = keep; });
        PaneOf40Rows();
        string wide = new('w', _console.Profile.Width + 10);
        _chat.Enqueue(new ChatResponseUpdate(ChatRole.Assistant, "Empty:\n\n```\n```\n\nOne:\n\n```py\nx = 1\n```\n\nThen"), FakeChatClient.Call("k1", GetCurrentTimeTool.ToolName));
        _chat.EnqueueText("After:\n\n```\nafter\n```\n\nWide:\n\n```" + wide + "\nw\n```\n\nLast:\n\n```\nend\n```");
        List<(int X, int Y, string Text)> glyphs = [];
        StepsWhenIdle(Line("go"), input =>
        {
            glyphs = FindCopyGlyphs();
            for (int n = 1; n <= glyphs.Count + 1; n++)
            {
                PushLine(input, "/copy code " + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            PushLine(input, "/exit");
        });

        string output = await RunAsync();

        // The empty block has no ⧉; the too-wide label wraps, and only an unfolded one carries a ⧉ on its last row (keep 0).
        string[] expected = keep == 0 ? ["x = 1", "after", "w", "end"] : ["x = 1", "after", "end"];
        Assert.Equal(expected, glyphs.Select(g => g.Text));
        Assert.Equal(glyphs.Select(g => ClipboardLines(g.Text)), _copied);
        Assert.Contains("  ✗ " + CopyText.CodeBlockPastCountError(glyphs.Count + 1, glyphs.Count), output);
    }

    /// <summary>
    /// Code collapse count 0 (2026-11-02, review P1-2): a block streamed taller than the region keeps its label in the slot until it closes,
    /// so the label commits with its ⧉ and the block keeps its number, though it scrolled off the screen.
    /// </summary>
    [Fact]
    public async Task CopyCode_ATallBlockStreamedAtCollapseZero_KeepsItsNumber()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CopyUserPrompt = false; d.CodeCollapseCount = 0; });
        PaneOf40Rows();
        var lines = Enumerable.Range(1, 150).Select(n => "line " + n.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var updates = new List<ChatResponseUpdate> { new(ChatRole.Assistant, "Tall:\n\n```\n") };
        updates.AddRange(lines.Select(line => new ChatResponseUpdate(ChatRole.Assistant, line + "\n")));
        updates.Add(new ChatResponseUpdate(ChatRole.Assistant, "```\n\nShort:\n\n```\nend\n```"));
        _chat.Enqueue([.. updates]);
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 140)
            {
                // Ticks draw the block open and taller than the region: without the held label, its rows commit without their ⧉.
                await Task.Delay(50, CancellationToken.None);
                for (int tick = 0; tick < 3; tick++)
                {
                    _time.Advance(ScreenPane.Tick);
                    await Task.Delay(20, CancellationToken.None);
                }
            }
        };
        StepsWhenIdle(Line("go"), Line("/copy code 1"), Line("/copy code"), Line("/exit"));

        string output = await RunAsync();

        Assert.Equal([ClipboardLines(string.Join("\n", lines)), "end"], _copied);
        Assert.Contains("  · " + CopyText.CopiedCodeNotice(1, 2), output);
        Assert.Contains("  · " + CopyText.CopiedCodeNotice(2, 2), output);
    }

    /// <summary>
    /// <c>/copy code</c> under the first reply (2026-11-02, review P11-3): the streaming reply's closed block, though the log holds no reply yet
    /// and the block is still in the live slot.
    /// </summary>
    [Fact]
    public async Task CopyCode_UnderAReply_CopiesTheStreamingReplysClosedBlock()
    {
        MidTurnFixture(i =>
        {
            if (i == 2)
            {
                PushLine("/copy code");
            }
        }, "Here:\n\n```\nfirst\n```\n\n", "more ", "text.");

        string output = await RunAsync();

        Assert.Equal(["first"], _copied);
        Assert.Contains("  · " + CopyText.CopiedCodeNotice(1, 1), output);
    }

    /// <summary>A block's hidden characters stay off the clipboard and the notice says so (2026-11-02, review P12-4); the ⧉ click drops them too.</summary>
    [Fact]
    public async Task CopyCode_LeavesOutHiddenCharacters_AndSaysSo()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CopyUserPrompt = false; });
        PaneOf40Rows();
        _chat.EnqueueText("```\nexample\u200B.com\n```");
        (int X, int Y, string Text)? at = null;
        StepsWhenIdle(Line("go"), input =>
        {
            at = FindCopyGlyph();
            input.PushClick(at!.Value.X, at.Value.Y);
            PushLine(input, "/copy code");
        }, Line("/exit"));

        string output = await RunAsync();

        Assert.Equal(["example.com", "example.com"], _copied);
        Assert.Contains("  · " + CopyText.WithHidden(CopyText.CopiedCodeNotice(1, 1), 1), output);
        Assert.Contains(CopyText.HiddenLeftOut(1), CopyText.WithHidden(InputLine.CopiedFlash(11), 1));
    }

    /// <summary>A Mac's pasteboard gets bare line feeds (2026-11-02, review P3-1): <c>/copy code</c> and plain <c>/copy</c> alike.</summary>
    [MacFact]
    public async Task OnAMac_TheClipboardGetsNoCarriageReturns()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CopyUserPrompt = false; });
        _chat.EnqueueText("Two:\n\n```\nx\ny\n```");
        PushLine("go");
        PushLine("/copy code");
        PushLine("/copy");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal(2, _copied.Count);
        Assert.Equal("x\ny", _copied[0]);
        Assert.DoesNotContain('\r', _copied[1]);
    }
}
