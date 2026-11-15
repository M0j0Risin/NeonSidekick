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
    /// <summary>Nothing at all: a type even <c>IS NOT NULL</c> refuses (Oracle's LONG). Never selected into a source.</summary>
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

/// <summary>One column a profile may read: its name as the catalog spells it (read whole), its type as shown, and what is measured.</summary>
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

    /// <summary>
    /// Pass 2 found every value once (2026-11-15, #65): the most common value's count is 1, so no value repeats in the rows read. An
    /// approximate distinct count can't show that on its own.
    /// </summary>
    public bool AllDistinct { get; init; }
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

    /// <summary>
    /// A name as the <c>columns</c> argument wrote it, its quotes taken off (2026-11-15, #51): <paramref name="Quoted"/> when it was quoted, so
    /// it is matched exactly. Double quotes here; SQL Server adds brackets, MySQL backticks.
    /// </summary>
    public virtual (string Name, bool Quoted) Unquote(string written)
    {
        ArgumentNullException.ThrowIfNull(written);
        string name = written.Trim();
        return name.Length > 1 && name[0] == '"' && name[^1] == '"'
            ? (name[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal), true)
            : (name, false);
    }

    /// <summary>Every row counted (SQL Server's <c>COUNT_BIG</c>, past 2^31).</summary>
    public virtual string CountAll => "COUNT(*)";

    public virtual string CountOf(string column) => "COUNT(" + column + ")";

    public virtual string Distinct(string column) => "COUNT(DISTINCT " + column + ")";

    /// <summary>Whether <see cref="Distinct"/> is an estimate on its own (an <c>APPROX_COUNT_DISTINCT</c>).</summary>
    public virtual bool ApproximateDistinct => false;

    public virtual string Min(string column, ProfileMeasure measure) => "MIN(" + column + ")";

    public virtual string Max(string column, ProfileMeasure measure) => "MAX(" + column + ")";

    /// <summary>Whether a column's values can be grouped for its top values (DuckDB's nested types can't).</summary>
    public virtual bool Groupable(ProfileColumn column) => true;

    /// <summary>
    /// Whether <see cref="Sample"/> of a source smaller than the rows asked for returns every row of it (DuckDB's reservoir): then a source with
    /// no estimate is read as a sample of one more than the cap, and fewer rows back means the whole source was read.
    /// </summary>
    public virtual bool SampleTakesSmallWhole => false;

    /// <summary>
    /// The select list a source reads (2026-11-15, #17/#60/#123/#124): the picked columns by name, never <c>*</c>, so a hidden column, one the
    /// account can't read, or Oracle's LONG (measured <see cref="ProfileMeasure.None"/>, left out) never reaches the source's own select.
    /// </summary>
    public string SelectList(IReadOnlyList<ProfileColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var read = columns.Where(c => c.Measure != ProfileMeasure.None).Select(c => Quote(c.Name)).ToList();
        return read.Count > 0 ? string.Join(", ", read) : "1 AS " + Quote("_");
    }

    /// <summary>The whole table as a FROM source.</summary>
    public virtual string Whole(string table) => table + " p";

    /// <summary>The first <paramref name="rows"/> rows of <paramref name="list"/> as a FROM source, bounded whatever the table's size.</summary>
    public abstract string FirstRows(string table, string list, long rows);

    /// <summary>
    /// A sample of about <paramref name="rows"/> rows of a <paramref name="kind"/> as a FROM source, capped at that many; null when this kind
    /// isn't sampled here (a view, a foreign table) or the family samples nothing. <paramref name="estimate"/> is the catalog's count, null when
    /// it has none.
    /// </summary>
    public virtual string? Sample(string table, string list, string kind, long rows, long? estimate) => null;

    /// <summary><paramref name="query"/> (a <c>SELECT</c>, ordered) cut at <paramref name="rows"/> rows.</summary>
    protected abstract string Limit(string query, int rows);

    /// <summary>The <paramref name="top"/> most common values of <paramref name="column"/> with their counts, the most common first.</summary>
    public virtual string Top(string column, string from, int top) =>
        Limit($"SELECT {column}, {CountAll} FROM {from} WHERE {column} IS NOT NULL GROUP BY {column} ORDER BY 2 DESC, 1", top);

    /// <summary>Pass 2's statements for <paramref name="columns"/>: one a column here; one in all where the family reads its source once.</summary>
    public virtual IReadOnlyList<string> Tops(IReadOnlyList<ProfileColumn> columns, string from, int top)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return [.. columns.Select(c => Top(Quote(c.Name), from, top))];
    }

    /// <summary>Pass 2's grids read back: each of <paramref name="count"/> columns' values with their counts, null for one not read.</summary>
    public virtual IReadOnlyList<IReadOnlyList<(string Value, long Count)>?> ReadTops(IReadOnlyList<SqlGrid> grids, int count)
    {
        ArgumentNullException.ThrowIfNull(grids);
        var tops = new IReadOnlyList<(string, long)>?[count];
        for (int g = 0; g < Math.Min(grids.Count, count); g++)
        {
            tops[g] = [.. grids[g].Rows.Where(r => r.Length >= 2).Select(r => (r[0], TableProfile.Whole(r[1]) ?? 0))];
        }

        return tops;
    }

    /// <summary>
    /// Pass 2 for a family that reads its source once (2026-11-15, #5/#21/#104: PostgreSQL, DuckDB): the source as one CTE <c>s</c> (the
    /// keyword <paramref name="materialized"/> or none), then each column's group as a parenthesized branch that orders and limits on the
    /// typed value and numbers its rows, all <c>UNION ALL</c>ed. Only the outer select casts to text (<paramref name="text"/>), so ties still
    /// break by the typed value. Each row carries its column's index and its rank: <c>UNION ALL</c> keeps no order.
    /// </summary>
    protected string OneSourceTops(IReadOnlyList<ProfileColumn> columns, string from, int top, string materialized, string text)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var branches = columns.Select((c, i) =>
        {
            string q = Quote(c.Name);
            return $"SELECT {Invariant(i)} AS i, CAST(r.v AS {text}) AS v, r.n, row_number() OVER (ORDER BY r.n DESC, r.v) AS k "
                + $"FROM (SELECT {q} AS v, {CountAll} AS n FROM s WHERE {q} IS NOT NULL GROUP BY {q} ORDER BY 2 DESC, 1 LIMIT {Invariant(top)}) r";
        });
        return $"WITH s AS {materialized}(SELECT * FROM {from})\nSELECT i, v, n, k FROM (\n" + string.Join("\nUNION ALL\n", branches) + "\n) t ORDER BY i, k";
    }

    /// <summary><see cref="OneSourceTops"/>' rows read back from <paramref name="grid"/>: by index, then rank (the statement's order, held here too).</summary>
    protected static IReadOnlyList<IReadOnlyList<(string Value, long Count)>?> ReadOneSourceTops(SqlGrid? grid, int count)
    {
        var tops = new List<(string, long)>?[count];
        if (grid is null)
        {
            return tops;
        }

        foreach (var row in grid.Rows.Where(r => r.Length >= 4).OrderBy(r => TableProfile.Whole(r[0]) ?? -1).ThenBy(r => TableProfile.Whole(r[3]) ?? 0))
        {
            if (TableProfile.Whole(row[0]) is { } i && i >= 0 && i < count)
            {
                (tops[i] ??= []).Add((row[1], TableProfile.Whole(row[2]) ?? 0));
            }
        }

        // A column with no non-null value has no rows here, as its own statement would have none.
        for (int i = 0; i < count; i++)
        {
            tops[i] ??= [];
        }

        return tops;
    }

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
                TableProfile.Whole(row[at + 1]), ApproximateDistinct, Value(column, row[at + 2]), Value(column, row[at + 3])) { Groupable = Groupable(column) });
        }

        return stats;
    }

    protected static string Invariant(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static string? Value(ProfileColumn column, string cell) =>
        column.Measure is ProfileMeasure.Full or ProfileMeasure.Bool ? cell : null;
}

/// <summary>
/// <c>*_profile</c>'s engine (2026-11-14, the database-tools plan's #14): a table's data at a glance in one answer, so a model need not read
/// pages of sample rows. Two passes inside the call's one deadline, each through the family's read-only run routine: an aggregate (rows, and
/// per column nulls, distinct, min, max), then the most common values of the columns that repeat. A table whose catalog estimate is over the
/// request's <see cref="Request.SampleAbove"/> is sampled to about that many rows; any other is read as its first cap + 1 rows, so fewer back
/// is the whole table, exact, and the cap + 1st says it is bigger (2026-11-15, #3/#106: no count pass). Pure but for the runner it is given.
/// </summary>
public static class TableProfile
{
    public const int MaxColumns = 40;
    public const int DefaultTop = 5;
    public const int MaxTop = 10;

    /// <summary>A column with more distinct values than this has no top values worth showing.</summary>
    public const int MaxDistinctForTop = 1_000;

    /// <summary>The rows a profile reads at most: a bigger table is sampled. Each tool's own (its settable <c>SampleAbove</c>, for the live facts).</summary>
    public const long DefaultSampleAbove = 100_000;

    /// <summary>A sample under this share of the rows asked for is page sampling come back short: the first rows are read instead.</summary>
    public const double ShortSample = 0.1;

    /// <summary>One run of the family's: the statements in one session, given the seconds left of the call's deadline (never 0).</summary>
    public delegate Task<SqlRun> Runner(IReadOnlyList<string> statements, int timeoutSeconds, CancellationToken cancellationToken);

    /// <summary>
    /// One call: <paramref name="Shown"/> is the table as the answer names it, <paramref name="Table"/> its quoted SQL (or a parenthesized
    /// source), <paramref name="Columns"/> every column in the catalog's order, <paramref name="Asked"/> the <c>columns</c> argument (null:
    /// all), <paramref name="Estimate"/> the catalog's row count (null: none), <paramref name="Describe"/> what lists the columns, and
    /// <paramref name="Watch"/> the call's clock, started as the tool began (2026-11-15, #22/#74/#137: the resolve and the catalog spend the
    /// same deadline as the passes).
    /// </summary>
    public sealed record Request(string Shown, string Table, string Kind, IReadOnlyList<ProfileColumn> Columns, IReadOnlyList<string>? Asked, int Top,
        long? Estimate, int TimeoutSeconds, int MaxChars, string Describe, Stopwatch Watch)
    {
        /// <summary>The rows read at most (<see cref="DefaultSampleAbove"/>): a table estimated bigger is sampled.</summary>
        public long SampleAbove { get; init; } = DefaultSampleAbove;

        /// <summary>The column catalog came back cut at its row cap (2026-11-15, #105): the answer says only the first ones were read.</summary>
        public bool ColumnsCut { get; init; }

        /// <summary>The seconds left of the call's deadline: 0 once it has passed.</summary>
        public int Left => SqlDeadline.Left(Watch, TimeoutSeconds);
    }

    /// <summary>
    /// A profile catalog's cell read whole (2026-11-15, #34): a column's name is quoted back into the SQL, so the grid's scrubbed and clipped
    /// cell (a control character, a long name) would name a column the table doesn't have. Catalog values are short; a NULL reads empty, and a
    /// value that can't be read reads empty too, and is never quoted (<see cref="Pick"/>). Not <see cref="QueryPlan.WholeCell"/>'s text
    /// reader: SQLite's opens a blob handle on the column's table, which <c>sqlite_master</c> refuses.
    /// </summary>
    public static string CatalogCell(System.Data.Common.DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        try
        {
            return reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
        }
        catch (Exception ex) when (ex is not (System.Data.Common.DbException or OperationCanceledException or OutOfMemoryException))
        {
            return "";
        }
    }

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
    /// past it. A name is matched exactly, then (unquoted) case-blind, as the families read an unquoted name; two case twins (<c>Id</c>,
    /// <c>ID</c>) for one unquoted name are refused as ambiguous (2026-11-15, #98). A column whose name read back empty is never picked.
    /// Null and <paramref name="refusal"/> set for a name the table doesn't have.
    /// </summary>
    public static IReadOnlyList<ProfileColumn>? Pick(Request request, ProfileDialect dialect, out IReadOnlyList<string> rest, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dialect);
        rest = [];
        refusal = null;
        var named = request.Columns.Where(c => c.Name.Length > 0).ToList();
        var picked = new List<ProfileColumn>();
        if (request.Asked is not { Count: > 0 } asked)
        {
            picked.AddRange(named);
        }
        else
        {
            foreach (string written in asked)
            {
                var (wanted, quoted) = dialect.Unquote(written);
                var match = named.FirstOrDefault(c => c.Name.Equals(wanted, StringComparison.Ordinal));
                if (match is null && !quoted)
                {
                    var folded = named.Where(c => c.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (folded.Count > 1)
                    {
                        refusal = TableProfileText.Ambiguous(wanted, request.Shown, [.. folded.Select(c => c.Name)]);
                        return null;
                    }

                    match = folded.FirstOrDefault();
                }

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
        if (Pick(request, dialect, out var rest, out string? refusal) is not { } columns)
        {
            return refusal!;
        }

        long cap = request.SampleAbove;
        long? about = request.Estimate is > 0 ? request.Estimate : null;   // a zero estimate is as often stale as empty: read as first rows
        string list = dialect.SelectList(columns);

        // How much is read (#3/#106): an estimate past the cap is sampled to the cap; anything else is read as cap + 1 rows, which tells
        // the whole table (fewer back) from a bigger one (all of them back) in the one pass.
        bool probe = about is null || about <= cap;
        ProfileScope scope;
        string from;
        if (probe)
        {
            (scope, from) = about is null && dialect.SampleTakesSmallWhole && dialect.Sample(request.Table, list, request.Kind, cap + 1, null) is { } whole
                ? (ProfileScope.Sample, whole)
                : (ProfileScope.FirstRows, dialect.FirstRows(request.Table, list, cap + 1));
        }
        else
        {
            (scope, from) = dialect.Sample(request.Table, list, request.Kind, cap, about) is { } sampled
                ? (ProfileScope.Sample, sampled)
                : (ProfileScope.FirstRows, dialect.FirstRows(request.Table, list, cap));
        }

        var first = await Pass1Async(request, dialect, run, columns, from, cancellationToken).ConfigureAwait(false);

        // A sample that fails (#148: an external or foreign table, a memory-optimized one) or comes back far short of the rows asked for (page
        // sampling over a skewed table) is read again as the first rows, inside the same deadline.
        if (scope == ProfileScope.Sample && (first.Run?.Outcome == SqlOutcome.Failed || (!probe && first.Stats is not null && first.Rows < cap * ShortSample)))
        {
            from = dialect.FirstRows(request.Table, list, probe ? cap + 1 : cap);
            scope = ProfileScope.FirstRows;
            first = await Pass1Async(request, dialect, run, columns, from, cancellationToken).ConfigureAwait(false);
        }

        if (first.Answer is { } stopped)
        {
            return stopped;
        }

        if (first.Run!.Outcome != SqlOutcome.Ok)
        {
            return Failed(first.Run, request, error);
        }

        if (first.Stats is not { } stats)
        {
            return TableProfileText.Unreadable(request.Shown);
        }

        long rows = first.Rows;
        bool moreThan = false;
        if (probe)
        {
            // cap + 1 back: the table is bigger than the cap (and than any estimate); fewer: every row was read.
            if (rows > cap)
            {
                moreThan = true;
            }
            else
            {
                scope = ProfileScope.Whole;
            }
        }

        string connection = first.Run.Connection, database = first.Run.Database;

        // Pass 2: the columns that repeat, each one's most common values.
        var notes = new List<string>();
        if (request.ColumnsCut)
        {
            notes.Add(TableProfileText.ColumnsCut(request.Columns.Count));
        }

        if (request.Top > 0)
        {
            var candidates = stats.Select((s, i) => (s, i)).Where(p => Repeats(p.s)).ToList();
            if (candidates.Count > 0)
            {
                if (request.Left is not (> 0 and var left))
                {
                    notes.Add(TableProfileText.TopStopped(request.TimeoutSeconds));
                }
                else
                {
                    var asked = candidates.Select(p => p.s.Column).ToList();
                    var tops = await run(dialect.Tops(asked, from, request.Top), left, cancellationToken).ConfigureAwait(false);
                    var read = dialect.ReadTops(tops.Grids, asked.Count);
                    var filled = stats.ToList();
                    for (int g = 0; g < candidates.Count; g++)
                    {
                        if (read[g] is not { } values)
                        {
                            continue;
                        }

                        // Every count 1 (#65): nothing repeats in the rows read, so the column is all distinct there and has no top worth listing.
                        filled[candidates[g].i] = values is [{ Count: 1 }, ..]
                            ? filled[candidates[g].i] with { AllDistinct = true }
                            : filled[candidates[g].i] with { Top = values };
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
            QueryPlanText.Where(connection, database), request.Watch.Elapsed, !dialect.ApproximateDistinct);
        return TableProfileText.Answer(result, notes, request.MaxChars);
    }

    /// <summary>Pass 1 read: the run and what it read, or <see cref="Answer"/> when the deadline passed before it could start.</summary>
    private sealed record Pass1(SqlRun? Run, IReadOnlyList<ColumnStats>? Stats, long Rows, string? Answer);

    /// <summary>
    /// One pass 1 over <paramref name="from"/> (2026-11-15, #88: the first try and the first-rows rerun alike, so the answer's labels are the
    /// run's that read the rows shown). The seconds left are read once: a 0 (time is up) ends the call before the runner, which would read a
    /// 0 as "no limit" on SQL Server and MySQL (#74).
    /// </summary>
    private static async Task<Pass1> Pass1Async(Request request, ProfileDialect dialect, Runner run, IReadOnlyList<ProfileColumn> columns, string from, CancellationToken cancellationToken)
    {
        if (request.Left is not (> 0 and var left))
        {
            return new Pass1(null, null, 0, TableProfileText.Stopped(request.Shown, "", request.TimeoutSeconds));
        }

        var ran = await run(dialect.Aggregate(columns, from), left, cancellationToken).ConfigureAwait(false);
        if (ran.Outcome != SqlOutcome.Ok)
        {
            return new Pass1(ran, null, 0, null);
        }

        var stats = dialect.ReadAggregate(ran.Grids, columns, out long rows);
        return new Pass1(ran, stats, rows, null);
    }

    /// <summary>Whether a column's values repeat few enough to list: measured, not unique, at most <see cref="MaxDistinctForTop"/>.</summary>
    private static bool Repeats(ColumnStats stats) =>
        stats.Groupable && stats.Column.Measure is ProfileMeasure.Distinct or ProfileMeasure.Full or ProfileMeasure.Bool
        && stats is { NonNull: > 0, Distinct: > 0 and <= MaxDistinctForTop } && stats.Distinct < stats.NonNull;

    private static string Failed(SqlRun run, Request request, Func<SqlRun, string> error) =>
        run.Outcome == SqlOutcome.Timeout ? TableProfileText.Stopped(request.Shown, run.Connection, request.TimeoutSeconds) : error(run);
}

/// <summary>
/// A profile as read, for <see cref="TableProfileText.Answer"/>: the rows profiled and how (<paramref name="About"/> the catalog's estimate,
/// <paramref name="MoreThan"/> the cap the read passed), every column's measures, the columns left past <see cref="TableProfile.MaxColumns"/>,
/// and whether the distinct counts are exact over the rows read (<paramref name="ExactDistinct"/>: not an approximate count).
/// </summary>
public sealed record ProfileResult(string Table, ProfileScope Scope, long Rows, long? About, long? MoreThan, int ColumnCount, IReadOnlyList<ColumnStats> Columns,
    IReadOnlyList<string> NotProfiled, string Where, TimeSpan Elapsed, bool ExactDistinct);
