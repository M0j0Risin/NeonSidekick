using System.Text.RegularExpressions;

namespace NeonSidekick.Files;

/// <summary>
/// One CSV field as the app writes it (2026-11-14, the database-tools plan's #13: lifted from <c>DirectoryCsv.Cell</c> so the AD tools'
/// lists and the query tools' <c>to</c> guard one way): RFC 4180 quoting, and a field starting <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab
/// or a carriage return given a leading <c>'</c>, so a spreadsheet opening the file reads it as text rather than a formula. A query's
/// export knows a number from text, so its text that is a plain number (<c>-5</c>, a big integer that came as text) may be left as it is
/// (<paramref name="exemptNumbers"/>, the user's pick, "guard text, not numbers"); the AD tools' text is a stranger's (a phone number
/// <c>-1…</c> was guarded on purpose), so they keep it off and their output is byte for byte what it was. Pure.
/// </summary>
public static partial class CsvCell
{
    /// <summary>
    /// <paramref name="value"/> guarded, then quoted when it holds a comma, a quote or a line break. <paramref name="exemptNumbers"/> leaves a
    /// plain number unguarded (<see cref="IsPlainNumber"/>).
    /// </summary>
    public static string Text(string value, bool exemptNumbers)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Quote(Guard(value, exemptNumbers));
    }

    /// <summary><paramref name="value"/> with a leading <c>'</c> when a spreadsheet would read it as a formula; a plain number kept when <paramref name="exemptNumbers"/>.</summary>
    public static string Guard(string value, bool exemptNumbers)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value[0] is not ('=' or '+' or '-' or '@' or '\t' or '\r'))
        {
            return value;
        }

        return exemptNumbers && IsPlainNumber(value) ? value : "'" + value;
    }

    /// <summary><paramref name="value"/> in double quotes (a quote doubled) when it holds a comma, a quote or a line break; else as it is.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a plain decimal number: an optional minus, ASCII digits, an optional fraction and exponent. ASCII
    /// only (<c>[0-9]</c>, not <c>\d</c>, which matches every Unicode digit), so a full-width or Arabic-Indic digit string stays guarded.
    /// </summary>
    public static bool IsPlainNumber(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return PlainNumber().IsMatch(value);
    }

    [GeneratedRegex(@"^-?[0-9]+(\.[0-9]+)?([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainNumber();
}
