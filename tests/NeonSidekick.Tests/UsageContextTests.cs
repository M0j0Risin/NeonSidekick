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
/// as sent, the reference line's three cases, the last request's breakdown mid-reply, the prompt in a group's share, the bar's edges and shades, the
/// label cap. Since 2026-11-27 (plan G): the meter with the headline at its right, the legend, row bars, every row one line, the stops.
/// </summary>
public class UsageContextTests
{
    internal static readonly ContextLength Window = new(32_768, ContextLengthProbe.MaxModelLenSource);

    internal static ConversationCompactor.TokenEstimate History(long text = 8_000, long results = 4_000, long thinking = 0, long pictures = 0) =>
        new(text, 0, results, 0, thinking, pictures);

    /// <summary>The shared facts (2026-11-27: <see cref="UsageTextTests"/> builds the merged tab on them too).</summary>
    internal static ContextFacts Facts(ContextLength? window = null, long counted = 2_950, ConversationCompactor.TokenEstimate? history = null, bool preserve = false, bool running = false) =>
        new(window,
            counted,
            [("Persona", 100), ("Rules", 400), ("Memory", 0), ("Project", 200)],
            [new ContextToolGroup("Shell", 3, 500, Switch: SettingsField.FileTools), new ContextToolGroup("Databases", 6, 1_200, Switch: SettingsField.SqlTools), new ContextToolGroup("Clock", 1, 50, Switch: null)],
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
    public void TheRows_PromptPartsAndGroupsLargestFirst_WithEachGroupsShare()
    {
        var rows = UsageText.ContextRows(Facts(Window));

        Assert.Equal(
            [UsageText.PromptLabel, UsageText.ToolsLabel, "Databases", "Shell", "Clock", UsageText.HistoryLabel, UsageText.ThinkingLabel, UsageText.FreeLabel],
            rows.Select(r => r.Label));
        Assert.Equal("Rules 400 · Project 200 · Persona 100", rows[0].Note);   // largest first, the empty Memory left out
        Assert.Equal(UsageText.ToolsNote(10, 3), rows[1].Note);
        Assert.Equal("10 tools in 3 groups", rows[1].Note);
        Assert.Equal(UsageText.GroupShare(4), rows[2].Note);   // 1,200 of 32,768
        Assert.Equal(UsageText.GroupShare(0), rows[4].Note);   // every group shows its share (2026-11-27), the clock's a sliver
        Assert.Equal("<1%", UsageText.GroupShare(0));
        Assert.Equal(1_200, rows[2].Tokens);
        Assert.True(rows[2].Sub && !rows[1].Sub);
        Assert.Equal("18 turns · tool results 1.0k", rows[5].Note);
        Assert.Equal(UsageText.ThinkingNotSent, rows[6].Note);
        Assert.Equal("0", rows[6].Value);
    }

    [Fact]
    public void TheHeadline_IsTheEstimate_TheServersCountBeneath()
    {
        var facts = Facts(Window);

        Assert.Equal($"~{facts.Estimate:N0} of 32,768 · {UsageText.PercentText(UsageText.Percent(facts.Estimate, Window)!.Value)}", UsageText.ContextHeadline(facts));
        Assert.Equal(UsageText.ServerCounted(2_950), UsageText.ContextReference(facts));
        Assert.Equal(UsageText.NothingSentYet, UsageText.ContextReference(Facts(Window, counted: 0)));
        // Free is marked an estimate (2026-11-02, review P1-3: the pin moved from the bare number).
        Assert.Equal((UsageText.FreeLabel, UsageText.EstimateMark + UsageText.CompactNumber(32_768 - facts.Estimate)), (UsageText.ContextRows(facts)[^1].Label, UsageText.ContextRows(facts)[^1].Value));
    }

    [Fact]
    public void WithoutAWindow_NoShareNoMeterNoFree()
    {
        var facts = Facts();
        var rows = UsageText.ContextRows(facts);

        Assert.EndsWith("tokens; window unknown", UsageText.ContextHeadline(facts), StringComparison.Ordinal);
        Assert.DoesNotContain(rows, r => r.Label is UsageText.FreeLabel or UsageText.OverLabel);
        Assert.All(rows.Where(r => r.Sub), r => Assert.Equal("", r.Note));   // no share without a window

        using var console = new TestConsole();
        console.Profile.Width = 100;
        console.Write(UsageText.ContextTab(facts, 100));
        string output = console.Output;
        Assert.DoesNotContain(UsageText.MeterFull + "" + UsageText.MeterFull, output, StringComparison.Ordinal);   // the legend's marks, no meter
        Assert.DoesNotContain(UsageText.MeterEmpty, output);
        Assert.Equal(UsageText.ContextHeadline(facts), output.Split('\n')[0].Trim());   // the headline alone on the first line
        Assert.Contains('█', output);   // the row bars scale to the largest row, not the window
    }

    [Fact]
    public void TheTab_DrawsTheMeterAcrossTheWidth_TheHeadlineAtItsRight_AndTheRows()
    {
        var facts = Facts(Window);
        using var console = new TestConsole();
        console.Profile.Width = 80;
        console.Write(UsageText.ContextTab(facts, 80));
        string output = console.Output;
        var lines = output.Split('\n');

        string meter = lines[0].TrimEnd();
        Assert.StartsWith(new string(' ', SystemPromptSummary.BodyIndent) + UsageText.MeterFull, meter, StringComparison.Ordinal);
        Assert.EndsWith(new string(' ', UsageText.MeterGap) + UsageText.ContextHeadline(facts), meter, StringComparison.Ordinal);
        Assert.Equal(80, NeonSidekick.UI.TextCells.Width(meter));   // the indent taken off once (2026-11-27, the last-3 review's #11)
        Assert.Contains(UsageText.MeterEmpty, meter);
        Assert.Contains(UsageText.LegendFree, lines[1], StringComparison.Ordinal);
        Assert.Equal(UsageText.ContextReference(facts), lines[2].Trim());
        Assert.Equal("", lines[3].Trim());   // a blank line, then the rows with no heading
        Assert.StartsWith("  " + UsageText.PromptLabel, lines[4], StringComparison.Ordinal);
        Assert.Contains("Databases", output, StringComparison.Ordinal);
        Assert.Contains(UsageText.GroupShare(4), output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A line too narrow for <see cref="UsageText.MeterMinCells"/> beside the headline (2026-11-27, the last-3 review's #30): the headline
    /// alone, cut to the line, still one line.
    /// </summary>
    [Fact]
    public void ANarrowTab_DrawsTheHeadlineAlone_CutToTheLine()
    {
        var facts = Facts(Window);
        string headline = UsageText.ContextHeadline(facts);
        int width = SystemPromptSummary.BodyIndent + UsageText.MeterGap + NeonSidekick.UI.TextCells.Width(headline) + UsageText.MeterMinCells - 1;
        using var console = new TestConsole();
        console.Profile.Width = width;
        console.Write(new ContextMeter(UsageText.BarPieces(facts), Window.Tokens, headline, SystemPromptSummary.BodyIndent));
        string line = console.Output.Split('\n')[0].TrimEnd();
        Assert.Equal(new string(' ', SystemPromptSummary.BodyIndent) + headline, line);
        Assert.DoesNotContain(UsageText.MeterFull, line);

        using var tiny = new TestConsole();
        tiny.Profile.Width = 12;
        tiny.Write(new ContextMeter(UsageText.BarPieces(facts), Window.Tokens, headline, SystemPromptSummary.BodyIndent));
        string cut = tiny.Output.Split('\n')[0].TrimEnd();
        Assert.True(NeonSidekick.UI.TextCells.Width(cut) <= 12, cut);
        Assert.Single(tiny.Output.TrimEnd('\n').Split('\n'));
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

    /// <summary>A group's share counts the prompt's sentences its switch takes out too (2026-11-02, the review's P2-8).</summary>
    [Fact]
    public void GroupShare_CountsTheGroupsPromptSentences()
    {
        var facts = new ContextFacts(Window, 0, [("Rules", 400)], [new ContextToolGroup("Shell", 3, 500, Switch: SettingsField.FileTools, PromptTokens: 1_000)], null, History(), 1, false);

        Assert.Equal(UsageText.GroupShare(5), UsageText.ContextRows(facts)[2].Note);   // 1,500 of 32,768
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
        Assert.Contains('░', Bar(facts, ColorSystem.NoColors));   // free, with no ▱ to tell apart
        Assert.Contains(UsageText.MeterEmpty, Bar(facts, ColorSystem.TrueColor));
    }

    /// <summary>A long group's label ends in an ellipsis at <see cref="UsageText.LabelCap"/> cells (2026-11-02, the review's P2-10); the plain lines keep it whole.</summary>
    [Fact]
    public void ALongGroupLabel_IsCut_AndTheNoteStillShows()
    {
        string name = "MCP: " + new string('x', 40);
        var facts = new ContextFacts(Window, 0, [("Rules", 400)], [new ContextToolGroup(name, 3, 1_200, Switch: SettingsField.FileTools)], null, History(), 1, false);
        using var console = new TestConsole();
        console.Profile.Width = 80;
        console.Profile.Capabilities.Unicode = true;
        console.Write(UsageText.ContextTab(facts, 80));
        string row = console.Output.Split('\n').Single(l => l.Contains("MCP: ", StringComparison.Ordinal));

        Assert.True(row.Contains('…', StringComparison.Ordinal), row);
        Assert.DoesNotContain(name, row, StringComparison.Ordinal);
        Assert.Contains(UsageText.GroupShare(4), row, StringComparison.Ordinal);
        Assert.Contains("  " + name + ": ~1.2k (" + UsageText.GroupShare(4) + ")", UsageText.ContextLines(facts));
    }

    /// <summary>The meter's cells alone: its line's first, up to the headline at its right.</summary>
    private static string Bar(ContextFacts facts, ColorSystem colors)
    {
        using var console = new TestConsole();
        console.Profile.Width = 60;
        console.Profile.Capabilities.ColorSystem = colors;
        console.Write(UsageText.ContextTab(facts, 60));
        string line = console.Output.Split('\n')[0];
        int head = line.IndexOf(UsageText.ContextHeadline(facts), StringComparison.Ordinal);
        return (head > 0 ? line[..head] : line).Trim();
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
        console.Write(UsageText.ContextTab(facts, 120));
        Assert.Equal(UsageText.ClaudeCodeContext, console.Output.Trim());
    }

    [Fact]
    public void ThePlainLines_HeadlineReferenceAndARowEach()
    {
        var facts = Facts(Window);
        var lines = UsageText.ContextLines(facts);

        Assert.StartsWith($"Context: ~{facts.Estimate:N0} of 32,768 tokens (", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(ContextLengthProbe.MaxModelLenSource + ")", lines[0], StringComparison.Ordinal);
        Assert.Equal(UsageText.PlainLegend(facts), lines[1]);
        Assert.Equal("(" + UsageText.ServerCounted(2_950) + ")", lines[2]);
        Assert.Equal("system prompt: ~700 (Rules 400 · Project 200 · Persona 100)", lines[3]);
        Assert.Contains("  Databases: ~1.2k (" + UsageText.GroupShare(4) + ")", lines);
        Assert.Contains("thinking: 0 " + UsageText.ThinkingNotSent, lines);   // its own parentheses, never a second pair
        Assert.Equal(3 + UsageText.ContextRows(facts).Count, lines.Count);
    }

    /// <summary>The plain legend (2026-11-27): each piece's word and figure, no marks; pictures only with some, over past the window.</summary>
    [Fact]
    public void ThePlainLegend_NamesEachPiece_WithNoMarks()
    {
        var facts = Facts(Window);
        string legend = UsageText.PlainLegend(facts);

        Assert.DoesNotContain(UsageText.MeterFull, legend);
        Assert.DoesNotContain(UsageText.MeterEmpty, legend);
        foreach (string word in new[] { UsageText.LegendPrompt, UsageText.LegendTools, UsageText.LegendHistory, UsageText.LegendThinking, UsageText.LegendFree })
        {
            Assert.Contains(word + " ", legend, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(UsageText.LegendPictures, legend, StringComparison.Ordinal);
        Assert.Contains(UsageText.LegendPictures + " ", UsageText.PlainLegend(Facts(Window, history: History(pictures: 2))), StringComparison.Ordinal);
        string over = UsageText.PlainLegend(Facts(new ContextLength(1_000, "configured")));
        Assert.Contains(UsageText.LegendOver + " ", over, StringComparison.Ordinal);
        Assert.DoesNotContain(UsageText.LegendFree, over, StringComparison.Ordinal);
        Assert.DoesNotContain(UsageText.LegendFree, UsageText.PlainLegend(Facts()));   // no window, no free
    }

    /// <summary>A row bar (2026-11-27): the largest row full, a sliver one eighth, zero empty.</summary>
    [Fact]
    public void ARowBar_ScalesToTheLargestRow()
    {
        Assert.Equal(new string('█', 20), UsageText.RowBar(1_000, 1_000, 20));
        Assert.Equal("▏", UsageText.RowBar(1, 1_000, 20));
        Assert.Equal("", UsageText.RowBar(0, 1_000, 20));
        Assert.Equal("█████▏", UsageText.RowBar(5_100, 20_000, 20));   // 5.1 cells: five and an eighth

        // Free is left out of the largest: the History row, the largest of the rest, is full.
        using var console = new TestConsole();
        console.Profile.Width = 120;
        console.Write(UsageText.ContextTab(Facts(Window), 120));
        string history = console.Output.Split('\n').Single(l => l.StartsWith("  " + UsageText.HistoryLabel, StringComparison.Ordinal));
        Assert.Contains(new string('█', UsageText.RowBarCells), history, StringComparison.Ordinal);
        string free = console.Output.Split('\n').Single(l => l.StartsWith("  " + UsageText.FreeLabel, StringComparison.Ordinal));
        Assert.DoesNotContain('█', free);
    }

    /// <summary>Below <see cref="UsageText.RowBarMinWidth"/> cells the bar column goes, before any note is cut (2026-11-27).</summary>
    [Fact]
    public void TheBarColumn_GoesBelowItsWidth()
    {
        string Drawn(int width)
        {
            using var console = new TestConsole();
            console.Profile.Width = width;
            console.Write(UsageText.ContextTab(Facts(Window), width));
            return string.Join('\n', console.Output.Split('\n').Skip(4));   // the rows
        }

        Assert.Contains('█', Drawn(UsageText.RowBarMinWidth));
        Assert.DoesNotContain('█', Drawn(UsageText.RowBarMinWidth - 1));
    }

    /// <summary>Every row is one line (2026-11-27): a long note is cut with an ellipsis, never wrapped, so a stop's line is its row.</summary>
    [Fact]
    public void EveryRow_IsOneLine_ALongNoteCut()
    {
        var facts = Facts(Window) with { NoToolsNote = null };
        var parts = Enumerable.Range(1, 12).Select(i => ("Part" + i, (long)(100 + i))).ToList();
        var wide = new ContextFacts(Window, 0, parts, facts.Groups, null, History(), 1, false);
        using var console = new TestConsole();
        console.Profile.Width = 70;
        console.Write(UsageText.ContextTab(wide, 70));
        var lines = console.Output.TrimEnd('\n').Split('\n');

        Assert.Equal(4 + UsageText.ContextRows(wide).Count, lines.Length);
        Assert.Contains('…', lines[4]);
        Assert.All(lines, l => Assert.True(NeonSidekick.UI.TextCells.Width(l.TrimEnd()) <= 70, l));
    }

    /// <summary>
    /// The merged tab's stops (2026-11-27): a line per row but Free and Over, each line starting with its row's label, the first group the start.
    /// </summary>
    [Fact]
    public void TheLayout_StopsLandOnTheRowsLines()
    {
        var facts = Facts(Window);
        var layout = UsageText.StatisticsLayout(new TokenTally(), facts, [], Sessions.ModelBests.None, null, 100);
        using var console = new TestConsole();
        console.Profile.Width = 100;
        console.Write(layout.Content);
        var lines = console.Output.Split('\n');

        var rows = UsageText.ContextRows(facts).Where(r => r.Piece is not (ContextPiece.Free or ContextPiece.Over)).ToList();
        Assert.Equal(rows.Select(r => r.Label), layout.StopRows.Select(r => r.Label));
        for (int i = 0; i < layout.Stops.Count; i++)
        {
            Assert.StartsWith(new string(' ', SystemPromptSummary.BodyIndent) + (rows[i].Sub ? new string(' ', UsageText.SubIndent) : "") + rows[i].Label,
                lines[layout.Stops[i]], StringComparison.Ordinal);
        }

        Assert.Equal("Databases", layout.StopRows[layout.InitialStop].Label);   // the first tool group
    }

    /// <summary>A group switched off from the tab (2026-11-27) stays in its place, dimmed, its figure kept and its note <see cref="UsageText.GroupOff"/>.</summary>
    [Fact]
    public void AGroupSwitchedOffFromTheTab_StaysListed_Off()
    {
        var databases = new ContextToolGroup("Databases", 6, 1_200, SettingsField.SqlTools);
        var facts = Facts(Window) with { Groups = [new ContextToolGroup("Shell", 3, 500, SettingsField.FileTools)], SwitchedOff = [databases] };
        var row = Assert.Single(UsageText.ContextRows(facts), r => r.Label == "Databases");

        Assert.True(row.Off);
        Assert.Equal(UsageText.GroupOff, row.Note);
        Assert.Equal("~1.2k", row.Value);
        Assert.Same(databases, row.Group);
        Assert.Equal(500, facts.ToolTokens);   // the meter and totals without it
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("context", UsageText.ContextTabIndex)]
    [InlineData(" Context ", UsageText.ContextTabIndex)]
    [InlineData("prompt", UsageText.PromptTabIndex)]   // the three from /sys (2026-11-07, the user's ask)
    [InlineData(" Prompt ", UsageText.PromptTabIndex)]
    [InlineData("tools", UsageText.ToolsTabIndex)]
    [InlineData("TOOLS", UsageText.ToolsTabIndex)]
    [InlineData("skills", UsageText.SkillsTabIndex)]
    [InlineData(" Skills", UsageText.SkillsTabIndex)]
    [InlineData("persona", null)]
    [InlineData("reset", null)]
    public void TheArgument_NamesTheTab(string args, int? tab)
    {
        Assert.Equal(tab, ChatScreen.UsageTab(args));
    }
}
