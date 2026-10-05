using System.Globalization;

namespace NeonSidekick.UI;

/// <summary>
/// The wording of a folded tool run's summary line (2026-09-22, the user's ask): the run's calls
/// counted by name — <c>  ▸ 🛠️ 7 tool calls — read_file ×3, grep ×2, run_command ×2</c>, the
/// triangle down (<see cref="ExpandedGlyph"/>) while the run shows every line. The names most
/// called first, a tie in the order they were first called; a name called once goes without its
/// count. The indent is <see cref="TranscriptRenderer.ToolGlyph"/>'s, so the reply's glyph can
/// stand over it as it does over a 🛠️ line. Pinned.
/// </summary>
public static class ToolGroupText
{
    /// <summary>The summary's mark while the run is folded. One cell (pinned against <see cref="TextCells"/>).</summary>
    public const string CollapsedGlyph = "▸";

    /// <summary>The summary's mark while the run shows every line. One cell.</summary>
    public const string ExpandedGlyph = "▾";

    /// <summary>
    /// The summary's text for <paramref name="tally"/> (each name and how many times it was called,
    /// in the order first called): <c>  ▸ 🛠️ 1 tool call — grep</c>, <c>  ▾ 🛠️ 3 tool calls — grep ×2, read_file</c>;
    /// <c>  ▸ 🛠️ tool calls</c> when nothing was counted. Cut at <see cref="TranscriptRenderer.ToolTextLimit"/>.
    /// </summary>
    public static string Summary(IReadOnlyList<(string Name, int Count)> tally, bool expanded, int failed = 0)
    {
        ArgumentNullException.ThrowIfNull(tally);
        string head = "  " + (expanded ? ExpandedGlyph : CollapsedGlyph) + " " + TranscriptRenderer.ToolGlyph.TrimStart();
        int total = tally.Sum(t => t.Count);
        if (total == 0)
        {
            return head + "tool calls";
        }

        var names = tally
            .Select((t, i) => (t.Name, t.Count, First: i))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.First)
            .Select(t => t.Count == 1 ? t.Name : t.Name + " ×" + t.Count.ToString(CultureInfo.InvariantCulture));
        string calls = total.ToString(CultureInfo.InvariantCulture) + (total == 1 ? " tool call" : " tool calls");
        // The failures counted (2026-10-04, the UI review) before the names, so a long list never cuts them off.
        string failures = failed > 0 ? FailedNote(failed) : "";
        return TranscriptRenderer.Truncate(head + calls + failures + " — " + string.Join(", ", names), TranscriptRenderer.ToolTextLimit);
    }

    /// <summary>After the count of a run with failed results (2026-10-04): <c> · 1 failed</c>. Pinned.</summary>
    public static string FailedNote(int failed) => " · " + failed.ToString(CultureInfo.InvariantCulture) + " failed";

    /// <summary><see cref="Summary"/> dim, as a tool line is.</summary>
    public static string SummaryMarkup(IReadOnlyList<(string Name, int Count)> tally, bool expanded, int failed = 0) =>
        Theme.ColorMarkup(failed > 0 ? Theme.Warn : Theme.Dim, Summary(tally, expanded, failed));

    /// <summary>The transcript's notice after <c>/expand</c> or <c>/collapse</c> (<c>/tools expand|collapse</c> until later on 2026-09-22; thinking too since 2026-09-26). Pinned.</summary>
    public static string ExpandedNotice(bool expanded) =>
        expanded ? "(tool calls, code blocks, diffs and thinking expanded; Ctrl+O or /collapse folds them)" : "(tool calls, code blocks, diffs and thinking collapsed; Ctrl+O, /expand or a click on a summary unfolds them)";
}
