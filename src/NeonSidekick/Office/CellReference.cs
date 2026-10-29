using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NeonSidekick.Office;

/// <summary>A block of cells, 1-based and inclusive; <see cref="WholeColumns"/> and <see cref="WholeRows"/> when it was given as <c>A:C</c> or <c>2:10</c>.</summary>
public sealed record CellRange(int FirstColumn, int FirstRow, int LastColumn, int LastRow, bool WholeColumns = false, bool WholeRows = false)
{
    /// <summary>Whether the range is one cell.</summary>
    public bool IsOneCell => FirstColumn == LastColumn && FirstRow == LastRow && !WholeColumns && !WholeRows;

    /// <summary>The cells this range and <paramref name="other"/> share; null when none.</summary>
    public CellRange? Intersect(CellRange other)
    {
        ArgumentNullException.ThrowIfNull(other);
        int c1 = Math.Max(FirstColumn, other.FirstColumn), r1 = Math.Max(FirstRow, other.FirstRow);
        int c2 = Math.Min(LastColumn, other.LastColumn), r2 = Math.Min(LastRow, other.LastRow);
        return c1 <= c2 && r1 <= r2 ? new CellRange(c1, r1, c2, r2) : null;
    }

    /// <summary>As Excel writes it: <c>A1:F200</c>, <c>B7</c> for one cell.</summary>
    public override string ToString() =>
        FirstColumn == LastColumn && FirstRow == LastRow
            ? CellReference.Cell(FirstColumn, FirstRow)
            : CellReference.Cell(FirstColumn, FirstRow) + ":" + CellReference.Cell(LastColumn, LastRow);
}

/// <summary>Why a range didn't read.</summary>
public enum RangeProblem
{
    None,
    Bad,
    PastTheEdge,
    Name,
}

/// <summary>
/// A1 references (2026-10-28, the Office plan): column letters and numbers, a cell, a range with its sheet in front (<c>Sheet1!A1:F200</c>,
/// Excel's quoted <c>'Q3 Sales'!A1:F200</c> with <c>''</c> for a quote inside), <c>$</c> signs ignored, whole columns <c>A:C</c> and rows
/// <c>2:10</c>; a sheet's name quoted back when it must be. Past <c>XFD1048576</c> is refused; something shaped like a name is told apart
/// from a typo, since defined names aren't resolved. Pure.
/// </summary>
public static partial class CellReference
{
    /// <summary>Excel's last column, XFD.</summary>
    public const int MaxColumn = 16_384;

    /// <summary>Excel's last row.</summary>
    public const int MaxRow = 1_048_576;

    /// <summary>The letters of 1-based column <paramref name="column"/>: 1 is A, 27 is AA.</summary>
    public static string Column(int column)
    {
        var sb = new StringBuilder();
        for (int n = column; n > 0; n = (n - 1) / 26)
        {
            sb.Insert(0, (char)('A' + ((n - 1) % 26)));
        }

        return sb.ToString();
    }

    /// <summary>The column number of <paramref name="letters"/> (any case); past <see cref="MaxColumn"/> is kept so the caller can refuse it; -1 for no letters.</summary>
    public static long ColumnNumber(ReadOnlySpan<char> letters)
    {
        if (letters.Length == 0)
        {
            return -1;
        }

        long n = 0;
        foreach (char c in letters)
        {
            char upper = char.ToUpperInvariant(c);
            if (upper is < 'A' or > 'Z')
            {
                return -1;
            }

            n = (n * 26) + (upper - 'A' + 1);
        }

        return n;
    }

    /// <summary><c>B7</c>.</summary>
    public static string Cell(int column, int row) => Column(column) + row.ToString(CultureInfo.InvariantCulture);

    /// <summary>A cell's reference as a cell's <c>r</c> attribute gives it (<c>B7</c>); false for anything else.</summary>
    public static bool TryParseCell(string? text, out int column, out int row)
    {
        column = 0;
        row = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var match = CellPattern().Match(text.Replace("$", "", StringComparison.Ordinal));
        if (!match.Success)
        {
            return false;
        }

        long c = ColumnNumber(match.Groups[1].ValueSpan);
        if (c is < 1 or > MaxColumn || !int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out row) || row is < 1 or > MaxRow)
        {
            return false;
        }

        column = (int)c;
        return true;
    }

    /// <summary>A sheet's used range as <c>&lt;dimension ref&gt;</c> gives it (<c>A1:F200</c> or <c>A1</c>); null for anything else.</summary>
    public static CellRange? ParseDimension(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string[] parts = text.Split(':');
        if (parts.Length is < 1 or > 2 || !TryParseCell(parts[0], out int c1, out int r1))
        {
            return null;
        }

        if (parts.Length == 1)
        {
            return new CellRange(c1, r1, c1, r1);
        }

        return TryParseCell(parts[1], out int c2, out int r2) ? new CellRange(Math.Min(c1, c2), Math.Min(r1, r2), Math.Max(c1, c2), Math.Max(r1, r2)) : null;
    }

    /// <summary>
    /// <paramref name="text"/> as <c>range</c> gives it: the sheet named in front (null when none) and the block; <see cref="RangeProblem"/>
    /// says why not. Whole columns run over every row and whole rows over every column, for the caller to bound by the used range.
    /// </summary>
    public static RangeProblem TryParseRange(string text, out string? sheet, out CellRange? range) => TryParseRange(text, out sheet, out range, out _);

    /// <summary>
    /// As <see cref="TryParseRange(string, out string?, out CellRange?)"/>, with <paramref name="body"/> the block as written after the sheet,
    /// <c>$</c> signs dropped (2026-10-28, review P5-16: the reader split it again by hand, so a quoted sheet name holding <c>!</c> split
    /// differently).
    /// </summary>
    public static RangeProblem TryParseRange(string text, out string? sheet, out CellRange? range, out string body)
    {
        ArgumentNullException.ThrowIfNull(text);
        sheet = null;
        range = null;
        body = text.Trim();
        if (body.StartsWith('\''))
        {
            var name = new StringBuilder();
            int i = 1;
            bool closed = false;
            while (i < body.Length)
            {
                if (body[i] == '\'')
                {
                    if (i + 1 < body.Length && body[i + 1] == '\'')
                    {
                        name.Append('\'');
                        i += 2;
                        continue;
                    }

                    closed = true;
                    break;
                }

                name.Append(body[i++]);
            }

            if (!closed || i + 1 >= body.Length || body[i + 1] != '!' || name.Length == 0)
            {
                return RangeProblem.Bad;
            }

            sheet = name.ToString();
            body = body[(i + 2)..];
        }
        else if (body.LastIndexOf('!') is >= 0 and int bang)
        {
            sheet = body[..bang].Trim();
            body = body[(bang + 1)..];
            if (sheet.Length == 0)
            {
                return RangeProblem.Bad;
            }
        }

        body = body.Replace("$", "", StringComparison.Ordinal).Trim();
        if (CellsPattern().Match(body) is { Success: true } cells)
        {
            long c1 = ColumnNumber(cells.Groups[1].ValueSpan);
            long r1 = long.Parse(cells.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
            long c2 = cells.Groups[3].Success ? ColumnNumber(cells.Groups[3].ValueSpan) : c1;
            long r2 = cells.Groups[4].Success ? long.Parse(cells.Groups[4].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : r1;
            return Block(c1, r1, c2, r2, false, false, out range);
        }

        if (ColumnsPattern().Match(body) is { Success: true } columns)
        {
            return Block(ColumnNumber(columns.Groups[1].ValueSpan), 1, ColumnNumber(columns.Groups[2].ValueSpan), MaxRow, true, false, out range);
        }

        if (RowsPattern().Match(body) is { Success: true } rows)
        {
            return Block(1, long.Parse(rows.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture), MaxColumn,
                long.Parse(rows.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture), false, true, out range);
        }

        return NamePattern().IsMatch(body) ? RangeProblem.Name : RangeProblem.Bad;
    }

    private static RangeProblem Block(long c1, long r1, long c2, long r2, bool wholeColumns, bool wholeRows, out CellRange? range)
    {
        range = null;
        if (c1 < 1 || c2 < 1 || r1 < 1 || r2 < 1)
        {
            return RangeProblem.Bad;
        }

        if (c1 > MaxColumn || c2 > MaxColumn || r1 > MaxRow || r2 > MaxRow)
        {
            return RangeProblem.PastTheEdge;
        }

        range = new CellRange((int)Math.Min(c1, c2), (int)Math.Min(r1, r2), (int)Math.Max(c1, c2), (int)Math.Max(r1, r2), wholeColumns, wholeRows);
        return RangeProblem.None;
    }

    /// <summary>A sheet's name as a reference writes it: bare when it can be, else quoted with <c>''</c> for a quote inside.</summary>
    public static string QuoteSheet(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return BareName().IsMatch(name) && !CellPattern().IsMatch(name) && !RowColumnStyle().IsMatch(name)
            ? name
            : "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary><c>Sheet1!A1:F200</c>, the sheet quoted when it must be.</summary>
    public static string WithSheet(string sheet, CellRange range) => QuoteSheet(sheet) + "!" + range;

    [GeneratedRegex("^([A-Za-z]{1,3})([0-9]{1,7})$", RegexOptions.CultureInvariant)]
    private static partial Regex CellPattern();

    [GeneratedRegex("^([A-Za-z]{1,4})([0-9]{1,8})(?::([A-Za-z]{1,4})([0-9]{1,8}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex CellsPattern();

    [GeneratedRegex("^([A-Za-z]{1,4}):([A-Za-z]{1,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnsPattern();

    [GeneratedRegex("^([0-9]{1,8}):([0-9]{1,8})$", RegexOptions.CultureInvariant)]
    private static partial Regex RowsPattern();

    [GeneratedRegex(@"^[\p{L}_\\][\p{L}\p{N}_.\\]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{N}_.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex BareName();

    [GeneratedRegex("^[Rr][0-9]*[Cc][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RowColumnStyle();
}
