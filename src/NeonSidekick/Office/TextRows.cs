using System.Globalization;

namespace NeonSidekick.Office;

/// <summary>The text files a query's <c>to</c> writes beside a workbook (2026-11-14, the database-tools plan's #13).</summary>
public enum TextTableFormat
{
    /// <summary><c>.csv</c>: RFC 4180, a UTF-8 BOM, CRLF, a header row.</summary>
    Csv,

    /// <summary><c>.json</c>: one indented array of objects, UTF-8 with no BOM.</summary>
    Json,

    /// <summary><c>.jsonl</c>: one compact object a line, each ended with <c>\n</c>.</summary>
    JsonLines,
}

/// <summary>
/// What the CSV and JSON writers share (2026-11-14, the database-tools plan's #13): the header made unique (<see cref="SheetWriter.Unique"/>,
/// case-blind, as a workbook's, so a JSON key never repeats), the row cap a sheet has (<see cref="SheetWriter.MaxDataRows"/>, so <c>to</c>'s
/// counts and its cut note read the same for every format), the cells cut at <see cref="XlsxValue.MaxChars"/> counted, and each kind's text.
/// They can't be an <see cref="IWorkbookJob"/>, whose sheet is the workbook's own writer, so <see cref="Finish"/> is theirs.
/// </summary>
public abstract class TextRows : ISheetRows, IDisposable
{
    private readonly IReadOnlySet<int> _utc;
    private bool _started;
    private bool _finished;
    private long _rows;
    private long _cut;
    private bool _rowsCut;

    /// <param name="utc">The columns whose date-times are UTC (a header the family marked <c> (UTC)</c>, taken off it): written with a <c>Z</c>.</param>
    protected TextRows(IReadOnlySet<int>? utc) => _utc = utc ?? new HashSet<int>();

    /// <summary>The header as written: made unique, every lone surrogate U+FFFD.</summary>
    protected string[] Columns { get; private set; } = [];

    public string? Begin(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (_started)
        {
            throw new InvalidOperationException("The header is written once.");
        }

        if (columns.Count == 0)
        {
            return OfficeText.NoColumns;
        }

        _started = true;
        Columns = [.. SheetWriter.Unique(columns).Select(XlsxValue.WellFormed)];
        WriteHeader();
        return null;
    }

    public bool Add(IReadOnlyList<XlsxValue> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!_started)
        {
            throw new InvalidOperationException("Begin comes first.");
        }

        if (_rows >= SheetWriter.MaxDataRows)
        {
            _rowsCut = true;
            return false;
        }

        for (int c = 0; c < Columns.Length; c++)
        {
            if (c < row.Count && row[c].Cut)
            {
                _cut++;
            }
        }

        _rows++;
        WriteRow(row);
        return true;
    }

    /// <summary>The file's end written and flushed: the rows and columns, the cells cut, whether rows were left out (no filter range: a workbook's).</summary>
    public SheetResult Finish()
    {
        if (!_started)
        {
            throw new InvalidOperationException("Begin comes first.");
        }

        if (!_finished)
        {
            _finished = true;
            WriteEnd();
        }

        return new SheetResult(_rows, Columns.Length, _cut, _rowsCut, "");
    }

    /// <summary>Column <paramref name="c"/> of <paramref name="row"/>, blank past its end.</summary>
    protected static XlsxValue CellOf(IReadOnlyList<XlsxValue> row, int c) => c < row.Count ? row[c] : XlsxValue.Blank;

    /// <summary>Whether column <paramref name="c"/> holds UTC date-times.</summary>
    protected bool IsUtc(int c) => _utc.Contains(c);

    protected abstract void WriteHeader();

    protected abstract void WriteRow(IReadOnlyList<XlsxValue> row);

    protected abstract void WriteEnd();

    public abstract void Dispose();

    /// <summary>A date as <c>yyyy-MM-dd</c>.</summary>
    public static string DateText(DateTime when) => when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A date-time in ISO 8601, its fraction only when there is one, and a <c>Z</c> when <paramref name="utc"/>.</summary>
    public static string DateTimeText(DateTime when, bool utc) =>
        when.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture) + (utc ? "Z" : "");

    /// <summary>A time of day from Excel's fraction of a day: <c>HH:mm:ss</c>, its fraction only when there is one.</summary>
    public static string TimeText(double days)
    {
        long ticks = Math.Clamp((long)Math.Round(days * TimeSpan.TicksPerDay), 0, TimeSpan.TicksPerDay - 1);
        return new TimeOnly(ticks).ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
    }

    /// <summary>A duration from Excel's days: the hours however many (<c>36:00:00</c>), then minutes and seconds, a fraction only when there is one.</summary>
    public static string DurationText(double days)
    {
        long ticks = Math.Max(0, (long)Math.Round(days * TimeSpan.TicksPerDay));
        var span = TimeSpan.FromTicks(ticks);
        long hours = ticks / TimeSpan.TicksPerHour;
        string fraction = span.Ticks % TimeSpan.TicksPerSecond == 0 ? ""
            : "." + (span.Ticks % TimeSpan.TicksPerSecond).ToString("0000000", CultureInfo.InvariantCulture).TrimEnd('0');
        return hours.ToString(CultureInfo.InvariantCulture) + ":" + span.Minutes.ToString("00", CultureInfo.InvariantCulture)
            + ":" + span.Seconds.ToString("00", CultureInfo.InvariantCulture) + fraction;
    }

    /// <summary>A number's spelling: its exact source text when it was rounded for Excel, else its own.</summary>
    public static string NumberText(XlsxValue value) =>
        value.Exact ?? value.Text ?? value.Number.ToString("R", CultureInfo.InvariantCulture);
}
