using System.Text;
using NeonSidekick.Files;

namespace NeonSidekick.Office;

/// <summary>
/// A query's rows as CSV (2026-11-14, the database-tools plan's #13): RFC 4180 over the pending file, UTF-8 with a BOM (Excel misreads UTF-8
/// without one; the AD tools' CSV has it too), CRLF line ends, a header row. A number is its exact spelling, a boolean <c>true</c>/<c>false</c>,
/// dates and times ISO; NULL is an empty field and <c>''</c> a quoted empty one (<c>""</c>), which DuckDB reads apart and Excel doesn't. Text
/// is guarded against a spreadsheet reading it as a formula (<see cref="CsvCell"/>), a plain number left as it is; every value is written
/// exactly, whole up to 1,048,576 characters a cell (<see cref="TextRows.CellCap"/>), a lone surrogate as U+FFFD.
/// </summary>
public sealed class CsvRows : TextRows
{
    private readonly StreamWriter _writer;

    public CsvRows(Stream stream, IReadOnlySet<int>? utc = null) : base(utc)
    {
        ArgumentNullException.ThrowIfNull(stream);
        // UTF8Encoding(true) writes the BOM and replaces rather than throws; the values reach it already well formed.
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), bufferSize: 64 * 1024, leaveOpen: true) { NewLine = "\r\n" };
    }

    protected override void WriteHeader() => Line(Columns.Select(c => CsvCell.Text(c, exemptNumbers: true)));

    protected override void WriteRow(IReadOnlyList<XlsxValue> row)
    {
        for (int c = 0; c < Columns.Length; c++)
        {
            if (c > 0)
            {
                _writer.Write(',');
            }

            _writer.Write(Field(CellOf(row, c), IsUtc(c)));
        }

        _writer.WriteLine();
    }

    protected override void WriteEnd() => _writer.Flush();

    public override void Dispose() => _writer.Dispose();

    /// <summary>One cell as its CSV field.</summary>
    public static string Field(XlsxValue value, bool utc) => value.Kind switch
    {
        XlsxKind.Blank => "",
        XlsxKind.Number => NumberText(value),
        XlsxKind.Bool => value.Number != 0 ? "true" : "false",
        XlsxKind.Date => DateText(value.When),
        XlsxKind.DateTime => DateTimeText(value.When, utc),
        XlsxKind.Time => TimeText(value.Number),
        XlsxKind.Duration => DurationText(value.Number),
        // No WellFormed pass (2026-11-15, #68): the writer's UTF8Encoding replaces a lone surrogate with U+FFFD itself.
        _ => value.Text is not { Length: > 0 } text ? "\"\"" : CsvCell.Text(text, exemptNumbers: true),
    };

    private void Line(IEnumerable<string> fields)
    {
        _writer.Write(string.Join(',', fields));
        _writer.WriteLine();
    }
}
