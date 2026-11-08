using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// Where a glyph stands on the toolbar row (2026-11-07, the user's ask: adding or removing a toolbar item broke the click tests, each
/// clicking a glyph by its column): worked out from the strip the screen draws for the test's own items, so the next item added or removed
/// moves nothing. <c>ChatScreenTests.ToolbarColumns_X_IsWhereTheDrawnRowHasEachGlyph</c> pins it against a drawn row.
/// </summary>
internal static class ToolbarColumns
{
    /// <summary>The column of <paramref name="glyph"/>'s first cell on the toolbar row <paramref name="settings"/> draw.</summary>
    public static int X(AppSettingsData settings, string glyph)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var items = ToolbarItems.Resolve(settings.ToolbarItems).ToHashSet(StringComparer.Ordinal);
        string strip = ChatScreen.ToolbarStripFor(items, settings).Strip;
        int at = strip.IndexOf(glyph, StringComparison.Ordinal);
        return at >= 0 ? TextCells.Width(strip[..at]) : throw new InvalidOperationException("The toolbar draws no " + glyph + ": " + strip);
    }
}
