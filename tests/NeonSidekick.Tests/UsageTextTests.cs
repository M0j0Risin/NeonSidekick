using NeonSidekick.App;
using NeonSidekick.UI;
using NeonSidekick.Llm;
using NeonSidekick.Sessions;
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

        // The last reply said nothing: the last request still stands, with the last known speed (2026-10-26, the user's choice).
        var tally = Filled();
        tally.BeginTurn();
        tally.EndTurn();
        Assert.Equal("1.2k / 4.1k · 30% · 46 tok/s", UsageText.HintPart(tally, Window));
        Assert.Equal("1.2k tokens · 46 tok/s", UsageText.HintPart(tally, null));

        // A reply of untimed requests alone (a tool call sent whole) keeps the reply before's speed too.
        tally.BeginTurn();
        tally.Add(new TokenUsage(1_300, 200, 1_500, 1, TimeSpan.FromSeconds(0.5), TimeSpan.FromMilliseconds(4)).Judged(shown: 1));
        Assert.Equal("1.5k / 4.1k · 37% · 46 tok/s", UsageText.HintPart(tally, Window));
        Assert.DoesNotContain("tok/s", UsageText.Lines(tally)[0], StringComparison.Ordinal);   // /usage's last reply stays exact: no speed

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
    public void Lines_ArePinned()
    {
        // The context line left them (2026-11-27): ContextLines' headline covers it.
        Assert.Equal([UsageText.NothingCounted], UsageText.Lines(new TokenTally()));

        Assert.Equal(
        [
            "Tokens — last reply: 1,240 (1,102 in, 138 out, 120 reasoning, 2 requests) · 0.8 s to first token · 46.0 tok/s",
            // The aggregates' wait is per request (4.8 s over 9, 9.8 s over 21), their speed over every request's streaming.
            "Tokens — this conversation (every request summed): 12,480 (11,900 in, 580 out, 120 reasoning, 9 requests) · 0.5 s to first token · 40.0 tok/s",
            "Tokens — since launch (every request summed): 30,112 (28,700 in, 1,412 out, 120 reasoning, 21 requests) · 0.5 s to first token · 40.0 tok/s",
        ], UsageText.Lines(Filled()));
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
            "(last reply: no usage reported)",
            "Tokens — this conversation (every request summed): 120 (100 in, 20 out, 1 request) · 0.5 s to first token · 20.0 tok/s",
            "Tokens — since launch (every request summed): 120 (100 in, 20 out, 1 request) · 0.5 s to first token · 20.0 tok/s",
            "(1 reply reported no usage)",
        ], UsageText.Lines(tally));

        tally.BeginTurn();
        tally.EndTurn();
        tally.BeginTurn();
        tally.EndTurn();
        Assert.Equal("(3 replies reported no usage)", UsageText.Lines(tally)[^1]);
    }

    [Fact]
    public void ASkillLearningReflection_IsARowUnderTheLaunchSection_OnlyOnceOneRan()
    {
        var tally = new TokenTally();
        tally.BeginTurn();
        tally.Add(Usage(100, 20, 1, 0.5, 1));
        tally.EndTurn();
        Assert.DoesNotContain(UsageText.Lines(tally), l => l.StartsWith("skill learning", StringComparison.Ordinal));

        tally.AddLearning(Usage(3000, 120, 2, 0.5, 1), requests: 2);

        Assert.Equal("2 requests · 3,120 tokens", UsageText.LearningValue(tally));
        Assert.Equal("skill learning: 2 requests · 3,120 tokens", UsageText.Lines(tally)[^1]);
        Assert.StartsWith("Tokens — since launch (every request summed): 3,240 (3,100 in, 140 out, 3 requests)", UsageText.Lines(tally)[2], StringComparison.Ordinal);

        string[] lines = Tab(tally);
        // The Other runs section's row (2026-11-27; the launch section's until then), the tab's last.
        Assert.Equal(Head(UsageText.OtherRunsHeading, width: 100), lines[^3]);
        Assert.Equal("  " + UsageText.LearningLabel + new string(' ', SlashCommands.HelpColumnGap) + "2 requests · 3,120 tokens", lines.Last(l => l.Length > 0));

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

    /// <summary>A section's rule as the tab draws it at <paramref name="width"/> (2026-10-10, the user's ask): the label, its note after a run of rule, the rule to the edge.</summary>
    private static string Head(string label, string note = "", int width = 80)
    {
        string text = "── " + label + (note.Length > 0 ? " ── " + note : "");
        return text + " " + new string(ScreenPane.RuleGlyph, width - TextCells.Width(text) - 1);
    }

    /// <summary>
    /// The Tokens section (2026-11-27, plan C4): one table of the three scopes, matched by label, each scope's value right-aligned under its head,
    /// one label width.
    /// </summary>
    [Fact]
    public void TheTokensTable_HasAColumnPerScope_ARowPerLabel()
    {
        var tally = Filled();
        string[] lines = Tab(tally);
        int rule = Array.IndexOf(lines, Head(UsageText.TokensHeading, width: 100));
        Assert.True(rule > 0);

        string heads = lines[rule + 1];
        int last = heads.IndexOf(UsageText.LastReplyColumn, StringComparison.Ordinal);
        int conversation = heads.IndexOf(UsageText.ConversationColumn, StringComparison.Ordinal);
        int launch = heads.IndexOf(UsageText.SinceLaunchColumn, StringComparison.Ordinal);
        Assert.True(last > 0 && conversation > last && launch > conversation, heads);

        var labels = UsageText.Rows(tally.Session, averaged: true).Select(r => r.Label).ToList();
        for (int i = 0; i < labels.Count; i++)
        {
            string row = lines[rule + 2 + i];
            Assert.StartsWith("  " + labels[i], row, StringComparison.Ordinal);
            string session = UsageText.Rows(tally.Session, averaged: true)[i].Value;
            Assert.EndsWith(session, row, StringComparison.Ordinal);
            Assert.Equal(launch + UsageText.SinceLaunchColumn.Length, row.Length);   // right-aligned under its head
        }

        string total = lines[rule + 2];
        Assert.True(total.IndexOf("1,240", StringComparison.Ordinal) < total.IndexOf("12,480", StringComparison.Ordinal));
        Assert.True(total.IndexOf("12,480", StringComparison.Ordinal) < total.IndexOf("30,112", StringComparison.Ordinal));
    }

    /// <summary>
    /// With nothing counted the top and the breakdown show, then the Tokens rule over <see cref="UsageText.NothingCounted"/>, no Trend or Records
    /// (2026-11-27); an unreported last reply leaves its column blank and says so under the table, as the conversation's count of them.
    /// </summary>
    [Fact]
    public void TheTab_SaysNothingCounted_AndNamesTheRepliesWithoutAReport()
    {
        string[] empty = Tab(new TokenTally());
        int rule = Array.IndexOf(empty, Head(UsageText.TokensHeading, width: 100));
        Assert.True(rule > 0);
        Assert.Equal("  " + UsageText.NothingCounted, empty[rule + 1]);
        Assert.DoesNotContain(empty, l => l.StartsWith("── " + UsageText.TrendHeading, StringComparison.Ordinal));
        Assert.DoesNotContain(empty, l => l.StartsWith("── " + UsageText.RecordsHeading, StringComparison.Ordinal));
        Assert.StartsWith("  " + UsageText.MeterFull, empty[0], StringComparison.Ordinal);   // the meter first, no rule

        var tally = new TokenTally();
        tally.BeginTurn();
        tally.Add(Usage(100, 20, 1, 0.5, 1));
        tally.EndTurn();
        tally.BeginTurn();
        tally.EndTurn();

        string[] lines = Tab(tally);
        Assert.Contains("  " + UsageText.ScopeNote(UsageText.LastReplyColumn, UsageText.NoReportNote), lines);
        Assert.Contains("  " + UsageText.ScopeNote(UsageText.ConversationColumn, "1 reply reported no usage"), lines);
        int tokens = Array.IndexOf(lines, Head(UsageText.TokensHeading, width: 100));
        string heads = lines[tokens + 1];
        string total = lines[tokens + 2];
        int lastEnd = heads.IndexOf(UsageText.LastReplyColumn, StringComparison.Ordinal) + UsageText.LastReplyColumn.Length;
        Assert.True(string.IsNullOrWhiteSpace(total[(lastEnd - UsageText.LastReplyColumn.Length)..lastEnd]), total);   // the last reply's cell blank
        Assert.Contains("  Reasoning", lines.Skip(tokens).First(l => l.StartsWith("  Reasoning", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.EndsWith("—", lines.Skip(tokens).First(l => l.StartsWith("  Reasoning", StringComparison.Ordinal)), StringComparison.Ordinal);   // no request counted its thinking
    }

    /// <summary>The merged tab at <paramref name="width"/> on the shared facts, its lines' padding trimmed.</summary>
    private static string[] Tab(TokenTally tally, IReadOnlyList<ReplyPoint>? replies = null, ModelBests? bests = null, string? model = null, int width = 100)
    {
        using var console = new TestConsole();
        console.Profile.Width = width;
        console.Write(UsageText.StatisticsTab(tally, UsageContextTests.Facts(UsageContextTests.Window), replies ?? tally.Replies, bests ?? ModelBests.None, model, width));
        return Lines(console);
    }

    private static ReplyPoint Point(int number, double? speed = 100, double? first = 0.5, long? context = 1_000, long? output = 200, string? model = "qwen", bool marked = false) =>
        new(model, speed, first is { } s ? TimeSpan.FromSeconds(s) : null, context, output, null, marked, number);

    /// <summary>A measure's cell (2026-11-27): its level between the lowest and highest shown, the middle when all are one, a gap for none.</summary>
    [Fact]
    public void ASparkCell_ScalesBetweenTheLowestAndHighest()
    {
        Assert.Equal('▁', UsageText.SparkCell(10, 10, 20));
        Assert.Equal('█', UsageText.SparkCell(20, 10, 20));
        Assert.Equal('▄', UsageText.SparkCell(5, 5, 5));   // equal values: the middle
        Assert.Equal(' ', UsageText.SparkCell(null, 10, 20));
    }

    /// <summary>The trend (2026-11-27): none under two replies; a line a measure; │ at a model change, ┆ before a compaction; the newest that fit.</summary>
    [Fact]
    public void TheTrend_MarksAModelChangeAndACompaction_AndKeepsTheNewestThatFit()
    {
        Assert.Empty(UsageText.TrendLines([Point(1)]));
        var replies = new List<ReplyPoint> { Point(1, speed: 100), Point(2, speed: 120), Point(3, speed: 90, marked: true), Point(4, speed: 80, model: "other"), Point(5, speed: null) };
        var lines = UsageText.TrendLines(replies);

        Assert.Equal(4, lines.Count);
        Assert.StartsWith(UsageText.TrendSpeedLabel.ToLowerInvariant() + ": ", lines[0], StringComparison.Ordinal);
        string cells = lines[0][(lines[0].IndexOf(": ", StringComparison.Ordinal) + 2)..lines[0].IndexOf(" (", StringComparison.Ordinal)];
        Assert.Equal("▅█" + UsageText.CompactedMark + "▃" + UsageText.ModelChangeMark + "▁" + UsageText.ModelChangeMark + " ", cells);   // back to qwen; a gap for the untimed one
        Assert.EndsWith("(100 → 80 tok/s)", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("(200 · 200 avg)", lines[3], StringComparison.Ordinal);

        // At a narrow width the pane keeps the newest and says how many of how many.
        var many = Enumerable.Range(1, 100).Select(i => Point(i, speed: i)).ToList();
        var tally = new TokenTally();
        tally.BeginTurn();
        tally.Add(Usage(100, 20, 1, 0.5, 1));
        tally.EndTurn();
        string[] tab = Tab(tally, replies: many, width: 60);
        string rule = tab.Single(l => l.StartsWith("── " + UsageText.TrendHeading, StringComparison.Ordinal));
        Assert.Contains("last ", rule, StringComparison.Ordinal);
        Assert.Contains(" of 100", rule, StringComparison.Ordinal);
        Assert.All(tab, l => Assert.True(TextCells.Width(l) <= 60, l));
        Assert.Equal(UsageText.Plural(31, "reply", "replies"), UsageText.TrendCount(31, 31));
    }

    /// <summary>
    /// Records (2026-11-27): this conversation's bests beside the model's and any model's; ★ when the conversation holds the model's best, "★ new"
    /// in its column; the any-model column only when it says something else; two columns when narrow.
    /// </summary>
    [Fact]
    public void TheRecords_StarTheModelsBest_AndShowAnyModelOnlyWhenItDiffers()
    {
        var replies = new List<ReplyPoint> { Point(1, speed: 120, output: 300), Point(2, speed: 187, output: 2_914, first: 6.2, context: 52_000) };
        var mine = new ModelBest("qwen", 150, 6_120, 14_800, 128_000, 1_200_000);
        var bests = new ModelBests(mine, (412, "tiny"), (6_120, "qwen"), (14_800, 128_000, "qwen"), 1_500_000);
        var cells = UsageText.RecordCells(replies, bests, "qwen");

        var fastest = cells.Single(c => c.Label == UsageText.FastestLabel);
        Assert.EndsWith(UsageText.ReplyRef(2) + " " + UsageText.RecordStar, fastest.Conversation, StringComparison.Ordinal);
        Assert.EndsWith(UsageText.NewRecord, fastest.Model, StringComparison.Ordinal);
        Assert.EndsWith("tiny", fastest.Any, StringComparison.Ordinal);
        var longest = cells.Single(c => c.Label == UsageText.LongestLabel);
        Assert.DoesNotContain(UsageText.RecordStar, longest.Conversation, StringComparison.Ordinal);   // 2,914 short of the model's 6,120
        Assert.Contains(UsageText.Pages(3_214), cells.Single(c => c.Label == UsageText.WrittenLabel).Conversation, StringComparison.Ordinal);
        Assert.Equal("≈ 5 pages", UsageText.Pages(3_214));

        var tally = new TokenTally();
        tally.BeginTurn();
        tally.Add(Usage(100, 20, 1, 0.5, 1));
        tally.EndTurn();
        string[] wide = Tab(tally, replies, bests, "qwen", 140);
        string heads = wide[Array.FindIndex(wide, l => l.StartsWith("── " + UsageText.RecordsHeading, StringComparison.Ordinal)) + 1];
        Assert.Contains(UsageText.ModelEverColumn("qwen"), heads, StringComparison.Ordinal);
        Assert.Contains(UsageText.AnyModelColumn, heads, StringComparison.Ordinal);

        // Every best this model's, the written sums one: the any-model column says nothing new.
        var same = new ModelBests(mine, (150, "qwen"), (6_120, "qwen"), (14_800, 128_000, "qwen"), 1_200_000);
        string[] alone = Tab(tally, replies, same, "qwen", 140);
        Assert.DoesNotContain(UsageText.AnyModelColumn, alone[Array.FindIndex(alone, l => l.StartsWith("── " + UsageText.RecordsHeading, StringComparison.Ordinal)) + 1], StringComparison.Ordinal);

        // Narrow: fewer columns, the conversation's kept.
        string[] narrow = Tab(tally, replies, bests, "qwen", 70);
        string narrowHeads = narrow[Array.FindIndex(narrow, l => l.StartsWith("── " + UsageText.RecordsHeading, StringComparison.Ordinal)) + 1];
        Assert.Contains(UsageText.ThisConversationColumn, narrowHeads, StringComparison.Ordinal);
        Assert.DoesNotContain(UsageText.AnyModelColumn, narrowHeads, StringComparison.Ordinal);

        // The plain lines: the all-time parts only with saved figures.
        Assert.Contains(UsageText.ModelEverColumn("qwen"), UsageText.RecordLines(replies, bests, "qwen")[0], StringComparison.Ordinal);
        Assert.DoesNotContain(" ever ", UsageText.RecordLines(replies, ModelBests.None, "qwen")[0], StringComparison.Ordinal);
        Assert.Empty(UsageText.RecordLines([], ModelBests.None, "qwen"));
    }

    /// <summary>The rendered rows with the grid's column padding trimmed off.</summary>
    private static string[] Lines(TestConsole console) => console.Output.Split('\n').Select(l => l.TrimEnd()).ToArray();

    [Fact]
    public void Lines_DropTheSpeed_WhenNothingWasGenerated()
    {
        var tally = new TokenTally();
        tally.BeginTurn();
        tally.Add(Usage(100, 0, 1, 0.5, 0));
        tally.EndTurn();

        Assert.Equal(
        [
            "Tokens — last reply: 100 (100 in, 0 out, 1 request) · 0.5 s to first token",
            "Tokens — this conversation (every request summed): 100 (100 in, 0 out, 1 request) · 0.5 s to first token",
            "Tokens — since launch (every request summed): 100 (100 in, 0 out, 1 request) · 0.5 s to first token",
        ], UsageText.Lines(tally));
    }

    [Fact]
    public void BusyHintPart_LastKnown_IsTheHintPartShape_WithTheSpeedCarriedIntoTheNextTurn()
    {
        var streaming = new StreamMeter.Reading(true, 500, 40);
        Assert.Null(UsageText.BusyHintPart(new TokenTally(), Window, MidTurnUsage.LastKnown, streaming));

        var tally = Filled();
        Assert.Equal("1.2k / 4.1k · 30% · 46 tok/s", UsageText.BusyHintPart(tally, Window, MidTurnUsage.LastKnown, streaming));

        // The next turn has begun: both keep the last known speed (the idle part since 2026-10-26).
        tally.BeginTurn();
        Assert.Equal("1.2k / 4.1k · 30% · 46 tok/s", UsageText.HintPart(tally, Window));
        Assert.Equal("1.2k / 4.1k · 30% · 46 tok/s", UsageText.BusyHintPart(tally, Window, MidTurnUsage.LastKnown, streaming));
        Assert.Equal("1.2k tokens · 46 tok/s", UsageText.BusyHintPart(tally, null, MidTurnUsage.LastKnown, streaming));
    }

    [Fact]
    public void TheParts_AreTheHintRowsFiguresApart_EachMarkedWhenEstimated_AndJoinToTheRow()
    {
        var tally = Filled();

        var idle = UsageText.HintParts(tally, Window)!;
        Assert.Equal(("1.2k / 4.1k", "30%", "46 tok/s"), (idle.Tokens, idle.Percent, idle.Speed));
        Assert.Equal(UsageText.HintPart(tally, Window), idle.Joined);

        var unknown = UsageText.HintParts(tally, null)!;
        Assert.Equal(("1.2k tokens", (string?)null), (unknown.Tokens, unknown.Percent));
        Assert.Equal(UsageText.HintPart(tally, null), unknown.Joined);
        Assert.Null(UsageText.HintParts(new TokenTally(), Window));

        var live = new StreamMeter.Reading(true, 500, 40);
        var estimate = UsageText.BusyHintParts(tally, Window, MidTurnUsage.Estimate, live)!;
        Assert.Equal(("~1.7k / 4.1k", "~42%", "~40 tok/s"), (estimate.Tokens, estimate.Percent, estimate.Speed));
        Assert.Equal(UsageText.BusyHintPart(tally, Window, MidTurnUsage.Estimate, live), estimate.Joined);
        Assert.Equal(UsageText.BusyHintPart(tally, Window, MidTurnUsage.LastKnown, live), UsageText.BusyHintParts(tally, Window, MidTurnUsage.LastKnown, live)!.Joined);
        Assert.Equal("46 tok/s", UsageText.BusyHintParts(tally, Window, MidTurnUsage.Estimate, new StreamMeter.Reading(true, 1, null))!.Speed);   // no rate yet: the last known, unmarked
    }

    [Fact]
    public void ContextPercent_IsTheStatusBarsShare_ZeroWithNothing_NullWithoutAWindow()
    {
        // The performance bar's CTX (2026-11-21, the user's ask): the busy row's share, from the same figure.
        var tally = Filled();
        var idle = StreamMeter.Reading.Idle;
        Assert.Equal(30, UsageText.ContextPercent(tally, Window, MidTurnUsage.LastKnown, idle));
        Assert.Equal(30, UsageText.ContextPercent(tally, Window, MidTurnUsage.Estimate, idle));   // idle: no estimate

        var live = new StreamMeter.Reading(true, 500, 40);
        Assert.Equal(30, UsageText.ContextPercent(tally, Window, MidTurnUsage.LastKnown, live));   // last known: moves at a report
        Assert.Equal(42, UsageText.ContextPercent(tally, Window, MidTurnUsage.Estimate, live));    // estimate: known plus the chunks
        Assert.Equal("~" + UsageText.ContextPercent(tally, Window, MidTurnUsage.Estimate, live) + "%",
            UsageText.BusyHintParts(tally, Window, MidTurnUsage.Estimate, live)!.Percent);

        Assert.Null(UsageText.ContextPercent(tally, null, MidTurnUsage.LastKnown, idle));   // the window unknown: left out
        Assert.Equal(0, UsageText.ContextPercent(new TokenTally(), Window, MidTurnUsage.LastKnown, idle));   // nothing in use

        // After a connect the figure stays: the history crossed over, and the meter shows no speed.
        tally.HideFromHint();
        Assert.Null(UsageText.BusyHintParts(tally, Window, MidTurnUsage.LastKnown, idle));
        Assert.Equal(UsageText.Percent(tally.LastRequest.Total, Window), UsageText.ContextPercent(tally, Window, MidTurnUsage.LastKnown, idle));
    }

    [Fact]
    public void BusyHintPart_Estimate_AddsTheStreamedChunks_Marked_AndFallsBackOutsideAStream()
    {
        var tally = Filled();

        // The last request's 1,240 plus 500 chunks, at the chunks' rate; every figure marked.
        Assert.Equal("~1.7k / 4.1k · ~42% · ~40 tok/s", UsageText.BusyHintPart(tally, Window, MidTurnUsage.Estimate, new StreamMeter.Reading(true, 500, 40)));
        Assert.Equal("~1.7k tokens · ~40 tok/s", UsageText.BusyHintPart(tally, null, MidTurnUsage.Estimate, new StreamMeter.Reading(true, 500, 40)));
        // One chunk in, no rate yet: the context marked, the last known speed unmarked (2026-10-26).
        Assert.Equal("~1.2k / 4.1k · ~30% · 46 tok/s", UsageText.BusyHintPart(tally, Window, MidTurnUsage.Estimate, new StreamMeter.Reading(true, 1, null)));
        Assert.Equal("~12 tokens", UsageText.BusyHintPart(new TokenTally(), null, MidTurnUsage.Estimate, new StreamMeter.Reading(true, 12, null)));   // none known
        // The very first request of a conversation estimates from nothing.
        Assert.Equal("~12 tokens · ~6 tok/s", UsageText.BusyHintPart(new TokenTally(), null, MidTurnUsage.Estimate, new StreamMeter.Reading(true, 12, 6)));

        // The prefill (no chunk yet) and between requests: the last known figures, unmarked.
        Assert.Equal("1.2k / 4.1k · 30% · 46 tok/s", UsageText.BusyHintPart(tally, Window, MidTurnUsage.Estimate, new StreamMeter.Reading(true, 0, null)));
        Assert.Equal("1.2k / 4.1k · 30% · 46 tok/s", UsageText.BusyHintPart(tally, Window, MidTurnUsage.Estimate, StreamMeter.Reading.Idle));
        Assert.Null(UsageText.BusyHintPart(new TokenTally(), Window, MidTurnUsage.Estimate, StreamMeter.Reading.Idle));
    }

    [Fact]
    public void HiddenFromTheHint_TheLastKnownFiguresGo_TheLiveEstimateStays_AndTheNextReportBringsThemBack()
    {
        var tally = Filled();
        tally.HideFromHint();   // an LLM connect: the figures are the old model's

        Assert.Null(UsageText.HintPart(tally, Window));
        Assert.Null(UsageText.BusyHintPart(tally, Window, MidTurnUsage.LastKnown, new StreamMeter.Reading(true, 500, 40)));
        Assert.Null(UsageText.BusyHintPart(tally, Window, MidTurnUsage.Estimate, StreamMeter.Reading.Idle));
        Assert.Equal("~1.7k / 4.1k · ~42% · ~40 tok/s", UsageText.BusyHintPart(tally, Window, MidTurnUsage.Estimate, new StreamMeter.Reading(true, 500, 40)));
        Assert.Equal("~1.7k / 4.1k · ~42%", UsageText.BusyHintPart(tally, Window, MidTurnUsage.Estimate, new StreamMeter.Reading(true, 500, null)));   // no old speed before the rate
        Assert.StartsWith("Tokens — last reply: 1,240 ", UsageText.Lines(tally)[0], StringComparison.Ordinal);   // /usage keeps them
        Assert.Equal(1_240, tally.LastRequest.Total);   // and the headline's count is the same figure

        tally.BeginTurn();
        tally.Add(Usage(1_300, 20, 1, 0.5, 1));
        Assert.Equal("1.3k / 4.1k · 32% · 20 tok/s", UsageText.HintPart(tally, Window));
    }

    /// <summary>The bots' and the advisors' rows (2026-10-10): under the launch section, and with nothing of this chat's counted, under the empty note.</summary>
    [Fact]
    public void StatisticsTab_AndLines_ShowTheBotsAndTheAdvisors_OnceEachCounted()
    {
        var tally = new TokenTally();
        tally.AddBots(Usage(1000, 50, 1, 0.5, 1), 3);
        tally.AddAdvisors(Usage(300, 40, 1, 0.5, 1), 1);

        Assert.Equal("3 requests · 1,000 in · 50 out", UsageText.ApartValue(3, tally.Bots));
        Assert.Equal([UsageText.NothingCounted, "botchat: 3 requests · 1,000 in · 50 out", "advisors: 1 request · 300 in · 40 out"], UsageText.Lines(tally));

        string[] lines = Tab(tally);
        Assert.Contains(lines, l => l.StartsWith("  " + UsageText.BotsLabel, StringComparison.Ordinal) && l.EndsWith("3 requests · 1,000 in · 50 out", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("  " + UsageText.AdvisorsLabel, StringComparison.Ordinal) && l.EndsWith("1 request · 300 in · 40 out", StringComparison.Ordinal));

        tally.Add(Usage(100, 20, 1, 0.5, 1));
        Assert.Contains("advisors: 1 request · 300 in · 40 out", UsageText.Lines(tally));
    }

    // ── The sections, shared with the Web UI's card (2026-10-16) ──

    [Fact]
    public void Sections_AreThePanesInItsOrder_TheExtrasUnderTheLaunch()
    {
        var tally = Filled();
        tally.AddBots(Usage(500, 50, 1, 0.5, 1), requests: 3);

        var shown = UsageText.Sections(tally, Window);

        Assert.False(shown.Nothing || shown.NothingYet);
        Assert.Empty(shown.Apart);
        Assert.Equal(
            [UsageText.ContextHeading, UsageText.LastReplyHeading, UsageText.ConversationHeading, UsageText.SessionHeading],
            shown.Sections.Select(s => s.Heading));
        Assert.Equal(ContextLengthProbe.MaxModelLenSource, shown.Sections[0].Note);
        Assert.Equal(UsageText.ContextRows(tally, Window), shown.Sections[0].Rows);
        Assert.Equal(UsageText.Rows(tally.LastReply, averaged: false), shown.Sections[1].Rows);
        Assert.Equal((UsageText.BotsLabel, UsageText.ApartValue(3, Usage(500, 50, 1, 0.5, 1))), shown.Sections[3].Rows[^1]);
    }

    [Fact]
    public void Sections_BeforeAnythingWasCounted_TheContextAlone_TheExtrasApart_OrNothing()
    {
        Assert.True(UsageText.Sections(new TokenTally(), null).Nothing);

        var windowOnly = UsageText.Sections(new TokenTally(), Window);
        Assert.Equal((false, true), (windowOnly.Nothing, windowOnly.NothingYet));
        Assert.Equal([UsageText.ContextHeading], windowOnly.Sections.Select(s => s.Heading));

        var bots = new TokenTally();
        bots.AddBots(Usage(500, 50, 1, 0.5, 1), requests: 3);
        var apart = UsageText.Sections(bots, null);
        Assert.False(apart.Nothing);
        Assert.Equal([UsageText.BotsLabel], apart.Apart.Select(r => r.Label));
        Assert.Equal(UsageText.WindowUnknown, apart.Sections[0].Note);
    }

    [Fact]
    public void TheWebUsageCard_IsTheMeterAndTheTiles_FromTheSameSections()
    {
        var tally = Filled();
        var card = NeonSidekick.WebUi.WebUsageView.Of(tally, Window);

        Assert.Equal(UsageText.Label, card.Title);
        Assert.Equal(new NeonSidekick.WebUi.WebUsageContext(UsageText.ContextHeading, "1.2k", "4.1k", 30, ContextLengthProbe.MaxModelLenSource), card.Context);
        Assert.Equal([UsageText.LastReplyHeading, UsageText.ConversationHeading, UsageText.SessionHeading], card.Sections.Select(s => s.Heading));
        Assert.Equal(UsageText.Rows(tally.LastReply, averaged: false).Select(r => (r.Label, r.Value)), card.Sections[0].Rows.Select(r => (r.Label, r.Value)));
        Assert.Null(card.Empty);

        var nothing = NeonSidekick.WebUi.WebUsageView.Of(new TokenTally(), null);
        Assert.Equal((null, 0, UsageText.NothingCounted), (nothing.Context, nothing.Sections.Count, nothing.Empty));

        var bots = new TokenTally();
        bots.AddBots(Usage(500, 50, 1, 0.5, 1), requests: 3);
        var apart = NeonSidekick.WebUi.WebUsageView.Of(bots, null);
        Assert.Equal((UsageText.WindowUnknown, null, null), (apart.Context!.Source, apart.Context.Window, apart.Context.Percent));
        Assert.Equal(UsageText.NothingCounted, apart.Empty);
        Assert.Equal([UsageText.SessionHeading], apart.Sections.Select(s => s.Heading));   // the extras in a tile of their own
    }
}
