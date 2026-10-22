using System.Globalization;
using System.Text;

namespace NeonSidekick.Obsidian;

/// <summary>
/// The daily-notes settings in force: the folder (vault-relative, <c>/</c>-separated, empty for the root), the moment.js date format, the
/// template note (empty for none); <paramref name="Source"/> where they came from (2026-10-22, review U10: the core plugin's file, Periodic
/// Notes' when it makes the daily notes, or the defaults), and <paramref name="CoreOff"/> when the core Daily notes plugin is turned off.
/// </summary>
public sealed record DailyNoteSettings(string Folder, string Format, string Template, string Source = DailyNotes.CoreSource, bool CoreOff = false)
{
    public static DailyNoteSettings Default { get; } = new("", DailyNotes.DefaultFormat, "");
}

/// <summary>
/// Obsidian's core Daily notes plugin, as a file layout (2026-09-22): the note for a day is
/// <c>&lt;folder&gt;/&lt;date in format&gt;.md</c>, created from the template note when missing. The format
/// is moment.js's (<see cref="Format"/>), since that is what <c>daily-notes.json</c> holds; the template
/// gets the substitutions <c>obsidian-daily-notes-interface</c> makes, the library the core plugin, Calendar and Periodic Notes share
/// (<see cref="ApplyTemplate(string, string, DateOnly, DateTime, string, TimeSpan?)"/>). Invariant culture throughout: month and
/// day names are English, which is also what Obsidian writes under its default locale. Pure.
/// </summary>
public static class DailyNotes
{
    public const string DefaultFormat = "YYYY-MM-DD";
    public const string DefaultTimeFormat = "HH:mm";

    /// <summary>Where the settings came from (review U10). Pinned words.</summary>
    public const string CoreSource = "Daily notes";
    public const string PeriodicSource = "Periodic Notes";

    /// <summary>The settings from <c>daily-notes.json</c>'s text (null for no file): blanks fall back to the defaults, the folder and template trimmed of slashes.</summary>
    public static DailyNoteSettings Settings(DailyNotesConfigFile? file)
    {
        if (file is null)
        {
            return DailyNoteSettings.Default;
        }

        return new DailyNoteSettings(
            Clean(file.Folder),
            string.IsNullOrWhiteSpace(file.Format) ? DefaultFormat : file.Format.Trim(),
            Clean(file.Template));
    }

    /// <summary>A settings path as the vault spells it: <c>\</c> as <c>/</c>, no slashes at either end.</summary>
    public static string Clean(string? path) => (path ?? "").Replace('\\', '/').Trim().Trim('/');

    /// <summary>The vault-relative path of the note for <paramref name="date"/>: <c>Daily/2026-09-22.md</c>.</summary>
    public static string PathFor(DailyNoteSettings settings, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string name = Format(date.ToDateTime(TimeOnly.MinValue), settings.Format) + ".md";
        return settings.Folder.Length == 0 ? name : settings.Folder + "/" + name;
    }

    /// <summary>
    /// <see cref="ApplyTemplate(string, string, DateOnly, DateTime, string, TimeSpan?)"/> for a note dated <paramref name="now"/>'s day in
    /// <see cref="DefaultFormat"/>.
    /// </summary>
    public static string ApplyTemplate(string template, string title, DateTime now) =>
        ApplyTemplate(template, title, DateOnly.FromDateTime(now), now, DefaultFormat);

    /// <summary>
    /// A template's text for the daily note titled <paramref name="title"/> (its name in <paramref name="dailyFormat"/>) for
    /// <paramref name="date"/>, made at <paramref name="now"/> — as <c>obsidian-daily-notes-interface</c> fills one (2026-10-22, review U5,
    /// the fix plan's D3): <c>{{date}}</c> and <c>{{title}}</c> the note's name, <c>{{time}}</c> now as <c>HH:mm</c>,
    /// <c>{{date+1d:FMT}}</c> / <c>{{time-2h}}</c> the note's day at now's time moved by the amount (units <c>y q M w d h m s</c>, <c>m</c>
    /// minutes and <c>M</c> months as moment has them) in <c>FMT</c> or else the daily format, and <c>{{yesterday}}</c> / <c>{{tomorrow}}</c>
    /// in the daily format. An unknown <c>{{…}}</c> stays. <paramref name="offset"/> is the local zone's, for <c>X</c> and <c>Z</c>.
    /// </summary>
    public static string ApplyTemplate(string template, string title, DateOnly date, DateTime now, string dailyFormat, TimeSpan? offset = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(dailyFormat);
        var sb = new StringBuilder(template.Length);
        int i = 0;
        while (i < template.Length)
        {
            int open = template.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }

            int close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }

            sb.Append(template, i, open - i);
            sb.Append(Substitute(template[(open + 2)..close]) ?? template[open..(close + 2)]);
            i = close + 2;
        }

        return sb.ToString();

        string? Substitute(string inner)
        {
            string text = inner.Trim();
            string lower = text.ToLowerInvariant();
            switch (lower)
            {
                case "title" or "date":
                    return title;
                case "time":
                    return Format(now, DefaultTimeFormat, offset);
                case "yesterday":
                    return Format(date.AddDays(-1).ToDateTime(TimeOnly.MinValue), dailyFormat, offset);
                case "tomorrow":
                    return Format(date.AddDays(1).ToDateTime(TimeOnly.MinValue), dailyFormat, offset);
            }

            string name = lower.StartsWith("date", StringComparison.Ordinal) ? "date" : lower.StartsWith("time", StringComparison.Ordinal) ? "time" : "";
            if (name.Length == 0)
            {
                return null;
            }

            int at = name.Length;
            while (at < text.Length && text[at] == ' ')
            {
                at++;
            }

            var moment = date.ToDateTime(TimeOnly.FromDateTime(now));
            if (at < text.Length && text[at] is '+' or '-')
            {
                int digits = at + 1;
                while (digits < text.Length && char.IsAsciiDigit(text[digits]))
                {
                    digits++;
                }

                if (digits == at + 1 || digits >= text.Length || !int.TryParse(text[at..digits], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int amount)
                    || Add(moment, amount, text[digits]) is not { } moved)
                {
                    return null;
                }

                moment = moved;
                at = digits + 1;
                while (at < text.Length && text[at] == ' ')
                {
                    at++;
                }
            }

            if (at == text.Length)
            {
                return Format(moment, dailyFormat, offset);
            }

            return text[at] == ':' ? Format(moment, text[(at + 1)..].Trim(), offset) : null;
        }
    }

    /// <summary>moment's <c>add(amount, unit)</c> for one letter: <c>M</c> months and <c>m</c> minutes, the rest either case; null for another letter.</summary>
    private static DateTime? Add(DateTime moment, int amount, char unit)
    {
        try
        {
            return unit switch
            {
                'y' or 'Y' => moment.AddYears(amount),
                'q' or 'Q' => moment.AddMonths(amount * 3),
                'M' => moment.AddMonths(amount),
                'w' or 'W' => moment.AddDays(amount * 7.0),
                'd' or 'D' => moment.AddDays(amount),
                'h' or 'H' => moment.AddHours(amount),
                'm' => moment.AddMinutes(amount),
                's' or 'S' => moment.AddSeconds(amount),
                _ => null,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static readonly string[] Tokens =
    [
        "LLLL", "LLL", "LL", "LTS", "LT", "L", "llll", "lll", "ll", "l",
        "YYYY", "YY", "Qo", "Q", "MMMM", "MMM", "MM", "Mo", "M", "DDDD", "DDD", "DD", "Do", "D", "dddd", "ddd", "dd", "d", "E", "e",
        "GGGG", "GG", "gggg", "gg", "WW", "W", "ww", "w", "HH", "H", "hh", "h", "kk", "k", "mm", "m", "ss", "s", "SSS", "SS", "S",
        "A", "a", "ZZ", "Z", "X", "x",
    ];

    /// <summary>moment's English long-date tokens, as the patterns they stand for (2026-10-22, review U12: <c>LL</c> named every day <c>LL.md</c>).</summary>
    private static string? LongDate(string token) => token switch
    {
        "LT" => "h:mm A",
        "LTS" => "h:mm:ss A",
        "L" => "MM/DD/YYYY",
        "LL" => "MMMM D, YYYY",
        "LLL" => "MMMM D, YYYY h:mm A",
        "LLLL" => "dddd, MMMM D, YYYY h:mm A",
        "l" => "M/D/YYYY",
        "ll" => "MMM D, YYYY",
        "lll" => "MMM D, YYYY h:mm A",
        "llll" => "ddd, MMM D, YYYY h:mm A",
        _ => null,
    };

    /// <summary>
    /// <paramref name="moment"/> formatted with a moment.js pattern: <c>YYYY YY Q Qo MMMM MMM MM M Mo DDDD DDD DD D Do dddd ddd dd d E e
    /// GGGG GG WW W gggg gg ww w HH H hh h kk k mm m ss s SSS SS S A a ZZ Z X x</c> and the long dates <c>L LL LLL LLLL LT LTS l ll lll llll</c>,
    /// <c>[literal]</c> text kept as written, every other character itself. <c>GGGG WW</c> are ISO weeks; <c>gggg ww</c> are the English
    /// locale's, Sunday first with the week holding 1 January as week 1 (2026-10-22, review Q5: they were ISO too, so 2026-01-04 was W01 where
    /// Obsidian names W02). <paramref name="offset"/> is the moment's zone (2026-10-22, review I15: <c>X</c> read local time as UTC); none is UTC.
    /// Invariant.
    /// </summary>
    public static string Format(DateTime moment, string pattern, TimeSpan? offset = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(pattern.Length + 8);
        int i = 0;
        while (i < pattern.Length)
        {
            if (pattern[i] == '[')
            {
                int close = pattern.IndexOf(']', i + 1);
                if (close > i)
                {
                    sb.Append(pattern, i + 1, close - i - 1);
                    i = close + 1;
                    continue;
                }
            }

            string? token = null;
            foreach (var t in Tokens)
            {
                if (string.CompareOrdinal(pattern, i, t, 0, t.Length) == 0)
                {
                    token = t;
                    break;
                }
            }

            if (token is null)
            {
                sb.Append(pattern[i]);
                i++;
                continue;
            }

            if (LongDate(token) is { } expanded)
            {
                sb.Append(Format(moment, expanded, offset));
                i += token.Length;
                continue;
            }

            int week = ISOWeek.GetWeekOfYear(moment);
            var (localYear, localWeek) = LocaleWeek(moment);
            int hour12 = moment.Hour % 12 == 0 ? 12 : moment.Hour % 12;
            int quarter = (moment.Month - 1) / 3 + 1;
            var zone = offset ?? TimeSpan.Zero;
            long epochMs = (long)(DateTime.SpecifyKind(moment, DateTimeKind.Unspecified) - zone - DateTime.UnixEpoch).TotalMilliseconds;
            sb.Append(token switch
            {
                "YYYY" => moment.Year.ToString("0000", inv),
                "YY" => (moment.Year % 100).ToString("00", inv),
                "Q" => quarter.ToString(inv),
                "Qo" => Ordinal(quarter),
                "MMMM" => moment.ToString("MMMM", inv),
                "MMM" => moment.ToString("MMM", inv),
                "MM" => moment.Month.ToString("00", inv),
                "Mo" => Ordinal(moment.Month),
                "M" => moment.Month.ToString(inv),
                "DDDD" => moment.DayOfYear.ToString("000", inv),
                "DDD" => moment.DayOfYear.ToString(inv),
                "DD" => moment.Day.ToString("00", inv),
                "Do" => Ordinal(moment.Day),
                "D" => moment.Day.ToString(inv),
                "dddd" => moment.ToString("dddd", inv),
                "ddd" => moment.ToString("ddd", inv),
                "dd" => moment.ToString("ddd", inv)[..2],
                "d" or "e" => ((int)moment.DayOfWeek).ToString(inv),
                "E" => (moment.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)moment.DayOfWeek).ToString(inv),
                "GGGG" => ISOWeek.GetYear(moment).ToString("0000", inv),
                "GG" => (ISOWeek.GetYear(moment) % 100).ToString("00", inv),
                "gggg" => localYear.ToString("0000", inv),
                "gg" => (localYear % 100).ToString("00", inv),
                "WW" => week.ToString("00", inv),
                "W" => week.ToString(inv),
                "ww" => localWeek.ToString("00", inv),
                "w" => localWeek.ToString(inv),
                "HH" => moment.Hour.ToString("00", inv),
                "H" => moment.Hour.ToString(inv),
                "hh" => hour12.ToString("00", inv),
                "h" => hour12.ToString(inv),
                "kk" => (moment.Hour == 0 ? 24 : moment.Hour).ToString("00", inv),
                "k" => (moment.Hour == 0 ? 24 : moment.Hour).ToString(inv),
                "mm" => moment.Minute.ToString("00", inv),
                "m" => moment.Minute.ToString(inv),
                "ss" => moment.Second.ToString("00", inv),
                "s" => moment.Second.ToString(inv),
                "SSS" => moment.Millisecond.ToString("000", inv),
                "SS" => (moment.Millisecond / 10).ToString("00", inv),
                "S" => (moment.Millisecond / 100).ToString(inv),
                "A" => moment.Hour < 12 ? "AM" : "PM",
                "a" => moment.Hour < 12 ? "am" : "pm",
                "Z" => Zone(zone, colon: true),
                "ZZ" => Zone(zone, colon: false),
                "X" => Math.Floor(epochMs / 1000.0).ToString(inv),
                "x" => epochMs.ToString(inv),
                _ => token,
            });
            i += token.Length;
        }

        return sb.ToString();
    }

    /// <summary><c>+02:00</c> (<paramref name="colon"/>) or <c>+0200</c>.</summary>
    private static string Zone(TimeSpan offset, bool colon)
    {
        var a = offset.Duration();
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        return sign + a.Hours.ToString("00", CultureInfo.InvariantCulture) + (colon ? ":" : "") + a.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// moment's week of year in its English locale (Sunday the first day, the week holding 1 January week 1 — <c>dow 0, doy 6</c>), and the
    /// year it belongs to (review Q5): moment's <c>weekOfYear</c>, step for step.
    /// </summary>
    public static (int Year, int Week) LocaleWeek(DateTime moment)
    {
        int year = moment.Year;
        int week = FloorDiv(moment.DayOfYear - FirstWeekOffset(year) - 1, 7) + 1;
        if (week < 1)
        {
            year--;
            week += WeeksInYear(year);
        }
        else if (week > WeeksInYear(year))
        {
            week -= WeeksInYear(year);
            year++;
        }

        return (year, week);

        static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);
    }

    // moment's firstWeekOffset(year, dow 0, doy 6): fwd = 7 + dow - doy = 1, the first week's start relative to 1 January.
    private static int FirstWeekOffset(int year)
    {
        const int fwd = 1;
        int fwdlw = (7 + (int)new DateTime(year, 1, fwd).DayOfWeek) % 7;
        return -fwdlw + fwd - 1;
    }

    private static int WeeksInYear(int year) =>
        (DateTime.IsLeapYear(year) ? 366 : 365) - FirstWeekOffset(year) + FirstWeekOffset(year + 1) is int days ? days / 7 : 0;

    /// <summary><c>1st</c>, <c>2nd</c>, <c>3rd</c>, <c>11th</c>, <c>22nd</c>: moment's English ordinals.</summary>
    public static string Ordinal(int n)
    {
        int tens = n % 100;
        string suffix = tens is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    /// <summary>The most days a <c>+N</c> / <c>-N</c> date steps.</summary>
    public const int MaxStepDays = 36_500;

    /// <summary>
    /// A <c>date</c> argument as a day: blank or <c>today</c> is <paramref name="today"/>, <c>yesterday</c> and
    /// <c>tomorrow</c> step one, <c>+3</c> / <c>-2</c> step that many days, else an ISO <c>YYYY-MM-DD</c>. False for anything else.
    /// </summary>
    public static bool TryParseDay(string text, DateOnly today, out DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(text);
        string t = text.Trim().ToLowerInvariant();
        day = today;
        switch (t)
        {
            case "" or "today":
                return true;
            case "yesterday":
                day = today.AddDays(-1);
                return true;
            case "tomorrow":
                day = today.AddDays(1);
                return true;
        }

        // A range test, not Math.Abs (2026-10-22, review I14: Abs(int.MinValue) threw for "-2147483648").
        if ((t[0] == '+' || t[0] == '-') && int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int step))
        {
            if (step is < -MaxStepDays or > MaxStepDays)
            {
                return false;
            }

            try
            {
                day = today.AddDays(step);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        return DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }
}
