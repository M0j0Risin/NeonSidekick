using System.Globalization;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The words the clock tools speak and the forms they accept: pure statics, every string pinned.
/// Invariant culture throughout, so the weekday and month names are English on every machine.
/// </summary>
public static class ClockText
{
    /// <summary>The one relative date the tools accept.</summary>
    public const string Today = "today";

    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>The clock's now in its local zone.</summary>
    public static DateTimeOffset LocalNow(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone);
    }

    /// <summary>The clock's local date: what <c>today</c> means.</summary>
    public static DateOnly LocalDate(TimeProvider time) => DateOnly.FromDateTime(LocalNow(time).DateTime);

    /// <summary>The offset as the tools print it: <c>UTC+09:00</c>, <c>UTC-07:00</c>.</summary>
    public static string FormatOffset(TimeSpan offset) =>
        "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary><c>Friday 11 September 2026, 14:05</c>.</summary>
    public static string FormatClock(DateTimeOffset moment) =>
        moment.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>Friday 11 September 2026, 14:05 (Pacific Daylight Time, UTC-07:00)</c>: the moment as
    /// already converted into <paramref name="zone"/>, with the zone's name for that instant.
    /// </summary>
    public static string FormatMoment(DateTimeOffset moment, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        string name = zone.IsDaylightSavingTime(moment) ? zone.DaylightName : zone.StandardName;
        return FormatClock(moment) + " (" + name + ", " + FormatOffset(moment.Offset) + ")";
    }

    /// <summary>
    /// <c>Saturday 12 September 2026, 06:05 in Asia/Tokyo (UTC+09:00); local time is Friday 11 September 2026, 14:05</c>:
    /// both moments on one line, so the model cannot mix them up.
    /// </summary>
    public static string FormatElsewhere(DateTimeOffset there, string zoneAsGiven, DateTimeOffset local) =>
        FormatClock(there) + " in " + zoneAsGiven + " (" + FormatOffset(there.Offset) + "); local time is " + FormatClock(local);

    /// <summary><c>2026-12-25</c>.</summary>
    public static string Iso(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary><c>Friday</c>.</summary>
    public static string Weekday(DateOnly date) => date.ToString("dddd", CultureInfo.InvariantCulture);

    /// <summary><c>today</c> (any case, trimmed) or exactly <c>yyyy-MM-dd</c>. Nothing else.</summary>
    public static bool TryParseDate(string text, DateOnly today, out DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim();
        if (string.Equals(trimmed, Today, StringComparison.OrdinalIgnoreCase))
        {
            date = today;
            return true;
        }

        return DateOnly.TryParseExact(trimmed, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>
    /// A zone by IANA name (<see cref="WindowsZones"/>, because under invariant globalization the
    /// runtime knows only Windows ids), by Windows id or name, or by a city the Windows display
    /// name lists (<c>Tokyo</c> in <c>(UTC+09:00) Osaka, Sapporo, Tokyo</c>); the last segment of
    /// an unknown IANA-shaped name is tried as a city too. False for blank and unknown.
    /// </summary>
    public static bool TryFindZone(string text, out TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(text);
        string id = text.Trim();
        if (id.Length == 0)
        {
            zone = null!;
            return false;
        }

        if (WindowsZones.TryGetWindowsId(id, out var windowsId) && TryFindSystemZone(windowsId, out zone))
        {
            return true;
        }

        if (TryFindSystemZone(id, out zone))
        {
            return true;
        }

        var zones = TimeZoneInfo.GetSystemTimeZones();
        foreach (var candidate in zones)
        {
            if (string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.StandardName, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.DaylightName, id, StringComparison.OrdinalIgnoreCase))
            {
                zone = candidate;
                return true;
            }
        }

        // "Tokyo", or the "Tokyo" in an "Asia/Tokyo" the table does not know.
        int slash = id.LastIndexOf('/');
        string city = (slash >= 0 ? id[(slash + 1)..] : id).Replace('_', ' ');
        foreach (var candidate in zones)
        {
            if (ListsCity(candidate.DisplayName, city) || NamesCity(candidate.Id, city))
            {
                zone = candidate;
                return true;
            }
        }

        zone = null!;
        return false;
    }

    /// <summary>
    /// Whether an IANA zone id, <c>Asia/Tokyo</c> or <c>America/Port_of_Spain</c>, ends in <paramref name="city"/> (2026-10-06, the
    /// macOS build): off Windows the system's zones are IANA's and their display names list no cities, so "Tokyo" found nothing.
    /// Always false on Windows, whose zones are found by <see cref="ListsCity"/> as before.
    /// </summary>
    public static bool NamesCity(string zoneId, string city)
    {
        ArgumentNullException.ThrowIfNull(zoneId);
        ArgumentNullException.ThrowIfNull(city);
        if (OperatingSystem.IsWindows() || city.Length == 0)
        {
            return false;
        }

        int slash = zoneId.LastIndexOf('/');
        return slash >= 0 && string.Equals(zoneId[(slash + 1)..].Replace('_', ' '), city.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a Windows display name, <c>(UTC+09:00) Osaka, Sapporo, Tokyo</c>, lists <paramref name="city"/> as one of its items.</summary>
    public static bool ListsCity(string displayName, string city)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(city);
        if (city.Length == 0)
        {
            return false;
        }

        int close = displayName.IndexOf(')');
        string list = close >= 0 ? displayName[(close + 1)..] : displayName;
        foreach (var item in list.Split(','))
        {
            if (string.Equals(item.Trim(), city, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>1 day</c>, <c>15 weeks</c>, <c>-3 days</c>.</summary>
    public static string Count(int n, string unit) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + unit + (Math.Abs(n) == 1 ? "" : "s");

    /// <summary><c>6 weeks</c>, <c>2 months and 3 days</c>, <c>1 year, 2 months and 3 days</c>; empty when every amount is zero.</summary>
    public static string Amounts(int years, int months, int weeks, int days)
    {
        var parts = new List<string>(4);
        if (years != 0)
        {
            parts.Add(Count(years, "year"));
        }

        if (months != 0)
        {
            parts.Add(Count(months, "month"));
        }

        if (weeks != 0)
        {
            parts.Add(Count(weeks, "week"));
        }

        if (days != 0)
        {
            parts.Add(Count(days, "day"));
        }

        return parts.Count switch
        {
            0 => "",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
    }

    /// <summary><c>Error: unknown time zone 'x'; give an IANA name such as Europe/Paris</c>.</summary>
    public static string UnknownZone(string text) => $"Error: unknown time zone '{text.Trim()}'; give an IANA name such as Europe/Paris";

    /// <summary><c>Error: 'x' is not a date for 'from'; use today or yyyy-MM-dd</c>.</summary>
    public static string BadDate(string argument, string text) => $"Error: '{text.Trim()}' is not a date for '{argument}'; use {Today} or {DateFormat}";

    /// <summary><c>Error: 'x' is not a whole number for 'days'</c>.</summary>
    public static string BadInteger(string argument, string text) => $"Error: '{text.Trim()}' is not a whole number for '{argument}'";

    public const string OutOfRange = "Error: that date is out of range";

    /// <summary>The runtime's lookup, which throws for an unknown id and for an id it cannot read.</summary>
    private static bool TryFindSystemZone(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        zone = null!;
        return false;
    }
}
