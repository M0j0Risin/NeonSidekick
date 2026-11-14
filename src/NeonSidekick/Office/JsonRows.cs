using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeonSidekick.Office;

/// <summary>
/// A query's rows as JSON (2026-11-14, the database-tools plan's #13): <c>.json</c> one indented array of objects ended with a newline
/// (<c>[]</c> with no rows), <c>.jsonl</c> one compact object a line, each ended with <c>\n</c>; UTF-8 with no BOM, as RFC 8259 and JSON Lines
/// say, and the relaxed escaping, so <c>é</c> and <c>&lt;</c> stay as written (a data file, never HTML). Keys are the header made unique; a
/// number is a JSON number in its exact spelling (a whole number past 15 digits came as text and stays a string, which a JavaScript reader
/// needs), a boolean <c>true</c>/<c>false</c>, NULL <c>null</c> and <c>''</c> <c>""</c>, dates and times ISO strings. A lone surrogate is
/// U+FFFD: <see cref="Utf8JsonWriter"/> throws on one, and the export's catch doesn't take that exception.
/// </summary>
public sealed partial class JsonRows : TextRows
{
    /// <summary>How much the writer holds before it is flushed to the file (it buffers in memory until then).</summary>
    private const int FlushAt = 64 * 1024;

    private readonly Stream _stream;
    private readonly bool _lines;
    private readonly Utf8JsonWriter _writer;
    private JsonEncodedText[] _keys = [];

    public JsonRows(Stream stream, bool lines, IReadOnlySet<int>? utc = null) : base(utc)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _lines = lines;
        _writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = !lines,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    protected override void WriteHeader()
    {
        _keys = [.. Columns.Select(c => JsonEncodedText.Encode(c, JavaScriptEncoder.UnsafeRelaxedJsonEscaping))];
        if (!_lines)
        {
            _writer.WriteStartArray();
        }
    }

    protected override void WriteRow(IReadOnlyList<XlsxValue> row)
    {
        _writer.WriteStartObject();
        for (int c = 0; c < _keys.Length; c++)
        {
            _writer.WritePropertyName(_keys[c]);
            Write(CellOf(row, c), IsUtc(c));
        }

        _writer.WriteEndObject();
        if (_lines)
        {
            // One root value a line: the writer flushed, the line ended, and reset for the next object.
            _writer.Flush();
            _stream.WriteByte((byte)'\n');
            _writer.Reset(_stream);
        }
        else if (_writer.BytesPending >= FlushAt)
        {
            _writer.Flush();
        }
    }

    protected override void WriteEnd()
    {
        if (!_lines)
        {
            _writer.WriteEndArray();
            _writer.Flush();
            _stream.WriteByte((byte)'\n');
        }

        _writer.Flush();
        _stream.Flush();
    }

    public override void Dispose() => _writer.Dispose();

    private void Write(XlsxValue value, bool utc)
    {
        switch (value.Kind)
        {
            case XlsxKind.Blank:
                _writer.WriteNullValue();
                break;
            case XlsxKind.Number when NumberText(value) is var number && JsonNumber().IsMatch(number):
                _writer.WriteRawValue(number, skipInputValidation: false);
                break;
            case XlsxKind.Number:
                _writer.WriteStringValue(NumberText(value));
                break;
            case XlsxKind.Bool:
                _writer.WriteBooleanValue(value.Number != 0);
                break;
            case XlsxKind.Date:
                _writer.WriteStringValue(DateText(value.When));
                break;
            case XlsxKind.DateTime:
                _writer.WriteStringValue(DateTimeText(value.When, utc));
                break;
            case XlsxKind.Time:
                _writer.WriteStringValue(TimeText(value.Number));
                break;
            case XlsxKind.Duration:
                _writer.WriteStringValue(DurationText(value.Number));
                break;
            default:
                _writer.WriteStringValue(XlsxValue.WellFormed(value.Text ?? ""));
                break;
        }
    }

    /// <summary>A number as JSON spells one (no leading <c>+</c>, <c>.5</c> or <c>1.</c>): anything else a number's text holds is written as a string.</summary>
    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex JsonNumber();
}
