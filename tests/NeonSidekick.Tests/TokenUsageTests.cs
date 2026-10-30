using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;

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
        // A tool call sent whole: one update showed.
        var burst = new TokenUsage(1_000, 400, 1_400, 1, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(5)).Judged(shown: 1);
        Assert.Equal(new TokenUsage(1_000, 400, 1_400, 1, TimeSpan.FromSeconds(2), TimeSpan.Zero, UntimedOutput: 400), burst);
        Assert.Null(burst.TokensPerSecond);

        // A short real answer keeps its speed however quick (the review's R12): six tokens streamed over 50 ms.
        var quick = new TokenUsage(1_000, 6, 1_006, 1, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(50));
        Assert.Equal(quick, quick.Judged(shown: 6));
        Assert.Equal(120, quick.Judged(shown: 6).TokensPerSecond!.Value, 6);

        // Over the ceiling is a burst too, however long the span.
        var tooFast = new TokenUsage(1_000, 6_000, 7_000, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Equal(6_000, tooFast.Judged(shown: 50).UntimedOutput);
        var fast = new TokenUsage(1_000, 5_000, 6_000, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Equal(fast, fast.Judged(shown: 50));

        // Nothing generated, or nothing counted: nothing to judge.
        var silent = new TokenUsage(1_000, 0, 1_000, 1, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        Assert.Equal(silent, silent.Judged(shown: 0));
        Assert.Equal(TokenUsage.Zero, TokenUsage.Zero.Judged(shown: 0));
    }

    /// <summary>2026-10-26, the review of 6d71509d: the clock behind every request's spans.</summary>
    [Fact]
    public void StreamClock_TimesFromTheFirstShowingUpdate_ToTheLast_NotTheTrailingReport()
    {
        var time = new ManualTimeProvider();
        var clock = new StreamClock(time);
        time.Advance(TimeSpan.FromMilliseconds(700));
        clock.Observe(FakeChatClient.Text("a"));
        time.Advance(TimeSpan.FromSeconds(1));
        clock.Observe(FakeChatClient.Text("b"));
        time.Advance(TimeSpan.FromSeconds(3));   // the report comes late (R34)
        clock.Observe(FakeChatClient.Usage(10, 30));
        clock.End();

        var usage = clock.Stamp(new TokenUsage(10, 30, 40, 1, TimeSpan.Zero, TimeSpan.Zero));

        Assert.Equal(TimeSpan.FromMilliseconds(700), usage.ToFirstToken);
        Assert.Equal(TimeSpan.FromSeconds(1), usage.Generating);
        Assert.Equal(30, usage.TokensPerSecond);
        Assert.Equal(TokenUsage.Zero, clock.Stamp(TokenUsage.Zero));
    }

    [Fact]
    public void StreamClock_AnUnmarkedBurst_HasNoKnownWait_AMarkedOneKeepsIt()
    {
        var time = new ManualTimeProvider();
        var burst = new StreamClock(time);
        time.Advance(TimeSpan.FromSeconds(2));
        burst.Observe(FakeChatClient.Call("c1", "echo"));
        var unknown = burst.Stamp(new TokenUsage(10, 300, 310, 1, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, unknown.ToFirstToken);
        Assert.Equal(1, unknown.UnknownWaits);
        Assert.True(unknown.WaitUnknown);
        Assert.Null(unknown.AverageToFirstToken);
        Assert.Equal("first token after ?, streamed untimed", Assistant.SpansLogText(unknown));

        var marked = new StreamClock(time);
        time.Advance(TimeSpan.FromSeconds(1));
        marked.Observe(FakeChatClient.Progress());
        time.Advance(TimeSpan.FromSeconds(2));
        marked.Observe(FakeChatClient.Call("c2", "echo"));
        var kept = marked.Stamp(new TokenUsage(10, 40, 50, 1, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(1), kept.ToFirstToken);
        Assert.Equal(0, kept.UnknownWaits);
        Assert.Equal(20, kept.TokensPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(1), (unknown + kept).AverageToFirstToken);   // the known wait alone (R37)
        Assert.Equal("first token after 1.00s, streamed 2.00s", Assistant.SpansLogText(kept));
    }

    [Fact]
    public void StreamClock_ReportedReasoning_NeverSeenStreaming_IsUntimed()
    {
        // A server that thinks unseen and counts it (R28): the answer's tokens alone are over the span.
        var time = new ManualTimeProvider();
        var clock = new StreamClock(time);
        clock.Observe(FakeChatClient.Text("a"));
        time.Advance(TimeSpan.FromSeconds(1));
        clock.Observe(FakeChatClient.Text("b"));
        var usage = clock.Stamp(new TokenUsage(10, 520, 530, 1, TimeSpan.Zero, TimeSpan.Zero, Reasoning: 500));
        Assert.Equal(500, usage.UntimedOutput);
        Assert.Equal(20, usage.TokensPerSecond);

        // Thinking seen streaming is timed with the rest.
        var seen = new StreamClock(time);
        seen.Observe(new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("hmm")]));
        time.Advance(TimeSpan.FromSeconds(1));
        seen.Observe(FakeChatClient.Text("b"));
        Assert.Equal(0, seen.Stamp(new TokenUsage(10, 20, 30, 1, TimeSpan.Zero, TimeSpan.Zero, Reasoning: 10)).UntimedOutput);
    }

    [Fact]
    public void StreamClock_AMarkerIsTheServerAnswering_ButNoTokenForTheMeter()
    {
        var time = new ManualTimeProvider();
        var meter = new StreamMeter(time);
        meter.Begin();
        var clock = new StreamClock(time, meter);
        Assert.Null(clock.First);
        clock.Observe(FakeChatClient.Progress());
        Assert.NotNull(clock.First);   // the dropped-request check reads it as taken (R15)
        Assert.Equal(0, meter.Read().Chunks);   // R19
        clock.Observe(FakeChatClient.Text("a"));
        Assert.Equal(1, meter.Read().Chunks);
    }

    [Fact]
    public void ASum_OfATimedAndAnUntimedRequest_ReadsTheTimedOnesSpeed()
    {
        var timed = new TokenUsage(1_000, 100, 1_100, 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)).Judged(shown: 100);
        var untimed = new TokenUsage(1_100, 400, 1_500, 1, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(3)).Judged(shown: 1);

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

    /// <summary>A call's arguments are the server answering (2026-10-29): their chunks carry no content, yet the dropped-request check must see them.</summary>
    [Fact]
    public void StreamClock_ACallsArgumentChunk_IsTheServerAnswering()
    {
        var time = new ManualTimeProvider();
        var clock = new StreamClock(time);
        clock.Observe(new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>()));
        Assert.Null(clock.First);

        time.Advance(TimeSpan.FromSeconds(1));
        clock.Observe(FakeChatClient.ArgumentDelta(0, "c1", "echo", "{"));
        Assert.Equal(time.GetTimestamp(), clock.First);
    }
}
