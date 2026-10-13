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
        Assert.Equal([ToolbarItems.Path, ToolbarItems.Model], order[^2..]);   // the model's name after the path, its default place (2026-10-12)
        Assert.Equal(ToolbarItems.Names.Length, order.Count);
    }

    /// <summary>
    /// The toolbar's Model name (2026-10-12, the user's ask: free to move, after the path by default): an order saved before it puts it
    /// after the path; saved ahead of the path it stays there; anything else saved after the path goes back ahead of it.
    /// </summary>
    [Fact]
    public void TheToolbarsOrder_TheModelAlonePassesThePath()
    {
        List<string> old = [.. ToolbarItems.Names.Where(id => id != ToolbarItems.Model)];
        Assert.Equal([ToolbarItems.Path, ToolbarItems.Model], ToolbarItems.Order(old)[^2..]);
        var ahead = ToolbarItems.Order(["model", "settings"]);
        Assert.Equal(["model", "settings"], ahead.Take(2));
        Assert.Equal(ToolbarItems.Path, ahead[^1]);
        var mended = ToolbarItems.Order(["settings", "path", "tools", "model"]);
        Assert.Equal([ToolbarItems.Path, ToolbarItems.Model], mended[^2..]);
        Assert.True(mended.IndexOf("tools") < mended.IndexOf("path"));
    }

    [Fact]
    public void Move_TheModelStepsOverThePath_TheGlyphsStopAtIt()
    {
        var order = ToolbarItems.Order(null);
        int path = order.IndexOf(ToolbarItems.Path);
        int model = order.IndexOf(ToolbarItems.Model);
        var up = ItemOrder.Move(order, model, -1, ToolbarItems.Path, ToolbarItems.Passers);
        Assert.Equal([ToolbarItems.Model, ToolbarItems.Path], up.Order[^2..]);
        Assert.Equal(path, up.Index);
        var top = ItemOrder.Move(order, model, HomeBarItems.Top, ToolbarItems.Path, ToolbarItems.Passers);
        Assert.Equal((ToolbarItems.Model, 0), (top.Order[0], top.Index));
        var down = ItemOrder.Move(up.Order, up.Index, 1, ToolbarItems.Path, ToolbarItems.Passers);
        Assert.Equal(order, down.Order);   // and back past it
        var glyph = ItemOrder.Move(order, path - 1, HomeBarItems.Bottom, ToolbarItems.Path, ToolbarItems.Passers);
        Assert.Equal(path - 1, glyph.Index);   // a glyph stops ahead of the path
        Assert.Equal(order, ItemOrder.Move(order, path, -1, ToolbarItems.Path, ToolbarItems.Passers).Order);   // the path never moves
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

    /// <summary>
    /// Toolbar order (2026-10-12, the user's ask): the four bars under the lower rule, null their default order; a saved order
    /// mended as the others are; the row's value; the pane's <see cref="PaneBar"/>s; a copy keeps it apart.
    /// </summary>
    [Fact]
    public void TheBarsOrder_IsTheFourBars_DefaultForNull_TheRowNamesThem()
    {
        Assert.Equal(["hint", "toolbar", "perf", "home"], PaneBars.Order(null));
        Assert.Equal(["perf", "home", "hint", "toolbar"], PaneBars.Order([" PERF ", "spark", "hint"]));   // toolbar after hint, home after perf
        Assert.Null(PaneBars.SaveOrder(PaneBars.Order(null)));
        Assert.Equal(PaneBars.DefaultValue, PaneBars.Value(null));
        Assert.Equal(PaneBars.DefaultValue, PaneBars.Value(["hint", "toolbar"]));
        Assert.Equal("Tool bar, Performance bar, Hint bar, Home bar", PaneBars.Value(["toolbar", "perf", "hint", "home"]));   // "Toolbar" until later on 2026-10-12
        Assert.Equal([PaneBar.Toolbar, PaneBar.Perf, PaneBar.Hint, PaneBar.Home], PaneBars.Bars(["toolbar", "perf", "hint", "home"]));
        Assert.Equal(ScreenPane.DefaultBarOrder, PaneBars.Bars(null));

        Assert.True(PaneBars.Shown(PaneBars.Hint, new AppSettingsData()));
        Assert.False(PaneBars.Shown(PaneBars.Home, new AppSettingsData()));
        Assert.False(PaneBars.Shown(PaneBars.Perf, new AppSettingsData { PerformanceBarItems = [] }));
        Assert.True(PaneBars.Shown(PaneBars.Perf, new AppSettingsData()));   // the default four

        var source = new AppSettingsData { BarOrder = ["home"] };
        var copy = AppSettings.Copy(source);
        Assert.Equal(["home"], copy.BarOrder);
        Assert.NotSame(source.BarOrder, copy.BarOrder);
    }
}
