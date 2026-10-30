using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NeonSidekick.Files;

namespace NeonSidekick.Office;

/// <summary>What a source cell was before it became text: JSON's own types count as such in a column's inference.</summary>
public enum RawKind
{
    Text,
    Number,
    Bool,
    Null,
    Nested,
}

/// <summary>One source cell: its text and, from JSON, its own type.</summary>
public readonly record struct RawCell(string Text, RawKind Kind = RawKind.Text)
{
    public bool Empty => Kind == RawKind.Null || (Kind == RawKind.Text && Text.Length == 0);
}

/// <summary>
/// A column's type, judged over every cell and never per cell (2026-10-29, the Office writing plan: a column of ZIP codes, <c>02134</c> and
/// <c>90210</c>, stays text throughout). Numbers only when every non-empty cell is an invariant number with no leading zero, no <c>+</c> and
/// at most 15 digits (so <c>1.234,56</c> stays text rather than being guessed); dates when every cell is ISO 8601 with no offset; booleans
/// when every cell is <c>true</c> or <c>false</c>. JSON's own numbers and booleans count as such. Pure.
/// </summary>
public sealed partial class ColumnJudge
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd"];

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF",
    ];

    private readonly List<State> _columns = [];

    private sealed class State
    {
        public bool Number = true;
        public bool Date = true;
        public bool DateTime = true;
        public bool Bool = true;
        public bool Any;
    }

    /// <summary>One cell seen in <paramref name="column"/>.</summary>
    public void Observe(int column, RawCell cell)
    {
        while (_columns.Count <= column)
        {
            _columns.Add(new State());
        }

        if (cell.Empty)
        {
            return;
        }

        var s = _columns[column];
        s.Any = true;
        string text = cell.Text.Trim();
        s.Number &= cell.Kind == RawKind.Number ? XlsxValue.TryExcelNumber(cell.Text, out _) : cell.Kind == RawKind.Text && IsNumberText(text);
        s.Bool &= cell.Kind == RawKind.Bool || (cell.Kind == RawKind.Text && (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase)));
        bool date = cell.Kind == RawKind.Text && System.DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        s.Date &= date;
        s.DateTime &= date || (cell.Kind == RawKind.Text && System.DateTime.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    }

    /// <summary><paramref name="cell"/> as its column's type makes it.</summary>
    public XlsxValue Convert(int column, RawCell cell)
    {
        if (cell.Empty)
        {
            return XlsxValue.Blank;
        }

        if (cell.Kind == RawKind.Nested || column >= _columns.Count || !_columns[column].Any)
        {
            return XlsxValue.OfText(cell.Text);
        }

        var s = _columns[column];
        string text = cell.Text.Trim();
        if (s.Number)
        {
            return XlsxValue.OfNumberText(text);
        }

        if (s.Bool)
        {
            return XlsxValue.OfBool(text.Equals("true", StringComparison.OrdinalIgnoreCase));
        }

        if (s.Date && System.DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return XlsxValue.OfDateTime(day, dateColumn: true);
        }

        if (s.DateTime && System.DateTime.TryParseExact(text, [.. DateFormats, .. DateTimeFormats], CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment))
        {
            return XlsxValue.OfDateTime(moment);
        }

        return XlsxValue.OfText(cell.Text);
    }

    /// <summary>
    /// Whether <paramref name="text"/> is a number the judge takes: invariant, no <c>+</c>, no leading zero, and one Excel holds without losing
    /// a digit (<see cref="XlsxValue.TryExcelNumber"/>: at most 15 digits, no exponent past a double's range). An exponent counts only after a
    /// decimal point or with a sign (2026-10-30, review F44: codes like <c>1E10</c> and <c>2E05</c> turned a column into numbers); Python's
    /// <c>1e-05</c>, R's <c>1e+10</c> and Excel's <c>1.00E+10</c> all sign theirs.
    /// </summary>
    public static bool IsNumberText(string text) => NumberPattern().IsMatch(text) && XlsxValue.TryExcelNumber(text, out _);

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?((?:(?<=\.[0-9]+)[eE][-+]?|[eE][-+])[0-9]{1,3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}

/// <summary>A CSV that ends inside a quoted field; <see cref="Row"/> is the record the quote opened in, the header row 1.</summary>
public sealed class UnclosedQuoteException(int row) : Exception("a quote never closes, from row " + row.ToString(CultureInfo.InvariantCulture))
{
    public int Row { get; } = row;
}

/// <summary>
/// The tables <c>convert_to_xlsx</c> reads (2026-10-29, the Office writing plan): CSV (RFC 4180; Excel's <c>sep=X</c> first line honored and
/// dropped, else a header with more <c>;</c> than <c>,</c> outside quotes read as semicolons, as European-locale exports are) and TSV (split on
/// tabs, no quotes: IANA's TSV holds no tab in a field) streamed in two passes — the columns judged, then written — with no cap but Excel's
/// rows; JSON (an array of objects, keys the header in first-seen order, or of arrays, the first the header) read whole up to
/// <see cref="MaxJsonBytes"/>. Ragged rows are padded; a row longer than the header adds columns. A file is decoded as <c>read_file</c>
/// decodes it, its encoding judged over the whole file once. A field stops growing past Excel's cell cap and a record past its last column
/// (2026-10-29, review P4-2: one unclosed quote read the rest of a large file into one string).
/// </summary>
public static class TableSource
{
    /// <summary>The largest JSON file read whole.</summary>
    public const long MaxJsonBytes = 64L * 1024 * 1024;

    /// <summary>Whether <paramref name="path"/> names a table <c>convert_to_xlsx</c> reads, by its extension (any case).</summary>
    public static bool IsTablePath(string path) =>
        Path.GetExtension(path.Trim()).ToLowerInvariant() is ".csv" or ".tsv" or ".json";

    /// <summary>
    /// The file at <paramref name="full"/> written into <paramref name="sheet"/>: null when done, else the refusal (<paramref name="display"/>
    /// names it). <paramref name="cancellationToken"/> stops it between rows.
    /// </summary>
    public static string? Write(string full, string display, ISheetRows sheet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        string extension = Path.GetExtension(full).ToLowerInvariant();
        if (extension == ".json")
        {
            return Json(full, display, sheet, cancellationToken);
        }

        // Its length and time now, held against both passes (2026-10-29, review P1-16: a file still being saved was judged on one text and
        // written from another), and its encoding judged once for both: a mark or a UTF-16 pattern by the head, else UTF-8 read strictly by the
        // judging pass, which starts again in the legacy code page at the first byte that isn't (2026-10-30, review F22: a whole read of its
        // own did that first). Nothing is written before the judging pass ends, so a restart leaves no trace.
        var stamp = Stamp(full);
        TextCodec? head;
        using (var stream = RegularFiles.OpenRead(full, FileShare.ReadWrite | FileShare.Delete))
        {
            head = TextFile.DetectHead(stream);
        }

        var codec = head ?? TextCodec.Utf8;
        bool strict = head is null;
        bool tsv = extension == ".tsv";
        while (true)
        {
            try
            {
                return ReadDelimited(full, display, sheet, codec, strict, tsv, () => Stamp(full) == stamp, cancellationToken);
            }
            catch (DecoderFallbackException) when (strict)
            {
                codec = TextFile.LegacyCodec;
                strict = false;
            }
        }
    }

    /// <summary>
    /// <see cref="Write"/>'s CSV or TSV in <paramref name="codec"/>: the delimiter probed, then <see cref="Rows"/>. With <paramref name="strict"/>
    /// the probe and the judging pass throw <see cref="DecoderFallbackException"/> at a byte that isn't UTF-8; the writing pass never does.
    /// </summary>
    private static string? ReadDelimited(string full, string display, ISheetRows sheet, TextCodec codec, bool strict, bool tsv, Func<bool> unchanged, CancellationToken cancellationToken)
    {
        char delimiter = '\t';
        bool skipFirst = false;
        if (!tsv)
        {
            using var probe = OpenText(full, codec, strict);
            string first = FirstLine(probe);
            if (first.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) && first.Length == 5)
            {
                delimiter = first[4];
                skipFirst = true;
            }
            else
            {
                delimiter = Delimiter(first);
            }
        }

        int opened = 0;
        IEnumerable<List<string>> Records()
        {
            // The first opening is the judging pass: strict when asked. The writing pass reads forgivingly, as before.
            using var reader = OpenText(full, codec, strict && opened++ == 0);
            if (skipFirst)
            {
                FirstLine(reader);
            }

            foreach (var record in tsv ? Lines(reader) : Delimited(reader, delimiter))
            {
                yield return record;
            }
        }

        try
        {
            return Rows(Records, display, sheet, cancellationToken, unchanged);
        }
        catch (UnclosedQuoteException ex)
        {
            return OfficeText.UnclosedQuote(display, ex.Row);
        }
    }

    /// <summary>The file's length and last write time, what <see cref="Write"/> holds the passes to.</summary>
    private static (long Length, DateTime Written) Stamp(string full)
    {
        var info = new FileInfo(full);
        return (info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>The longest first line <see cref="FirstLine"/> holds: a header past it is judged on its start.</summary>
    private const int FirstLineChars = 64 * 1024;

    /// <summary>The first line, to its end or <see cref="FirstLineChars"/>; the rest of a longer one is read and dropped.</summary>
    private static string FirstLine(TextReader reader)
    {
        var line = new StringBuilder();
        int c;
        while ((c = reader.Read()) >= 0 && c is not '\r' and not '\n')
        {
            if (line.Length < FirstLineChars)
            {
                line.Append((char)c);
            }
        }

        if (c == '\r' && reader.Peek() == '\n')
        {
            reader.Read();
        }

        return line.ToString();
    }

    /// <summary>
    /// A CSV's delimiter from its first line: <c>;</c> only when more of them than of <c>,</c> stand outside quotes, so a tie (none of
    /// either included) stays <c>,</c> (2026-10-29, review P1-16: <c>"Smith, J";Age</c> held a comma inside quotes and was read as commas).
    /// </summary>
    public static char Delimiter(string firstLine)
    {
        ArgumentNullException.ThrowIfNull(firstLine);
        int commas = 0;
        int semicolons = 0;
        bool quoted = false;
        foreach (char ch in firstLine)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && ch == ',')
            {
                commas++;
            }
            else if (!quoted && ch == ';')
            {
                semicolons++;
            }
        }

        return semicolons > commas ? ';' : ',';
    }

    /// <summary>
    /// Records from <paramref name="records"/> (opened once to judge, once to write; the first the header) into <paramref name="sheet"/>:
    /// null when done, else the refusal. The judging pass stops one record past <paramref name="maxRows"/> (2026-10-29, review P4-4: a file
    /// past Excel's last row was judged to its end, and its unwritten rows could turn a column to text), so the sheet still says it was cut;
    /// the writing pass takes no more records than the judging pass saw, and <paramref name="unchanged"/>, when given, is asked before and
    /// after it (review P1-16): a source that changed meanwhile is refused, nothing written.
    /// </summary>
    public static string? Rows(Func<IEnumerable<List<string>>> records, string display, ISheetRows sheet, CancellationToken cancellationToken,
        Func<bool>? unchanged = null, int maxRows = SheetWriter.MaxDataRows)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(sheet);
        var judge = new ColumnJudge();
        List<string>? header = null;
        int width = 0;
        long seen = 0;
        foreach (var record in records())
        {
            cancellationToken.ThrowIfCancellationRequested();
            seen++;
            if (header is null)
            {
                header = record;
                width = record.Count;
                continue;
            }

            // The record past the cap is counted, so the sheet says it was cut, but never judged nor widens the sheet (2026-10-30, review F114:
            // its text could turn a column the written rows hold as numbers into text, and its length add empty columns).
            if (seen > (long)maxRows + 1)
            {
                break;
            }

            width = Math.Max(width, record.Count);
            for (int c = 0; c < record.Count; c++)
            {
                judge.Observe(c, new RawCell(record[c]));
            }
        }

        if (header is null)
        {
            return OfficeText.EmptyTable(display);
        }

        if (unchanged is not null && !unchanged())
        {
            return OfficeText.ChangedWhileRead(display);
        }

        if (sheet.Begin([.. header, .. Enumerable.Repeat("", width - header.Count)]) is { } refused)
        {
            return refused;
        }

        long written = 0;
        foreach (var record in records())
        {
            if (++written == 1)
            {
                continue;
            }

            if (written > seen)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!sheet.Add([.. record.Select((text, c) => judge.Convert(c, new RawCell(text)))]))
            {
                break;
            }
        }

        return unchanged is not null && !unchanged() ? OfficeText.ChangedWhileRead(display) : null;
    }

    /// <summary>
    /// A table in memory (the model's, or a JSON file's) into <paramref name="sheet"/>: <paramref name="header"/> and rows of
    /// <see cref="RawCell"/>s, judged then written; null when done, else the refusal.
    /// </summary>
    public static string? Cells(IReadOnlyList<string> header, IReadOnlyList<IReadOnlyList<RawCell>> rows, ISheetRows sheet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(sheet);
        var judge = new ColumnJudge();
        foreach (var row in rows)
        {
            for (int c = 0; c < row.Count; c++)
            {
                judge.Observe(c, row[c]);
            }
        }

        if (sheet.Begin(header) is { } refused)
        {
            return refused;
        }

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sheet.Add([.. row.Select((cell, c) => judge.Convert(c, cell))]))
            {
                break;
            }
        }

        return null;
    }

    /// <summary>A JSON value as a cell: a string, number or boolean as itself, null blank, an object or array as its compact text.</summary>
    public static RawCell Cell(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => new RawCell(value.GetString() ?? ""),
        JsonValueKind.Number => new RawCell(value.GetRawText(), RawKind.Number),
        JsonValueKind.True => new RawCell("true", RawKind.Bool),
        JsonValueKind.False => new RawCell("false", RawKind.Bool),
        JsonValueKind.Null or JsonValueKind.Undefined => new RawCell("", RawKind.Null),
        _ => new RawCell(Compact(value), RawKind.Nested),
    };

    private static string Compact(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            value.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? Json(string full, string display, ISheetRows sheet, CancellationToken cancellationToken)
    {
        if (new FileInfo(full).Length > MaxJsonBytes)
        {
            return OfficeText.JsonTooLarge(display, MaxJsonBytes);
        }

        string text;
        using (var reader = OpenText(full))
        {
            text = reader.ReadToEnd();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 });
        }
        catch (JsonException ex)
        {
            return OfficeText.BadJson(display, ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                return OfficeText.BadJson(display, OfficeText.JsonNotArray);
            }

            var items = root.EnumerateArray().ToList();
            if (items.Count == 0)
            {
                return OfficeText.EmptyTable(display);
            }

            if (items.TrueForAll(i => i.ValueKind == JsonValueKind.Object))
            {
                // One pass over each item's properties, a key's column found by a dictionary (2026-10-29, review P4-6: TryGetProperty per
                // key per item walked every property, quadratic in a wide object). A repeated key's last value wins, as TryGetProperty's did.
                var keys = new List<string>();
                var column = new Dictionary<string, int>(StringComparer.Ordinal);
                var rows = new List<IReadOnlyList<RawCell>>(items.Count);
                foreach (var item in items)
                {
                    var cells = new List<RawCell>(keys.Count);
                    foreach (var property in item.EnumerateObject())
                    {
                        if (!column.TryGetValue(property.Name, out int c))
                        {
                            c = keys.Count;
                            column.Add(property.Name, c);
                            keys.Add(property.Name);
                        }

                        while (cells.Count <= c)
                        {
                            cells.Add(new RawCell("", RawKind.Null));
                        }

                        cells[c] = Cell(property.Value);
                    }

                    rows.Add(cells);
                }

                if (keys.Count == 0)
                {
                    // Objects with no keys: no header to write (review P7-9: the writer's "the result has no columns").
                    return OfficeText.EmptyTable(display);
                }

                return Cells(keys, rows, sheet, cancellationToken);
            }

            if (items.TrueForAll(i => i.ValueKind == JsonValueKind.Array))
            {
                var header = items[0].EnumerateArray().Select(v => Cell(v).Text).ToList();
                var rows = items.Skip(1).Select(item => (IReadOnlyList<RawCell>)[.. item.EnumerateArray().Select(Cell)]).ToList();
                int width = Math.Max(header.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Count));
                header.AddRange(Enumerable.Repeat("", width - header.Count));
                return Cells(header, rows, sheet, cancellationToken);
            }

            return OfficeText.BadJson(display, OfficeText.JsonMixedItems);
        }
    }

    /// <summary>
    /// A text file opened for reading as <c>read_file</c> decodes it: the encoding judged over the whole file when none is given
    /// (<see cref="TextFile.Detect(Stream)"/>; the comment said "from its head" until 2026-10-30, review F46), the mark skipped, bad bytes U+FFFD —
    /// or, with <paramref name="strict"/> and UTF-8, a <see cref="DecoderFallbackException"/> (<see cref="Write"/>'s judging pass, review F22).
    /// </summary>
    public static StreamReader OpenText(string full, TextCodec? codec = null, bool strict = false)
    {
        var stream = RegularFiles.OpenRead(full, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            codec ??= TextFile.Detect(stream);
            stream.Position = codec.MarkLength;
            var encoding = strict && codec.Form is TextForm.Utf8 or TextForm.Utf8Bom ? new UTF8Encoding(false, throwOnInvalidBytes: true) : codec.StreamEncoding;
            return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>A field's text: one character past Excel's cap is kept, so the cell is cut and counted, the rest read and dropped.</summary>
    private const int FieldChars = XlsxValue.MaxChars + 1;

    /// <summary>A record's fields: one past Excel's last column is kept, so the sheet refuses it, the rest read and dropped.</summary>
    private const int RecordFields = CellReference.MaxColumn + 1;

    private static void Append(StringBuilder field, char ch)
    {
        if (field.Length < FieldChars)
        {
            field.Append(ch);
        }
    }

    private static void End(List<string> record, StringBuilder field)
    {
        if (record.Count < RecordFields)
        {
            record.Add(field.ToString());
        }

        field.Clear();
    }

    /// <summary>
    /// RFC 4180 records: quoted fields with doubled quotes and line breaks inside, CR, LF or CRLF ends. A quote still open at the end is
    /// <see cref="UnclosedQuoteException"/>, naming the record it opened in.
    /// </summary>
    public static IEnumerable<List<string>> Delimited(TextReader reader, char delimiter)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var record = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        bool any = false;
        int number = 1;
        int c;
        while ((c = reader.Read()) >= 0)
        {
            char ch = (char)c;
            if (quoted)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        Append(field, '"');
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    Append(field, ch);
                }

                continue;
            }

            if (ch == '"' && field.Length == 0)
            {
                quoted = true;
                any = true;
            }
            else if (ch == delimiter)
            {
                End(record, field);
                any = true;
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                }

                if (any || field.Length > 0 || record.Count > 0)
                {
                    End(record, field);
                    yield return record;
                    number++;
                }

                record = [];
                field.Clear();
                any = false;
            }
            else
            {
                Append(field, ch);
                any = true;
            }
        }

        if (quoted)
        {
            throw new UnclosedQuoteException(number);
        }

        if (any || field.Length > 0 || record.Count > 0)
        {
            End(record, field);
            yield return record;
        }
    }

    /// <summary>TSV records: one a line, split on tabs, nothing quoted; blank lines skipped. Read a character at a time under the same caps.</summary>
    public static IEnumerable<List<string>> Lines(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var record = new List<string>();
        var field = new StringBuilder();
        bool any = false;
        int c;
        while ((c = reader.Read()) >= 0)
        {
            char ch = (char)c;
            if (ch == '\t')
            {
                End(record, field);
                any = true;
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                }

                if (any)
                {
                    End(record, field);
                    yield return record;
                }

                record = [];
                field.Clear();
                any = false;
            }
            else
            {
                Append(field, ch);
                any = true;
            }
        }

        if (any)
        {
            End(record, field);
            yield return record;
        }
    }
}
