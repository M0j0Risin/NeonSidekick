using System.Globalization;

namespace NeonSidekick.UI;

/// <summary>
/// The scroll row's wording while the transcript is scrolled up (2026-09-18; its own class since 2026-11-02, the review, as
/// <see cref="CodeFoldText"/> is a fold's): <c>⇡ 12 rows below · PgUp/PgDn scroll · Ctrl+End bottom</c>, or with rows written since the
/// view left the bottom <c>↓ 42 new rows · message 12 of 30 · Ctrl+End bottom</c>. Most useful first: the row is cut from its end. Pinned.
/// </summary>
public static class ScrollHintText
{
    /// <summary>The rows-below count's arrow.</summary>
    public const string BelowArrow = "⇡ ";

    /// <summary>The new-rows count's arrow (2026-11-01).</summary>
    public const string NewArrow = "↓ ";

    /// <summary>What the keys do while scrolled.</summary>
    public const string ScrollKeys = "PgUp/PgDn scroll";

    /// <summary>The way back down.</summary>
    public const string BottomKey = "Ctrl+End bottom";

    /// <summary>
    /// The row's text: <paramref name="below"/> rows under the region, or with <paramref name="fresh"/> rows written since the view left
    /// the bottom the new-rows count; the message holding the top row after it while <paramref name="message"/> and
    /// <paramref name="messages"/> are known (0: none).
    /// </summary>
    public static string Hint(int below, int fresh = 0, int message = 0, int messages = 0)
    {
        string where = message > 0 && messages > 0 ? ScreenPane.HintSeparator + Message(message, messages) : "";
        return fresh > 0
            ? NewArrow + NewRows(fresh) + where + ScreenPane.HintSeparator + BottomKey
            : BelowArrow + RowsBelow(below) + where + ScreenPane.HintSeparator + ScrollKeys + ScreenPane.HintSeparator + BottomKey;
    }

    /// <summary><c>12 rows below</c>, <c>1 row below</c>.</summary>
    public static string RowsBelow(int rows) => rows.ToString(CultureInfo.InvariantCulture) + (rows == 1 ? " row below" : " rows below");

    /// <summary><c>42 new rows</c>, <c>1 new row</c> (2026-11-01).</summary>
    public static string NewRows(int rows) => rows.ToString(CultureInfo.InvariantCulture) + (rows == 1 ? " new row" : " new rows");

    /// <summary><c>message 12 of 30</c> (2026-11-01).</summary>
    public static string Message(int message, int messages) =>
        "message " + message.ToString(CultureInfo.InvariantCulture) + " of " + messages.ToString(CultureInfo.InvariantCulture);
}
