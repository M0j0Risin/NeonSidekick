using System.Globalization;

namespace NeonSidekick.Llm;

/// <summary>
/// The turn loop's own words about a call it stopped (2026-10-30, review F37: they were constants of <see cref="Assistant"/> and
/// <see cref="ToolArgumentWatch"/>, apart from the area's other text, which is all about the model's errors).
/// </summary>
public static class TurnText
{
    /// <summary>The name a call goes by on the busy row while no chunk has named it. Pinned.</summary>
    public const string UnnamedCall = "(unnamed)";

    /// <summary>
    /// What follows <see cref="RunawayNotice"/> when the user is the one to go on (2026-10-30, review F96): not under an audit or Ralph
    /// pass, whose own stop line says how to go on. Pinned.
    /// </summary>
    public const string AskAgain = " Ask again.";

    /// <summary>
    /// The error a turn ends on when a call streamed past its tool's ceiling (2026-10-29, session 832: a <c>present_audit</c> retry wrote
    /// 60,259 tokens of arguments for seven minutes): the request was dropped and nothing ran. The way on is the host's to add
    /// (<see cref="TurnEvent.Notice.AskAgain"/>). Pinned.
    /// </summary>
    public static string RunawayNotice(string name, int ceiling) =>
        string.Create(CultureInfo.InvariantCulture, $"Stopped: the model's call to {name} ran past {ceiling:N0} characters, more than it ever takes, so nothing ran. It was likely repeating itself.");

    /// <summary>The <c>--log</c> line for a call stopped past its ceiling (2026-10-29). Pinned.</summary>
    internal static string RunawayLogLine(string name, long chars, int ceiling) =>
        string.Create(CultureInfo.InvariantCulture, $"Arguments for tool '{name}' passed {chars:N0} characters (its ceiling is {ceiling:N0}); the request was stopped.");

    /// <summary>
    /// The line a result cut to fit the context window carries between its head and tail (2026-11-09, the mid-turn guard's cut,
    /// <see cref="ConversationCompactor.CutLatestResults"/>): <c>[… 140,000 characters cut here to fit the context window; read the rest in
    /// smaller pieces or with a narrower call; don't run a command again to see its output]</c> (the advice since 2026-11-10, review P6-2/P9-3:
    /// a model re-ran the command to see what was cut). In the shape of the app's other cut mark (<c>SkillLearner.CutMark</c>). It names no
    /// argument, since it lands in every tool's results and their range arguments differ. A write holding it is refused
    /// (<see cref="ConversationCompactor.HoldsCutMark"/>, which reads the opening up to "context window"). Pinned.
    /// </summary>
    public static string CutMarker(long cut) =>
        string.Create(CultureInfo.InvariantCulture, $"[… {cut:N0} characters cut here to fit the context window; read the rest in smaller pieces or with a narrower call; don't run a command again to see its output]");

    /// <summary>
    /// <see cref="CutMarker"/> for a call whose result was cut before in the turn (2026-11-10, review P4-1/P15-1, in the spirit of
    /// <see cref="Assistant.CutOffAgainResult"/>): the same opening, so <see cref="ConversationCompactor.HoldsCutMark"/> refuses a copy, and
    /// the text the model needed kept; a marker, not an <c>Error:</c> result, so no error is counted. Pinned.
    /// </summary>
    public static string CutAgainMarker(long cut) =>
        string.Create(CultureInfo.InvariantCulture, $"[… {cut:N0} characters cut here to fit the context window; this call was cut before, so don't call it again: use what you have, or a narrower call]");

    /// <summary>
    /// The line a picture-only round's carrier gets when its pictures were cut before in the turn (2026-11-10, review P4-1): the calls that
    /// fetched them are told not to fetch them again. Pinned.
    /// </summary>
    public const string PicturesCutAgainNote = "(these pictures were cut to fit the context window before, so don't fetch them again: use what you have)";

    /// <summary>
    /// The transcript line after the guard pruned: <c>(✂️ context at 85%: pruned 23 tool results from this turn)</c>; with long call
    /// arguments stubbed too (2026-10-17), <c>pruned 3 tool results and 2 call arguments</c>, or the arguments alone. Here since 2026-11-10
    /// (review P1-10). Pinned.
    /// </summary>
    public static string TurnPrunedNotice(int percent, int pruned, int arguments = 0) =>
        "(" + Assistant.PruneGlyph + "context at " + percent.ToString(CultureInfo.InvariantCulture) + "%: pruned " + PrunedWords(pruned, arguments) + " from this turn)";

    /// <summary>
    /// The transcript line after the guard cut the newest round to fit (2026-11-09): <c>(✂️ context at 97%: cut 2 tool results to fit,
    /// 312.0k → 61.0k characters)</c>, <c>cut 1 tool result and 3 pictures to fit, …</c> when the carrier's pictures went too, and
    /// <c>cut 2 pictures to fit</c> with no figures when only they did. With room kept for the reply (2026-11-10, review P24-1), it names
    /// it: <c>context at 76% + 19% kept for the reply</c>. One line a round, however many results; the log has each.
    /// </summary>
    public static string TurnCutNotice(int percent, int replyPercent, IReadOnlyList<ConversationCompactor.CutEntry> cuts, int pictures)
    {
        ArgumentNullException.ThrowIfNull(cuts);
        string what = Both(cuts.Count, "tool result", pictures, "picture");
        string figures = cuts.Count == 0 ? ""
            : ", " + App.UsageText.CompactNumber(cuts.Sum(c => (long)c.Before)) + " → " + App.UsageText.CompactNumber(cuts.Sum(c => (long)c.After)) + " characters";
        string reply = replyPercent > 0 ? " + " + replyPercent.ToString(CultureInfo.InvariantCulture) + "% kept for the reply" : "";
        return "(" + Assistant.PruneGlyph + "context at " + percent.ToString(CultureInfo.InvariantCulture) + "%" + reply + ": cut " + what + " to fit" + figures + ")";
    }

    /// <summary><c>23 tool results</c>, <c>3 tool results and 2 call arguments</c>, <c>2 call arguments</c> (2026-10-17): what a prune stubbed, for its notices.</summary>
    public static string PrunedWords(int results, int arguments) => Both(results, "tool result", arguments, "call argument");

    /// <summary>The error line when the guard stopped the turn: <c>🛑 Stopped at 85% …</c>. Here since 2026-11-10 (review P1-10). Pinned.</summary>
    public static string TurnStoppedNotice(int percent) =>
        Assistant.StopGlyph + "Stopped at " + percent.ToString(CultureInfo.InvariantCulture) + "% of the context window (LLM tool compact mode is stop); /compact or /clear before continuing.";

    /// <summary>
    /// The line before the shrink after the server said the request was too long (2026-11-10, review P12-3: the cause first): <c>(✂️ the
    /// server said the request was too long (41,200 of 32,768 tokens))</c>, the figures only when both are known (<see cref="ContextOverflow.Figures"/>).
    /// </summary>
    public static string OverflowNotice(ContextOverflow overflow)
    {
        ArgumentNullException.ThrowIfNull(overflow);
        return "(" + Assistant.PruneGlyph + "the server said the request was too long" + overflow.Figures + ")";
    }

    /// <summary>
    /// The line when the mid-turn summary was skipped because the server refused the summarizer's own request as too long (2026-11-10,
    /// review P11-3): the turn goes on, so it is no error. Pinned.
    /// </summary>
    public const string SummarySkippedNotice = "(" + Assistant.CompactGlyph + "summary skipped: the server said its request was too long)";

    /// <summary>The line after the shrink, before the round is sent again (2026-11-10, review P12-3). Pinned.</summary>
    public const string OverflowRetryNotice = "(" + Assistant.PruneGlyph + "shrunk and sent again)";

    /// <summary><c>N noun</c> and <c>M other</c> joined, either alone when the other is none (2026-11-10, review P4-8: one joiner for the guard's notices).</summary>
    private static string Both(int first, string firstNoun, int second, string secondNoun) =>
        second == 0 ? Assistant.Plural(first, firstNoun)
        : first == 0 ? Assistant.Plural(second, secondNoun)
        : Assistant.Plural(first, firstNoun) + " and " + Assistant.Plural(second, secondNoun);

    /// <summary><paramref name="notice"/> as the user reads it: <see cref="AskAgain"/> after a notice that asks for it, unless a loop's pass runs (F96).</summary>
    public static string Shown(TurnEvent.Notice notice, bool loopPass)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return notice.AskAgain && !loopPass ? notice.Text + AskAgain : notice.Text;
    }
}
