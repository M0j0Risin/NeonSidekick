using NeonSidekick.App;
using NeonSidekick.Llm;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class UsageTextTests
{
    /// <summary>A small window, so the filled tally's last request (1,240 tokens) is a readable share of it.</summary>
    private static readonly ContextLength Window = new(4_096, ContextLengthProbe.MaxModelLenSource);

    private static TokenUsage Usage(long input, long output, int requests, double waitSeconds, double generatingSeconds, long? reasoning = null) =>
        new(input, output, input + output, requests, TimeSpan.FromSeconds(waitSeconds), TimeSpan.FromSeconds(generatingSeconds), reasoning);

    /// <summary>
    /// Three replies, the first before a /clear: the conversation and the session differ. Only the
    /// last reply's server counted its thinking, so the summed scopes carry that one report.
    /// </summary>
    private static TokenTally Filled()
    {
        var tally = new TokenTally();
        tally.BeginTurn();
        tally.Add(Usage(16_800, 832, 12, 5, 20.8));
        tally.EndTurn();
        tally.ResetConversation();
        tally.BeginTurn();
        tally.Add(Usage(10_798, 442, 7, 4, 11.5));
        tally.EndTurn();
        tally.BeginTurn();
        tally.Add(Usage(1_102, 138, 2, 0.8, 3, reasoning: 120));
        tally.EndTurn();
        return tally;
    }

    [Theory]
    [InlineData(0, "0 tokens")]
    [InlineData(999, "999 tokens")]
    [InlineData(1_000, "1.0k tokens")]
    [InlineData(12_480, "12.5k tokens")]
    [InlineData(999_499, "999.5k tokens")]
    [InlineData(1_234_567, "1.2M tokens")]
    public void Compact_IsPinned(long tokens, string expected)
    {
        Assert.Equal(expected, UsageText.Compact(tokens));
        Assert.Equal(expected[..^" tokens".Length], UsageText.CompactNumber(tokens));
    }

    [Fact]
    public void SpeedAndSeconds_ArePinned()
    {
        Assert.Equal("42.3 tok/s", UsageText.Speed(42.34, 1));
        Assert.Equal("42 tok/s", UsageText.Speed(42.34, 0));
        Assert.Equal("1234.6 tok/s", UsageText.Speed(1234.56, 1));
        Assert.Equal("0.8 s", UsageText.Seconds(TimeSpan.FromMilliseconds(800)));
        Assert.Equal("12.3 s", UsageText.Seconds(TimeSpan.FromMilliseconds(12_345)));
        Assert.Equal("0.0 s", UsageText.Seconds(TimeSpan.Zero));
    }

    [Fact]
    public void Percent_IsTheRoundedShare_NullWithoutAWindowOrAnythingInUse()
    {
        Assert.Null(UsageText.Percent(1_240, null));
        Assert.Null(UsageText.Percent(0, Window));
        Assert.Null(UsageText.Percent(100, new ContextLength(0, "x")));
        Assert.Equal(30, UsageText.Percent(1_240, Window));
        Assert.Equal(1, UsageText.Percent(50, Window));
        Assert.Equal(0, UsageText.Percent(1, Window));
        Assert.Equal(110, UsageText.Percent(4_500, Window));   // past a server-side trim: shown as is
        Assert.Equal("38%", UsageText.PercentText(38));
    }

    [Fact]
    public void HintPart_IsTheContextInUse_OverTheWindow_ItsShare_AndTheLastReplySpeed()
    {
        Assert.Null(UsageText.HintPart(new TokenTally(), Window));

        // One base throughout: the last request's 1,240 tokens, never the conversation's 12,480 sum.
        Assert.Equal("1.2k / 4.1k · 30% · 46 tok/s", UsageText.HintPart(Filled(), Window));
        Assert.Equal("1.2k tokens · 46 tok/s", UsageText.HintPart(Filled(), null));   // no window: the count keeps its unit
        Assert.Equal("4.6k / 151.4k · 3% · 136 tok/s", UsageText.HintPart(WithLast(4_604), new ContextLength(151_427, "x")));   // the live SGLang figures

        // The last reply said nothing: the last request still stands, without the speed.
        var tally = Filled();
        tally.BeginTurn();
        tally.EndTurn();
        Assert.Equal("1.2k / 4.1k · 30%", UsageText.HintPart(tally, Window));
        Assert.Equal("1.2k tokens", UsageText.HintPart(tally, null));

        // A cleared conversation shows nothing until the next reply, whatever the session holds.
        tally.ResetConversation();
        Assert.Null(UsageText.HintPart(tally, Window));
        Assert.Null(UsageText.HintPart(tally, null));
    }

    /// <summary>The filled tally with one more reply whose request totals <paramref name="total"/> tokens.</summary>
    private static TokenTally WithLast(long total)
    {
        var tally = Filled();
        tally.BeginTurn();
        tally.Add(Usage(total - 150, 150, 1, 0.5, 1.1));
        tally.EndTurn();
        return tally;
    }

    [Fact]
    public void ContextLine_IsPinned_InItsFourShapes()
    {
        Assert.Equal("Context: window unknown", UsageText.ContextLine(new TokenTally(), null));
        Assert.Equal("Context: window 4,096 tokens (max_model_len on /v1/models); nothing sent yet", UsageText.ContextLine(new TokenTally(), Window));
        Assert.Equal("Context: 1,240 tokens; window unknown", UsageText.ContextLine(Filled(), null));
        Assert.Equal("Context: 1,240 of 4,096 tokens (30%, max_model_len on /v1/models)", UsageText.ContextLine(Filled(), Window));
        Assert.Equal("Context: 1,240 of 32,768 tokens (4%, configured)", UsageText.ContextLine(Filled(), ContextLength.Configured(32_768)));
    }

    [Fact]
    public void Lines_ArePinned()
    {
        Assert.Equal(["(no tokens counted yet)"], UsageText.Lines(new TokenTally(), null));
        Assert.Equal(
        [
            "Context: window 4,096 tokens (max_model_len on /v1/models); nothing sent yet",
            "(no tokens counted yet)",
        ], UsageText.Lines(new TokenTally(), Window));

        Assert.Equal(
        [
            "Context: 1,240 of 4,096 tokens (30%, max_model_len on /v1/models)",
            "Tokens — last reply: 1,240 (1,102 in, 138 out, 120 reasoning, 2 requests) · 0.8 s to first token · 46.0 tok/s",
            // The aggregates' wait is per request (4.8 s over 9, 9.8 s over 21), their speed over every request's streaming.
            "Tokens — this conversation (every request summed): 12,480 (11,900 in, 580 out, 120 reasoning, 9 requests) · 0.5 s to first token · 40.0 tok/s",
            "Tokens — since launch (every request summed): 30,112 (28,700 in, 1,412 out, 120 reasoning, 21 requests) · 0.5 s to first token · 40.0 tok/s",
        ], UsageText.Lines(Filled(), Window));
    }

    [Fact]
    public void Lines_NameTheRepliesWithoutAReport()
    {
        var tally = new TokenTally();
        tally.BeginTurn();
        tally.Add(Usage(100, 20, 1, 0.5, 1));
        tally.EndTurn();
        tally.BeginTurn();
        tally.EndTurn();

        Assert.Equal(
        [
            "Context: 120 tokens; window unknown",
            "(last reply: no usage reported)",
            "Tokens — this conversation (every request summed): 120 (100 in, 20 out, 1 request) · 0.5 s to first token · 20.0 tok/s",
            "Tokens — since launch (every request summed): 120 (100 in, 20 out, 1 request) · 0.5 s to first token · 20.0 tok/s",
            "(1 reply reported no usage)",
        ], UsageText.Lines(tally, null));

        tally.BeginTurn();
        tally.EndTurn();
        tally.BeginTurn();
        tally.EndTurn();
        Assert.Equal("(3 replies reported no usage)", UsageText.Lines(tally, null)[^1]);
    }

    [Fact]
    public void ASkillLearningReflection_IsARowUnderTheLaunchSection_OnlyOnceOneRan()
    {
        var tally = new TokenTally();
        tally.BeginTurn();
        tally.Add(Usage(100, 20, 1, 0.5, 1));
        tally.EndTurn();
        Assert.DoesNotContain(UsageText.Lines(tally, null), l => l.StartsWith("skill learning", StringComparison.Ordinal));

        tally.AddLearning(Usage(3000, 120, 2, 0.5, 1), requests: 2);

        Assert.Equal("2 requests · 3,120 tokens", UsageText.LearningValue(tally));
        Assert.Equal("skill learning: 2 requests · 3,120 tokens", UsageText.Lines(tally, null)[^1]);
        Assert.StartsWith("Tokens — since launch (every request summed): 3,240 (3,100 in, 140 out, 3 requests)", UsageText.Lines(tally, null)[3], StringComparison.Ordinal);

        using var console = new TestConsole();
        console.Profile.Width = 2000;
        console.Write(UsageText.TokensTab(tally, null));
        string[] lines = Lines(console);
        Assert.Equal("Skill learning       2 requests · 3,120 tokens", lines[^2]);   // under the launch section's blank row

        var one = new TokenTally();
        one.AddLearning(TokenUsage.Zero, requests: 1);
        Assert.Equal("1 request · 0 tokens", UsageText.LearningValue(one));
    }

    [Fact]
    public void Rows_ArePinned()
    {
        Assert.Equal(
        [
            ("Total", "1,240"), ("Prompt", "1,102"), ("Completion", "138"), ("Reasoning", "120"), ("Requests", "2"),
            ("Time to first token", "0.8 s"), ("Speed", "46.0 tok/s"),
        ], UsageText.Rows(Usage(1_102, 138, 2, 0.8, 3, reasoning: 120), averaged: false));

        // An aggregate's wait is per request (2026-09-17): 3 s over 4 requests; no speed when nothing streamed.
        Assert.Equal(
        [
            ("Total", "100"), ("Prompt", "100"), ("Completion", "0"), ("Reasoning", "—"), ("Requests", "4"),
            ("Time to first token", "0.8 s"),
        ], UsageText.Rows(Usage(100, 0, 4, 3, 0), averaged: true));

        // The Reasoning row is always there: a dash when no request reported a count, 0 when thinking was off.
        Assert.Equal(
        [
            ("Total", "100"), ("Prompt", "100"), ("Completion", "0"), ("Reasoning", "—"), ("Requests", "1"),
            ("Time to first token", "0.5 s"),
        ], UsageText.Rows(Usage(100, 0, 1, 0.5, 0), averaged: true));
        Assert.Contains(("Reasoning", "0"), UsageText.Rows(Usage(100, 4, 1, 0.5, 1, reasoning: 0), averaged: true));

        // An aggregate with nothing counted (the conversation after /clear) has no wait to average.
        Assert.DoesNotContain(UsageText.Rows(TokenUsage.Zero, averaged: true), row => row.Label == "Time to first token");
    }

    [Fact]
    public void ContextRows_ArePinned()
    {
        Assert.Equal([("Window", "unknown")], UsageText.ContextRows(new TokenTally(), null));
        Assert.Equal([("Window", "4,096")], UsageText.ContextRows(new TokenTally(), Window));
        Assert.Equal([("Window", "unknown"), ("In use", "1,240")], UsageText.ContextRows(Filled(), null));
        Assert.Equal([("Window", "4,096"), ("In use", "1,240"), ("Used", "30%")], UsageText.ContextRows(Filled(), Window));

        // /clear: the history went, so did the context in use; the window stays.
        var cleared = Filled();
        cleared.ResetConversation();
        Assert.Equal([("Window", "4,096")], UsageText.ContextRows(cleared, Window));
    }

    [Fact]
    public void TokensTab_RendersAHeadingPerScope_OverItsRows()
    {
        using var console = new TestConsole();
        console.Profile.Width = 2000;
        console.Write(UsageText.TokensTab(Filled(), Window));

        string[] lines = Lines(console);
        // The Context section first, its source dim in the value column.
        Assert.Equal("Context              max_model_len on /v1/models", lines[0]);
        Assert.Equal("Window               4,096", lines[1]);
        Assert.Equal("In use               1,240", lines[2]);
        Assert.Equal("Used                 30%", lines[3]);
        Assert.Equal("", lines[4]);
        Assert.Equal(UsageText.LastReplyHeading, lines[5]);
        Assert.Equal("Total                1,240", lines[6]);
        Assert.Equal("Prompt               1,102", lines[7]);
        Assert.Equal("Completion           138", lines[8]);
        Assert.Equal("Reasoning            120", lines[9]);
        Assert.Equal("Requests             2", lines[10]);
        Assert.Equal("Time to first token  0.8 s", lines[11]);
        Assert.Equal("Speed                46.0 tok/s", lines[12]);
        Assert.Equal("", lines[13]);
        // One grid for the four sections: the value column stays where the widest label put it.
        Assert.Equal("This conversation    every request summed", lines[14]);
        Assert.Equal("Total                12,480", lines[15]);
        Assert.Equal("Reasoning            120", lines[18]);   // the one request that reported a count
        Assert.Equal("Time to first token  0.5 s", lines[20]);   // per request: 4.8 s over 9
        Assert.Equal("Speed                40.0 tok/s", lines[21]);
        Assert.Equal("", lines[22]);
        Assert.Equal("Since launch         every request summed", lines[23]);
        Assert.Equal("Total                30,112", lines[24]);
        Assert.Equal("Requests             21", lines[28]);
        Assert.Equal("Time to first token  0.5 s", lines[29]);   // 9.8 s over 21
        Assert.Equal("Speed                40.0 tok/s", lines[30]);
        Assert.Equal("", lines[31]);
    }

    [Fact]
    public void TokensTab_NamesTheRepliesWithoutAReport_AndTheEmptyTally()
    {
        using var empty = new TestConsole();
        empty.Write(UsageText.TokensTab(new TokenTally(), null));
        Assert.Equal(UsageText.NothingCounted, empty.Output);

        // A known window before the first reply: the Context section, then the empty note.
        using var known = new TestConsole();
        known.Profile.Width = 2000;
        known.Write(UsageText.TokensTab(new TokenTally(), Window));
        string[] before = Lines(known);
        Assert.Equal("Context                  max_model_len on /v1/models", before[0]);
        Assert.Equal("Window                   4,096", before[1]);
        Assert.Equal("", before[2]);
        Assert.Equal(UsageText.NothingCounted, before[3]);

        var tally = new TokenTally();
        tally.BeginTurn();
        tally.Add(Usage(100, 20, 1, 0.5, 1));
        tally.EndTurn();
        tally.BeginTurn();
        tally.EndTurn();

        using var console = new TestConsole();
        console.Profile.Width = 2000;
        console.Write(UsageText.TokensTab(tally, null));

        string[] lines = Lines(console);
        // The aggregates carry the Time to first token row now (2026-09-17), the widest label of the grid.
        Assert.Equal("Context              unknown", lines[0]);
        Assert.Equal("Window               unknown", lines[1]);
        Assert.Equal("In use               120", lines[2]);
        Assert.Equal("", lines[3]);
        // The notes sit in the value column, so a long one never widens the label column.
        Assert.Equal("Last reply           " + UsageText.NoReportNote, lines[4]);
        Assert.Equal("", lines[5]);   // no rows under an empty scope
        Assert.Equal("This conversation    every request summed; 1 reply reported no usage", lines[6]);
        Assert.Equal("Total                120", lines[7]);
        Assert.Equal("Reasoning            —", lines[10]);   // no request counted its thinking: the row stays, with a dash
        Assert.Equal("Time to first token  0.5 s", lines[12]);
    }

    /// <summary>The rendered rows with the grid's column padding trimmed off.</summary>
    private static string[] Lines(TestConsole console) => console.Output.Split('\n').Select(l => l.TrimEnd()).ToArray();

    [Fact]
    public void NotesTab_IsEveryNote_ABlankRowBetween()
    {
        using var console = new TestConsole();
        console.Profile.Width = 2000;
        console.Write(UsageText.NotesTab());

        string[] lines = console.Output.Split('\n');
        Assert.Equal(UsageText.Notes.Count * 2 + 1, lines.Length);
        for (int i = 0; i < UsageText.Notes.Count; i++)
        {
            Assert.Equal(UsageText.Notes[i], lines[i * 2]);
            Assert.Equal(" ", lines[i * 2 + 1]);
        }

        Assert.Equal(9, UsageText.Notes.Count);
        Assert.StartsWith("After a compact (/compact, or LLM auto compact (%)) the context in use is measured again at the next reply", UsageText.Notes[8]);
        Assert.StartsWith("The counts are the server's own usage report", UsageText.Notes[0]);
        Assert.Contains("the last request's prompt plus its completion is the context in use", UsageText.Notes[1]);
        Assert.Contains("the conversation and launch totals sum every request", UsageText.Notes[1]);
        Assert.Contains("thinking included", UsageText.Notes[2]);
        Assert.Contains("A picture in the conversation is tokenised by the server and counted in the prompt figure", UsageText.Notes[2]);
        Assert.StartsWith("Reasoning is the thinking's share of the completion", UsageText.Notes[3]);
        Assert.Contains("SGLang at the top of usage", UsageText.Notes[3]);
        Assert.Contains("Ollama counts none", UsageText.Notes[3]);
        Assert.Contains("0 is a report too", UsageText.Notes[3]);
        Assert.Contains("the prefill", UsageText.Notes[4]);
        Assert.Contains("the wait per request", UsageText.Notes[4]);
        Assert.Contains("max_model_len or context_length on /v1/models", UsageText.Notes[5]);
        Assert.Contains("NEONSIDEKICK_LLM_CONTEXT", UsageText.Notes[5]);
        Assert.Contains("never as zeros", UsageText.Notes[6]);
        Assert.Contains("Nothing is saved", UsageText.Notes[7]);
    }

    [Fact]
    public void Lines_DropTheSpeed_WhenNothingWasGenerated()
    {
        var tally = new TokenTally();
        tally.BeginTurn();
        tally.Add(Usage(100, 0, 1, 0.5, 0));
        tally.EndTurn();

        Assert.Equal(
        [
            "Context: 100 tokens; window unknown",
            "Tokens — last reply: 100 (100 in, 0 out, 1 request) · 0.5 s to first token",
            "Tokens — this conversation (every request summed): 100 (100 in, 0 out, 1 request) · 0.5 s to first token",
            "Tokens — since launch (every request summed): 100 (100 in, 0 out, 1 request) · 0.5 s to first token",
        ], UsageText.Lines(tally, null));
    }

    [Fact]
    public void BusyHintPart_LastKnown_IsTheHintPartShape_WithTheSpeedCarriedIntoTheNextTurn()
    {
        var streaming = new StreamMeter.Reading(true, 500, 40);
        Assert.Null(UsageText.BusyHintPart(new TokenTally(), Window, MidTurnUsage.LastKnown, streaming));

        var tally = Filled();
        Assert.Equal("1.2k / 4.1k · 30% · 46 tok/s", UsageText.BusyHintPart(tally, Window, MidTurnUsage.LastKnown, streaming));

        // The next turn has begun: the idle part would lose the speed, the busy one keeps the last known.
        tally.BeginTurn();
        Assert.Equal("1.2k / 4.1k · 30%", UsageText.HintPart(tally, Window));
        Assert.Equal("1.2k / 4.1k · 30% · 46 tok/s", UsageText.BusyHintPart(tally, Window, MidTurnUsage.LastKnown, streaming));
        Assert.Equal("1.2k tokens · 46 tok/s", UsageText.BusyHintPart(tally, null, MidTurnUsage.LastKnown, streaming));
    }

    [Fact]
    public void BusyHintPart_Estimate_AddsTheStreamedChunks_Marked_AndFallsBackOutsideAStream()
    {
        var tally = Filled();

        // The last request's 1,240 plus 500 chunks, at the chunks' rate; every figure marked.
        Assert.Equal("~1.7k / 4.1k · ~42% · ~40 tok/s", UsageText.BusyHintPart(tally, Window, MidTurnUsage.Estimate, new StreamMeter.Reading(true, 500, 40)));
        Assert.Equal("~1.7k tokens · ~40 tok/s", UsageText.BusyHintPart(tally, null, MidTurnUsage.Estimate, new StreamMeter.Reading(true, 500, 40)));
        // One chunk in, no rate yet: the context alone.
        Assert.Equal("~1.2k / 4.1k · ~30%", UsageText.BusyHintPart(tally, Window, MidTurnUsage.Estimate, new StreamMeter.Reading(true, 1, null)));
        // The very first request of a conversation estimates from nothing.
        Assert.Equal("~12 tokens · ~6 tok/s", UsageText.BusyHintPart(new TokenTally(), null, MidTurnUsage.Estimate, new StreamMeter.Reading(true, 12, 6)));

        // The prefill (no chunk yet) and between requests: the last known figures, unmarked.
        Assert.Equal("1.2k / 4.1k · 30% · 46 tok/s", UsageText.BusyHintPart(tally, Window, MidTurnUsage.Estimate, new StreamMeter.Reading(true, 0, null)));
        Assert.Equal("1.2k / 4.1k · 30% · 46 tok/s", UsageText.BusyHintPart(tally, Window, MidTurnUsage.Estimate, StreamMeter.Reading.Idle));
        Assert.Null(UsageText.BusyHintPart(new TokenTally(), Window, MidTurnUsage.Estimate, StreamMeter.Reading.Idle));
    }
}
