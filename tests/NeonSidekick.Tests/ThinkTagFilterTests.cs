using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

public class ThinkTagFilterTests
{
    private static (string Output, ThinkTagFilter Filter) Run(params string[] deltas)
    {
        var filter = new ThinkTagFilter();
        var output = string.Concat(deltas.Select(filter.Push)) + filter.Flush();
        return (output, filter);
    }

    [Fact]
    public void PlainText_PassesThroughUnchanged()
    {
        var (output, filter) = Run("Hello", ", ", "world.");

        Assert.Equal("Hello, world.", output);
        Assert.False(filter.SawBlock);
        Assert.False(filter.SawOrphanClose);
    }

    [Fact]
    public void WholeBlock_InOneDelta_IsDropped()
    {
        var (output, filter) = Run("<think>\nThe user asks.\n</think>\n\nAnswer.");

        Assert.Equal("Answer.", output);
        Assert.True(filter.SawBlock);
        Assert.False(filter.SawOrphanClose);
    }

    [Fact]
    public void Block_SplitAcrossDeltas_IsDropped_AndTheTextAfterStreams()
    {
        var filter = new ThinkTagFilter();

        Assert.Equal("", filter.Push("<thi"));
        Assert.Equal("", filter.Push("nk>The user asks.</th"));
        Assert.Equal("Answer", filter.Push("ink>\n\nAnswer"));
        Assert.Equal(" one.", filter.Push(" one."));
        Assert.Equal(" Two.", filter.Push(" Two."));
        Assert.Equal("", filter.Flush());
        Assert.True(filter.SawBlock);
    }

    [Fact]
    public void OrphanClose_IsDropped_WithTheWhitespaceAfterIt()
    {
        var filter = new ThinkTagFilter();

        // What SGLang's qwen3 parser streams when the model skipped the opener (field, 2026-09-11).
        Assert.Equal("Got it.\n", filter.Push("Got it.\n"));
        Assert.Equal("", filter.Push("</think>"));
        Assert.Equal("", filter.Push("\n\n"));
        Assert.Equal("Done.", filter.Push("Done."));
        Assert.Equal("", filter.Flush());
        Assert.True(filter.SawOrphanClose);
        Assert.False(filter.SawBlock);
    }

    [Fact]
    public void FalseTagStart_IsReleased_WhenTheNextDeltaDisprovesIt()
    {
        var filter = new ThinkTagFilter();

        Assert.Equal("a ", filter.Push("a <"));
        Assert.Equal("<b", filter.Push("b"));
        Assert.Equal(" ", filter.Push(" </"));
        Assert.Equal("", filter.Push("t"));
        Assert.Equal("</tr>", filter.Push("r>"));
    }

    [Fact]
    public void ReplyEndingInATagStart_IsReleasedByFlush()
    {
        var filter = new ThinkTagFilter();

        Assert.Equal("x ", filter.Push("x <"));
        Assert.Equal("<", filter.Flush());
        Assert.Equal("", filter.Flush());
    }

    [Fact]
    public void UnfinishedBlock_IsDroppedByFlush()
    {
        var filter = new ThinkTagFilter();

        Assert.Equal("", filter.Push("<think>never closed </th"));
        Assert.Equal("", filter.Flush());
    }

    [Fact]
    public void EmptyDelta_EmitsNothing_AndChangesNothing()
    {
        var filter = new ThinkTagFilter();

        Assert.Equal("", filter.Push(""));
        Assert.Equal("Hi", filter.Push("Hi"));
        Assert.Equal("", filter.Push(""));
        Assert.Equal("", filter.Flush());
    }

    [Fact]
    public void TextBeforeABlock_IsKept()
    {
        var (output, _) = Run("Sure. ", "<think>hmm</think>", " Yes.");

        Assert.Equal("Sure. Yes.", output);
    }

    [Fact]
    public void ABlockAfterAnOrphanClose_IsStillDropped()
    {
        var (output, filter) = Run("a</think>b<think>c</think>d");

        Assert.Equal("abd", output);
        Assert.True(filter.SawOrphanClose);
        Assert.True(filter.SawBlock);
    }

    [Fact]
    public void Push_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ThinkTagFilter().Push(null!));
    }

    [Fact]
    public void TakeThinking_HandsOverTheBlockText_AsItStreams()
    {
        var filter = new ThinkTagFilter();

        Assert.Equal("", filter.Push("<think>The user"));
        Assert.Equal("The user", filter.TakeThinking());
        Assert.Equal("", filter.TakeThinking());
        Assert.Equal("", filter.Push(" asks.</th"));

        // The possible close tag stays back until the next delta settles it.
        Assert.Equal(" asks.", filter.TakeThinking());
        Assert.Equal("Answer.", filter.Push("ink>\n\nAnswer."));
        Assert.Equal("", filter.TakeThinking());
    }

    [Fact]
    public void TakeThinking_KeepsAFalseCloseTagStart_AsThinking()
    {
        var filter = new ThinkTagFilter();

        filter.Push("<think>a </t");
        filter.Push("able");
        Assert.Equal("a </table", filter.TakeThinking());
    }

    [Fact]
    public void TakeThinking_GetsAnUnfinishedBlock_FromFlush()
    {
        var filter = new ThinkTagFilter();

        filter.Push("<think>still going</thi");
        Assert.Equal("", filter.Flush());
        Assert.Equal("still going</thi", filter.TakeThinking());
    }

    [Fact]
    public void TakeThinking_IsEmpty_ForAnOrphanClose()
    {
        var (output, filter) = Run("Got it.", "</think>", "Done.");

        Assert.Equal("Got it.Done.", output);
        Assert.Equal("", filter.TakeThinking());
    }
}
