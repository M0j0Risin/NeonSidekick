using NeonSidekick.App;
using NeonSidekick.Perf;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The toolbar's and the performance bar's order (2026-10-12, the user's ask: the home bar picker's move keys on their
/// checklists): <see cref="ItemOrder"/>, <see cref="ToolbarItems.Order"/> and <see cref="PerfBarItems.Order"/>, and the bars
/// drawing in it.
/// </summary>
public class ItemOrderTests
{
    private static readonly string[] Names = ["a", "b", "c", "d"];

    [Fact]
    public void Resolve_NullIsTheNames_ASavedOrderIsKept_UnknownsDropped_MissingOnesAfterTheirPredecessor()
    {
        Assert.Equal(Names, ItemOrder.Resolve(null, Names));
        Assert.Equal(["d", "c", "b", "a"], ItemOrder.Resolve(["d", "c", "b", "a"], Names));
        Assert.Equal(["c", "d", "a", "b"], ItemOrder.Resolve([" C ", "gone", null!, "a", "c"], Names));   // trimmed, any case; d after c, b after a
        Assert.Equal(["a", "b", "d", "c"], ItemOrder.Resolve(["d", "c"], Names));                         // a first (nothing before it), b after a
    }

    [Fact]
    public void Resolve_KeepsThePinnedItemLast_AndMoveNeverMovesItOrPastIt()
    {
        Assert.Equal(["b", "c", "a", "d"], ItemOrder.Resolve(["d", "b", "a"], Names, pinnedLast: "d"));   // c after b, d pinned
        var order = ItemOrder.Resolve(null, Names, pinnedLast: "d");

        AssertMoved(["b", "c", "a", "d"], 2, ItemOrder.Move(order, 0, HomeBarItems.Bottom, "d"));
        AssertMoved(["a", "b", "d", "c"], 2, ItemOrder.Move(order, 3, -1));          // unpinned, it moves
        AssertMoved(["a", "b", "c", "d"], 3, ItemOrder.Move(order, 3, -1, "d"));     // pinned, it stays
        AssertMoved(["a", "b", "c", "d"], 2, ItemOrder.Move(order, 2, 1, "d"));      // the row above the pin stays above it
    }

    private static void AssertMoved(string[] expected, int index, (List<string> Order, int Index) moved)
    {
        Assert.Equal(expected, moved.Order);
        Assert.Equal(index, moved.Index);
    }

    [Fact]
    public void Save_IsNull_ForTheNamesOwnOrder()
    {
        Assert.Null(ItemOrder.Save(["a", "b", "c", "d"], Names));
        Assert.Equal(["b", "a", "c", "d"], ItemOrder.Save(["b", "a", "c", "d"], Names));
        Assert.Null(ToolbarItems.SaveOrder(ToolbarItems.Order(null)));
        Assert.Null(PerfBarItems.SaveOrder(PerfBarItems.Order(null)));
    }

    [Fact]
    public void TheToolbarsOrder_IsEveryItem_ThePathLast()
    {
        Assert.Equal(ToolbarItems.Names, ToolbarItems.Order(null));
        var order = ToolbarItems.Order(["path", "perf", "settings"]);
        Assert.Equal(["perf", "settings"], order.Take(2));
        Assert.Equal(ToolbarItems.Path, order[^1]);
        Assert.Equal(ToolbarItems.Names.Length, order.Count);
    }

    [Fact]
    public void TheToolbarStrip_DrawsInTheSavedOrder()
    {
        var items = ToolbarItems.Resolve(["settings", "tools", "skills"]);
        Assert.Equal(string.Join(ChatScreen.GlyphSeparator, ChatScreen.SettingsToolGlyph, ChatScreen.ToolsToolGlyph, ChatScreen.SkillsToolGlyph), ChatScreen.ToolbarStripFor(items, new AppSettingsData()).Strip);
        var (strip, _) =ChatScreen.ToolbarStripFor(items, new AppSettingsData { ToolbarOrder = ["skills", "settings"] });
        Assert.Equal(string.Join(ChatScreen.GlyphSeparator, ChatScreen.SkillsToolGlyph, ChatScreen.SettingsToolGlyph, ChatScreen.ToolsToolGlyph), strip);
    }

    [Fact]
    public void ThePerformanceBar_DrawsItsMeters_AndItsRowValue_InTheSavedOrder()
    {
        // A saved order is a full one (the checklist saves every meter); RAM first.
        List<string> saved = ["ram", "cpu", "gpu", "vram", "net", "netdown", "netup", "proc"];
        var items = new HashSet<string>(["cpu", "ram"], StringComparer.Ordinal);
        var reading = new PerfSnapshot(34, 50, null, null);
        string usual = PerfBar.Render(PerfBarStyle.Text, items, reading, 120)!.Text;
        string moved = PerfBar.Render(PerfBarStyle.Text, items, reading, 120, order: PerfBarItems.Order(saved))!.Text;
        Assert.True(usual.IndexOf("CPU", StringComparison.Ordinal) < usual.IndexOf("RAM", StringComparison.Ordinal));
        Assert.True(moved.IndexOf("RAM", StringComparison.Ordinal) < moved.IndexOf("CPU", StringComparison.Ordinal));

        Assert.Equal(saved, PerfBarItems.Order(saved));
        Assert.Equal("RAM, CPU · text", PerfBarItems.Value(["cpu", "ram"], "text", saved));
        Assert.Equal("CPU, RAM · text", PerfBarItems.Value(["cpu", "ram"], "text"));
    }

    [Fact]
    public void ACopy_KeepsBothOrders_ApartFromTheSource()
    {
        var source = new AppSettingsData { ToolbarOrder = ["perf"], PerformanceBarOrder = ["ram"] };
        var copy = AppSettings.Copy(source);
        Assert.Equal(["perf"], copy.ToolbarOrder);
        Assert.Equal(["ram"], copy.PerformanceBarOrder);
        Assert.NotSame(source.ToolbarOrder, copy.ToolbarOrder);
        Assert.NotSame(source.PerformanceBarOrder, copy.PerformanceBarOrder);
    }
}
