using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The <c>/usage</c> Context tab's words and layout (2026-11-01, feature idea #1): the estimate as the headline with the server's count under
/// it, the rows adding up to it, the tool groups largest first with what turning one off saves, no bar or share without a window, the
/// history worked out from the server's count while a reply runs, the Claude Code line, and the plain lines. Since 2026-11-02 (the review): schemas
/// as sent, the reference line's three cases, the last request's breakdown mid-reply, the prompt in "off saves", the bar's edges and shades, the label cap.
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
        Assert.Equal("18 turns · tool results 1.0k", rows[5].Note);
        Assert.Equal(UsageText.ThinkingNotSent, rows[6].Note);
        Assert.Equal("0", rows[6].Value);
    }

    [Fact]
    public void TheHeadline_IsTheEstimate_TheServersCountBeneath()
    {
        var facts = Facts(Window);

        Assert.Equal($"~{facts.Estimate:N0} of 32,768 ({UsageText.PercentText(UsageText.Percent(facts.Estimate, Window)!.Value)})", UsageText.ContextHeadline(facts));
        Assert.Equal("the server counted 2,950 at the last request", UsageText.ContextReference(facts));
        Assert.Equal(UsageText.NothingSentYet, UsageText.ContextReference(Facts(Window, counted: 0)));
        // Free is marked an estimate (2026-11-02, review P1-3: the pin moved from the bare number).
        Assert.Equal((UsageText.FreeLabel, UsageText.EstimateMark + UsageText.CompactNumber(32_768 - facts.Estimate)), (UsageText.ContextRows(facts)[^1].Label, UsageText.ContextRows(facts)[^1].Value));
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
        Assert.Equal([1, 49, 0, 24], UsageText.BarCells(pieces, 1_000, 100));   // a sliver keeps a cell; the empty piece none; 99 cells, one kept free

        (ContextPiece, long)[] over = [(ContextPiece.Prompt, 600), (ContextPiece.History, 600)];
        Assert.Equal(99, UsageText.BarCells(over, 1_000, 100).Sum());   // the last cell the warning's

        var rows = UsageText.ContextRows(new ContextFacts(new ContextLength(1_000, "configured"), 0, [("Rules", 600)], [], SystemPromptSummary.NoToolsLine(false), History(text: 2_400, results: 0), 2, false));
        Assert.Equal((UsageText.OverLabel, "~200"), (rows[^1].Label, rows[^1].Value));
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
        Assert.Equal(("~1.0k", UsageText.ThinkingSent), (thinking.Value, thinking.Note));

        var pictures = Assert.Single(UsageText.ContextRows(Facts(Window, history: History(pictures: 2))), r => r.Label == UsageText.PicturesLabel);
        Assert.Equal(("~2.0k", "2 pictures"), (pictures.Value, pictures.Note));

        // The Anthropic and OpenAI APIs take back the turn in flight's alone, whatever the switch says (2026-11-02, the review's P8-4).
        var api = UsageText.ContextRows(Facts(Window, history: History(thinking: 4_000), preserve: true) with { InFlightOnly = true });
        Assert.Equal(UsageText.ThinkingInFlightOnly, Assert.Single(api, r => r.Label == UsageText.ThinkingLabel).Note);
    }

    /// <summary>A schema as the wire sends it (2026-11-02, the review's P3-2/P5-3): an indented literal weighs what its minified form does; no object, the empty one.</summary>
    [Fact]
    public void ASchema_CountsMinified_AndAnUndefinedOne_AsTheEmptyObject()
    {
        var indented = ToolSchema.Parse("""
            {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "The file." }
                }
            }
            """);
        var minified = ToolSchema.Parse("""{"type":"object","properties":{"path":{"type":"string","description":"The file."}}}""");

        Assert.Equal(minified.GetRawText().Length, ChatScreen.SchemaChars(indented));
        Assert.Equal(ChatScreen.SchemaChars(minified), ChatScreen.SchemaChars(indented));
        Assert.Equal(ChatScreen.EmptySchema.Length, ChatScreen.SchemaChars(default));
        Assert.Equal((4 + 5 + ChatScreen.EmptySchema.Length + ChatScreen.ToolWrapperChars) / ConversationCompactor.CharsPerToken, ChatScreen.ToolTokens(new NoSchemaTool()));
    }

    /// <summary>The reference line (2026-11-02, the review's P3-3/P8-3): a forgotten count is not "nothing sent", and after a connect the count is the previous model's.</summary>
    [Fact]
    public void TheReferenceLine_TellsAForgottenCount_AndThePreviousModelsCount()
    {
        Assert.Equal(UsageText.NothingSentYet, UsageText.ContextReference(Facts(Window, counted: 0)));
        Assert.Equal(UsageText.CountedAgain, UsageText.ContextReference(Facts(Window, counted: 0) with { Sent = true }));
        Assert.Equal(UsageText.PreviousModelCounted(2_950), UsageText.ContextReference(Facts(Window) with { Sent = true, PreviousModel = true }));
        Assert.Equal("the previous model counted 2,950 at the last request", UsageText.PreviousModelCounted(2_950));
    }

    /// <summary>Mid-reply (2026-11-02, the review's P2-1/P8-1): the turn's last request's breakdown, said so; "a reply is running" only for a reply.</summary>
    [Fact]
    public void MidReply_TheLastSentBreakdown_KeepsEveryRow_AndSaysSo()
    {
        var facts = Facts(Window, history: History(thinking: 400, pictures: 1)) with { AsLastSent = true, ReplyRunning = true };
        var rows = UsageText.ContextRows(facts);

        Assert.Equal("18 turns · tool results 1.0k · " + UsageText.HistoryAsSent + "; " + UsageText.ReplyRunningNote, Assert.Single(rows, r => r.Label == UsageText.HistoryLabel).Note);
        Assert.Contains(rows, r => r.Label == UsageText.PicturesLabel);
        Assert.Contains(rows, r => r.Label == UsageText.ThinkingLabel);

        var load = UsageText.ContextRows(facts with { ReplyRunning = false });   // a model load's or a /print's wait
        Assert.EndsWith(" · " + UsageText.HistoryAsSent, Assert.Single(load, r => r.Label == UsageText.HistoryLabel).Note, StringComparison.Ordinal);
    }

    /// <summary>What a group's switch saves counts the prompt's sentences it takes out too (2026-11-02, the review's P2-8).</summary>
    [Fact]
    public void OffSaves_CountsTheGroupsPromptSentences()
    {
        var facts = new ContextFacts(Window, 0, [("Rules", 400)], [new ContextToolGroup("Shell", 3, 500, Switchable: true, PromptTokens: 1_000)], null, History(), 1, false);

        Assert.Equal(UsageText.OffSaves(5), UsageText.ContextRows(facts)[2].Note);   // 1,500 of 32,768
    }

    /// <summary>The bar (2026-11-02, the review's P6-2/P6-3): a large piece leaves the later ones their cell and a free one; past the window, the warning cell.</summary>
    [Fact]
    public void TheBar_ALargePiece_LeavesTheLaterOnesACell_AndPastTheWindow_TheWarningCell()
    {
        (ContextPiece, long)[] crowded = [(ContextPiece.Prompt, 990), (ContextPiece.Tools, 1), (ContextPiece.History, 1)];
        Assert.Equal([7, 1, 1], UsageText.BarCells(crowded, 1_000, 10));   // and one free

        (ContextPiece, long)[] over = [(ContextPiece.Prompt, 2_000), (ContextPiece.Tools, 1)];
        Assert.Equal([8, 1], UsageText.BarCells(over, 1_000, 10));

        (ContextPiece, long)[] full = [(ContextPiece.Prompt, 1_000)];
        Assert.Equal([10], UsageText.BarCells(full, 1_000, 10));   // exactly the window: no free cell to keep
    }

    /// <summary>With no color (2026-11-02, the review's P4-6) each piece draws its own shade, and past the window the warning cell is a <c>!</c>.</summary>
    [Fact]
    public void TheBar_WithNoColor_DrawsAShadeAPiece()
    {
        var facts = Facts(new ContextLength(4_000, "configured"), history: History(text: 4_000, results: 0, thinking: 400));
        string bar = Bar(facts, ColorSystem.NoColors);

        Assert.Contains('█', bar);
        Assert.Contains('▓', bar);
        Assert.Contains('▒', bar);
        Assert.Contains('▚', bar);
        Assert.DoesNotContain('▓', Bar(facts, ColorSystem.TrueColor));

        var over = Facts(new ContextLength(1_000, "configured"));
        Assert.EndsWith("!", Bar(over, ColorSystem.NoColors), StringComparison.Ordinal);
    }

    /// <summary>A long group's label ends in an ellipsis at <see cref="UsageText.LabelCap"/> cells (2026-11-02, the review's P2-10); the plain lines keep it whole.</summary>
    [Fact]
    public void ALongGroupLabel_IsCut_AndTheNoteStillShows()
    {
        string name = "MCP: " + new string('x', 40);
        var facts = new ContextFacts(Window, 0, [("Rules", 400)], [new ContextToolGroup(name, 3, 1_200, Switchable: true)], null, History(), 1, false);
        using var console = new TestConsole();
        console.Profile.Width = 80;
        console.Profile.Capabilities.Unicode = true;
        console.Write(UsageText.ContextTab(facts));
        string row = console.Output.Split('\n').Single(l => l.Contains("MCP: ", StringComparison.Ordinal));

        Assert.True(row.Contains('…', StringComparison.Ordinal), row);
        Assert.DoesNotContain(name, row, StringComparison.Ordinal);
        Assert.Contains("off saves 4%", row, StringComparison.Ordinal);
        Assert.Contains("  " + name + ": ~1.2k (off saves 4%)", UsageText.ContextLines(facts));
    }

    private static string Bar(ContextFacts facts, ColorSystem colors)
    {
        using var console = new TestConsole();
        console.Profile.Width = 60;
        console.Profile.Capabilities.ColorSystem = colors;
        console.Write(UsageText.ContextTab(facts));
        return console.Output.Split('\n').Single(l => l.Contains('░', StringComparison.Ordinal) || l.Contains('!', StringComparison.Ordinal)).Trim();
    }

    /// <summary>A tool whose schema is undefined (an MCP server's that sent none).</summary>
    private sealed class NoSchemaTool : AIFunction
    {
        public override string Name => "bare";

        public override string Description => "Bare.";

        public override JsonElement JsonSchema => default;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => new("done");
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

        Assert.StartsWith($"Context: ~{facts.Estimate:N0} of 32,768 tokens (", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(ContextLengthProbe.MaxModelLenSource + ")", lines[0], StringComparison.Ordinal);
        Assert.Equal("(the server counted 2,950 at the last request)", lines[1]);
        Assert.Equal("system prompt: ~700 (Rules 400 · Project 200 · Persona 100)", lines[2]);
        Assert.Contains("  Databases: ~1.2k (off saves 4%)", lines);
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
