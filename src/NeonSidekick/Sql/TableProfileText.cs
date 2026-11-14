using System.Globalization;
using System.Text;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Sql;

/// <summary>
/// The <c>*_profile</c> tools' words and their one renderer (2026-11-14, the database-tools plan's #14): a header that is a full sentence on its
/// own (the transcript's note), then one compact Markdown table, a row a column, then the notes. Every name and type the catalog gave goes
/// through <see cref="UntrustedText.Name"/> and every value was scrubbed as it was read (<see cref="SqlText.Cell"/>), each clipped to
/// <see cref="ValueChars"/>; the answer keeps to <see cref="QueryPlan.MaxAnswerChars"/>, or the result chars when lower.
/// </summary>
public static class TableProfileText
{
    /// <summary>The longest a value (a min, a max, a top value) stands in a cell.</summary>
    public const int ValueChars = 40;

    /// <summary>What a cell holds for a measure its type doesn't take.</summary>
    public const string NotMeasured = "—";

    public const string Heading = "| column | type | nulls | distinct | min | max | top |";
    public const string Rule = "|---|---|---|---|---|---|---|";

    /// <summary>The note under a table where some type wasn't measured.</summary>
    public const string NotMeasuredNote = "— : not measured for that type (long text, binary, JSON, XML and the like).";

    public const string NoTable = "Error: give the table to profile.";

    public static string BadTop(int max) => $"Error: top must be 0 to {Invariant(max)}";

    public static string BadColumns(string raw) => $"Error: '{raw.Trim()}' is not a list of column names for 'columns'";

    public static string NoSuchColumn(string column, string table, string describe) =>
        $"Error: {UntrustedText.Name(table)} has no column {UntrustedText.Name(column)}; {describe} lists its columns.";

    /// <summary>Pass 1 (or the bounded count) ran past the timeout: nothing to show, and the way to make it smaller.</summary>
    public static string Stopped(string table, string connection, int seconds) =>
        $"Error: the profile of {UntrustedText.Name(table)} on {connection} ran past {Invariant(seconds)} s and was stopped; name fewer columns (columns: [...]).";

    public static string TopStopped(int seconds) => $"Top values ran past the {Invariant(seconds)} s timeout and were stopped; top: 0 skips them.";

    public static string TopFailed(string detail) => $"Top values couldn't be read: {UntrustedText.Name(detail)}.";

    public static string Unreadable(string table) => $"Error: the profile of {UntrustedText.Name(table)} came back in a shape the app can't read.";

    public static string NotProfiled(IReadOnlyList<string> names) =>
        $"Not profiled (past {Invariant(TableProfile.MaxColumns)} columns): {string.Join(", ", names.Select(n => UntrustedText.Name(n)))}; name them in columns.";

    public static string LeftOut(IReadOnlyList<string> names) =>
        $"({SqlText.Count(names.Count, "more column")} left out to keep the answer short: {string.Join(", ", names.Select(n => UntrustedText.Name(n)))}.)";

    /// <summary>How many rows the profile read, and of what.</summary>
    public static string Scope(ProfileResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string of = result.MoreThan is { } cap ? $"of more than {Invariant(cap)} rows" : result.About is { } about ? $"of about {Invariant(about)} rows" : "";
        return result.Scope switch
        {
            ProfileScope.Sample => $"from a sample of ~{Invariant(result.Rows)} {of}".TrimEnd(),
            ProfileScope.FirstRows => $"from the first {Invariant(result.Rows)} {of}".TrimEnd(),
            _ => SqlText.Count(result.Rows, "row") + " (exact)",
        };
    }

    public static string Header(ProfileResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"{UntrustedText.Name(result.Table)}: {Scope(result)} · {SqlText.Count(result.ColumnCount, "column")} · {result.Where} ({SqlText.Millis(result.Elapsed)}).";
    }

    /// <summary>The answer: the header, the table (columns kept while it fits <paramref name="maxChars"/>), then <paramref name="notes"/>.</summary>
    public static string Answer(ProfileResult result, IReadOnlyList<string> notes, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(notes);
        var tail = new List<string>();
        if (result.NotProfiled.Count > 0)
        {
            tail.Add(NotProfiled(result.NotProfiled));
        }

        if (result.Columns.Any(c => c.Column.Measure is ProfileMeasure.None or ProfileMeasure.NullsOnly || (c.Column.Measure == ProfileMeasure.Distinct)))
        {
            tail.Add(NotMeasuredNote);
        }

        tail.AddRange(notes);
        var text = new StringBuilder(Header(result)).Append('\n').Append(Heading).Append('\n').Append(Rule);
        int tailChars = tail.Sum(t => t.Length + 1);
        var left = new List<string>();
        for (int i = 0; i < result.Columns.Count; i++)
        {
            // Room kept for the line naming the columns after this one, should they not fit.
            string row = Row(result, result.Columns[i]);
            var after = result.Columns.Skip(i + 1).Select(c => c.Column.Name).ToList();
            int reserve = after.Count > 0 ? LeftOut(after).Length + 1 : 0;
            if (left.Count > 0 || (i > 0 && text.Length + 1 + row.Length + tailChars + reserve > maxChars))
            {
                left.Add(result.Columns[i].Column.Name);
                continue;
            }

            text.Append('\n').Append(row);
        }

        if (left.Count > 0)
        {
            text.Append('\n').Append(LeftOut(left));
        }

        foreach (string line in tail)
        {
            text.Append('\n').Append(line);
        }

        string answer = text.ToString();
        return answer.Length <= maxChars ? answer : Files.TextCut.Safe(answer, maxChars - 1) + "…";
    }

    /// <summary>One column's row of the table.</summary>
    public static string Row(ProfileResult result, ColumnStats stats)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(stats);
        var column = stats.Column;
        bool ranged = column.Measure is ProfileMeasure.Full or ProfileMeasure.Bool;
        bool counted = column.Measure is ProfileMeasure.Distinct or ProfileMeasure.Full or ProfileMeasure.Bool;
        string[] cells =
        [
            Clip(UntrustedText.Name(column.Name), SqlText.MaxColumnNameChars),
            Clip(UntrustedText.Name(column.Type), ValueChars),
            column.Measure == ProfileMeasure.None || stats.NonNull is null ? NotMeasured : Nulls(result.Rows, stats.NonNull.Value),
            !counted || stats.Distinct is null ? NotMeasured : Distinct(result, stats),
            ranged ? Value(stats.Min) : NotMeasured,
            ranged ? Value(stats.Max) : NotMeasured,
            !counted ? NotMeasured : stats.Top is { } top ? string.Join(", ", top.Select(t => Value(t.Value) + " (" + Invariant(t.Count) + ")")) : "",
        ];

        return "| " + string.Join(" | ", cells.Select(SqlText.TableCell)) + " |";
    }

    private static string Nulls(long rows, long nonNull)
    {
        long nulls = Math.Max(0, rows - nonNull);
        if (nulls == 0 || rows == 0)
        {
            return Invariant(nulls);
        }

        double share = 100.0 * nulls / rows;
        string percent = share < 1 ? "<1" : Math.Round(share).ToString(CultureInfo.InvariantCulture);
        return $"{Invariant(nulls)} ({percent}%)";
    }

    private static string Distinct(ProfileResult result, ColumnStats stats)
    {
        long n = stats.Distinct!.Value;
        if (!result.ExactDistinct || stats.DistinctApproximate)
        {
            return "~" + Invariant(n);
        }

        return n > 0 && n == stats.NonNull ? Invariant(n) + " (unique)" : Invariant(n);
    }

    /// <summary>A value as a cell: empty for NULL (every value null), else clipped to <see cref="ValueChars"/>.</summary>
    private static string Value(string? value) =>
        value is null || value == SqlText.Null ? "" : Clip(value, ValueChars);

    private static string Clip(string text, int max) => text.Length <= max ? text : Files.TextCut.Safe(text, max) + "…";

    private static string Invariant(long n) => n.ToString(CultureInfo.InvariantCulture);
}
