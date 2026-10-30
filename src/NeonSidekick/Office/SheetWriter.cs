using System.Globalization;
using System.Xml;
using NeonSidekick.UI;

namespace NeonSidekick.Office;

/// <summary>
/// Where rows go, a sheet being written (2026-10-29, the Office writing plan): what a database export, a CSV or the model's table fills,
/// one row at a time, so memory stays flat however many rows come.
/// </summary>
public interface ISheetRows
{
    /// <summary>The header; null when it starts, else the refusal (no columns, past Excel's <see cref="CellReference.MaxColumn"/>).</summary>
    string? Begin(IReadOnlyList<string> columns);

    /// <summary>One data row (copied when kept); false once the sheet is full at Excel's last row, so the caller stops reading.</summary>
    bool Add(IReadOnlyList<XlsxValue> row);
}

/// <summary>
/// The style indexes (<c>cellXfs</c>) a sheet's cells use: the bold header, a date, a date-time, a time, a duration, and a whole number written
/// with every digit (format <c>0</c>, 2026-10-29, review P6-1: a 13-digit barcode showed as <c>4.00638E+12</c> in General).
/// </summary>
public sealed record SheetStyles(int Header, int Date, int DateTime, int Time, int Duration, int Integer);

/// <summary>What a written sheet holds: its data rows and columns, the cells cut at Excel's cap, whether rows were left out past Excel's last row, and its filter's range.</summary>
public sealed record SheetResult(long Rows, int Columns, long CutCells, bool RowsCut, string FilterRange);

/// <summary>
/// One worksheet part, streamed (2026-10-29, the Office writing plan), shared by a new workbook (<see cref="ExcelWriter"/>) and an added or
/// replaced sheet (<see cref="WorkbookAppender"/>), each passing its own style indexes and date system. Its elements in <c>CT_Worksheet</c>'s
/// order: <c>sheetViews</c> (the header row frozen), <c>cols</c> (widths from the first <see cref="WidthRows"/> rows, held until then, wide
/// characters counted double, 8–60), <c>sheetData</c> (a bold header, then the rows; strings inline so no shared-string table is held),
/// <c>autoFilter</c>, <c>pageMargins</c>, and <c>ignoredErrors</c> (<c>numberStoredAsText</c> over text columns holding digit strings, so
/// long IDs get no green triangles). No <c>dimension</c>: it would come before the data, and Excel and the reader both scan. Past
/// <see cref="MaxDataRows"/> it stops and says so; a blank or repeated column name is made unique.
/// </summary>
public sealed class SheetWriter : ISheetRows
{
    public const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";

    /// <summary>The most data rows a sheet holds under its header (Excel's last row is 1,048,576).</summary>
    public const int MaxDataRows = CellReference.MaxRow - 1;

    /// <summary>How many rows the column widths are judged from.</summary>
    public const int WidthRows = 200;

    /// <summary>The least a whole number is written with format <c>0</c>: General shows 11 digits and more in E notation.</summary>
    public const double IntegerFrom = 1e11;

    private readonly XmlWriter _w;
    private readonly SheetStyles _styles;
    private readonly bool _date1904;
    private readonly bool _selected;
    private readonly List<XlsxValue[]> _held = [];
    private string[] _columns = [];
    private double[] _widths = [];
    private bool[] _digitText = [];
    private string[] _letters = [];
    private bool _started;
    private bool _open;
    private long _rows;
    private long _cut;
    private bool _rowsCut;

    /// <param name="selected">The sheet is the workbook's selected tab (a new workbook's only sheet; never an added one).</param>
    public SheetWriter(XmlWriter writer, SheetStyles styles, bool date1904, bool selected)
    {
        _w = writer ?? throw new ArgumentNullException(nameof(writer));
        _styles = styles ?? throw new ArgumentNullException(nameof(styles));
        _date1904 = date1904;
        _selected = selected;
    }

    /// <summary>The header as written, made unique.</summary>
    public IReadOnlyList<string> Columns => _columns;

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

        if (columns.Count > CellReference.MaxColumn)
        {
            return OfficeText.TooManyColumns(columns.Count);
        }

        _started = true;
        _columns = Unique(columns);
        _widths = [.. _columns.Select(c => Width(c) + 2.0)];   // the filter's button
        // A header that reads as a number ("2024") is text too, so its green triangle is waived with the column's (2026-10-29, review P6-7).
        _digitText = [.. _columns.Select(LooksNumeric)];
        // Each column's letters made once, not per cell (2026-10-29, review P4-7).
        _letters = [.. Enumerable.Range(1, _columns.Length).Select(CellReference.Column)];
        return null;
    }

    public bool Add(IReadOnlyList<XlsxValue> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!_started)
        {
            throw new InvalidOperationException("Begin comes first.");
        }

        if (_rows >= MaxDataRows)
        {
            _rowsCut = true;
            return false;
        }

        var cells = new XlsxValue[_columns.Length];
        for (int c = 0; c < cells.Length; c++)
        {
            cells[c] = c < row.Count ? row[c] : XlsxValue.Blank;
            if (cells[c].Cut)
            {
                _cut++;
            }

            if (!_digitText[c] && cells[c] is { Kind: XlsxKind.Text, Text: { } text } && LooksNumeric(text))
            {
                _digitText[c] = true;
            }
        }

        _rows++;
        if (!_open)
        {
            _held.Add(cells);
            foreach (var (value, c) in cells.Select((v, c) => (v, c)))
            {
                _widths[c] = Math.Max(_widths[c], Width(value));
            }

            if (_held.Count >= WidthRows)
            {
                Open();
            }

            return true;
        }

        // No flush per so many rows (2026-10-29, review P6-6): the writer's own buffer reaches the file as it fills, and each flush made the
        // zip's deflater end a block early. Finish flushes once.
        WriteRow(cells, _rows + 1);
        return true;
    }

    /// <summary>The sheet's end: whatever was held written, then the filter, margins and ignored errors.</summary>
    public SheetResult Finish()
    {
        if (!_started)
        {
            throw new InvalidOperationException("Begin comes first.");
        }

        if (!_open)
        {
            Open();
        }

        _w.WriteEndElement();   // sheetData
        string last = _letters[^1];
        string filter = "A1:" + last + (_rows + 1).ToString(CultureInfo.InvariantCulture);
        _w.WriteStartElement("autoFilter", Ns);
        _w.WriteAttributeString("ref", filter);
        _w.WriteEndElement();
        _w.WriteStartElement("pageMargins", Ns);
        foreach (var (name, value) in new[] { ("left", "0.7"), ("right", "0.7"), ("top", "0.75"), ("bottom", "0.75"), ("header", "0.3"), ("footer", "0.3") })
        {
            _w.WriteAttributeString(name, value);
        }

        _w.WriteEndElement();
        string end = (_rows + 1).ToString(CultureInfo.InvariantCulture);
        var ranges = Enumerable.Range(0, _columns.Length).Where(c => _digitText[c]).Select(c => _letters[c] + "1:" + _letters[c] + end).ToList();
        if (ranges.Count > 0)
        {
            _w.WriteStartElement("ignoredErrors", Ns);
            _w.WriteStartElement("ignoredError", Ns);
            _w.WriteAttributeString("sqref", string.Join(' ', ranges));
            _w.WriteAttributeString("numberStoredAsText", "1");
            _w.WriteEndElement();
            _w.WriteEndElement();
        }

        _w.WriteEndElement();   // worksheet
        _w.Flush();
        string absolute = "$A$1:$" + last + "$" + (_rows + 1).ToString(CultureInfo.InvariantCulture);
        return new SheetResult(_rows, _columns.Length, _cut, _rowsCut, absolute);
    }

    private void Open()
    {
        _open = true;
        _w.WriteStartElement("worksheet", Ns);
        _w.WriteAttributeString("xmlns", "r", null, OfficePackageWriter.RNs);
        _w.WriteStartElement("sheetViews", Ns);
        _w.WriteStartElement("sheetView", Ns);
        if (_selected)
        {
            _w.WriteAttributeString("tabSelected", "1");
        }

        _w.WriteAttributeString("workbookViewId", "0");
        _w.WriteStartElement("pane", Ns);
        _w.WriteAttributeString("ySplit", "1");
        _w.WriteAttributeString("topLeftCell", "A2");
        _w.WriteAttributeString("activePane", "bottomLeft");
        _w.WriteAttributeString("state", "frozen");
        _w.WriteEndElement();
        _w.WriteStartElement("selection", Ns);
        _w.WriteAttributeString("pane", "bottomLeft");
        _w.WriteAttributeString("activeCell", "A2");
        _w.WriteAttributeString("sqref", "A2");
        _w.WriteEndElement();
        _w.WriteEndElement();
        _w.WriteEndElement();
        _w.WriteStartElement("cols", Ns);
        for (int c = 0; c < _columns.Length; c++)
        {
            string number = (c + 1).ToString(CultureInfo.InvariantCulture);
            _w.WriteStartElement("col", Ns);
            _w.WriteAttributeString("min", number);
            _w.WriteAttributeString("max", number);
            _w.WriteAttributeString("width", Math.Clamp(_widths[c], 8, 60).ToString("0.##", CultureInfo.InvariantCulture));
            _w.WriteAttributeString("customWidth", "1");
            _w.WriteEndElement();
        }

        _w.WriteEndElement();
        _w.WriteStartElement("sheetData", Ns);
        _w.WriteStartElement("row", Ns);
        _w.WriteAttributeString("r", "1");
        for (int c = 0; c < _columns.Length; c++)
        {
            Cell(c, "1", new XlsxValue(XlsxKind.Text, Text: _columns[c]), _styles.Header);
        }

        _w.WriteEndElement();
        long row = 2;
        foreach (var cells in _held)
        {
            WriteRow(cells, row++);
        }

        _held.Clear();
    }

    private void WriteRow(XlsxValue[] cells, long row)
    {
        string number = row.ToString(CultureInfo.InvariantCulture);
        _w.WriteStartElement("row", Ns);
        _w.WriteAttributeString("r", number);
        for (int c = 0; c < cells.Length; c++)
        {
            if (cells[c].Kind != XlsxKind.Blank)
            {
                Cell(c, number, cells[c], null);
            }
        }

        _w.WriteEndElement();
    }

    private void Cell(int column, string row, XlsxValue value, int? style)
    {
        // A date the workbook's system can't hold is its ISO text (before 1900-03-01, Lotus's leap-day bug).
        if (value.Kind is XlsxKind.Date or XlsxKind.DateTime && NumberFormats.Serial(value.When, _date1904) is null)
        {
            value = XlsxValue.OfText(IsoText(value));
        }

        _w.WriteStartElement("c", Ns);
        _w.WriteAttributeString("r", _letters[column] + row);
        int? s = style ?? value.Kind switch
        {
            XlsxKind.Date => _styles.Date,
            XlsxKind.DateTime => _styles.DateTime,
            XlsxKind.Time => _styles.Time,
            XlsxKind.Duration => _styles.Duration,
            XlsxKind.Number when IsLargeWhole(value.Number) => _styles.Integer,
            _ => null,
        };
        if (s is { } index)
        {
            _w.WriteAttributeString("s", index.ToString(CultureInfo.InvariantCulture));
        }

        switch (value.Kind)
        {
            case XlsxKind.Text:
                _w.WriteAttributeString("t", "inlineStr");
                _w.WriteStartElement("is", Ns);
                _w.WriteStartElement("t", Ns);
                string text = value.Text ?? "";
                if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]) || text.Contains('\n', StringComparison.Ordinal)))
                {
                    _w.WriteAttributeString("xml", "space", null, "preserve");
                }

                _w.WriteString(XlsxValue.Escape(text));
                _w.WriteEndElement();
                _w.WriteEndElement();
                break;
            case XlsxKind.Bool:
                _w.WriteAttributeString("t", "b");
                _w.WriteElementString("v", Ns, value.Number != 0 ? "1" : "0");
                break;
            case XlsxKind.Date or XlsxKind.DateTime:
                _w.WriteElementString("v", Ns, NumberFormats.Serial(value.When, _date1904)!.Value.ToString("R", CultureInfo.InvariantCulture));
                break;
            default:
                _w.WriteElementString("v", Ns, value.Text ?? value.Number.ToString("R", CultureInfo.InvariantCulture));
                break;
        }

        _w.WriteEndElement();
    }

    /// <summary>A whole number General would show in E notation.</summary>
    public static bool IsLargeWhole(double number) => Math.Abs(number) >= IntegerFrom && number == Math.Floor(number);

    private static string IsoText(XlsxValue value) =>
        value.Kind == XlsxKind.Date || value.When.TimeOfDay == TimeSpan.Zero
            ? value.When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value.When.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);

    /// <summary>The cells a value fills, as the widths count them: wide characters double, the longest line of a text.</summary>
    private static double Width(XlsxValue value) => value.Kind switch
    {
        XlsxKind.Blank => 0,
        XlsxKind.Text => Width(value.Text ?? ""),
        XlsxKind.Bool => 5,
        XlsxKind.Date => 10,
        XlsxKind.DateTime => 19,
        XlsxKind.Time => 8,
        // [h]:mm:ss: the hours' digits and ":mm:ss" (2026-10-29, review P2-12: a 1,000-hour duration showed as ####).
        XlsxKind.Duration => Math.Floor(value.Number * 24).ToString(CultureInfo.InvariantCulture).Length + 6,
        XlsxKind.Number when IsLargeWhole(value.Number) => value.Number.ToString("0", CultureInfo.InvariantCulture).Length,
        _ => NumberFormats.General(value.Number).Length,
    };

    private static double Width(string text)
    {
        int widest = 0;
        foreach (string line in text.Split('\n'))
        {
            widest = Math.Max(widest, TextCells.Width(line.Length > 200 ? line[..200] : line));
        }

        return widest;
    }

    /// <summary>Text Excel would flag as a number stored as text.</summary>
    private static bool LooksNumeric(string text) =>
        text.Length is > 0 and < 64 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>The header made unique ignoring case, a blank name <see cref="Sql.SqlText.UnnamedColumn"/>'s way.</summary>
    public static string[] Unique(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var names = new string[columns.Count];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < columns.Count; c++)
        {
            string name = string.IsNullOrWhiteSpace(columns[c]) ? Sql.SqlText.UnnamedColumn(c) : XlsxValue.OfText(columns[c]).Text!;
            string candidate = name;
            for (int n = 2; !used.Add(candidate); n++)
            {
                candidate = name + " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
            }

            names[c] = candidate;
        }

        return names;
    }
}
