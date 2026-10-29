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
        s.Number &= cell.Kind == RawKind.Number ? XlsxValue.SignificantDigits(cell.Text) <= XlsxValue.MaxDigits : cell.Kind == RawKind.Text && IsNumberText(text);
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

    /// <summary>Whether <paramref name="text"/> is a number the judge takes: invariant, no <c>+</c>, no leading zero, at most 15 digits.</summary>
    public static bool IsNumberText(string text) => NumberPattern().IsMatch(text) && XlsxValue.SignificantDigits(text) <= XlsxValue.MaxDigits;

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]{1,3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}

/// <summary>
/// The tables <c>convert_to_xlsx</c> reads (2026-10-29, the Office writing plan): CSV (RFC 4180; Excel's <c>sep=X</c> first line honored and
/// dropped, else a header with no comma but a <c>;</c> read as semicolons, as European-locale exports are) and TSV (split on tabs, no quotes:
/// IANA's TSV holds no tab in a field) streamed in two passes — the columns judged, then written — with no cap but Excel's rows; JSON (an
/// array of objects, keys the header in first-seen order, or of arrays, the first the header) read whole up to <see cref="MaxJsonBytes"/>.
/// Ragged rows are padded; a row longer than the header adds columns. A file is decoded as <c>read_file</c> decodes it.
/// </summary>
public static class TableSource
{
    /// <summary>The largest JSON file read whole.</summary>
    public const long MaxJsonBytes = 64L * 1024 * 1024;

    /// <summary>How much of a file's head judges its encoding.</summary>
    private const int HeadBytes = 64 * 1024;

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

        bool tsv = extension == ".tsv";
        char delimiter = '\t';
        bool skipFirst = false;
        if (!tsv)
        {
            using var probe = OpenText(full);
            string? first = probe.ReadLine();
            if (first is not null && first.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) && first.Length == 5)
            {
                delimiter = first[4];
                skipFirst = true;
            }
            else
            {
                delimiter = first is not null && !first.Contains(',', StringComparison.Ordinal) && first.Contains(';', StringComparison.Ordinal) ? ';' : ',';
            }
        }

        IEnumerable<List<string>> Records()
        {
            using var reader = OpenText(full);
            if (skipFirst)
            {
                reader.ReadLine();
            }

            foreach (var record in tsv ? Lines(reader) : Delimited(reader, delimiter))
            {
                yield return record;
            }
        }

        return Rows(Records, display, sheet, cancellationToken);
    }

    /// <summary>
    /// Records from <paramref name="records"/> (opened once to judge, once to write; the first the header) into <paramref name="sheet"/>:
    /// null when done, else the refusal.
    /// </summary>
    public static string? Rows(Func<IEnumerable<List<string>>> records, string display, ISheetRows sheet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(sheet);
        var judge = new ColumnJudge();
        List<string>? header = null;
        int width = 0;
        foreach (var record in records())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (header is null)
            {
                header = record;
                width = record.Count;
                continue;
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

        if (sheet.Begin([.. header, .. Enumerable.Repeat("", width - header.Count)]) is { } refused)
        {
            return refused;
        }

        bool first = true;
        foreach (var record in records())
        {
            if (first)
            {
                first = false;
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!sheet.Add([.. record.Select((text, c) => judge.Convert(c, new RawCell(text)))]))
            {
                break;
            }
        }

        return null;
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
                return OfficeText.BadJson(display, "the top level is not an array");
            }

            var items = root.EnumerateArray().ToList();
            if (items.Count == 0)
            {
                return OfficeText.EmptyTable(display);
            }

            if (items.TrueForAll(i => i.ValueKind == JsonValueKind.Object))
            {
                var keys = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in items)
                {
                    foreach (var property in item.EnumerateObject())
                    {
                        if (seen.Add(property.Name))
                        {
                            keys.Add(property.Name);
                        }
                    }
                }

                var rows = items.Select(item => (IReadOnlyList<RawCell>)[.. keys.Select(k => item.TryGetProperty(k, out var v) ? Cell(v) : new RawCell("", RawKind.Null))]).ToList();
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

            return OfficeText.BadJson(display, "its items are not all objects or all arrays");
        }
    }

    /// <summary>A text file opened for reading as <c>read_file</c> decodes it: the encoding judged from its head, the mark skipped, bad bytes U+FFFD.</summary>
    public static StreamReader OpenText(string full)
    {
        var stream = RegularFiles.OpenRead(full, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            byte[] head = new byte[(int)Math.Min(HeadBytes, stream.Length)];
            stream.ReadExactly(head);
            int keep = head.Length;

            // A head cut through a UTF-8 character isn't judged invalid for it.
            if (stream.Length > head.Length)
            {
                for (int back = 0; back <= 3 && keep - back > 0; back++)
                {
                    if (System.Text.Unicode.Utf8.IsValid(head.AsSpan(0, keep - back)))
                    {
                        keep -= back;
                        break;
                    }
                }
            }

            var codec = TextFile.Detect(head.AsSpan(0, keep));
            stream.Position = codec.MarkLength;
            return new StreamReader(stream, codec.StreamEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>RFC 4180 records: quoted fields with doubled quotes and line breaks inside, CR, LF or CRLF ends.</summary>
    public static IEnumerable<List<string>> Delimited(TextReader reader, char delimiter)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var record = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        bool any = false;
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
                        field.Append('"');
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(ch);
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
                record.Add(field.ToString());
                field.Clear();
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
                    record.Add(field.ToString());
                    yield return record;
                }

                record = [];
                field.Clear();
                any = false;
            }
            else
            {
                field.Append(ch);
                any = true;
            }
        }

        if (any || field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }

    /// <summary>TSV records: one a line, split on tabs, nothing quoted; blank lines skipped.</summary>
    public static IEnumerable<List<string>> Lines(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length > 0)
            {
                yield return [.. line.Split('\t')];
            }
        }
    }
}
