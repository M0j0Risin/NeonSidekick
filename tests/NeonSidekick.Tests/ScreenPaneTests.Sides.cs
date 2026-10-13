using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The hint bar's and the tool bar's two groups (2026-10-13, the user's ask: any item pinned left or right, the sort free within each side):
/// the left group from column 0, the right group ending on the last cell, each item's zone where it stands; what gives way on a narrow
/// row — the name and the path first, then the right group, then the left; the busy row following the sides.
/// </summary>
public partial class ScreenPaneTests
{
    private static ScreenPane.ToolbarItem Glyph(string glyph) => ScreenPane.ToolbarItem.GlyphOf(glyph);

    /// <summary>A glyph and the model's name pinned right, the path pinned left among the glyphs: each drawn on its side, each a zone there.</summary>
    [Fact]
    public void TheToolbar_DrawsEachGroupOnItsSide_EachItemAZoneWhereItStands()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Toolbar = () => new ScreenPane.ToolbarParts([Glyph("🔧"), ScreenPane.ToolbarItem.PathOf(@"D:\x")], [Glyph("🎓"), ScreenPane.ToolbarItem.ModelOf("llama")]);
        pane.Show();
        pane.ShowInput("abc", 3);

        // "🔧 D:\x" 0–6; "🎓 llama" ending on the last cell (38): 🎓 31–32, "llama" 34–38.
        Assert.Contains("\n🔧 " + @"D:\x" + new string(' ', 24) + "🎓 llama", Output);
        Assert.True(pane.TryHitToolbar(0, 103, out var tool));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🔧", 0), tool);
        Assert.True(pane.TryHitToolbar(4, 103, out tool));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Path, "", 3), tool);
        Assert.True(pane.TryHitToolbar(32, 103, out tool));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🎓", 31), tool);
        Assert.True(pane.TryHitToolbar(36, 103, out tool));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Model, "", 34), tool);
        Assert.True(pane.TryHitToolbar(20, 103, out tool));
        Assert.Equal(ScreenPane.ToolbarZone.Row, tool.Zone);
    }

    /// <summary>
    /// A narrow toolbar, in the user's order: the model's name shrinks before any glyph goes; then the right group gives way from its
    /// inner end (its outer glyphs kept behind +N), the left group whole.
    /// </summary>
    [Fact]
    public void TheToolbar_TheNameGivesWayFirst_ThenTheRightGroup_TheLeftKeptWhole()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Hint = () => "idle";
        string left = "🔧 🎓 🌐 🔧 🎓 🌐 🔧 🎓 🌐 🔧";   // 29 cells
        var leftItems = left.Split(' ').Select(Glyph).ToList();
        pane.Toolbar = () => new ScreenPane.ToolbarParts(leftItems, [ScreenPane.ToolbarItem.ModelOf("qwen3-30b-a3b")]);
        pane.Show();
        pane.ShowInput("abc", 3);
        Assert.Contains("\n" + left + "  " + ScreenPane.Fit("qwen3-30b-a3b", 8), Output);

        pane.Toolbar = () => new ScreenPane.ToolbarParts(leftItems, "🎨 🎲 🎯 🎮".Split(' ').Select(Glyph).ToList());
        int mark = Output.Length;
        _time.Advance(ScreenPane.Tick);   // the toolbar follows on the tick
        Assert.Contains(left + "  +2 🎯 🎮", Output[mark..]);
        Assert.True(pane.TryHitToolbar(34, 103, out var tool));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🎯", 34), tool);
        Assert.True(pane.TryHitToolbar(0, 103, out tool));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🔧", 0), tool);
    }

    /// <summary>The status strip pinned right: it stands ahead of the model at the right edge, and its glyph answers there.</summary>
    [Fact]
    public void HintLayout_TheStripPinnedRight_StandsAtTheRightEdge_ItsGlyphAZoneThere()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Strip = () => "🔊";
        pane.Trailer = () => "llama";
        pane.TrailerMark = () => "◕";
        pane.HintLayout = () => [ScreenPane.HintSlot.Of("1k"), ScreenPane.HintSlot.Strip.AtRight(), ScreenPane.HintSlot.Model.AtRight()];
        pane.Show();
        pane.ShowInput("abc", 3);

        // "1k", blanks, "🔊 · llama ◕" from 27: 🔊 27–28, "llama ◕" 32–38 (the mark at 38).
        Assert.Contains("\n1k" + new string(' ', 25) + "🔊 · llama ◕", Output);
        Assert.True(pane.TryHitHint(28, 102, out var hit));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", 27), hit);
        Assert.True(pane.TryHitHint(33, 102, out hit));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Trailer, "", 32), hit);
        Assert.True(pane.TryHitHint(38, 102, out hit));
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Mark, "", 38), hit);
        Assert.True(pane.TryHitHint(0, 102, out hit));
        Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);
    }

    /// <summary>A narrow hint row: the right group gives way from its inner end (cut from the front), the left kept whole.</summary>
    [Fact]
    public void HintLayout_ARowTooNarrow_TheRightGroupGivesWay_TheLeftKeptWhole()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Trailer = () => "";
        pane.HintLayout = () => [ScreenPane.HintSlot.Of(new string('x', 30)), ScreenPane.HintSlot.Of("abcdefghij").AtRight()];
        pane.Show();
        pane.ShowInput("abc", 3);

        Assert.Contains("\n" + new string('x', 30) + "  " + ScreenPane.FitTail("abcdefghij", 7), Output);
    }

    /// <summary>
    /// The busy row follows the sides (2026-10-13, the user's pick): the spinner and its label lead the left group, the strip after them;
    /// the tally and the model pinned right stand at the right edge. The spinner is the usage zone, the tally one of its own, the strip
    /// a button where it stands; the model is no button under a reply.
    /// </summary>
    [Fact]
    public void TheBusyRow_FollowsTheSides_TheSpinnerLeads_TheTallyAndTheModelAtTheRight()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Hint = () => "idle";
        pane.Strip = () => "🔊";
        pane.BusyUsage = () => "1.2k";
        pane.Trailer = () => "llama";
        pane.HintLayout = () => [ScreenPane.HintSlot.Strip, ScreenPane.HintSlot.UsageOf("").AtRight(), ScreenPane.HintSlot.Model.AtRight()];
        pane.Show();
        pane.ShowInput("", 0);

        using (pane.BeginBusy("thinking"))
        {
            string spinner = Theme.SpinnerFrames[0] + " " + ScreenPane.BusyText("thinking", TimeSpan.Zero);
            int strip = TextCells.Width(spinner + ScreenPane.HintSeparator);
            Assert.Contains(spinner + ScreenPane.HintSeparator + "🔊", Output);
            Assert.Contains("1.2k · llama", Output);
            Assert.True(pane.TryHitHint(0, 102, out var hit));
            Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Usage, "", 0), hit);
            Assert.True(pane.TryHitHint(strip + 1, 102, out hit));
            Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", strip), hit);
            Assert.True(pane.TryHitHint(28, 102, out hit));   // "1.2k · llama" from 27
            Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Usage, "", 27), hit);
            Assert.True(pane.TryHitHint(35, 102, out hit));   // the model: no button under a reply
            Assert.Equal(ScreenPane.HintZone.Row, hit.Zone);
        }
    }
}
