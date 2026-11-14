using System.Diagnostics;
using System.Globalization;

namespace NeonSidekick.Sql;

/// <summary>
/// What a profile measures of one column (2026-11-14, the database-tools plan's #14), read from the column catalog by an
/// <b>allow-list</b> of types, never a deny-list: a deny-list misses <c>min(boolean)</c> (none in PostgreSQL), <c>min(bit)</c> (refused by
/// SQL Server) and <c>min(uuid)</c>.
/// </summary>
public enum ProfileMeasure
{
    /// <summary>Nothing at all: a type even <c>IS NOT NULL</c> refuses (Oracle's LONG).</summary>
    None,

    /// <summary>The nulls alone: LOBs, XML, JSON, binary, geometry, arrays.</summary>
    NullsOnly,

    /// <summary>Nulls, distinct and the top values, no MIN/MAX: <c>bit</c>, <c>uuid</c>, an enum where the order means little.</summary>
    Distinct,

    /// <summary>Every measure: numbers, dates and times, text up to 4,000 characters.</summary>
    Full,

    /// <summary>Every measure, the range as <c>bool_and</c>/<c>bool_or</c> (PostgreSQL's <c>bool</c>, which has no MIN).</summary>
    Bool,
}

/// <summary>One column a profile may read: its name as the catalog spells it, its type as shown, and what is measured.</summary>
public sealed record ProfileColumn(string Name, string Type, ProfileMeasure Measure);

/// <summary>What the profiled rows were: the whole table, a sample, or the first rows (a view, MySQL, SQLite: no sample to take).</summary>
public enum ProfileScope
{
    Whole,
    Sample,
    FirstRows,
}

/// <summary>One column's measures as read: null where a measure didn't apply. <see cref="Top"/> is null until pass 2 reads it.</summary>
public sealed record ColumnStats(ProfileColumn Column, long? NonNull, long? Distinct, bool DistinctApproximate, string? Min, string? Max)
{
    public IReadOnlyList<(string Value, long Count)>? Top { get; init; }

    /// <summary>Whether its values can be grouped for the top values (DuckDB's nested types can't be ordered).</summary>
    public bool Groupable { get; init; } = true;
}

/// <summary>
/// One family's SQL for a profile (2026-11-14, the database-tools plan's #14): its quoting, its row cap and sample, its distinct count and
/// its range for a type. Every identifier is the catalog's own, quoted here; no model text reaches the SQL. Each family's dialect sits beside
/// its catalog queries, with the catalog statement it reads columns by.
/// </summary>
public abstract class ProfileDialect
{
    /// <summary>The seed every sample is taken with, so a second call reads the same rows.</summary>
    public const int Seed = 42;

    public abstract string Quote(string name);

    /// <summary>Every row counted (SQL Server's <c>COUNT_BIG</c>, past 2^31).</summary>
    public virtual string CountAll => "COUNT(*)";

    public virtual string CountOf(string column) => "COUNT(" + column + ")";

    public virtual string Distinct(string column) => "COUNT(DISTINCT " + column + ")";

    /// <summary>Whether <see cref="Distinct"/> is an estimate on its own (an <c>APPROX_COUNT_DISTINCT</c>).</summary>
    public virtual bool ApproximateDistinct => false;

    public virtual string Min(string column, ProfileMeasure measure) => "MIN(" + column + ")";

    public virtual string Max(string column, ProfileMeasure measure) => "MAX(" + column + ")";

    /// <summary>The whole table as a FROM source.</summary>
    public virtual string Whole(string table) => table + " p";

    /// <summary>The first <paramref name="rows"/> rows as a FROM source, bounded whatever the table's size.</summary>
    public abstract string FirstRows(string table, long rows);

    /// <summary>
    /// A sample of about <paramref name="rows"/> rows of a <paramref name="kind"/> as a FROM source, capped at that many; null when this kind
    /// isn't sampled here (a view, a foreign table) or the family samples nothing. <paramref name="estimate"/> is the catalog's count, null when
    /// it has none.
    /// </summary>
    public virtual string? Sample(string table, string kind, long rows, long? estimate) => null;

    /// <summary><paramref name="query"/> (ordered) cut at <paramref name="rows"/> rows.</summary>
    protected abstract string Limit(string query, int rows);

    /// <summary>The <paramref name="top"/> most common values of <paramref name="column"/> with their counts, the most common first.</summary>
    public virtual string Top(string column, string from, int top) =>
        Limit($"SELECT {column}, {CountAll} FROM {from} WHERE {column} IS NOT NULL GROUP BY {column} ORDER BY 2 DESC, 1", top);

    /// <summary>The rows of <paramref name="table"/> counted to <paramref name="cap"/> at most: bounded on a table of any size.</summary>
    public string CappedCount(string table, long cap) => $"SELECT {CountAll} FROM {FirstRows(table, cap)}";

    /// <summary>
    /// Pass 1's statements: one aggregate, <c>COUNT(*)</c> then four slots a column (non-null count, distinct, min, max), <c>NULL</c> in a
    /// slot its type doesn't take.
    /// </summary>
    public virtual IReadOnlyList<string> Aggregate(IReadOnlyList<ProfileColumn> columns, string from)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var parts = new List<string> { CountAll };
        foreach (var column in columns)
        {
            string q = Quote(column.Name);
            parts.Add(column.Measure switch
            {
                ProfileMeasure.None => "NULL",
                ProfileMeasure.NullsOnly => CountOf($"CASE WHEN {q} IS NOT NULL THEN 1 END"),
                _ => CountOf(q),
            });
            parts.Add(column.Measure is ProfileMeasure.Distinct or ProfileMeasure.Full or ProfileMeasure.Bool ? Distinct(q) : "NULL");
            parts.Add(column.Measure is ProfileMeasure.Full or ProfileMeasure.Bool ? Min(q, column.Measure) : "NULL");
            parts.Add(column.Measure is ProfileMeasure.Full or ProfileMeasure.Bool ? Max(q, column.Measure) : "NULL");
        }

        return ["SELECT " + string.Join(", ", parts) + " FROM " + from];
    }

    /// <summary>Pass 1's grids read back: the rows profiled and each column's measures; null when the shape isn't the one asked for.</summary>
    public virtual IReadOnlyList<ColumnStats>? ReadAggregate(IReadOnlyList<SqlGrid> grids, IReadOnlyList<ProfileColumn> columns, out long rows)
    {
        ArgumentNullException.ThrowIfNull(grids);
        ArgumentNullException.ThrowIfNull(columns);
        rows = 0;
        if (grids is not [{ Rows: [var row, ..] }, ..] || row.Length < 1 + 4 * columns.Count || TableProfile.Whole(row[0]) is not { } counted)
        {
            return null;
        }

        rows = counted;
        var stats = new List<ColumnStats>(columns.Count);
        for (int i = 0; i < columns.Count; i++)
        {
            int at = 1 + 4 * i;
            var column = columns[i];
            stats.Add(new ColumnStats(column, column.Measure == ProfileMeasure.None ? null : TableProfile.Whole(row[at]),
                TableProfile.Whole(row[at + 1]), ApproximateDistinct, Value(column, row[at + 2]), Value(column, row[at + 3])));
        }

        return stats;
    }

    private static string? Value(ProfileColumn column, string cell) =>
        column.Measure is ProfileMeasure.Full or ProfileMeasure.Bool ? cell : null;
}

/// <summary>
/// <c>*_profile</c>'s engine (2026-11-14, the database-tools plan's #14): a table's data at a glance in one answer, so a model need not read
/// pages of sample rows. Two passes inside one deadline, each through the family's read-only run routine: an aggregate (rows, and per column
/// nulls, distinct, min, max), then the most common values of the columns that repeat. A table over <see cref="SampleAbove"/> rows (the
/// catalog's estimate) is sampled to about that many; one with no estimate is counted first, with a count bounded whatever its size. Pure but
/// for the runner it is given.
/// </summary>
public static class TableProfile
{
    public const int MaxColumns = 40;
    public const int DefaultTop = 5;
    public const int MaxTop = 10;

    /// <summary>A column with more distinct values than this has no top values worth showing.</summary>
    public const int MaxDistinctForTop = 1_000;

    /// <summary>The rows a profile reads at most: a bigger table is sampled. Internal and settable so the live facts make each server sample.</summary>
    internal static long SampleAbove { get; set; } = 100_000;

    /// <summary>A sample under this share of the rows asked for is page sampling come back short: the first rows are read instead.</summary>
    public const double ShortSample = 0.1;

    /// <summary>One run of the family's: the statements in one session, given the seconds left of the call's deadline.</summary>
    public delegate Task<SqlRun> Runner(IReadOnlyList<string> statements, int timeoutSeconds, CancellationToken cancellationToken);

    /// <summary>
    /// One call: <paramref name="Shown"/> is the table as the answer names it, <paramref name="Table"/> its quoted SQL (or a parenthesized
    /// source), <paramref name="Columns"/> every column in the catalog's order, <paramref name="Asked"/> the <c>columns</c> argument (null:
    /// all), <paramref name="Estimate"/> the catalog's row count (null: none), <paramref name="Describe"/> the tool that lists the columns.
    /// </summary>
    public sealed record Request(string Shown, string Table, string Kind, IReadOnlyList<ProfileColumn> Columns, IReadOnlyList<string>? Asked, int Top,
        long? Estimate, int TimeoutSeconds, int MaxChars, string Describe, StringComparison NameMatch);

    /// <summary>A count cell as a whole number (an Oracle NUMBER reads <c>1204</c>, a DuckDB one as text); null for NULL or anything else.</summary>
    public static long? Whole(string? cell)
    {
        if (cell is null || cell == SqlText.Null)
        {
            return null;
        }

        if (long.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
        {
            return n;
        }

        return decimal.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d) && d >= long.MinValue && d <= long.MaxValue
            ? (long)Math.Round(d)
            : null;
    }

    /// <summary>
    /// The columns a call profiles, in the order asked (or the catalog's), at most <see cref="MaxColumns"/>; <paramref name="rest"/> the ones
    /// past it. Null and <paramref name="refusal"/> set for a name the table doesn't have.
    /// </summary>
    public static IReadOnlyList<ProfileColumn>? Pick(Request request, out IReadOnlyList<string> rest, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(request);
        rest = [];
        refusal = null;
        var picked = new List<ProfileColumn>();
        if (request.Asked is not { Count: > 0 } asked)
        {
            picked.AddRange(request.Columns);
        }
        else
        {
            foreach (string name in asked)
            {
                // A quoted name ("MixedCase") is matched as written alone, as Oracle and PostgreSQL read it.
                string wanted = name.Trim();
                bool quoted = wanted.Length > 1 && wanted[0] == '"' && wanted[^1] == '"';
                wanted = quoted ? wanted[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : wanted;
                var match = request.Columns.FirstOrDefault(c => c.Name.Equals(wanted, StringComparison.Ordinal))
                    ?? (quoted ? null : request.Columns.FirstOrDefault(c => c.Name.Equals(wanted, request.NameMatch)));
                if (match is null)
                {
                    refusal = TableProfileText.NoSuchColumn(wanted, request.Shown, request.Describe);
                    return null;
                }

                if (!picked.Contains(match))
                {
                    picked.Add(match);
                }
            }
        }

        if (picked.Count > MaxColumns)
        {
            rest = [.. picked.Skip(MaxColumns).Select(c => c.Name)];
            picked = [.. picked.Take(MaxColumns)];
        }

        return picked;
    }

    /// <summary>The call's answer: the header, one Markdown table, the notes; or the family's <c>Error:</c> sentence.</summary>
    public static async Task<string> RunAsync(Request request, ProfileDialect dialect, Runner run, Func<SqlRun, string> error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(error);
        if (Pick(request, out var rest, out string? refusal) is not { } columns)
        {
            return refusal!;
        }

        var watch = Stopwatch.StartNew();
        long cap = SampleAbove;
        long? about = request.Estimate is > 0 ? request.Estimate : null;   // a zero estimate is as often stale as empty: counted instead
        bool moreThan = false;
        var scope = ProfileScope.Whole;
        string from = dialect.Whole(request.Table);
        string connection = "", database = "";

        if (about is null)
        {
            var counted = await run([dialect.CappedCount(request.Table, cap + 1)], Left(watch, request), cancellationToken).ConfigureAwait(false);
            (connection, database) = (counted.Connection, counted.Database);
            if (counted.Outcome != SqlOutcome.Ok)
            {
                return Failed(counted, request, error);
            }

            if (Whole(counted.Grids.FirstOrDefault()?.Rows.FirstOrDefault()?[0]) is > 0 and var n && n > cap)
            {
                moreThan = true;
                (scope, from) = dialect.Sample(request.Table, request.Kind, cap, null) is { } sampled ? (ProfileScope.Sample, sampled) : (ProfileScope.FirstRows, dialect.FirstRows(request.Table, cap));
            }
        }
        else if (about > cap)
        {
            (scope, from) = dialect.Sample(request.Table, request.Kind, cap, about) is { } sampled ? (ProfileScope.Sample, sampled) : (ProfileScope.FirstRows, dialect.FirstRows(request.Table, cap));
        }

        if (Left(watch, request) == 0)
        {
            return TableProfileText.Stopped(request.Shown, connection, request.TimeoutSeconds);
        }

        var first = await run(dialect.Aggregate(columns, from), Left(watch, request), cancellationToken).ConfigureAwait(false);
        (connection, database) = (first.Connection, first.Database);
        if (first.Outcome != SqlOutcome.Ok)
        {
            return Failed(first, request, error);
        }

        if (dialect.ReadAggregate(first.Grids, columns, out long rows) is not { } stats)
        {
            return TableProfileText.Unreadable(request.Shown);
        }

        // Page sampling can come back far short of the rows asked for (a skewed or fragmented table): the first rows instead, same deadline.
        if (scope == ProfileScope.Sample && rows < cap * ShortSample)
        {
            if (Left(watch, request) == 0)
            {
                return TableProfileText.Stopped(request.Shown, connection, request.TimeoutSeconds);
            }

            from = dialect.FirstRows(request.Table, cap);
            scope = ProfileScope.FirstRows;
            var again = await run(dialect.Aggregate(columns, from), Left(watch, request), cancellationToken).ConfigureAwait(false);
            if (again.Outcome != SqlOutcome.Ok)
            {
                return Failed(again, request, error);
            }

            if (dialect.ReadAggregate(again.Grids, columns, out rows) is not { } firstRows)
            {
                return TableProfileText.Unreadable(request.Shown);
            }

            stats = firstRows;
        }

        // Pass 2: the columns that repeat, each one's most common values.
        var notes = new List<string>();
        if (request.Top > 0)
        {
            var candidates = stats.Select((s, i) => (s, i)).Where(p => Repeats(p.s)).ToList();
            if (candidates.Count > 0)
            {
                if (Left(watch, request) == 0)
                {
                    notes.Add(TableProfileText.TopStopped(request.TimeoutSeconds));
                }
                else
                {
                    var tops = await run([.. candidates.Select(p => dialect.Top(dialect.Quote(p.s.Column.Name), from, request.Top))], Left(watch, request), cancellationToken).ConfigureAwait(false);
                    var filled = stats.ToList();
                    for (int g = 0; g < Math.Min(tops.Grids.Count, candidates.Count); g++)
                    {
                        var values = tops.Grids[g].Rows.Where(r => r.Length >= 2).Select(r => (r[0], Whole(r[1]) ?? 0)).ToList();
                        filled[candidates[g].i] = filled[candidates[g].i] with { Top = values };
                    }

                    stats = filled;
                    if (tops.Outcome == SqlOutcome.Timeout)
                    {
                        notes.Add(TableProfileText.TopStopped(request.TimeoutSeconds));
                    }
                    else if (tops.Outcome != SqlOutcome.Ok)
                    {
                        notes.Add(TableProfileText.TopFailed(tops.Detail));
                    }
                }
            }
        }

        var result = new ProfileResult(request.Shown, scope, rows, about, moreThan ? cap : null, request.Columns.Count, stats, rest,
            QueryPlanText.Where(connection, database), watch.Elapsed, scope == ProfileScope.Whole && !dialect.ApproximateDistinct);
        return TableProfileText.Answer(result, notes, request.MaxChars);
    }

    /// <summary>Whether a column's values repeat few enough to list: measured, not unique, at most <see cref="MaxDistinctForTop"/>.</summary>
    private static bool Repeats(ColumnStats stats) =>
        stats.Groupable && stats.Column.Measure is ProfileMeasure.Distinct or ProfileMeasure.Full or ProfileMeasure.Bool
        && stats is { NonNull: > 0, Distinct: > 0 and <= MaxDistinctForTop } && stats.Distinct < stats.NonNull;

    private static int Left(Stopwatch watch, Request request) => SqlDeadline.Left(watch, request.TimeoutSeconds);

    private static string Failed(SqlRun run, Request request, Func<SqlRun, string> error) =>
        run.Outcome == SqlOutcome.Timeout ? TableProfileText.Stopped(request.Shown, run.Connection, request.TimeoutSeconds) : error(run);
}

/// <summary>
/// A profile as read, for <see cref="TableProfileText.Answer"/>: the rows profiled and how (<paramref name="About"/> the catalog's estimate,
/// <paramref name="MoreThan"/> the cap a bounded count passed), every column's measures, the columns left past <see cref="TableProfile.MaxColumns"/>,
/// and whether the distinct counts are exact.
/// </summary>
public sealed record ProfileResult(string Table, ProfileScope Scope, long Rows, long? About, long? MoreThan, int ColumnCount, IReadOnlyList<ColumnStats> Columns,
    IReadOnlyList<string> NotProfiled, string Where, TimeSpan Elapsed, bool ExactDistinct);
