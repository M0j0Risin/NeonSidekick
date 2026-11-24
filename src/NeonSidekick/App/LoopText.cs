using System.Globalization;

namespace NeonSidekick.App;

/// <summary>
/// A running <c>/loop</c>'s item on the lower rule and its "end after this pass" (2026-11-23, the user's ask): <c>⟳ 3/10</c> while pass 3
/// of 10 runs or waits before the next, <c>⟳ 3</c> for <c>/loop infinite</c>; a double-click on it ends the loop after the pass in progress
/// (<see cref="EndingTail"/> until then), where Esc ends it at once. The loop's other notices stay beside <c>ChatScreen.LoopTurnNotice</c>.
/// </summary>
public static class LoopText
{
    /// <summary>The loop's glyph (U+27F3, one cell). Pinned.</summary>
    public const string Glyph = "⟳";

    /// <summary>What a loop's item on the lower rule gains once a double-click asked it to end after this pass (🔁, 🔎 and ⟳ alike). Pinned.</summary>
    public const string EndingTail = " · ending";

    /// <summary>The item: <c>⟳ 3/10</c>, or <c>⟳ 3</c> with no count. Pinned.</summary>
    public static string RuleTitle(int pass, int? count) =>
        count is int total
            ? Glyph + " " + pass.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture)
            : Glyph + " " + pass.ToString(CultureInfo.InvariantCulture);

    /// <summary>A message loop ended by the double-click, worded as <c>ChatScreen.LoopStoppedNotice</c>; <paramref name="ran"/> counts the passes sent.</summary>
    public static string EndedNotice(int ran) => $"(loop ended after {UsageText.Plural(ran, "message", "messages")}, as asked)";

    /// <summary>A command loop ended by the double-click, worded as <c>ChatScreen.LoopCommandStoppedNotice</c>.</summary>
    public static string CommandEndedNotice(int ran) => $"(loop ended after {UsageText.Plural(ran, "run", "runs")}, as asked)";
}
