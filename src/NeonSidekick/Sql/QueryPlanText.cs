using System.Globalization;
using System.Text;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Sql;

/// <summary>
/// The <c>*_explain</c> tools' words and their one renderer (2026-11-14, the database-tools plan's #12): a header that is a full sentence
/// on its own (the transcript's note), the plan's steps as an indented list, then the flags. Never the raw plan: a short summary a local
/// model can read in its context, at most <see cref="QueryPlan.MaxAnswerChars"/> (or the result chars, when lower). Every name, predicate
/// and warning the server wrote goes through <see cref="UntrustedText.Name"/>, so a plan can't reorder the text or slip an escape in.
/// </summary>
public static class QueryPlanText
{
    /// <summary>The longest a server string (an object's name, a predicate, a warning) stands in the answer.</summary>
    public const int MaxNameChars = 160;

    /// <summary>The raw plan's start an unreadable plan is answered with.</summary>
    public const int RawChars = 2_000;

    public const string StepsHeading = "Steps:";
    public const string FlagsHeading = "Worth a look:";
    public const string NoFlags = "Nothing stands out: no full scan of a big table, big sort or missing index.";

    /// <summary>SQL Server's answers end with this, since the server keeps its suggestions across queries too (#167: built from the tool's own names).</summary>
    public static readonly string SqlServerFooter =
        $"See also {SqlIndexesTool.ToolName} with {SqlIndexesTool.MissingArgument}: true for the server's index suggestions.";

    /// <summary>
    /// A refused lead: the tool adds the explain itself, and DuckDB's DESCRIBE, SHOW and SUMMARIZE aren't plans (2026-11-15, #156: an EXPLAIN
    /// lead has its own sentence, and "an" goes before a vowel).
    /// </summary>
    public static string NotPlannable(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (string.Equals(word, "EXPLAIN", StringComparison.OrdinalIgnoreCase))
        {
            return "Error: give the query alone, without EXPLAIN; the tool explains it itself (never runs it).";
        }

        string article = word.Length > 0 && "AEIOUaeiou".Contains(word[0], StringComparison.Ordinal) ? "an" : "a";
        return $"Error: give the query alone; the tool explains it itself (never runs it), and {article} {word} statement has no plan to show.";
    }

    /// <summary>SQL Server's refusal for an account without SHOWPLAN (Msg 262).</summary>
    public const string NeedsShowplan = "Error: the account needs SHOWPLAN on the database to see a query's plan (GRANT SHOWPLAN TO the user).";

    /// <summary>MariaDB's refusal of a WITH query's plan, with the way round it.</summary>
    public static string WithRefused(string detail) =>
        $"Error: the server would not explain the query: {detail}. Explain the SELECT inside the WITH instead.";

    /// <summary>Where a plan ran: <c>conn/db</c>, or the database alone for SQLite and DuckDB.</summary>
    public static string Where(string connection, string database) =>
        database.Length == 0 || string.Equals(connection, database, StringComparison.Ordinal) ? connection : connection + "/" + database;

    /// <summary>
    /// The answer for a plan the parser could not read: the header, the reason, and the raw plan's start fenced as the server's text, at most
    /// <see cref="RawChars"/> of it and the whole within <paramref name="maxChars"/> (2026-11-15, #83: a lower result-chars setting holds here too).
    /// </summary>
    public static string Unreadable(string connection, string database, TimeSpan elapsed, string raw, int maxChars = QueryPlan.MaxAnswerChars)
    {
        ArgumentNullException.ThrowIfNull(raw);
        string head = $"Plan for {Where(connection, database)} (estimated, not run; {SqlText.Millis(elapsed)}): the plan couldn't be read, so here is its start.\n"
            + UntrustedText.QuoteNote + "\n";
        int room = Math.Min(RawChars, maxChars - head.Length - UntrustedText.Fenced("…").Length);
        string start = raw.Length <= room ? raw : room <= 0 ? "…" : Files.TextCut.Safe(raw, room) + "…";
        string answer = head + UntrustedText.Fenced(start);
        return answer.Length <= maxChars ? answer : Files.TextCut.Safe(answer, Math.Max(0, maxChars - 1)) + "…";
    }

    // ─── flags ──────────────────────────────────────────────────────────────────

    public static string FullScan(string target, double? rows) =>
        $"A full scan of {Name(target)} ({RowsOrEvery(rows)}): an index on the columns it filters or joins on may help.";

    public static string KeyLookup(string target, double? rows) =>
        $"Key lookups into {Name(target)} ({RowsOrEvery(rows)}): an index that covers the columns read (INCLUDE) avoids them.";

    public static string NestedLoop(double? rows) =>
        $"A nested loop over {Rows(rows)}: each row probes the other input; an index on the join columns usually helps.";

    public static string CrossProduct(double? rows) =>
        $"A join with no join condition ({Rows(rows)}): every row of one input meets every row of the other.";

    public static string BigSort(double? rows) => $"A sort of {Rows(rows)}: an index in that order can avoid it.";

    public static string TempBTree(string detail) => $"A temporary B-tree ({Name(detail)}): an index in that order can avoid it.";

    /// <summary>A temporary table, for <paramref name="purpose"/> (<c>GROUP BY</c>, <c>ORDER BY</c>, <c>DISTINCT</c>) when the plan says.</summary>
    public static string TempTable(string? purpose) =>
        purpose is null ? "A temporary table holds rows between steps." : $"A temporary table for the {Name(purpose)}: an index in that order can avoid it.";

    /// <summary>A scan that reads a big table to keep a few rows (PostgreSQL's estimate is of the rows kept).</summary>
    public static string FilteredScan(string target, double? kept) =>
        $"A full scan of {Name(target)} that keeps {Rows(kept)}: an index on the columns it filters on may help.";

    public static string Filesort(string? table) => table is null ? "A filesort: the rows are sorted after they are read." : $"A filesort of {Name(table)}: the rows are sorted after they are read.";

    public static string JoinBuffer(string target, double? rows) =>
        $"A join with no usable index on {Name(target)} ({RowsOrEvery(rows)}, through a join buffer).";

    /// <summary>SQLite's Bloom filter on a join's table (#162): it judged a lookup by those columns worth speeding up on every run.</summary>
    public static string BloomFilter(string target) =>
        $"SQLite builds a Bloom filter on {Name(target)} for every run: an index on the columns it joins on may help.";

    /// <summary>A plan read only to its first <paramref name="steps"/> (#165).</summary>
    public static string PlanCut(int steps) =>
        $"The plan was cut at {steps.ToString("N0", CultureInfo.InvariantCulture)} steps; the rest weren't read.";

    public static string AutomaticIndex(string target) =>
        $"SQLite builds a temporary index on {Name(target)} for every run: a lasting index on those columns saves that.";

    public static string MissingIndex(string table, IReadOnlyList<string> equality, IReadOnlyList<string> inequality, IReadOnlyList<string> include, double? impact)
    {
        ArgumentNullException.ThrowIfNull(equality);
        ArgumentNullException.ThrowIfNull(inequality);
        ArgumentNullException.ThrowIfNull(include);
        var parts = new List<string>();
        if (equality.Count > 0)
        {
            parts.Add("equality: " + string.Join(", ", equality.Select(Name)));
        }

        if (inequality.Count > 0)
        {
            parts.Add("inequality: " + string.Join(", ", inequality.Select(Name)));
        }

        if (include.Count > 0)
        {
            parts.Add("include: " + string.Join(", ", include.Select(Name)));
        }

        string by = impact is { } i ? $", about {Invariant(Math.Round(i, MidpointRounding.AwayFromZero))}% cheaper" : "";
        return $"The server suggests an index on {Name(table)} ({string.Join("; ", parts)}){by}.";
    }

    public static string ImplicitConversion(string expression) =>
        $"An implicit conversion ({Name(expression)}) may keep an index from being used: compare values of the column's own type.";

    /// <summary>Columns the optimizer had no statistics for (#59): its estimates over them are guesses.</summary>
    public static string NoStatistics(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return $"No statistics on {string.Join(", ", columns.Select(Name))}: the row estimates over them are guesses; statistics or an index on them may help.";
    }

    public const string NoJoinPredicate = "A join with no join predicate: every row of one input meets every row of the other.";

    public static string Warning(string text) => $"The plan warns: {Name(text)}.";

    // ─── the answer ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The answer: the header, the steps (indented by depth), the flags, then <paramref name="footer"/>. Within <paramref name="maxChars"/>:
    /// a plan whose steps don't fit keeps the costliest steps (by cost, else rows), each with the steps above it (#127, so the indented tree
    /// never shows a child without its parent), in tree order, and says how many it left out. The flags are said once each (#7) and take
    /// at most about half the room, the rest counted (#63); the footer is always kept.
    /// </summary>
    public static string Answer(QueryPlan plan, string connection, string database, TimeSpan elapsed, int maxChars, string? footer = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var flags = plan.Flags.Distinct(StringComparer.Ordinal).ToList();
        var head = new StringBuilder();
        head.Append("Plan for ").Append(Where(connection, database)).Append(" (estimated, not run; ").Append(SqlText.Millis(elapsed)).Append("): ")
            .Append(SqlText.Count(plan.Steps.Count, "step"));
        if (QueryPlan.Estimate(plan.TotalRows) is { } total)
        {
            head.Append(", ~").Append(SqlText.Count(total, "row"));
        }

        if (plan.TotalCost is { } cost)
        {
            head.Append(", cost ").Append(Cost(cost));
        }

        head.Append('.');

        var tail = new StringBuilder();
        tail.Append('\n').Append(FlagsHeading).Append('\n');
        if (flags.Count == 0)
        {
            tail.Append(NoFlags).Append('\n');
        }

        // The flags' share: about half the answer, the note's own line reserved when any is left out.
        int flagRoom = maxChars / 2;
        int said = 0;
        foreach (string flag in flags)
        {
            int length = flag.Length + 3;
            bool last = said == flags.Count - 1;
            if (length + (last ? 0 : MoreFlags(flags.Count - said - 1).Length + 1) > flagRoom)
            {
                break;
            }

            tail.Append("- ").Append(flag).Append('\n');
            flagRoom -= length;
            said++;
        }

        if (said < flags.Count)
        {
            tail.Append(MoreFlags(flags.Count - said)).Append('\n');
        }

        string end = footer is null ? "" : footer + "\n";
        var lines = plan.Steps.Select(StepLine).ToList();
        int fixedLength = head.Length + 1 + StepsHeading.Length + 1 + tail.Length + end.Length;
        int room = maxChars - fixedLength;
        var keep = new bool[lines.Count];
        if (lines.Sum(l => l.Length + 1) <= room)
        {
            Array.Fill(keep, true);
        }
        else
        {
            // The costliest first, each with its ancestors (#127); the note's own line reserved.
            room -= LeftOut(lines.Count).Length + 1;
            var parent = Parents(plan.Steps);
            var order = Enumerable.Range(0, lines.Count)
                .OrderByDescending(i => plan.Steps[i].Cost ?? -1).ThenByDescending(i => plan.Steps[i].Rows ?? -1).ThenBy(i => i);
            foreach (int i in order)
            {
                var chain = new List<int>();
                for (int at = i; at >= 0 && !keep[at]; at = parent[at])
                {
                    chain.Add(at);
                }

                int length = chain.Sum(c => lines[c].Length + 1);
                if (chain.Count > 0 && length <= room)
                {
                    chain.ForEach(c => keep[c] = true);
                    room -= length;
                }
            }
        }

        var sb = new StringBuilder(head.ToString()).Append('\n').Append(StepsHeading).Append('\n');
        int left = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (keep[i])
            {
                sb.Append(lines[i]).Append('\n');
            }
            else
            {
                left++;
            }
        }

        if (left > 0)
        {
            sb.Append(LeftOut(left)).Append('\n');
        }

        string body = sb.Append(tail).ToString().TrimEnd('\n');
        string closing = end.TrimEnd('\n');
        int bodyRoom = maxChars - (closing.Length == 0 ? 0 : closing.Length + 1);
        if (body.Length > bodyRoom)
        {
            body = Files.TextCut.Safe(body, Math.Max(0, bodyRoom - 1)) + "…";
        }

        string answer = closing.Length == 0 ? body : body + "\n" + closing;
        return answer.Length <= maxChars ? answer : Files.TextCut.Safe(answer, Math.Max(0, maxChars - 1)) + "…";
    }

    public static string LeftOut(int steps) => $"({SqlText.Count(steps, "more step")} left out)";

    /// <summary>The flags past the answer's share for them (#63).</summary>
    public static string MoreFlags(int flags) => $"({SqlText.Count(flags, "more flag")} left out)";

    /// <summary>Each step's parent: the nearest step before it at a lower depth, or -1 for a root.</summary>
    private static int[] Parents(IReadOnlyList<QueryPlanStep> steps)
    {
        var parent = new int[steps.Count];
        var open = new Stack<int>();
        for (int i = 0; i < steps.Count; i++)
        {
            while (open.Count > 0 && steps[open.Peek()].Depth >= steps[i].Depth)
            {
                open.Pop();
            }

            parent[i] = open.Count > 0 ? open.Peek() : -1;
            open.Push(i);
        }

        return parent;
    }

    private static string StepLine(QueryPlanStep step)
    {
        var sb = new StringBuilder();
        sb.Append(' ', Math.Min(step.Depth, 12) * 2).Append("- ").Append(Name(step.Operation));
        if (step.Target is { Length: > 0 } target)
        {
            sb.Append(" on ").Append(Name(target));
        }

        var notes = new List<string>();
        if (QueryPlan.Estimate(step.Rows) is { } rows)
        {
            notes.Add("~" + SqlText.Count(rows, "row"));
        }

        if (step.Cost is { } cost)
        {
            notes.Add("cost " + Cost(cost));
        }

        if (notes.Count > 0)
        {
            sb.Append(" (").Append(string.Join(", ", notes)).Append(')');
        }

        return sb.ToString();
    }

    private static string Rows(double? rows) => QueryPlan.Estimate(rows) is { } n ? "~" + SqlText.Count(n, "row") : "an unknown number of rows";

    private static string RowsOrEvery(double? rows) => QueryPlan.Estimate(rows) is { } n ? "~" + SqlText.Count(n, "row") : "every row";

    private static string Cost(double cost) => cost.ToString(cost >= 100 ? "0" : "0.##", CultureInfo.InvariantCulture);

    private static string Name(string text)
    {
        string line = UntrustedText.Name(text);
        return line.Length <= MaxNameChars ? line : Files.TextCut.Safe(line, MaxNameChars) + "…";
    }

    private static string Invariant(double n) => n.ToString("0", CultureInfo.InvariantCulture);
}
