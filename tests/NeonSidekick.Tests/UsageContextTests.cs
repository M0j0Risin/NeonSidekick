using NeonSidekick.App;
using NeonSidekick.Llm;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The <c>/usage</c> Context tab's words and layout (2026-11-01, feature idea #1): the estimate as the headline with the server's count under
/// it, the rows adding up to it, the tool groups largest first with what turning one off saves, no bar or share without a window, the
/// history worked out from the server's count while a reply runs, the Claude Code line, and the plain lines.
/// </summary>
public class UsageContextTests
{
    private static readonly ContextLength Window = new(32_768, ContextLengthProbe.MaxModelLenSource);

    private static ConversationCompactor.TokenEstimate History(long text = 8_000, long results = 4_000, long thinking = 0, long pictures = 0) =>
        new(text, 0, results, 0, thinking, pictures);

    private static ContextFacts Facts(ContextLength? window = null, long counted = 2_950, ConversationCompactor.TokenEstimate? history = null, bool preserve = false, bool running = false) =>
        new(window,
            counted,
            [("Persona", 100), ("Rules", 400), ("Memory", 0), ("Project", 200)],
            [new ContextToolGroup("Shell", 3, 500, Switchable: true), new ContextToolGroup("Databases", 6, 1_200, Switchable: true), new ContextToolGroup("Clock", 1, 50, Switchable: false)],
            null,
            running ? null : history ?? History(),
            running ? 0 : 18,
            preserve);

    [Fact]
    public void TheRows_AddUpToTheHeadline_NothingCountedTwice()
    {
        var facts = Facts(Window, history: History(thinking: 400, pictures: 1), preserve: true);
        var estimate = facts.History!.Value;

        Assert.Equal(700, facts.PromptTokens);
        Assert.Equal(1_750, facts.ToolTokens);
        Assert.Equal(estimate.HistoryTokens + estimate.PictureTokenCount + estimate.ThinkingTokens, estimate.Total);
        Assert.Equal(700 + 1_750 + estimate.Total, facts.Estimate);
        Assert.Equal(facts.Estimate, UsageText.BarPieces(facts).Sum(p => p.Tokens));
    }

    [Fact]
    public void TheRows_PromptPartsAndGroupsLargestFirst_WithWhatAGroupOffSaves()
    {
        var rows = UsageText.ContextRows(Facts(Window));

        Assert.Equal(
            [UsageText.PromptLabel, UsageText.ToolsLabel, "Databases", "Shell", "Clock", UsageText.HistoryLabel, UsageText.ThinkingLabel, UsageText.FreeLabel],
            rows.Select(r => r.Label));
        Assert.Equal("Rules 400 · Project 200 · Persona 100", rows[0].Note);   // largest first, the empty Memory left out
        Assert.Equal(UsageText.ToolsNote(10, 3), rows[1].Note);
        Assert.Equal("10 tools in 3 groups", rows[1].Note);
        Assert.Equal(UsageText.OffSaves(4), rows[2].Note);   // 1,200 of 32,768
        Assert.Equal("off saves 4%", rows[2].Note);
        Assert.Equal("", rows[4].Note);   // the clock has no switch
        Assert.True(rows[2].Sub && !rows[1].Sub);
        Assert.Equal("18 messages · tool results 1.0k", rows[5].Note);
        Assert.Equal(UsageText.ThinkingNotSent, rows[6].Note);
        Assert.Equal("0", rows[6].Value);
    }

    [Fact]
    public void TheHeadline_IsTheEstimate_TheServersCountBeneath()
    {
        var facts = Facts(Window);

        Assert.Equal($"≈{facts.Estimate:N0} of 32,768 ({UsageText.PercentText(UsageText.Percent(facts.Estimate, Window)!.Value)})", UsageText.ContextHeadline(facts));
        Assert.Equal("the server counted 2,950 at the last request", UsageText.ContextReference(facts));
        Assert.Equal(UsageText.NothingSentYet, UsageText.ContextReference(Facts(Window, counted: 0)));
        Assert.Equal((UsageText.FreeLabel, UsageText.CompactNumber(32_768 - facts.Estimate)), (UsageText.ContextRows(facts)[^1].Label, UsageText.ContextRows(facts)[^1].Value));
    }

    [Fact]
    public void WithoutAWindow_NoShareNoBarNoFree()
    {
        var facts = Facts();
        var rows = UsageText.ContextRows(facts);

        Assert.EndsWith("tokens; window unknown", UsageText.ContextHeadline(facts), StringComparison.Ordinal);
        Assert.DoesNotContain(rows, r => r.Label is UsageText.FreeLabel or UsageText.OverLabel);
        Assert.All(rows.Where(r => r.Sub), r => Assert.Equal("", r.Note));   // no share to save

        using var console = new TestConsole();
        console.Profile.Width = 100;
        console.Write(UsageText.ContextTab(facts));
        Assert.DoesNotContain("█", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("░", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTab_DrawsTheBarAcrossTheWidth_AndTheRows()
    {
        var facts = Facts(Window);
        using var console = new TestConsole();
        console.Profile.Width = 80;
        console.Write(UsageText.ContextTab(facts));
        string output = console.Output;

        string bar = output.Split('\n').Single(l => l.Contains('█', StringComparison.Ordinal));
        Assert.Equal(80 - 2 * NeonSidekick.App.SystemPromptSummary.BodyIndent, bar.Trim().Length);
        Assert.Contains(UsageText.ContextHeadline(facts), output, StringComparison.Ordinal);
        Assert.Contains("Databases", output, StringComparison.Ordinal);
        Assert.Contains("off saves 4%", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBar_EachPieceItsShare_AtLeastOneCell_PastTheWindowFull()
    {
        (ContextPiece, long)[] pieces = [(ContextPiece.Prompt, 1), (ContextPiece.Tools, 500), (ContextPiece.History, 0), (ContextPiece.Pictures, 250)];
        Assert.Equal([1, 49, 0, 25], UsageText.BarCells(pieces, 1_000, 100));   // a sliver keeps a cell; the empty piece none

        (ContextPiece, long)[] over = [(ContextPiece.Prompt, 600), (ContextPiece.History, 600)];
        Assert.Equal(100, UsageText.BarCells(over, 1_000, 100).Sum());

        var rows = UsageText.ContextRows(new ContextFacts(new ContextLength(1_000, "configured"), 0, [("Rules", 600)], [], SystemPromptSummary.NoToolsLine(false), History(text: 2_400, results: 0), 2, false));
        Assert.Equal((UsageText.OverLabel, "≈200"), (rows[^1].Label, rows[^1].Value));
        Assert.Equal(SystemPromptSummary.NoToolsLine(false), rows[1].Note);
        Assert.Equal("0", rows[1].Value);
    }

    [Fact]
    public void MidReply_TheHistoryIsTheServersCountLessThePromptAndTools()
    {
        var facts = Facts(Window, counted: 20_000, running: true);
        var rows = UsageText.ContextRows(facts);

        var history = Assert.Single(rows, r => r.Label == UsageText.HistoryLabel);
        Assert.Equal(UsageText.HistoryFromCount, history.Note);
        Assert.Equal(20_000 - 700 - 1_750, facts.HistoryTokens);
        Assert.Equal(20_000, facts.Estimate);
        Assert.DoesNotContain(rows, r => r.Label is UsageText.ThinkingLabel or UsageText.PicturesLabel);

        Assert.DoesNotContain(UsageText.ContextRows(Facts(Window, counted: 0, running: true)), r => r.Label == UsageText.HistoryLabel);   // nothing counted yet
    }

    [Fact]
    public void Thinking_WithPreserveOn_IsCountedAndSaidSo()
    {
        var rows = UsageText.ContextRows(Facts(Window, history: History(thinking: 4_000), preserve: true));
        var thinking = Assert.Single(rows, r => r.Label == UsageText.ThinkingLabel);
        Assert.Equal(("≈1.0k", UsageText.ThinkingSent), (thinking.Value, thinking.Note));

        var pictures = Assert.Single(UsageText.ContextRows(Facts(Window, history: History(pictures: 2))), r => r.Label == UsageText.PicturesLabel);
        Assert.Equal(("≈2.0k", "2 pictures"), (pictures.Value, pictures.Note));
    }

    [Fact]
    public void TheClaudeCodeServer_IsOneLine()
    {
        var facts = new ContextFacts(Window, 0, [], [], null, null, 0, false, ClaudeCode: true);
        Assert.Equal([UsageText.ClaudeCodeContext], UsageText.ContextLines(facts));
        using var console = new TestConsole();
        console.Profile.Width = 120;
        console.Write(UsageText.ContextTab(facts));
        Assert.Equal(UsageText.ClaudeCodeContext, console.Output.Trim());
    }

    [Fact]
    public void ThePlainLines_HeadlineReferenceAndARowEach()
    {
        var facts = Facts(Window);
        var lines = UsageText.ContextLines(facts);

        Assert.StartsWith($"Context: ≈{facts.Estimate:N0} of 32,768 tokens (", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(ContextLengthProbe.MaxModelLenSource + ")", lines[0], StringComparison.Ordinal);
        Assert.Equal("(the server counted 2,950 at the last request)", lines[1]);
        Assert.Equal("system prompt: ≈700 (Rules 400 · Project 200 · Persona 100)", lines[2]);
        Assert.Contains("  Databases: ≈1.2k (off saves 4%)", lines);
        Assert.Equal(2 + UsageText.ContextRows(facts).Count, lines.Count);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("context", UsageText.ContextTabIndex)]
    [InlineData(" Context ", UsageText.ContextTabIndex)]
    [InlineData("reset", null)]
    public void TheArgument_NamesTheTab(string args, int? tab)
    {
        Assert.Equal(tab, ChatScreen.UsageTab(args));
    }
}
