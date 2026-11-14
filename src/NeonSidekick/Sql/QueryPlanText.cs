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

    /// <summary>SQL Server's answers end with this, since the server keeps its suggestions across queries too.</summary>
    public const string SqlServerFooter = "See also sql_indexes with missing: true for the server's index suggestions.";

    /// <summary>Oracle's absence, said where the tools are listed: <c>EXPLAIN PLAN</c> writes <c>PLAN_TABLE</c>, which the read-only session refuses.</summary>
    public const string NoOracle = "Oracle has no explain tool: its EXPLAIN PLAN writes a table, which the read-only session refuses.";

    /// <summary>A refused lead: the tool adds the explain itself, and DuckDB's DESCRIBE, SHOW and SUMMARIZE aren't plans.</summary>
    public static string NotPlannable(string word) =>
        $"Error: give the query alone; the tool explains it itself (never runs it), and a {word} statement has no plan to show.";

    /// <summary>SQL Server's refusal for an account without SHOWPLAN (Msg 262).</summary>
    public const string NeedsShowplan = "Error: the account needs SHOWPLAN on the database to see a query's plan (GRANT SHOWPLAN TO the user).";

    /// <summary>MariaDB's refusal of a WITH query's plan, with the way round it.</summary>
    public static string WithRefused(string detail) =>
        $"Error: the server would not explain the query: {detail}. Explain the SELECT inside the WITH instead.";

    /// <summary>Where a plan ran: <c>conn/db</c>, or the database alone for SQLite and DuckDB.</summary>
    public static string Where(string connection, string database) =>
        database.Length == 0 || string.Equals(connection, database, StringComparison.Ordinal) ? connection : connection + "/" + database;

    /// <summary>The answer for a plan the parser could not read: the header, the reason, and the raw plan's start fenced as the server's text.</summary>
    public static string Unreadable(string connection, string database, TimeSpan elapsed, string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        string start = raw.Length <= RawChars ? raw : Files.TextCut.Safe(raw, RawChars) + "…";
        return $"Plan for {Where(connection, database)} (estimated, not run; {SqlText.Millis(elapsed)}): the plan couldn't be read, so here is its start.\n"
            + UntrustedText.QuoteNote + "\n" + UntrustedText.Fenced(start);
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

    public const string NoJoinPredicate = "A join with no join predicate: every row of one input meets every row of the other.";

    public static string Warning(string text) => $"The plan warns: {Name(text)}.";

    // ─── the answer ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The answer: the header, the steps (indented by depth), the flags, then <paramref name="footer"/>. Within <paramref name="maxChars"/>:
    /// a plan whose steps don't fit keeps every flag and the costliest steps (by cost, else rows), in tree order, and says how many it left out.
    /// </summary>
    public static string Answer(QueryPlan plan, string connection, string database, TimeSpan elapsed, int maxChars, string? footer = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
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
        if (plan.Flags.Count == 0)
        {
            tail.Append(NoFlags).Append('\n');
        }

        foreach (string flag in plan.Flags)
        {
            tail.Append("- ").Append(flag).Append('\n');
        }

        if (footer is not null)
        {
            tail.Append(footer).Append('\n');
        }

        var lines = plan.Steps.Select(StepLine).ToList();
        int fixedLength = head.Length + 1 + StepsHeading.Length + 1 + tail.Length;
        int room = maxChars - fixedLength;
        var keep = new bool[lines.Count];
        if (lines.Sum(l => l.Length + 1) <= room)
        {
            Array.Fill(keep, true);
        }
        else
        {
            // The costliest first; the note's own line reserved.
            room -= LeftOut(lines.Count).Length + 1;
            var order = Enumerable.Range(0, lines.Count)
                .OrderByDescending(i => plan.Steps[i].Cost ?? -1).ThenByDescending(i => plan.Steps[i].Rows ?? -1).ThenBy(i => i);
            foreach (int i in order)
            {
                if (lines[i].Length + 1 <= room)
                {
                    keep[i] = true;
                    room -= lines[i].Length + 1;
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

        string answer = sb.Append(tail).ToString().TrimEnd('\n');
        return answer.Length <= maxChars ? answer : Files.TextCut.Safe(answer, Math.Max(0, maxChars - 1)) + "…";
    }

    public static string LeftOut(int steps) => $"({SqlText.Count(steps, "more step")} left out)";

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
