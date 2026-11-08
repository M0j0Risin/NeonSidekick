using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// Where a glyph stands on the toolbar row (2026-11-07, the user's ask: adding or removing a toolbar item broke the click tests, each
/// clicking a glyph by its column): worked out from the strip the screen draws for the test's own items, so the next item added or removed
/// moves nothing. <c>ChatScreenTests.ToolbarColumns_X_IsWhereTheDrawnRowHasEachGlyph</c> pins it against a drawn row.
/// </summary>
/// <remarks>
/// Right only for a bare order (2026-11-08, the review): the strip has no dividers, which the row draws as runs of their own, and no glyph
/// pinned right, which the row draws at the far edge. <see cref="X"/> throws on either rather than give a wrong column. The path and the
/// model name sit right by default (<see cref="ToolbarItems.RightDefaults"/>) and are drawn at the edge, not in the strip, so they don't count.
/// </remarks>
internal static class ToolbarColumns
{
    /// <summary>
    /// The column of <paramref name="glyph"/>'s first cell on the toolbar row <paramref name="settings"/> draw, for the items the screen
    /// shows (<paramref name="here"/>, the fixture's own filter, as the screen's <c>ToolbarItemHere</c>; 2026-11-08, the review).
    /// </summary>
    public static int X(AppSettingsData settings, string glyph, Func<string, bool> here)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(here);
        var sides = ToolbarItems.Sides(settings.ToolbarOrder, settings.ToolbarRight);
        if (sides.All.Any(ItemOrder.IsDivider) || sides.Right.Any(id => id != ToolbarItems.Path && id != ToolbarItems.Model))
        {
            throw new InvalidOperationException("ToolbarColumns.X needs a bare order: no divider, nothing pinned right but the path and the model name.");
        }

        var items = ToolbarItems.Resolve(settings.ToolbarItems).Where(here).ToHashSet(StringComparer.Ordinal);
        string strip = ChatScreen.ToolbarStripFor(items, settings).Strip;
        int at = strip.IndexOf(glyph, StringComparison.Ordinal);
        return at >= 0 ? TextCells.Width(strip[..at]) : throw new InvalidOperationException("The toolbar draws no " + glyph + ": " + strip);
    }
}
