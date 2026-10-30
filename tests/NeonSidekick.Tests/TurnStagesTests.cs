using NeonSidekick.App;
using NeonSidekick.Comfy;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;

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
    public void TheModelsPictureCall_ReadsAsEveryComfyUIGeneration()
    {
        // 2026-09-25: generate_image is the ComfyUI label, not the tool's name; the other tools keep theirs.
        var stages = new TurnStages(funVerbs: true, new Random(7));
        stages.Start();
        Assert.Equal(ComfyText.TextToImageLabel, stages.Advance(Call(GenerateImageTool.ToolName)));
        Assert.False(ComfyText.IsGeneratingLabel(stages.Advance(Result(GenerateImageTool.ToolName))!));
        Assert.Equal("read_file", stages.Advance(Call("read_file")));
    }

    [Theory]
    [InlineData("{\"prompt\":\"a cat\"}", false)]
    [InlineData("{\"prompt\":\"a cat\",\"image\":\"\"}", false)]
    [InlineData("{\"prompt\":\"a cat\",\"image\":\"cat.png\"}", true)]
    [InlineData("{\"image\":\"a.png\",\"image2\":\"b.png\"}", true)]
    [InlineData("not json", false)]
    public void TheModelsPictureCall_IsThePaletteWithAnInputPicture_TheFramedPictureWithout(string arguments, bool fromPicture)
    {
        // 2026-09-26, the user's ask: 🖼️ text-to-image, 🎨 image-to-image.
        var stages = new TurnStages(funVerbs: false, new Random(7));
        stages.Start();
        Assert.Equal(fromPicture ? ComfyText.GeneratingLabel : ComfyText.TextToImageLabel,
            stages.Advance(new TurnEvent.ToolCall(GenerateImageTool.ToolName, "call_0001", arguments)));
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
        string Draw() => last = ThinkingVerbs.Pick(expected, last)!;

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

    /// <summary>
    /// 2026-10-29, session 832: a call being written names itself (not "thinking"), with its size once it shows; one verb for the call (a
    /// fun one under the setting, kept across its sizes), never the tool stage (its clock would restart at each size), and the call's own
    /// name once it is whole.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACallBeingWritten_ReadsAsWritingTheTool_ThenTheToolsName(bool funVerbs)
    {
        var stages = new TurnStages(funVerbs, new Random(7));
        stages.Start();

        string named = stages.Advance(new TurnEvent.ToolWriting("present_audit", 0))!;
        Assert.EndsWith(" present_audit", named, StringComparison.Ordinal);
        string verb = named[..^" present_audit".Length];
        if (funVerbs)
        {
            Assert.Contains(verb, ThinkingVerbs.All);
        }
        else
        {
            Assert.Equal(TurnStages.WritingLabel, verb);
        }

        Assert.False(stages.InTool);
        Assert.Equal(TurnStages.ToolWritingLabel("present_audit", 41_000, verb), stages.Advance(new TurnEvent.ToolWriting("present_audit", 41_000)));
        Assert.False(stages.InTool);
        Assert.Equal("present_audit", stages.Advance(Call("present_audit")));
        Assert.True(stages.InTool);
    }

    /// <summary>A call's label that reads the same (its size not shown yet) changes nothing on the busy row (2026-10-30, review F21).</summary>
    [Fact]
    public void ACallsLabel_ThatReadsTheSame_IsShownOnce()
    {
        var stages = new TurnStages(funVerbs: false, new Random(7));
        stages.Start();
        Assert.Equal(TurnStages.ToolWritingLabel("present_audit", 12), stages.Advance(new TurnEvent.ToolWriting("present_audit", 12)));
        Assert.Null(stages.Advance(new TurnEvent.ToolWriting("present_audit", 640)));
        Assert.Equal(TurnStages.ToolWritingLabel("present_audit", 41_000), stages.Advance(new TurnEvent.ToolWriting("present_audit", 41_000)));
        Assert.Null(stages.Advance(new TurnEvent.ToolWriting("present_audit", 41_000)));
    }

    /// <summary>Thinking after a call being written is the thinking stage; after text it stays writing, as it was (2026-10-30, review F77).</summary>
    [Fact]
    public void ThinkingAfterACallBeingWritten_ReadsAsThinking_AfterTextNothing()
    {
        var thinking = new TurnEvent.ThinkingDelta("hmm");
        var stages = new TurnStages(funVerbs: false, new Random(7));
        stages.Start();
        Assert.NotNull(stages.Advance(new TurnEvent.ToolWriting("present_audit", 12)));
        Assert.Equal("thinking", stages.Advance(thinking));
        Assert.Null(stages.Advance(thinking));
        // The same call again after the thinking: its label comes back.
        Assert.Equal(TurnStages.ToolWritingLabel("present_audit", 12), stages.Advance(new TurnEvent.ToolWriting("present_audit", 12)));

        Assert.Equal("writing", stages.Advance(Text));
        Assert.Null(stages.Advance(thinking));
    }

    [Fact]
    public void TheWritingLabel_ShowsTheSize_FromItsThreshold()
    {
        Assert.Equal(TurnStages.WritingLabel + " present_audit", TurnStages.ToolWritingLabel("present_audit", ToolArgumentWatch.CountShownFrom - 1));
        Assert.StartsWith(TurnStages.WritingLabel + " present_audit (" + UsageText.CompactNumber(ToolArgumentWatch.CountShownFrom), TurnStages.ToolWritingLabel("present_audit", ToolArgumentWatch.CountShownFrom), StringComparison.Ordinal);
        Assert.Contains(UsageText.CompactNumber(41_000), TurnStages.ToolWritingLabel("present_audit", 41_000), StringComparison.Ordinal);
    }

    [Fact]
    public void AMidTurnCompact_ReadsAsTheCompactsSpinner_ThenThinkingAgain()
    {
        // 2026-09-28: the summariser's wait is named; the next request's wait after it is the thinking stage.
        var stages = new TurnStages(funVerbs: false, new Random(7));
        stages.Start();
        Assert.Equal("read_file", stages.Advance(Call("read_file")));
        Assert.Equal("thinking", stages.Advance(Result("read_file")));
        Assert.Equal(CompactionText.CompactingLabel, stages.Advance(new TurnEvent.Compacting(91)));
        Assert.Equal("thinking", stages.Advance(new TurnEvent.Compacted(new ConversationCompactor.Result(5, 3, 0, null, Summarised: true), 91, ThisTurn: true)));
        Assert.Equal("writing", stages.Advance(Text));
    }
}
