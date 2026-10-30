using System.Globalization;
using System.Numerics;
using System.Text;
using NeonSidekick.Files;

namespace NeonSidekick.Office;

/// <summary>What one written cell holds.</summary>
public enum XlsxKind
{
    Blank,
    Number,
    Bool,
    Date,
    DateTime,
    Time,
    Duration,
    Text,
}

/// <summary>
/// One cell's typed value and the rules that make it (2026-10-29, the Office writing plan): never a formula (text is always a string cell,
/// so <c>=cmd|…</c> stays text); a number past 15 significant digits, NaN or infinity as text (an ID like <c>1234567890123456789</c> isn't
/// mangled into <c>1.23457E+18</c>); a date kept as its <see cref="DateTime"/> until the writer knows the workbook's date system, then text
/// when the system can't hold it (before 1900-03-01, Lotus's leap-day bug); a <see cref="DateTimeOffset"/> as ISO 8601 text with its offset;
/// a <see cref="TimeSpan"/> a time of day in [0, 24 h), a <c>[h]:mm:ss</c> duration past it, text when negative; text cut at Excel's
/// <see cref="MaxChars"/> with <c>…</c> (<see cref="Cut"/> counted by the answer); bytes as <c>0x…</c> hex under the same cap. Pure.
/// </summary>
/// <param name="Number">A number's value, a time's or a duration's in days.</param>
/// <param name="Text">A text cell's text, or a number's exact invariant spelling (a decimal as written, a double round-trip).</param>
/// <param name="When">A date's or a date-time's value.</param>
/// <param name="Cut">The text was cut at <see cref="MaxChars"/>.</param>
public readonly record struct XlsxValue(XlsxKind Kind, double Number = 0, string? Text = null, DateTime When = default, bool Cut = false)
{
    /// <summary>The most characters Excel holds in a cell.</summary>
    public const int MaxChars = 32_767;

    /// <summary>The most significant digits Excel keeps of a number.</summary>
    public const int MaxDigits = 15;

    public static readonly XlsxValue Blank = new(XlsxKind.Blank);

    /// <summary>
    /// An empty string, which is not NULL (2026-10-29, review P3-4: a database's <c>''</c> and its NULL both came out blank): an empty text
    /// cell. A CSV's empty field stays <see cref="Blank"/> through <see cref="OfText"/>, since CSV can't tell the two apart.
    /// </summary>
    public static readonly XlsxValue EmptyText = new(XlsxKind.Text, Text: "");

    /// <summary>
    /// A database's text: <see cref="EmptyText"/> for <c>''</c>, else <see cref="OfText"/> (2026-10-30, review F15/F42: DuckDB's cells and
    /// SQLite's date-column text still went through <see cref="OfText"/>, so their <c>''</c> came out blank like a NULL).
    /// </summary>
    public static XlsxValue OfDbText(string text) => text.Length == 0 ? EmptyText : OfText(text);

    /// <summary>Text, cut at <see cref="MaxChars"/>; null or empty is a blank cell.</summary>
    public static XlsxValue OfText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Blank;
        }

        return text.Length <= MaxChars
            ? new XlsxValue(XlsxKind.Text, Text: text)
            : new XlsxValue(XlsxKind.Text, Text: TextCut.Safe(text, MaxChars - 1) + "…", Cut: true);
    }

    public static XlsxValue OfBool(bool value) => new(XlsxKind.Bool, value ? 1 : 0);

    /// <summary>A double as itself (Excel stores doubles, so nothing is lost); NaN and the infinities as text.</summary>
    public static XlsxValue OfDouble(double value) =>
        double.IsFinite(value)
            ? new XlsxValue(XlsxKind.Number, value, value.ToString("R", CultureInfo.InvariantCulture))
            : OfText(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>A decimal written as its own exact text within <see cref="MaxDigits"/> significant digits, else as text.</summary>
    public static XlsxValue OfDecimal(decimal value)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);
        return SignificantDigits(text) <= MaxDigits ? new XlsxValue(XlsxKind.Number, (double)value, text) : OfText(text);
    }

    /// <summary>An integer as a number within <see cref="MaxDigits"/> significant digits, else as text.</summary>
    public static XlsxValue OfInteger(BigInteger value)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);
        return SignificantDigits(text) <= MaxDigits ? new XlsxValue(XlsxKind.Number, (double)value, text) : OfText(text);
    }

    /// <summary>A number already in invariant text (a reader's own spelling): a number by <see cref="TryExcelNumber"/>, else text.</summary>
    public static XlsxValue OfNumberText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryExcelNumber(text, out var number) ? number : OfText(text);
    }

    /// <summary>
    /// <paramref name="text"/> as a number Excel holds without losing a digit (2026-10-29, review P1-2/P2-5: <c>1e-30</c> parsed as a decimal
    /// 0, <c>1e400</c> as infinity, both written as numbers): the decimal when its digits are the text's own, within 15; else the double
    /// when it is finite, not a zero the text's digits aren't, and the text has at most 15 digits; else false (the text stays text). Pure.
    /// </summary>
    public static bool TryExcelNumber(string text, out XlsxValue value)
    {
        ArgumentNullException.ThrowIfNull(text);
        int digits = SignificantDigits(text);
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal exact))
        {
            string written = exact.ToString(CultureInfo.InvariantCulture);
            if (SignificantDigits(written) == digits && digits <= MaxDigits)
            {
                value = new XlsxValue(XlsxKind.Number, (double)exact, written);
                return true;
            }
        }

        if (digits <= MaxDigits && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) && double.IsFinite(real)
            && (real != 0 || digits == 0))
        {
            value = OfDouble(real);
            return true;
        }

        value = default;
        return false;
    }

    public static XlsxValue OfDate(DateOnly value) => new(XlsxKind.Date, When: value.ToDateTime(TimeOnly.MinValue));

    /// <summary>A date-time; a date alone when it is midnight in a column whose type is a date (<paramref name="dateColumn"/>).</summary>
    public static XlsxValue OfDateTime(DateTime value, bool dateColumn = false) =>
        new(dateColumn && value.TimeOfDay == TimeSpan.Zero ? XlsxKind.Date : XlsxKind.DateTime, When: value);

    public static XlsxValue OfTime(TimeOnly value) => new(XlsxKind.Time, NumberFormats.Days(value.ToTimeSpan()));

    /// <summary>A time of day in [0, 24 h), a duration past it, text when negative.</summary>
    public static XlsxValue OfTimeSpan(TimeSpan value) =>
        value < TimeSpan.Zero ? OfText(value.ToString("c", CultureInfo.InvariantCulture))
        : value < TimeSpan.FromDays(1) ? new XlsxValue(XlsxKind.Time, NumberFormats.Days(value))
        : new XlsxValue(XlsxKind.Duration, NumberFormats.Days(value));

    /// <summary>ISO 8601 text with the offset: the offset is the value, so it is never converted away.</summary>
    public static XlsxValue OfOffset(DateTimeOffset value) => OfText(value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture));

    /// <summary>Bytes as <c>0x…</c> hex, cut at <see cref="MaxChars"/>; <paramref name="more"/> when the reader stopped short of the end.</summary>
    public static XlsxValue OfBytes(ReadOnlySpan<byte> bytes, bool more = false)
    {
        int fit = (MaxChars - 3) / 2;
        bool cut = more || bytes.Length > fit;
        string hex = "0x" + Convert.ToHexString(bytes.Length > fit ? bytes[..fit] : bytes);
        return new XlsxValue(XlsxKind.Text, Text: cut ? hex + "…" : hex, Cut: cut);
    }

    /// <summary>
    /// Any value a reader hands over, by its runtime type: the numbers, booleans, dates, times, offsets, bytes and GUIDs; anything else
    /// as its invariant text. <paramref name="dateColumn"/> as <see cref="OfDateTime"/> takes it.
    /// </summary>
    public static XlsxValue From(object? value, bool dateColumn = false) => value switch
    {
        null or DBNull => Blank,
        string { Length: 0 } => EmptyText,
        string s => OfText(s),
        bool b => OfBool(b),
        double d => OfDouble(d),
        float f => OfDouble(double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
        Half h => OfDouble((double)h),
        decimal m => OfDecimal(m),
        byte b8 => OfInteger(b8),
        sbyte s8 => OfInteger(s8),
        short s16 => OfInteger(s16),
        ushort u16 => OfInteger(u16),
        int i32 => OfInteger(i32),
        uint u => OfInteger(u),
        long l => OfInteger(l),
        ulong ul => OfInteger(ul),
        Int128 i128 => OfInteger(BigInteger.Parse(i128.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
        UInt128 u128 => OfInteger(BigInteger.Parse(u128.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
        BigInteger big => OfInteger(big),
        DateOnly date => OfDate(date),
        DateTime moment => OfDateTime(moment, dateColumn),
        DateTimeOffset offset => OfOffset(offset),
        TimeOnly time => OfTime(time),
        TimeSpan span => OfTimeSpan(span),
        byte[] bytes => OfBytes(bytes),
        Guid guid => OfText(guid.ToString("D", CultureInfo.InvariantCulture)),
        char c => OfText(c.ToString()),
        IFormattable formattable => OfText(formattable.ToString(null, CultureInfo.InvariantCulture)),
        _ => OfText(value.ToString()),
    };

    /// <summary>The significant digits of a number's invariant text: leading and trailing zeros, the sign, the point and an exponent left out.</summary>
    public static int SignificantDigits(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int exponent = text.IndexOfAny(['e', 'E']);
        string mantissa = (exponent >= 0 ? text[..exponent] : text).Replace(".", "", StringComparison.Ordinal).TrimStart('-', '+').Trim('0');
        return mantissa.Length;
    }

    /// <summary>
    /// <paramref name="text"/> made safe for a cell's <c>&lt;t&gt;</c> (2026-10-29, the inverse of <see cref="OfficeXml.Unescape"/>): a control
    /// character XML can't carry (and a carriage return, as Excel writes it) as <c>_xHHHH_</c>, a literal <c>_xHHHH_</c> guarded with
    /// <c>_x005F_</c>, a lone surrogate as U+FFFD.
    /// </summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder? sb = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            string? instead = null;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb?.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                instead = "�";
            }
            else if ((c < 0x20 && c is not '\t' and not '\n') || c is '￾' or '￿')
            {
                instead = "_x" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) + "_";
            }
            else if (c == '_' && LooksEscaped(text, i))
            {
                instead = "_x005F_";
            }

            if (instead is null)
            {
                sb?.Append(c);
                continue;
            }

            sb ??= new StringBuilder(text, 0, i, text.Length + 16);
            sb.Append(instead);
        }

        return sb?.ToString() ?? text;
    }

    /// <summary>
    /// <paramref name="text"/> made safe for XML that carries no escapes (a Word run, a property): control characters but tab and line
    /// feed left out, a lone surrogate as U+FFFD.
    /// </summary>
    public static string XmlSafe(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder? sb = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb?.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            bool bad = char.IsSurrogate(c) || (c < 0x20 && c is not '\t' and not '\n') || c is '￾' or '￿';
            if (!bad)
            {
                sb?.Append(c);
                continue;
            }

            sb ??= new StringBuilder(text, 0, i, text.Length);
            if (char.IsSurrogate(c))
            {
                sb.Append('�');
            }
        }

        return sb?.ToString() ?? text;
    }

    private static bool LooksEscaped(string text, int at) =>
        at + 6 < text.Length && text[at + 1] == 'x' && text[at + 6] == '_' && char.IsAsciiHexDigit(text[at + 2]) && char.IsAsciiHexDigit(text[at + 3])
        && char.IsAsciiHexDigit(text[at + 4]) && char.IsAsciiHexDigit(text[at + 5]);
}
