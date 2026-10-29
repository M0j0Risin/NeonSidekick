using System.Globalization;
using System.Text;

namespace NeonSidekick.Office;

/// <summary>How a number cell is shown.</summary>
public enum CellFormat
{
    General,
    Date,
    Time,
    DateTime,
    Percent,

    /// <summary>A duration in total hours (<c>[h]:mm:ss</c>, built-in 46): 1.5 days reads <c>36:00:00</c> (2026-10-28, review P2-03).</summary>
    Elapsed,
}

/// <summary>
/// A number cell as Excel shows it by default (2026-10-28, the Office plan): at most 15 significant digits, invariant (a stored
/// <c>0.30000000000000004</c> reads <c>0.3</c>); a percent format as <c>25%</c>, so a rate is never read a hundred times too small; a date,
/// time or date-time format as ISO with no time zone added or converted (Excel stores none). Other formats (currency, thousands, fixed
/// decimals) aren't applied in v1. The date formats: built-in ids 14–22 and 45–47, the East Asian ids 27–36 and 50–58, and a custom
/// code with y/m/d/h/s outside quotes and brackets. The 1900 system steps over Excel's phantom 29 Feb 1900 (serial 60 itself reads
/// <c>1900-02-29</c>, as Excel shows it); the 1904 system counts from 1904-01-01. Pure.
/// </summary>
public static class NumberFormats
{
    /// <summary>The last serial Excel shows as a date (9999-12-31).</summary>
    public const double MaxSerial = 2_958_465;

    /// <summary>What a cell styled with number format <paramref name="id"/> (and its code, when the workbook defines one) shows as.</summary>
    public static CellFormat Classify(int id, string? code)
    {
        if (code is not null)
        {
            return ClassifyCode(code);
        }

        return id switch
        {
            9 or 10 => CellFormat.Percent,
            >= 14 and <= 17 => CellFormat.Date,
            46 => CellFormat.Elapsed,
            >= 18 and <= 21 or 45 or 47 => CellFormat.Time,
            22 => CellFormat.DateTime,
            >= 27 and <= 36 or >= 50 and <= 58 => CellFormat.Date,
            _ => CellFormat.General,
        };
    }

    /// <summary>
    /// A custom code's kind: its first section, with quoted text, brackets and escaped characters left out. A bracket holding only h, m or s
    /// letters (<c>[h]</c>, <c>[mm]</c>) makes it <see cref="CellFormat.Elapsed"/> (2026-10-28, review P2-03: <c>[h]:mm</c> read as a date and
    /// <c>[h]:mm:ss</c> dropped whole days); a color, locale or condition bracket is still left out.
    /// </summary>
    public static CellFormat ClassifyCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Trim().Equals("General", StringComparison.OrdinalIgnoreCase))
        {
            return CellFormat.General;
        }

        var kept = new StringBuilder();
        bool elapsed = false;
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (c == ';')
            {
                break;
            }

            if (c == '"')
            {
                int close = code.IndexOf('"', i + 1);
                i = close < 0 ? code.Length : close;
                continue;
            }

            if (c == '[')
            {
                int close = code.IndexOf(']', i + 1);
                if (close > i + 1 && code.AsSpan(i + 1, close - i - 1).IndexOfAnyExcept("hHmMsS") < 0)
                {
                    elapsed = true;
                }

                i = close < 0 ? code.Length : close;
                continue;
            }

            if (c is '\\' or '_' or '*')
            {
                i++;
                continue;
            }

            kept.Append(char.ToLowerInvariant(c));
        }

        string cleaned = kept.ToString().Replace("am/pm", "", StringComparison.Ordinal).Replace("a/p", "", StringComparison.Ordinal);
        bool hours = cleaned.Contains('h', StringComparison.Ordinal);
        bool seconds = cleaned.Contains('s', StringComparison.Ordinal);
        bool date = cleaned.Contains('y', StringComparison.Ordinal) || cleaned.Contains('d', StringComparison.Ordinal)
            || (cleaned.Contains('m', StringComparison.Ordinal) && !hours && !seconds);
        bool time = hours || seconds;
        return elapsed ? CellFormat.Elapsed
            : date && time ? CellFormat.DateTime
            : date ? CellFormat.Date
            : time ? CellFormat.Time
            : cleaned.Contains('%', StringComparison.Ordinal) ? CellFormat.Percent
            : CellFormat.General;
    }

    /// <summary><paramref name="value"/> shown as <paramref name="format"/> says; a serial outside Excel's dates shown as a number.</summary>
    public static string Format(double value, CellFormat format, bool date1904)
    {
        switch (format)
        {
            case CellFormat.Percent:
                return General(value * 100) + "%";
            case CellFormat.Elapsed when value is >= 0 and <= MaxSerial:
                long total = (long)Math.Round(value * 86_400, MidpointRounding.AwayFromZero);
                return (total / 3600).ToString(CultureInfo.InvariantCulture) + ":" + (total / 60 % 60).ToString("00", CultureInfo.InvariantCulture)
                    + ":" + (total % 60).ToString("00", CultureInfo.InvariantCulture);
            case CellFormat.Date or CellFormat.Time or CellFormat.DateTime when value is >= 0 and <= MaxSerial && !double.IsNaN(value):
                long days = (long)Math.Floor(value);
                long seconds = (long)Math.Round((value - days) * 86_400, MidpointRounding.AwayFromZero);
                if (seconds >= 86_400)
                {
                    days++;
                    seconds -= 86_400;
                }

                string time = new TimeOnly(seconds * TimeSpan.TicksPerSecond).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                if (format == CellFormat.Time)
                {
                    return time;
                }

                if (!date1904 && days == 0)
                {
                    // Excel's day 0 (2026-10-28, review P9-10): a date shows 1900-01-00, a time in a date-time format its time alone.
                    return format == CellFormat.Date && seconds == 0 ? "1900-01-00" : time;
                }

                if (Date(days, date1904) is not { } date)
                {
                    return General(value);
                }

                return format == CellFormat.DateTime || seconds != 0 ? date + "T" + time : date;
            default:
                return General(value);
        }
    }

    /// <summary>A number with at most 15 significant digits, invariant.</summary>
    public static string General(double value) => value.ToString("G15", CultureInfo.InvariantCulture);

    /// <summary>The first day the 1900 system's serials count right (2026-10-29, the Office writing plan): before it lies Lotus's phantom 29 Feb.</summary>
    public static readonly DateTime FirstTrueSerialDay = new(1900, 3, 1);

    /// <summary>
    /// The serial Excel stores for <paramref name="value"/> (2026-10-29, the Office writing plan: the writing direction beside the reading
    /// one, so both count the same days): whole days from 1899-12-30 (the 1900 system, from 1900-03-01 on, where Lotus's leap-day bug no
    /// longer shifts them) or from 1904-01-01, the time of day as the fraction. Null for a day the system can't hold, which the writer
    /// keeps as text. Pure.
    /// </summary>
    public static double? Serial(DateTime value, bool date1904)
    {
        if (date1904 ? value < new DateTime(1904, 1, 1) : value < FirstTrueSerialDay)
        {
            return null;
        }

        var origin = date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30);
        return (value.Ticks - origin.Ticks) / (double)TimeSpan.TicksPerDay;
    }

    /// <summary>A time of day or a duration as Excel's fraction of days (<c>0.5</c> is noon, <c>1.5</c> is 36 hours).</summary>
    public static double Days(TimeSpan value) => value.Ticks / (double)TimeSpan.TicksPerDay;

    private static string? Date(long days, bool date1904)
    {
        // Past 9999-12-31 (2026-10-28, review P3-06: a fraction rounding up to the next day pushed the last serial past it, and DateOnly threw).
        if (days > MaxSerial || (date1904 && days > MaxSerial - 1462))
        {
            return null;   // past 9999-12-31 counted from 1904
        }

        if (date1904)
        {
            return new DateOnly(1904, 1, 1).AddDays((int)days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // Excel's 1900 system counts 1900 as a leap year (Lotus's bug, kept): serial 60 is a 29 Feb that never was.
        return days switch
        {
            60 => "1900-02-29",
            < 60 => new DateOnly(1899, 12, 31).AddDays((int)days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => new DateOnly(1899, 12, 30).AddDays((int)days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }
}
