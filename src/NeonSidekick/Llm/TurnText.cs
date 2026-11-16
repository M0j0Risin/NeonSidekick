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

    /// <summary>
    /// The answer to a call carrying an argument its tool does not take (2026-11-15, feature idea #42: <c>web_search {limit: 5}</c> ran as if
    /// unfiltered, and the model believed it had filtered). Each key is named with what it likely should have been, then the tool's own names
    /// (the first <paramref name="takes"/>, <paramref name="moreTakes"/> counted, #76); nothing ran. Several keys read "doesn't take 'a' or 'b'"
    /// (#108: "takes no arguments 'a' and 'b'" read as a tool with none). <paramref name="keys"/> and <paramref name="takes"/> are already echoed
    /// (<see cref="Tools.ArgumentText.Echo"/>) and hinted by <see cref="NearestHint"/>, <see cref="SynonymHint"/>, <see cref="BothSentHint"/>,
    /// <see cref="CaseTwinHint"/> or <see cref="RowsShownHint"/>. Pinned.
    /// </summary>
    public static string UnknownArguments(string tool, IReadOnlyList<(string Key, string? Hint)> keys, int more, IReadOnlyList<string> takes, int moreTakes = 0)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(takes);
        string named;
        if (keys.Count == 1 && more == 0)
        {
            named = "takes no argument '" + keys[0].Key + "'" + (keys[0].Hint is { } hint ? "; " + hint : "");
        }
        else
        {
            var items = keys.Select(k => "'" + k.Key + "'" + (k.Hint is { } hint ? " (" + hint + ")" : "")).ToList();
            named = more > 0
                ? "doesn't take " + string.Join(", ", items) + ", or " + more.ToString(CultureInfo.InvariantCulture) + " more"
                : "doesn't take " + string.Join(", ", items.Take(items.Count - 1)) + " or " + items[^1];
        }

        string list = string.Join(", ", takes) + (moreTakes > 0 ? ", and " + moreTakes.ToString(CultureInfo.InvariantCulture) + " more" : "");
        return "Error: " + tool + " " + named + (named.EndsWith('?') ? " " : ". ") + "It takes: " + list + ". Nothing ran.";
    }

    /// <summary>The hint beside a misnamed argument: the name it likely meant. Pinned.</summary>
    public static string NearestHint(string name) => "did you mean '" + name + "'?";

    /// <summary>The hint beside a count name the tool calls by another: the tool's own name for it. Pinned.</summary>
    public static string SynonymHint(string name) => "it is '" + name + "'";

    /// <summary>The hint beside a count name sent with the tool's own name for it too: the model contradicted itself. Pinned.</summary>
    public static string BothSentHint(string name) => "send only '" + name + "'";

    /// <summary>
    /// The hint beside a key that differs from a sent one only in case (2026-11-15, the review's #121: <c>{path, Path}</c>). <paramref name="key"/>
    /// is already echoed. Pinned.
    /// </summary>
    public static string CaseTwinHint(string key, string sent) => "drop '" + key + "'; '" + sent + "' was sent";

    /// <summary>
    /// The hint beside a count name on a statement tool (2026-11-15, the review's #41): its <paramref name="rows"/> limits only what is shown,
    /// so no count name is pointed there. Pinned.
    /// </summary>
    public static string RowsShownHint(string rows) => rows + " only limits the rows shown; put any limit in the statement itself";

    /// <summary>
    /// The line after the result of a call whose keys went unread (2026-11-15, #42): a tool that takes no arguments ran, since nothing could
    /// have been filtered, or a commentary key (#55) was left aside. <paramref name="keys"/> are already echoed. Pinned.
    /// </summary>
    public static string IgnoredArguments(string tool, IReadOnlyList<string> keys, int more, bool takesArguments = false)
    {
        ArgumentNullException.ThrowIfNull(keys);
        string list = string.Join(", ", keys) + (more > 0 ? ", and " + more.ToString(CultureInfo.InvariantCulture) + " more" : "");
        string why = !takesArguments ? tool + " takes no arguments" : tool + " doesn't read " + (keys.Count + more == 1 ? "it" : "them");
        return "(ignored: " + list + "; " + why + ")";
    }

    /// <summary><paramref name="notice"/> as the user reads it: <see cref="AskAgain"/> after a notice that asks for it, unless a loop's pass runs (F96).</summary>
    public static string Shown(TurnEvent.Notice notice, bool loopPass)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return notice.AskAgain && !loopPass ? notice.Text + AskAgain : notice.Text;
    }
}
