using System.Data.Common;
using System.Globalization;
using System.Numerics;
using NeonSidekick.Office;

namespace NeonSidekick.Sql;

/// <summary>A cell read for a workbook: the family's typed reader of one column of the current row.</summary>
public delegate XlsxValue SqlCellReader(DbDataReader reader, int ordinal);

/// <summary>
/// A query's rows streamed into a workbook's sheet (2026-10-29, the Office writing plan's export, the six query tools' <c>to</c>): in place of
/// <see cref="SqlGrid.ReadAsync"/>, inside the family's own read-only session, so the gates, the session and the rollback are the chat's.
/// Each row goes straight to the <see cref="ISheetRows"/> (a <c>SheetWriter</c> over the pending file), so memory stays flat; the grid it
/// hands back holds the columns and the first <see cref="SqlExport.PreviewRows"/> rows as the chat would show them, and
/// <see cref="SqlGrid.More"/> when the cap stopped it (read one row past, as the chat does). A sheet that won't take the columns stops the
/// read with <see cref="Refusal"/> set, never a throw the family's catch could turn into a server's error.
/// </summary>
public sealed class SqlExportJob
{
    private readonly ISheetRows _sink;
    private SqlCellReader _cell;
    private readonly Func<DbDataReader, int, string>? _header;

    /// <param name="header">A column's header when the family adds to its name (PostgreSQL's <c> (UTC)</c>); null for the name.</param>
    public SqlExportJob(ISheetRows sink, SqlCellReader cell, Func<DbDataReader, int, string>? header = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _cell = cell ?? throw new ArgumentNullException(nameof(cell));
        _header = header;
    }

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

        Read = true;
        var headers = _header is null ? columns : [.. columns.Select((c, i) => _header(reader, i))];
        Refusal = _sink.Begin(headers);
        if (Refusal is not null)
        {
            return new SqlGrid(columns, [], false);
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
                row[i] = _cell(reader, i);
            }

            if (!_sink.Add(row))
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

    /// <summary>The clause a cut chat query's "more exist" adds while a save root is open, so the model learns <c>to</c> when it helps.</summary>
    public const string SaveHint = ", or write them all to a spreadsheet with to";

    /// <summary>The hint when <paramref name="canSave"/>, else nothing: the six families' cut sentences end with it.</summary>
    public static string Hint(bool canSave) => canSave ? SaveHint : "";

    /// <summary>Whether a column's type name says a date alone (<c>date</c> in any case): its midnight values are dates, not date-times.</summary>
    public static bool IsDateType(string? typeName) => string.Equals(typeName?.Trim(), "date", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A cell by the common rules: NULL is blank; a value of a type the sheet holds (numbers, booleans, text, dates and times, bytes, GUIDs)
    /// typed by <see cref="XlsxValue.From"/>, a date column's midnight a date; anything else (an array, a provider's own type) the family's
    /// own text through <paramref name="fallback"/>. A value the provider can't hand over (past <see cref="decimal"/>) is its provider value's
    /// text, by the 15-digit rule when it is a number.
    /// </summary>
    public static XlsxValue Typed(DbDataReader reader, int ordinal, Func<DbDataReader, int, string> fallback)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(fallback);
        try
        {
            if (reader.IsDBNull(ordinal))
            {
                return XlsxValue.Blank;
            }

            object value = reader.GetValue(ordinal);
            return IsScalar(value) ? XlsxValue.From(value, IsDateType(reader.GetDataTypeName(ordinal))) : XlsxValue.OfText(fallback(reader, ordinal));
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException)
        {
            try
            {
                string text = reader.GetProviderSpecificValue(ordinal)?.ToString() ?? "";
                return XlsxValue.OfNumberText(text);
            }
            catch (Exception inner) when (inner is not (DbException or OperationCanceledException or OutOfMemoryException))
            {
                return XlsxValue.OfText(fallback(reader, ordinal));
            }
        }
    }

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
