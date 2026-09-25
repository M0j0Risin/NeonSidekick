using NeonSidekick.App;
using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

public sealed class TurnStagesTests
{
    private static readonly TurnEvent Text = new TurnEvent.TextDelta("hi");
    private static readonly TurnEvent Usage = new TurnEvent.Usage(new TokenUsage(10, 5, 15, 1, TimeSpan.Zero, TimeSpan.Zero));
    private static readonly TurnEvent Notice = new TurnEvent.Notice("(✂️ context at 85%: pruned 2 tool results from this turn)", IsError: false);

    private static TurnEvent Call(string name) => new TurnEvent.ToolCall(name, "call_0001", "{}");

    private static TurnEvent Result(string name) => new TurnEvent.ToolResult(name, "call_0001", "ok");

    [Fact]
    public void PlainWords_FollowTheStages_ThinkingWritingToolThinking()
    {
        var stages = new TurnStages(funVerbs: false, new Random(7));
        Assert.Equal("thinking", stages.Start());

        // Text: writing once, then nothing while it streams; usage ends the stream silently.
        Assert.Equal("writing", stages.Advance(Text));
        Assert.Null(stages.Advance(Text));
        Assert.Null(stages.Advance(Usage));
        // A tool: its bare name from the call, thinking again from the result (the next wait).
        Assert.Equal("read_file", stages.Advance(Call("read_file")));
        Assert.Null(stages.Advance(Notice));
        Assert.Equal("thinking", stages.Advance(Result("read_file")));
        // The next stream: writing again.
        Assert.Equal("writing", stages.Advance(Text));
        Assert.Null(stages.Advance(Text));
    }

    [Fact]
    public void AQuietTool_NamesItselfToo_AndConsecutiveToolsFlipThroughThinking()
    {
        var stages = new TurnStages(funVerbs: false, new Random(7));
        stages.Start();
        Assert.Equal("web_search", stages.Advance(Call("web_search")));
        Assert.Equal("thinking", stages.Advance(Result("web_search")));
        Assert.Equal("web_fetch", stages.Advance(Call("web_fetch")));
        Assert.Equal("thinking", stages.Advance(Result("web_fetch")));
    }

    [Fact]
    public void AToolCallStraightAfterTheStart_NeedsNoWriting()
    {
        // A buffered tool call with no text ahead of it: thinking → the tool → thinking, and the
        // first text after it is the first writing stage.
        var stages = new TurnStages(funVerbs: false, new Random(7));
        Assert.Equal("thinking", stages.Start());
        Assert.Equal("list_files", stages.Advance(Call("list_files")));
        Assert.Equal("thinking", stages.Advance(Result("list_files")));
        Assert.Equal("writing", stages.Advance(Text));
    }

    [Fact]
    public void FunVerbs_DrawAFreshVerbPerNonToolStage_NeverTheOneJustShown_TheToolStillNamed()
    {
        var stages = new TurnStages(funVerbs: true, new Random(7));
        var expected = new Random(7);
        string? last = null;
        string Draw() => last = ThinkingVerbs.Pick(expected, last);

        Assert.Equal(Draw(), stages.Start());
        Assert.Equal(Draw(), stages.Advance(Text));
        Assert.Null(stages.Advance(Text));
        Assert.Null(stages.Advance(Usage));
        Assert.Equal("read_file", stages.Advance(Call("read_file")));
        Assert.Equal(Draw(), stages.Advance(Result("read_file")));
        Assert.Equal(Draw(), stages.Advance(Text));
    }

    [Fact]
    public void FunVerbs_TwoAdjacentStages_NeverReadTheSame()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            var stages = new TurnStages(funVerbs: true, new Random(seed));
            string previous = stages.Start();
            Assert.Contains(previous, ThinkingVerbs.All);
            for (int i = 0; i < 20; i++)
            {
                string next = stages.Advance(i % 2 == 0 ? Text : Result("x"))!;
                Assert.Contains(next, ThinkingVerbs.All);
                Assert.NotEqual(previous, next);
                previous = next;
            }
        }
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new TurnStages(false, null!));
        Assert.Throws<ArgumentNullException>(() => new TurnStages(false, new Random(1)).Advance(null!));
    }
}
