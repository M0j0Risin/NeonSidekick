using NeonSidekick.App;
using NeonSidekick.Llm;

namespace NeonSidekick.WebUi;

/// <summary>
/// The page's usage card (2026-10-16, the user's ask: the <c>/usage</c> lines in a text box read as amateurish): the PC pane's own sections
/// (<see cref="UsageText.Sections"/>) as a meter and tiles. The meter is the Context section — the tokens in use over the window, written as
/// the status row writes them, and the share; the tiles are the other sections in the pane's order, their rows as the pane shows them. While
/// nothing was counted the card says so, and the extras (<c>/claude</c>, the bots, the advisors) stand in a tile of their own. Pure.
/// </summary>
public static class WebUsageView
{
    public static WebUsageCard Of(TokenTally tally, ContextLength? window)
    {
        ArgumentNullException.ThrowIfNull(tally);
        var shown = UsageText.Sections(tally, window);
        if (shown.Nothing)
        {
            return new WebUsageCard(UsageText.Label, null, [], UsageText.NothingCounted);
        }

        long inUse = tally.LastRequest.Total;
        var context = new WebUsageContext(
            UsageText.ContextHeading,
            inUse > 0 ? UsageText.CompactNumber(inUse) : null,
            window is { } w ? UsageText.CompactNumber(w.Tokens) : null,
            UsageText.Percent(inUse, window),
            window?.Source ?? UsageText.WindowUnknown);
        var tiles = shown.Sections.Skip(1).Select(Tile).ToList();
        if (shown.NothingYet && shown.Apart.Count > 0)
        {
            tiles.Add(Tile(new UsageSection(UsageText.SessionHeading, "", shown.Apart)));
        }

        return new WebUsageCard(UsageText.Label, context, tiles, shown.NothingYet ? UsageText.NothingCounted : null);
    }

    private static WebUsageSection Tile(UsageSection section) =>
        new(section.Heading, section.Note, [.. section.Rows.Select(r => new WebUsageRow(r.Label, r.Value))]);
}
