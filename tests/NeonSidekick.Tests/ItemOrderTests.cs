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

    /// <summary>
    /// The two groups (2026-10-13, the user's ask: any item of the hint bar and the tool bar pinned left or right): a split keeps the
    /// order within each side; a step inside a group moves the item there, a step past the group's edge crosses into the other group
    /// (the index unchanged), Home/End stay within the item's own group.
    /// </summary>
    [Fact]
    public void MoveInGroups_MovesWithinAGroup_CrossesAtItsEdge_TopAndBottomStayInIt()
    {
        var sides = ItemOrder.Split(Names, new HashSet<string> { "b", "d" });
        Assert.Equal(["a", "c"], sides.Left);
        Assert.Equal(["b", "d"], sides.Right);
        Assert.Equal(["a", "c", "b", "d"], sides.All);
        Assert.True(sides.IsRight("d"));

        AssertSides(["c", "a"], ["b", "d"], 0, ItemOrder.MoveInGroups(sides, 1, -1));
        AssertSides(["a"], ["c", "b", "d"], 1, ItemOrder.MoveInGroups(sides, 1, 1));       // off the left's end: the right's first
        AssertSides(["a", "c", "b"], ["d"], 2, ItemOrder.MoveInGroups(sides, 2, -1));      // off the right's start: the left's last
        AssertSides(["a", "c"], ["d", "b"], 3, ItemOrder.MoveInGroups(sides, 2, 1));
        AssertSides(["a", "c"], ["b", "d"], 3, ItemOrder.MoveInGroups(sides, 3, 1));       // the right's last stays
        AssertSides(["a", "c"], ["b", "d"], 0, ItemOrder.MoveInGroups(sides, 0, -1));      // the left's first stays
        AssertSides(["a", "c"], ["d", "b"], 2, ItemOrder.MoveInGroups(sides, 3, HomeBarItems.Top));      // the top of its own group
        AssertSides(["c", "a"], ["b", "d"], 1, ItemOrder.MoveInGroups(sides, 0, HomeBarItems.Bottom));   // the bottom of its own group
        AssertSides(["a", "c"], ["b", "d"], 9, ItemOrder.MoveInGroups(sides, 9, 1));       // out of range: nothing
    }

    /// <summary>Shift+←/→ (2026-10-13): to the right group's start, to the left group's end — next to the gap; already there, nothing.</summary>
    [Fact]
    public void ToSide_PinsTheItemNextToTheGap()
    {
        var sides = ItemOrder.Split(Names, new HashSet<string> { "c" });
        AssertSides(["b", "d"], ["a", "c"], 2, ItemOrder.ToSide(sides, 0, toRight: true));
        AssertSides(["a", "b", "d", "c"], [], 3, ItemOrder.ToSide(sides, 3, toRight: false));
        AssertSides(["a", "b", "d"], ["c"], 0, ItemOrder.ToSide(sides, 0, toRight: false));   // already left
        AssertSides(["a", "b", "d"], ["c"], 3, ItemOrder.ToSide(sides, 3, toRight: true));    // already right
    }

    [Fact]
    public void TheRightGroup_ResolvesAndSaves_NullForTheDefaults()
    {
        Assert.True(ItemOrder.ResolveRight(null, Names, ["d"]).SetEquals(["d"]));
        Assert.True(ItemOrder.ResolveRight([" B ", "gone", null!], Names, ["d"]).SetEquals(["b"]));
        Assert.Empty(ItemOrder.ResolveRight([], Names, ["d"]));
        Assert.Null(ItemOrder.SaveRight(["d"], Names, ["d"]));
        Assert.Equal(["b", "d"], ItemOrder.SaveRight(["d", "b"], Names, ["d"]));
        Assert.Equal([], ItemOrder.SaveRight([], Names, ["d"]));

        Assert.True(ToolbarItems.ResolveRight(null).SetEquals([ToolbarItems.Path, ToolbarItems.Model]));   // the row as it was
        Assert.True(HintBarItems.ResolveRight(null).SetEquals([HintBarItems.Model, HintBarItems.WorkingDirectory]));
    }

    private static void AssertSides(string[] left, string[] right, int index, (ItemOrder.Sided Groups, int Index) moved)
    {
        Assert.Equal(left, moved.Groups.Left);
        Assert.Equal(right, moved.Groups.Right);
        Assert.Equal(index, moved.Index);
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

    /// <summary>The toolbar's order (2026-10-13): every item free, the path no longer held last; by default the path and the name are the right group.</summary>
    [Fact]
    public void TheToolbarsOrder_IsEveryItemFree_ThePathAndTheNameRightByDefault()
    {
        Assert.Equal(ToolbarItems.Names, ToolbarItems.Order(null));
        var order = ToolbarItems.Order(["path", "perf", "settings"]);
        Assert.Equal(["path", "model", "perf", "settings"], order.Take(4));   // the model after the path, its neighbour
        Assert.Equal(ToolbarItems.Names.Length, order.Count);
        var sides = ToolbarItems.Sides(null, null);
        Assert.Equal([ToolbarItems.Path, ToolbarItems.Model], sides.Right);
        Assert.Equal(ToolbarItems.Names, sides.All);
        var moved = ToolbarItems.Sides(["model", "settings"], ["settings"]);
        Assert.Equal(["settings"], moved.Right);
        Assert.Equal(["model", "profile"], moved.Left.Take(2));
    }

    [Fact]
    public void TheToolbarStrip_DrawsInTheSavedOrder()
    {
        var items = ToolbarItems.Resolve(["settings", "tools", "skills"]);
        Assert.Equal(string.Join(ChatScreen.GlyphSeparator, ChatScreen.SettingsToolGlyph, ChatScreen.ToolsToolGlyph, ChatScreen.SkillsToolGlyph), ChatScreen.ToolbarStripFor(items, new AppSettingsData()).Strip);
        var (strip, _) =ChatScreen.ToolbarStripFor(items, new AppSettingsData { ToolbarOrder = ["skills", "settings"] });
        Assert.Equal(string.Join(ChatScreen.GlyphSeparator, ChatScreen.SkillsToolGlyph, ChatScreen.SettingsToolGlyph, ChatScreen.ToolsToolGlyph), strip);
        // The left group's glyphs, then the right's (2026-10-13).
        (strip, _) = ChatScreen.ToolbarStripFor(items, new AppSettingsData { ToolbarRight = ["settings"] });
        Assert.Equal(string.Join(ChatScreen.GlyphSeparator, ChatScreen.ToolsToolGlyph, ChatScreen.SkillsToolGlyph, ChatScreen.SettingsToolGlyph), strip);
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
        var source = new AppSettingsData { ToolbarOrder = ["perf"], PerformanceBarOrder = ["ram"], ToolbarRight = ["perf"], HintBarRight = ["usage"] };
        var copy = AppSettings.Copy(source);
        Assert.Equal(["perf"], copy.ToolbarOrder);
        Assert.Equal(["perf"], copy.ToolbarRight);
        Assert.Equal(["usage"], copy.HintBarRight);
        Assert.NotSame(source.ToolbarRight, copy.ToolbarRight);
        Assert.NotSame(source.HintBarRight, copy.HintBarRight);
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
