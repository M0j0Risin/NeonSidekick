using NeonSidekick.Llm;

namespace NeonSidekick.App;

/// <summary>
/// The words for <c>/compact</c>: one transcript notice per outcome. Pure statics, every string
/// pinned. The summary itself is not shown unless <c>LLM compact show summary</c> is on (2026-09-21:
/// <see cref="DetailLines"/>, dim lines under the notice, closed by the two protected counts); the figures are the message counts either
/// side and, for a summary with a usage report, the summariser's own request — what it read → what
/// it wrote — not the new context in use, which is only measured at the next reply.
/// </summary>
public static class CompactionText
{
    /// <summary>
    /// What every line of <c>/compact</c> or the automatic compact opens with, inside its parentheses
    /// (2026-09-19, the user's pick; <c>ChatScreen.TrashGlyph</c>'s shape): the clamp U+1F5DC with
    /// its variation selector — bare it is text-presentation — and a space. A line where only tool
    /// results were pruned wears <see cref="Assistant.PruneGlyph"/> instead. <see cref="Assistant.CompactGlyph"/>
    /// since 2026-09-28, the mid-turn compact's lines being the tool loop's. Pinned.
    /// </summary>
    public const string CompactGlyph = Assistant.CompactGlyph;

    public const string NothingToCompact = "(" + CompactGlyph + "nothing to compact)";
    public const string Cancelled = "(" + CompactGlyph + "compact cancelled)";

    /// <summary>The prefix of a failure's error line: <c>🗜️ Compact failed: </c> + <c>Assistant.Explain</c> (<see cref="Assistant.CompactFailedPrefix"/>, the mid-turn guard's too).</summary>
    public const string FailedPrefix = Assistant.CompactFailedPrefix;

    /// <summary>The spinner's label while the summariser runs (<c>compacting the conversation</c> until 2026-09-28, the user's call).</summary>
    public const string CompactingLabel = "compacting";

    /// <summary>
    /// <c>(🗜️ compacted: 38 messages → 7 · 41.2k → 3.1k tokens)</c> for a summary with a usage report,
    /// <c>(🗜️ compacted: 38 messages → 7)</c> without one, <c>(✂️ compacted: 12 tool results pruned)</c> for
    /// a prune (the scissors: nothing was summarised); <c>auto-compacted at 83%</c> in place of
    /// <c>compacted</c> when the threshold fired, and a summary that also stubbed the kept turns'
    /// results (the automatic compact) ends <c> · 12 tool results pruned</c> under the clamp still.
    /// </summary>
    public static string Notice(ConversationCompactor.Result result, int? autoPercent)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Line(result, autoPercent is { } percent ? "auto-compacted at " + UsageText.PercentText(percent) : "compacted");
    }

    /// <summary>
    /// The mid-turn compact's second stage (2026-09-28): <c>(🗜️ compacted this turn at 91%: 24 messages → 5 · 60.1k → 1.2k tokens)</c>,
    /// this turn's earlier iterations having become a progress note. The first stage, the turns before this one, is
    /// <see cref="Notice"/>'s automatic line. Pinned.
    /// </summary>
    public static string TurnNotice(ConversationCompactor.Result result, int percent)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Line(result, "compacted this turn at " + UsageText.PercentText(percent));
    }

    private static string Line(ConversationCompactor.Result result, string head)
    {
        string glyph = result.Summarised ? CompactGlyph : Assistant.PruneGlyph;
        string pruned = UsageText.Plural(result.Pruned, "tool result", "tool results") + " pruned";
        string body;
        if (!result.Summarised)
        {
            body = pruned;
        }
        else
        {
            body = result.MessagesBefore + " messages → " + result.MessagesAfter;
            if (result.Usage is { } usage)
            {
                body += " · " + UsageText.CompactNumber(usage.Input) + " → " + UsageText.CompactNumber(usage.Output) + " tokens";
            }

            if (result.Pruned > 0)
            {
                body += " · " + pruned;
            }
        }

        return "(" + glyph + head + ": " + body + ")";
    }

    /// <summary>
    /// The summary's text as transcript lines (2026-09-21): split at line breaks, each trimmed, the
    /// blank ones dropped — the summariser writes plain paragraphs, and a notice row is one line.
    /// </summary>
    public static IReadOnlyList<string> SummaryLines(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var lines = new List<string>();
        foreach (string raw in summary.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>
    /// One pruned result's line (2026-09-21): <c>(✂️ read_file · 4,312 characters)</c>, or for a
    /// carrier's pictures <c>(✂️ 2 pictures from view_image)</c>.
    /// </summary>
    public static string PrunedLine(ConversationCompactor.PrunedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string body = entry.Pictures > 0
            ? UsageText.Plural(entry.Pictures, "picture", "pictures") + " from " + entry.Tool
            : entry.Tool + " · " + UsageText.Plural(entry.Characters, "character", "characters");
        return "(" + Assistant.PruneGlyph + body + ")";
    }

    /// <summary>
    /// The detail's closing lines (later on 2026-09-21, the user's ask): <c>(🗜️ 2 messages protected at the start)</c>
    /// for the opening call pairs carried across, <c>(🗜️ 5 messages protected at the end)</c> for the
    /// recent turns kept in place. Zero is still said — the two lines always close the detail, so a
    /// reader learns that nothing was kept rather than wondering. Pinned.
    /// </summary>
    public static string OpeningKeptLine(int messages) => "(" + CompactGlyph + UsageText.Plural(messages, "message", "messages") + " protected at the start)";

    public static string RecentKeptLine(int messages) => "(" + CompactGlyph + UsageText.Plural(messages, "message", "messages") + " protected at the end)";

    /// <summary>
    /// What <c>LLM compact show summary</c> adds under the notice (2026-09-21): the summary's lines
    /// (<see cref="SummaryLines"/>) when there is one, then one <see cref="PrunedLine"/> per stubbed
    /// result, in order, and last the two protected counts (<see cref="OpeningKeptLine"/>,
    /// <see cref="RecentKeptLine"/>), which every result has.
    /// </summary>
    public static IReadOnlyList<string> DetailLines(ConversationCompactor.Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = new List<string>();
        if (result.Summary is { } summary)
        {
            lines.AddRange(SummaryLines(summary));
        }

        foreach (var entry in result.Entries)
        {
            lines.Add(PrunedLine(entry));
        }

        lines.Add(OpeningKeptLine(result.OpeningKept));
        lines.Add(RecentKeptLine(result.RecentKept));
        return lines;
    }
}
