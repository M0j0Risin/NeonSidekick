using Microsoft.Extensions.AI;
using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

public class TokenUsageTests
{
    [Fact]
    public void From_ReadsTheCounts_AndOneRequest()
    {
        var usage = TokenUsage.From(
            new UsageDetails { InputTokenCount = 1102, OutputTokenCount = 138, TotalTokenCount = 1240 },
            TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(2));

        Assert.Equal(new TokenUsage(1102, 138, 1240, 1, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(2)), usage);
        Assert.False(usage.IsEmpty);
        Assert.Equal(69, usage.TokensPerSecond);
    }

    [Fact]
    public void From_ToleratesAPartialReport()
    {
        // A server that reports only some fields: a missing count is zero, a missing total the sum.
        var usage = TokenUsage.From(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }, TimeSpan.Zero, TimeSpan.Zero);
        Assert.Equal(15, usage.Total);

        var empty = TokenUsage.From(new UsageDetails(), TimeSpan.Zero, TimeSpan.Zero);
        Assert.Equal(new TokenUsage(0, 0, 0, 1, TimeSpan.Zero, TimeSpan.Zero), empty);
        Assert.False(empty.IsEmpty);  // a report is a report, even an empty one
    }

    [Fact]
    public void Zero_IsEmpty_AndAddsEverything()
    {
        Assert.True(TokenUsage.Zero.IsEmpty);
        Assert.Null(TokenUsage.Zero.TokensPerSecond);

        var a = new TokenUsage(10, 5, 15, 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        var b = new TokenUsage(20, 10, 30, 2, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4));

        Assert.Equal(new TokenUsage(30, 15, 45, 3, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6)), a + b);
        Assert.Equal(a, TokenUsage.Zero + a);
    }

    [Fact]
    public void From_ReadsTheReasoningCount_WhenTheServerCountedIt()
    {
        // vLLM 0.28's streamed report for a thinking reply (captured 2026-09-14): 135 of the 139 completion tokens.
        var counted = TokenUsage.From(new UsageDetails { InputTokenCount = 22, OutputTokenCount = 139, TotalTokenCount = 161, ReasoningTokenCount = 135 }, TimeSpan.Zero, TimeSpan.Zero);
        Assert.Equal(135, counted.Reasoning);
        Assert.Equal(new TokenUsage(22, 139, 161, 1, TimeSpan.Zero, TimeSpan.Zero, 135), counted);

        // Thinking off is a count of zero, not a missing one; a server without the field leaves it null.
        Assert.Equal(0, TokenUsage.From(new UsageDetails { OutputTokenCount = 4, ReasoningTokenCount = 0 }, TimeSpan.Zero, TimeSpan.Zero).Reasoning);
        Assert.Null(TokenUsage.From(new UsageDetails { OutputTokenCount = 4 }, TimeSpan.Zero, TimeSpan.Zero).Reasoning);
        Assert.Null(TokenUsage.Zero.Reasoning);
    }

    [Fact]
    public void Add_SumsTheReasoningReports_AndStaysNullOnlyWithoutOne()
    {
        var none = new TokenUsage(10, 5, 15, 1, TimeSpan.Zero, TimeSpan.Zero);
        var three = new TokenUsage(10, 5, 15, 1, TimeSpan.Zero, TimeSpan.Zero, 3);
        var five = new TokenUsage(10, 5, 15, 1, TimeSpan.Zero, TimeSpan.Zero, 5);

        Assert.Null((none + none).Reasoning);
        Assert.Equal(5, (none + five).Reasoning);   // one report: its count, never a dash
        Assert.Equal(5, (five + none).Reasoning);
        Assert.Equal(8, (three + five).Reasoning);
        Assert.Equal(3, (TokenUsage.Zero + three).Reasoning);
    }

    [Fact]
    public void TokensPerSecond_IsCompletionTokensOverTheStreamingTime()
    {
        Assert.Equal(50, new TokenUsage(0, 100, 100, 1, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(2)).TokensPerSecond);
        Assert.Null(new TokenUsage(100, 0, 100, 1, TimeSpan.Zero, TimeSpan.FromSeconds(2)).TokensPerSecond);   // nothing generated
        Assert.Null(new TokenUsage(0, 100, 100, 1, TimeSpan.FromSeconds(1), TimeSpan.Zero).TokensPerSecond);   // no time passed: no division
    }

    /// <summary>2026-10-26, the user's report of over 100,000 tok/s: a burst's span is not believed.</summary>
    [Fact]
    public void Judged_LeavesABurstUntimed_AndKeepsAQuickRealReply()
    {
        // A tool call sent whole: 400 tokens over 5 ms.
        var burst = new TokenUsage(1_000, 400, 1_400, 1, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(5)).Judged();
        Assert.Equal(new TokenUsage(1_000, 400, 1_400, 1, TimeSpan.FromSeconds(2), TimeSpan.Zero, UntimedOutput: 400), burst);
        Assert.Null(burst.TokensPerSecond);

        // 100 ms or more is believed: a short real reply keeps its figure.
        var quick = new TokenUsage(1_000, 20, 1_020, 1, TimeSpan.FromSeconds(2), TokenUsage.MinDecodeSpan);
        Assert.Equal(quick, quick.Judged());
        Assert.Equal(200, quick.Judged().TokensPerSecond!.Value, 6);

        // Over the ceiling is a burst too, however long the span.
        var tooFast = new TokenUsage(1_000, 6_000, 7_000, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Equal(6_000, tooFast.Judged().UntimedOutput);
        var fast = new TokenUsage(1_000, 5_000, 6_000, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Equal(fast, fast.Judged());

        // Nothing generated, or nothing counted: nothing to judge.
        var silent = new TokenUsage(1_000, 0, 1_000, 1, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        Assert.Equal(silent, silent.Judged());
        Assert.Equal(TokenUsage.Zero, TokenUsage.Zero.Judged());
    }

    [Fact]
    public void ASum_OfATimedAndAnUntimedRequest_ReadsTheTimedOnesSpeed()
    {
        var timed = new TokenUsage(1_000, 100, 1_100, 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)).Judged();
        var untimed = new TokenUsage(1_100, 400, 1_500, 1, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(3)).Judged();

        var reply = timed + untimed;
        Assert.Equal(500, reply.Output);
        Assert.Equal(400, reply.UntimedOutput);
        Assert.Equal(50, reply.TokensPerSecond);
        Assert.Null((untimed + untimed).TokensPerSecond);

        // The default leaves the old figures alone.
        Assert.Equal(0, new TokenUsage(0, 100, 100, 1, TimeSpan.Zero, TimeSpan.FromSeconds(2)).UntimedOutput);
    }

    [Fact]
    public void AverageToFirstToken_IsTheWaitPerRequest_NullWhenNothingCounted()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), new TokenUsage(0, 100, 100, 3, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(2)).AverageToFirstToken);
        Assert.Equal(TimeSpan.FromMilliseconds(500), new TokenUsage(0, 100, 100, 1, TimeSpan.FromMilliseconds(500), TimeSpan.Zero).AverageToFirstToken);
        Assert.Null(TokenUsage.Zero.AverageToFirstToken);
    }
}
