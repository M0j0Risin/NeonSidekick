using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using NeonSidekick.Files;

namespace NeonSidekick.Office;

/// <summary>
/// A query's rows as JSON (2026-11-14, the database-tools plan's #13): <c>.json</c> one indented array of objects ended with a newline
/// (<c>[]</c> with no rows), <c>.jsonl</c> one compact object a line, each ended with <c>\n</c>; UTF-8 with no BOM, as RFC 8259 and JSON Lines
/// say, and the relaxed escaping, so <c>é</c> and <c>&lt;</c> stay as written (a data file, never HTML). Keys are the header made unique; a
/// number is a JSON number in its exact spelling (a whole number past 15 digits came as text and stays a string, which a JavaScript reader
/// needs), a boolean <c>true</c>/<c>false</c>, NULL <c>null</c> and <c>''</c> <c>""</c>, dates and times ISO strings. A lone surrogate is
/// U+FFFD: <see cref="Utf8JsonWriter"/> throws on one, and the export's catch doesn't take that exception.
/// </summary>
public sealed class JsonRows : TextRows
{
    /// <summary>How much is held before it is written to the file (the writer buffers in memory until then).</summary>
    private const int FlushAt = 64 * 1024;

    private readonly Stream _stream;
    private readonly bool _lines;
    private readonly Utf8JsonWriter _writer;

    /// <summary>
    /// JSON Lines' buffer (2026-11-15, the database-tools review's #61: each row was flushed to the file on its own): the rows and their line
    /// ends gather here and go to the file past <see cref="FlushAt"/>.
    /// </summary>
    private readonly ArrayBufferWriter<byte>? _buffer;
    private JsonEncodedText[] _keys = [];

    public JsonRows(Stream stream, bool lines, IReadOnlySet<int>? utc = null) : base(utc)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _lines = lines;
        var options = new JsonWriterOptions
        {
            Indented = !lines,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        if (lines)
        {
            _buffer = new ArrayBufferWriter<byte>(FlushAt + 4096);
            _writer = new Utf8JsonWriter(_buffer, options);
        }
        else
        {
            _writer = new Utf8JsonWriter(stream, options);
        }
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
        if (_buffer is not null)
        {
            // One root value a line: the writer flushed into the buffer, the line ended, and reset for the next object.
            _writer.Flush();
            _buffer.Write("\n"u8);
            _writer.Reset(_buffer);
            if (_buffer.WrittenCount >= FlushAt)
            {
                Drain();
            }
        }
        else if (_writer.BytesPending >= FlushAt)
        {
            _writer.Flush();
        }
    }

    protected override void WriteEnd()
    {
        if (_buffer is not null)
        {
            _writer.Flush();
            Drain();
        }
        else
        {
            _writer.WriteEndArray();
            _writer.Flush();
            _stream.WriteByte((byte)'\n');
        }

        _stream.Flush();
    }

    /// <summary>JSON Lines' gathered rows written to the file and the buffer emptied.</summary>
    private void Drain()
    {
        _stream.Write(_buffer!.WrittenSpan);
        _buffer.ResetWrittenCount();
    }

    public override void Dispose() => _writer.Dispose();

    private void Write(XlsxValue value, bool utc)
    {
        switch (value.Kind)
        {
            case XlsxKind.Blank:
                _writer.WriteNullValue();
                break;
            case XlsxKind.Number when NumberText(value) is var number && (value.Exact is null || IsJsonNumber(number)):
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

    /// <summary>
    /// Whether a number's source spelling (its <see cref="XlsxValue.Exact"/>) is one JSON reads (2026-11-15, the database-tools review's
    /// #78/#134: the one grammar is <see cref="CsvCell.IsPlainNumber"/>'s, and JSON adds no leading zero, so <c>007</c> is a string).
    /// Every other number's text is the app's own invariant spelling, already JSON's. Anything else is written as a string.
    /// </summary>
    public static bool IsJsonNumber(string number)
    {
        ArgumentNullException.ThrowIfNull(number);
        if (!CsvCell.IsPlainNumber(number))
        {
            return false;
        }

        int digits = number.StartsWith('-') ? 1 : 0;
        return !(number.Length > digits + 1 && number[digits] == '0' && char.IsAsciiDigit(number[digits + 1]));
    }
}
