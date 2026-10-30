using System.Data.Common;
using System.Globalization;
using System.Numerics;
using NeonSidekick.Files;
using NeonSidekick.Office;

namespace NeonSidekick.Sql;

/// <summary>
/// A cell read for a workbook: the family's typed reader of one column of the current row. <paramref name="typeName"/> is the column's
/// type name, read once per result set (2026-10-29, review P4-8: asked per cell, it cost a provider call each).
/// </summary>
public delegate XlsxValue SqlCellReader(DbDataReader reader, int ordinal, string? typeName);

/// <summary>
/// A query's rows streamed into a workbook's sheet (2026-10-29, the Office writing plan's export, the six query tools' <c>to</c>): in place of
/// <see cref="SqlGrid.ReadAsync"/>, inside the family's own read-only session, so the gates, the session and the rollback are the chat's.
/// Each row goes straight to the <see cref="ISheetRows"/> (a <c>SheetWriter</c> over the pending file), so memory stays flat; the grid it
/// hands back holds the columns and the first <see cref="SqlExport.PreviewRows"/> rows as the chat would show them, and
/// <see cref="SqlGrid.More"/> when the cap stopped it (read one row past, as the chat does). The sheet is begun only when the first result
/// set arrives (2026-10-29, review P2-8: a query that failed to connect or parse had already copied the workbook and left a checkpoint
/// entry). A sheet that won't begin or take the columns, or a file that fails under a row (a full disk), stops the read with
/// <see cref="Refusal"/> set, never a throw the family's catch could turn into a server's error (2026-10-30, review F103), and the grid says
/// <see cref="SqlGrid.More"/>, so the family cancels the rest rather than draining it (review F53).
/// </summary>
public sealed class SqlExportJob
{
    private readonly Func<(ISheetRows? Sink, string? Refusal)> _begin;
    private SqlCellReader _cell;
    private readonly Func<DbDataReader, int, string>? _header;
    private readonly Func<Exception, string>? _failed;

    /// <param name="begin">The sheet begun (the pending file opened): the rows' sink, or null with the refusal. Called once, inside the session.</param>
    /// <param name="header">A column's header when the family adds to its name (PostgreSQL's <c> (UTC)</c>); null for the name.</param>
    /// <param name="failed">
    /// The refusal for a file failure (<see cref="XlsxTarget.Failure"/>) while the sheet takes the columns or a row, worded with the workbook's
    /// name; null lets it throw.
    /// </param>
    public SqlExportJob(Func<(ISheetRows? Sink, string? Refusal)> begin, SqlCellReader cell, Func<DbDataReader, int, string>? header = null,
        Func<Exception, string>? failed = null)
    {
        _begin = begin ?? throw new ArgumentNullException(nameof(begin));
        _cell = cell ?? throw new ArgumentNullException(nameof(cell));
        _header = header;
        _failed = failed;
    }

    /// <summary>A job over a sheet already begun.</summary>
    public SqlExportJob(ISheetRows sink, SqlCellReader cell, Func<DbDataReader, int, string>? header = null)
        : this(Already(sink ?? throw new ArgumentNullException(nameof(sink))), cell, header)
    {
    }

    private static Func<(ISheetRows?, string?)> Already(ISheetRows sink) => () => (sink, null);

    /// <summary>
    /// The cell reader replaced once the family knows the columns' types: DuckDB reads every value as text (its wrapper) and learns the
    /// types from a run of the statement with no rows first (<c>DuckDbAccess.ExportCell</c>).
    /// </summary>
    public void Retype(SqlCellReader cell) => _cell = cell ?? throw new ArgumentNullException(nameof(cell));

    /// <summary>The sheet's refusal of the columns (too many), or null.</summary>
    public string? Refusal { get; private set; }

    /// <summary>The rows written so far: what a stopped export says it had read.</summary>
    public long Rows { get; private set; }

    /// <summary>Whether a result set was read: an export takes the first only (every gate allows one statement).</summary>
    public bool Read { get; private set; }

    /// <summary>The current result set into the sheet, up to <paramref name="maxRows"/> rows.</summary>
    public async Task<SqlGrid> ReadAsync(DbDataReader reader, int maxRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var columns = new string[reader.FieldCount];
        for (int i = 0; i < columns.Length; i++)
        {
            string name = reader.GetName(i);
            columns[i] = string.IsNullOrEmpty(name) ? SqlText.UnnamedColumn(i) : name;
        }

        if (Read)
        {
            return new SqlGrid(columns, [], false);
        }

        // A refusal says More (2026-10-30, review F53: false told the families the set was read to its end, so their dispose drained every
        // row of it before the answer came back).
        Read = true;
        var (sink, refused) = _begin();
        if (sink is null)
        {
            Refusal = refused ?? FileText.SaveNotHere;
            return new SqlGrid(columns, [], true);
        }

        var headers = _header is null ? columns : [.. columns.Select((c, i) => _header(reader, i))];
        try
        {
            Refusal = sink.Begin(headers);
        }
        catch (Exception ex) when (_failed is not null && XlsxTarget.Failure(ex))
        {
            Refusal = _failed(ex);
        }

        if (Refusal is not null)
        {
            return new SqlGrid(columns, [], true);
        }

        var types = new string?[columns.Length];
        for (int i = 0; i < types.Length; i++)
        {
            types[i] = reader.GetDataTypeName(i);
        }

        var preview = new List<string[]>();
        var row = new XlsxValue[columns.Length];
        bool more = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Rows == maxRows)
            {
                more = true;
                break;
            }

            for (int i = 0; i < row.Length; i++)
            {
                row[i] = _cell(reader, i, types[i]);
            }

            bool added;
            try
            {
                added = sink.Add(row);
            }
            catch (Exception ex) when (_failed is not null && XlsxTarget.Failure(ex))
            {
                Refusal = _failed(ex);
                more = true;
                break;
            }

            if (!added)
            {
                more = true;
                break;
            }

            if (preview.Count < SqlExport.PreviewRows)
            {
                preview.Add([.. row.Select(SqlExport.Show)]);
            }

            Rows++;
        }

        return new SqlGrid(columns, preview, more);
    }
}

/// <summary>
/// What the six families' exports share (2026-10-29, the Office writing plan): the caps, the timeout, the hint a cut chat query adds, the
/// common typed cell reader and the preview's text of a cell.
/// </summary>
public static class SqlExport
{
    /// <summary>The rows the answer shows of an export.</summary>
    public const int PreviewRows = 5;

    /// <summary>The most rows an export writes: Excel's last row, the header taking the first.</summary>
    public const int MaxRows = SheetWriter.MaxDataRows;

    /// <summary>The least time an export gets (the user's call, 2026-10-29): a big result takes longer to write than a chat query to read.</summary>
    public const int MinTimeoutSeconds = 600;

    /// <summary>An export's timeout: the larger of the family's *Query timeout* and <see cref="MinTimeoutSeconds"/>.</summary>
    public static int TimeoutFor(int setting) => Math.Max(setting, MinTimeoutSeconds);

    /// <summary>
    /// A made-up workbook name's stem for a connection or database <paramref name="name"/>: <c>query-&lt;name&gt;</c>, or <c>query</c> when
    /// nothing of the name is fit for a file name (2026-10-29, review P3-12: a name of symbols alone made <c>query-.xlsx</c>).
    /// </summary>
    public static string FileStem(string? name) => FileNames.Safe(name ?? "") is { Length: > 0 } safe ? "query-" + safe : "query";

    /// <summary>Whether a column's type name says a date alone (<c>date</c> in any case): its midnight values are dates, not date-times.</summary>
    public static bool IsDateType(string? typeName) => string.Equals(typeName?.Trim(), "date", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A cell by the common rules: NULL is blank; a value of a type the sheet holds (numbers, booleans, text, dates and times, bytes, GUIDs)
    /// typed by <see cref="XlsxValue.From"/>, a date column's midnight a date (<paramref name="typeName"/>; null when no column of the family
    /// holds a date alone); anything else (an array, a provider's own type) the family's export text through <paramref name="text"/>, never
    /// the chat's clipped cell (2026-10-29, review P5-1: an array was cut at the chat's 500 characters and scrubbed). A value the provider
    /// can't hand over (past <see cref="decimal"/>) is its provider value's text, by the 15-digit rule when it is a number.
    /// </summary>
    public static XlsxValue Typed(DbDataReader reader, int ordinal, string? typeName, Func<object, string> text)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return reader.IsDBNull(ordinal) ? XlsxValue.Blank : FromValue(reader.GetValue(ordinal), typeName, text);
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException)
        {
            return FromProvider(reader, ordinal, typeName);
        }
    }

    /// <summary>A value already read (MySQL's, judged for its zero dates first) by <see cref="Typed"/>'s rules.</summary>
    public static XlsxValue FromValue(object? value, string? typeName, Func<object, string> text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return value is null or DBNull ? XlsxValue.Blank
            : IsScalar(value) ? XlsxValue.From(value, IsDateType(typeName))
            : XlsxValue.OfText(text(value));
    }

    /// <summary>A value GetValue won't hand over: its provider value's invariant text, by the 15-digit rule; the type's name when that fails too.</summary>
    public static XlsxValue FromProvider(DbDataReader reader, int ordinal, string? typeName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        try
        {
            return XlsxValue.OfNumberText(InvariantText(reader.GetProviderSpecificValue(ordinal)));
        }
        catch (Exception inner) when (inner is not (DbException or OperationCanceledException or OutOfMemoryException))
        {
            return XlsxValue.OfText(SqlExportText.Unreadable(typeName));
        }
    }

    /// <summary>
    /// A value's text for a cell, in full and culture-free (2026-10-29, review P1-11: a provider's own number type wrote the culture's
    /// separators): dates and times as ISO, bytes as hex, a double round-trip, anything formattable invariant, else its own text. The
    /// cell's cap is <see cref="XlsxValue.OfText"/>'s.
    /// </summary>
    public static string InvariantText(object? value) => value switch
    {
        null or DBNull => "",
        string s => s,
        bool b => b ? "true" : "false",
        DateTime d => d.ToString(d.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, (XlsxValue.MaxChars - 3) / 2 + 1)),
        Guid g => g.ToString("D", CultureInfo.InvariantCulture),
        double f => f.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString() ?? "",
    };

    /// <summary>Whether <paramref name="value"/> is a kind the sheet types itself; anything else goes as the family's text.</summary>
    public static bool IsScalar(object? value) => value is null or DBNull or string or bool or double or float or Half or decimal or byte or sbyte
        or short or ushort or int or uint or long or ulong or Int128 or UInt128 or BigInteger or DateOnly or DateTime or DateTimeOffset or TimeOnly
        or TimeSpan or byte[] or Guid or char;

    /// <summary>
    /// A cell of the preview as the chat shows one (<see cref="SqlText.Cell"/>: scrubbed, clipped): NULL for a blank, the number's own
    /// text, the date or time as ISO.
    /// </summary>
    public static string Show(XlsxValue value) => value.Kind switch
    {
        XlsxKind.Blank => SqlText.Null,
        XlsxKind.Bool => value.Number != 0 ? "true" : "false",
        XlsxKind.Number => value.Text ?? value.Number.ToString("R", CultureInfo.InvariantCulture),
        XlsxKind.Date => value.When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        XlsxKind.DateTime => SqlText.Cell(value.When),
        XlsxKind.Time or XlsxKind.Duration => SqlText.Cell(TimeSpan.FromDays(value.Number)),
        _ => SqlText.Cell(value.Text ?? ""),
    };
}
