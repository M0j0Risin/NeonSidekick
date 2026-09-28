using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class CompactionTextTests
{
    private static readonly TokenUsage Usage = new(41_200, 3_100, 44_300, 1, TimeSpan.Zero, TimeSpan.Zero);

    [Fact]
    public void CompactGlyph_IsTheClampWithItsSelector_AtTwoCells_ThenASpace()
    {
        // U+1F5DC + U+FE0F (2026-09-19): the pair's two cells, the selector zero after a wide character.
        Assert.Equal("\U0001F5DC️ ", CompactionText.CompactGlyph);
        Assert.Equal(2, TextCells.Width(CompactionText.CompactGlyph.TrimEnd()));
        Assert.Equal(3, TextCells.Width(CompactionText.CompactGlyph));
    }

    [Fact]
    public void TheThreeOutcomes_OpenWithTheClamp()
    {
        Assert.Equal("(🗜️ nothing to compact)", CompactionText.NothingToCompact);
        Assert.Equal("(🗜️ compact cancelled)", CompactionText.Cancelled);
        Assert.Equal("🗜️ Compact failed: ", CompactionText.FailedPrefix);
    }

    [Fact]
    public void Notice_ASummary_WearsTheClamp_WhateverItsTail()
    {
        Assert.Equal("(🗜️ compacted: 38 messages → 7 · 41.2k → 3.1k tokens)", CompactionText.Notice(new ConversationCompactor.Result(38, 7, 0, Usage, Summarised: true), null));
        Assert.Equal("(🗜️ compacted: 38 messages → 7)", CompactionText.Notice(new ConversationCompactor.Result(38, 7, 0, null, Summarised: true), null));
        Assert.Equal("(🗜️ auto-compacted at 83%: 38 messages → 7 · 41.2k → 3.1k tokens · 12 tool results pruned)", CompactionText.Notice(new ConversationCompactor.Result(38, 7, 12, Usage, Summarised: true), 83));
        Assert.Equal("(🗜️ auto-compacted at 83%: 38 messages → 7 · 1 tool result pruned)", CompactionText.Notice(new ConversationCompactor.Result(38, 7, 1, null, Summarised: true), 83));
    }

    [Fact]
    public void TurnNotice_TheMidTurnCompactsSecondStage_SaysThisTurn()
    {
        // 2026-09-28: this turn's earlier iterations became a progress note.
        Assert.Equal("(🗜️ compacted this turn at 91%: 24 messages → 5 · 41.2k → 3.1k tokens)", CompactionText.TurnNotice(new ConversationCompactor.Result(24, 5, 0, Usage, Summarised: true), 91));
        Assert.Equal("(🗜️ compacted this turn at 62%: 5 messages → 3)", CompactionText.TurnNotice(new ConversationCompactor.Result(5, 3, 0, null, Summarised: true), 62));
    }

    [Fact]
    public void Notice_APruneAlone_WearsTheScissors()
    {
        Assert.Equal("(✂️ compacted: 12 tool results pruned)", CompactionText.Notice(new ConversationCompactor.Result(38, 38, 12, null), null));
        Assert.Equal("(✂️ compacted: 1 tool result pruned)", CompactionText.Notice(new ConversationCompactor.Result(38, 38, 1, null), null));
        Assert.Equal("(✂️ auto-compacted at 35%: 1 tool result pruned)", CompactionText.Notice(new ConversationCompactor.Result(14, 14, 1, null), 35));
        Assert.StartsWith("(" + Assistant.PruneGlyph, CompactionText.Notice(new ConversationCompactor.Result(14, 14, 1, null), 35));
    }

    [Fact]
    public void SummaryLines_SplitsAtBreaks_TrimsEach_DropsTheBlanks()
    {
        Assert.Equal(["The user asked for a haiku.", "It was written."], CompactionText.SummaryLines("  The user asked for a haiku.\n\n\r\n It was written. \n"));
        Assert.Empty(CompactionText.SummaryLines(" \n "));
    }

    [Fact]
    public void PrunedLine_NamesTheTool_AndTheSize_OrThePictures()
    {
        Assert.Equal("(✂️ read_file · 4,312 characters)", CompactionText.PrunedLine(new ConversationCompactor.PrunedEntry("read_file", 4312)));
        Assert.Equal("(✂️ run_command · 1 character)", CompactionText.PrunedLine(new ConversationCompactor.PrunedEntry("run_command", 1)));
        Assert.Equal("(✂️ 2 pictures from view_image)", CompactionText.PrunedLine(new ConversationCompactor.PrunedEntry("view_image", 0, 2)));
        Assert.Equal("(✂️ 1 picture from view_image)", CompactionText.PrunedLine(new ConversationCompactor.PrunedEntry("view_image", 0, 1)));
        Assert.Equal("tool", ConversationCompactor.UnknownTool);
    }

    [Fact]
    public void ProtectedLines_ArePinned_ZeroSaidToo()
    {
        // The detail's closing lines (later on 2026-09-21): the opening pairs' messages and the recent turns' — zero is still said.
        Assert.Equal("(🗜️ 6 messages protected at the start)", CompactionText.OpeningKeptLine(6));
        Assert.Equal("(🗜️ 1 message protected at the start)", CompactionText.OpeningKeptLine(1));
        Assert.Equal("(🗜️ 0 messages protected at the start)", CompactionText.OpeningKeptLine(0));
        Assert.Equal("(🗜️ 2 messages protected at the end)", CompactionText.RecentKeptLine(2));
        Assert.Equal("(🗜️ 1 message protected at the end)", CompactionText.RecentKeptLine(1));
    }

    [Fact]
    public void DetailLines_TheSummaryFirst_ThenEachPrunedResult_ThenTheProtectedCounts()
    {
        var entries = new[] { new ConversationCompactor.PrunedEntry("read_file", 500), new ConversationCompactor.PrunedEntry("view_image", 0, 1) };
        Assert.Equal(
            ["A summary.", "Two lines.", "(✂️ read_file · 500 characters)", "(✂️ 1 picture from view_image)", "(🗜️ 4 messages protected at the start)", "(🗜️ 2 messages protected at the end)"],
            CompactionText.DetailLines(new ConversationCompactor.Result(38, 7, 2, null, Summarised: true) { Summary = "A summary.\nTwo lines.", Entries = entries, OpeningKept = 4, RecentKept = 2 }));
        Assert.Equal(["(✂️ read_file · 500 characters)", "(🗜️ 0 messages protected at the start)", "(🗜️ 12 messages protected at the end)"], CompactionText.DetailLines(new ConversationCompactor.Result(12, 12, 1, null) { Entries = [entries[0]], RecentKept = 12 }));
        // A bare result still closes with the two counts: the detail is never empty.
        Assert.Equal(["(🗜️ 0 messages protected at the start)", "(🗜️ 0 messages protected at the end)"], CompactionText.DetailLines(new ConversationCompactor.Result(38, 7, 0, null, Summarised: true)));
    }
}
