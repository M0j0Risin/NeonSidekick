using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The setting <c>Toolbar order</c> (2026-10-12, the user's ask: "freely arrange the order" of the hint bar, the toolbar, the
/// performance bar and the home bar, the hint bar under the performance bar for one; the home bar picker's move keys again):
/// the four rows under the pane's lower rule in the user's order, saved in <see cref="AppSettingsData.BarOrder"/>. Null is
/// <see cref="Names"/>' order, the one the pane always had, and what the page's <c>⊡ default</c> puts back. Only the order:
/// each bar's own row (<c>Toolbar</c>, <c>Performance bar</c>, <c>Home bar</c>; <c>Show …</c> until 2026-10-12, and all four under
/// Tool bars since, this one <c>Order</c> on the pane) shows or hides it, and one that
/// is off keeps its place for when it comes back. A short window still drops the home bar first, then the performance bar, then
/// the toolbar, wherever they sit. <see cref="Order"/> is <see cref="ItemOrder.Resolve"/>, so an unknown word is dropped without a
/// warning and a missing id goes back beside its neighbour. Pure.
/// </summary>
public static class PaneBars
{
    public const string Hint = "hint";
    public const string Toolbar = "toolbar";
    public const string Perf = "perf";
    public const string Home = "home";

    /// <summary>Every bar in the default order, top to bottom. Pinned.</summary>
    public static readonly string[] Names = [Hint, Toolbar, Perf, Home];

    /// <summary>A bar's name on the page and in the row's value. Pinned.</summary>
    public static string Title(string id) => id switch
    {
        Hint => "Hint bar",
        Toolbar => "Toolbar",
        Perf => "Performance bar",
        Home => "Home bar",
        _ => id,
    };

    /// <summary>
    /// What a bar is, dim after its name on the page. Pinned. Until 2026-10-12 the row that shows or hides it, which read
    /// <c>Toolbar  Toolbar</c> once the rows took the bars' own names (the user's): what is on it since.
    /// </summary>
    public static string Describe(string id) => id switch
    {
        Hint => "always shown; the busy row under a reply",
        Toolbar => "the panes' glyphs and the path",
        Perf => "the meters",
        Home => "Home Assistant's chips",
        _ => "",
    };

    /// <summary>The page's names are padded to this, so the descriptions line up (<c>Performance bar</c> is 15).</summary>
    public const int TitleWidth = 17;

    /// <summary>One row of the page: the name and <see cref="Describe"/> dimmed, <see cref="OffMark"/> before it for a bar that is off. Pinned.</summary>
    public static string Label(string id, bool shown) =>
        Markup.Escape(Title(id).PadRight(TitleWidth)) + Theme.DimMarkup((shown ? "" : OffMark) + Describe(id));

    /// <summary>A bar that is off on the page: it keeps its place, it just is not drawn. Pinned.</summary>
    public const string OffMark = "off · ";

    /// <summary>Whether <paramref name="id"/>'s own setting shows it under <paramref name="data"/>: the hint bar always; the others while something is on them.</summary>
    public static bool Shown(string id, AppSettingsData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return id switch
        {
            Toolbar => App.ToolbarItems.Resolve(data.ToolbarItems).Count > 0,
            Perf => PerfBarItems.Resolve(data.PerformanceBarItems).Count > 0,
            Home => App.HomeBarItems.Resolve(data.HomeBarItems).Count > 0,
            _ => true,
        };
    }

    /// <summary>The bars' order for <paramref name="saved"/>: every bar, <see cref="Names"/>' order for null (<see cref="ItemOrder.Resolve"/>).</summary>
    public static List<string> Order(IReadOnlyList<string>? saved) => ItemOrder.Resolve(saved, Names);

    /// <summary>What saves for <paramref name="order"/>: null for <see cref="Names"/>' own order (<see cref="ItemOrder.Save"/>).</summary>
    public static List<string>? SaveOrder(IReadOnlyList<string> order) => ItemOrder.Save(order, Names);

    /// <summary>The pane's order for <paramref name="saved"/> (<see cref="ScreenPane.BarOrder"/>): <see cref="Order"/> as <see cref="PaneBar"/>s.</summary>
    public static IReadOnlyList<PaneBar> Bars(IReadOnlyList<string>? saved) => Order(saved).Select(id => id switch
    {
        Toolbar => PaneBar.Toolbar,
        Perf => PaneBar.Perf,
        Home => PaneBar.Home,
        _ => PaneBar.Hint,
    }).ToList();

    /// <summary>The <c>Toolbar order</c> row's value: <see cref="DefaultValue"/> for the default order, else the names top to bottom. Pinned.</summary>
    public static string Value(IReadOnlyList<string>? saved)
    {
        var order = Order(saved);
        return SaveOrder(order) is null ? DefaultValue : string.Join(", ", order.Select(Title));
    }

    /// <summary>The row's value in the default order. Pinned.</summary>
    public const string DefaultValue = "default";
}
